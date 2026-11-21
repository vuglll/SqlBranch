using System.Text.Json;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

/// <summary>Tasks 6.10 (the guard) and 6.12: <c>branch rm</c> on fakes. Nothing here reaches the real docker.</summary>
public sealed class BranchRemoveTests
{
    private readonly BranchWorld _w = new();

    private (int Exit, JsonElement Json, string Out, string Err) Rm(params string[] args)
    {
        var run = _w.Run(["branch", "rm", .. args, "--format", "json"]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement, run.Out, run.Err);
    }

    private static string ErrorCode(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString()!;

    private static string ErrorMessage(JsonElement json) => json.GetProperty("error").GetProperty("message").GetString()!;

    private bool Dropped() => _w.Ran("DROP DATABASE");

    private bool Deleted() => _w.Timeline.Any(l => l.StartsWith("sh: rm -rf", StringComparison.Ordinal));

    // ---- 6.12: the remove ----

    [Fact]
    public void A_remove_closes_connections_with_single_user_rollback_drops_the_database_and_deletes_the_folder_in_that_order()
    {
        _w.AddBranch("pr-142");

        var run = Rm("pr-142");

        Assert.Equal(0, run.Exit);
        Assert.Equal("branch rm", run.Json.GetProperty("command").GetString());
        Assert.Equal("pr-142", run.Json.GetProperty("branch").GetProperty("name").GetString());
        Assert.True(run.Json.GetProperty("branch").GetProperty("databaseDropped").GetBoolean());
        var single = _w.IndexOf("ALTER DATABASE [pr-142] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        var drop = _w.IndexOf("DROP DATABASE [pr-142];");
        var delete = _w.Timeline.FindIndex(l => l.StartsWith("sh: rm -rf '/pool/branches/pr-142' && [ ! -e '/pool/branches/pr-142' ]", StringComparison.Ordinal));
        Assert.True(single >= 0 && single < drop && drop < delete, string.Join('\n', _w.Timeline));
        Assert.False(_w.World.Connector.Databases.ContainsKey("pr-142"));
        Assert.Contains("sqlbranch:branch:pr-142", _w.World.Connector.TakenLocks);
    }

    [Fact]
    public void The_text_output_names_the_branch_and_quiet_prints_its_name()
    {
        _w.AddBranch("pr-142");
        Assert.Contains("pr-142", _w.Run("branch", "rm", "pr-142").Out, StringComparison.Ordinal);

        _w.AddBranch("pr-143");
        Assert.Equal("pr-143\n", _w.Run("branch", "rm", "pr-143", "--quiet").Out.Replace("\r", string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("OFFLINE")]
    [InlineData("RECOVERY_PENDING")]
    [InlineData("SUSPECT")]
    public void A_branch_that_is_not_online_is_dropped_without_single_user_and_its_folder_is_deleted(string state)
    {
        _w.AddBranch("pr-142");
        _w.States["pr-142"] = state;

        var run = Rm("pr-142");

        Assert.Equal(0, run.Exit);
        Assert.True(run.Json.GetProperty("branch").GetProperty("databaseDropped").GetBoolean());
        Assert.False(_w.Ran("SINGLE_USER"));
        Assert.True(Dropped());
        Assert.True(Deleted());
        Assert.False(_w.World.Connector.Databases.ContainsKey("pr-142"));
    }

    [Fact]
    public void A_branch_with_a_record_and_no_database_still_has_its_folder_removed_and_no_drop_runs()
    {
        _w.AddBranchWithoutDatabase("dead");

        var run = Rm("dead");

        Assert.Equal(0, run.Exit);
        Assert.False(run.Json.GetProperty("branch").GetProperty("databaseDropped").GetBoolean());
        Assert.False(Dropped());
        Assert.False(_w.Ran("SINGLE_USER"));
        Assert.True(_w.Ran("rm -rf '/pool/branches/dead'"));
    }

    [Fact]
    public void A_folder_that_cannot_be_deleted_exits_1_naming_it_after_the_database_was_dropped()
    {
        _w.AddBranch("pr-142");
        _w.FailScript = script => script.StartsWith("rm -rf", StringComparison.Ordinal);

        var run = Rm("pr-142");

        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", ErrorCode(run.Json));
        Assert.Contains("/pool/branches/pr-142", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("sqlbranch status", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.True(Dropped());
        Assert.False(_w.World.Connector.Databases.ContainsKey("pr-142"));
        // The folder is still in the record map, which is what status then reports as a folder with no database.
        Assert.True(_w.Records.ContainsKey("pr-142"));
    }

    [Fact]
    public void Status_then_exits_1_and_lists_the_left_folder_as_a_branch_folder_with_no_database()
    {
        _w.AddBranch("pr-142");
        _w.FailScript = script => script.StartsWith("rm -rf", StringComparison.Ordinal);
        Assert.Equal(1, Rm("pr-142").Exit);
        _w.FailScript = null;

        var status = _w.Run("status", "--format", "json");

        Assert.Equal(1, status.Exit);
        var mismatch = JsonDocument.Parse(status.Out).RootElement.GetProperty("mismatches").EnumerateArray().Single();
        Assert.Equal("pr-142", mismatch.GetProperty("name").GetString());
        Assert.Equal("branch_missing_database", mismatch.GetProperty("kind").GetString());
    }

    [Fact]
    public void After_a_successful_remove_status_is_clean()
    {
        _w.AddBranch("pr-142");
        Assert.Equal(0, Rm("pr-142").Exit);

        Assert.Equal(0, _w.Run("status", "--format", "json").Exit);
    }

    [Fact]
    public void A_drop_that_fails_leaves_the_folder_alone_gives_the_connections_back_and_exits_1()
    {
        _w.AddBranch("pr-142");
        _w.World.Connector.FailDrop.Add("pr-142");

        var run = Rm("pr-142");

        Assert.Equal(1, run.Exit);
        Assert.False(Deleted());
        Assert.True(_w.Ran("ALTER DATABASE [pr-142] SET MULTI_USER;"));
        Assert.True(_w.World.Connector.Databases.ContainsKey("pr-142"));
    }

    [Fact]
    public void A_database_with_the_branchs_name_and_files_elsewhere_is_never_dropped_and_the_folder_is_removed_with_a_warning()
    {
        _w.AddBranchWithoutDatabase("scratch");
        _w.World.Connector.Databases["scratch"] = ["/var/opt/mssql/data/scratch.mdf"];

        var run = Rm("scratch");

        Assert.Equal(0, run.Exit);
        Assert.False(Dropped());
        Assert.True(_w.World.Connector.Databases.ContainsKey("scratch"));
        Assert.Contains("left alone", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_remove_while_another_command_holds_the_branch_lock_exits_1_and_changes_nothing()
    {
        _w.AddBranch("pr-142");
        _w.World.Connector.HeldLocks.Add("sqlbranch:branch:pr-142");

        var run = Rm("pr-142");

        Assert.Equal(1, run.Exit);
        Assert.False(Dropped());
        Assert.False(Deleted());
    }

    [Fact]
    public void A_branch_name_with_a_bracket_in_it_never_reaches_a_statement_because_the_rules_refuse_it_first()
    {
        var run = Rm("a]b");

        Assert.Equal(2, run.Exit);
        Assert.Empty(_w.Timeline);
    }

    // ---- 6.10: the guard ----

    [Fact]
    public void An_unknown_branch_exits_1_branch_not_found_and_touches_nothing()
    {
        var run = Rm("nope");

        Assert.Equal(1, run.Exit);
        Assert.Equal("branch_not_found", ErrorCode(run.Json));
        Assert.False(Dropped());
        Assert.False(Deleted());
    }

    [Fact]
    public void A_database_a_user_made_has_no_record_so_rm_exits_1_says_it_is_not_a_branch_and_drops_nothing()
    {
        _w.World.Connector.Databases["scratch"] = ["/var/opt/mssql/data/scratch.mdf"];

        var run = Rm("scratch");

        Assert.Equal(1, run.Exit);
        Assert.Equal("branch_not_found", ErrorCode(run.Json));
        Assert.Contains("scratch", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("not a branch", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Dropped());
        Assert.False(_w.Ran("ALTER DATABASE"));
        Assert.False(Deleted());
        Assert.True(_w.World.Connector.Databases.ContainsKey("scratch"));
    }

    [Theory]
    [InlineData("../images/shop")]
    [InlineData("../../etc")]
    [InlineData("pr/142")]
    [InlineData("master")]
    [InlineData("tempdb")]
    [InlineData("sqlbranch_build_ab12")]
    [InlineData("PR-1")]
    [InlineData("")]
    public void A_name_that_breaks_the_rules_exits_2_before_any_sql_or_file_action(string name)
    {
        _w.AddBranch("shop");

        var run = Rm(name);

        Assert.Equal(2, run.Exit);
        Assert.Equal("invalid_name", ErrorCode(run.Json));
        Assert.Empty(_w.Timeline);
        Assert.Empty(_w.World.Connector.Attempts);
        Assert.Empty(_w.World.Runtime.Runs);
    }

    [Fact]
    public void Before_init_rm_exits_3_not_initialized_and_a_bad_name_still_exits_2()
    {
        var world = new CliWorld();

        Assert.Equal(3, world.Run("branch", "rm", "pr-142", "--format", "json").Exit);
        Assert.Equal(2, world.Run("branch", "rm", "../x", "--format", "json").Exit);
    }

    [Fact]
    public void A_stopped_engine_exits_3_engine_stopped()
    {
        var world = new CliWorld();
        world.WithEngine(running: false);

        var run = world.Run("branch", "rm", "pr-142", "--format", "json");

        Assert.Equal(3, run.Exit);
    }
}
