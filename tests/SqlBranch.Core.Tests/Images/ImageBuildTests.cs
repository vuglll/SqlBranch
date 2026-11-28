using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

/// <summary>
/// <c>image build</c> end to end on fakes: the container runtime, the engine's answers to RESTORE and the
/// scripts that run inside the engine. Nothing here reaches the real docker.
/// </summary>
public sealed partial class ImageBuildTests
{
    private const long Gib = 1L << 30;

    private readonly CliWorld _world = new();

    /// <summary>Everything that happened, in order: <c>sh</c> scripts in the engine, <c>sql</c> statements, <c>cp</c> copies.</summary>
    private readonly List<string> _timeline = [];

    private long _free = 100 * Gib;
    private long _staged;
    private bool _imageExists;

    /// <summary>What the engine says about the image folder on a format-2 pool: <c>none</c>, <c>legacy</c> or <c>catalog.json</c> as base64.</summary>
    private string _catalogFile = "none";
    private string _lastId = "none";
    private int _fileQueries;
    private bool _renamed;
    private string _recoveryModel = "FULL";
    private Func<string, bool>? _failScript;

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _headers = [Header(1, 1, "Database")];

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _files =
    [
        FileRow("Shop", @"C:\Data\Shop.mdf", "D", "PRIMARY", 8 * 1024 * 1024),
        FileRow("Shop_log", @"C:\Data\Shop_log.ldf", "L", string.Empty, 256 * 1024 * 1024),
    ];

    private EngineSqlException? _inspectFailure;

    /// <summary>The statement that fails with <see cref="_inspectFailure"/>: the first of the three inspections by default.</summary>
    private string _inspectFailOn = "LABELONLY";

    /// <summary>Answers the two <c>sys.master_files</c> queries (folder, database, query number from 1) instead of the default Shop files.</summary>
    private Func<string, string, int, IReadOnlyList<IReadOnlyDictionary<string, object?>>>? _masterFiles;

    public ImageBuildTests()
    {
        _world.WithEngine();
        _world.Files["/work/shop.bak"] = 1_000_000;

        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (script == StatusReporter.ListingScript || script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal))
            {
                return inner(container, command);
            }

            _timeline.Add("sh: " + script);
            if (_failScript?.Invoke(script) == true)
            {
                return FakeContainerRuntime.Fail(1, "boom");
            }

