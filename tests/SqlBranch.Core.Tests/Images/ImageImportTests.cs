using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// <c>image import</c> on a fake runtime: the archive is a byte array, the engine's <c>tar</c> is a sink that records what the client
/// sends, and the pool is a few paths the fake shell keeps. Nothing here reaches the real docker.
/// </summary>
public sealed partial class ImageImportTests : IDisposable
{
    private const long Gib = 1L << 30;
    private const string Id = "abc123def456";

    private readonly CliWorld _world = new();
    private readonly byte[] _data = new byte[300_000];
    private readonly byte[] _log = new byte[70_001];
    private readonly ImageManifest _manifest;

    /// <summary>Everything that happened, in order: shell scripts and the streaming exec.</summary>
    private readonly List<string> _timeline = [];

    /// <summary>Paths that exist in the pool, as the fake shell knows them.</summary>
    private readonly HashSet<string> _paths = [];

    private long _free = 100 * Gib;
    private Func<string, bool>? _failScript;
    private string[]? _statSizes;

    /// <summary>The engine's tar is running inside the container: set when the exec starts, cleared when it ends.</summary>
    private bool _writerRunning;

    /// <summary>Killing the docker client does not stop the tar inside the container; only the end-check script does.</summary>
    private bool _writerSurvivesClient;

    private string _ensureOutput = "SQLBRANCH_TAR=ended";
    private int _ensureExit;
    private TimeSpan _exitDelay = TimeSpan.Zero;
    private RecordingSink? _sink;
    private FakeStreamingProcess? _process;
    private string _tarStdErr = string.Empty;
    private int _tarExit;
    private long? _sinkFailsAfter;

    /// <summary>What was true at each removal: the client process had exited, and the in-container writer was not running.</summary>
    private readonly List<(string Script, bool ClientExited, bool WriterRunning)> _removals = [];

    public ImageImportTests()
    {
        new Random(7).NextBytes(_data);
        new Random(8).NextBytes(_log);
        _manifest = ArchiveCrafter.Manifest("shop",
            new ImageFile("Shop", "shop.mdf", "data", _data.Length), new ImageFile("Shop_log", "shop_log.ldf", "log", _log.Length));

        _world.WithEngine();
        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (script == StatusReporter.ListingScript || script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal))
            {
                return inner(container, command);
            }

            if (script.Contains("SQLBRANCH_TAR=", StringComparison.Ordinal))
            {
                _timeline.Add("ensure-ended");
                _writerRunning = false;
                return _ensureExit == 0 ? FakeContainerRuntime.Ok(_ensureOutput) : FakeContainerRuntime.Fail(_ensureExit, "stuck");
            }

            _timeline.Add("sh: " + script);
            if (script.Contains("rm -rf", StringComparison.Ordinal))
            {
                _removals.Add((script, _process?.Exited ?? true, _writerRunning));
            }

            if (_failScript?.Invoke(script) == true)
            {
                return FakeContainerRuntime.Fail(1, "boom");
            }

