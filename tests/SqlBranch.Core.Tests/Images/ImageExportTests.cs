using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;
using ZstdSharp;

namespace SqlBranch.Core.Tests.Images;

/// <summary>
/// <c>image export</c> to a file on a fake runtime: the engine's <c>tar</c> is a stream a test scripts, the destination is a real folder.
/// </summary>
public sealed class ImageExportTests : IDisposable
{
    private readonly CliWorld _world = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("sqlbranch-export-").FullName;
    private readonly byte[] _data = new byte[1_500_000];
    private readonly byte[] _log = new byte[300_000];
    private readonly ImageManifest _manifest;

    private bool _imageExists = true;
    private string? _credentialsJson;
    private string? _catalogJson;
    private string? _versionDirectory;

    /// <summary>Scripts what the engine's tar delivers.</summary>
    private Func<CancellationToken, FakeStreamingProcess> _tar;

    public ImageExportTests()
    {
        new Random(1).NextBytes(_data);
        new Random(2).NextBytes(_log);
        _manifest = new ImageManifest(
            ImageManifest.CurrentFormat, "shop", "Shop", "16.0.4135.4",
            [new ImageFile("Shop", "shop.mdf", "data", _data.Length), new ImageFile("Shop_log", "shop_log.ldf", "log", _log.Length)],
            "FULL", "SIMPLE", 268_435_456, LogRebuilt: true, new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero),
            "shop.bak", new string('a', 64), new Dictionary<string, string>(), "mcr.microsoft.com/mssql/server:2022-latest");
        _tar = token => new FakeStreamingProcess(new ForwardOnly(TarBytes()), cancellationToken: token);

