using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;

namespace SqlBranch.Core.Engine;

/// <summary>Where a later command found the pool (design D2a, "Finding the pool").</summary>
public enum PoolSource
{
    /// <summary>The engine container's labels.</summary>
    EngineLabels,

    /// <summary>The managed pool file on the container host.</summary>
    ManagedFile,

    /// <summary>The pool volume (<c>sqlbranch-pool</c> by default) when it carries the managed label.</summary>
    Volume,

    /// <summary>The configured <c>pool</c> directory, which holds the marker.</summary>
    Directory,
}

/// <summary>A pool that exists.</summary>
/// <param name="Location">What a container mounts at /pool.</param>
/// <param name="Backend"><c>plain</c> or <c>loop-xfs</c>.</param>
/// <param name="Source">Which lookup found it.</param>
/// <param name="Engine">The engine container when one exists, else null.</param>
public sealed record FoundPool(PoolLocation Location, string Backend, PoolSource Source, ContainerInfo? Engine);

/// <summary>
/// Finds out whether SqlBranch is initialized and where its pool is, in the
/// order of design D2a: the engine's labels, the managed pool file, the
/// <c>sqlbranch-pool</c> volume, then the configured directory with a marker.
/// Finding any of them means initialized, so a half-done teardown or an
/// unmounted pool after a restart is still recognised.
/// </summary>
public sealed class PoolFinder
{
    private readonly IContainerRuntime _runtime;
    private readonly ManagedPaths _paths;
    private readonly Func<string, bool> _directoryExists;
    private readonly string _engineName;
    private readonly string _poolVolume;

    /// <param name="runtime">The container runtime.</param>
    /// <param name="paths">The managed pool's paths on the container host.</param>
    /// <param name="directoryExists">
    /// Whether a host directory exists. The runtime would create a missing bind
    /// source, so a missing directory must be ruled out before a container mounts it.
    /// </param>
    /// <param name="engineName">The engine container's name.</param>
    /// <param name="poolVolume">The copy-mode pool volume's name.</param>
    public PoolFinder(
        IContainerRuntime runtime,
        ManagedPaths paths,
        Func<string, bool>? directoryExists = null,
        string engineName = ContainerLabels.EngineName,
        string poolVolume = ContainerLabels.PoolVolume)
    {
        _poolVolume = poolVolume;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _directoryExists = directoryExists ?? Directory.Exists;
        _engineName = engineName;
    }

    /// <summary>Whether a host directory exists, by the same rule the lookup uses.</summary>
    public bool DirectoryExists(string path) => _directoryExists(path);

    /// <summary>The pool the engine's labels name.</summary>
    public static PoolLocation LocationOf(ContainerInfo engine, ManagedPaths paths, string poolVolume = ContainerLabels.PoolVolume)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(paths);

        var backend = engine.Labels.GetValueOrDefault(ContainerLabels.Backend);
        var pool = engine.Labels.GetValueOrDefault(ContainerLabels.Pool);
        if (backend is null || pool is null)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The container '{engine.Name}' has no {ContainerLabels.Backend} or {ContainerLabels.Pool} label, so SqlBranch cannot tell where its pool is.");
        }

        if (backend == PoolProvisioner.BackendLoopXfs)
        {
            return paths.Location;
        }

        return pool == poolVolume
            ? new PoolLocation(PoolKind.Volume, poolVolume)
            : new PoolLocation(PoolKind.Directory, pool);
    }

    /// <summary>The pool, or null when nothing SqlBranch created is found.</summary>
    public async Task<FoundPool?> FindAsync(Settings settings, string defaultPoolDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _runtime.GetInfoAsync(cancellationToken).ConfigureAwait(false);

        var engine = await _runtime.InspectAsync(_engineName, cancellationToken).ConfigureAwait(false);
        if (engine is { IsManaged: false })
        {
            throw new SqlBranchException(ErrorCodes.ForeignContainer,
                $"A container named '{engine.Name}' exists that SqlBranch did not create, so SqlBranch will not use or change it. " +
                "Rename or remove that container yourself.");
        }

        if (engine is not null)
        {
            var location = LocationOf(engine, _paths, _poolVolume);
            return new FoundPool(location, engine.Labels[ContainerLabels.Backend], PoolSource.EngineLabels, engine);
        }

        if (await ManagedFileExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return new FoundPool(_paths.Location, PoolProvisioner.BackendLoopXfs, PoolSource.ManagedFile, null);
        }

        // A volume of that name that SqlBranch did not label is the user's own: it is not a pool.
        if (await _runtime.VolumeIsManagedAsync(_poolVolume, cancellationToken).ConfigureAwait(false))
        {
            return new FoundPool(new PoolLocation(PoolKind.Volume, _poolVolume), PoolProvisioner.BackendPlain, PoolSource.Volume, null);
        }

        var directory = settings.Pool ?? defaultPoolDirectory;
        if (_directoryExists(directory))
        {
            var location = new PoolLocation(PoolKind.Directory, directory);
            if (await HasMarkerAsync(settings.EngineImage, location, cancellationToken).ConfigureAwait(false))
            {
                return new FoundPool(location, PoolProvisioner.BackendPlain, PoolSource.Directory, null);
            }
        }

        return null;
    }

    /// <summary>The pool, or exit 3 <c>not_initialized</c> naming <c>sqlbranch init</c>. Every command but <c>init</c> and <c>doctor</c> starts here.</summary>
    public async Task<FoundPool> RequireAsync(Settings settings, string defaultPoolDirectory, CancellationToken cancellationToken) =>
        await FindAsync(settings, defaultPoolDirectory, cancellationToken).ConfigureAwait(false)
        ?? throw new SqlBranchException(ErrorCodes.NotInitialized,
            "SqlBranch is not initialized: there is no engine container, pool file, pool volume or pool directory. Run 'sqlbranch init' first.");

    private async Task<bool> ManagedFileExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await new ManagedPool(_runtime, _paths).FileExistsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ManagedPoolException)
        {
            // The helper could not run, so there is no evidence of a pool file. The other lookups still decide.
            return false;
        }
    }

    private async Task<bool> HasMarkerAsync(string image, PoolLocation location, CancellationToken cancellationToken)
    {
        try
        {
            return await DirectoryPool.InspectAsync(_runtime, image, location, cancellationToken).ConfigureAwait(false) == DirectoryState.Marked;
        }
        catch (SqlBranchException ex) when (ex.Code == ErrorCodes.OperationFailed)
        {
            return false;
        }
    }
}
