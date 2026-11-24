using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Images;

public enum ImageSource
{
    Backup,
    Scripts,

    /// <summary>A backup the engine reads from an <c>https://</c> or <c>s3://</c> URL (design D2).</summary>
    Url,
}

/// <summary>What <c>image build</c> is asked to do. The client has already checked the file.</summary>
/// <param name="Name">The image name, valid by <see cref="NameRules"/>.</param>
/// <param name="BackupHostPath">The backup on the client, as the user gave it (made absolute).</param>
/// <param name="BackupBytes">The backup's size on the client.</param>
/// <param name="EngineBackupPath">The backup's path in the engine when the user keeps it under the pool's import folder, else null and it is staged.</param>
/// <param name="BackupSet">The backup set to use, from 1.</param>
/// <param name="KeepRecoveryModel">Do not switch the database to SIMPLE.</param>
public sealed record ImageBuildRequest(
    string Name,
    string BackupHostPath,
    long BackupBytes,
    string? EngineBackupPath = null,
    int BackupSet = 1,
    bool KeepRecoveryModel = false)
{
    public IReadOnlyList<ImageScript> SourceScripts { get; init; } = [];
    public IReadOnlyList<ImageScript> PostScripts { get; init; } = [];
    public TimeSpan? ScriptTimeout { get; init; }
    public bool RunsScripts => SourceScripts.Count != 0 || PostScripts.Count != 0;

    /// <summary>The backup URL of a URL build; the engine reads it directly and nothing is staged.</summary>
    public BackupUrl? Url { get; init; }

    /// <summary>The storage secret of a URL build; it goes into one engine credential and nowhere else.</summary>
    public StorageSecrets? Secrets { get; init; }
}

/// <summary>A finished build.</summary>
/// <param name="Manifest">What was written to the pool.</param>
/// <param name="Mode">The pool's clone mode.</param>
public sealed record ImageBuildResult(ImageManifest Manifest, CloneMode Mode)
{
    /// <summary>The version ID of the published image on a format-2 pool; null on format 1.</summary>
    public string? Version { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<(string Name, TimeSpan Duration)> ScriptTimes { get; init; } = [];
}

/// <summary>
/// Builds one image from a full backup as the ordered steps of design D4. Everything that
/// touches files runs inside the engine container; the client only copies the backup in.
/// Whatever the build created is removed when it fails, and the staging folder is removed
/// when it succeeds too.
/// </summary>
public sealed partial class ImageBuilder
{
    private static readonly string[] SealSteps =
        ["post-scripts", "check-database", "recovery-model", "checkpoint", "detach", "rebuild-log", "seal", "manifest"];

