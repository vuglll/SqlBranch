using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using SqlBranch.Core.Containers;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Storage;
using Xunit.Abstractions;
using ZstdSharp;

namespace SqlBranch.IntegrationTests;

/// <summary>Archives the real writer would never make, for the cases of tasks 5.1 and 5.2 that need a damaged or an odd one.</summary>
internal static class TestArchives
{
    /// <summary>A stream of zeros of a given length, for an image file of any size without holding it.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, length - _position);
            buffer[..n].Clear();
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A valid archive, written by the real writer, of an image whose files are zeros of the given sizes. It is a few megabytes however big the image.</summary>
    public static async Task WriteZeroImageAsync(string path, ImageManifest like, string name, long dataBytes, long logBytes)
    {
        var manifest = like with
        {
            Name = name,
            Files = [new ImageFile("Zero", "zero.mdf", "data", dataBytes), new ImageFile("Zero_log", "zero_log.ldf", "log", logBytes)],
            UnknownFields = null,
        };
        await using var file = File.Create(path);
        await using var writer = new ImageArchiveWriter(file);
        await writer.WriteHeadAsync(manifest, CancellationToken.None);
        await writer.WriteFileAsync(manifest.Files[0], new ZeroStream(dataBytes), CancellationToken.None);
        await writer.WriteFileAsync(manifest.Files[1], new ZeroStream(logBytes), CancellationToken.None);
        await writer.FinishAsync(CancellationToken.None);
    }

    /// <summary>An archive that holds only the head entry, whose manifest lists files of the given size: enough for the checks that run before any file is written.</summary>
    public static async Task WriteHeadOnlyAsync(string path, ImageManifest like, string name, long sizeBytes)
    {
        var manifest = like with { Name = name, Files = [new ImageFile("Huge", "huge.mdf", "data", sizeBytes)], UnknownFields = null };
        await using var file = File.Create(path);
        await using var compressed = new CompressionStream(file, level: 1);
        await using var tar = new TarWriter(compressed, TarEntryFormat.Pax, leaveOpen: true);
        var head = Encoding.UTF8.GetBytes($$"""{ "archiveFormat": 1, "manifest": {{manifest.ToJson()}} }""");
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ImageArchive.HeadEntry) { DataStream = new MemoryStream(head) });
    }

    /// <summary>Copies an archive entry by entry, letting <paramref name="change"/> replace one entry's bytes (the checksums stay as exported).</summary>
    public static async Task RewriteAsync(string source, string destination, Func<string, byte[], byte[]> change)
    {
        await using var input = File.OpenRead(source);
        await using var decompressed = new DecompressionStream(input);
        using var reader = new TarReader(decompressed, leaveOpen: true);
        await using var output = File.Create(destination);
        await using var compressed = new CompressionStream(output, level: 1);
        await using var writer = new TarWriter(compressed, TarEntryFormat.Pax, leaveOpen: true);
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            using var bytes = new MemoryStream();
            await entry.DataStream!.CopyToAsync(bytes);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, entry.Name) { DataStream = new MemoryStream(change(entry.Name, bytes.ToArray())) });
        }
    }

    public static async Task TruncateAsync(string source, string destination)
    {
        var bytes = await File.ReadAllBytesAsync(source);
        await File.WriteAllBytesAsync(destination, bytes.AsMemory(0, bytes.Length / 2).ToArray());
    }
}

/// <summary>A handler that tells the test about each request and lets it cancel the run, for the cancelled upload of task 5.2.</summary>
internal sealed class HookHandler(HttpMessageHandler inner, Action<HttpRequestMessage> onSent) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        onSent(request);
        return response;
    }
}

/// <summary>
/// The export and import cases of tasks 5.1 and 5.2, written once and run in whatever clone mode the pool has and in JSON and in text, so the
/// secret leak test of 5.3 can run all of them. The file cases need only the test engine; the cloud cases use the fixture's MinIO and Azurite
/// through the client's own storage code, with an HTTP handler that trusts the fixture's certificate authority (design D9).
/// </summary>
internal sealed class ImageArchiveScenarios(CloudSourceFixture fixture, ITestOutputHelper output, OutputRecorder recorder)
{
    /// <summary>The image every case exports; built once from the fixture's Shop backup (100 rows).</summary>
    public const string Source = "exp-shop";

    /// <summary>Makes the names of the objects of one scenario set its own, so two classes of one collection never meet at one key.</summary>
    private readonly string _run = DockerHost.NewId();

    private readonly List<string> _commands = [];
    private readonly Lazy<HttpMessageHandler> _minio = new(() => fixture.Minio!.CreateHandler());
    private readonly Lazy<HttpMessageHandler> _azurite = new(() => fixture.Azurite!.CreateHandler());

    private EngineWorld World => fixture.World;