            if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok($"SQLBRANCH_FREE={_free - _staged}");
            }

            if (script.Contains("SQLBRANCH_LASTID=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_LASTID=" + _lastId);
            }

            if (script.Contains("SQLBRANCH_CATALOGFILE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_CATALOGFILE=" + _catalogFile);
            }

            if (script.Contains("SQLBRANCH_EXISTS=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok($"SQLBRANCH_EXISTS={(_imageExists ? "yes" : "no")}");
            }

            if (script.Contains("SQLBRANCH_SHA256=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_SHA256=" + new string('a', 64));
            }

            return FakeContainerRuntime.Ok();
        };
        _world.Runtime.OnCopy = (host, container) =>
        {
            _timeline.Add($"cp: {host} -> {container}");
            _staged = _world.Files[host];
        };

        var connector = _world.Connector;
        connector.ExecuteHandler = sql =>
        {
            _timeline.Add("sql: " + sql);
            _renamed |= sql.Contains("MODIFY FILE", StringComparison.Ordinal);
        };
        connector.ParameterizedExecuteHandler = (sql, _) => _timeline.Add("sql: " + sql);
        connector.ParameterizedScalarHandler = (sql, _) => sql.Contains("recovery_model_desc", StringComparison.Ordinal) ? _recoveryModel : 1;
        connector.RowsHandler = Rows;
    }

    private static Dictionary<string, object?> Header(int position, int type, string description, int major = 16) =>
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Position"] = position,
            ["BackupType"] = type,
            ["BackupTypeDescription"] = description,
            ["DatabaseName"] = "Shop",
            ["SoftwareVersionMajor"] = major,
            ["SoftwareVersionMinor"] = 0,
            ["SoftwareVersionBuild"] = 4000,
            ["RecoveryModel"] = "FULL",
        };

    private static Dictionary<string, object?> FileRow(string logical, string physical, string type, string group, long size) =>
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["LogicalName"] = logical,
            ["PhysicalName"] = physical,
            ["Type"] = type,
            ["FileGroupName"] = group,
            ["Size"] = size,
        };

    private static Dictionary<string, object?> MasterFile(string name, string physical, string type, long bytes) =>
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = name,
            ["physical_name"] = physical,
            ["type_desc"] = type,
            ["bytes"] = bytes,
        };

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        _timeline.Add("sql: " + sql);
        if (_inspectFailure is not null && sql.Contains(_inspectFailOn, StringComparison.Ordinal))
        {
            throw _inspectFailure;
        }

        if (sql.Contains("LABELONLY", StringComparison.Ordinal))
        {
            return [new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["FamilyCount"] = 1 }];
        }

        if (sql.Contains("HEADERONLY", StringComparison.Ordinal))
        {
            return _headers;
        }

        if (sql.Contains("FILELISTONLY", StringComparison.Ordinal))
        {
            return _files;
        }

        // sys.master_files: first the restored database, then the one with the rebuilt log.
        var database = (string)parameters["n"]!;
        var folder = "/pool/build/" + database["sqlbranch_build_".Length..];
        _fileQueries++;
        if (_masterFiles is { } custom)
        {
            return custom(folder, database, _fileQueries);
        }

        return _fileQueries == 1
            ?
            [
                MasterFile("Shop", folder + "/Shop.mdf", "ROWS", 8 * 1024 * 1024),
                MasterFile("Shop_log", folder + "/Shop_log.ldf", "LOG", 256 * 1024 * 1024),
            ]
            :
            [
                MasterFile("Shop", folder + "/Shop.mdf", "ROWS", 8 * 1024 * 1024),
                MasterFile(_renamed ? "Shop_log" : database + "_log", folder + "/" + database + "_log.ldf", "LOG", 524_288),
            ];
    }

    private (int Exit, JsonElement Json, string Err) Build(params string[] extra)
    {
        var run = _world.Run(["image", "build", "shop", "--from-bak", "shop.bak", .. extra, "--format", "json"]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement, run.Err);
    }

    private static string ErrorCode(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString()!;

    private static string ErrorMessage(JsonElement json) => json.GetProperty("error").GetProperty("message").GetString()!;

    private int IndexOf(string marker) => _timeline.FindIndex(l => l.Contains(marker, StringComparison.Ordinal));

    private bool Ran(string marker) => IndexOf(marker) >= 0;

    private IEnumerable<string> RemovedFolders() =>
        _timeline.Where(l => l.StartsWith("sh: rm -rf", StringComparison.Ordinal));

    [GeneratedRegex(@"printf %s '(?<b64>[^']+)'")]
    private static partial Regex ManifestScript();

    private ImageManifest WrittenManifest()
    {
        var script = _timeline.Single(l => l.Contains("base64 -d", StringComparison.Ordinal));
        var b64 = ManifestScript().Match(script).Groups["b64"].Value;
        return ImageManifest.FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
    }

    // ---- 5.1: names are checked before any SQL or file action ----

    [Fact]
    public void A_bad_name_never_reaches_a_file_or_a_statement_through_the_cli()
    {
        var world = _world;

        foreach (var name in new[] { "../branches/pr-142", "Shop DB", "sqlbranch_x", "a/b" })
        {
            var run = world.Run("image", "build", name, "--from-bak", "shop.bak", "--format", "json");

            Assert.Equal(2, run.Exit);
            Assert.Equal("invalid_name", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        Assert.Empty(world.Connector.Attempts);
        Assert.Empty(world.Connector.Statements);
        Assert.Empty(world.Runtime.Copies);
        Assert.DoesNotContain(world.Runtime.Calls, c => c.StartsWith("exec", StringComparison.Ordinal));
    }

    // ---- 5.2: the client checks the file first ----

    [Fact]
    public void A_missing_backup_file_exits_2_naming_the_file_and_nothing_is_copied_or_connected()
    {
        _world.Files.Clear();

        var run = Build();

        Assert.Equal(2, run.Exit);
        Assert.Equal("usage_error", ErrorCode(run.Json));
        Assert.Contains("shop.bak", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Copies);
        Assert.Empty(_world.Connector.Attempts);
        Assert.Empty(_timeline);
    }

    [Fact]
    public void An_unreadable_backup_file_exits_2_before_anything_is_copied()
    {
        _world.UnreadableFiles.Add("/work/shop.bak");

        var run = Build();

        Assert.Equal(2, run.Exit);
        Assert.Contains("cannot be read", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Copies);
        Assert.Empty(_timeline);
    }

    [Fact]
    public void A_file_the_engine_cannot_read_as_a_backup_exits_1_not_a_backup_and_the_staged_copy_is_removed()
    {
        _inspectFailure = new EngineSqlException("The media family on device is incorrectly formed.", [3241, 3013]);

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("not_a_backup", ErrorCode(run.Json));
        Assert.Contains("shop.bak", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain("backup.bak", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Single(_world.Runtime.Copies);
        Assert.Contains(RemovedFolders(), l => l.Contains("/pool/build/", StringComparison.Ordinal));
        Assert.False(Ran("RESTORE DATABASE"));
    }

    // ---- 5.3 through the command ----

    [Fact]
    public void A_differential_backup_exits_1_with_the_type_code_after_staging_and_the_staging_is_removed()
    {
        _headers = [Header(1, 5, "Database Differential")];

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("unsupported_backup_type", ErrorCode(run.Json));
        Assert.Contains("Only full database backups are supported", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Ran("RESTORE DATABASE"));
        Assert.Contains(RemovedFolders(), l => l.Contains("/pool/build/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_backup_set_flag_picks_the_set_and_a_differential_first_set_is_then_fine()
    {
        _headers = [Header(1, 5, "Database Differential"), Header(2, 1, "Database")];

        var run = Build("--backup-set", "2");

        Assert.Equal(0, run.Exit);
        var restore = _world.Connector.Parameters.Last(p => p.ContainsKey("l0") && p.ContainsKey("file"));
        Assert.Equal(2, restore["file"]);
    }

    // ---- 5.4: the two space checks ----

    [Fact]
    public void Too_little_space_for_the_backup_exits_1_pool_full_before_anything_is_copied_and_the_pool_is_unchanged()
    {
        _world.Files["/work/shop.bak"] = 20 * Gib;
        _free = 10 * Gib;

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("pool_full", ErrorCode(run.Json));
        Assert.Contains("20 GiB", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("10 GiB", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Copies);
        Assert.DoesNotContain(_timeline, l => l.StartsWith("sh: mkdir", StringComparison.Ordinal) && l.Contains("/pool/build", StringComparison.Ordinal));
        Assert.Empty(RemovedFolders());
        Assert.DoesNotContain(_world.Connector.Parameters, p => p.ContainsKey("bak"));
    }

    [Fact]
    public void Too_little_space_for_the_restored_files_exits_1_pool_full_with_both_numbers_and_deletes_the_staged_copy()
    {
        _world.Files["/work/shop.bak"] = 20 * Gib;
        _free = 40 * Gib;
        _files =
        [
            FileRow("Shop", @"C:\Data\Shop.mdf", "D", "PRIMARY", 40 * Gib),
            FileRow("Shop_log", @"C:\Data\Shop_log.ldf", "L", string.Empty, 10 * Gib),
        ];

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("pool_full", ErrorCode(run.Json));
        Assert.Contains("50 GiB", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("20 GiB", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Single(_world.Runtime.Copies);
        Assert.False(Ran("RESTORE DATABASE"));
        var remove = Assert.Single(RemovedFolders());
        Assert.Contains("/pool/build/", remove, StringComparison.Ordinal);
        Assert.True(IndexOf("cp:") < _timeline.IndexOf(remove));
    }

    [Fact]
    public void Exactly_enough_space_is_enough()
    {
        _world.Files["/work/shop.bak"] = 20 * Gib;
        _free = 20 * Gib;
        _files = [FileRow("Shop", "/x/Shop.mdf", "D", "PRIMARY", 0), FileRow("Shop_log", "/x/Shop_log.ldf", "L", string.Empty, 0)];

        var run = Build();

        // 20 GiB free before staging, 0 after: the restored files need 0 bytes.
        Assert.Equal(0, run.Exit);
    }

    // ---- 5.5: the pipeline ----

    [Fact]
    public void A_build_runs_the_steps_in_order_and_reports_the_clone_mode_in_text_and_json()
    {
        var run = Build();
        var text = _world.Run("image", "build", "shop2", "--from-bak", "shop.bak");

        Assert.Equal(0, run.Exit);
        Assert.Equal("thin", run.Json.GetProperty("image").GetProperty("cloneMode").GetString());
        Assert.Equal("image build", run.Json.GetProperty("command").GetString());
        Assert.Contains("Clone mode: thin", text.Out, StringComparison.Ordinal);

        var order = new[]
        {
            "SQLBRANCH_EXISTS", "SQLBRANCH_FREE", "mkdir '/pool/build/", "cp:", "SQLBRANCH_SHA256", "LABELONLY", "HEADERONLY", "FILELISTONLY",
            "SQLBRANCH_FREE", "RESTORE DATABASE", "SET RECOVERY SIMPLE", "CHECKPOINT", "sys.master_files", "sp_detach_db",
            "rm -f '/pool/build/", "ATTACH_REBUILD_LOG", "MODIFY FILE", "sp_detach_db", "mkdir '/pool/images/", "base64 -d", "rm -rf '/pool/build/",
        };
        var position = -1;
        foreach (var marker in order)
        {
            var next = _timeline.FindIndex(position + 1, l => l.Contains(marker, StringComparison.Ordinal));
            Assert.True(next > position, $"'{marker}' did not come after position {position} in:\n{string.Join('\n', _timeline.Take(_timeline.Count / 2))}");
            position = next;
        }
    }

    [Fact]
    public void The_backup_is_copied_in_with_the_engines_owner_and_the_staging_folder_is_removed_after_a_success()
    {
        var run = Build();

        Assert.Equal(0, run.Exit);
        var copy = Assert.Single(_world.Runtime.Copies);
        Assert.Equal(ContainerLabels.EngineName, copy.Container);
        Assert.Equal("/work/shop.bak", copy.HostPath);
        Assert.StartsWith("/pool/build/", copy.ContainerPath, StringComparison.Ordinal);
        Assert.Equal("10001:0", copy.Owner);
        var remove = Assert.Single(RemovedFolders());
        Assert.Contains(Path.GetDirectoryName(copy.ContainerPath)!, remove, StringComparison.Ordinal);
        Assert.Equal(_timeline.Count - 1, _timeline.IndexOf(remove));
    }

    [Fact]
    public void The_restore_names_every_file_in_the_staging_folder_and_passes_paths_as_parameters()
    {
        Build();

        var restore = _world.Connector.Parameters.Single(p => p.ContainsKey("l0") && p.ContainsKey("file"));
        Assert.Equal("Shop", restore["l0"]);
        Assert.Equal("Shop_log", restore["l1"]);
        Assert.Matches("^/pool/build/[0-9a-f]{12}/Shop.mdf$", (string)restore["p0"]!);
        Assert.Matches("^/pool/build/[0-9a-f]{12}/Shop_log.ldf$", (string)restore["p1"]!);
        Assert.Matches("^/pool/build/[0-9a-f]{12}/backup.bak$", (string)restore["bak"]!);
        var sql = _timeline.Single(l => l.Contains("RESTORE DATABASE", StringComparison.Ordinal));
        Assert.Contains("RESTORE DATABASE [sqlbranch_build_", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("/pool/", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_manifest_records_the_source_the_engine_the_files_the_recovery_models_and_the_backup()
    {
        var run = Build();

        Assert.Equal(0, run.Exit);
        var manifest = WrittenManifest();
        Assert.Equal("shop", manifest.Name);
        Assert.Equal("Shop", manifest.SourceDatabase);
        Assert.Equal("16.0.4325.4", manifest.EngineVersion);
        Assert.Equal(CliWorld.EngineImage, manifest.EngineImage);
        Assert.Equal("FULL", manifest.OriginalRecoveryModel);
        Assert.Equal("SIMPLE", manifest.SealedRecoveryModel);
        Assert.Equal(256 * 1024 * 1024, manifest.OriginalLogSizeBytes);
        Assert.True(manifest.LogRebuilt);
        Assert.Equal("shop.bak", manifest.BackupFile);
        Assert.Equal(new string('a', 64), manifest.BackupSha256);
        Assert.Empty(manifest.Labels);
        Assert.Equal(ImageManifest.CurrentFormat, manifest.Format);
        Assert.Equal(
            [new ImageFile("Shop", "Shop.mdf", "data", 8 * 1024 * 1024), new ImageFile("Shop_log", "Shop_log.ldf", "log", 524_288)],
            manifest.Files);
        var image = run.Json.GetProperty("image");
        Assert.Equal(2, image.GetProperty("files").GetArrayLength());
        Assert.Equal(8 * 1024 * 1024 + 524_288, image.GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public void The_rebuilt_log_is_given_the_name_the_source_log_had_and_the_files_are_sealed_read_only()
    {
        Build();

        Assert.Contains(_timeline, l => l.Contains("NEWNAME = [Shop_log]", StringComparison.Ordinal));
        var move = _timeline.Single(l => l.StartsWith("sh: set -e; mkdir '/pool/images/shop/data'", StringComparison.Ordinal));
        Assert.Contains("mv '/pool/build/", move, StringComparison.Ordinal);
        Assert.Contains("/Shop_log.ldf'", move, StringComparison.Ordinal);
        Assert.Contains("chmod 444 '/pool/images/shop/data/Shop.mdf'", move, StringComparison.Ordinal);
        Assert.Contains("chmod 444 '/pool/images/shop/data/Shop_log.ldf'", move, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlbranch_build_", move.Split("mv ")[^1].Split(';')[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_old_log_is_deleted_before_the_data_files_are_attached_with_a_rebuilt_log()
    {
        Build();

        var deleteLog = _timeline.FindIndex(l => l.StartsWith("sh: rm -f '/pool/build/", StringComparison.Ordinal) && l.Contains("Shop_log.ldf", StringComparison.Ordinal));
        var attach = IndexOf("ATTACH_REBUILD_LOG");
        Assert.True(deleteLog >= 0 && deleteLog < attach);
        var attachSql = _timeline[attach];
        Assert.Contains("(FILENAME = N'/pool/build/", attachSql, StringComparison.Ordinal);
        Assert.Contains("Shop.mdf')", attachSql, StringComparison.Ordinal);
        Assert.DoesNotContain("Shop_log.ldf", attachSql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_build_holds_the_image_lock_and_its_own_build_lock()
    {
        Build();

        Assert.Equal(["sqlbranch:image:shop", _world.Connector.TakenLocks[1]], _world.Connector.TakenLocks);
        Assert.Matches("^sqlbranch:build:[0-9a-f]{12}$", _world.Connector.TakenLocks[1]);
        var id = _world.Connector.TakenLocks[1]["sqlbranch:build:".Length..];
        Assert.Contains(_timeline, l => l.Contains($"[sqlbranch_build_{id}]", StringComparison.Ordinal));
    }

    [Fact]
    public void The_password_is_not_in_the_output_the_statements_or_the_scripts()
    {
        var run = _world.Run("image", "build", "shop", "--from-bak", "shop.bak", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain(CliWorld.StoredPassword, run.Out + run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(_timeline, l => l.Contains(CliWorld.StoredPassword, StringComparison.Ordinal));
    }

    // ---- recovery model flag ----

    [Fact]
    public void The_default_build_sets_simple_before_the_restore_is_sealed_and_again_after_the_log_is_rebuilt()
    {
        var run = Build();

        Assert.Equal(0, run.Exit);
        Assert.Equal(2, _timeline.Count(l => l.Contains("SET RECOVERY SIMPLE", StringComparison.Ordinal)));
        Assert.False(Ran("SET RECOVERY FULL"));
        Assert.True(_timeline.FindLastIndex(l => l.Contains("SET RECOVERY SIMPLE", StringComparison.Ordinal)) > IndexOf("ATTACH_REBUILD_LOG"));
    }

    [Fact]
    public void Keep_recovery_model_skips_the_switch_and_seals_with_the_original_model()
    {
        var run = Build("--keep-recovery-model");

        Assert.Equal(0, run.Exit);
        Assert.False(Ran("SET RECOVERY SIMPLE"));
        // The engine resets the model when it attaches the database with a new log, so the build sets it again before the last detach.
        Assert.True(IndexOf("SET RECOVERY FULL") > IndexOf("ATTACH_REBUILD_LOG"));
        Assert.True(IndexOf("SET RECOVERY FULL") < _timeline.FindLastIndex(l => l.Contains("sp_detach_db", StringComparison.Ordinal)));
        Assert.Equal("FULL", WrittenManifest().SealedRecoveryModel);
        Assert.Equal("FULL", run.Json.GetProperty("image").GetProperty("sealedRecoveryModel").GetString());
    }

    // ---- names, locks and refusals ----

    [Fact]
    public void An_existing_image_exits_1_and_nothing_is_copied()
    {
        _imageExists = true;

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_exists", ErrorCode(run.Json));
        Assert.Empty(_world.Runtime.Copies);
        Assert.Empty(RemovedFolders());
    }

    [Fact]
    public void Name_already_used_on_a_format_2_pool_is_image_exists_and_nothing_is_copied()
    {
        Format2Pool.Apply(_world);
        _catalogFile = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Format2Pool.Catalog("shop", Format2Pool.VersionId("shop"))));

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_exists", ErrorCode(run.Json));
        Assert.Empty(_world.Runtime.Copies);
        Assert.False(Ran("SQLBRANCH_EXISTS="));
    }

    [Fact]
    public void A_name_used_by_an_image_an_older_client_wrote_is_image_exists_on_a_format_2_pool()
    {
        Format2Pool.Apply(_world);
        _catalogFile = "legacy";

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_exists", ErrorCode(run.Json));
        Assert.Empty(_world.Runtime.Copies);
    }

    [Fact]
    public void A_name_whose_catalog_has_no_current_version_is_free_on_a_format_2_pool()
    {
        Format2Pool.Apply(_world);
        _catalogFile = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Format2Pool.Catalog("shop", null)));

        var run = Build();

        Assert.NotEqual("image_exists", run.Exit == 0 ? string.Empty : ErrorCode(run.Json));
    }

    [Fact]
    public void A_build_of_the_same_name_that_is_running_makes_this_one_exit_1()
    {
        _world.Connector.HeldLocks.Add("sqlbranch:image:shop");

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Contains("running", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Copies);
        Assert.Empty(_timeline);
    }

    [Fact]
    public void A_stopped_engine_exits_3_engine_stopped()
    {
        _world.Runtime.Containers[ContainerLabels.EngineName] = _world.Runtime.Containers[ContainerLabels.EngineName] with { Running = false };

        var run = Build();

        Assert.Equal(3, run.Exit);
        Assert.Equal("engine_stopped", ErrorCode(run.Json));
        Assert.Empty(_world.Runtime.Copies);
    }

    [Fact]
    public void Before_init_the_command_exits_3_not_initialized()
    {
        var empty = new CliWorld();
        empty.Files["/work/shop.bak"] = 10;

        var run = empty.Run("image", "build", "shop", "--from-bak", "shop.bak", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("not_initialized", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void A_backup_under_the_pools_import_folder_is_used_where_it_is_without_a_copy()
    {
        _world.Files["/data/sb/import/shop.bak"] = 5_000;

        var run = _world.Run("image", "build", "shop", "--from-bak", "/data/sb/import/shop.bak", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Empty(_world.Runtime.Copies);
        var backup = _world.Connector.Parameters.First(p => p.ContainsKey("bak"))["bak"];
        Assert.Equal("/pool/import/shop.bak", backup);
        // Only the build folder is removed, never the user's backup.
        Assert.All(RemovedFolders(), l => Assert.DoesNotContain("/pool/import", l, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/data/sb/import/x.bak", "/pool/import/x.bak")]
    [InlineData("/data/sb/import/sub/x.bak", "/pool/import/sub/x.bak")]
    [InlineData("/data/sb/imports/x.bak", null)]
    [InlineData("/data/sb/x.bak", null)]
    [InlineData("/data/sb/import/", null)]
    [InlineData("/data/sb/import/../x.bak", null)]
    [InlineData("/elsewhere/import/x.bak", null)]
    public void Only_a_file_inside_the_import_folder_of_a_directory_pool_counts_as_imported(string path, string? expected)
    {
        Assert.Equal(expected, ImagePaths.ImportPathOf(path, new PoolLocation(PoolKind.Directory, "/data/sb")));
    }

    [Fact]
    public void A_pool_volume_has_no_import_folder_the_client_can_name()
    {
        Assert.Null(ImagePaths.ImportPathOf("/data/sb/import/x.bak", new PoolLocation(PoolKind.Volume, "sqlbranch-pool")));
    }

    // ---- the space reserve (a full pool must not stop the cleanup) ----

    private int ReserveFreed() =>
        _timeline.FindIndex(l => l.StartsWith("sh: rm -f '/pool/build/", StringComparison.Ordinal) && l.EndsWith("/.reserve'", StringComparison.Ordinal));

    [Fact]
    public void The_build_folder_step_makes_a_64_MiB_reserve_that_cannot_fail_the_step()
    {
        Build();

        var folder = _timeline.Single(l => l.StartsWith("sh: mkdir -p '/pool/build' && mkdir '/pool/build/", StringComparison.Ordinal));
        Assert.Contains("fallocate -l 64M '/pool/build/", folder, StringComparison.Ordinal);
        Assert.Contains("dd if=/dev/zero of='/pool/build/", folder, StringComparison.Ordinal);
        Assert.EndsWith("/.reserve'; true; }", folder, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_build_frees_the_reserve_before_the_cleanup_runs_any_sql()
    {
        _world.Connector.ExecuteHandler = sql =>
        {
            _timeline.Add("sql: " + sql);
            if (sql.Contains("CHECKPOINT", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("checkpoint failed");
            }
        };
        _world.Connector.Databases["sqlbranch_build_x"] = [];

        var run = Build();

        Assert.Equal(1, run.Exit);
        var failed = _timeline.FindIndex(l => l.Contains("CHECKPOINT", StringComparison.Ordinal));
        var freed = ReserveFreed();
        var drop = _timeline.FindIndex(l => l.Contains("DROP DATABASE [sqlbranch_build_", StringComparison.Ordinal));
        Assert.True(failed >= 0 && freed > failed && freed < drop, string.Join('\n', _timeline));
        Assert.DoesNotContain(_timeline.Skip(failed + 1).Take(freed - failed - 1), l => l.StartsWith("sql: ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_successful_build_leaves_the_reserve_to_the_removal_of_its_folder()
    {
        var run = Build();

        Assert.Equal(0, run.Exit);
        Assert.Equal(-1, ReserveFreed());
        Assert.Contains(RemovedFolders(), l => l.Contains("rm -rf '/pool/build/", StringComparison.Ordinal));
    }

    // ---- undoing what the build did ----

    [Fact]
    public void A_failure_after_the_restore_drops_the_temporary_database_and_removes_the_staging_folder_but_no_image_folder()
    {
        _world.Connector.ExecuteHandler = sql =>
        {
            _timeline.Add("sql: " + sql);
            if (sql.Contains("CHECKPOINT", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("checkpoint failed");
            }
        };
        _world.Connector.Databases["sqlbranch_build_x"] = [];

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Contains("checkpoint failed", ErrorMessage(run.Json), StringComparison.Ordinal);
        var drop = _timeline.FindIndex(l => l.Contains("DROP DATABASE [sqlbranch_build_", StringComparison.Ordinal));
        Assert.True(drop > IndexOf("RESTORE DATABASE"));
        var removed = Assert.Single(RemovedFolders());
        Assert.Contains("/pool/build/", removed, StringComparison.Ordinal);
        Assert.True(_timeline.IndexOf(removed) > drop);
        Assert.False(Ran("rm -rf '/pool/images/shop'"));
    }

    [Fact]
    public void A_failure_while_writing_the_manifest_removes_the_image_folder_the_build_made()
    {
        _failScript = s => s.Contains("base64 -d", StringComparison.Ordinal);

        var run = Build();

        Assert.Equal(1, run.Exit);
        var removed = RemovedFolders().ToList();
        Assert.Contains(removed, l => l.Contains("rm -rf '/pool/images/shop'", StringComparison.Ordinal));
        Assert.Contains(removed, l => l.Contains("rm -rf '/pool/build/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_copy_removes_the_build_folder_and_reports_the_failure()
    {
        _world.Runtime.CopyFailure = new SqlBranchException("docker cp failed with exit code 1: no space left on device");

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Contains("no space left", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains(RemovedFolders(), l => l.Contains("/pool/build/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_restore_drops_the_temporary_database_and_reports_the_engines_message()
    {
        _world.Connector.ParameterizedExecuteHandler = (sql, _) =>
        {
            _timeline.Add("sql: " + sql);
            throw new EngineSqlException("Write on \"/pool/build/x/Shop.mdf\" failed: 112(There is not enough space on the disk.)", [3202]);
        };

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Contains("restore failed", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains(_timeline, l => l.Contains("DROP DATABASE [sqlbranch_build_", StringComparison.Ordinal));
        Assert.Contains(RemovedFolders(), l => l.Contains("/pool/build/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_staging_folder_that_cannot_be_removed_after_a_success_is_reported_as_a_failure()
    {
        _failScript = s => s.StartsWith("rm -rf '/pool/build/", StringComparison.Ordinal);

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Contains("could not remove", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("sqlbranch status", ErrorMessage(run.Json), StringComparison.Ordinal);
    }

    // ---- group 5 review fixes ----

    [Fact]
    public void A_failed_image_folder_mkdir_runs_no_rm_of_the_image_folder_because_the_build_did_not_make_it()
    {
        _failScript = s => s.Contains("mkdir '/pool/images/shop'", StringComparison.Ordinal);

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Contains("creating the image folder", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Ran("rm -rf '/pool/images/shop'"));
        Assert.False(Ran("mkdir '/pool/images/shop/data'"));
        Assert.Contains(RemovedFolders(), l => l.Contains("rm -rf '/pool/build/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_image_folder_is_made_in_its_own_step_before_the_data_folder_and_the_moves()
    {
        Build();

        var folder = IndexOf("mkdir -p '/pool/images' && mkdir '/pool/images/shop'");
        var move = IndexOf("sh: set -e; mkdir '/pool/images/shop/data'");
        Assert.True(folder >= 0 && folder < move, string.Join('\n', _timeline));
    }

    [Fact]
    public void A_failure_names_what_the_cleanup_could_not_remove_and_keeps_the_builds_own_error_code()
    {
        _world.Connector.ExecuteHandler = sql =>
        {
            _timeline.Add("sql: " + sql);
            if (sql.Contains("CHECKPOINT", StringComparison.Ordinal))
            {
                throw new SqlBranchException(ErrorCodes.PoolFull, "checkpoint failed");
            }

            if (sql.Contains("DROP DATABASE", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("drop refused");
            }
        };
        _failScript = s => s.StartsWith("rm -rf '/pool/build/", StringComparison.Ordinal);

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("pool_full", ErrorCode(run.Json));
        var message = ErrorMessage(run.Json);
        Assert.Contains("checkpoint failed", message, StringComparison.Ordinal);
        Assert.Contains("could not remove", message, StringComparison.Ordinal);
        Assert.Contains("the temporary database sqlbranch_build_", message, StringComparison.Ordinal);
        Assert.Contains("drop refused", message, StringComparison.Ordinal);
        Assert.Contains("/pool/build/", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch status", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drop_in_the_cleanup_is_guarded_like_the_single_user_line()
    {
        _failScript = s => s.Contains("base64 -d", StringComparison.Ordinal);
        _world.Connector.ExecuteHandler = sql =>
        {
            _timeline.Add("sql: " + sql);
            if (sql.Contains("CHECKPOINT", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("checkpoint failed");
            }
        };

        Build();

        var single = _timeline.Single(l => l.Contains("SET SINGLE_USER", StringComparison.Ordinal));
        var drop = _timeline.Single(l => l.Contains("DROP DATABASE [sqlbranch_build_", StringComparison.Ordinal));
        Assert.StartsWith("sql: IF DB_ID(N'sqlbranch_build_", single, StringComparison.Ordinal);
        Assert.StartsWith("sql: IF DB_ID(N'sqlbranch_build_", drop, StringComparison.Ordinal);
        Assert.Contains("IS NOT NULL DROP DATABASE", drop, StringComparison.Ordinal);
    }

    [Fact]
    public void The_restore_error_for_a_backup_the_engine_version_cannot_take_is_a_version_mismatch_naming_both_versions()
    {
        _world.Connector.ParameterizedExecuteHandler = (sql, _) =>
        {
            _timeline.Add("sql: " + sql);
            throw new EngineSqlException(
                "The database was backed up on a server running version 17.00.1000. That version is incompatible with this server, which is running version 16.00.4325.",
                [3169, 3013]);
        };

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("version_mismatch", ErrorCode(run.Json));
        Assert.Contains("2025", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("2022", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains(RemovedFolders(), l => l.Contains("/pool/build/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_backup_that_cannot_be_opened_is_a_plain_failure_with_the_operating_systems_message()
    {
        _inspectFailure = new EngineSqlException("Cannot open backup device '/pool/build/x/backup.bak'. Operating system error 5(Access is denied).", [3201, 3013]);

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", ErrorCode(run.Json));
        Assert.Contains("Access is denied", ErrorMessage(run.Json), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_name_exits_2_before_init_for_build_and_rm()
    {
        var empty = new CliWorld();
        empty.Files["/work/shop.bak"] = 10;

        var build = empty.Run("image", "build", "../branches/x", "--from-bak", "shop.bak", "--format", "json");
        var rm = empty.Run("image", "rm", "../branches/x", "--format", "json");
        var fine = empty.Run("image", "rm", "shop", "--format", "json");

        Assert.Equal(2, build.Exit);
        Assert.Equal(2, rm.Exit);
        Assert.Equal("invalid_name", JsonDocument.Parse(rm.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(3, fine.Exit);
    }

    [Fact]
    public void Hostile_logical_and_physical_names_stay_inside_quotes_in_every_statement_and_script()
    {
        const string logical = "Sh]o'p;EVIL";
        const string logLogical = "Lo]g';EVIL";
        const string physical = "C:\\Data\\we'ird$(EVIL).mdf\nEVIL";
        const string logPhysical = "/x/lo'g$(EVIL);.ldf";
        _files =
        [
            FileRow(logical, physical, "D", "PRIMARY", 8 * 1024 * 1024),
            FileRow(logLogical, logPhysical, "L", string.Empty, 256 * 1024 * 1024),
        ];
        _masterFiles = (folder, database, query) => query == 1
            ?
            [
                MasterFile(logical, folder + "/we'ird$(EVIL).mdf", "ROWS", 8 * 1024 * 1024),
                MasterFile(logLogical, folder + "/lo'g$(EVIL);.ldf", "LOG", 256 * 1024 * 1024),
            ]
            :
            [
                MasterFile(logical, folder + "/we'ird$(EVIL).mdf", "ROWS", 8 * 1024 * 1024),
                MasterFile(database + "_log", folder + "/" + database + "_log.ldf", "LOG", 524_288),
            ];

        var run = Build();

        Assert.Equal(0, run.Exit);

        // RESTORE ... MOVE: the names are parameters, so the statement text never holds them.
        var restore = _world.Connector.Parameters.Single(p => p.ContainsKey("l0") && p.ContainsKey("file"));
        Assert.Equal(logical, restore["l0"]);
        Assert.Equal(logLogical, restore["l1"]);
        Assert.DoesNotContain(_timeline.Single(l => l.Contains("RESTORE DATABASE", StringComparison.Ordinal)), "EVIL", StringComparison.Ordinal);

        // ATTACH_REBUILD_LOG FILENAME: the quote is doubled inside the literal.
        var attach = _timeline.Single(l => l.Contains("ATTACH_REBUILD_LOG", StringComparison.Ordinal));
        Assert.Contains("we''ird$(EVIL).mdf')", attach, StringComparison.Ordinal);

        // MODIFY FILE NEWNAME: the ] is doubled inside the identifier.
        var rename = _timeline.Single(l => l.Contains("NEWNAME", StringComparison.Ordinal));
        Assert.Contains("NEWNAME = [Lo]]g';EVIL]", rename, StringComparison.Ordinal);

        // The seal script moves the files with shell-quoted paths.
        var seal = _timeline.Single(l => l.StartsWith("sh: set -e; mkdir '/pool/images/shop/data'", StringComparison.Ordinal));
        Assert.Contains("'/pool/build/", seal, StringComparison.Ordinal);
        Assert.Contains("we'\\''ird$(EVIL).mdf'", seal, StringComparison.Ordinal);

        // Whatever the names hold, none of it is ever outside a quote or a bracket.
        foreach (var line in _timeline.Where(l => l.Contains("EVIL", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("EVIL", OutsideQuotes(line), StringComparison.Ordinal);
        }
    }

    /// <summary>The line with every shell word in single quotes, every T-SQL literal and every bracketed identifier cut out.</summary>
    private static string OutsideQuotes(string line)
    {
        var text = line.Replace("'\\''", "\u0001", StringComparison.Ordinal);
        if (line.StartsWith("sql:", StringComparison.Ordinal))
        {
            text = Regex.Replace(text, @"N'(?:[^']|'')*'", string.Empty);
            return Regex.Replace(text, @"\[(?:[^\]]|\]\])*\]", string.Empty);
        }

        return Regex.Replace(text, "'[^']*'", string.Empty);
    }

    public static TheoryData<string> AllSteps()
    {
        var data = new TheoryData<string>();
        foreach (var step in ImageBuilder.StepNames(ImageSource.Backup))
        {
            data.Add(step);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSteps))]
    public async Task A_failure_injected_after_any_step_undoes_exactly_what_the_build_had_made_by_then(string step)
    {
        var builder = new ImageBuilder(_world.Runtime, _world.Connector, _world.Redactor)
        {
            AfterStep = (name, _) => name == step ? throw new InvalidOperationException("injected") : Task.CompletedTask,
        };
        var pool = await new PoolFinder(_world.Runtime, ManagedPaths.Default, _ => true).RequireAsync(
            _world.Services().LoadSettings(null), "/home/u/.sqlbranch/pool", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildAsync(pool, 14330, new ImageBuildRequest("shop", "/work/shop.bak", 1_000_000), CancellationToken.None));

        Assert.Equal("injected", ex.Message);
        var steps = ImageBuilder.StepNames(ImageSource.Backup).ToList();
        bool Reached(string name) => steps.IndexOf(step) >= steps.IndexOf(name);

        // The build folder is made after the space check, so it exists from the "stage" step on.
        var buildFolder = RemovedFolders().Where(l => l.Contains("rm -rf '/pool/build/", StringComparison.Ordinal)).ToList();
        Assert.Equal(Reached("stage"), buildFolder.Count == 1);

        // The temporary database is attached between the restore and the first detach, and no other time.
        var attached = Reached("restore") && !Reached("detach");
        Assert.Equal(attached, Ran("DROP DATABASE"));

        // The image folder is made by the seal step and is removed again.
        var imageFolder = Reached("seal");
        Assert.Equal(imageFolder, Ran("rm -rf '/pool/images/shop'"));

        // Whatever was removed was removed after everything that created it.
        if (Ran("DROP DATABASE") && buildFolder.Count == 1)
        {
            Assert.True(_timeline.IndexOf(buildFolder[0]) > IndexOf("DROP DATABASE"));
        }

        // The staged copy was made only from the stage step on, and the build folder that held it is gone.
        Assert.Equal(Reached("stage"), _world.Runtime.Copies.Count > 0);
    }

    public static TheoryData<string> ScriptSteps() => new(ImageBuilder.StepNames(ImageSource.Scripts));

    [Theory]
    [MemberData(nameof(ScriptSteps))]
    public async Task A_script_build_failure_at_each_step_removes_its_own_resources(string step)
    {
        _world.Connector.QueryHandler = sql => sql.Contains("user_access_desc FROM", StringComparison.Ordinal) ? [["ONLINE", false, "MULTI_USER"]] : [];
        var builder = new ImageBuilder(_world.Runtime, _world.Connector, _world.Redactor)
        {
            AfterStep = (name, _) => name == step ? throw new InvalidOperationException("injected") : Task.CompletedTask,
        };
        var pool = await new PoolFinder(_world.Runtime, ManagedPaths.Default, _ => true).RequireAsync(
            _world.Services().LoadSettings(null), "/home/u/.sqlbranch/pool", CancellationToken.None);
        var request = new ImageBuildRequest("shop", string.Empty, 0)
        {
            SourceScripts = [new ImageScript(new ScriptFile("schema.sql", new string('a', 64)), [new ScriptBatch(1, "SELECT 1;")])],
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(pool, 14330, request, CancellationToken.None));
        Assert.Equal("injected", ex.Message);
        var steps = ImageBuilder.StepNames(ImageSource.Scripts).ToList();
        bool Reached(string name) => steps.IndexOf(step) >= steps.IndexOf(name);
        Assert.Equal(Reached("create-database"), Ran("rm -rf '/pool/build/"));
        Assert.Equal(Reached("run-scripts"), Ran("DROP LOGIN"));
        Assert.Equal(Reached("create-database") && !Reached("detach"), Ran("DROP DATABASE"));
        Assert.Equal(Reached("seal"), Ran("rm -rf '/pool/images/shop'"));
        if (Ran("DROP DATABASE"))
        {
            Assert.True(IndexOf("SET SINGLE_USER") < IndexOf("DROP DATABASE"));
            Assert.True(IndexOf("DROP DATABASE") < IndexOf("rm -rf '/pool/build/"));
            if (Ran("DROP LOGIN"))
            {
                Assert.True(IndexOf("DROP DATABASE") < IndexOf("DROP LOGIN"));
            }
        }
    }

    [Fact]
    public void The_step_names_are_the_ones_the_pipeline_runs()
    {
        var backup = ImageBuilder.StepNames(ImageSource.Backup);
        var scripts = ImageBuilder.StepNames(ImageSource.Scripts);
        Assert.Equal(14, backup.Count);
        Assert.Equal(16, ImageBuilder.StepNames(ImageSource.Url).Count);
        Assert.Equal(backup.Skip(6), ImageBuilder.StepNames(ImageSource.Url).Skip(8));
        Assert.Equal(["check-name", "check-scripts", "create-database", "run-scripts"], scripts.Take(4));
        Assert.Equal(backup.Skip(6), scripts.Skip(4));
        Assert.Equal(ImageBuilder.StepNames(ImageSource.Backup).Count, ImageBuilder.StepNames(ImageSource.Backup).Distinct().Count());
    }

    [Theory]
    [InlineData(@"C:\Data\Shop.mdf", "Shop.mdf")]
    [InlineData("/var/opt/mssql/data/shop_log.ldf", "shop_log.ldf")]
    [InlineData("/x/we ird;name$.mdf", "we_ird_name_.mdf")]
    [InlineData("/x/.hidden", "hidden")]
    [InlineData("/x/", "file0")]
    public void File_names_are_safe_for_the_shell_and_keep_the_source_name_where_possible(string physical, string expected)
    {
        Assert.Equal(expected, ImageBuilder.UniqueFileName(physical, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Two_files_with_one_name_in_different_folders_get_different_names()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = ImageBuilder.UniqueFileName("/a/data.ndf", 1, used);
        var second = ImageBuilder.UniqueFileName("/b/DATA.ndf", 2, used);

        Assert.Equal("data.ndf", first);
        Assert.Equal("2_DATA.ndf", second);
    }
}