    public static IReadOnlyList<string> StepNames(ImageSource source) => source switch
    {
        ImageSource.Backup => ["check-name", "check-backup-space", "stage", "inspect", "check-restore-space", "restore", .. SealSteps],
        ImageSource.Scripts => ["check-name", "check-scripts", "create-database", "run-scripts", .. SealSteps],
        ImageSource.Url => ["check-name", "check-source", "build-folder", "credential", "inspect", "check-restore-space", "restore", "drop-credential", .. SealSteps],
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>The user and group of the engine inside its container; the files of an image belong to them.</summary>
    private const string EngineOwner = DirectoryPool.EngineOwner;

    private const string StagedBackupName = "backup.bak";

    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly string _engineName;
    private readonly TimeProvider _time;
    private readonly Func<string> _newId;

    public ImageBuilder(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        string engineName = ContainerLabels.EngineName,
        TimeProvider? time = null,
        Func<string>? newId = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _engineName = engineName;
        _time = time ?? TimeProvider.System;
        _newId = newId ?? (() => Guid.NewGuid().ToString("N")[..12]);
    }

    /// <summary>Runs after each step with the step's name. A test throws here to fail a build at a chosen step.</summary>
    public Func<string, CancellationToken, Task>? AfterStep { get; init; }

    /// <summary>Gets one line for the user while the build runs, such as the wait for a credential name. It goes to standard error.</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>Waits between two tries for a credential lock. A test replaces it, so no test sleeps.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    /// <summary>How often a build that waits for a credential name tries again.</summary>
    public static readonly TimeSpan CredentialPoll = TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeCharacters();

    /// <summary>The state of one build, kept so a failure can undo exactly what the build did.</summary>
    private sealed class State(ImageBuildRequest request, string id)
    {
        /// <summary>The pool format read with the credentials: 1 or 2.</summary>
        public int PoolFormat { get; init; } = 1;

        public ImageBuildRequest Request { get; } = request;

        public string Id { get; } = id;

        public string Database => ImagePaths.BuildDatabase(Id);

        public string BuildDirectory => ImagePaths.Build(Id);

        /// <summary>The backup as the engine reads it.</summary>
        public string BackupPath { get; set; } = string.Empty;

        public IReadOnlyList<DatabaseTrigger> Triggers { get; set; } = [];

        public required EngineEndpoint Endpoint { get; init; }
        public string? LoginPassword { get; set; }
        public List<string> Warnings { get; } = [];
        public List<(string Name, TimeSpan Duration)> ScriptTimes { get; } = [];
        public long InitialLogBytes { get; set; }
        public string Broker { get; set; } = "disabled";

        public string BackupSha256 { get; set; } = string.Empty;

        public string EngineVersion { get; set; } = string.Empty;

        /// <summary>The engine container's image reference, as Docker reports it.</summary>
        public string EngineImage { get; set; } = string.Empty;

        public BackupInspection Inspection { get; set; } = null!;

        /// <summary>The build folder was made, so it is removed at the end.</summary>
        public bool BuildDirectoryCreated { get; set; }

        /// <summary>The temporary database may exist, so it is dropped on failure.</summary>
        public bool DatabaseMayExist { get; set; }

        /// <summary>The seal of this build; it knows whether this build made <c>/pool/images/&lt;name&gt;</c>, so only this build removes it on failure.</summary>
        public ImageSealer? Sealer { get; set; }

        /// <summary>Restored file name in the build folder, by logical name.</summary>
        public Dictionary<string, string> RestoredNames { get; } = new(StringComparer.Ordinal);

        public string OriginalRecoveryModel { get; set; } = string.Empty;

        public string SealedRecoveryModel { get; set; } = string.Empty;

        public IReadOnlyList<(string Logical, string Physical, string Type, long Bytes)> Sealed { get; set; } = [];

        public ImageManifest? Manifest { get; set; }

        /// <summary>The version ID published on a format-2 pool.</summary>
        public string? Version { get; set; }

        public BacpacProvenance? Bacpac { get; init; }
        public BacpacOwnership? Ownership { get; init; }
        public BacpacOperation? Operation { get; init; }

        /// <summary>Every step ran, so the image folder is the finished image and cleanup must keep it.</summary>
        public bool Succeeded { get; set; }

        /// <summary>The build holds the application lock of its credential name.</summary>
        public bool CredentialLockTaken { get; set; }

        /// <summary><c>CREATE CREDENTIAL</c> was sent, so a credential of the name may exist.</summary>
        public bool CredentialCreateSent { get; set; }

        /// <summary>The id and creation time of the credential this build made, read back after <c>CREATE</c>.</summary>
        public int? CredentialId { get; set; }

        public string? CredentialDate { get; set; }

        /// <summary>The credential is gone (dropped, or no longer the one this build made).</summary>
        public bool CredentialDropped { get; set; }

        /// <summary>A full marker is in the build folder and its credential still exists, so no cleanup may delete the marker.</summary>
        public bool MarkerMustStay => CredentialId is not null && !CredentialDropped;
    }

    /// <summary>Builds the image, or throws the first failure after undoing what the build did.</summary>
    public async Task<ImageBuildResult> BuildAsync(FoundPool pool, int fallbackPort, ImageBuildRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(request);
        NameRules.ValidateImage(request.Name);

        var engine = pool.Engine ?? throw new SqlBranchException(ErrorCodes.NotInitialized,
            $"The pool {pool.Location} exists but the engine container does not. Run 'sqlbranch init' to create the engine again.");
        if (!engine.Running)
        {
            throw new SqlBranchException(ErrorCodes.EngineStopped, "The engine is stopped. Run 'sqlbranch engine start' to start it.");
        }

        var credentials = await EngineCredentials.ReadFileAsync(_runtime, engine.Image, pool.Location, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The engine credentials are missing from {pool.Location}. Run 'sqlbranch teardown --force' and 'sqlbranch init' to start again.");
        var password = credentials.Password;
        _redactor.Register(password);

        await using var session = await _connector.ConnectAsync(EngineEndpoint.Loopback(EngineStarter.PortOf(engine, fallbackPort)), password, cancellationToken).ConfigureAwait(false);

        // Two builds of one name cannot both go on. The lock lasts as long as the session.
        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Image(request.Name), cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.ImageExists,
                $"Another build of the image '{request.Name}' is running. Wait for it to finish, or choose another name.");
        }

        var state = new State(request, _newId()) { PoolFormat = credentials.Format, EngineImage = engine.Image, Endpoint = EngineEndpoint.Loopback(EngineStarter.PortOf(engine, fallbackPort)) };
        // The build lock is what lets status tell a running build from a dead one.
        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Build(state.Id), cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException($"The build id {state.Id} is in use. Try again.");
        }

