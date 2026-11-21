using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Engine;

/// <summary>What <c>teardown --force</c> removed and what it could not.</summary>
/// <param name="Databases">User databases it dropped.</param>
/// <param name="DatabaseFiles">Database files it found instead, when the engine could not be started.</param>
/// <param name="ImportBackups">Backups it deleted from the import folder.</param>
/// <param name="EngineRemoved">The engine container was removed by this run.</param>
/// <param name="FoldersRemoved">SqlBranch's folders and the marker were deleted from the pool.</param>
/// <param name="PoolFileRemoved">The managed pool's mount and file were removed by this run.</param>
/// <param name="VolumeRemoved">The pool volume (<c>sqlbranch-pool</c> by default) was removed by this run.</param>
/// <param name="Left">What still exists after a failed step; empty when everything is gone.</param>
/// <param name="Notes">Things that did not stop the teardown, such as a database that could not be dropped.</param>
public sealed record TeardownOutcome(
    IReadOnlyList<string> Databases,
    IReadOnlyList<string> DatabaseFiles,
    IReadOnlyList<string> ImportBackups,
    bool EngineRemoved,
    bool FoldersRemoved,
    bool PoolFileRemoved,
    bool VolumeRemoved,
    IReadOnlyList<string> Left,
    IReadOnlyList<string> Notes);

/// <summary>
/// <c>sqlbranch teardown --force</c> (design D15). Every step skips what is
/// already gone, so a run that stopped half way can be run again. Every path
/// it deletes is a constant under <c>/pool</c> inside a work container that
/// mounts only the found pool, or the managed pool's fixed paths.
/// </summary>
public sealed class TeardownRunner
{
    /// <summary>The folders SqlBranch creates in a pool, in the order they are deleted. The marker follows, last.</summary>
    public static readonly IReadOnlyList<string> PoolItems =
    [
        "engine", "images", "branches", "import", "build", ".probe",
    ];

    /// <summary>Databases that belong to SQL Server itself.</summary>
    public const string UserDatabasesSql = "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name";

