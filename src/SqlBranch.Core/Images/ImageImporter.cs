using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Runtime.ExceptionServices;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using ZstdSharp;

namespace SqlBranch.Core.Images;

/// <summary>
/// An import that was cancelled. <see cref="Leftovers"/> lists what its cleanup could not remove (empty when everything it made is gone),
/// so the caller can say so.
/// </summary>
public sealed class ImportCancelledException : OperationCanceledException
{
    public ImportCancelledException(IReadOnlyList<string> leftovers, CancellationToken token)
        : base("The import was cancelled.", token)
    {
        Leftovers = leftovers;
    }

    public IReadOnlyList<string> Leftovers { get; }
}

/// <summary>A finished import.</summary>
/// <param name="Manifest">What was written to the pool: the archive's manifest with the new name.</param>
/// <param name="Mode">The pool's clone mode.</param>
public sealed record ImageImportResult(ImageManifest Manifest, CloneMode Mode)
{
    /// <summary>The version ID of the imported image on a format-2 pool; null on format 1.</summary>
    public string? Version { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// <c>image import</c> (design D3, D4, D7 and D8). The archive's head was read and its manifest checked before the engine was looked
/// for; this class takes that open <see cref="ImageArchiveReader"/> and, under the build lock and the image name's lock, writes the
/// files into <c>/pool/build/&lt;id&gt;/data</c> through <c>docker exec -i ... tar -xf -</c> with a tar the client builds itself, checks
/// them against the archive's checksums, and seals them with the same seal as a build. No byte of an entry is sent before the entry's
/// name, type, place in the order and header size have been checked against the manifest.
/// </summary>
public sealed class ImageImporter
{
    /// <summary>The steps in the order they run; <see cref="AfterStep"/> sees each name.</summary>
    internal static readonly IReadOnlyList<string> StepNames =
        ["check-name", "check-version", "check-space", "build-folder", "write", "verify", "seal", "manifest"];

    private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly string _engineName;
    private readonly Func<string> _newId;
    private readonly TimeProvider _time;

    public ImageImporter(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        string engineName = ContainerLabels.EngineName,
        Func<string>? newId = null,
        TimeProvider? time = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _engineName = engineName;
        _newId = newId ?? (() => Guid.NewGuid().ToString("N")[..12]);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Runs after each step with the step's name. A test throws here to fail an import at a chosen step.</summary>
    public Func<string, CancellationToken, Task>? AfterStep { get; init; }

    /// <summary>Receives one line about every 10 seconds while data moves.</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>The file that records the process id of the engine's <c>tar</c> of an import, inside the container.</summary>
    internal static string PidFile(string id) => $"/tmp/sqlbranch-import-{id}.pid";

    /// <summary>
    /// The command that starts the engine's <c>tar</c>: it records its own process id, then becomes <c>tar</c>, so that cleanup can
    /// find the process inside the container (killing the <c>docker exec</c> client does not stop it).
    /// </summary>
    internal static string[] TarCommand(string id) =>
    [
        "sh", "-c", "echo $$ > \"$1\" && exec tar -C \"$2\" -xf - --no-same-owner --no-same-permissions", "sh",
        PidFile(id), $"{ImagePaths.Build(id)}/data",
    ];

    /// <summary>
    /// The script that makes sure the <c>tar</c> of an import has ended: it stops the process that the pid file names, only when that
    /// process's command line holds the import's own folder, waits for it to be gone, and deletes the pid file. It prints
    /// <c>SQLBRANCH_TAR=ended</c>, or <c>SQLBRANCH_TAR=running</c> and exits 1 when the process did not go away.
    /// </summary>
    internal const string EnsureEndedScript =
        "f=\"$1\"; d=\"$2\"; [ -f \"$f\" ] || { echo SQLBRANCH_TAR=ended; exit 0; }; pid=$(cat \"$f\"); " +
        "case \"$pid\" in ''|*[!0-9]*) rm -f \"$f\"; echo SQLBRANCH_TAR=ended; exit 0;; esac; " +
        "alive() { [ -r \"/proc/$pid/cmdline\" ] && tr '\\0' ' ' < \"/proc/$pid/cmdline\" | grep -qF -- \"$d\"; }; i=0; " +
        "while alive; do " +
        "[ \"$i\" -eq 0 ] && kill -TERM \"$pid\" 2>/dev/null; [ \"$i\" -eq 50 ] && kill -KILL \"$pid\" 2>/dev/null; " +
        "[ \"$i\" -ge 150 ] && { echo SQLBRANCH_TAR=running; exit 1; }; sleep 0.1; i=$((i+1)); done; " +
        "rm -f \"$f\"; echo SQLBRANCH_TAR=ended";

    private sealed class State(string id, string target, ImageArchiveReader archive)
    {
        /// <summary>The pool format read with the credentials: 1 or 2.</summary>
        public int PoolFormat { get; init; } = 1;

        /// <summary>The pool and the engine's image, so the format can be read again just before the image is published (design D6 step 2).</summary>
        public PoolLocation? Pool { get; init; }

        public string EngineImage { get; init; } = string.Empty;

        public string Id { get; } = id;

        public string Target { get; } = target;

        public ImageArchiveReader Archive { get; } = archive;

        public ImageManifest Source => Archive.Head.Manifest;

        public string BuildDirectory => ImagePaths.Build(Id);

        public bool BuildDirectoryCreated { get; set; }

        /// <summary>The engine's <c>tar</c> was started, so cleanup must see that it has ended before it removes anything.</summary>
        public bool TarStarted { get; set; }

        public IStreamingProcess? Process { get; set; }

        public ImageSealer? Sealer { get; set; }

        public List<string> Warnings { get; } = [];

        public ImageManifest? Manifest { get; set; }

        /// <summary>The version ID published on a format-2 pool.</summary>
        public string? Version { get; set; }

        public bool Succeeded { get; set; }

        public IReadOnlyList<ArchiveFileChecksum> Computed { get; set; } = [];
    }

    /// <summary>
    /// Imports the archive as <paramref name="targetName"/>, or throws the first failure after undoing what the import did.
    /// <paramref name="archive"/> must be open, its head read and its manifest checked by <see cref="ArchiveManifestChecks"/>.
    /// </summary>
    public async Task<ImageImportResult> ImportAsync(FoundPool pool, int fallbackPort, ImageArchiveReader archive, string targetName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(archive);
        NameRules.ValidateImage(targetName);

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
        await PoolUpgradeGuard.RequireWritableAsync(credentials, session, cancellationToken).ConfigureAwait(false);

        // An import and a build or another import of one name cannot both go on. The lock lasts as long as the session.
        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Image(targetName), cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.ImageExists,
                $"Another build or import of the image '{targetName}' is running. Wait for it to finish, or choose another name with --as.");
        }

        // The format may have changed since the first read (design D8); act on what it is now.
        credentials = await PoolUpgradeGuard.RecheckAsync(_runtime, _engineName, engine.Image, pool.Location, session, cancellationToken).ConfigureAwait(false);

        var state = new State(_newId(), targetName, archive) { PoolFormat = credentials.Format, Pool = pool.Location, EngineImage = engine.Image };
        // The build lock is what lets status tell a running import from a dead one: an import uses the build folder.
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
#pragma warning disable CA1031 // Whatever failed, the import undoes what it did and then reports the failure itself.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        var leftovers = await CleanupAsync(state).ConfigureAwait(false);
        if (failure is not null)
        {
            if (cancellationToken.IsCancellationRequested && failure is not OperationCanceledException)
            {
                // Killing the docker client ends its streams in ways that look like failures; the cause was the cancel.
                failure = new OperationCanceledException(cancellationToken);
            }

            if (failure is OperationCanceledException cancelled)
            {
                throw new ImportCancelledException(leftovers, cancelled.CancellationToken);
            }

            if (leftovers.Count > 0 && failure is not OperationCanceledException)
            {
                var detail = $"{failure.Message} The cleanup could not remove {string.Join(" and ", leftovers)}. 'sqlbranch status' lists what is left.";
                throw new SqlBranchException(failure is SqlBranchException known ? known.Code : ErrorCodes.OperationFailed, detail);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (leftovers.Count > 0)
        {
            throw new SqlBranchException($"The image '{targetName}' was imported, but the import could not remove {string.Join(" and ", leftovers)}. 'sqlbranch status' lists what is left.");
        }

        var mode = await CloneProbe.ProbeInEngineAsync(_runtime, cancellationToken, _engineName).ConfigureAwait(false);
        return new ImageImportResult(state.Manifest!, mode) { Warnings = state.Warnings, Version = state.Version };
    }

    private async Task RunStepsAsync(IEngineSession session, State state, CancellationToken cancellationToken)
    {
        var source = state.Source;

        // 1. The name is free. The check runs under the lock, so a build or import of the name cannot slip in.
        if (await ImageCatalog.NameExistsAsync(_runtime, _engineName, state.Target, state.PoolFormat, cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.ImageExists,
                $"The image '{state.Target}' already exists. Import the archive under another name with --as <name>, or remove the image with 'sqlbranch image rm {state.Target}'.");
        }

        await StepDoneAsync("check-name", cancellationToken).ConfigureAwait(false);

        // 2. The engine can attach these files (design D8).
        var (localVersion, _) = await EngineStarter.ReadVersionAsync(session, cancellationToken).ConfigureAwait(false);
        CheckVersion(source, localVersion, state.Warnings);
        await StepDoneAsync("check-version", cancellationToken).ConfigureAwait(false);

        // 3. Room for the files, before anything is written.
        await PoolFreeSpace.RequireAsync(_runtime, _engineName, source.TotalBytes, $"importing the image '{state.Target}'", cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("check-space", cancellationToken).ConfigureAwait(false);

        // 4. The build folder, which is what status lists if the client dies.
        state.BuildDirectoryCreated = true;
        await Shell($"mkdir -p {Quoting.Shell(ImagePaths.BuildRoot)} && mkdir {Quoting.Shell(state.BuildDirectory)} && mkdir {Quoting.Shell(state.BuildDirectory + "/data")}",
            "creating the build folder", cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("build-folder", cancellationToken).ConfigureAwait(false);

        // 5. Every entry is checked, then its bytes go to the engine's tar.
        await WriteAsync(state, cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("write", cancellationToken).ConfigureAwait(false);

        // 6. The archive's checksums, and the sizes the engine sees.
        await VerifyAsync(state, cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("verify", cancellationToken).ConfigureAwait(false);

        // 7. The seal of a build, then the manifest last: an image exists once it has one.
        state.Sealer = new ImageSealer(_runtime, _engineName, state.Target);
        await state.Sealer.SealAsync([.. source.Files.Select(f => new SealFile($"{state.BuildDirectory}/data/{f.FileName}", f.FileName))], cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("seal", cancellationToken).ConfigureAwait(false);

        state.Manifest = source with { Name = state.Target };
        await state.Sealer.WriteManifestAsync(state.Manifest, cancellationToken).ConfigureAwait(false);
        await StepDoneAsync("manifest", cancellationToken).ConfigureAwait(false);

        // On a finished format-2 pool the first version is published through the catalog, after the seal (design D3, D4). The format is
        // read again here, as for a build (design D6 step 2, D7).
        if (state.Pool is { } pool && await PoolUpgradeGuard.PublishesThroughCatalogAsync(_runtime, _engineName, state.EngineImage, pool, cancellationToken).ConfigureAwait(false))
        {
            state.Version = await ImageVersions.PublishAsync(_runtime, _engineName, state.Target, _time, cancellationToken).ConfigureAwait(false);
            await StepDoneAsync("catalog", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StepDoneAsync(string step, CancellationToken cancellationToken)
    {
        if (AfterStep is { } hook)
        {
            await hook(step, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<string> Shell(string script, string what, CancellationToken cancellationToken) =>
        EngineShell.RunAsync(_runtime, _engineName, script, what, cancellationToken);

    // ---- version ----

    /// <summary>A newer major version is refused; a newer build within the same major version is a warning (design D8).</summary>
    private static void CheckVersion(ImageManifest source, string localVersion, List<string> warnings)
    {
        if (!Version.TryParse(localVersion, out var local))
        {
            // The engine's own answer cannot be read; the attach at branch create reports a real problem.
            return;
        }

        var archive = Version.Parse(source.EngineVersion);
        var engineImage = string.IsNullOrEmpty(source.EngineImage) ? "unknown" : source.EngineImage;
        if (archive.Major > local.Major)
        {
            throw new SqlBranchException(ErrorCodes.VersionMismatch,
                $"The image was built on SQL Server {source.EngineVersion} (engine image {engineImage}), which is newer than this engine, {localVersion}. " +
                "SQL Server cannot attach files from a newer version. Import it into a pool whose engine is at least as new.");
        }

        if (archive.Major == local.Major && archive > local)
        {
            warnings.Add($"The image was built on SQL Server {source.EngineVersion}, a newer build than this engine, {localVersion}, of the same major version. " +
                "Branches from it should attach; if one cannot, 'sqlbranch branch create' reports the engine's error.");
        }
    }

    // ---- write ----

    private async Task WriteAsync(State state, CancellationToken cancellationToken)
    {
        var source = state.Source;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long done = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var computed = new List<ArchiveFileChecksum>();

        state.TarStarted = true;
        state.Process = await _runtime.ExecStreamAsync(_engineName, TarCommand(state.Id), StreamDirection.Write, cancellationToken).ConfigureAwait(false);
        var process = state.Process;

        try
        {
            var sink = new EngineInputStream(process.Stream);
            // Not disposed on failure: its footer would be written to a pipe whose reader may be gone or stuck.
#pragma warning disable CA2000
            var tar = new TarWriter(sink, TarEntryFormat.Pax, leaveOpen: true);
#pragma warning restore CA2000
            foreach (var file in source.Files)
            {
                var entry = await state.Archive.NextAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new SqlBranchException(ErrorCodes.ArchiveCorrupt, $"The archive ends before {file.FileName}; it is truncated.");
                CheckEntry(entry, file, source, seen);

                using var content = new HashingReadStream(entry.Data ?? Stream.Null, file.SizeBytes, bytes =>
                {
                    done += bytes;
                    if (Progress is not null && clock.Elapsed - lastReport >= ProgressEvery)
                    {
                        lastReport = clock.Elapsed;
                        Progress($"Imported {new ByteSize(done).ToBinary()} of {new ByteSize(source.TotalBytes).ToBinary()} of image {state.Target}.");
                    }
                });
                try
                {
                    await tar.WriteEntryAsync(
                        new PaxTarEntry(TarEntryType.RegularFile, file.FileName)
                        {
                            DataStream = content,
                            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                            ModificationTime = source.BuiltAt,
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ZstdException)
                {
                    throw new SqlBranchException(ErrorCodes.ArchiveCorrupt, $"The archive is damaged or ends early while it holds {file.FileName}.");
                }

                if (content.Count != file.SizeBytes)
                {
                    throw new SqlBranchException(ErrorCodes.ArchiveCorrupt,
                        $"{file.FileName} ends after {AsText(content.Count)} of {AsText(file.SizeBytes)} bytes; the archive is truncated.");
                }

                computed.Add(new ArchiveFileChecksum(file.FileName, file.SizeBytes, content.Hash()));
            }

            await tar.DisposeAsync().ConfigureAwait(false);
            var finished = await process.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (finished.ExitCode != 0)
            {
                throw TarFailure(finished.ExitCode, finished.StdErr);
            }

            // The tar exited 0: nothing in the container writes any more, so the end-check is not needed. Its pid file goes.
            state.TarStarted = false;
            await TryRemovePidFileAsync(state).ConfigureAwait(false);
            state.Computed = computed;
        }
        catch (EngineStreamException ex)
        {
            // The engine's tar stopped reading: its own words say why.
            cancellationToken.ThrowIfCancellationRequested();
            ProcessOutput? finished = null;
            try
            {
                finished = await process.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Reported below with what is known.
            }

            throw finished is { } output
                ? TarFailure(output.ExitCode, output.StdErr)
                : new SqlBranchException(ErrorCodes.OperationFailed, $"The engine's tar stopped reading: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private async Task TryRemovePidFileAsync(State state)
    {
        try
        {
            await EngineShell.RunAsync(_runtime, _engineName, $"rm -f {Quoting.Shell(PidFile(state.Id))}", "removing the tar's process id file", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A stale file in the container's /tmp is harmless, whatever kept it from being removed.
        }
    }

    /// <summary>The next entry must be the next listed file, a regular file, with the size the manifest says. Nothing has been sent yet.</summary>
    private static void CheckEntry(ArchiveEntry entry, ImageFile expected, ImageManifest manifest, HashSet<string> seen)
    {
        var shown = ArchiveManifestChecks.Printable(entry.Name);
        if (entry.Type is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            throw Invalid($"it holds '{shown}' as a {DescribeType(entry.Type)}, not a regular file.");
        }

        var wanted = ImageArchive.DataEntry(expected.FileName);
        if (entry.Name != wanted)
        {
            if (seen.Contains(entry.Name) || entry.Name is ImageArchive.HeadEntry)
            {
                throw Invalid($"it holds '{shown}' more than once.");
            }

            if (entry.Name.StartsWith(ImageArchive.DataPrefix, StringComparison.Ordinal) && manifest.Files.Any(f => ImageArchive.DataEntry(f.FileName) == entry.Name))
            {
                throw Invalid($"it holds '{shown}' where the manifest's order puts '{expected.FileName}'.");
            }

            if (entry.Name.Contains("..", StringComparison.Ordinal) || entry.Name.Contains('\\', StringComparison.Ordinal) ||
                entry.Name.StartsWith('/') || entry.Name.Count(c => c == '/') > 1)
            {
                throw Invalid($"it holds the entry '{shown}', whose name is not a plain file name of the image.");
            }

            throw Invalid($"it holds '{shown}', which its manifest does not list.");
        }

        if (entry.Length != expected.SizeBytes)
        {
            throw Invalid($"the header of '{expected.FileName}' says {AsText(entry.Length)} bytes but the manifest says {AsText(expected.SizeBytes)}.");
        }

        seen.Add(entry.Name);
    }

    private static string DescribeType(TarEntryType type) => type switch
    {
        TarEntryType.SymbolicLink => "symbolic link",
        TarEntryType.HardLink => "hard link",
        TarEntryType.Directory => "directory",
        TarEntryType.CharacterDevice or TarEntryType.BlockDevice => "device",
        TarEntryType.Fifo => "named pipe",
        _ => $"entry of type {type}",
    };

    private static SqlBranchException Invalid(string reason) =>
        new(ErrorCodes.ArchiveInvalid, $"The archive is not safe to import: {reason} Nothing was imported.");

    private static SqlBranchException TarFailure(int exitCode, string stdErr)
    {
        var line = stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var detail = line is null ? "." : $": {line}.";
        var code = stdErr.Contains("No space left", StringComparison.OrdinalIgnoreCase) ? ErrorCodes.PoolFull : ErrorCodes.OperationFailed;
        return new SqlBranchException(code, $"The engine's tar exited with code {AsText(exitCode)} while writing the image files{detail} Nothing was imported.");
    }

    private static string AsText(long number) => number.ToString(CultureInfo.InvariantCulture);

    // ---- verify ----

    private async Task VerifyAsync(State state, CancellationToken cancellationToken)
    {
        var source = state.Source;
        var archive = state.Archive;

        var last = await archive.NextAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.ArchiveCorrupt, $"The archive has no {ImageArchive.ChecksumsEntry}; it is truncated.");
        if (last.Name != ImageArchive.ChecksumsEntry || last.Type is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || last.Data is null)
        {
            throw Invalid($"it holds '{ArchiveManifestChecks.Printable(last.Name)}', which its manifest does not list.");
        }

        if (last.Length > ImageArchive.MaxMetadataBytes)
        {
            throw new SqlBranchException(ErrorCodes.ArchiveCorrupt, $"The archive's {ImageArchive.ChecksumsEntry} is too large to be one.");
        }

        var bytes = new byte[last.Length];
        try
        {
            await last.Data.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ZstdException)
        {
            throw new SqlBranchException(ErrorCodes.ArchiveCorrupt, $"The archive is damaged or ends early inside {ImageArchive.ChecksumsEntry}.");
        }

        var checksums = ArchiveChecksums.Parse(bytes);
        if (!string.Equals(checksums.ManifestSha256, archive.Head.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlBranchException(ErrorCodes.ArchiveCorrupt, "The archive's manifest does not match its checksums; the archive was changed.");
        }

        foreach (var file in source.Files)
        {
            var recorded = checksums.Files.FirstOrDefault(c => c is not null && c.FileName == file.FileName)
                ?? throw new SqlBranchException(ErrorCodes.ArchiveCorrupt, $"The archive has no checksum for {file.FileName}.");
            var actual = state.Computed.First(c => c.FileName == file.FileName);
            if (recorded.SizeBytes != actual.SizeBytes || !string.Equals(recorded.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlBranchException(ErrorCodes.ArchiveCorrupt, $"{file.FileName} does not match its checksum; the archive is damaged.");
            }
        }

        // Nothing may follow the checksums in the tar.
        if (await archive.NextAsync(cancellationToken).ConfigureAwait(false) is { } extra)
        {
            throw Invalid($"it holds '{ArchiveManifestChecks.Printable(extra.Name)}' after its checksums, which its manifest does not list.");
        }

        // The tar end marker can be decoded before the compressed frame is complete.
        await archive.ValidateEndAsync(cancellationToken).ConfigureAwait(false);

        // The engine's own view: the sizes of what the tar wrote.
        var names = string.Join(' ', source.Files.Select(f => Quoting.Shell(f.FileName)));
        var output = await Shell($"cd {Quoting.Shell(state.BuildDirectory + "/data")} && stat -c %s -- {names}", "checking the size of the written files", cancellationToken).ConfigureAwait(false);
        var sizes = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < source.Files.Count; i++)
        {
            var file = source.Files[i];
            if (i >= sizes.Length || !long.TryParse(sizes[i], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size != file.SizeBytes)
            {
                throw new SqlBranchException(ErrorCodes.ArchiveCorrupt,
                    $"{file.FileName} has {(i < sizes.Length ? sizes[i] : "no")} bytes in the pool but the manifest says {AsText(file.SizeBytes)}.");
            }
        }
    }

    // ---- cleanup ----

    /// <summary>
    /// Undoes what the import made. The engine's <c>tar</c> is made to end first, so that nothing is writing into the build folder when
    /// it is removed; if that cannot be confirmed, nothing is removed. The image folder goes only when this import made it.
    /// Returns what could not be removed, with the reason.
    /// </summary>
    private async Task<IReadOnlyList<string>> CleanupAsync(State state)
    {
        var problems = new List<string>();

        if (state.Process is { } process)
        {
            try
            {
                // Kills the docker client and waits for it to exit. This alone does not stop the tar inside the container.
                await process.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Cleanup goes on; the next step decides whether removing is safe.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                problems.Add($"the docker client of the import ({ex.Message})");
            }

            state.Process = null;
        }

        if (state.TarStarted && !await EnsureTarEndedAsync(state, problems).ConfigureAwait(false))
        {
            return problems;
        }

        if (state.Sealer is { ImageDirectoryCreated: true } sealer && !state.Succeeded)
        {
            problems.AddRange(await sealer.RemoveCreatedAsync().ConfigureAwait(false));
        }

        if (state.BuildDirectoryCreated)
        {
            problems.AddRange(await EngineShell.TryRemoveAsync(_runtime, _engineName, state.BuildDirectory).ConfigureAwait(false));
        }

        return problems;
    }

    /// <summary>True when the engine's <c>tar</c> of this import is known to have ended.</summary>
    private async Task<bool> EnsureTarEndedAsync(State state, List<string> problems)
    {
        try
        {
            var result = await _runtime.ExecAsync(_engineName,
                ["sh", "-c", EnsureEndedScript, "sh", PidFile(state.Id), $"{state.BuildDirectory}/data"], CancellationToken.None).ConfigureAwait(false);
            if (result.Succeeded && result.StdOut.Contains("SQLBRANCH_TAR=ended", StringComparison.Ordinal))
            {
                return true;
            }

            problems.Add($"{state.BuildDirectory} (the engine's tar could not be stopped, so nothing was removed)");
        }
#pragma warning disable CA1031 // Reported with the failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            problems.Add($"{state.BuildDirectory} (the engine's tar could not be checked, so nothing was removed: {ex.Message})");
        }

        return false;
    }

    /// <summary>A write that failed because the engine's side of the pipe is gone or stuck; not a failure of the archive.</summary>
    private sealed class EngineStreamException(Exception inner) : Exception("The engine's tar stopped reading.", inner);

    /// <summary>The engine's standard input: any I/O failure on it is the engine's, not the archive's.</summary>
    private sealed class EngineInputStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                inner.Write(buffer);
            }
            catch (IOException ex)
            {
                throw new EngineStreamException(ex);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new EngineStreamException(ex);
            }
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            try
            {
                await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new EngineStreamException(ex);
            }
        }

        public override void Flush()
        {
            try
            {
                inner.Flush();
            }
            catch (IOException ex)
            {
                throw new EngineStreamException(ex);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
