using System.Text.Json;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

/// <summary>Tasks 6.10 (the guard) and 6.11: <c>branch reset</c> on fakes. Nothing here reaches the real docker.</summary>
public sealed class BranchResetTests
{
    private const long GiB = 1L << 30;

    private readonly BranchWorld _w = new();

    private (int Exit, JsonElement Json, string Out, string Err) Reset(params string[] args)
    {
        var run = _w.Run(["branch", "reset", .. (args.Length == 0 ? ["pr-142"] : args), "--format", "json"]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement, run.Out, run.Err);
    }

    [Fact]
    public void Legacy_record_defaults_to_a_permanent_branch()
    {
        var record = BranchRecord.FromJson("""{"image":"shop","cloneMode":"thin","createdAt":"2026-11-17T10:00:00Z","labels":{}}""");
        Assert.False(record.Ephemeral);
        Assert.Null(record.ExpiresAt);
        Assert.Empty(record.Labels);
    }

    [Fact]
    public void Reset_preserves_expiry_and_labels()
    {
        _w.AddBranch("pr-142");
        var original = _w.RecordOf("pr-142");
        var expiry = original.CreatedAt.AddMinutes(30);
        _w.Records["pr-142"] = (original with
        {
            Ephemeral = true,
            ExpiresAt = expiry,
            Labels = new Dictionary<string, string> { ["run"] = "17" },
        }).ToJson();

        Assert.Equal(0, Reset().Exit);
        var reset = _w.RecordOf("pr-142");
        Assert.True(reset.Ephemeral);
        Assert.Equal(expiry, reset.ExpiresAt);
        Assert.Equal("17", reset.Labels["run"]);
    }