    /// <summary>The engine's database files that are not user databases.</summary>
    private static readonly HashSet<string> SystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "master.mdf", "mastlog.ldf", "model.mdf", "modellog.ldf", "msdbdata.mdf", "msdblog.ldf",
        "tempdb.mdf", "templog.ldf", "model_msdbdata.mdf", "model_msdblog.ldf",
        "model_replicatedmaster.mdf", "model_replicatedmaster.ldf",
    };

    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly ManagedPaths _paths;
    private readonly EngineTimings _timings;
    private readonly string _engineName;
    private readonly string _poolVolume;

    public TeardownRunner(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        ManagedPaths? paths = null,
        EngineTimings? timings = null,
        string engineName = ContainerLabels.EngineName,
        string poolVolume = ContainerLabels.PoolVolume)
    {
        _poolVolume = poolVolume;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _paths = paths ?? ManagedPaths.Default;
        _timings = timings ?? EngineTimings.Default;
        _engineName = engineName;
    }

    /// <summary>
    /// Lists the engine's data files, one tagged line each. Read-only; used only
    /// when the engine cannot be started to list the databases.
    /// </summary>
    public const string DataFilesScript =
        "for f in /pool/engine/data/*; do [ -f \"$f\" ] && echo \"SQLBRANCH_DATAFILE|${f##*/}\"; done; true";

    /// <summary>
    /// Deletes SqlBranch's own folders from the pool and the marker last, after listing the import
    /// folder. On a directory or volume pool it does nothing unless the marker is there (the marker
    /// alone identifies a pool, design D3), and it never deletes <c>/pool</c> itself or any other file.
    /// </summary>
    public static string FoldersScript(bool guarded)
    {
        var guard = guarded
            ? $"[ -f {PoolMarker.Path} ]"
            : "true";
        var items = string.Join(' ', PoolItems.Select(i => $"{ContainerLabels.PoolMountPath}/{i}"));
        return $"if {guard}; then " +
            $"for f in {ContainerLabels.PoolMountPath}/import/* {ContainerLabels.PoolMountPath}/import/.[!.]*; do [ -e \"$f\" ] && echo \"SQLBRANCH_IMPORT|${{f##*/}}\"; done; " +
            $"rm -rf {items} && rm -f {PoolMarker.Path} && echo SQLBRANCH_TEARDOWN=done; " +
            "else echo SQLBRANCH_TEARDOWN=nothing; fi";
    }

    /// <summary>Runs the five steps of design D15 on the pool the lookup found.</summary>
    public async Task<TeardownOutcome> RunAsync(FoundPool pool, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(settings);

        var image = pool.Engine?.Image ?? settings.EngineImage;
        var isManaged = pool.Backend == PoolProvisioner.BackendLoopXfs;
        var isVolume = pool.Location.Kind == PoolKind.Volume;
        var isDirectory = pool.Location.Kind == PoolKind.Directory;
        var notes = new List<string>();
        var left = new List<string>();
        var foreignVolume = false;
        var databases = new List<string>();
        var files = new List<string>();
        var imports = new List<string>();
        var starter = new EngineStarter(_runtime, _connector, _redactor, _paths, _timings, _engineName);

        // 1. Make the pool reachable.
        var reachable = true;
        if (isManaged)
        {
            try
            {
                await starter.EnsureMountedAsync(pool.Backend, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlBranchException ex) when (ex.Code == ErrorCodes.PoolNotMounted)
            {
                reachable = false;
                notes.Add($"The managed pool is not mounted and could not be mounted again, so its user databases cannot be listed. {ex.Message}");
            }
        }

        // 2. List and drop the user databases, or list their files when the engine will not start.
        if (reachable)
        {
            var dropped = pool.Engine is not null &&
                await TryDropDatabasesAsync(starter, pool, settings, databases, notes, cancellationToken).ConfigureAwait(false);
            if (!dropped)
            {
                await ListDataFilesAsync(image, pool.Location, files, notes, cancellationToken).ConfigureAwait(false);
            }
        }

        // 3. Remove the engine container.
        var engineRemoved = false;
        if (pool.Engine is not null)
        {
            try
            {
                engineRemoved = await _runtime.RemoveAsync(_engineName, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlBranchException ex)
            {
                left.Add($"the engine container {_engineName} ({ex.Message})");
            }
        }

        // 4. Delete SqlBranch's folders and, last, the marker, in a work container that mounts only the pool.
        // A volume SqlBranch did not label is the user's own: nothing in it is touched.
        if (isVolume && !await _runtime.VolumeIsManagedAsync(_poolVolume, cancellationToken).ConfigureAwait(false))
        {
            foreignVolume = true;
            notes.Add($"The Docker volume '{_poolVolume}' was not created by SqlBranch, so it and its files were left alone.");
        }

        var foldersRemoved = false;
        string? foldersLeft = null;
        if (reachable && !foreignVolume)
        {
            try
            {
                var result = await PoolWork.RunAsync(_runtime, image, pool.Location, FoldersScript(isDirectory || isVolume), readOnly: false, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded && result.StdOut.Contains("SQLBRANCH_TEARDOWN=done", StringComparison.Ordinal))
                {
                    foldersRemoved = true;
                    imports.AddRange(Tagged(result.StdOut, "SQLBRANCH_IMPORT|"));
                }
                else if (!result.Succeeded)
                {
                    foldersLeft = $"the SqlBranch folders and the marker in {pool.Location} (exit code {result.ExitCode})";
                }
            }
            catch (SqlBranchException ex)
            {
                foldersLeft = $"the SqlBranch folders and the marker in {pool.Location} ({ex.Message})";
            }
        }

        // 5. Remove what holds the pool: the managed mount and file, or the volume. A directory is never removed.
        var poolFileRemoved = false;
        var volumeRemoved = false;
        if (isManaged)
        {
            var managed = new ManagedPool(_runtime, _paths);
            var unmounted = true;
            try
            {
                await managed.UnmountAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ManagedPoolException or SqlBranchException)
            {
                // The file stays while it is mounted: deleting it under a mount would only hide it.
                left.Add($"{_paths.MountPoint} and {_paths.PoolFile} ({ex.Message})");
                unmounted = false;
            }

            if (unmounted)
            {
                try
                {
                    await managed.DeleteFileAsync(cancellationToken).ConfigureAwait(false);
                    poolFileRemoved = true;
                    foldersLeft = null;
                }
                catch (Exception ex) when (ex is ManagedPoolException or SqlBranchException)
                {
                    left.Add($"{_paths.PoolFile} ({ex.Message})");
                }
            }
        }
        else if (isVolume && !foreignVolume)
        {
            try
            {
                volumeRemoved = await _runtime.RemoveVolumeAsync(_poolVolume, cancellationToken).ConfigureAwait(false);
                foldersLeft = null;
            }
            catch (SqlBranchException ex)
            {
                left.Add($"the volume {_poolVolume} ({ex.Message})");
            }
        }

        if (foldersLeft is not null)
        {
            left.Add(foldersLeft);
        }

        return new TeardownOutcome(databases, files, imports, engineRemoved, foldersRemoved, poolFileRemoved, volumeRemoved, left, notes);
    }

    /// <summary>Starts the engine if needed and drops every user database. False when the engine could not be used.</summary>
    private async Task<bool> TryDropDatabasesAsync(
        EngineStarter starter,
        FoundPool pool,
        Settings settings,
        List<string> dropped,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        try
        {
            var access = await starter.StartAsync(pool.Engine!, pool, settings.Port, null, null, cancellationToken).ConfigureAwait(false);
            await using var session = await _connector.ConnectAsync(access.Endpoint, access.Password, cancellationToken).ConfigureAwait(false);
            var rows = await session.QueryAsync(UserDatabasesSql, cancellationToken).ConfigureAwait(false);
            foreach (var name in rows.Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture)).OfType<string>())
            {
                try
                {
                    await session.ExecuteAsync(DropSql(name), cancellationToken).ConfigureAwait(false);
                    dropped.Add(name);
                }
#pragma warning disable CA1031 // The pool is deleted next, which removes the files anyway; the failure is reported.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    notes.Add($"Could not drop the database {name}: {_redactor.Redact(ex.Message)}");
                }
            }

            return true;
        }
#pragma warning disable CA1031 // Any failure to use the engine means "list the files instead".
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            notes.Add($"The engine could not be used to list the databases ({_redactor.Redact(ex.Message)}). The database files in the pool are listed instead.");
            return false;
        }
    }

    /// <summary>The statement that drops one database; the name comes from the engine itself.</summary>
    public static string DropSql(string name)
    {
        var quoted = "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
        return $"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted};";
    }

    private async Task ListDataFilesAsync(string image, PoolLocation location, List<string> files, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            var result = await PoolWork.RunAsync(_runtime, image, location, DataFilesScript, readOnly: true, cancellationToken).ConfigureAwait(false);
            files.AddRange(Tagged(result.StdOut, "SQLBRANCH_DATAFILE|").Where(IsUserDatabaseFile));
        }
        catch (SqlBranchException ex)
        {
            notes.Add($"Could not list the database files in {location}: {ex.Message}");
        }
    }

    private static bool IsUserDatabaseFile(string name) =>
        !SystemFiles.Contains(name) &&
        (name.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith(".ndf", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Tagged(string output, string prefix) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l[prefix.Length..]);
}
