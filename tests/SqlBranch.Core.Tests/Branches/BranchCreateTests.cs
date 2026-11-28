using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

/// <summary>
/// <c>branch create</c> end to end on fakes: the container runtime, the engine's answers and the scripts that run
/// inside the engine. Nothing here reaches the real docker.
/// </summary>
public sealed partial class BranchCreateTests
{
    private readonly CliWorld _world = new();

    /// <summary>Everything that happened, in order: <c>sh</c> scripts in the engine and <c>sql</c> statements.</summary>
    private readonly List<string> _timeline = [];

    private bool _imageExists = true;

    /// <summary>What the engine says about <c>catalog.json</c> of the image: <c>none</c> or the file as base64.</summary>
    private string _catalogFile = "none";
    private long _freeBytes = 1L << 40;
    private string _branchState = "none";
    private int _userDatabases;
    private int _databaseFiles;
    private Func<string, bool>? _failScript;
    private Action<string>? _onStatement;

    private ImageManifest _manifest = new(
        ImageManifest.CurrentFormat, "shop", "Shop", "16.0.4125.3",
        [new ImageFile("Shop", "Shop.mdf", "data", 8L << 20), new ImageFile("Shop_log", "Shop_log.ldf", "log", 524_288)],
        "FULL", "SIMPLE", 268_435_456, LogRebuilt: true, new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero),
        "shop.bak", new string('a', 64), new Dictionary<string, string>(), CliWorld.EngineImage);

    public BranchCreateTests()
    {
        _world.WithEngine();

        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal))
            {
                _timeline.Add("sh: " + script);
                return inner(container, command);
            }

            _timeline.Add("sh: " + script);
            if (_failScript?.Invoke(script) == true)
            {
                return FakeContainerRuntime.Fail(1, "boom");
            }

            // A catalog the command wrote is the catalog a later read finds (adoption reads the image again).
            if (script.Contains("catalog.json.new", StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.Match(script, "printf %s '([A-Za-z0-9+/=]+)' \\| base64 -d > '/pool/images/[^']+/catalog\\.json\\.new'") is { Success: true } written)
            {
                _catalogFile = written.Groups[1].Value;
            }

            if (script.Contains("SQLBRANCH_CATALOGFILE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_CATALOGFILE=" + _catalogFile);
            }

            if (script.Contains("SQLBRANCH_MANIFEST=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_MANIFEST=" + (_imageExists ? Convert.ToBase64String(Encoding.UTF8.GetBytes(_manifest.ToJson())) : "none"));
            }

            if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_FREE=" + _freeBytes);
            }

            if (script.Contains("SQLBRANCH_BRANCH=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_BRANCH=" + _branchState);
            }

            return FakeContainerRuntime.Ok();
        };

        var connector = _world.Connector;
        connector.ExecuteHandler = sql =>
        {
            _timeline.Add("sql: " + sql);
            _onStatement?.Invoke(sql);
        };
        connector.ParameterizedScalarHandler = (sql, _) =>
        {
            _timeline.Add("sql: " + sql);
            return sql.Contains("sys.databases", StringComparison.Ordinal) ? _userDatabases
                : sql.Contains("sys.master_files", StringComparison.Ordinal) ? _databaseFiles
                : 1;
        };
    }

    private (int Exit, JsonElement Json, string Out, string Err) Create(params string[] args)
    {
        var run = _world.Run(["branch", "create", .. (args.Length == 0 ? ["pr-142", "--from", "shop"] : args), "--format", "json"]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement, run.Out, run.Err);
    }

    private static string ErrorCode(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString()!;

    private static string ErrorMessage(JsonElement json) => json.GetProperty("error").GetProperty("message").GetString()!;

    private int IndexOf(string marker) => _timeline.FindIndex(l => l.Contains(marker, StringComparison.Ordinal));

    private bool Ran(string marker) => IndexOf(marker) >= 0;

    private IEnumerable<string> Removed() => _timeline.Where(l => l.StartsWith("sh: rm -rf", StringComparison.Ordinal));

    [GeneratedRegex(@"printf %s '(?<b64>[^']+)'")]
    private static partial Regex RecordScript();

    private BranchRecord WrittenRecord()
    {
        var script = _timeline.Single(l => l.Contains("base64 -d > '/pool/branches/", StringComparison.Ordinal));
        var b64 = RecordScript().Match(script).Groups["b64"].Value;
        return BranchRecord.FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("30m", 30)]
    public void Ephemeral_create_records_the_expiry_and_last_value_for_each_label(string? ttl, int minutes)
    {
        _world.Clock = new FixedClock();
        var args = new List<string> { "pr-142", "--from", "shop", "--ephemeral", "--label", "run=1", "--label", "team=web", "--label", "run=17" };
        if (ttl is not null)
        {
            args.AddRange(["--ttl", ttl]);
        }

        var run = Create([.. args]);
        Assert.Equal(0, run.Exit);
        var record = WrittenRecord();
        Assert.True(record.Ephemeral);
        Assert.Equal(_world.Clock.GetUtcNow().AddMinutes(minutes), record.ExpiresAt);
        Assert.Equal("17", record.Labels["run"]);
        Assert.Equal("web", record.Labels["team"]);
        Assert.Equal(record.ExpiresAt, run.Json.GetProperty("branch").GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("--ttl", "30m", false)]
    [InlineData("--ttl", "30x", true)]
    [InlineData("--ttl", "0", true)]
    [InlineData("--ttl", "-5m", true)]
    [InlineData("--ttl", "366d", true)]
    [InlineData("--label", "run17", false)]
    [InlineData("--label", "Run Id=17", false)]
    [InlineData("--label", "sqlbranch.owner=testing", false)]
    public void Invalid_create_options_fail_before_accessing_the_engine(string option, string value, bool ephemeral)
    {
        string[] flags = ephemeral ? ["--ephemeral"] : [];
        var run = Create(["pr-142", "--from", "shop", .. flags, option, value]);
        Assert.Equal(2, run.Exit);
        Assert.Equal("usage_error", ErrorCode(run.Json));
        Assert.Contains(option, ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Empty(_timeline);
    }

    // ---- 6.1: the create ----

    [Fact]
    public void A_create_in_thin_mode_clones_with_reflink_attaches_and_reports_how_to_connect_without_the_password()
    {
        var run = Create();

        Assert.Equal(0, run.Exit);
        var branch = run.Json.GetProperty("branch");
        Assert.Equal("pr-142", branch.GetProperty("name").GetString());
        Assert.Equal("shop", branch.GetProperty("image").GetString());
        Assert.Equal("thin", branch.GetProperty("cloneMode").GetString());
        Assert.Equal("127.0.0.1,14330", branch.GetProperty("server").GetString());
        Assert.Equal("pr-142", branch.GetProperty("database").GetString());
        Assert.Equal("sa", branch.GetProperty("user").GetString());
        Assert.DoesNotContain(CliWorld.StoredPassword, run.Out + run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(_timeline, l => l.Contains(CliWorld.StoredPassword, StringComparison.Ordinal));

        var clone = _timeline.Single(l => l.StartsWith("sh: set -e; mkdir '/pool/branches/pr-142/data'", StringComparison.Ordinal));
        Assert.Contains("cp --reflink=always -- '/pool/images/shop/data/Shop.mdf' '/pool/branches/pr-142/data/Shop.mdf'; chmod 640 '/pool/branches/pr-142/data/Shop.mdf'", clone, StringComparison.Ordinal);
        Assert.Contains("cp --reflink=always -- '/pool/images/shop/data/Shop_log.ldf' '/pool/branches/pr-142/data/Shop_log.ldf'", clone, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE DATABASE [pr-142] ON (FILENAME = N'/pool/branches/pr-142/data/Shop.mdf'), (FILENAME = N'/pool/branches/pr-142/data/Shop_log.ldf') FOR ATTACH;",
            _timeline.Single(l => l.Contains("FOR ATTACH", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Empty(Removed());
    }

    [Fact]
    public void The_text_output_shows_the_clone_mode_and_how_to_get_the_connection_string_but_never_the_password()
    {
        var run = _world.Run("branch", "create", "pr-142", "--from", "shop");

        Assert.Equal(0, run.Exit);
        Assert.Contains("Clone mode: thin", run.Out, StringComparison.Ordinal);
        Assert.Contains("sqlbranch branch conn pr-142", run.Out, StringComparison.Ordinal);
        Assert.Contains("Server: 127.0.0.1,14330", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(CliWorld.StoredPassword, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_create_in_copy_mode_uses_a_plain_cp_and_reports_copy()
    {
        _world.Host.EngineProbe = "copy";

        var run = Create();

        Assert.Equal(0, run.Exit);
        Assert.Equal("copy", run.Json.GetProperty("branch").GetProperty("cloneMode").GetString());
        var clone = _timeline.Single(l => l.StartsWith("sh: set -e; mkdir '/pool/branches/pr-142/data'", StringComparison.Ordinal));
        Assert.Contains("cp -- '/pool/images/shop/data/Shop.mdf'", clone, StringComparison.Ordinal);
        Assert.DoesNotContain("--reflink", clone, StringComparison.Ordinal);
        Assert.Equal("copy", WrittenRecord().CloneMode);
        Assert.False(WrittenRecord().ExplicitCopy, "a copy pool without --copy is not an explicit copy");
    }

    [Fact]
    public void A_create_with_the_copy_flag_records_explicit_copy_and_one_without_it_leaves_the_field_out()
    {
        Create("pr-142", "--from", "shop", "--copy");
        var script = _timeline.Single(l => l.Contains("base64 -d", StringComparison.Ordinal));
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(RecordScript().Match(script).Groups["b64"].Value));
        Assert.True(WrittenRecord().ExplicitCopy);
        Assert.Contains("\"explicitCopy\": true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_names_the_image_the_mode_the_time_and_has_a_labels_map_in_camel_case()
    {
        Create();

        var script = _timeline.Single(l => l.Contains("base64 -d", StringComparison.Ordinal));
        Assert.Contains("> '/pool/branches/pr-142/branch.json.new'", script, StringComparison.Ordinal);
        Assert.Contains("mv -f '/pool/branches/pr-142/branch.json.new' '/pool/branches/pr-142/branch.json'", script, StringComparison.Ordinal);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(RecordScript().Match(script).Groups["b64"].Value))).RootElement;
        Assert.Equal("shop", json.GetProperty("image").GetString());
        Assert.Equal("thin", json.GetProperty("cloneMode").GetString());
        Assert.True(json.TryGetProperty("createdAt", out var created));
        Assert.True(DateTimeOffset.TryParse(created.GetString(), out _));
        Assert.Equal(JsonValueKind.Object, json.GetProperty("labels").ValueKind);
    }

    [Fact]
    public void The_branches_folder_is_made_for_the_engines_user_only_when_it_is_missing_and_the_branch_folder_with_mkdir_without_p()
    {
        Create();

        var folder = _timeline.Single(l => l.Contains("mkdir '/pool/branches/pr-142'", StringComparison.Ordinal));
        Assert.Contains("if [ ! -d '/pool/branches' ]; then mkdir -p '/pool/branches' && if [ \"$(id -u)\" = 0 ]; then chown 10001:0 '/pool/branches'; fi; fi", folder, StringComparison.Ordinal);
        Assert.DoesNotContain("mkdir -p '/pool/branches/pr-142'", folder, StringComparison.Ordinal);
    }

    [Fact]
    public void The_steps_run_in_order_check_folder_clone_record_attach()
    {
        Create();

        var order = new[] { "SQLBRANCH_MANIFEST", "SQLBRANCH_BRANCH", "sys.databases", "SQLBRANCH_PROBE", "mkdir '/pool/branches/pr-142'", "set -e; mkdir '/pool/branches/pr-142/data'", "base64 -d", "FOR ATTACH" };
        var position = -1;
        foreach (var marker in order)
        {
            var next = _timeline.FindIndex(position + 1, l => l.Contains(marker, StringComparison.Ordinal));
            Assert.True(next > position, $"'{marker}' did not come after {position} in:\n{string.Join('\n', _timeline)}");
            position = next;
        }
    }

    [Fact]
    public void The_create_holds_the_branch_lock_and_the_image_lock_and_the_probe_has_a_folder_of_its_own()
    {
        Create();

        Assert.Equal(["sqlbranch:branch:pr-142", "sqlbranch:image:shop"], _world.Connector.TakenLocks);
        Assert.Contains(_world.Connector.Statements, s => s == EngineLocks.TakeSharedSql);
        var probe = _timeline.Single(l => l.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal));
        Assert.Matches(@"/pool/\.probe-[0-9a-f]{12}/a", probe);
    }

    [Theory]
    [InlineData("master", "reserved")]
    [InlineData("tempdb", "reserved")]
    [InlineData("sqlbranch_build_x", "sqlbranch_")]
    [InlineData("../images/shop", "Names are")]
    [InlineData("Upper", "Names are")]
    public void A_bad_branch_name_exits_2_before_anything_is_connected_or_touched(string name, string said)
    {
        var run = Create(name, "--from", "shop");

        Assert.Equal(2, run.Exit);
        Assert.Equal("invalid_name", ErrorCode(run.Json));
        Assert.Contains(said, ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Empty(_timeline);
        Assert.Empty(_world.Connector.Attempts);
    }

    [Fact]
    public void A_bad_name_exits_2_even_before_init_and_a_good_one_exits_3()
    {
        var empty = new CliWorld();

        var bad = empty.Run("branch", "create", "master", "--from", "shop", "--format", "json");
        var badImage = empty.Run("branch", "create", "ok", "--from", "../x", "--format", "json");
        var good = empty.Run("branch", "create", "ok", "--from", "shop", "--format", "json");

        Assert.Equal(2, bad.Exit);
        Assert.Equal(2, badImage.Exit);
        Assert.Equal(3, good.Exit);
    }

    [Fact]
    public void An_unknown_image_exits_1_image_not_found_and_nothing_is_made()
    {
        _imageExists = false;

        var run = Create("pr-142", "--from", "missing");

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_not_found", ErrorCode(run.Json));
        Assert.Contains("missing", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Ran("mkdir '/pool/branches"));
        Assert.Empty(Removed());
    }

    [Fact]
    public void A_name_used_by_a_user_database_exits_1_branch_exists_and_the_database_is_not_touched()
    {
        _userDatabases = 1;

        var run = Create("scratch", "--from", "shop");

        Assert.Equal(1, run.Exit);
        Assert.Equal("branch_exists", ErrorCode(run.Json));
        Assert.Contains("database named 'scratch' already exists", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Ran("mkdir '/pool/branches"));
        Assert.DoesNotContain(_timeline, l => l.Contains("DROP DATABASE", StringComparison.Ordinal) || l.Contains("SINGLE_USER", StringComparison.Ordinal));
        Assert.Empty(Removed());
    }

    [Fact]
    public void An_existing_branch_exits_1_says_so_and_nothing_of_it_is_changed()
    {
        _branchState = "branch";
        _userDatabases = 1;

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Equal("branch_exists", ErrorCode(run.Json));
        Assert.Contains("branch 'pr-142' already exists", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Ran("mkdir '/pool/branches/pr-142'"));
        Assert.Empty(Removed());
        Assert.DoesNotContain(_timeline, l => l.Contains("DROP DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_folder_left_by_an_earlier_command_is_refused_and_never_deleted_by_this_create()
    {
        _branchState = "folder";

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Equal("branch_exists", ErrorCode(run.Json));
        Assert.Contains("/pool/branches/pr-142", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Empty(Removed());
    }

    [Fact]
    public void A_branch_lock_another_create_holds_makes_this_one_exit_1_before_it_looks_at_anything()
    {
        _world.Connector.HeldLocks.Add("sqlbranch:branch:pr-142");

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Equal("branch_exists", ErrorCode(run.Json));
        Assert.DoesNotContain(_timeline, l => l.StartsWith("sh:", StringComparison.Ordinal));
        Assert.Empty(_world.Connector.TakenLocks);
    }

    [Fact]
    public void An_image_that_a_removal_or_build_holds_makes_the_create_exit_1_and_releases_nothing_it_does_not_own()
    {
        _world.Connector.HeldLocks.Add("sqlbranch:image:shop");

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Contains("building or removing the image 'shop'", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Ran("mkdir '/pool/branches"));
        Assert.Empty(Removed());
    }

    [Fact]
    public void A_stopped_engine_exits_3_engine_stopped()
    {
        _world.Runtime.Containers[ContainerLabels.EngineName] = _world.Runtime.Containers[ContainerLabels.EngineName] with { Running = false };

        var run = Create();

        Assert.Equal(3, run.Exit);
        Assert.Equal("engine_stopped", ErrorCode(run.Json));
    }

    [Fact]
    public void A_manifest_file_name_with_a_slash_is_refused_before_any_file_is_cloned()
    {
        var evil = _manifest with { Files = [new ImageFile("Shop", "../../engine/credentials.json", "data", 1)] };
        _world.Runtime.ExecHandler = Wrap(_world.Runtime.ExecHandler, script =>
            script.Contains("SQLBRANCH_MANIFEST=", StringComparison.Ordinal)
                ? FakeContainerRuntime.Ok("SQLBRANCH_MANIFEST=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(evil.ToJson())))
                : null);

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Contains("not a plain file name", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain(_timeline, l => l.Contains("cp ", StringComparison.Ordinal) && l.Contains("credentials", StringComparison.Ordinal));
        Assert.Contains(Removed(), l => l.Contains("'/pool/branches/pr-142'", StringComparison.Ordinal));
    }

    private static Func<string, IReadOnlyList<string>, ContainerResult> Wrap(
        Func<string, IReadOnlyList<string>, ContainerResult> inner, Func<string, ContainerResult?> override_) =>
        (container, command) => override_(command.Count >= 3 ? command[2] : string.Empty) ?? inner(container, command);

    // ---- 6.2: a failed create leaves nothing behind ----

    [Fact]
    public void A_failed_copy_removes_the_branch_folder_and_never_touches_a_database()
    {
        _failScript = s => s.Contains("cp --reflink", StringComparison.Ordinal);

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Contains("cloning the image's files", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("boom", ErrorMessage(run.Json), StringComparison.Ordinal);
        var removed = Assert.Single(Removed());
        Assert.Contains("rm -rf '/pool/branches/pr-142'", removed, StringComparison.Ordinal);
        Assert.False(Ran("FOR ATTACH"));
        Assert.DoesNotContain(_timeline, l => l.Contains("DROP DATABASE", StringComparison.Ordinal));
        Assert.DoesNotContain(_timeline, l => l.Contains("sys.master_files", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_that_runs_the_pool_out_of_space_exits_1_and_no_folder_remains()
    {
        _world.Host.EngineProbe = "copy";
        _world.Runtime.ExecHandler = Wrap(_world.Runtime.ExecHandler, script =>
            script.StartsWith("set -e; mkdir '/pool/branches/pr-142/data'", StringComparison.Ordinal)
                ? FakeContainerRuntime.Fail(1, "cp: error writing '/pool/branches/pr-142/data/Shop.mdf': No space left on device")
                : null);

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Contains("No space left on device", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("rm -rf '/pool/branches/pr-142'", Assert.Single(Removed()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_attach_after_the_clone_removes_the_folder_and_drops_nothing_when_no_database_was_attached()
    {
        _onStatement = sql =>
        {
            if (sql.Contains("FOR ATTACH", StringComparison.Ordinal))
            {
                throw new EngineSqlException("Unable to open the physical file. Operating system error 5(Access is denied).", [5120]);
            }
        };

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Contains("Access is denied", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("rm -rf '/pool/branches/pr-142'", Assert.Single(Removed()), StringComparison.Ordinal);
        Assert.DoesNotContain(_timeline, l => l.Contains("DROP DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void An_attach_that_failed_after_the_database_came_up_is_dropped_before_its_folder_is_deleted()
    {
        _databaseFiles = 2;
        _onStatement = sql =>
        {
            if (sql.Contains("FOR ATTACH", StringComparison.Ordinal))
            {
                _world.Connector.Databases["pr-142"] = ["/pool/branches/pr-142/data/Shop.mdf"];
                throw new TimeoutException("The connection was lost after the attach.");
            }
        };

        var run = Create();

        Assert.Equal(1, run.Exit);
        var single = IndexOf("ALTER DATABASE [pr-142] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        var drop = IndexOf("DROP DATABASE [pr-142];");
        var remove = IndexOf("rm -rf '/pool/branches/pr-142'");
        Assert.True(single >= 0 && single < drop && drop < remove, string.Join('\n', _timeline));
        Assert.DoesNotContain("pr-142", _world.Connector.Databases.Keys);
    }

    [Fact]
    public void A_database_with_the_branchs_name_that_has_no_file_in_its_folder_is_never_dropped()
    {
        _databaseFiles = 0;
        _onStatement = sql =>
        {
            if (sql.Contains("FOR ATTACH", StringComparison.Ordinal))
            {
                throw new EngineSqlException("Database 'pr-142' already exists.", [1801]);
            }
        };
        _world.Connector.Databases["pr-142"] = ["/var/opt/mssql/data/pr-142.mdf"];

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.DoesNotContain(_timeline, l => l.Contains("DROP DATABASE", StringComparison.Ordinal) || l.Contains("SINGLE_USER", StringComparison.Ordinal));
        Assert.Contains("pr-142", _world.Connector.Databases.Keys);
    }

    [Fact]
    public void A_cleanup_that_cannot_drop_keeps_the_folder_and_says_what_is_left()
    {
        _databaseFiles = 2;
        _world.Connector.FailDrop.Add("pr-142");
        _onStatement = sql =>
        {
            if (sql.Contains("FOR ATTACH", StringComparison.Ordinal))
            {
                throw new EngineSqlException("attach failed", [1]);
            }
        };

        var run = Create();

        Assert.Equal(1, run.Exit);
        var message = ErrorMessage(run.Json);
        Assert.Contains("attach failed", message, StringComparison.Ordinal);
        Assert.Contains("the database pr-142", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch status", message, StringComparison.Ordinal);
        Assert.Empty(Removed());
    }

    [Fact]
    public void A_folder_that_cannot_be_removed_after_a_failure_is_named_in_the_error_and_keeps_the_original_code()
    {
        _failScript = s => s.Contains("cp --reflink", StringComparison.Ordinal) || s.StartsWith("rm -rf", StringComparison.Ordinal);

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Contains("could not remove /pool/branches/pr-142", ErrorMessage(run.Json), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_the_create_did_not_make_is_never_deleted_when_the_mkdir_fails()
    {
        _failScript = s => s.Contains("mkdir '/pool/branches/pr-142'", StringComparison.Ordinal) && !s.StartsWith("set -e", StringComparison.Ordinal);

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Contains("creating the branch folder", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Empty(Removed());
    }

    public static TheoryData<string> AllSteps()
    {
        var data = new TheoryData<string>();
        foreach (var step in BranchCreator.StepNames)
        {
            data.Add(step);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSteps))]
    public async Task A_failure_injected_after_any_step_undoes_exactly_what_the_create_had_made_by_then(string step)
    {
        var steps = BranchCreator.StepNames.ToList();
        bool Reached(string name) => steps.IndexOf(step) >= steps.IndexOf(name);
        _databaseFiles = 2;
        var creator = new BranchCreator(_world.Runtime, _world.Connector, _world.Redactor)
        {
            AfterStep = (name, _) => name == step ? throw new InvalidOperationException("injected") : Task.CompletedTask,
        };
        var pool = await new PoolFinder(_world.Runtime, ManagedPaths.Default, _ => true).RequireAsync(
            _world.Services().LoadSettings(null), "/home/u/.sqlbranch/pool", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            creator.CreateAsync(pool, 14330, "pr-142", "shop", CancellationToken.None));

        Assert.Equal("injected", ex.Message);

        // The folder exists from the folder step on, and then it is removed again; before it nothing is removed.
        Assert.Equal(Reached("folder"), Removed().Any(l => l.Contains("rm -rf '/pool/branches/pr-142'", StringComparison.Ordinal)));

        // The database is dropped only when the attach had been started.
        Assert.Equal(Reached("attach"), Ran("DROP DATABASE [pr-142]"));

        // Whatever was removed was removed after everything that created it.
        if (Ran("DROP DATABASE") && Ran("rm -rf '/pool/branches"))
        {
            Assert.True(IndexOf("DROP DATABASE") < IndexOf("rm -rf '/pool/branches"));
        }
    }

    // ---- the lock helper and the probe ----

    [Fact]
    public async Task A_shared_lock_is_taken_in_shared_mode_with_the_resource_as_a_parameter()
    {
        var connector = new FakeEngineConnector { SaPassword = "p" };
        await using var session = await connector.ConnectAsync(EngineEndpoint.Loopback(1), "p", CancellationToken.None);

        Assert.True(await EngineLocks.TryTakeSharedAsync(session, "sqlbranch:image:x'; DROP DATABASE master; --", CancellationToken.None));

        var statement = Assert.Single(connector.Statements);
        Assert.Contains("@LockMode = 'Shared'", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP DATABASE", statement, StringComparison.Ordinal);
        connector.HeldLocks.Add("held");
        Assert.False(await EngineLocks.TryTakeSharedAsync(session, "held", CancellationToken.None));
    }

    [Fact]
    public async Task A_probe_with_an_id_works_in_a_folder_of_its_own_and_an_id_with_other_characters_is_refused()
    {
        string? script = null;
        _world.Runtime.ExecHandler = (_, command) =>
        {
            script = command[2];
            return FakeContainerRuntime.Ok("SQLBRANCH_PROBE=thin");
        };

        var mode = await CloneProbe.ProbeInEngineAsync(_world.Runtime, CancellationToken.None, ContainerLabels.EngineName, "ab12");

        Assert.Equal(CloneMode.Thin, mode);
        Assert.Contains("mkdir -p /pool/.probe-ab12 &&", script, StringComparison.Ordinal);
        Assert.Contains("rm -rf /pool/.probe-ab12", script, StringComparison.Ordinal);
        Assert.Equal(CloneProbe.ScriptFor("/pool/.probe"), CloneProbe.Script);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CloneProbe.ProbeInEngineAsync(_world.Runtime, CancellationToken.None, ContainerLabels.EngineName, "x; rm -rf /"));
    }
}
