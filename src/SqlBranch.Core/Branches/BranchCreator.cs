using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Branches;

/// <summary>A created branch, with what a client needs to connect. The password is never part of it.</summary>
/// <param name="Name">The branch and database name.</param>
/// <param name="Image">The image it was cloned from.</param>
/// <param name="Mode">How the files were made: <c>thin</c> shares blocks with the image, <c>copy</c> is a full copy.</param>
/// <param name="Server">Host and port as a client's data source, for example <c>127.0.0.1,14330</c>.</param>
/// <param name="Database">The database name.</param>
/// <param name="User">The login to connect with.</param>
public sealed record BranchCreateResult(string Name, string Image, CloneMode Mode, string Server, string Database, string User)
{
    /// <summary>The version ID of the base on a format-2 pool; null on format 1.</summary>
    public string? ImageVersion { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// <c>branch create</c> as design D5 describes it. The files of the image are cloned inside the engine container,
/// the folder, the clone, <c>branch.json</c> and the attach run under the branch's application lock, and whatever the
/// command made is removed again when a step fails. The image is held in shared mode, so creates from one image run
/// together while <c>image rm</c> waits for them.
/// </summary>
public sealed class BranchCreator
{
    /// <summary>The login every branch is reached with. The engine has one: the password is in the pool's credentials file.</summary>
    public const string LoginName = "sa";

    /// <summary>The names of the steps in the order they run, as <see cref="AfterStep"/> sees them.</summary>
    public static readonly IReadOnlyList<string> StepNames = ["check", "folder", "clone", "record", "attach"];

    private const string ManifestTag = "SQLBRANCH_MANIFEST=";
    private const string BranchTag = "SQLBRANCH_BRANCH=";

    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly string _engineName;
    private readonly TimeProvider _time;
    private readonly Func<string> _newId;

    public BranchCreator(
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

    /// <summary>Runs after each step with the step's name. A test throws here to fail a create at a chosen step.</summary>
    public Func<string, CancellationToken, Task>? AfterStep { get; init; }

    /// <summary>What the create made so far, so a failure removes exactly that.</summary>
    private sealed class State(string name)
    {
        public string Name { get; } = name;

        /// <summary>This create made the branch folder (and the folder did not exist before).</summary>
        public bool FolderCreated { get; set; }

        /// <summary>The attach was started, so a database named like the branch may be ours.</summary>
        public bool AttachStarted { get; set; }

        /// <summary>The version the branch was cloned from on a format-2 pool (design D5).</summary>
        public string? Version { get; set; }
    }

    /// <summary>
    /// Creates the branch, or throws the first failure after undoing what the create did. <paramref name="copy"/> asks for full
    /// copies even on a pool that can share blocks (<c>--copy</c>); <paramref name="requireThin"/> is the setting of that name.
    /// </summary>
    public async Task<BranchCreateResult> CreateAsync(
        FoundPool pool, int fallbackPort, string name, string image, CancellationToken cancellationToken, bool copy = false, bool requireThin = false,
        TimeSpan? timeLimit = null, IReadOnlyDictionary<string, string>? labels = null)
    {
        ArgumentNullException.ThrowIfNull(pool);

        // Before any SQL runs and before any file is touched.
        NameRules.ValidateBranch(name);
        NameRules.ValidateImage(image);
        await using var engine = await BranchEngine.OpenAsync(_runtime, _connector, _redactor, _engineName, pool, fallbackPort, cancellationToken).ConfigureAwait(false);
        return await CreateAsync(engine, name, image, copy, requireThin, timeLimit, labels, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<BranchCreateResult> CreateAsync(BranchEngine engine, string name, string image,
        bool copy, bool requireThin, TimeSpan? timeLimit, IReadOnlyDictionary<string, string>? labels, CancellationToken cancellationToken)
    {
        await engine.RequireWritableAsync(cancellationToken).ConfigureAwait(false);
        var session = engine.Session;
        // Two creates of one name cannot both go on. The lock lasts as long as the session, so it is what
        // lets status tell a create that is running from one that died.
        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Branch(name), cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.BranchExists,
                $"Another command is creating or changing the branch '{name}'. Wait for it to finish, or choose another name.");
        }

        // Shared: other creates from this image run together, while 'image rm' and 'image build' of it wait.
        if (!await EngineLocks.TryTakeSharedAsync(session, EngineLocks.Image(image), cancellationToken).ConfigureAwait(false))
        {
            // An upgrade holds every existing image's lock while it waits (design D8): say so, not "being built or removed".
            await engine.RecheckFormatAsync(cancellationToken).ConfigureAwait(false);
            // An image an older client wrote has to be adopted by this command, which does not wait for the lock (design D7).
            if (engine.PoolFormat >= 2 &&
                await ImageAdoption.BusyIfUnadoptedAsync(_runtime, _engineName, image, cancellationToken).ConfigureAwait(false) is { } busy)
            {
                throw busy;
            }

            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Another command is building or removing the image '{image}'. Wait for it to finish, then run the command again.");
        }

        // The format may have changed since the first read (design D8); nothing has been written yet.
        await engine.RecheckFormatAsync(cancellationToken).ConfigureAwait(false);

        var createdAt = _time.GetUtcNow();
        var record = new BranchRecord(image, CloneModeNames.Of(CloneMode.Copy), createdAt, labels ?? new Dictionary<string, string>(),
            ExplicitCopy: copy, Ephemeral: timeLimit.HasValue, ExpiresAt: createdAt + timeLimit);
        var state = new State(name);
        Exception? failure = null;
        CloneMode mode = CloneMode.Copy;
        try
        {
            mode = await RunStepsAsync(session, state, record, copy, requireThin, engine.PoolFormat, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever failed, the create undoes what it did and then reports the failure itself.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        if (failure is not null)
        {
            var leftovers = await UndoAsync(session, state).ConfigureAwait(false);
            if (leftovers.Count > 0 && failure is not OperationCanceledException)
            {
                var detail = $"{failure.Message} The cleanup could not remove {string.Join(" and ", leftovers)}. 'sqlbranch status' lists what is left.";
                throw new SqlBranchException(failure is SqlBranchException known ? known.Code : ErrorCodes.OperationFailed, detail);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new BranchCreateResult(name, image, mode, engine.Server, name, LoginName)
        { ExpiresAt = record.ExpiresAt, Labels = record.Labels, ImageVersion = state.Version };
    }

    private async Task<CloneMode> RunStepsAsync(IEngineSession session, State state, BranchRecord record, bool copy, bool requireThin, int poolFormat, CancellationToken cancellationToken)
    {
        var name = state.Name;

        var image = record.Image;

        // 1. The image exists and the name is free. Both are read under the locks, so neither can change meanwhile.
        var source = await ReadImageAsync(image, poolFormat, cancellationToken).ConfigureAwait(false);
        var manifest = source.Manifest;
        state.Version = source.Id;
        await RequireFreeNameAsync(session, name, cancellationToken).ConfigureAwait(false);
        var poolMode = await CloneProbe.ProbeInEngineAsync(_runtime, cancellationToken, _engineName, _newId()).ConfigureAwait(false);

        // requireThin first (exit 3), then room for a full copy (exit 1): both before any file of the branch exists.
        // A branch made with --copy is a full copy in either mode, so requireThin does not apply to it.
        BranchGuards.RequireThin(requireThin, poolMode, copy);
        var mode = copy ? CloneMode.Copy : poolMode;
        await BranchGuards.RequireSpaceForCopyAsync(_runtime, _engineName, mode, manifest.TotalBytes, $"a full copy of the image '{image}'", cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("check", cancellationToken).ConfigureAwait(false);

        if (poolFormat >= 2 && source.Id is null)
        {
            // An image an older client wrote after the upgrade: give it its first version before the base is recorded (design D7).
            // After every refusal above, so a refused command writes nothing, and before the first file of the branch.
            source = await ImageAdoption.AdoptAsync(_runtime, _engineName, session, image, source, record.CreatedAt, cancellationToken).ConfigureAwait(false);
            state.Version = source.Id;
            manifest = source.Manifest;
        }

        // 2. The folder. 'mkdir' without -p, so a folder that was already there is never taken for this create's own.
        // The engine's shell runs as mssql (10001), which owns a folder it makes but may not give it to group 0: only
        // root may chown. On Docker Desktop the shared folder accepted the chown anyway; native Linux refuses it.
        await ShellAsync(
            $"if [ ! -d {Quoting.Shell(BranchPaths.BranchesRoot)} ]; then mkdir -p {Quoting.Shell(BranchPaths.BranchesRoot)} && " +
            $"if [ \"$(id -u)\" = 0 ]; then chown {DirectoryPool.EngineOwner} {Quoting.Shell(BranchPaths.BranchesRoot)}; fi; fi && " +
            $"mkdir {Quoting.Shell(BranchPaths.Branch(name))}",
            "creating the branch folder", cancellationToken).ConfigureAwait(false);
        state.FolderCreated = true;
        await StepDoneAsync("folder", cancellationToken).ConfigureAwait(false);

        // 3. Clone every file of the image. thin shares the blocks, copy writes full files. The image's files are
        // read-only, and so would a clone be, so the branch's own files are made writable for the engine.
        var data = BranchPaths.Data(name);
        var script = $"set -e; mkdir {Quoting.Shell(data)}; " + BranchFiles.CloneCommands(manifest, image, mode, data, source.Directory);

        await ShellAsync(script, "cloning the image's files", cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("clone", cancellationToken).ConfigureAwait(false);

        // 4. The record. It is written before the attach so that a client that dies between the clone and the attach
        // leaves a branch with a record and no database, which 'status' reports and 'branch rm' can clean up.
        // On a format-2 pool the base is the version just read, adopted above when an older client wrote the image (design D5, D7).
        record = record with { CloneMode = CloneModeNames.Of(mode), ImageVersion = state.Version, BaseKnown = state.Version is null ? null : true };
        await ShellAsync(BranchEngine.RecordWriteScript(name, record), "writing branch.json", cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("record", cancellationToken).ConfigureAwait(false);

        // 5. Attach. A name is never part of a path or a statement unquoted: the paths are literals, the name a bracketed identifier.
        var files = BranchFiles.AttachFiles(manifest, data);
        state.AttachStarted = true;
        await session.ExecuteAsync($"CREATE DATABASE {Quoting.Identifier(name)} ON {files} FOR ATTACH;", cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("attach", cancellationToken).ConfigureAwait(false);
        return mode;
    }

    private async Task StepDoneAsync(string step, CancellationToken cancellationToken)
    {
        if (AfterStep is { } hook)
        {
            await hook(step, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ImageVersionData> ReadImageAsync(string image, int poolFormat, CancellationToken cancellationToken)
    {
        if (poolFormat >= 2)
        {
            return await ImageCatalog.ReadVersionAsync(_runtime, _engineName, image, null, cancellationToken).ConfigureAwait(false)
                ?? throw new SqlBranchException(ErrorCodes.ImageNotFound, $"There is no image named '{image}'. 'sqlbranch image list' shows the images.");
        }

        var path = Quoting.Shell(ImagePaths.Manifest(image));
        var output = await ShellAsync($"if [ -f {path} ]; then echo {ManifestTag}$(base64 -w0 {path}); else echo {ManifestTag}none; fi", "reading the image", cancellationToken).ConfigureAwait(false);
        var value = ValueOf(output, ManifestTag);
        if (value is null or "none")
        {
            throw new SqlBranchException(ErrorCodes.ImageNotFound, $"There is no image named '{image}'. 'sqlbranch image list' shows the images.");
        }

        try
        {
            return new ImageVersionData(null, ".", ImageManifest.FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(value))));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"The manifest of the image '{image}' cannot be read, so no branch can be made from it.");
        }
    }

    /// <summary>The name is used by no database on the engine and by no branch folder.</summary>
    private async Task RequireFreeNameAsync(IEngineSession session, string name, CancellationToken cancellationToken)
    {
        var folder = Quoting.Shell(BranchPaths.Branch(name));
        var json = Quoting.Shell(BranchPaths.Json(name));
        var state = ValueOf(
            await ShellAsync($"if [ -e {json} ]; then echo {BranchTag}branch; elif [ -e {folder} ]; then echo {BranchTag}folder; else echo {BranchTag}none; fi", "checking the branch name", cancellationToken).ConfigureAwait(false),
            BranchTag);
        var databases = Convert.ToInt32(
            await session.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = @n", new Dictionary<string, object?> { ["n"] = name }, cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        if (state == "branch")
        {
            throw new SqlBranchException(ErrorCodes.BranchExists, $"The branch '{name}' already exists. Remove it with 'sqlbranch branch rm {name}' or choose another name.");
        }

        if (databases > 0)
        {
            throw new SqlBranchException(ErrorCodes.BranchExists,
                $"A database named '{name}' already exists on the engine, and it is not a branch, so SqlBranch will not use or change it. Choose another name.");
        }

        if (state == "folder")
        {
            throw new SqlBranchException(ErrorCodes.BranchExists,
                $"The folder {BranchPaths.Branch(name)} already exists in the pool, left by an earlier command. 'sqlbranch status' reports it. Delete that folder or choose another name.");
        }
    }

    // ---- undo ----

    /// <summary>
    /// Drops the database when it is attached from this branch's folder, then deletes the folder this create made.
    /// A database with the branch's name that has no file under the folder is somebody else's and is left alone.
    /// Returns what it could not remove, each with the reason.
    /// </summary>
    private async Task<IReadOnlyList<string>> UndoAsync(IEngineSession session, State state)
    {
        var problems = new List<string>();
        var name = state.Name;
        if (state.AttachStarted)
        {
            try
            {
                var prefix = BranchPaths.Branch(name) + "/";
                var ours = Convert.ToInt32(
                    await session.ScalarAsync(
                        "SELECT COUNT(*) FROM sys.master_files WHERE database_id = DB_ID(@n) AND LEFT(physical_name, LEN(@p)) = @p",
                        new Dictionary<string, object?> { ["n"] = name, ["p"] = prefix }, CancellationToken.None).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (ours > 0)
                {
                    var quoted = Quoting.Identifier(name);
                    await session.ExecuteAsync($"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", CancellationToken.None).ConfigureAwait(false);
                    await session.ExecuteAsync($"DROP DATABASE {quoted};", CancellationToken.None).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // Reported with the create's own failure; it must not hide it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                problems.Add($"the database {name} ({ex.Message})");
            }
        }

        // The folder goes only when the database is not left attached to it.
        if (state.FolderCreated && problems.Count == 0)
        {
            try
            {
                await ShellAsync($"rm -rf {Quoting.Shell(BranchPaths.Branch(name))}", "removing the branch folder", CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Reported with the create's own failure.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                problems.Add($"{BranchPaths.Branch(name)} ({ex.Message})");
            }
        }
        else if (state.FolderCreated)
        {
            problems.Add($"{BranchPaths.Branch(name)} (the database is still attached to it)");
        }

        return problems;
    }

    // ---- shell ----

    private async Task<string> ShellAsync(string script, string what, CancellationToken cancellationToken)
    {
        var result = await _runtime.ExecAsync(_engineName, ["sh", "-c", script], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var detail = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            throw new SqlBranchException($"The engine could not do this step ({what}), exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}{(detail is null ? string.Empty : ": " + detail)}.");
        }

        return result.StdOut;
    }

    private static string? ValueOf(string output, string prefix) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
}