        Exception? failure = null;
        try
        {
            await RunStepsAsync(session, state, cancellationToken).ConfigureAwait(false);
            state.Succeeded = true;
        }
#pragma warning disable CA1031 // Whatever failed, the build undoes what it did and then reports the failure itself.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        var leftovers = await CleanupAsync(session, state).ConfigureAwait(false);
        if (failure is not null)
        {
            if (leftovers.Count > 0 && failure is not OperationCanceledException)
            {
                // The build's own failure stays the headline; what the cleanup could not remove is added to it.
                var detail = $"{failure.Message} The cleanup could not remove {string.Join(" and ", leftovers)}. 'sqlbranch status' lists what is left.";
                throw new SqlBranchException(failure is SqlBranchException known ? known.Code : ErrorCodes.OperationFailed, detail);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (leftovers.Count > 0)
        {
            throw new SqlBranchException($"The image '{request.Name}' was built, but the build could not remove {string.Join(" and ", leftovers)}. 'sqlbranch status' lists what is left.");
        }

        var mode = await CloneProbe.ProbeInEngineAsync(_runtime, cancellationToken, _engineName).ConfigureAwait(false);
        return new ImageBuildResult(state.Manifest!, mode) { ScriptTimes = state.ScriptTimes, Warnings = state.Warnings, Version = state.Version };
    }

    private async Task RunStepsAsync(IEngineSession session, State state, CancellationToken cancellationToken)
    {
        var request = state.Request;

        // 1. The name is free. The check runs under the lock, so a second build of the name cannot slip in.
        if (await ImageCatalog.NameExistsAsync(_runtime, _engineName, request.Name, state.PoolFormat, cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.ImageExists, $"The image '{request.Name}' already exists. Remove it with 'sqlbranch image rm {request.Name}' or choose another name.");
        }

        await StepDoneAsync("check-name", cancellationToken).ConfigureAwait(false);

        if (request.RunsScripts)
        {
            await CheckGuestAsync(session, cancellationToken).ConfigureAwait(false);
        }

        await CleanLeftoverLoginsAsync(session, state, cancellationToken).ConfigureAwait(false);
        await SweepCredentialsAsync(session, state, null, takeLock: true, cancellationToken).ConfigureAwait(false);
        if (request.Url is not null)
        {
            await BuildUrlSourceAsync(session, state, cancellationToken).ConfigureAwait(false);
        }
        else if (request.SourceScripts.Count == 0)
        {
            await BuildBackupSourceAsync(session, state, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CreateScriptDatabaseAsync(session, state, cancellationToken).ConfigureAwait(false);
        }

        if (request.RunsScripts)
        {
            DatabaseTriggerCatalog.RequirePostScriptsAllowed(state.Triggers);
            await PrepareScriptLoginAsync(session, state, cancellationToken).ConfigureAwait(false);
            await RunScriptsAsync(state, cancellationToken).ConfigureAwait(false);
        }

        await FinishImageAsync(session, state, cancellationToken).ConfigureAwait(false);

        // The manifest is written last: an image exists once it has one.
        await WriteManifestAsync(state, cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("manifest", cancellationToken).ConfigureAwait(false);

        // On a format-2 pool the first version is published through the catalog, after the seal (design D3, D4).
        if (state.PoolFormat >= 2)
        {
            state.Version = await ImageVersions.PublishAsync(_runtime, _engineName, request.Name, _time, cancellationToken).ConfigureAwait(false);
            await StepDoneAsync("catalog", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BuildBackupSourceAsync(IEngineSession session, State state, CancellationToken cancellationToken)
    {
        var request = state.Request;
        // 2. Room for the staged copy, before anything is copied.
        var staging = request.EngineBackupPath is null;
        if (staging)
        {
            await RequireSpaceAsync(request.BackupBytes, $"the staged copy of {Path.GetFileName(request.BackupHostPath)}", cancellationToken).ConfigureAwait(false);
        }

        await StepDoneAsync("check-backup-space", cancellationToken).ConfigureAwait(false);

        // 3. Stage the backup in the build folder, unless the user keeps it in the import folder.
        state.BuildDirectoryCreated = true;
        await ShellAsync($"mkdir -p {Quoting.Shell(ImagePaths.BuildRoot)} && mkdir {Quoting.Shell(state.BuildDirectory)} && {ImagePaths.MakeBuildReserve(state.BuildDirectory)}", "creating the build folder", cancellationToken).ConfigureAwait(false);
        if (request.EngineBackupPath is { } imported)
        {
            state.BackupPath = imported;
        }
        else
        {
            state.BackupPath = $"{state.BuildDirectory}/{StagedBackupName}";
            await _runtime.CopyToAsync(_engineName, request.BackupHostPath, state.BackupPath, EngineOwner, cancellationToken).ConfigureAwait(false);
        }

        var hash = await ShellAsync($"echo SQLBRANCH_SHA256=$(sha256sum {Quoting.Shell(state.BackupPath)} | cut -d' ' -f1)", "hashing the backup", cancellationToken).ConfigureAwait(false);
        state.BackupSha256 = ValueOf(hash, "SQLBRANCH_SHA256=") ?? throw new SqlBranchException("Could not compute the backup's SHA-256 inside the engine.");
        await StepDoneAsync("stage", cancellationToken).ConfigureAwait(false);

        // 4. What is in the backup. Only the engine reads it.
        (state.EngineVersion, _) = await EngineStarter.ReadVersionAsync(session, cancellationToken).ConfigureAwait(false);
        state.Inspection = await BackupInspector.InspectAsync(session, state.BackupPath, request.BackupSet, state.EngineVersion, cancellationToken, Path.GetFileName(request.BackupHostPath)).ConfigureAwait(false);
        await StepDoneAsync("inspect", cancellationToken).ConfigureAwait(false);

        // 5. Room for the restored files. The staged copy is already in the pool, so it counts against the free space.
        await RequireSpaceAsync(state.Inspection.TotalBytes, "the restored database files", cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("check-restore-space", cancellationToken).ConfigureAwait(false);

        // 6. Restore under the temporary name with the files in the build folder.
        await RestoreAsync(session, state, cancellationToken).ConfigureAwait(false);
        state.Triggers = await DatabaseTriggerCatalog.ReadAsync(session, state.Database, cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("restore", cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishImageAsync(IEngineSession session, State state, CancellationToken cancellationToken)
    {
        var request = state.Request;
        await StepDoneAsync("post-scripts", cancellationToken).ConfigureAwait(false);
        IReadOnlyList<(string Logical, string Physical, string Type, long Bytes)>? restored = null;
        if (request.RunsScripts)
        {
            restored = await FreezeAndCheckAsync(session, state, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            state.Broker = await session.ScalarAsync($"SELECT is_broker_enabled FROM sys.databases WHERE name={Quoting.Literal(state.Database)}", cancellationToken).ConfigureAwait(false) is true ? "enabled" : "disabled";
        }

        // 7. Record the recovery model, then SIMPLE so a branch's log stays small.
        if (state.OriginalRecoveryModel.Length == 0)
        {
            state.OriginalRecoveryModel = await ReadRecoveryModelAsync(session, state, cancellationToken).ConfigureAwait(false);
        }
        state.SealedRecoveryModel = state.OriginalRecoveryModel;
        await StepDoneAsync("check-database", cancellationToken).ConfigureAwait(false);
        if (!request.KeepRecoveryModel)
        {
            await session.ExecuteAsync($"ALTER DATABASE {Quoting.Identifier(state.Database)} SET RECOVERY SIMPLE;", cancellationToken).ConfigureAwait(false);
            state.SealedRecoveryModel = "SIMPLE";
        }

        await StepDoneAsync("recovery-model", cancellationToken).ConfigureAwait(false);

        // 8. Everything in the log is in the data files, so the detach is clean.
        await session.ExecuteAsync($"EXEC {Quoting.Identifier(state.Database)}.sys.sp_executesql N'CHECKPOINT';", cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("checkpoint", cancellationToken).ConfigureAwait(false);
        restored ??= await FilesAsync(session, state, cancellationToken).ConfigureAwait(false);

        if (state.Ownership is { } ownership)
        {
            await ownership.BeginSealAsync(state.Operation!, cancellationToken).ConfigureAwait(false);
        }
        await DetachAsync(session, state, cancellationToken).ConfigureAwait(false);
        if (state.Ownership is null)
        {
            await DropBuildLoginAsync(session, state, cancellationToken).ConfigureAwait(false);
        }
        await StepDoneAsync("detach", cancellationToken).ConfigureAwait(false);

        // 9. A new, minimal log, so that creating a branch does not rewrite the source's log.
        await RebuildLogAsync(session, state, restored, cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("rebuild-log", cancellationToken).ConfigureAwait(false);

        // 10. Move the files into the image folder and make them read-only.
        await SealAsync(state, cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("seal", cancellationToken).ConfigureAwait(false);

    }

    private static async Task<string> ReadRecoveryModelAsync(IEngineSession session, State state, CancellationToken token)
    {
        var recovery = await session.ScalarAsync("SELECT recovery_model_desc FROM sys.databases WHERE name = @n",
            new Dictionary<string, object?> { ["n"] = state.Database }, token).ConfigureAwait(false);
        return Convert.ToString(recovery, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task StepDoneAsync(string step, CancellationToken cancellationToken)
    {
        if (AfterStep is { } hook)
        {
            await hook(step, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- restore ----

    private static async Task RestoreAsync(IEngineSession session, State state, CancellationToken cancellationToken)
    {
        var url = state.Request.Url;
        var parameters = new Dictionary<string, object?> { [BackupInspector.BackupParameter] = state.BackupPath };
        if (url is null)
        {
            parameters[BackupInspector.FileParameter] = state.Inspection.Set.Position;
        }
        var moves = new StringBuilder();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var file in state.Inspection.Files)
        {
            var name = UniqueFileName(file.PhysicalName, index, used);
            state.RestoredNames[file.LogicalName] = name;
            parameters[$"l{index}"] = file.LogicalName;
            parameters[$"p{index}"] = $"{state.BuildDirectory}/{name}";
            moves.Append(CultureInfo.InvariantCulture, $", MOVE @l{index} TO @p{index}");
            index++;
        }

        state.DatabaseMayExist = true;
        // A URL restore takes no FILE (set 1 only) and, for S3, names the region.
        var sql = url is null
            ? $"RESTORE DATABASE {Quoting.Identifier(state.Database)} FROM DISK = @bak WITH FILE = @file{moves}, STATS = 10;"
            : $"RESTORE DATABASE {Quoting.Identifier(state.Database)} FROM URL = @bak WITH {(moves.Length > 2 ? moves.ToString()[2..] + ", " : string.Empty)}STATS = 10{(BackupInspector.UrlOptions(url) is { Length: > 0 } options ? ", " + options : string.Empty)};";
        try
        {
            await session.ExecuteAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (EngineSqlException ex)
        {
            throw BackupInspector.MapError(ex, state.BackupPath, BackupStage.Restore, state.EngineVersion, url);
        }
    }

    /// <summary>A file name that is safe in a shell and a path, the same name as in the backup where possible, and unique in the folder.</summary>
    public static string UniqueFileName(string physicalName, int index, HashSet<string> used)
    {
        var slash = Math.Max(physicalName.LastIndexOf('/'), physicalName.LastIndexOf('\\'));
        var baseName = UnsafeCharacters().Replace(physicalName[(slash + 1)..], "_").TrimStart('.');
        if (baseName.Length == 0)
        {
            baseName = $"file{index.ToString(CultureInfo.InvariantCulture)}";
        }

        if (baseName.Length > 100)
        {
            baseName = baseName[^100..];
        }

        var name = baseName;
        if (!used.Add(name))
        {
            name = $"{index.ToString(CultureInfo.InvariantCulture)}_{baseName}";
            used.Add(name);
        }

        return name;
    }

    private static async Task<IReadOnlyList<(string Logical, string Physical, string Type, long Bytes)>> FilesAsync(
        IEngineSession session, State state, CancellationToken cancellationToken)
    {
        var rows = await session.QueryRowsAsync(
            "SELECT name, physical_name, type_desc, CAST(size AS bigint) * 8192 AS bytes FROM sys.master_files WHERE database_id = DB_ID(@n) ORDER BY file_id",
            new Dictionary<string, object?> { ["n"] = state.Database }, cancellationToken).ConfigureAwait(false);
        return
        [
            .. rows.Select(r => (
                Convert.ToString(r["name"], CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(r["physical_name"], CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(r["type_desc"], CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToInt64(r["bytes"], CultureInfo.InvariantCulture))),
        ];
    }

    private static async Task DetachAsync(IEngineSession session, State state, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync("EXEC master.sys.sp_detach_db @dbname = @n, @skipchecks = 'true';",
            new Dictionary<string, object?> { ["n"] = state.Database }, cancellationToken).ConfigureAwait(false);
        state.DatabaseMayExist = false;
    }

    // ---- the log ----

    private async Task RebuildLogAsync(
        IEngineSession session,
        State state,
        IReadOnlyList<(string Logical, string Physical, string Type, long Bytes)> restored,
        CancellationToken cancellationToken)
    {
        var data = restored.Where(f => f.Type != "LOG").ToList();
        var logs = restored.Where(f => f.Type == "LOG").ToList();
        if (logs.Count > 0)
        {
            if (state.Ownership is { } ownership)
            {
                await ownership.RemoveOriginalLogAsync(state.Operation!, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ShellAsync("rm -f " + string.Join(' ', logs.Select(l => Quoting.Shell(l.Physical))), "deleting the old log", cancellationToken).ConfigureAwait(false);
            }
        }

        // The data files are listed as they are; the engine writes a new log next to them.
        var files = string.Join(", ", data.Select(f => $"(FILENAME = {Quoting.Literal(f.Physical)})"));
        state.DatabaseMayExist = true;
        await session.ExecuteAsync($"CREATE DATABASE {Quoting.Identifier(state.Database)} ON {files} FOR ATTACH_REBUILD_LOG;", state.Ownership is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false);

        if (state.Ownership is { } rebuiltOwner)
        {
            await rebuiltOwner.RecordRebuiltAsync(state.Operation!).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        var rebuilt = await FilesAsync(session, state, cancellationToken).ConfigureAwait(false);
        // The new log is named after the temporary database. Give it the name the source's log had.
        if (logs.Count > 0 && rebuilt.FirstOrDefault(f => f.Type == "LOG") is { } newLog && newLog.Logical != logs[0].Logical)
        {
            try
            {
                await session.ExecuteAsync(
                    $"ALTER DATABASE {Quoting.Identifier(state.Database)} MODIFY FILE (NAME = {Quoting.Identifier(newLog.Logical)}, NEWNAME = {Quoting.Identifier(logs[0].Logical)});",
                    cancellationToken).ConfigureAwait(false);
                rebuilt = await FilesAsync(session, state, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The name is cosmetic; keep the generated one rather than fail the build over it.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                // Intentionally empty.
            }
        }

        // The engine resets the recovery model of a database attached with a new log. The image keeps the model
        // the manifest records, so set it again; the value is one of three fixed words, never user input.
        if (state.SealedRecoveryModel is "FULL" or "BULK_LOGGED" or "SIMPLE")
        {
            await session.ExecuteAsync($"ALTER DATABASE {Quoting.Identifier(state.Database)} SET RECOVERY {state.SealedRecoveryModel};", cancellationToken).ConfigureAwait(false);
        }

        // Builds without scripts can retain imported ownership triggers. Leave those triggers untouched.
        if (state.Request.RunsScripts || (state.Ownership is not null &&
            !state.Triggers.Any(trigger => trigger.Enabled && trigger.HandlesOwnershipChange)))
        {
            // The change of owner fires ALTER_AUTHORIZATION_DATABASE triggers as sa. The restricted login
            // can no longer connect and the detach ended every session, so this final comparison also
            // catches triggers planted in the background before detach.
            await RequireTriggersUnchangedAsync(session, state, cancellationToken).ConfigureAwait(false);
            await session.ExecuteAsync($"ALTER AUTHORIZATION ON DATABASE::{Quoting.Identifier(state.Database)} TO [sa];", cancellationToken).ConfigureAwait(false);
        }
        if (state.Ownership is { } detachOwner)
        {
            await detachOwner.VerifyTargetAsync(state.Operation!, cancellationToken).ConfigureAwait(false);
        }
        await DetachAsync(session, state, cancellationToken).ConfigureAwait(false);
        state.Sealed = rebuilt;
    }

    // ---- seal ----

    private async Task SealAsync(State state, CancellationToken cancellationToken)
    {
        var name = state.Request.Name;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var final = new List<(string Logical, string FileName, string Type, long Bytes, string Source)>();
        var firstLogName = state.Inspection?.Files.FirstOrDefault(f => f.Type == "L") is { } log ? state.RestoredNames[log.LogicalName] : null;
        var index = 0;
        foreach (var file in state.Sealed)
        {
            var isLog = file.Type == "LOG";
            var fileName = isLog && firstLogName is not null && used.Add(firstLogName)
                ? firstLogName
                : UniqueFileName(file.Physical, index, used);
            final.Add((file.Logical, fileName, isLog ? "log" : "data", file.Bytes, file.Physical));
            index++;
        }

        var files = final.Select(f => new SealFile(f.Source, f.FileName)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (state.Ownership is { } ownership)
        {
            await ownership.StageImageAsync(state.Operation!, files, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            state.Sealer ??= new ImageSealer(_runtime, _engineName, name);
            await state.Sealer.SealAsync(files, cancellationToken).ConfigureAwait(false);
        }

        var manifest = new ImageManifest(
            ImageManifest.CurrentFormat,
            name,
            state.Inspection?.Set.DatabaseName ?? name,
            state.EngineVersion,
            [.. final.Select(f => new ImageFile(f.Logical, f.FileName, f.Type, f.Bytes))],
            state.OriginalRecoveryModel,
            state.SealedRecoveryModel,
            state.Inspection?.LogBytes ?? state.InitialLogBytes,
            LogRebuilt: true,
            _time.GetUtcNow(),
            state.Inspection is null ? null : state.Request.Url?.BackupFile ?? Path.GetFileName(state.Request.BackupHostPath),
            state.Inspection is null || state.Request.Url is not null ? null : state.BackupSha256,
            new Dictionary<string, string>(),
            state.EngineImage) {
                DdlTriggers = state.Triggers.Select(t => t.Name).ToArray(),
                ServiceBroker = state.Broker,
                SourceType = state.Bacpac is not null ? "bacpac" : state.Inspection is null ? "scripts" : "backup",
                BacpacFile = state.Bacpac?.File,
                BacpacSha256 = state.Bacpac?.Sha256,
                BacpacUrl = state.Bacpac?.Url,
                BackupUrl = state.Inspection is null ? null : state.Request.Url?.Url,
                Scripts = state.Request.SourceScripts.Select(s => s.File).ToArray(),
                PostScripts = state.Request.PostScripts.Select(s => s.File).ToArray(),
            };
        state.Manifest = manifest;
    }

    private static Task WriteManifestAsync(State state, CancellationToken cancellationToken) =>
        state.Sealer!.WriteManifestAsync(state.Manifest!, cancellationToken);

    // ---- cleanup ----

    /// <summary>
    /// Removes what the build created: the temporary database, the build folder (with the staged backup),
    /// and on failure the image folder this build made. Returns what it could not remove, each with the reason.
    /// </summary>
    private async Task<IReadOnlyList<string>> CleanupAsync(IEngineSession session, State state)
    {
        var problems = new List<string>();

        // A failed build may have filled the pool: free the reserve before any SQL, so master's log has room to grow.
        if (state.BuildDirectoryCreated && !state.Succeeded)
        {
            await FreeReserveAsync(state).ConfigureAwait(false);
        }

        // The credential goes first: whatever else stays behind, a secret must not.
        await DropCredentialInCleanupAsync(session, state, problems).ConfigureAwait(false);

        if (state.DatabaseMayExist)
        {
            try
            {
                var name = Quoting.Identifier(state.Database);
                var literal = Quoting.Literal(state.Database);
                if (state.LoginPassword is not null)
                {
                    await session.ExecuteAsync($"ALTER LOGIN {name} DISABLE", CancellationToken.None).ConfigureAwait(false);
                    await KillSessionsAsync(session, state, CancellationToken.None).ConfigureAwait(false);
                }
                await session.ExecuteAsync($"IF DB_ID({literal}) IS NOT NULL AND DATABASEPROPERTYEX({literal}, 'Status') = 'ONLINE' ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", CancellationToken.None).ConfigureAwait(false);
                var outside = state.Request.RunsScripts
                    ? (await FilesAsync(session, state, CancellationToken.None).ConfigureAwait(false)).Where(f => !InsideBuildDirectory(state, f.Physical)).Select(f => f.Physical).ToArray()
                    : [];
                if (outside.Length == 0)
                {
                    await session.ExecuteAsync($"IF DB_ID({literal}) IS NOT NULL DROP DATABASE {name};", CancellationToken.None).ConfigureAwait(false);
                    state.DatabaseMayExist = false;
                }
                else
                {
                    await DetachAsync(session, state, CancellationToken.None).ConfigureAwait(false);
                    problems.Add($"outside files left in place: {string.Join(", ", outside)}");
                }
            }
#pragma warning disable CA1031 // A failed cleanup is reported with the build's result; it must not hide the build's own failure.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                problems.Add($"the temporary database {state.Database} and its build folder {state.BuildDirectory} ({ex.Message})");
            }
        }

        if (state.LoginPassword is not null)
        {
            try
            {
                await DropBuildLoginAsync(session, state, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"the build login {state.Database} ({ex.Message})");
            }
        }

        // The image folder is removed only after a failure, and only when this build made it.
        if (state.Sealer is { ImageDirectoryCreated: true } sealer && !state.Succeeded)
        {
            problems.AddRange(await sealer.RemoveCreatedAsync().ConfigureAwait(false));
        }

        if (state.BuildDirectoryCreated && !state.DatabaseMayExist)
        {
            // A marker whose credential still exists stays, so that status lists it and the next build drops it.
            problems.AddRange(state.MarkerMustStay
                ? await TryRemoveAsync(state.BuildDirectory, keepMarker: true).ConfigureAwait(false)
                : await TryRemoveAsync(state.BuildDirectory).ConfigureAwait(false));
        }

        return problems;
    }

    private async Task FreeReserveAsync(State state)
    {
        try
        {
            await ShellAsync($"rm -f {Quoting.Shell(ImagePaths.BuildReserve(state.BuildDirectory))}", "freeing the build's space reserve", CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the build folder's own removal reports anything that stays.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private Task<IEnumerable<string>> TryRemoveAsync(string path, bool keepMarker = false) =>
        EngineShell.TryRemoveAsync(_runtime, _engineName, path, keepMarker);

    // ---- shell and space ----

    private Task RequireSpaceAsync(long needed, string what, CancellationToken cancellationToken) =>
        PoolFreeSpace.RequireAsync(_runtime, _engineName, needed, what, cancellationToken);

    private Task<string> ShellAsync(string script, string what, CancellationToken cancellationToken) =>
        EngineShell.RunAsync(_runtime, _engineName, script, what, cancellationToken);

    private static string? ValueOf(string output, string prefix) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
}
