using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// One engine on a pool that cannot share blocks: <c>poolBackend</c> plain on a folder of the client, which Docker Desktop
/// serves from the host's file system, so the probe says copy. Its folder is the test's own and goes with it.
/// </summary>
#pragma warning disable CA1001 // xunit disposes a fixture through IAsyncLifetime.DisposeAsync, which disposes the folder.
public sealed class CopyEngineFixture : ImageEngineFixture
#pragma warning restore CA1001
{
    private HostPoolDirectory? _directory;

    protected override EngineWorld NewWorld()
    {
        var world = new EngineWorld(plain: false);
        _directory = new HostPoolDirectory(world.Runtime);
        world.Environment["SQLBRANCH_POOL_BACKEND"] = "plain";
        world.Environment["SQLBRANCH_POOL"] = _directory.Path;
        return world;
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_directory is not null)
        {
            await _directory.DisposeAsync();
        }
    }
}

/// <summary>Tasks 5.13, 6.6, 6.7 and 6.8 in copy mode, where every branch is a full copy.</summary>
[Trait("Category", "Integration")]
public sealed class BranchCopyModeIntegrationTests(CopyEngineFixture fixture, ITestOutputHelper output) : IClassFixture<CopyEngineFixture>
{
    private BranchKit Kit => new(fixture, output);

    [IntegrationFact]
    public async Task Prune_handles_expiry_open_connections_broken_records_and_concurrent_reset()
    {
        Assert.Equal("copy", await Kit.CloneModeAsync());
        await Kit.VerifyPruneAsync();
    }

    [IntegrationFact]
    public async Task The_copy_test_engine_started_and_its_pool_cannot_share_blocks()
    {
        Assert.True(fixture.Init.Exit == 0, fixture.Init.Out + fixture.Init.Err);
        Assert.Equal("copy", await Kit.CloneModeAsync());
    }

    [IntegrationFact]
    public async Task Image_rm_of_an_image_with_a_branch_needs_force_and_after_a_forced_removal_the_copy_branch_still_answers()
    {
        Assert.Equal("copy", await Kit.CloneModeAsync());
        await Kit.ForcedImageRemovalLeavesTheBranchWorkingAsync();
    }

    [IntegrationFact]
    public async Task A_row_written_in_one_copy_branch_is_in_no_sibling_and_no_later_branch_and_the_image_files_keep_their_hashes()
    {
        Assert.Equal("copy", await Kit.CloneModeAsync());
        await Kit.BranchesAreIsolatedAsync();
    }

    [IntegrationFact]
    public async Task Branch_list_shows_state_own_size_and_a_removed_image_in_a_copy_pool()
    {
        Assert.Equal("copy", await Kit.CloneModeAsync());
        await Kit.BranchListShowsStateOwnSizeAndARemovedImageAsync();
    }

    [IntegrationFact]
    public async Task Branch_conn_prints_a_string_that_opens_a_copy_branch_and_reads_the_image_rows()
    {
        Assert.Equal("copy", await Kit.CloneModeAsync());
        await Kit.BranchConnPrintsAStringThatOpensTheBranchAsync();
    }

    [IntegrationFact]
    public async Task Branch_rm_leaves_a_user_database_alone_and_removes_a_copy_branch_with_an_open_connection()
    {
        Assert.Equal("copy", await Kit.CloneModeAsync());
        await Kit.BranchRmIsGuardedAndRemovesABranchWithAnOpenConnectionAsync();
    }

    [IntegrationFact]
    public async Task Branch_rm_and_reset_work_on_a_copy_branch_whose_database_is_offline()
    {
        Assert.Equal("copy", await Kit.CloneModeAsync());
        await Kit.OfflineBranchesCanBeRemovedAndResetAsync();
    }

    [IntegrationFact]
    public async Task Branch_reset_brings_a_dropped_table_back_in_a_copy_branch_and_the_old_connection_string_still_works()
    {
        Assert.Equal("copy", await Kit.CloneModeAsync());
        await Kit.BranchResetBringsBackADroppedTableAndKeepsTheConnectionStringAsync();
    }

    [IntegrationFact]
    public async Task Branch_files_after_a_million_inserts_into_a_rebuilt_log_image_pass_DBCC_CHECKDB_in_a_copy_pool()
    {
        Assert.Equal("copy", await Kit.CloneModeAsync());
        await Kit.CheckDbReportsNoErrorsOnWrittenBranchesAsync();
    }

    [IntegrationThinFact]
    public async Task Three_branches_from_a_1_GiB_image_grow_the_pool_by_under_48_MiB_is_skipped_not_passed_in_copy_mode()
    {
        await Kit.ThreeBranchesOfALargeImageGrowThePoolLittleAsync();
    }

    [IntegrationFact]
    public async Task RequireThin_refuses_a_create_in_copy_mode_with_exit_3_and_no_files_but_allows_a_copy_branch()
    {
        await Kit.EnsureImageAsync("shop");
        fixture.World!.Environment["SQLBRANCH_REQUIRE_THIN"] = "true";
        try
        {
            var refused = fixture.World.RunCli("branch", "create", "pr-142", "--from", "shop", "--format", "json");
            output.WriteLine(refused.Out + refused.Err);
            Assert.Equal(3, refused.Exit);
            Assert.Equal("thin_required", BranchKit.ErrorCode(refused.Out));
            Assert.Equal(string.Empty, await Kit.InPoolAsync("ls /pool/branches/pr-142 2>/dev/null"));
            Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = N'pr-142'"), System.Globalization.CultureInfo.InvariantCulture));

            var copy = fixture.World.RunCli("branch", "create", "bench1", "--from", "shop", "--copy", "--format", "json");
            output.WriteLine(copy.Out + copy.Err);
            Assert.True(copy.Exit == 0, copy.Out + copy.Err);
            Assert.Equal("copy", BranchKit.Parse(copy.Out).GetProperty("branch").GetProperty("cloneMode").GetString());
            Kit.AssertStatusIsClean();
        }
        finally
        {
            fixture.World.Environment.Remove("SQLBRANCH_REQUIRE_THIN");
            await Kit.DropBranchAsync("bench1");
        }
    }
}