    /// <summary>A runtime over the real docker whose command lines are kept, for the exporters the cases build themselves.</summary>
    private DockerRuntime RecordingRuntime => new(new RecordingProcessRunner(new SystemProcessRunner(), _commands), World.Redactor);

    /// <summary>The Ctrl+C test sends SIGINT through <c>perl</c>; without it the test is skipped.</summary>
    public static bool PerlAvailable { get; } = FindOnPath("perl");

    private static bool FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Any(d => d.Length > 0 && File.Exists(Path.Combine(d, program)));

    private BranchKit Kit => new(fixture.Engine, output);

    /// <summary>Every <c>docker</c> command line the cases ran through the tool, for the leak test.</summary>
    public IReadOnlyList<string> Commands => _commands;

    private static string Tag(bool json) => json ? "json" : "text";

    private static string[] Format(bool json) => json ? ["--format", "json"] : [];

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private HttpMessageHandler HandlerFor(BackupUrl url) => url.Kind == BackupUrlKind.S3 ? _minio.Value : _azurite.Value;

    private (int Exit, string Out, string Err, TimeSpan Elapsed) Run(string label, bool json, int? partSize, Action<IReadOnlyList<string>>? beforeDockerStart, params string[] args)
    {
        var run = World.RunCliWithStorage(HandlerFor, _commands, partSize, beforeDockerStart, [.. args, .. Format(json)]);
        recorder.Add(label + " stdout", run.Out);
        recorder.Add(label + " stderr", run.Err);
        output.WriteLine($"{label} -> {run.Exit} ({run.Elapsed.TotalSeconds:F1} s)");
        return run;
    }

    private (int Exit, string Out, string Err, TimeSpan Elapsed) Run(string label, bool json, params string[] args) => Run(label, json, null, null, args);

    private async Task<string> InPoolAsync(string script) => await Kit.InPoolAsync(script);

    private static string CodeOf(string json) => Parse(json).GetProperty("error").GetProperty("code").GetString()!;

    private static string MessageOf(bool json, (int Exit, string Out, string Err, TimeSpan Elapsed) run) =>
        json ? Parse(run.Out).GetProperty("error").GetProperty("message").GetString()! : run.Out + run.Err;

    private async Task<string> CloneModeAsync() => await Kit.CloneModeAsync();

    /// <summary>Builds the image every case exports, unless an earlier case did.</summary>
    public async Task EnsureSourceAsync()
    {
        if ((await InPoolAsync($"ls /pool/images/{Source}/manifest.json 2>/dev/null")).Length > 0)
        {
            return;
        }

        var build = World.RunCli("image", "build", Source, "--from-bak", fixture.ShopBackup, "--format", "json");
        Assert.True(build.Exit == 0, build.Out + build.Err);
    }

    private async Task<ImageManifest> SourceManifestAsync() => ImageManifest.FromJson(await InPoolAsync($"cat /pool/images/{Source}/manifest.json"));

    private static string NewFolder(string purpose)
    {
        var folder = Directory.CreateTempSubdirectory($"sqlbranch-it-{purpose}-").FullName;
        return folder;
    }