        _world.WithEngine();
        _world.Runtime.ExecHandler = (_, command) =>
            NoteScript(command) && command.Count >= 3 && command[2] == ImageCatalog.ListingScript
                ? FakeContainerRuntime.Ok(string.Join('\n', new[]
                {
                    _credentialsJson is null ? null : $"SQLBRANCH_POOLCRED|{System.Text.Json.JsonDocument.Parse(_credentialsJson).RootElement.GetProperty("format").GetInt32()}",
                    _catalogJson is null ? null : $"SQLBRANCH_CATALOG|shop|{Convert.ToBase64String(Encoding.UTF8.GetBytes(_catalogJson))}",
                    _versionDirectory is null ? null : $"SQLBRANCH_VMANIFEST|shop|{_versionDirectory}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(_manifest.ToJson()))}",
                    _imageExists ? $"SQLBRANCH_IMAGE|shop|{Convert.ToBase64String(Encoding.UTF8.GetBytes(_manifest.ToJson()))}" : null,
                }.Where(l => l is not null)))
                : FakeContainerRuntime.Ok();
        _world.Runtime.StreamHandler = (_, _, _, token) => _tar(token);
    }

    [Fact]
    public async Task Image_export_during_an_unfinished_upgrade_keeps_working()
    {
        _credentialsJson = Format2Pool.Credentials(UpgradeRecord.Running);
        _world.Host.CredentialsJson = _credentialsJson;
        _catalogJson = Format2Pool.Catalog("shop", Format2Pool.VersionId("shop"));
        var result = await Export(Dest());
        Assert.Equal(2, result.Files);
        await using (var reader = await ImageArchiveReader.OpenAsync(new MemoryStream(await File.ReadAllBytesAsync(Dest())), CancellationToken.None))
        {
            Assert.Equal(_manifest.ToJson(), reader.Head.Manifest.ToJson());
        }
        Assert.Equal(_credentialsJson, _world.Host.CredentialsJson);
    }

    private readonly List<string> _scripts = [];

    private bool NoteScript(IReadOnlyList<string> command)
    {
        _scripts.Add(string.Join(' ', command));
        return true;
    }

    [Fact]
    public async Task Image_export_of_an_unadopted_image_does_not_adopt_it()
    {
        // A format-2 pool, an image with a manifest and no catalog (written by an older client after the upgrade).
        _credentialsJson = Format2Pool.Credentials();
        _world.Host.CredentialsJson = _credentialsJson;
        var result = await Export(Dest());
        Assert.Equal(2, result.Files);
        Assert.DoesNotContain(_scripts, c => c.Contains("base64 -d >", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Dest(string name = "shop.sbimg") => Path.Combine(_folder, name);

    private FoundPool Pool() =>
        new(new PoolLocation(PoolKind.Directory, FakeInitHost.PoolDir), "plain", PoolSource.EngineLabels, _world.Runtime.Containers[ContainerLabels.EngineName]);

    private Task<ImageExportResult> Export(string destination, bool force = false, CancellationToken token = default) =>
        new ImageExporter(_world.Runtime).ExportAsync(Pool(), "shop", destination, force, token);

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>What <c>tar -C data -cf - shop.mdf shop_log.ldf</c> prints in GNU format, with sizes in the headers that a test may falsify.</summary>
    private byte[] TarBytes(long dataHeaderDelta = 0, string? firstName = null, bool dropLog = false)
    {
        using var output = new MemoryStream();
        using (var tar = new TarWriter(output, TarEntryFormat.Gnu, leaveOpen: true))
        {
            var data = dataHeaderDelta == 0 ? _data : _data[..(_data.Length + (int)dataHeaderDelta)];
            tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, firstName ?? "shop.mdf") { DataStream = new MemoryStream(data) });
            if (!dropLog)
            {
                tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "shop_log.ldf") { DataStream = new MemoryStream(_log) });
            }
        }

        // A real tar pads its output to whole records.
        var padded = new byte[(output.Length + 10239) / 10240 * 10240];
        output.ToArray().CopyTo(padded, 0);
        return padded;
    }

    private void TarIs(byte[] bytes, int exitCode = 0, string stdErr = "") =>
        _tar = token => new FakeStreamingProcess(new ForwardOnly(bytes), exitCode, stdErr, token);

    private void TarFailsAtHalf(bool eofInsteadOfError)
    {
        var bytes = TarBytes();
        _tar = token => new FakeStreamingProcess(new ForwardOnly(bytes, endAt: bytes.Length / 2, throwAtEnd: !eofInsteadOfError), exitCode: eofInsteadOfError ? 1 : 0, cancellationToken: token);
    }

    private void AssertNothingBesidesThe(params string[] files) =>
        Assert.Equal(files.Order(StringComparer.Ordinal), Directory.GetFileSystemEntries(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);

    private static async Task<List<(string Name, byte[] Data)>> EntriesAsync(string path)
    {
        await using var input = File.OpenRead(path);
        await using var decompressed = new DecompressionStream(input);
        await using var tar = new TarReader(decompressed);
        var entries = new List<(string, byte[])>();
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            using var copy = new MemoryStream();
            await entry.DataStream!.CopyToAsync(copy);
            entries.Add((entry.Name, copy.ToArray()));
        }

        return entries;
    }

    /// <summary>A stream that reads forward only, like a pipe, and can end or fail at a byte count.</summary>
    private sealed class ForwardOnly(byte[] bytes, long? endAt = null, bool throwAtEnd = false) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var limit = endAt ?? bytes.Length;
            if (_position >= limit && throwAtEnd)
            {
                throw new IOException("The connection to the engine was reset.");
            }

            var n = (int)Math.Min(Math.Min(buffer.Length, 16384), limit - _position);
            bytes.AsSpan((int)_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // --- Export an image ---

    [Fact]
    public async Task Export_writes_an_archive_with_the_manifest_the_files_and_their_checksums_and_leaves_no_partial_file()
    {
        var result = await Export(Dest());

        Assert.Equal(new ImageExportResult("shop", 2, _data.Length + _log.Length, new FileInfo(Dest()).Length), result);
        AssertNothingBesidesThe("shop.sbimg");
        var entries = await EntriesAsync(Dest());
        Assert.Equal(["sqlbranch-archive.json", "data/shop.mdf", "data/shop_log.ldf", "sqlbranch-checksums.json"], entries.Select(e => e.Name));
        Assert.Equal(_data, entries[1].Data);
        Assert.Equal(_log, entries[2].Data);
        var checksums = ArchiveChecksums.Parse(entries[3].Data);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(entries[0].Data)), checksums.ManifestSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_data)), checksums.Files[0].Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_log)), checksums.Files[1].Sha256);

        var command = Assert.Single(_world.Runtime.StreamExecs);
        Assert.Equal(["tar", "-C", "/pool/images/shop/data", "-cf", "-", "--", "shop.mdf", "shop_log.ldf"], command.Command);
        Assert.Equal(StreamDirection.Read, command.Direction);
    }

    [Fact]
    public async Task On_a_format_2_pool_export_reads_the_current_version_of_the_name_from_its_catalog()
    {
        _credentialsJson = Format2Pool.Credentials();
        _world.Host.CredentialsJson = _credentialsJson;
        _catalogJson = Format2Pool.Catalog("shop", Format2Pool.VersionId("shop"));

        var result = await Export(Dest());

        Assert.Equal(2, result.Files);
        Assert.Equal(["tar", "-C", "/pool/images/shop/data", "-cf", "-", "--", "shop.mdf", "shop_log.ldf"], Assert.Single(_world.Runtime.StreamExecs).Command);
    }

    [Fact]
    public async Task Export_streams_the_data_folder_of_the_same_catalog_entry_as_the_manifest()
    {
        var id = Format2Pool.VersionId("shop", "20261203T020000000Z");
        _credentialsJson = Format2Pool.Credentials();
        _world.Host.CredentialsJson = _credentialsJson;
        _versionDirectory = $"versions/{id}";
        _catalogJson = new ImageCatalogFile("shop", id, [new ImageVersionRef(id, _versionDirectory)]).ToJson();

        await Export(Dest());

        Assert.Equal($"/pool/images/shop/versions/{id}/data", Assert.Single(_world.Runtime.StreamExecs).Command[2]);
    }

    [Fact]
    public async Task On_a_format_2_pool_a_name_with_no_current_version_is_image_not_found()
    {
        _credentialsJson = Format2Pool.Credentials();
        _world.Host.CredentialsJson = _credentialsJson;
        _catalogJson = Format2Pool.Catalog("shop", null);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest()));

        Assert.Equal(SqlBranch.Core.Output.ErrorCodes.ImageNotFound, ex.Code);
    }

    [Fact]
    public async Task The_archive_starts_with_the_zstd_magic_bytes_and_reads_back_with_the_reader()
    {
        await Export(Dest());

        var bytes = await File.ReadAllBytesAsync(Dest());
        Assert.Equal([0x28, 0xb5, 0x2f, 0xfd], bytes[..4]);
        await using var reader = await ImageArchiveReader.OpenAsync(new MemoryStream(bytes), CancellationToken.None);
        Assert.Equal(_manifest.ToJson(), reader.Head.Manifest.ToJson());
    }

    [Fact]
    public async Task The_archive_holds_no_branch_name_and_no_engine_password()
    {
        _world.Runtime.Calls.Clear();

        await Export(Dest());

        // The whole decompressed tar, header bytes included.
        await using var input = File.OpenRead(Dest());
        await using var decompressed = new DecompressionStream(input);
        using var all = new MemoryStream();
        await decompressed.CopyToAsync(all);
        var text = Encoding.Latin1.GetString(all.ToArray());
        Assert.DoesNotContain("pr-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pr-2", text, StringComparison.Ordinal);
        Assert.DoesNotContain(CliWorld.StoredPassword, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_is_shorter_in_the_engine_than_in_the_manifest_fails_the_export_and_writes_nothing()
    {
        TarIs(TarBytes(dataHeaderDelta: -4096));

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest()));

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.Contains("shop.mdf", ex.Message, StringComparison.Ordinal);
        AssertNothingBesidesThe();
    }

    [Fact]
    public async Task An_entry_that_is_not_the_next_file_of_the_manifest_fails_the_export()
    {
        TarIs(TarBytes(firstName: "other.mdf"));
        var ex1 = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest()));

        TarIs(TarBytes(dropLog: true));
        var ex2 = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest()));

        Assert.All([ex1, ex2], ex => Assert.Equal(ErrorCodes.OperationFailed, ex.Code));
        AssertNothingBesidesThe();
    }

    [Fact]
    public async Task An_image_that_does_not_exist_is_image_not_found_and_nothing_is_written()
    {
        _imageExists = false;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest("nope.sbimg")));

        Assert.Equal(ErrorCodes.ImageNotFound, ex.Code);
        AssertNothingBesidesThe();
        Assert.Empty(_world.Runtime.StreamExecs);
    }

    // --- Export does not overwrite without force ---

    [Fact]
    public async Task An_existing_file_without_force_is_destination_exists_before_any_image_file_is_read_and_stays_as_it_was()
    {
        await File.WriteAllTextAsync(Dest(), "old archive");
        var before = Sha(Dest());

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest()));

        Assert.Equal(ErrorCodes.DestinationExists, ex.Code);
        Assert.Contains("--force", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, Sha(Dest()));
        Assert.Empty(_world.Runtime.StreamExecs);
        AssertNothingBesidesThe("shop.sbimg");
    }

    [Fact]
    public async Task An_existing_file_with_force_is_replaced_by_a_complete_archive()
    {
        await File.WriteAllTextAsync(Dest(), "old archive");

        await Export(Dest(), force: true);

        await using var reader = await ImageArchiveReader.OpenAsync(File.OpenRead(Dest()), CancellationToken.None);
        Assert.Equal("shop", reader.Head.Manifest.Name);
        AssertNothingBesidesThe("shop.sbimg");
    }

    [Fact]
    public async Task A_file_created_by_someone_else_during_the_export_is_destination_exists_and_is_not_replaced()
    {
        _tar = token =>
        {
            File.WriteAllText(Dest(), "someone else's archive");
            return new FakeStreamingProcess(new ForwardOnly(TarBytes()), cancellationToken: token);
        };
        var theirs = "someone else's archive";

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest()));

        Assert.Equal(ErrorCodes.DestinationExists, ex.Code);
        Assert.Equal(theirs, await File.ReadAllTextAsync(Dest()));
        AssertNothingBesidesThe("shop.sbimg");
    }

    // --- Failed exports leave the destination as it was ---

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stream_that_breaks_or_ends_at_half_leaves_no_file_and_no_partial_file(bool eofInsteadOfError)
    {
        TarFailsAtHalf(eofInsteadOfError);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest()));

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        AssertNothingBesidesThe();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failure_at_half_while_replacing_leaves_the_old_file_with_its_sha256(bool eofInsteadOfError)
    {
        await File.WriteAllTextAsync(Dest(), "old archive");
        var before = Sha(Dest());
        TarFailsAtHalf(eofInsteadOfError);

        await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest(), force: true));

        Assert.Equal(before, Sha(Dest()));
        AssertNothingBesidesThe("shop.sbimg");
    }

    [Fact]
    public async Task A_non_zero_tar_exit_after_a_complete_stream_fails_the_export_and_writes_nothing()
    {
        // The image was removed while it was read: tar delivered what it could and exited 2.
        TarIs(TarBytes(), exitCode: 2, stdErr: "tar: shop_log.ldf: Cannot open: No such file or directory");
        await File.WriteAllTextAsync(Dest("old.sbimg"), "old");

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest(), force: true));
        var exOther = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest("shop2.sbimg")));

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.Equal(ErrorCodes.OperationFailed, exOther.Code);
        Assert.Contains("exited with code 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("No such file", ex.Message, StringComparison.Ordinal);
        AssertNothingBesidesThe("old.sbimg");
    }

    [Fact]
    public async Task Cancelling_deletes_the_partial_file_keeps_the_old_one_and_waits_for_the_client_to_exit()
    {
        await File.WriteAllTextAsync(Dest(), "old archive");
        var before = Sha(Dest());
        using var cancel = new CancellationTokenSource();
        FakeStreamingProcess? process = null;
        var started = new TaskCompletionSource();
        var tar = TarBytes();
        _tar = token =>
        {
            // Delivers a little, then waits for the token as a slow engine would.
            process = new FakeStreamingProcess(new StallingStream(tar, 100_000, started, token), cancellationToken: token)
            {
                ExitDelay = TimeSpan.FromMilliseconds(150),
            };
            return process;
        };

        var running = Export(Dest(), force: true, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.NotNull(process);
        Assert.Equal(["killed", "exited"], process.Events);
        Assert.Equal(before, Sha(Dest()));
        AssertNothingBesidesThe("shop.sbimg");
    }

    [Fact]
    public async Task Cancelling_after_the_disk_flush_preserves_an_existing_destination()
    {
        await File.WriteAllTextAsync(Dest(), "existing archive");
        using var cancel = new CancellationTokenSource();
        var exporter = new ImageExporter(_world.Runtime) { AfterFlushToDisk = cancel.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(Pool(), "shop", Dest(), force: true, cancel.Token));

        Assert.Equal("existing archive", await File.ReadAllTextAsync(Dest()));
        AssertNothingBesidesThe("shop.sbimg");
    }

    [Fact]
    public async Task Cancelling_a_first_export_leaves_no_file_at_all()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var tar = TarBytes();
        _tar = token => new FakeStreamingProcess(new StallingStream(tar, 50_000, started, token), cancellationToken: token);

        var running = Export(Dest(), token: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        AssertNothingBesidesThe();
    }

    [Fact]
    public async Task Cancelling_while_the_killed_client_ends_the_stream_early_is_a_cancellation_not_a_damaged_stream()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var tar = TarBytes();
        _tar = token => new FakeStreamingProcess(new StallingStream(tar, 50_000, started, token, endOnCancel: true), cancellationToken: token);

        var running = Export(Dest(), token: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => running);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        AssertNothingBesidesThe();
    }

    /// <summary>Delivers the first bytes, tells the test it is stuck, then blocks until the token ends.</summary>
    private sealed class StallingStream(byte[] bytes, int deliver, TaskCompletionSource started, CancellationToken token, bool endOnCancel = false) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < deliver)
            {
                var n = Math.Min(Math.Min(buffer.Length, 16384), deliver - _position);
                bytes.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (endOnCancel)
            {
                // A killed `docker exec` client: the pipe just ends.
            }

            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // --- Through the command ---

    private (int Exit, JsonElement Json, string Out, string Err) Command(params string[] args)
    {
        var services = _world.Services() with { WorkingDirectory = _folder, DirectoryExists = Directory.Exists };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.CreateDefault(_world.Redactor, services).Run(["image", "export", .. args, "--format", "json"], stdout, stderr);
        return (exit, JsonDocument.Parse(stdout.ToString()).RootElement.Clone(), stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task The_command_reports_the_image_the_destination_and_both_sizes_in_json()
    {
        var run = Command("shop", "--to", "out.sbimg");

        Assert.Equal(0, run.Exit);
        Assert.Equal("image export", run.Json.GetProperty("command").GetString());
        var export = run.Json.GetProperty("export");
        Assert.Equal("shop", export.GetProperty("image").GetString());
        Assert.Equal("out.sbimg", export.GetProperty("to").GetString());
        Assert.Equal(2, export.GetProperty("files").GetInt32());
        Assert.Equal(_data.Length + _log.Length, export.GetProperty("imageBytes").GetInt64());
        Assert.Equal(new FileInfo(Dest("out.sbimg")).Length, export.GetProperty("archiveBytes").GetInt64());
        Assert.False(export.TryGetProperty("cloneMode", out _));
        await Task.CompletedTask;
    }

    [Fact]
    public void The_command_prints_a_result_line_in_text_and_exits_1_with_the_code_for_an_existing_file()
    {
        var services = _world.Services() with { WorkingDirectory = _folder, DirectoryExists = Directory.Exists };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.CreateDefault(_world.Redactor, services).Run(["image", "export", "shop", "--to", "t.sbimg"], stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Contains("Image shop is exported to t.sbimg.", stdout.ToString(), StringComparison.Ordinal);

        var again = Command("shop", "--to", "t.sbimg");
        Assert.Equal(1, again.Exit);
        Assert.Equal("destination_exists", again.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("--force", again.Json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal(0, Command("shop", "--to", "t.sbimg", "--force").Exit);
    }

    [Fact]
    public void The_command_exits_1_image_not_found_for_an_unknown_image()
    {
        _imageExists = false;

        var run = Command("shop", "--to", "n.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_not_found", run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(Dest("n.sbimg")));
    }
    [Fact]
    public async Task Use_an_image_with_an_unreadable_catalog_refuses_export_before_creating_a_file()
    {
        _credentialsJson = Format2Pool.Credentials();
        _world.Host.CredentialsJson = _credentialsJson;
        _catalogJson = "{not json";
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => Export(Dest()));
        Assert.Equal(ErrorCodes.OperationFailed, error.Code);
        Assert.Contains("sqlbranch image rm shop", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder));
        Assert.Empty(_world.Runtime.Copies);
    }

    [Fact]
    public async Task Unreadable_credentials_without_format_2_content_keep_the_legacy_export()
    {
        _world.Host.CredentialsJson = "{not json";
        var result = await Export(Dest());
        Assert.Equal("shop", result.Image);
        Assert.True(File.Exists(Dest()));
    }

}
