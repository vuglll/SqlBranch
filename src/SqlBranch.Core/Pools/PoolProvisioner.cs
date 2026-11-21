using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Pools;

/// <summary>The pool <c>init</c> ended up with.</summary>
/// <param name="Location">What the engine mounts at /pool.</param>
/// <param name="Backend"><c>plain</c> or <c>loop-xfs</c>.</param>
/// <param name="Mode">The clone mode the probe found.</param>
/// <param name="FallbackReason">Why copy mode was chosen under <c>auto</c>, or null.</param>
/// <param name="CreatedManagedPool">This run created the pool file and its mount.</param>
/// <param name="CreatedVolume">This run created the <c>sqlbranch-pool</c> volume.</param>
/// <param name="Lines">What to print: the pool file, size and mount point, and any fallback notice.</param>
/// <param name="CreatedMarker">This run wrote the pool marker into a directory or volume that existed before.</param>
public sealed record ProvisionedPool(
    PoolLocation Location,
    string Backend,
    CloneMode Mode,
    string? FallbackReason,
    bool CreatedManagedPool,
    bool CreatedVolume,
    IReadOnlyList<string> Lines,
    bool CreatedMarker = false);

/// <summary>
/// Chooses and prepares the pool by <c>poolBackend</c>: <c>plain</c> uses the
/// directory as it is, <c>loop-xfs</c> must create the managed pool, and
/// <c>auto</c> uses the directory when it can share blocks, then the managed
/// pool, then copy mode.
/// </summary>
public sealed class PoolProvisioner
{
    public const string BackendPlain = "plain";

    public const string BackendLoopXfs = "loop-xfs";

    private readonly IContainerRuntime _runtime;
    private readonly ManagedPool _managed;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _poolVolume;