    private async Task<List<string>> ImageNamesAsync() =>
        [.. Parse(Run("image list", true, "image", "list").Out).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

    private async Task AssertPoolUnchangedAsync(IReadOnlyList<string> imagesBefore, string imageThatMustNotExist)
    {
        var after = await ImageNamesAsync();
        Assert.DoesNotContain(imageThatMustNotExist, after);
        Assert.Equal(imagesBefore.Order(StringComparer.Ordinal), after.Order(StringComparer.Ordinal));
        Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/build"));
        Assert.Equal(0, World.RunCli("status").Exit);
    }

    /// <summary>The imported image holds the same bytes and the same manifest except its name, and a branch from it answers the 100 rows.</summary>
    private async Task AssertImportedAsync(string image, string cloneMode)
    {
        Assert.Equal(await Kit.ImageHashesAsync(Source), await Kit.ImageHashesAsync(image));
        var copy = await AssertImportedManifestAsync(Kit, Source, image);
        recorder.Add("manifest " + image, copy.ToJsonString());

        var branch = "b-" + image;
        try
        {
            var created = await Kit.CreateAsync(branch, image);
            Assert.Equal(cloneMode, created.GetProperty("cloneMode").GetString());
            await using var connection = await Kit.ConnectAsync(branch);
            Assert.Equal(100, await BranchKit.CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
        }
        finally
        {
            await Kit.DropBranchAsync(branch);
        }
    }

    public static async Task<JsonObject> AssertImportedManifestAsync(BranchKit kit, string source, string imported)
    {
        var original = JsonNode.Parse(await kit.InPoolAsync($"cat /pool/images/{source}/manifest.json"))!.AsObject();
        var copy = JsonNode.Parse(await kit.InPoolAsync($"cat /pool/images/{imported}/manifest.json"))!.AsObject();
        Assert.Equal(imported, (string?)copy["name"]);
        original["name"] = imported;
        Assert.True(JsonNode.DeepEquals(original, copy), "The imported manifest differs from the exported one by more than the name.");
        return copy;
    }

    private async Task RemoveImageAsync(string image)
    {
        var manifest = await InPoolAsync($"cat /pool/images/{image}/manifest.json 2>/dev/null");
        if (manifest.Length > 0)
        {
            recorder.Add("manifest " + image, manifest);
        }

        var removed = World.RunCli("image", "rm", image, "--force");
        output.WriteLine($"cleanup: image rm {image} -> {removed.Exit}");
        await InPoolAsync($"rm -rf /pool/images/{image}");
    }

    private static void AssertImported(bool json, (int Exit, string Out, string Err, TimeSpan Elapsed) run, string image, string cloneMode, string from)
    {
        Assert.True(run.Exit == 0, run.Out + run.Err);
        if (json)
        {
            var root = Parse(run.Out);
            Assert.Equal("image import", root.GetProperty("command").GetString());
            Assert.Equal(cloneMode, root.GetProperty("cloneMode").GetString());
            Assert.Equal(image, root.GetProperty("image").GetProperty("name").GetString());
            Assert.Equal(from, root.GetProperty("from").GetString());
        }
        else
        {
            Assert.Contains($"Clone mode: {cloneMode}", run.Out, StringComparison.Ordinal);
        }
    }

    // ---- task 5.1: files ----

    /// <summary>Export to a file and import it as a new name: the same bytes, the same manifest but the name, a branch that answers.</summary>
    public async Task FileRoundTripAsync(bool json)
    {
        await EnsureSourceAsync();
        var image = "imp-file-" + Tag(json);
        var folder = NewFolder("roundtrip");
        var archive = Path.Combine(folder, "shop.sbimg");
        try
        {
            var mode = await CloneModeAsync();
            var export = Run("image export to a file", json, "image", "export", Source, "--to", archive);
            Assert.True(export.Exit == 0, export.Out + export.Err);
            recorder.AddArchive("archive of " + image, archive);
            await using (var exported = File.OpenRead(archive))
            {
                var magic = new byte[4];
                await exported.ReadExactlyAsync(magic);
                Assert.Equal([0x28, 0xb5, 0x2f, 0xfd], magic);
            }
            Assert.Equal([archive], Directory.GetFileSystemEntries(folder));

            var import = Run("image import from a file", json, "image", "import", archive, "--as", image);
            AssertImported(json, import, image, mode, archive);
            Assert.Contains(image, await ImageNamesAsync());
            await AssertImportedAsync(image, mode);
            Assert.Equal(0, World.RunCli("status").Exit);
        }
        finally
        {
            await RemoveImageAsync(image);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>An existing destination is destination_exists and unchanged without --force; with --force it is replaced and imports.</summary>
    public async Task ExistingDestinationAsync(bool json)
    {
        await EnsureSourceAsync();
        var image = "imp-force-" + Tag(json);
        var folder = NewFolder("force");
        var archive = Path.Combine(folder, "shop.sbimg");
        try
        {
            Assert.Equal(0, Run("first export", json, "image", "export", Source, "--to", archive).Exit);
            var before = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(archive)));

            var again = Run("export onto an existing file", json, "image", "export", Source, "--to", archive);
            Assert.Equal(1, again.Exit);
            Assert.Contains("--force", MessageOf(json, again), StringComparison.Ordinal);
            if (json)
            {
                Assert.Equal("destination_exists", CodeOf(again.Out));
            }

            Assert.Equal(before, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(archive))));
            Assert.Equal([archive], Directory.GetFileSystemEntries(folder));

            var forced = Run("export with --force", json, "image", "export", Source, "--to", archive, "--force");
            Assert.True(forced.Exit == 0, forced.Out + forced.Err);
            Assert.Equal([archive], Directory.GetFileSystemEntries(folder));
            var import = Run("import of the replaced file", json, "image", "import", archive, "--as", image);
            Assert.True(import.Exit == 0, import.Out + import.Err);
            Assert.Equal(await Kit.ImageHashesAsync(Source), await Kit.ImageHashesAsync(image));
        }
        finally
        {
            await RemoveImageAsync(image);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>One changed byte and a cut archive are archive_corrupt naming the data file; no image is left and status is clean.</summary>
    public async Task CorruptAndTruncatedAsync(bool json)
    {
        await EnsureSourceAsync();
        var image = "imp-bad-" + Tag(json);
        var folder = NewFolder("corrupt");
        var good = Path.Combine(folder, "good.sbimg");
        try
        {
            var manifest = await SourceManifestAsync();
            var dataFile = manifest.Files.First(f => f.Type == "data").FileName;
            Assert.Equal(0, Run("export", json, "image", "export", Source, "--to", good).Exit);
            var before = await ImageNamesAsync();

            var flipped = Path.Combine(folder, "flipped.sbimg");
            await TestArchives.RewriteAsync(good, flipped, (name, bytes) =>
            {
                if (name == ImageArchive.DataEntry(dataFile))
                {
                    bytes[bytes.Length / 2] ^= 0xff;
                }

                return bytes;
            });
            var corrupt = Run("import of an archive with a changed byte", json, "image", "import", flipped, "--as", image);
            Assert.Equal(1, corrupt.Exit);
            Assert.Contains(dataFile, MessageOf(json, corrupt), StringComparison.Ordinal);
            if (json)
            {
                Assert.Equal("archive_corrupt", CodeOf(corrupt.Out));
            }

            await AssertPoolUnchangedAsync(before, image);

            var cut = Path.Combine(folder, "cut.sbimg");
            await TestArchives.TruncateAsync(good, cut);
            var truncated = Run("import of a cut archive", json, "image", "import", cut, "--as", image);
            Assert.Equal(1, truncated.Exit);
            if (json)
            {
                Assert.Equal("archive_corrupt", CodeOf(truncated.Out));
            }

            await AssertPoolUnchangedAsync(before, image);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>An archive whose files are bigger than the free space is pool_full naming both sizes, before anything is written.</summary>
    public async Task NotEnoughSpaceAsync(bool json)
    {
        await EnsureSourceAsync();
        var image = "imp-huge-" + Tag(json);
        var folder = NewFolder("space");
        var archive = Path.Combine(folder, "huge.sbimg");
        try
        {
            await TestArchives.WriteHeadOnlyAsync(archive, await SourceManifestAsync(), image, 1L << 45);
            var before = await ImageNamesAsync();

            var run = Run("import of an image that does not fit", json, "image", "import", archive);

            Assert.Equal(1, run.Exit);
            Assert.Contains("needs 32 TiB and ", MessageOf(json, run), StringComparison.Ordinal);
            Assert.Contains(" is available", MessageOf(json, run), StringComparison.Ordinal);
            if (json)
            {
                Assert.Equal("pool_full", CodeOf(run.Out));
            }

            await AssertPoolUnchangedAsync(before, image);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Image rm deletes a file of the image while the engine's tar reads it: exit 1 and no destination file.</summary>
    public async Task ImageRemovedDuringExportAsync(bool json)
    {
        await EnsureSourceAsync();
        var gone = "exp-gone-" + Tag(json);
        var folder = NewFolder("gone");
        var archive = Path.Combine(folder, "gone.sbimg");
        var copy = Path.Combine(folder, "copy.sbimg");
        try
        {
            Assert.Equal(0, Run("export for the copy", json, "image", "export", Source, "--to", copy).Exit);
            Assert.Equal(0, Run("import of the copy", json, "image", "import", copy, "--as", gone).Exit);
            var logFile = (await SourceManifestAsync()).Files.First(f => f.Type == "log").FileName;

            var run = Run("export while the image is removed", json, null,
                arguments =>
                {
                    if (arguments.Contains("-cf", StringComparer.Ordinal) && string.Join(' ', arguments).Contains($"/pool/images/{gone}/data", StringComparison.Ordinal))
                    {
                        var removed = World.Runtime.ExecAsync(World.EngineName, ["rm", "-f", $"/pool/images/{gone}/data/{logFile}"], CancellationToken.None).GetAwaiter().GetResult();
                        Assert.True(removed.Succeeded, removed.StdErr);
                    }
                },
                "image", "export", gone, "--to", archive);

            Assert.Equal(1, run.Exit);
            Assert.False(File.Exists(archive));
            Assert.Equal([copy], Directory.GetFileSystemEntries(folder));
        }
        finally
        {
            await RemoveImageAsync(gone);
            Directory.Delete(folder, recursive: true);
        }
    }

    private async Task<(string Archive, string Folder)> WriteBigArchiveAsync(string name)
    {
        var folder = NewFolder("big");
        var archive = Path.Combine(folder, name + ".sbimg");
        await TestArchives.WriteZeroImageAsync(archive, await SourceManifestAsync(), name, 1L << 30, 8L << 20);
        return (archive, folder);
    }

    /// <summary>Waits until the import's tar has written at least <paramref name="bytes"/> into its build folder.</summary>
    private async Task WaitForWrittenAsync(CliProcess import, long bytes)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Assert.False(import.HasExited, "The import ended before it had written " + bytes + " bytes.");
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(3), "The import wrote nothing in three minutes.");
            var written = await InPoolAsync("du -sk /pool/build/*/data 2>/dev/null | cut -f1 | head -1");
            if (long.TryParse(written, CultureInfo.InvariantCulture, out var kilobytes) && kilobytes * 1024 >= bytes)
            {
                output.WriteLine($"written {kilobytes} KiB after {clock.Elapsed.TotalSeconds:F1} s");
                return;
            }

            await Task.Delay(100);
        }
    }

    /// <summary>Starts the tool as its own process and has it log the docker command lines it runs to a file the test reads afterwards.</summary>
    private CliProcess StartLogged(bool interruptible, string[] args, out string commandLog)
    {
        commandLog = Path.Combine(Path.GetTempPath(), $"sqlbranch-it-commands-{DockerHost.NewId()}.log");
        World.Environment["SQLBRANCH_HARNESS_COMMAND_LOG"] = commandLog;
        try
        {
            return interruptible ? World.StartCliInterruptible(args) : World.StartCli(args);
        }
        finally
        {
            World.Environment.Remove("SQLBRANCH_HARNESS_COMMAND_LOG");
        }
    }

    private void RecordCommandLog(string label, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var lines = File.ReadAllLines(path);
        File.Delete(path);
        lock (_commands)
        {
            _commands.AddRange(lines);
        }

        recorder.Add(label + " docker command lines", string.Join('\n', lines));
    }

    /// <summary>Ctrl+C while the import writes files: non-zero exit, no image, no build folder, status 0.</summary>
    public async Task CancelledImportAsync(bool json)
    {
        if (!PerlAvailable)
        {
            throw new SkipTestException("perl is not on the PATH, and the Ctrl+C test starts the tool through it to get a SIGINT it does not ignore.");
        }

        await EnsureSourceAsync();
        var image = "imp-cancel-" + Tag(json);
        var (archive, folder) = await WriteBigArchiveAsync("zero-cancel");
        try
        {
            var before = await ImageNamesAsync();
            using var import = StartLogged(interruptible: true, ["image", "import", archive, "--as", image, .. Format(json)], out var commandLog);
            await WaitForWrittenAsync(import, 64L << 20);

            import.Interrupt();
            var result = await import.WaitAsync(TimeSpan.FromMinutes(2));
            recorder.Add("cancelled import stdout", result.Out);
            recorder.Add("cancelled import stderr", result.Err);
            output.WriteLine($"cancelled import -> {result.Exit}: {result.Out}{result.Err}");

            RecordCommandLog("cancelled import", commandLog);
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("cancelled", result.Out + result.Err, StringComparison.Ordinal);
            await AssertPoolUnchangedAsync(before, image);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The client is killed while the import writes files: status lists the build folder, and no image is listed.</summary>
    public async Task KilledImportAsync(bool json)
    {
        await EnsureSourceAsync();
        var image = "imp-killed-" + Tag(json);
        var (archive, folder) = await WriteBigArchiveAsync("zero-killed");
        string? leftover = null;
        try
        {
            using var import = StartLogged(interruptible: false, ["image", "import", archive, "--as", image, .. Format(json)], out var commandLog);
            await WaitForWrittenAsync(import, 64L << 20);
            import.Kill();
            RecordCommandLog("killed import", commandLog);

            var clock = Stopwatch.StartNew();
            (int Exit, string Out) status;
            do
            {
                Assert.True(clock.Elapsed < TimeSpan.FromMinutes(3), "status never reported the dead import.");
                await Task.Delay(1000);
                var run = World.RunCli("status", "--format", "json");
                status = (run.Exit, run.Out);
            }
            while (status.Exit == 0);

            recorder.Add("status after a killed import", status.Out);
            Assert.Equal(1, status.Exit);
            var mismatches = Parse(status.Out).GetProperty("mismatches").EnumerateArray().ToList();
            var buildFolder = Assert.Single(mismatches, m => m.GetProperty("kind").GetString() == "build_folder");
            leftover = buildFolder.GetProperty("name").GetString();
            Assert.StartsWith("/pool/build/", leftover, StringComparison.Ordinal);
            Assert.DoesNotContain(image, await ImageNamesAsync());
        }
        finally
        {
            // The tool has no cleanup command in 0.3: remove what the dead import left, as status says to. The tar may still be draining.
            if (leftover is not null && leftover.StartsWith("/pool/build/", StringComparison.Ordinal) && !leftover.Contains("..", StringComparison.Ordinal))
            {
                var quoted = "'" + leftover.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
                for (var attempt = 0; attempt < 20 && (await InPoolAsync($"rm -rf {quoted} /pool/images/{image}; ls -d {quoted} 2>/dev/null")).Length > 0; attempt++)
                {
                    await Task.Delay(500);
                }
            }

            await InPoolAsync("rm -f /tmp/sqlbranch-import-*.pid");
            Assert.Equal(0, World.RunCli("status").Exit);
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- task 5.2: MinIO and Azurite ----

    private AmazonS3Client S3Probe() => new(new BasicAWSCredentials(fixture.S3KeyId, fixture.S3Secret), new AmazonS3Config
    {
        ServiceURL = $"https://{fixture.Minio!.Address}:9000",
        ForcePathStyle = true,
        AuthenticationRegion = "us-east-1",
        MaxErrorRetry = 0,
        HttpClientFactory = new FixedHandlerFactory(_minio.Value),
    });

    private sealed class FixedHandlerFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);
    }

    private async Task<(long Length, string ETag)> S3ObjectAsync(string key)
    {
        using var client = S3Probe();
        var meta = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = CloudSourceFixture.Bucket, Key = key });
        return (meta.ContentLength, meta.ETag);
    }

    private static string KeyOf(string url) => string.Join('/', url["s3://".Length..].Split('/').Skip(2));

    /// <summary>Downloads an object a case stored and records it like an archive, so a secret stored inside it would be found.</summary>
    private async Task RecordStoredObjectAsync(string label, Func<string, Task> download)
    {
        var path = Path.Combine(NewFolder("stored"), "object.sbimg");
        try
        {
            await download(path);
            recorder.AddArchive(label, path);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private static List<string> ClientTempEntries() =>
        [.. Directory.EnumerateFileSystemEntries(Path.GetTempPath(), "*.sbimg*"), .. Directory.EnumerateFileSystemEntries(Path.GetTempPath(), "*sqlbranch-partial*"),
         .. Directory.EnumerateFileSystemEntries(Environment.CurrentDirectory, "*.sbimg*")];

    /// <summary>
    /// Export to MinIO and import it again. An existing object is destination_exists and unchanged. The import leaves no copy of the archive on
    /// the client, and no multipart upload is left in the bucket.
    /// </summary>
    public async Task S3RoundTripAsync(bool json)
    {
        await EnsureSourceAsync();
        var image = "imp-s3-" + Tag(json);
        var url = fixture.S3Url($"nightly/shop-{Tag(json)}-{_run}.sbimg");
        var mode = await CloneModeAsync();
        var tempBefore = ClientTempEntries();
        try
        {
            var export = Run("image export to s3", json, "image", "export", Source, "--to", url);
            Assert.True(export.Exit == 0, export.Out + export.Err);
            var stored = await S3ObjectAsync(KeyOf(url));
            if (json)
            {
                Assert.Equal(stored.Length, Parse(export.Out).GetProperty("export").GetProperty("archiveBytes").GetInt64());
                Assert.Equal(url, Parse(export.Out).GetProperty("export").GetProperty("to").GetString());
            }

            await RecordStoredObjectAsync("object stored in minio", async path =>
            {
                using var downloader = S3Probe();
                using var response = await downloader.GetObjectAsync(CloudSourceFixture.Bucket, KeyOf(url));
                await using var file = File.Create(path);
                await response.ResponseStream.CopyToAsync(file);
            });
            using (var client = S3Probe())
            {
                Assert.Empty((await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = CloudSourceFixture.Bucket })).MultipartUploads ?? []);
            }

            var again = Run("export onto an existing object", json, "image", "export", Source, "--to", url);
            Assert.Equal(1, again.Exit);
            Assert.Contains("--force", MessageOf(json, again), StringComparison.Ordinal);
            if (json)
            {
                Assert.Equal("destination_exists", CodeOf(again.Out));
            }

            Assert.Equal(stored, await S3ObjectAsync(KeyOf(url)));

            var import = Run("image import from s3", json, "image", "import", url, "--as", image);
            AssertImported(json, import, image, mode, url);
            Assert.Contains(image, await ImageNamesAsync());
            await AssertImportedAsync(image, mode);
            Assert.Equal(tempBefore, ClientTempEntries());
            Assert.Equal(0, World.RunCli("status").Exit);
        }
        finally
        {
            await RemoveImageAsync(image);
        }
    }

    /// <summary>Ctrl+C while parts are uploaded: no object at the key and no unfinished multipart upload.</summary>
    public async Task S3CancelledUploadAsync(bool json)
    {
        await EnsureSourceAsync();
        var url = fixture.S3Url($"cancelled/shop-{Tag(json)}-{_run}.sbimg");
        var key = KeyOf(url);
        var parsed = BackupUrl.Parse(url, null, "--to", null, null);
        var secrets = SqlBranch.Core.Secrets.StorageSecrets.Read(parsed, World.Environment, World.Redactor);
        using var cancel = new CancellationTokenSource();
        var parts = 0;
        var handler = new HookHandler(_minio.Value, request =>
        {
            if (request.Method == HttpMethod.Put && request.RequestUri!.Query.Contains("partNumber=", StringComparison.Ordinal) && Interlocked.Increment(ref parts) == 2)
            {
                cancel.Cancel();
            }
        });
        using var store = (IDisposable)ArchiveStores.Create(parsed, secrets, new StorageOptions { Handler = handler, PartSize = 16 * 1024 }, World.Redactor);
        var pool = await World.FindPoolAsync();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new ImageExporter(RecordingRuntime, World.EngineName).ExportToStoreAsync(pool, Source, (IArchiveStore)store, force: false, cancel.Token));

        Assert.True(parts >= 2, "The archive was too small to span two parts.");
        using var client = S3Probe();
        var listed = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = CloudSourceFixture.Bucket, Prefix = key });
        Assert.Empty(listed.S3Objects ?? []);
        var uploads = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = CloudSourceFixture.Bucket, Prefix = key });
        Assert.Empty(uploads.MultipartUploads ?? []);
        recorder.Add("cancelled upload", $"parts sent before the cancel: {parts}; {cancelled.GetType().Name}: {cancelled.Message}");
    }

    /// <summary>
    /// Another client writes the key while the parts go up: MinIO honours <c>If-None-Match: *</c> on the complete, so the export is
    /// destination_exists, the other client's object is unchanged and no multipart upload is left (task 5.2 repeats spike 1.2).
    /// </summary>
    public async Task S3ObjectCreatedDuringUploadAsync(bool json)
    {
        await EnsureSourceAsync();
        var url = fixture.S3Url($"race/shop-{Tag(json)}-{_run}.sbimg");
        var key = KeyOf(url);
        var other = Encoding.UTF8.GetBytes("written by another client");
        var parsed = BackupUrl.Parse(url, null, "--to", null, null);
        var secrets = SqlBranch.Core.Secrets.StorageSecrets.Read(parsed, World.Environment, World.Redactor);
        var puts = 0;
        var handler = new HookHandler(_minio.Value, request =>
        {
            if (request.Method == HttpMethod.Put && request.RequestUri!.Query.Contains("partNumber=", StringComparison.Ordinal) && Interlocked.Increment(ref puts) == 1)
            {
                fixture.Minio!.PutAsync($"/{CloudSourceFixture.Bucket}/{key}", other).GetAwaiter().GetResult();
            }
        });
        using var store = ArchiveStores.Create(parsed, secrets, new StorageOptions { Handler = handler }, World.Redactor) as IDisposable;
        var pool = await World.FindPoolAsync();

        var refused = await Assert.ThrowsAsync<SqlBranch.Core.SqlBranchException>(async () =>
            await new ImageExporter(RecordingRuntime, World.EngineName).ExportToStoreAsync(pool, Source, (IArchiveStore)store!, force: false, CancellationToken.None));

        Assert.Equal(SqlBranch.Core.Output.ErrorCodes.DestinationExists, refused.Code);
        Assert.Contains(url, refused.Message, StringComparison.Ordinal);
        using var client = S3Probe();
        using var stored = await client.GetObjectAsync(CloudSourceFixture.Bucket, key);
        using var copy = new MemoryStream();
        await stored.ResponseStream.CopyToAsync(copy);
        Assert.Equal(other, copy.ToArray());
        Assert.Empty((await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = CloudSourceFixture.Bucket, Prefix = key })).MultipartUploads ?? []);
        recorder.Add("object created during the upload", $"{url} {refused.Code.Code}: {refused.Message}");
    }

    /// <summary>A wrong secret on import: storage_unreachable, no docker command, the pool unchanged.</summary>
    public async Task S3WrongSecretAsync(bool json)
    {
        await EnsureSourceAsync();
        var url = fixture.S3Url($"wrong/shop-{Tag(json)}-{_run}.sbimg");
        var image = "imp-s3-wrong-" + Tag(json);
        var exported = Run("export the object the wrong secret will ask for", json, "image", "export", Source, "--to", url);
        Assert.True(exported.Exit == 0, exported.Out + exported.Err);
        var before = await ImageNamesAsync();
        var commandsBefore = _commands.Count;
        var saved = World.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"];
        World.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = fixture.WrongS3Secret;
        (int Exit, string Out, string Err, TimeSpan Elapsed) run;
        try
        {
            run = Run("image import with a wrong secret", json, "image", "import", url, "--as", image);
        }
        finally
        {
            World.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = saved;
        }

        Assert.Equal(1, run.Exit);
        Assert.Contains(url, MessageOf(json, run), StringComparison.Ordinal);
        if (json)
        {
            Assert.Equal("storage_unreachable", CodeOf(run.Out));
        }

        Assert.Equal(commandsBefore, _commands.Count);
        await AssertPoolUnchangedAsync(before, image);
    }

    private BlockBlobClient BlobProbe(string url) => new(new Uri(url), new AzureSasCredential(fixture.SasToken),
        new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(_azurite.Value, disposeHandler: false)) });

    /// <summary>
    /// Export to Azurite and import it again. An existing blob is destination_exists (Azurite answers the conditional commit with 409)
    /// and unchanged; with --force it is replaced.
    /// </summary>
    public async Task BlobRoundTripAsync(bool json)
    {
        await EnsureSourceAsync();
        var image = "imp-blob-" + Tag(json);
        var url = fixture.BlobUrl($"shop-{Tag(json)}-{_run}.sbimg");
        var mode = await CloneModeAsync();
        var tempBefore = ClientTempEntries();
        try
        {
            var export = Run("image export to azure blob", json, "image", "export", Source, "--to", url);
            Assert.True(export.Exit == 0, export.Out + export.Err);
            var stored = (await BlobProbe(url).GetPropertiesAsync()).Value;
            if (json)
            {
                Assert.Equal(stored.ContentLength, Parse(export.Out).GetProperty("export").GetProperty("archiveBytes").GetInt64());
            }

            var again = Run("export onto an existing blob", json, "image", "export", Source, "--to", url);
            Assert.Equal(1, again.Exit);
            Assert.Contains("--force", MessageOf(json, again), StringComparison.Ordinal);
            if (json)
            {
                Assert.Equal("destination_exists", CodeOf(again.Out));
            }

            Assert.Equal(stored.ETag, (await BlobProbe(url).GetPropertiesAsync()).Value.ETag);
            await RecordStoredObjectAsync("blob stored in azurite", async path =>
            {
                await using var file = File.Create(path);
                await BlobProbe(url).DownloadToAsync(file);
            });

            var forced = Run("export with --force onto an existing blob", json, "image", "export", Source, "--to", url, "--force");
            Assert.True(forced.Exit == 0, forced.Out + forced.Err);
            Assert.NotEqual(stored.ETag, (await BlobProbe(url).GetPropertiesAsync()).Value.ETag);

            var import = Run("image import from azure blob", json, "image", "import", url, "--as", image);
            AssertImported(json, import, image, mode, url);
            await AssertImportedAsync(image, mode);
            Assert.Equal(tempBefore, ClientTempEntries());
            Assert.Equal(0, World.RunCli("status").Exit);
        }
        finally
        {
            await RemoveImageAsync(image);
        }
    }

    /// <summary>A SAS with a wrong signature on import: storage_unreachable, no docker command, the pool unchanged.</summary>
    public async Task BlobWrongSasAsync(bool json)
    {
        await EnsureSourceAsync();
        var url = fixture.BlobUrl($"wrong-{Tag(json)}-{_run}.sbimg");
        var image = "imp-blob-wrong-" + Tag(json);
        var exported = Run("export the blob the wrong SAS will ask for", json, "image", "export", Source, "--to", url);
        Assert.True(exported.Exit == 0, exported.Out + exported.Err);
        var before = await ImageNamesAsync();
        var commandsBefore = _commands.Count;
        var saved = World.Environment["SQLBRANCH_AZURE_SAS_TOKEN"];
        World.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = fixture.WrongSasToken;
        (int Exit, string Out, string Err, TimeSpan Elapsed) run;
        try
        {
            run = Run("image import with a wrong SAS", json, "image", "import", url, "--as", image);
        }
        finally
        {
            World.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = saved;
        }

        Assert.Equal(1, run.Exit);
        Assert.Contains(url, MessageOf(json, run), StringComparison.Ordinal);
        Assert.True(MessageOf(json, run).Contains("HTTP 403 (AuthorizationFailure)", StringComparison.Ordinal), MessageOf(json, run));
        if (json)
        {
            Assert.Equal("storage_unreachable", CodeOf(run.Out));
        }

        Assert.Equal(commandsBefore, _commands.Count);
        await AssertPoolUnchangedAsync(before, image);
    }
}

