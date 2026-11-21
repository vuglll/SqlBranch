using System.Globalization;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;

namespace SqlBranch.Core.Engine;

/// <summary>The result of one check.</summary>
public enum CheckStatus
{
    Pass,
    Warn,
    Fail,
    Optional,
}

/// <summary>One line of the <c>doctor</c> report.</summary>
/// <param name="Id">A stable word: <c>docker</c>, <c>image</c>, <c>pool</c>, <c>port</c>, <c>space</c>, <c>host</c>, <c>cloneMode</c> or <c>sqlpackage</c>.</param>
/// <param name="Title">What was checked.</param>
/// <param name="Status">Pass, warn, fail or optional.</param>
/// <param name="Detail">What was found, with numbers.</param>
public sealed record DoctorCheck(string Id, string Title, CheckStatus Status, string Detail);

/// <summary>All checks of one run.</summary>
public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks)
{
    public bool AnyFailed => Checks.Any(c => c.Status == CheckStatus.Fail);
}

/// <summary>
/// <c>sqlbranch doctor</c>: Docker, the engine image, the pool, the engine
/// port, free pool space, the host architecture and the clone mode. It works
/// before <c>init</c> and changes nothing that lasts: the only files it writes
/// are one that it deletes at once and the clone-mode probe files.
/// </summary>
public sealed class DoctorRunner
{
    public const long WarnBelow = 10L << 30;
    public const long FailBelow = 1L << 30;

    private const string WritableAndSpaceScript =
        "t=/pool/.sqlbranch-doctor; if touch \"$t\" && rm -f \"$t\"; then echo SQLBRANCH_WRITABLE=yes; else echo SQLBRANCH_WRITABLE=no; fi; " +
        "echo \"SQLBRANCH_SPACE|$(df -B1 --output=size,used,avail /pool | tail -n 1)\"";

    private readonly IContainerRuntime _runtime;
    private readonly ManagedPaths _paths;
    private readonly PoolFinder _finder;
    private readonly Func<int, bool> _portIsFree;
    private readonly string _engineName;

