using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Output;
using SqlBranch.Core.Storage;

namespace SqlBranch.Core.Images;

/// <summary>What an export wrote.</summary>
/// <param name="Image">The image's name.</param>
/// <param name="Files">The number of database files in it.</param>
/// <param name="ImageBytes">The files' total size.</param>
/// <param name="ArchiveBytes">The size of the archive file.</param>
public sealed record ImageExportResult(string Image, int Files, long ImageBytes, long ArchiveBytes);

/// <summary>
/// <c>image export</c> to a file (design D3, D4 and D6). The image's files come from <c>docker exec ... tar -cf -</c> in the engine
/// container, pass through the client once (hashed, compressed) into <c>&lt;dest&gt;.sqlbranch-partial-&lt;id&gt;</c>, and that file is moved
/// onto the destination only when the engine's <c>tar</c> exited 0 and every entry had the size the manifest names. Any failure and a
/// cancellation delete the partial file, so the destination, and an older file there, is as it was.
/// </summary>
public sealed class ImageExporter
{
    /// <summary>The marker in the name of the file an export writes first. A killed export can leave one; it can be deleted.</summary>
    public const string PartialMarker = ".sqlbranch-partial-";

    private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

    private readonly IContainerRuntime _runtime;
    private readonly string _engineName;

    public ImageExporter(IContainerRuntime runtime, string engineName = ContainerLabels.EngineName)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _engineName = engineName;
    }

    /// <summary>Receives one line about every 10 seconds while data moves.</summary>
    public Action<string>? Progress { get; init; }

    internal Action? AfterFlushToDisk { get; init; }

    /// <summary>Exports the image to <paramref name="destination"/>, a full path whose folder exists.</summary>
    public async Task<ImageExportResult> ExportAsync(FoundPool pool, string name, string destination, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var manifest = await FindManifestAsync(pool, name, cancellationToken).ConfigureAwait(false);

        // Before any image file is read.
        if (!force && File.Exists(destination))
        {
            throw DestinationExists(destination);
        }

        var partial = $"{destination}{PartialMarker}{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            await WritePartialAsync(name, manifest, partial, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Commit(partial, destination, force);
        }
        catch
        {
            DeleteQuietly(partial);
            throw;
        }

        return new ImageExportResult(name, manifest.Files.Count, manifest.TotalBytes, new FileInfo(destination).Length);
    }

    /// <summary>
    /// Exports the image to Azure Blob Storage or S3 (design D5): the archive streams from the engine through the client, in parts, to
    /// <paramref name="store"/>, with no temporary copy. Without <paramref name="force"/> the object is checked for before any image file
    /// is read and the commit refuses to replace one that appeared meanwhile. On any failure and on cancellation the upload is aborted,
    /// and nothing is committed, so the destination is as it was.
    /// </summary>
    public async Task<ImageExportResult> ExportToStoreAsync(FoundPool pool, string name, IArchiveStore store, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(store);
        var manifest = await FindManifestAsync(pool, name, cancellationToken).ConfigureAwait(false);

        // Before any image file is read; with --force neither this check nor the conditional commit runs.
        if (!force && await store.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.DestinationExists,
                $"{store.DisplayUrl} already exists. Use --force to replace it, or choose another destination. It was not changed.");
        }

        await using var upload = await store.BeginUploadAsync(failIfExists: !force, manifest.TotalBytes, cancellationToken).ConfigureAwait(false);
        var sink = new PartUploadStream(upload, cancellationToken);
        var writer = CreateWriter(name, manifest, sink);
        try
        {
            await StreamArchiveAsync(name, manifest, writer, cancellationToken).ConfigureAwait(false);
            await sink.FinishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The writer's disposal must not send one more part of a failed archive.
            sink.Abandon();
            await upload.AbortAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }

        return new ImageExportResult(name, manifest.Files.Count, manifest.TotalBytes, sink.TotalBytes);
    }

    private async Task<ImageManifest> FindManifestAsync(FoundPool pool, string name, CancellationToken cancellationToken)
    {
        NameRules.ValidateImage(name);
        ImageCatalog.RequireRunningEngine(pool);

        var listing = await new ImageCatalog(_runtime, _engineName).ListAsync(cancellationToken).ConfigureAwait(false);
        return listing.Images.FirstOrDefault(i => i.Name == name)?.Manifest
            ?? throw new SqlBranchException(ErrorCodes.ImageNotFound, $"There is no image named '{name}'. 'sqlbranch image list' shows the images.");
    }

    private ImageArchiveWriter CreateWriter(string name, ImageManifest manifest, Stream output)
    {
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var total = manifest.TotalBytes;
        return new ImageArchiveWriter(output)
        {
            OnData = done =>
            {
                if (Progress is not null && clock.Elapsed - lastReport >= ProgressEvery)
                {
                    lastReport = clock.Elapsed;
                    Progress($"Exported {new ByteSize(done).ToBinary()} of {new ByteSize(total).ToBinary()} of image {name}.");
                }
            },
        };
    }

    private async Task WritePartialAsync(string name, ImageManifest manifest, string partial, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(partial, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = 1 << 16,
        });
        await using var writer = CreateWriter(name, manifest, file);
        await StreamArchiveAsync(name, manifest, writer, cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
        AfterFlushToDisk?.Invoke();
    }

    /// <summary>Streams the image's files from the engine's <c>tar</c> into <paramref name="writer"/> and finishes the archive.</summary>
    private async Task StreamArchiveAsync(string name, ImageManifest manifest, ImageArchiveWriter writer, CancellationToken cancellationToken)
    {

        string[] command = ["tar", "-C", ImagePaths.Data(name), "-cf", "-", "--", .. manifest.Files.Select(f => f.FileName)];
        await using var tar = await _runtime.ExecStreamAsync(_engineName, command, StreamDirection.Read, cancellationToken).ConfigureAwait(false);

        try
        {
            await writer.WriteHeadAsync(manifest, cancellationToken).ConfigureAwait(false);
            using var reader = new TarReader(tar.Stream, leaveOpen: true);
            foreach (var expected in manifest.Files)
            {
                var entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
                    ?? throw await EngineFailureAsync(tar, $"The engine's file stream ended before {expected.FileName}.", cancellationToken).ConfigureAwait(false);
                CheckEntry(entry, expected);
                await writer.WriteFileAsync(expected, entry.DataStream!, cancellationToken).ConfigureAwait(false);
            }

            if (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } extra)
            {
                throw new SqlBranchException(ErrorCodes.OperationFailed, $"The engine's file stream held an entry the manifest does not list ({Printable(extra.Name)}).");
            }

            // tar pads its last block; read to the end so it can exit.
            await tar.Stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            var finished = await tar.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (finished.ExitCode != 0)
            {
                throw new SqlBranchException(ErrorCodes.OperationFailed,
                    $"The engine's tar exited with code {finished.ExitCode.ToString(CultureInfo.InvariantCulture)} while reading image '{name}'{Detail(finished.StdErr)} " +
                    "The image may have been removed during the export. Nothing was written.");
            }

            await writer.FinishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // A killed `docker exec` ends the stream early; that is the cancel, not a damaged stream.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is InvalidDataException or EndOfStreamException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"The engine's file stream for image '{name}' ended early or is damaged: {ex.Message} Nothing was written.");
        }
        catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"Writing the archive failed: {ex.Message} Nothing was written.");
        }
    }

    /// <summary>An entry must be the next file of the manifest, a regular file, with exactly the size the manifest says.</summary>
    private static void CheckEntry(TarEntry entry, ImageFile expected)
    {
        var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
        if (name != expected.FileName || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The engine's file stream held '{Printable(entry.Name)}' where the manifest lists {expected.FileName}. Nothing was written.");
        }

        if (entry.Length != expected.SizeBytes)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"{expected.FileName} has {entry.Length.ToString(CultureInfo.InvariantCulture)} bytes in the engine but the manifest says " +
                $"{expected.SizeBytes.ToString(CultureInfo.InvariantCulture)}. Nothing was written.");
        }
    }

    /// <summary>The failure for a stream that ended too soon, with the engine's exit code and message when it already has them.</summary>
    private static async Task<SqlBranchException> EngineFailureAsync(IStreamingProcess process, string message, CancellationToken cancellationToken)
    {
        var finished = await process.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SqlBranchException(ErrorCodes.OperationFailed,
            $"{message} The engine's tar exited with code {finished.ExitCode.ToString(CultureInfo.InvariantCulture)}{Detail(finished.StdErr)} Nothing was written.");
    }

    private static string Detail(string stdErr)
    {
        var line = stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line is null ? "." : $": {line}.";
    }

    private static string Printable(string name) =>
        string.Concat((name.Length > 80 ? name[..80] + "..." : name).Select(c => char.IsControl(c) ? '?' : c));

    /// <summary>Moves the finished partial file onto the destination: refusing an existing one without force, replacing it with force.</summary>
    private static void Commit(string partial, string destination, bool force)
    {
        try
        {
            File.Move(partial, destination, overwrite: force);
        }
        catch (IOException) when (!force && File.Exists(destination))
        {
            // Another process wrote it while the archive was being made.
            throw DestinationExists(destination);
        }
        catch (IOException ex)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"The finished archive could not be moved to '{destination}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"The finished archive could not be moved to '{destination}': {ex.Message}");
        }
    }

    private static SqlBranchException DestinationExists(string destination) =>
        new(ErrorCodes.DestinationExists, $"'{destination}' already exists. Use --force to replace it, or choose another destination. It was not changed.");

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done; the name says what the file is.
        }
    }
}