            if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok($"SQLBRANCH_FREE={_free}");
            }

            if (script.Contains("SQLBRANCH_EXISTS=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok($"SQLBRANCH_EXISTS={(_paths.Contains("/pool/images/shop2") && _existsCheckSeesIt ? "yes" : "no")}");
            }

            if (script.Contains("stat -c %s", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok(string.Join('\n', _statSizes ?? [Text(_data.Length), Text(_log.Length)]));
            }

            foreach (var match in MkdirImage().Matches(script).Cast<Match>())
            {
                var path = match.Groups["p"].Value;
                if (!_paths.Add(path))
                {
                    return FakeContainerRuntime.Fail(1, "mkdir: cannot create directory: File exists");
                }
            }

            foreach (var match in RmRf().Matches(script).Cast<Match>())
            {
                var removed = match.Groups["p"].Value;
                _paths.RemoveWhere(path => path == removed || path.StartsWith(removed + "/", StringComparison.Ordinal));
            }

            return FakeContainerRuntime.Ok();
        };

        _world.Runtime.StreamHandler = (_, command, direction, token) =>
        {
            Assert.Equal(StreamDirection.Write, direction);
            _timeline.Add("exec-stream");
            _writerRunning = true;
            _sink = new RecordingSink { FailAfter = _sinkFailsAfter };
            _process = new FakeStreamingProcess(_sink, _tarExit, _tarStdErr, token)
            {
                ExitDelay = _exitDelay,
                OnInputClosed = () => _writerRunning = _writerSurvivesClient,
            };
            return _process;
        };
    }

    private bool _existsCheckSeesIt = true;

    [GeneratedRegex(@"mkdir '(?<p>/pool/images/[^']+)'")]
    private static partial Regex MkdirImage();

    [GeneratedRegex(@"rm -rf '(?<p>/pool/(?:images|build)/[^']+)'")]
    private static partial Regex RmRf();

    public void Dispose() => _sink?.Dispose();

    private static string Text(int number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<FoundPool> PoolAsync() =>
        await new PoolFinder(_world.Runtime, ManagedPaths.Default, _ => true).RequireAsync(
            _world.Services().LoadSettings(null), "/home/u/.sqlbranch/pool", CancellationToken.None);

    private ImageImporter Importer(Func<string, CancellationToken, Task>? afterStep = null, Action<string>? progress = null) =>
        new(_world.Runtime, _world.Connector, _world.Redactor, newId: () => Id) { AfterStep = afterStep, Progress = progress };

    private async Task<byte[]> ValidAsync(ImageManifest? manifest = null) =>
        await ArchiveCrafter.ValidAsync(manifest ?? _manifest, _data, _log);

    private async Task<ImageImportResult> ImportAsync(byte[] archive, string target = "shop2", ImageImporter? importer = null, CancellationToken token = default)
    {
        await using var reader = await ImageArchiveReader.OpenAsync(new MemoryStream(archive), CancellationToken.None);
        ArchiveManifestChecks.Check(reader.Head.Manifest, target);
        return await (importer ?? Importer()).ImportAsync(await PoolAsync(), 14330, reader, target, token);
    }

    private async Task<SqlBranchException> FailsAsync(byte[] archive, string target = "shop2", ImageImporter? importer = null) =>
        await Assert.ThrowsAsync<SqlBranchException>(() => ImportAsync(archive, target, importer));

    private bool Ran(string marker) => _timeline.Any(l => l.Contains(marker, StringComparison.Ordinal));

    private static List<(string Name, byte[] Data, UnixFileMode Mode)> ReadSent(byte[] sent)
    {
        using var tar = new TarReader(new MemoryStream(sent));
        var entries = new List<(string, byte[], UnixFileMode)>();
        while (tar.GetNextEntry() is { } entry)
        {
            using var copy = new MemoryStream();
            entry.DataStream!.CopyTo(copy);
            entries.Add((entry.Name, copy.ToArray(), entry.Mode));
        }

        return entries;
    }

    private void AssertNothingWasWritten()
    {
        Assert.DoesNotContain(_timeline, l => l == "exec-stream");
        Assert.DoesNotContain(_timeline, l => l.Contains("mkdir", StringComparison.Ordinal));
        Assert.Empty(_removals);
    }

    // ---- Import an image ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_writes_the_listed_files_through_a_client_built_tar_and_seals_them_under_the_new_name(bool legacyGzip)
    {
        var archive = legacyGzip
            ? ArchiveCrafter.CraftFull(_manifest,
                [new CraftEntry("data/shop.mdf", _data), new CraftEntry("data/shop_log.ldf", _log)],
                ArchiveCrafter.Checksums(_manifest, _data, _log))
            : await ValidAsync();
        var result = await ImportAsync(archive);

        Assert.Equal(_manifest.ToJson().Replace("\"shop\",", "\"shop2\",", StringComparison.Ordinal), result.Manifest.ToJson());
        var call = Assert.Single(_world.Runtime.StreamExecs);
        Assert.Equal(StreamDirection.Write, call.Direction);
        Assert.Equal(["sh", "-c"], call.Command.Take(2));
        Assert.Contains("exec tar -C \"$2\" -xf - --no-same-owner --no-same-permissions", call.Command[2], StringComparison.Ordinal);
        Assert.Equal("/pool/build/abc123def456/data", call.Command[^1]);

        var sent = ReadSent(_sink!.Bytes);
        Assert.Equal(["shop.mdf", "shop_log.ldf"], sent.Select(e => e.Name));
        Assert.Equal(_data, sent[0].Data);
        Assert.Equal(_log, sent[1].Data);
        Assert.All(sent, e => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, e.Mode));
    }

    [Fact]
    public async Task The_manifest_is_written_last_after_the_files_are_moved_in_and_the_build_folder_goes_afterwards()
    {
        await ImportAsync(await ValidAsync());

        var seal = _timeline.FindIndex(l => l.Contains("mv '/pool/build/abc123def456/data/shop.mdf' '/pool/images/shop2/data/shop.mdf'", StringComparison.Ordinal));
        var manifest = _timeline.FindIndex(l => l.Contains("base64 -d", StringComparison.Ordinal));
        var removed = _timeline.FindIndex(l => l.Contains("rm -rf '/pool/build/abc123def456'", StringComparison.Ordinal));
        Assert.True(seal >= 0 && seal < manifest && manifest < removed);
        var script = _timeline[seal];
        Assert.True(script.IndexOf("chmod 444 '/pool/images/shop2/data/shop.mdf'", StringComparison.Ordinal) < script.IndexOf("mv '/pool/build/abc123def456/data/shop_log.ldf'", StringComparison.Ordinal));
        Assert.Contains("chmod 444 '/pool/images/shop2/data/shop_log.ldf'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(_removals, r => r.Script.Contains("/pool/images/", StringComparison.Ordinal));
        Assert.Contains("shop2", Encoding.UTF8.GetString(Convert.FromBase64String(ManifestB64().Match(_timeline[manifest]).Groups["b64"].Value)), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"printf %s '(?<b64>[^']+)'")]
    private static partial Regex ManifestB64();

    /// <summary>The manifest of <see cref="_manifest"/> as JSON with fields this version does not know, at the top and in a file entry.</summary>
    private ImageManifest WithUnknownFields()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(_manifest.ToJson())!.AsObject();
        node["bacpacFile"] = "shop.bacpac";
        node["futureObject"] = System.Text.Json.Nodes.JsonNode.Parse("{\"a\":[1,2,{\"b\":null}]}");
        node["files"]![0]!["checksumHint"] = "x1";
        return ImageManifest.FromJson(node.ToJsonString());
    }

    [Fact]
    public async Task An_import_keeps_manifest_fields_it_does_not_know_and_changes_only_the_name()
    {
        var source = WithUnknownFields();
        Assert.NotNull(source.UnknownFields);

        await ImportAsync(await ValidAsync(source));

        var written = _timeline.Single(l => l.Contains("base64 -d", StringComparison.Ordinal));
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(ManifestB64().Match(written).Groups["b64"].Value));
        var expected = System.Text.Json.Nodes.JsonNode.Parse(source.ToJson())!.AsObject();
        expected["name"] = "shop2";
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, System.Text.Json.Nodes.JsonNode.Parse(json)));
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        Assert.Equal("shop.bacpac", (string?)parsed["bacpacFile"]);
        Assert.Equal("x1", (string?)parsed["files"]![0]!["checksumHint"]);
        Assert.Equal(2, (int?)parsed["futureObject"]!["a"]![1]);
    }

    [Fact]
    public void Unknown_fields_of_script_entries_in_scripts_and_post_scripts_are_kept_on_a_round_trip()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(_manifest.ToJson())!.AsObject();
        node["scripts"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"name\":\"a.sql\",\"sha256\":\"aa\",\"batchCount\":3}]");
        node["postScripts"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"name\":\"b.sql\",\"sha256\":\"bb\",\"future\":{\"x\":[1]}}]");

        var again = System.Text.Json.Nodes.JsonNode.Parse(ImageManifest.FromJson(node.ToJsonString()).ToJson())!;

        Assert.Equal(3, (int?)again["scripts"]![0]!["batchCount"]);
        Assert.Equal(1, (int?)again["postScripts"]![0]!["future"]!["x"]![0]);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(node["scripts"], again["scripts"]));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(node["postScripts"], again["postScripts"]));
    }

    [Fact]
    public async Task A_manifest_with_unknown_fields_survives_export_then_import_then_export_again()
    {
        var source = WithUnknownFields();
        var firstArchive = await ValidAsync(source);

        // Import reads the head, keeps the original fields, and the imported image is exported again with them.
        await using var reader = await ImageArchiveReader.OpenAsync(new MemoryStream(firstArchive), CancellationToken.None);
        var imported = reader.Head.Manifest with { Name = "shop2" };
        var secondHead = ArchiveHeadJson.Write(imported);
        var again = ArchiveHeadJson.Read(secondHead, string.Empty).Manifest;

        var expected = System.Text.Json.Nodes.JsonNode.Parse(source.ToJson())!.AsObject();
        expected["name"] = "shop2";
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, System.Text.Json.Nodes.JsonNode.Parse(again.ToJson())));
    }

    [Fact]
    public void Older_manifests_without_bacpac_fields_read_with_null_provenance()
    {
        var legacy = JsonNode.Parse(_manifest.ToJson())!.AsObject();
        foreach (var key in new[] { "bacpacFile", "bacpacSha256", "bacpacUrl" })
        {
            Assert.True(legacy.Remove(key));
        }

        var read = ImageManifest.FromJson(legacy.ToJsonString());
        Assert.Equal("backup", read.SourceType);
        Assert.Equal(_manifest.BackupFile, read.BackupFile);
        Assert.Null(read.BacpacFile);
        Assert.Null(read.BacpacSha256);
        Assert.Null(read.BacpacUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.blob.core.windows.net/db/shop.bacpac")]
    public async Task Bacpac_provenance_and_nested_extensions_survive_archive_format_one(string? url)
    {
        var manifest = WithUnknownFields() with
        {
            SourceType = "bacpac",
            SourceDatabase = "shop",
            BackupFile = null,
            BackupSha256 = null,
            BacpacSha256 = new string('b', 64),
            BacpacUrl = url,
        };

        var imported = await ImportAsync(await ValidAsync(manifest));
        var written = _timeline.Single(l => l.Contains("base64 -d", StringComparison.Ordinal));
        var stored = ImageManifest.FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(
            ManifestB64().Match(written).Groups["b64"].Value)));
        Assert.Equal(imported.Manifest.Name, stored.Name);

        await using var exported = await ImageArchiveReader.OpenAsync(new MemoryStream(await ValidAsync(stored)), CancellationToken.None);
        Assert.Equal(1, ImageArchive.CurrentFormat);
        var expected = JsonNode.Parse(manifest.ToJson())!;
        expected["name"] = "shop2";
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(exported.Head.Manifest.ToJson())));
        Assert.Equal("shop.bacpac", exported.Head.Manifest.BacpacFile);
        Assert.Equal(url, exported.Head.Manifest.BacpacUrl);
    }

    [Fact]
    public async Task The_image_keeps_its_own_name_when_none_is_given_and_a_valid_one_is_in_the_archive()
    {
        _paths.Clear();

        var result = await ImportAsync(await ValidAsync(), target: "shop");

        Assert.Equal("shop", result.Manifest.Name);
        Assert.Contains("/pool/images/shop/data/shop.mdf", string.Join('\n', _timeline), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_steps_run_in_the_order_of_the_design_and_progress_is_reported()
    {
        var seen = new List<string>();

        await ImportAsync(await ValidAsync(), importer: Importer((step, _) =>
        {
            seen.Add(step);
            return Task.CompletedTask;
        }));

        Assert.Equal(ImageImporter.StepNames, seen);
        Assert.Equal(["check-name", "check-version", "check-space", "build-folder", "write", "verify", "seal", "manifest"], seen);
    }

    [Fact]
    public async Task The_import_holds_the_image_lock_and_its_own_build_lock_and_gives_the_session_back()
    {
        await ImportAsync(await ValidAsync());

        Assert.Equal(["sqlbranch:image:shop2", "sqlbranch:build:" + Id], _world.Connector.TakenLocks);
        Assert.Equal(_world.Connector.SessionsOpened, _world.Connector.SessionsDisposed);
    }

    // ---- Import accepts only the listed files ----

    private CraftEntry Data(string name, byte[]? bytes = null, TarEntryType type = TarEntryType.RegularFile) =>
        new(name, bytes ?? (name.EndsWith("ldf", StringComparison.Ordinal) ? _log : _data), type);

    private byte[] WithEntries(IEnumerable<CraftEntry> data, bool checksums = true) =>
        ArchiveCrafter.CraftFull(_manifest, data, checksums ? ArchiveCrafter.Checksums(_manifest, _data, _log) : null);

    [Fact]
    public async Task A_path_traversal_entry_is_archive_invalid_and_none_of_its_bytes_reach_the_engine()
    {
        var evil = new byte[5000];
        Array.Fill(evil, (byte)0xAB);
        var archive = WithEntries([Data("data/shop.mdf"), Data("data/../../branches/pr-1/x.mdf", evil), Data("data/shop_log.ldf")]);

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        Assert.DoesNotContain("0xAB", ex.Message, StringComparison.Ordinal);
        Assert.False(ContainsRun(_sink!.Bytes, 0xAB, 64));
        Assert.DoesNotContain(_timeline, l => l.Contains("branches", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task A_symbolic_link_entry_is_archive_invalid_and_nothing_is_sent()
    {
        var archive = WithEntries([Data("data/shop.mdf", type: TarEntryType.SymbolicLink), Data("data/shop_log.ldf")]);

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_sink!.Bytes);
        AssertCleanedUp();
    }

    [Fact]
    public async Task A_hard_link_entry_is_archive_invalid()
    {
        var archive = WithEntries([Data("data/shop.mdf"), Data("data/shop_log.ldf", type: TarEntryType.HardLink)]);

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        AssertCleanedUp();
    }

    [Fact]
    public async Task A_repeated_entry_is_archive_invalid_before_its_bytes_are_sent()
    {
        var repeated = new byte[5000];
        Array.Fill(repeated, (byte)0xCD);
        var archive = WithEntries([Data("data/shop.mdf"), Data("data/shop.mdf", repeated), Data("data/shop_log.ldf")]);

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
        Assert.False(ContainsRun(_sink!.Bytes, 0xCD, 64));
        AssertCleanedUp();
    }

    [Fact]
    public async Task An_unlisted_entry_and_an_entry_after_the_files_are_archive_invalid()
    {
        var unlisted = WithEntries([Data("data/shop.mdf"), Data("data/extra.mdf", [1, 2, 3]), Data("data/shop_log.ldf")]);
        var ex = await FailsAsync(unlisted);
        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        Assert.Contains("does not list", ex.Message, StringComparison.Ordinal);

        var trailing = WithEntries([Data("data/shop.mdf"), Data("data/shop_log.ldf"), Data("data/extra.mdf", [1])], checksums: true);
        var ex2 = await FailsAsync(trailing);
        Assert.Equal(ErrorCodes.ArchiveInvalid, ex2.Code);
    }

    [Fact]
    public async Task An_entry_out_of_the_manifests_order_is_archive_invalid()
    {
        var archive = WithEntries([Data("data/shop_log.ldf"), Data("data/shop.mdf")]);

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        Assert.Contains("order", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_sink!.Bytes);
    }

    [Fact]
    public async Task An_entry_that_is_not_under_data_or_has_a_different_name_is_archive_invalid()
    {
        foreach (var name in new[] { "shop.mdf", "/data/shop.mdf", "data/SHOP.MDF", "data\\shop.mdf", "data/sub/shop.mdf" })
        {
            var ex = await FailsAsync(WithEntries([Data(name), Data("data/shop_log.ldf")]));
            Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        }
    }

    [Fact]
    public async Task A_header_size_larger_than_the_manifests_sends_not_one_byte_of_that_entry()
    {
        var big = new byte[_log.Length + 4096];
        Array.Fill(big, (byte)0xEF);
        var archive = WithEntries([Data("data/shop.mdf"), Data("data/shop_log.ldf", big)]);

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        Assert.Contains("header", ex.Message, StringComparison.Ordinal);
        Assert.False(ContainsRun(_sink!.Bytes, 0xEF, 64));
        AssertCleanedUp();

        // As the first entry, nothing at all is sent.
        _timeline.Clear();
        _removals.Clear();
        _paths.Clear();
        var first = WithEntries([Data("data/shop.mdf", new byte[_data.Length * 2]), Data("data/shop_log.ldf")]);
        var ex2 = await FailsAsync(first);
        Assert.Equal(ErrorCodes.ArchiveInvalid, ex2.Code);
        Assert.Empty(_sink!.Bytes);
    }

    [Fact]
    public async Task A_header_size_smaller_than_the_manifests_is_archive_invalid_too()
    {
        var archive = WithEntries([Data("data/shop.mdf", _data[..^1]), Data("data/shop_log.ldf")]);

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        Assert.Empty(_sink!.Bytes);
    }

    private static bool ContainsRun(byte[] bytes, byte value, int length)
    {
        var run = 0;
        foreach (var b in bytes)
        {
            run = b == value ? run + 1 : 0;
            if (run >= length)
            {
                return true;
            }
        }

        return false;
    }

    private void AssertCleanedUp(bool tarWasStillRunning = true)
    {
        // Once the tar has exited 0 the end-check is skipped; a failure before that runs it.
        Assert.Equal(tarWasStillRunning, _timeline.Contains("ensure-ended"));
        Assert.Equal(["/pool/build/abc123def456"], _removals.Select(r => RmRf().Match(r.Script).Groups["p"].Value));
        Assert.DoesNotContain(_paths, p => p.StartsWith("/pool/images/", StringComparison.Ordinal));
        Assert.Equal(_world.Connector.SessionsOpened, _world.Connector.SessionsDisposed);
    }

    // ---- Import refuses images from a newer SQL Server ----

    [Fact]
    public async Task An_image_from_a_newer_major_version_is_version_mismatch_and_the_pool_is_unchanged()
    {
        _world.Connector.Version = "16.0.4135.4|Developer Edition (64-bit)";
        var newer = (_manifest with { EngineVersion = "17.0.1000.7", EngineImage = "mcr.microsoft.com/mssql/server:2025-latest" });

        var ex = await FailsAsync(await ValidAsync(newer));

        Assert.Equal(ErrorCodes.VersionMismatch, ex.Code);
        Assert.Contains("17.0.1000.7", ex.Message, StringComparison.Ordinal);
        Assert.Contains("16.0.4135.4", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mcr.microsoft.com/mssql/server:2025-latest", ex.Message, StringComparison.Ordinal);
        AssertNothingWasWritten();
    }

    [Fact]
    public async Task A_newer_build_of_the_same_major_version_warns_naming_both_versions_and_goes_on()
    {
        _world.Connector.Version = "16.0.4100.1|Developer Edition (64-bit)";
        var newer = _manifest with { EngineVersion = "16.0.4135.4" };

        var result = await ImportAsync(await ValidAsync(newer));

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("16.0.4135.4", warning, StringComparison.Ordinal);
        Assert.Contains("16.0.4100.1", warning, StringComparison.Ordinal);
        Assert.Equal("shop2", result.Manifest.Name);
    }

    [Fact]
    public async Task An_image_from_an_older_or_the_same_version_imports_without_a_warning()
    {
        _world.Connector.Version = "17.0.1000.7|Developer Edition (64-bit)";

        var older = await ImportAsync(await ValidAsync());
        Assert.Empty(older.Warnings);
    }

    // ---- Import checks every file's SHA-256 ----

    [Fact]
    public async Task A_corrupted_byte_is_archive_corrupt_naming_the_file_and_leaves_no_image()
    {
        var damaged = (byte[])_data.Clone();
        damaged[1234] ^= 0xFF;
        var archive = WithEntries([Data("data/shop.mdf", damaged), Data("data/shop_log.ldf")]);

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.ArchiveCorrupt, ex.Code);
        Assert.Contains("shop.mdf", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_paths, p => p.StartsWith("/pool/images/", StringComparison.Ordinal));
        AssertCleanedUp(tarWasStillRunning: false);
    }

    [Fact]
    public async Task A_missing_checksum_list_is_archive_corrupt()
    {
        var ex = await FailsAsync(WithEntries([Data("data/shop.mdf"), Data("data/shop_log.ldf")], checksums: false));

        Assert.Equal(ErrorCodes.ArchiveCorrupt, ex.Code);
        Assert.Contains(ImageArchive.ChecksumsEntry, ex.Message, StringComparison.Ordinal);
        AssertCleanedUp(tarWasStillRunning: false);
    }

    [Fact]
    public async Task A_checksum_list_without_one_file_or_for_another_manifest_is_archive_corrupt()
    {
        var other = _manifest with { SourceDatabase = "Other" };
        var wrongHead = ArchiveCrafter.CraftFull(_manifest, [Data("data/shop.mdf"), Data("data/shop_log.ldf")], ArchiveCrafter.Checksums(other, _data, _log));
        var ex = await FailsAsync(wrongHead);
        Assert.Equal(ErrorCodes.ArchiveCorrupt, ex.Code);

        var oneFile = ArchiveCrafter.Checksums(_manifest, _data, _log);
        var json = Encoding.UTF8.GetString(oneFile.Data);
        var parsed = ArchiveChecksums.Parse(oneFile.Data);
        var partial = new CraftEntry(ImageArchive.ChecksumsEntry, Encoding.UTF8.GetBytes((parsed with { Files = [parsed.Files[0]] }).ToJson()));
        Assert.NotEmpty(json);
        var ex2 = await FailsAsync(ArchiveCrafter.CraftFull(_manifest, [Data("data/shop.mdf"), Data("data/shop_log.ldf")], partial));
        Assert.Equal(ErrorCodes.ArchiveCorrupt, ex2.Code);
        Assert.Contains("shop_log.ldf", ex2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_archive_is_archive_corrupt_at_every_cut_and_leaves_no_image()
    {
        var archive = await ValidAsync();

        foreach (var cut in new[] { archive.Length * 3 / 4, archive.Length / 2, archive.Length / 8 })
        {
            _timeline.Clear();
            _removals.Clear();
            _paths.Clear();
            var ex = await FailsAsync(archive[..cut]);

            Assert.Equal(ErrorCodes.ArchiveCorrupt, ex.Code);
            Assert.DoesNotContain(_paths, p => p.StartsWith("/pool/images/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_complete_tar_without_the_zstd_frame_ending_is_archive_corrupt_and_leaves_no_image()
    {
        using var gzip = new GZipStream(
            new MemoryStream(WithEntries([Data("data/shop.mdf"), Data("data/shop_log.ldf")])),
            CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] unfinished;
        await using (var zstd = new CompressionStream(output, level: 1, leaveOpen: true))
        {
            await gzip.CopyToAsync(zstd);
            // Flush the entire tar, including its end marker, before disposal completes the zstd frame.
            await zstd.FlushAsync();
            unfinished = output.ToArray();
        }

        var ex = await FailsAsync(unfinished);

        Assert.Equal(ErrorCodes.ArchiveCorrupt, ex.Code);
        AssertCleanedUp(tarWasStillRunning: false);
    }

    [Fact]
    public async Task A_file_with_another_size_in_the_pool_than_the_manifest_is_archive_corrupt()
    {
        _statSizes = [Text(_data.Length), Text(_log.Length - 1)];

        var ex = await FailsAsync(await ValidAsync());

        Assert.Equal(ErrorCodes.ArchiveCorrupt, ex.Code);
        Assert.Contains("shop_log.ldf", ex.Message, StringComparison.Ordinal);
        AssertCleanedUp(tarWasStillRunning: false);
    }

    // ---- Import checks space first ----

    [Fact]
    public async Task Too_little_free_space_is_pool_full_naming_both_sizes_before_anything_is_written()
    {
        var big = _manifest with { Files = [new ImageFile("Shop", "shop.mdf", "data", 50 * Gib), new ImageFile("Shop_log", "shop_log.ldf", "log", 0)] };
        _free = 40 * Gib;
        var archive = ArchiveCrafter.CraftFull(big, [], null);

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.PoolFull, ex.Code);
        Assert.Contains("50 GiB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("40 GiB", ex.Message, StringComparison.Ordinal);
        AssertNothingWasWritten();
    }

    // ---- Imported image names are unique ----

    [Fact]
    public async Task An_existing_image_is_image_exists_naming_as_and_nothing_is_written()
    {
        _paths.Add("/pool/images/shop2");

        var ex = await FailsAsync(await ValidAsync());

        Assert.Equal(ErrorCodes.ImageExists, ex.Code);
        Assert.Contains("--as", ex.Message, StringComparison.Ordinal);
        AssertNothingWasWritten();
        Assert.Contains("/pool/images/shop2", _paths);
    }

    [Fact]
    public async Task A_build_or_import_of_the_same_name_that_holds_the_image_lock_makes_this_import_image_exists()
    {
        // `image build` takes the same lock for its whole run (see the build tests), so of two that start together one gets here.
        _world.Connector.HeldLocks.Add("sqlbranch:image:crm");

        var ex = await FailsAsync(await ValidAsync(), target: "crm");

        Assert.Equal(ErrorCodes.ImageExists, ex.Code);
        Assert.Contains("--as", ex.Message, StringComparison.Ordinal);
        AssertNothingWasWritten();
        Assert.Equal(_world.Connector.SessionsOpened, _world.Connector.SessionsDisposed);
    }

    // ---- Failed imports leave nothing behind ----

    public static TheoryData<string> Steps() => new(ImageImporter.StepNames);

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task A_failure_after_any_step_removes_exactly_what_the_import_made_by_then(string step)
    {
        var archive = await ValidAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(
            archive,
            importer: Importer((name, _) => name == step ? throw new InvalidOperationException("injected") : Task.CompletedTask)));

        Assert.Equal("injected", ex.Message);
        var steps = ImageImporter.StepNames.ToList();
        bool Reached(string name) => steps.IndexOf(step) >= steps.IndexOf(name);

        var removed = _removals.Select(r => RmRf().Match(r.Script).Groups["p"].Value).ToList();
        Assert.Equal(Reached("build-folder"), removed.Contains("/pool/build/" + Id));
        Assert.Equal(Reached("seal"), removed.Contains("/pool/images/shop2"));
        Assert.All(removed, p => Assert.True(p is "/pool/build/" + Id or "/pool/images/shop2", p));
        Assert.DoesNotContain(_paths, p => p.StartsWith("/pool/images/", StringComparison.Ordinal));
        Assert.DoesNotContain("ensure-ended", _timeline); // every step after the tar is a failure after it exited 0
        Assert.Equal(_world.Connector.SessionsOpened, _world.Connector.SessionsDisposed);
        Assert.All(_removals, r => Assert.False(r.WriterRunning));
        Assert.All(_removals, r => Assert.True(r.ClientExited));
    }

    [Theory]
    [InlineData("mv ")]
    [InlineData("chmod 444 '/pool/images/shop2/data/")]
    [InlineData("base64 -d")]
    public async Task A_failed_move_mode_or_manifest_write_removes_the_image_folder_and_a_second_import_succeeds(string failingScript)
    {
        _failScript = script => script.Contains(failingScript, StringComparison.Ordinal);
        var archive = await ValidAsync();

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.DoesNotContain("/pool/images/shop2", _paths);
        Assert.Contains(_removals, r => r.Script.Contains("rm -rf '/pool/images/shop2'", StringComparison.Ordinal));

        _failScript = null;
        _removals.Clear();
        var again = await ImportAsync(archive);
        Assert.Equal("shop2", again.Manifest.Name);
        Assert.Contains("/pool/images/shop2", _paths);
    }

    [Fact]
    public async Task A_mkdir_of_an_image_folder_that_already_exists_fails_and_that_folder_is_never_removed()
    {
        // Another process made /pool/images/shop2 after the name check: the mkdir fails, and the folder is not this import's.
        _paths.Add("/pool/images/shop2");
        _existsCheckSeesIt = false;

        var ex = await FailsAsync(await ValidAsync());

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.Contains("creating the image folder", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/pool/images/shop2", _paths);
        Assert.DoesNotContain(_removals, r => r.Script.Contains("/pool/images/", StringComparison.Ordinal));
        Assert.Contains(_removals, r => r.Script.Contains("rm -rf '/pool/build/abc123def456'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_full_pool_while_writing_is_pool_full_and_nothing_stays()
    {
        _tarExit = 2;
        _tarStdErr = "tar: shop.mdf: Wrote only 4096 of 10240 bytes\ntar: shop.mdf: Cannot write: No space left on device";

        var ex = await FailsAsync(await ValidAsync());

        Assert.Equal(ErrorCodes.PoolFull, ex.Code);
        AssertCleanedUp();
    }

    [Fact]
    public async Task A_tar_that_stops_reading_midway_is_reported_with_its_own_words_and_cleaned_up()
    {
        _sinkFailsAfter = 150_000;
        _tarExit = 2;
        _tarStdErr = "tar: Unexpected EOF in archive";

        var ex = await FailsAsync(await ValidAsync());

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.Contains("Unexpected EOF", ex.Message, StringComparison.Ordinal);
        AssertCleanedUp();
    }

    // ---- The in-container tar must have ended before anything is removed ----

    [Fact]
    public async Task Cleanup_never_starts_while_the_in_container_writer_is_still_running()
    {
        // The archive is damaged inside the first file: the import fails while the engine's tar still waits for more input.
        // Killing the docker client does not stop that tar, and the client takes a while to exit.
        _writerSurvivesClient = true;
        _exitDelay = TimeSpan.FromMilliseconds(150);
        var archive = (await ValidAsync())[..140_000];

        var ex = await FailsAsync(archive);

        Assert.Equal(ErrorCodes.ArchiveCorrupt, ex.Code);
        Assert.NotNull(_process);
        Assert.Equal(["killed", "exited"], _process.Events);
        var ended = _timeline.IndexOf("ensure-ended");
        var removal = _timeline.FindIndex(l => l.Contains("rm -rf", StringComparison.Ordinal));
        Assert.True(ended >= 0 && ended < removal, "the in-container tar must be stopped before the removal");
        Assert.Single(_removals);
        Assert.True(_removals[0].ClientExited);
        Assert.False(_removals[0].WriterRunning);
    }

    [Fact]
    public async Task Cancelling_while_the_writer_survives_the_client_waits_for_both_before_removing()
    {
        _writerSurvivesClient = true;
        _exitDelay = TimeSpan.FromMilliseconds(150);
        using var cancel = new CancellationTokenSource();
        var archive = await ValidAsync();
        var importer = Importer((name, _) =>
        {
            if (name == "build-folder")
            {
                // Cancel as the write starts: the next write to the process sees it.
                _world.Runtime.StreamHandler = Wrap(_world.Runtime.StreamHandler!, cancel);
            }

            return Task.CompletedTask;
        });

        var ex = await Record.ExceptionAsync(() => ImportAsync(archive, importer: importer, token: cancel.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.NotNull(_process);
        Assert.True(_process.Killed);
        var removal = Assert.Single(_removals);
        Assert.True(removal.ClientExited);
        Assert.False(removal.WriterRunning);
        Assert.True(_timeline.IndexOf("ensure-ended") < _timeline.FindIndex(l => l.Contains("rm -rf", StringComparison.Ordinal)));
        AssertCleanedUp();
    }

    private static Func<string, IReadOnlyList<string>, StreamDirection, CancellationToken, IStreamingProcess> Wrap(
        Func<string, IReadOnlyList<string>, StreamDirection, CancellationToken, IStreamingProcess> inner, CancellationTokenSource cancel) =>
        (container, command, direction, token) =>
        {
            var process = inner(container, command, direction, token);
            cancel.Cancel();
            return process;
        };

    [Fact]
    public async Task When_the_writer_cannot_be_confirmed_ended_nothing_is_removed_and_the_leftover_is_reported()
    {
        _ensureExit = 1;
        _writerSurvivesClient = true;
        var archive = (await ValidAsync())[..140_000];

        var ex = await FailsAsync(archive);

        Assert.Empty(_removals);
        Assert.Contains("/pool/build/abc123def456", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was removed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch status", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_end_check_stops_only_a_process_whose_command_line_holds_this_imports_folder()
    {
        Assert.Contains("/proc/$pid/cmdline", ImageImporter.EnsureEndedScript, StringComparison.Ordinal);
        Assert.Contains("grep -qF -- \"$d\"", ImageImporter.EnsureEndedScript, StringComparison.Ordinal);
        Assert.Equal("/tmp/sqlbranch-import-abc123def456.pid", ImageImporter.PidFile(Id));
        var command = ImageImporter.TarCommand(Id);
        Assert.Equal("/tmp/sqlbranch-import-abc123def456.pid", command[^2]);
        Assert.Equal("/pool/build/abc123def456/data", command[^1]);
    }

    // ---- Killed import ----

    [Fact]
    public async Task A_cancel_carries_what_the_cleanup_could_not_remove()
    {
        _ensureExit = 1;
        using var cancel = new CancellationTokenSource();
        var archive = await ValidAsync();
        var importer = Importer((name, _) =>
        {
            if (name == "build-folder")
            {
                _world.Runtime.StreamHandler = Wrap(_world.Runtime.StreamHandler!, cancel);
            }

            return Task.CompletedTask;
        });

        var ex = await Record.ExceptionAsync(() => ImportAsync(archive, importer: importer, token: cancel.Token));

        var cancelled = Assert.IsType<ImportCancelledException>(ex);
        Assert.Contains("/pool/build/abc123def456", Assert.Single(cancelled.Leftovers), StringComparison.Ordinal);
        Assert.Empty(_removals);
    }

    [Fact]
    public async Task A_clean_cancel_carries_no_leftovers()
    {
        using var cancel = new CancellationTokenSource();
        var importer = Importer((name, _) =>
        {
            if (name == "check-version")
            {
                cancel.Cancel();
                cancel.Token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        });
        var archive = await ValidAsync();

        var ex = await Record.ExceptionAsync(() => ImportAsync(archive, importer: importer, token: cancel.Token));

        Assert.Empty(Assert.IsType<ImportCancelledException>(ex).Leftovers);
    }

    [Fact]
    public async Task After_the_tar_exited_0_a_failure_removes_the_image_folder_it_made_without_the_end_check()
    {
        _failScript = script => script.Contains("base64 -d", StringComparison.Ordinal);
        var archive = await ValidAsync();

        await FailsAsync(archive);

        Assert.DoesNotContain("ensure-ended", _timeline);
        Assert.Contains(_removals, r => r.Script.Contains("rm -rf '/pool/images/shop2'", StringComparison.Ordinal));
        Assert.Contains(_removals, r => r.Script.Contains("rm -rf '/pool/build/abc123def456'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_import_before_the_write_removes_nothing_it_did_not_make()
    {
        using var cancel = new CancellationTokenSource();
        var importer = Importer((name, _) =>
        {
            if (name == "check-version")
            {
                cancel.Cancel();
                cancel.Token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        });

        var archive = await ValidAsync();
        var ex = await Record.ExceptionAsync(() => ImportAsync(archive, importer: importer, token: cancel.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Empty(_removals);
        Assert.DoesNotContain(_timeline, l => l == "exec-stream");
    }

    /// <summary>The engine's standard input: it keeps what it is sent, and can break after a number of bytes as a tar that died would.</summary>
    private sealed class RecordingSink : Stream
    {
        private readonly MemoryStream _bytes = new();

        public long? FailAfter { get; init; }

        public byte[] Bytes => _bytes.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _bytes.Length;

        public override long Position
        {
            get => _bytes.Length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (FailAfter is { } limit && _bytes.Length + buffer.Length > limit)
            {
                throw new IOException("Broken pipe");
            }

            _bytes.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