    public DoctorRunner(
        IContainerRuntime runtime,
        ManagedPaths paths,
        Func<string, bool> directoryExists,
        Func<int, bool> portIsFree,
        string engineName = ContainerLabels.EngineName,
        string poolVolume = ContainerLabels.PoolVolume)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _portIsFree = portIsFree ?? throw new ArgumentNullException(nameof(portIsFree));
        _engineName = engineName;
        _finder = new PoolFinder(runtime, paths, directoryExists, engineName, poolVolume);
    }

    public async Task<DoctorReport> RunAsync(Settings settings, string defaultPoolDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var checks = new List<DoctorCheck>();

        RuntimeInfo info;
        try
        {
            info = await _runtime.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlBranchException ex) when (ex.Code == ErrorCodes.DockerUnavailable)
        {
            // Every other check needs Docker.
            checks.Add(new DoctorCheck("docker", "Docker is reachable", CheckStatus.Fail, ex.Message));
            return new DoctorReport(checks);
        }

        checks.Add(new DoctorCheck("docker", "Docker is reachable", CheckStatus.Pass, $"{info.OperatingSystem} on {info.Architecture}"));

        FoundPool? found = null;
        string? foreign = null;
        try
        {
            found = await _finder.FindAsync(settings, defaultPoolDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlBranchException ex) when (ex.Code == ErrorCodes.ForeignContainer)
        {
            foreign = ex.Message;
        }

        var image = found?.Engine?.Image ?? settings.EngineImage;
        checks.Add(await CheckImageAsync(image, cancellationToken).ConfigureAwait(false));

        var pool = foreign is not null
            ? new PoolChecks([new DoctorCheck("pool", "Pool is writable", CheckStatus.Fail, foreign)])
            : await CheckPoolAsync(settings, defaultPoolDirectory, found, image, info, cancellationToken).ConfigureAwait(false);
        checks.AddRange(pool.Checks);

        checks.Add(CheckPort(settings, found));
        checks.Add(CheckHost(info));
        return new DoctorReport(checks);
    }

    private sealed record PoolChecks(IReadOnlyList<DoctorCheck> Checks);

    private async Task<DoctorCheck> CheckImageAsync(string image, CancellationToken cancellationToken)
    {
        var present = await _runtime.ImageExistsAsync(image, cancellationToken).ConfigureAwait(false);
        return new DoctorCheck("image", "Engine image is available", present ? CheckStatus.Pass : CheckStatus.Warn,
            present ? image : $"{image} is not downloaded yet. 'sqlbranch init' pulls it, which needs network access and about 1.5 GB.");
    }

    private async Task<PoolChecks> CheckPoolAsync(
        Settings settings,
        string defaultPoolDirectory,
        FoundPool? found,
        string image,
        RuntimeInfo info,
        CancellationToken cancellationToken)
    {
        const string title = "Pool is writable";
        try
        {
            if (found is not null)
            {
                return await CheckExistingPoolAsync(found, image, cancellationToken).ConfigureAwait(false);
            }

            // Before init: look at where init would put the pool, without creating anything that lasts.
            if (settings.PoolBackend == PoolBackend.Plain)
            {
                return await CheckPlannedDirectoryAsync(settings, defaultPoolDirectory, image, cancellationToken).ConfigureAwait(false);
            }

            var managed = new ManagedPool(_runtime, _paths);
            if (!await managed.IsWritableAsync(cancellationToken).ConfigureAwait(false))
            {
                return new PoolChecks([new DoctorCheck("pool", title, CheckStatus.Fail, $"The container host's {_paths.Root} is not writable.")]);
            }

            var free = await managed.FreeBytesAsync(cancellationToken).ConfigureAwait(false);
            return new PoolChecks(
            [
                new DoctorCheck("pool", title, CheckStatus.Pass, $"{_paths.Root} on the container host; 'sqlbranch init' creates the pool there."),
                SpaceCheck(free, null, "free on the container host"),
            ]);
        }
        catch (ManagedPoolException ex)
        {
            return new PoolChecks([new DoctorCheck("pool", title, CheckStatus.Fail, ex.Message)]);
        }
        catch (SqlBranchException ex) when (ex.Code != ErrorCodes.DockerUnavailable)
        {
            return new PoolChecks([new DoctorCheck("pool", title, CheckStatus.Fail, ex.Message)]);
        }
    }

    private async Task<PoolChecks> CheckExistingPoolAsync(FoundPool found, string image, CancellationToken cancellationToken)
    {
        const string title = "Pool is writable";
        if (found.Backend == PoolProvisioner.BackendLoopXfs &&
            !await new ManagedPool(_runtime, _paths).IsMountedAsync(cancellationToken).ConfigureAwait(false))
        {
            return new PoolChecks([new DoctorCheck("pool", title, CheckStatus.Warn,
                $"The managed pool {found.Location} is not mounted. 'sqlbranch engine start' mounts it again.")]);
        }

        // The probe files are written only into a volume SqlBranch created.
        if (found.Location.Kind == PoolKind.Volume &&
            !await _runtime.VolumeIsManagedAsync(found.Location.Source, cancellationToken).ConfigureAwait(false))
        {
            return new PoolChecks([new DoctorCheck("pool", title, CheckStatus.Fail,
                $"The Docker volume '{found.Location.Source}' was not created by SqlBranch, so SqlBranch will not write to it.")]);
        }

        return await InspectPoolAsync(found.Location, image, found.Location.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<PoolChecks> CheckPlannedDirectoryAsync(Settings settings, string defaultPoolDirectory, string image, CancellationToken cancellationToken)
    {
        const string title = "Pool is writable";
        var directory = settings.Pool ?? defaultPoolDirectory;
        var location = new PoolLocation(PoolKind.Directory, directory);
        if (!_finder.DirectoryExists(directory))
        {
            return new PoolChecks([new DoctorCheck("pool", title, CheckStatus.Pass, $"{directory} does not exist yet; 'sqlbranch init' creates it.")]);
        }

        if (await DirectoryPool.InspectAsync(_runtime, image, location, cancellationToken).ConfigureAwait(false) == DirectoryState.Foreign)
        {
            return new PoolChecks([new DoctorCheck("pool", title, CheckStatus.Fail,
                $"{directory} is not empty and is not a SqlBranch pool, so 'sqlbranch init' would refuse it.")]);
        }

        return await InspectPoolAsync(location, image, directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes and deletes a file in the pool, reads its space and probes the clone mode.</summary>
    private async Task<PoolChecks> InspectPoolAsync(PoolLocation location, string image, string name, CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>();
        var result = await PoolWork.RunAsync(_runtime, image, location, WritableAndSpaceScript, readOnly: false, cancellationToken).ConfigureAwait(false);
        var writable = result.StdOut.Contains("SQLBRANCH_WRITABLE=yes", StringComparison.Ordinal);
        checks.Add(new DoctorCheck("pool", "Pool is writable", writable ? CheckStatus.Pass : CheckStatus.Fail,
            writable ? name : $"{name} cannot be written to (exit code {result.ExitCode})."));

        var spaceLine = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("SQLBRANCH_SPACE|", StringComparison.Ordinal));
        var space = spaceLine is null ? null : StatusReporter.ParseSpace(spaceLine["SQLBRANCH_SPACE|".Length..]);
        checks.Add(space is null
            ? new DoctorCheck("space", "Free pool space", CheckStatus.Fail, $"Could not read the free space of {name}.")
            : SpaceCheck(space.Free, space.Total, "free in the pool"));

        if (writable)
        {
            var mode = await CloneProbe.ProbeAsync(_runtime, image, location, cancellationToken).ConfigureAwait(false);
            checks.Add(mode == CloneMode.Thin
                ? new DoctorCheck("cloneMode", "Clone mode", CheckStatus.Pass, "thin: branches share blocks with their image.")
                : new DoctorCheck("cloneMode", "Clone mode", CheckStatus.Warn, CopyModeWarning));
        }

        return new PoolChecks(checks);
    }

    private static DoctorCheck SpaceCheck(long free, long? total, string what)
    {
        var status = free < FailBelow ? CheckStatus.Fail : free < WarnBelow ? CheckStatus.Warn : CheckStatus.Pass;
        var text = $"{Gib(free)} GiB {what}" + (total is { } t ? $" of {Gib(t)} GiB" : string.Empty);
        var advice = status switch
        {
            CheckStatus.Fail => " That is under 1 GiB.",
            CheckStatus.Warn => " That is under 10 GiB.",
            _ => string.Empty,
        };
        return new DoctorCheck("space", "Free pool space", status, text + "." + advice);
    }

    private static string Gib(long bytes) => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture);

    private DoctorCheck CheckPort(Settings settings, FoundPool? found)
    {
        var engine = found?.Engine;
        var port = engine is null ? settings.Port : EngineStarter.PortOf(engine, settings.Port);
        const string title = "Engine port is free or held by the engine";
        if (engine is { Running: true })
        {
            return new DoctorCheck("port", title, CheckStatus.Pass, $"127.0.0.1:{port} is held by {_engineName}.");
        }

        return _portIsFree(port)
            ? new DoctorCheck("port", title, CheckStatus.Pass, $"127.0.0.1:{port} is free.")
            : new DoctorCheck("port", title, CheckStatus.Fail, $"Port {port} on 127.0.0.1 is held by another process. Stop it or set 'port' to another value.");
    }

    /// <summary>The warning for a container host that is not x86-64 Linux, or null. Shared by <c>doctor</c> and <c>init</c>.</summary>
    public static string? ArchitectureWarning(string architecture)
    {
        ArgumentNullException.ThrowIfNull(architecture);
        var x86 = architecture.Equals("x86_64", StringComparison.OrdinalIgnoreCase) ||
            architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase);
        return x86
            ? null
            : $"The container host is {(architecture.Length == 0 ? "of an unknown architecture" : architecture)}, not x86-64 Linux. " +
              "The SQL Server engine runs under emulation, which is slower and less reliable.";
    }

    /// <summary>The warning for clone mode <c>copy</c>. Shared by <c>doctor</c> and <c>init</c>.</summary>
    public const string CopyModeWarning = "The clone mode is copy: branch creation will copy every file in full.";

    private static DoctorCheck CheckHost(RuntimeInfo info)
    {
        const string title = "Host is x86-64 Linux";
        return ArchitectureWarning(info.Architecture) is { } warning
            ? new DoctorCheck("host", title, CheckStatus.Warn, warning)
            : new DoctorCheck("host", title, CheckStatus.Pass, info.Architecture);
    }
}