    public PoolProvisioner(IContainerRuntime runtime, ManagedPaths paths, Func<DateTimeOffset>? clock = null, string poolVolume = ContainerLabels.PoolVolume)
    {
        _poolVolume = poolVolume;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _managed = new ManagedPool(runtime, paths ?? throw new ArgumentNullException(nameof(paths)));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <param name="settings">The resolved settings.</param>
    /// <param name="defaultPoolDirectory">The pool directory when <c>pool</c> is not set.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<ProvisionedPool> ProvisionAsync(Settings settings, string defaultPoolDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var image = settings.EngineImage;
        var directory = new PoolLocation(PoolKind.Directory, settings.Pool ?? defaultPoolDirectory);
        var info = await _runtime.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<string>();

        if (settings.PoolBackend == PoolBackend.Plain)
        {
            return await UseDirectoryAsync(settings, directory, reason: null, lines, cancellationToken).ConfigureAwait(false);
        }

        // A pool file from an earlier init is reused, never recreated, and its size is fixed.
        var exists = false;
        string? helperFailure = null;
        try
        {
            exists = await _managed.FileExistsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ManagedPoolException ex)
        {
            // The unprivileged helper could not run (its image cannot be pulled offline, say).
            if (settings.PoolBackend == PoolBackend.LoopXfs)
            {
                throw new SqlBranchException(ErrorCodes.PoolUnavailable,
                    $"poolBackend is loop-xfs, so there is no fallback to copy mode. The managed pool could not be checked: {ex.Message}");
            }

            helperFailure = ex.Message;
        }

        if (exists)
        {
            return await ReuseManagedAsync(settings, lines, cancellationToken).ConfigureAwait(false);
        }

        if (settings.PoolBackend == PoolBackend.Auto && !info.IsDockerDesktop)
        {
            // Docker Desktop's host folders are not on the VM's disk, so only Linux hosts can use the directory.
            await DirectoryPool.EnsureUsableAsync(_runtime, image, directory, cancellationToken).ConfigureAwait(false);
            if (await CloneProbe.ProbeAsync(_runtime, image, directory, cancellationToken).ConfigureAwait(false) == CloneMode.Thin)
            {
                return await UseDirectoryAsync(settings, directory, reason: null, lines, cancellationToken).ConfigureAwait(false);
            }
        }

        if (helperFailure is not null)
        {
            return await FallBackAsync(settings, directory, info, $"the managed pool could not be checked ({helperFailure})", lines, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await CreateManagedAsync(settings, lines, cancellationToken).ConfigureAwait(false);
        }
        catch (ManagedPoolException ex)
        {
            if (settings.PoolBackend == PoolBackend.LoopXfs)
            {
                throw new SqlBranchException(ErrorCodes.PoolUnavailable,
                    $"poolBackend is loop-xfs, so there is no fallback to copy mode. The managed pool could not be created: {ex.Message}");
            }

            var reason = ex.Message.Contains("privileged", StringComparison.OrdinalIgnoreCase)
                ? $"the managed pool needs a privileged helper container, and the container runtime refused it ({ex.Message})"
                : $"the managed pool could not be created or mounted ({ex.Message})";
            return await FallBackAsync(settings, directory, info, reason, lines, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProvisionedPool> UseDirectoryAsync(
        Settings settings,
        PoolLocation directory,
        string? reason,
        List<string> lines,
        CancellationToken cancellationToken)
    {
        var marker = await DirectoryPool.InitializeAsync(_runtime, settings.EngineImage, directory, _clock(), cancellationToken).ConfigureAwait(false);
        var mode = await CloneProbe.ProbeAsync(_runtime, settings.EngineImage, directory, cancellationToken).ConfigureAwait(false);
        return new ProvisionedPool(directory, BackendPlain, mode, reason, false, false, lines, marker);
    }

    private async Task<ProvisionedPool> ReuseManagedAsync(Settings settings, List<string> lines, CancellationToken cancellationToken)
    {
        var existing = await _managed.FileSizeAsync(cancellationToken).ConfigureAwait(false);
        if (existing != settings.PoolSize.Bytes)
        {
            throw new ConfigException(
                $"poolSize is fixed when the pool is created. The pool file {_managed.Paths.PoolFile} is {new ByteSize(existing).ToGigabytes()}, " +
                $"but poolSize is {settings.PoolSize.ToGigabytes()}. Set poolSize back, or run 'sqlbranch teardown --force' and init again.",
                settings.Origins.GetValueOrDefault(SettingKeys.PoolSize), SettingKeys.PoolSize);
        }

        if (!await _managed.IsMountedAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _managed.MountAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ManagedPoolException ex)
            {
                throw new SqlBranchException(ErrorCodes.PoolNotMounted, $"The managed pool is not mounted and could not be remounted: {ex.Message}");
            }
        }

        var location = _managed.Paths.Location;
        await DirectoryPool.InitializeAsync(_runtime, settings.EngineImage, location, _clock(), cancellationToken).ConfigureAwait(false);
        var mode = await CloneProbe.ProbeAsync(_runtime, settings.EngineImage, location, cancellationToken).ConfigureAwait(false);
        return new ProvisionedPool(location, BackendLoopXfs, mode, null, false, false, lines);
    }

    /// <summary>Creates the managed pool. On any failure it removes what it created and throws <see cref="ManagedPoolException"/>.</summary>
    private async Task<ProvisionedPool> CreateManagedAsync(Settings settings, List<string> lines, CancellationToken cancellationToken)
    {
        await _managed.EnsureHostSpaceAsync(settings.PoolSize, cancellationToken).ConfigureAwait(false);
        await _managed.CreateAsync(settings.PoolSize, lines.Add, cancellationToken).ConfigureAwait(false);

        var location = _managed.Paths.Location;
        try
        {
            await DirectoryPool.InitializeAsync(_runtime, settings.EngineImage, location, _clock(), cancellationToken).ConfigureAwait(false);
            var mode = await CloneProbe.ProbeAsync(_runtime, settings.EngineImage, location, cancellationToken).ConfigureAwait(false);
            return new ProvisionedPool(location, BackendLoopXfs, mode, null, true, false, lines);
        }
        catch (Exception ex) when (ex is SqlBranchException or ConfigException)
        {
            await _managed.RemoveAsync(CancellationToken.None).ConfigureAwait(false);
            throw new ManagedPoolException(ManagedPoolStep.Mount, ex.Message);
        }
    }

    private async Task<ProvisionedPool> FallBackAsync(
        Settings settings,
        PoolLocation directory,
        RuntimeInfo info,
        string reason,
        List<string> lines,
        CancellationToken cancellationToken)
    {
        lines.Add($"Falling back to copy mode: {reason}.");
        if (!info.IsDockerDesktop)
        {
            return await UseDirectoryAsync(settings, directory, reason, lines, cancellationToken).ConfigureAwait(false);
        }

        var createdVolume = false;
        if (!await _runtime.VolumeExistsAsync(_poolVolume, cancellationToken).ConfigureAwait(false))
        {
            await _runtime.CreateVolumeAsync(_poolVolume, cancellationToken).ConfigureAwait(false);
            createdVolume = true;
        }
        else if (!await _runtime.VolumeIsManagedAsync(_poolVolume, cancellationToken).ConfigureAwait(false))
        {
            // The runtime cannot add a label to a volume that exists, and SqlBranch never writes into a volume it did not create.
            throw new SqlBranchException(ErrorCodes.ForeignContainer,
                $"A Docker volume named '{_poolVolume}' exists that SqlBranch did not create, so SqlBranch will not use or change it. " +
                "Rename or remove that volume yourself, then run 'sqlbranch init' again.");
        }

        var volume = new PoolLocation(PoolKind.Volume, _poolVolume);
        try
        {
            var marker = await DirectoryPool.InitializeAsync(_runtime, settings.EngineImage, volume, _clock(), cancellationToken).ConfigureAwait(false);
            var mode = await CloneProbe.ProbeAsync(_runtime, settings.EngineImage, volume, cancellationToken).ConfigureAwait(false);
            return new ProvisionedPool(volume, BackendPlain, mode, reason, false, createdVolume, lines, marker);
        }
        catch when (createdVolume)
        {
            await _runtime.RemoveVolumeAsync(_poolVolume, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