    private string ListedState(string name)
    {
        var list = JsonDocument.Parse(_w.Run("branch", "list", "--format", "json").Out).RootElement;
        return list.GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("name").GetString() == name).GetProperty("state").GetString()!;
    }

    private static string ErrorCode(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString()!;

    private static string ErrorMessage(JsonElement json) => json.GetProperty("error").GetProperty("message").GetString()!;

    private bool Dropped() => _w.Ran("DROP DATABASE");

    private bool Cloned() => _w.Ran("/pool/branches/pr-142/next/");

    private bool Changed() => Dropped() || Cloned() || _w.Ran("mv -T") || _w.Ran("FOR ATTACH") || _w.Ran("base64 -d >");

    private void Fail(Func<string, bool> when) => _w.FailScript = when;

    private void FailAttach() => _w.OnStatement = sql =>
    {
        if (sql.StartsWith("CREATE DATABASE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The attach failed.");
        }
    };

    // ---- 6.11: the reset in the order of D5 ----

    [Fact]
    public void A_thin_reset_checks_the_image_clones_into_next_drops_with_single_user_moves_next_into_place_and_attaches_in_that_order()
    {
        _w.AddBranch("pr-142");

        var run = Reset();

        Assert.Equal(0, run.Exit);
        Assert.Equal("branch reset", run.Json.GetProperty("command").GetString());
        var branch = run.Json.GetProperty("branch");
        Assert.Equal("pr-142", branch.GetProperty("name").GetString());
        Assert.Equal("shop", branch.GetProperty("image").GetString());
        Assert.Equal("thin", branch.GetProperty("cloneMode").GetString());
        Assert.Equal("127.0.0.1,14330", branch.GetProperty("server").GetString());

        var steps = new[]
        {
            "SQLBRANCH_MANIFEST=",
            "SQLBRANCH_PROBE",
            "set -e; rm -rf '/pool/branches/pr-142/next'; mkdir '/pool/branches/pr-142/next'; cp --reflink=always -- '/pool/images/shop/data/Shop.mdf' '/pool/branches/pr-142/next/Shop.mdf'; chmod 640 '/pool/branches/pr-142/next/Shop.mdf';",
            "ALTER DATABASE [pr-142] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;",
            "DROP DATABASE [pr-142];",
            "rm -rf '/pool/branches/pr-142/data' && mv -T '/pool/branches/pr-142/next' '/pool/branches/pr-142/data'",
            "CREATE DATABASE [pr-142] ON (FILENAME = N'/pool/branches/pr-142/data/Shop.mdf'), (FILENAME = N'/pool/branches/pr-142/data/Shop_log.ldf') FOR ATTACH;",
        };
        var indexes = steps.Select(_w.IndexOf).ToArray();
        Assert.All(indexes, i => Assert.True(i >= 0, string.Join('\n', _w.Timeline)));
        Assert.Equal(indexes.Order().ToArray(), indexes);
        Assert.Contains("sqlbranch:branch:pr-142", _w.World.Connector.TakenLocks);
        Assert.Contains("sqlbranch:image:shop", _w.World.Connector.TakenLocks);
        Assert.Equal(["/pool/branches/pr-142/data/Shop.mdf", "/pool/branches/pr-142/data/Shop_log.ldf"], _w.World.Connector.Databases["pr-142"]);
    }

    [Fact]
    public void The_record_keeps_its_image_mode_and_time_and_has_no_state_afterwards_and_no_password_is_printed()
    {
        _w.AddBranch("pr-142", image: "shop", state: null);
        var before = _w.RecordOf("pr-142");

        var run = Reset();

        Assert.Equal(0, run.Exit);
        var after = _w.RecordOf("pr-142");
        Assert.Equal(before.Image, after.Image);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal("thin", after.CloneMode);
        Assert.Null(after.State);
        Assert.DoesNotContain("\"state\"", _w.Records["pr-142"], StringComparison.Ordinal);
        Assert.DoesNotContain(CliWorld.StoredPassword, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_output_shows_the_clone_mode_and_says_the_connection_string_is_unchanged()
    {
        _w.AddBranch("pr-142");

        var run = _w.Run("branch", "reset", "pr-142");

        Assert.Equal(0, run.Exit);
        Assert.Contains("Clone mode: thin", run.Out, StringComparison.Ordinal);
        Assert.Contains("unchanged", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reset_when_the_image_is_gone_exits_1_says_so_and_leaves_the_branch_online_and_unchanged()
    {
        _w.AddBranch("pr-142");
        _w.ImageExists = false;
        var record = _w.Records["pr-142"];

        var run = Reset();

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_not_found", ErrorCode(run.Json));
        Assert.Contains("no longer exists", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Changed(), string.Join('\n', _w.Timeline));
        Assert.Equal(record, _w.Records["pr-142"]);
        Assert.Equal("online", ListedState("pr-142"));
    }

    [Fact]
    public void A_reset_of_a_copy_branch_of_a_10_GiB_image_with_4_GiB_free_exits_1_pool_full_and_the_branch_is_unchanged()
    {
        _w.AddBranch("bench1", mode: "copy");
        _w.ImageOf(10 * GiB);
        _w.FreeBytes = 4 * GiB;
        var record = _w.Records["bench1"];

        var run = Reset("bench1");

        Assert.Equal(1, run.Exit);
        Assert.Equal("pool_full", ErrorCode(run.Json));
        Assert.Contains("10 GiB", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("4 GiB", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(_w.Ran("/pool/branches/bench1/next"), string.Join('\n', _w.Timeline));
        Assert.False(Dropped());
        Assert.Equal(record, _w.Records["bench1"]);
        Assert.Equal("online", ListedState("bench1"));
    }

    [Fact]
    public void A_thin_branch_on_a_thin_pool_needs_no_room_check_because_it_shares_the_images_blocks()
    {
        _w.AddBranch("pr-142");
        _w.ImageOf(10 * GiB);
        _w.FreeBytes = 1;

        Assert.Equal(0, Reset().Exit);
        Assert.False(_w.Ran("SQLBRANCH_FREE="));
    }

    [Fact]
    public void A_copy_branch_stays_a_copy_on_a_thin_pool_with_plain_cp_and_a_copy_record()
    {
        _w.AddBranch("bench1", mode: "copy");

        var run = Reset("bench1");

        Assert.Equal(0, run.Exit);
        Assert.Equal("copy", run.Json.GetProperty("branch").GetProperty("cloneMode").GetString());
        var clone = _w.Timeline.Single(l => l.Contains("mkdir '/pool/branches/bench1/next'", StringComparison.Ordinal));
        Assert.Contains("cp -- '/pool/images/shop/data/Shop.mdf'", clone, StringComparison.Ordinal);
        Assert.DoesNotContain("--reflink", clone, StringComparison.Ordinal);
        Assert.Equal("copy", _w.RecordOf("bench1").CloneMode);
        Assert.True(_w.Ran("SQLBRANCH_FREE="));
    }

    [Fact]
    public void A_reset_in_a_copy_mode_pool_is_a_copy_and_checks_the_room()
    {
        _w.World.Host.EngineProbe = "copy";
        _w.AddBranch("pr-142");

        var run = Reset();

        Assert.Equal(0, run.Exit);
        Assert.Equal("copy", run.Json.GetProperty("branch").GetProperty("cloneMode").GetString());
        Assert.True(_w.Ran("SQLBRANCH_FREE="));
        Assert.Equal("copy", _w.RecordOf("pr-142").CloneMode);
    }

    [Fact]
    public void RequireThin_refuses_a_reset_of_a_thin_branch_in_a_copy_pool_with_exit_3_and_changes_nothing()
    {
        _w.World.Host.EngineProbe = "copy";
        _w.World.Environment["SQLBRANCH_REQUIRE_THIN"] = "true";
        _w.AddBranch("pr-142");

        var run = Reset();

        Assert.Equal(3, run.Exit);
        Assert.Equal("thin_required", ErrorCode(run.Json));
        Assert.False(Changed());
        Assert.False(_w.Ran("SQLBRANCH_FREE="));
    }

    [Fact]
    public void RequireThin_does_not_apply_to_a_branch_that_was_made_as_a_copy()
    {
        _w.World.Host.EngineProbe = "copy";
        _w.World.Environment["SQLBRANCH_REQUIRE_THIN"] = "true";
        _w.AddBranch("bench1", mode: "copy", explicitCopy: true);

        Assert.Equal(0, Reset("bench1").Exit);
        Assert.True(_w.RecordOf("bench1").ExplicitCopy);
    }

    [Fact]
    public void RequireThin_refuses_a_reset_of_a_copy_branch_that_was_not_made_with_the_copy_flag_because_its_pool_was_in_copy_mode()
    {
        _w.World.Host.EngineProbe = "copy";
        _w.World.Environment["SQLBRANCH_REQUIRE_THIN"] = "true";
        _w.AddBranch("pr-142", mode: "copy");

        var run = Reset();

        Assert.Equal(3, run.Exit);
        Assert.Equal("thin_required", ErrorCode(run.Json));
        Assert.False(Changed());
    }

    [Fact]
    public void A_record_without_the_explicit_copy_field_reads_as_false()
    {
        var json = "{\"image\":\"shop\",\"cloneMode\":\"copy\",\"createdAt\":\"2026-11-17T09:30:00+00:00\",\"labels\":{}}";

        Assert.False(BranchRecord.FromJson(json).ExplicitCopy);
        Assert.DoesNotContain("explicitCopy", BranchRecord.FromJson(json).ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_reset_of_an_offline_branch_drops_it_without_single_user_and_attaches_a_fresh_online_one()
    {
        _w.AddBranch("pr-142");
        _w.States["pr-142"] = "OFFLINE";

        var run = Reset();

        Assert.Equal(0, run.Exit);
        Assert.True(Dropped());
        Assert.False(_w.Ran("SINGLE_USER"));
        Assert.True(_w.Ran("FOR ATTACH"));
        Assert.Null(_w.RecordOf("pr-142").State);
    }

    [Fact]
    public void A_record_that_cannot_be_written_after_a_good_attach_says_so_and_does_not_claim_the_attach_failed()
    {
        _w.AddBranch("pr-142");
        var writes = 0;
        Fail(script => script.Contains("base64 -d >", StringComparison.Ordinal) && ++writes == 2);

        var run = Reset();

        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", ErrorCode(run.Json));
        Assert.Contains("attached", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("record", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain("could not be attached", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.True(_w.World.Connector.Databases.ContainsKey("pr-142"));
    }

    [Fact]
    public void A_clone_that_fails_removes_next_leaves_the_branch_unchanged_and_exits_1()
    {
        _w.AddBranch("pr-142");
        var record = _w.Records["pr-142"];
        Fail(script => script.Contains("cp --reflink", StringComparison.Ordinal));

        var run = Reset();

        Assert.Equal(1, run.Exit);
        Assert.False(Dropped());
        Assert.True(_w.Ran("rm -rf '/pool/branches/pr-142/next'"));
        Assert.Equal(record, _w.Records["pr-142"]);
        Assert.Equal("online", ListedState("pr-142"));
    }

    [Fact]
    public void A_drop_that_fails_removes_next_gives_the_connections_back_and_leaves_the_branch_online()
    {
        _w.AddBranch("pr-142");
        _w.World.Connector.FailDrop.Add("pr-142");
        var record = _w.Records["pr-142"];

        var run = Reset();

        Assert.Equal(1, run.Exit);
        Assert.True(_w.Ran("ALTER DATABASE [pr-142] SET MULTI_USER;"));
        Assert.Contains(_w.Timeline.Skip(_w.IndexOf("DROP DATABASE")), l => l.StartsWith("sh: rm -rf '/pool/branches/pr-142/next'", StringComparison.Ordinal));
        Assert.False(_w.Ran("mv -T"));
        Assert.Equal(record, _w.Records["pr-142"]);
        Assert.Equal("online", ListedState("pr-142"));
    }

    // ---- broken, and the retry ----

    [Fact]
    public void An_attach_that_fails_after_the_drop_exits_1_marks_the_record_broken_and_the_list_shows_broken()
    {
        _w.AddBranch("pr-142");
        FailAttach();

        var run = Reset();

        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", ErrorCode(run.Json));
        Assert.Contains("broken", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("sqlbranch branch reset pr-142", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Equal("broken", _w.RecordOf("pr-142").State);
        Assert.Equal("shop", _w.RecordOf("pr-142").Image);
        Assert.False(_w.World.Connector.Databases.ContainsKey("pr-142"));
        Assert.Equal("broken", ListedState("pr-142"));
    }

    [Fact]
    public void A_second_reset_retries_a_broken_branch_from_its_image_and_clears_the_state()
    {
        _w.AddBranch("pr-142");
        FailAttach();
        Assert.Equal(1, Reset().Exit);
        _w.OnStatement = null;
        _w.Timeline.Clear();

        var run = Reset();

        Assert.Equal(0, run.Exit);
        Assert.False(Dropped(), "there is no database to drop on the retry");
        Assert.True(_w.Ran("mv -T '/pool/branches/pr-142/next' '/pool/branches/pr-142/data'"));
        Assert.Null(_w.RecordOf("pr-142").State);
        Assert.DoesNotContain("broken", _w.Records["pr-142"], StringComparison.Ordinal);
        Assert.Equal("online", ListedState("pr-142"));
        Assert.True(_w.World.Connector.Databases.ContainsKey("pr-142"));
    }

    [Fact]
    public void A_branch_marked_broken_whose_database_is_still_there_is_reset_normally()
    {
        _w.AddBranch("pr-142", state: "broken");

        Assert.Equal(0, Reset().Exit);
        Assert.Null(_w.RecordOf("pr-142").State);
        Assert.True(Dropped());
    }

    [Fact]
    public void A_broken_branch_whose_image_is_gone_stays_broken_and_exits_1_image_not_found()
    {
        _w.AddBranchWithoutDatabase("pr-142", state: "broken");
        _w.ImageExists = false;

        var run = Reset();

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_not_found", ErrorCode(run.Json));
        Assert.Equal("broken", _w.RecordOf("pr-142").State);
    }

    [Fact]
    public void A_leftover_next_from_a_reset_that_died_is_replaced()
    {
        _w.AddBranch("pr-142");

        Assert.Equal(0, Reset().Exit);
        Assert.True(_w.Ran("rm -rf '/pool/branches/pr-142/next'; mkdir '/pool/branches/pr-142/next'"));
    }

    // ---- 6.10: the guard ----

    [Fact]
    public void A_database_a_user_made_has_no_record_so_reset_exits_1_says_it_is_not_a_branch_and_changes_nothing()
    {
        _w.World.Connector.Databases["scratch"] = ["/var/opt/mssql/data/scratch.mdf"];

        var run = Reset("scratch");

        Assert.Equal(1, run.Exit);
        Assert.Equal("branch_not_found", ErrorCode(run.Json));
        Assert.Contains("not a branch", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(_w.Ran("ALTER DATABASE"));
        Assert.False(_w.Ran("DROP DATABASE"));
        Assert.False(_w.Ran("mkdir"));
        Assert.True(_w.World.Connector.Databases.ContainsKey("scratch"));
    }

    [Fact]
    public void A_database_of_the_branchs_name_with_files_outside_its_folder_is_never_dropped()
    {
        _w.AddBranchWithoutDatabase("scratch");
        _w.World.Connector.Databases["scratch"] = ["/var/opt/mssql/data/scratch.mdf"];

        var run = Reset("scratch");

        Assert.Equal(1, run.Exit);
        Assert.Contains("not this branch's", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Changed());
        Assert.True(_w.World.Connector.Databases.ContainsKey("scratch"));
    }

    [Theory]
    [InlineData("../images/shop")]
    [InlineData("master")]
    [InlineData("sqlbranch_build_ab12")]
    [InlineData("Pr-1")]
    [InlineData("a/b")]
    public void A_name_that_breaks_the_rules_exits_2_before_any_sql_or_file_action(string name)
    {
        var run = Reset(name);

        Assert.Equal(2, run.Exit);
        Assert.Equal("invalid_name", ErrorCode(run.Json));
        Assert.Empty(_w.Timeline);
        Assert.Empty(_w.World.Connector.Attempts);
        Assert.Empty(_w.World.Runtime.Runs);
    }

    [Fact]
    public void A_reset_while_another_command_holds_the_branch_lock_exits_1_and_changes_nothing()
    {
        _w.AddBranch("pr-142");
        _w.World.Connector.HeldLocks.Add("sqlbranch:branch:pr-142");

        var run = Reset();

        Assert.Equal(1, run.Exit);
        Assert.False(Changed());
    }

    [Fact]
    public void A_reset_while_the_image_is_being_removed_exits_1_and_changes_nothing()
    {
        _w.AddBranch("pr-142");
        _w.World.Connector.HeldLocks.Add("sqlbranch:image:shop");

        var run = Reset();

        Assert.Equal(1, run.Exit);
        Assert.False(Changed());
    }

    [Fact]
    public void An_unreadable_record_exits_1_and_changes_nothing()
    {
        _w.AddBranch("pr-142");
        _w.Records["pr-142"] = "not json";

        var run = Reset();

        Assert.Equal(1, run.Exit);
        Assert.Contains("cannot be read", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Dropped());
    }

    [Fact]
    public void Before_init_reset_exits_3_and_a_bad_name_still_exits_2()
    {
        var world = new CliWorld();

        Assert.Equal(3, world.Run("branch", "reset", "pr-142", "--format", "json").Exit);
        Assert.Equal(2, world.Run("branch", "reset", "../x", "--format", "json").Exit);
    }
}