internal static class OutputRecorderArchives
{
    /// <summary>Records an archive twice: its raw bytes as text (a leaked secret in a stored file would show) and its head and checksum entries unpacked.</summary>
    public static void AddArchive(this OutputRecorder recorder, string label, string path)
    {
        recorder.Add(label + " (bytes)", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        using var input = File.OpenRead(path);
        using var decompressed = new DecompressionStream(input);
        using var reader = new TarReader(decompressed, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.Name is ImageArchive.HeadEntry or ImageArchive.ChecksumsEntry)
            {
                using var text = new StreamReader(entry.DataStream!, Encoding.UTF8);
                recorder.Add($"{label} ({entry.Name})", text.ReadToEnd());
            }
            else
            {
                // A data entry is searched in blocks that overlap by more than the longest secret, so one cut by a block edge is still found.
                const int overlap = 1024;
                var buffer = new byte[(1 << 20) + overlap];
                var kept = 0;
                int read;
                while ((read = entry.DataStream!.Read(buffer, kept, 1 << 20)) > 0)
                {
                    var length = kept + read;
                    if (buffer.AsSpan(0, length).IndexOfAnyExcept((byte)0) >= 0)
                    {
                        recorder.Add($"{label} ({entry.Name}, non-zero block)", Encoding.Latin1.GetString(buffer, 0, length));
                    }

                    kept = Math.Min(overlap, length);
                    Array.Copy(buffer, length - kept, buffer, 0, kept);
                }
            }
        }
    }
}
