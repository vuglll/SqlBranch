using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Tasks 5.13, 6.4, 6.7 and 6.8 in the clone mode of the test pool, which is thin: an XFS pool with reflink that SqlBranch made.
/// The scenarios are in <see cref="BranchKit"/>, where a copy-mode pool runs them too.
/// </summary>
public sealed partial class BranchCreateIntegrationTests
{
    private BranchKit Kit => new(fixture, output);

    [IntegrationThinFact]
    public async Task Prune_handles_expiry_open_connections_broken_records_and_concurrent_reset()
    {
        SkipTestException.ThinRequired(await Kit.CloneModeAsync());
        await Kit.VerifyPruneAsync();
    }

    [IntegrationThinFact]
    public async Task Image_rm_of_an_image_with_a_branch_needs_force_and_after_a_forced_removal_the_thin_branch_still_answers()
    {
        SkipTestException.ThinRequired(await Kit.CloneModeAsync());
        await Kit.ForcedImageRemovalLeavesTheBranchWorkingAsync();
    }

    [IntegrationThinFact]
    public async Task A_row_written_in_one_thin_branch_is_in_no_sibling_and_no_later_branch_and_the_image_files_keep_their_hashes()
    {
        SkipTestException.ThinRequired(await Kit.CloneModeAsync());
        await Kit.BranchesAreIsolatedAsync();
    }

    [IntegrationThinFact]
    public async Task Branch_list_shows_state_own_size_and_a_removed_image_in_a_thin_pool()
    {
        SkipTestException.ThinRequired(await Kit.CloneModeAsync());
        await Kit.BranchListShowsStateOwnSizeAndARemovedImageAsync();
    }

    [IntegrationThinFact]
    public async Task Branch_conn_prints_a_string_that_opens_a_thin_branch_and_reads_the_image_rows()
    {
        SkipTestException.ThinRequired(await Kit.CloneModeAsync());
        await Kit.BranchConnPrintsAStringThatOpensTheBranchAsync();
    }

    [IntegrationThinFact]
    public async Task Branch_rm_leaves_a_user_database_alone_and_removes_a_thin_branch_with_an_open_connection()
    {
        SkipTestException.ThinRequired(await Kit.CloneModeAsync());
        await Kit.BranchRmIsGuardedAndRemovesABranchWithAnOpenConnectionAsync();
    }

    [IntegrationThinFact]
    public async Task Branch_rm_and_reset_work_on_a_thin_branch_whose_database_is_offline()
    {
        SkipTestException.ThinRequired(await Kit.CloneModeAsync());
        await Kit.OfflineBranchesCanBeRemovedAndResetAsync();
    }

    [IntegrationThinFact]
    public async Task Branch_reset_brings_a_dropped_table_back_in_a_thin_branch_and_the_old_connection_string_still_works()
    {
        SkipTestException.ThinRequired(await Kit.CloneModeAsync());
        await Kit.BranchResetBringsBackADroppedTableAndKeepsTheConnectionStringAsync();
    }

    [IntegrationFact]
    public async Task Branch_create_without_from_uses_the_default_image_and_without_one_exits_2()
    {
        await Kit.DefaultImageIsUsedWithoutFromAsync();
    }

    [IntegrationThinFact]
    public async Task Three_branches_from_a_1_GiB_image_grow_the_pool_by_under_48_MiB()
    {
        await Kit.ThreeBranchesOfALargeImageGrowThePoolLittleAsync();
    }

    [IntegrationThinFact]
    public async Task Branch_files_after_a_million_inserts_into_a_rebuilt_log_image_pass_DBCC_CHECKDB_in_a_thin_pool()
    {
        SkipTestException.ThinRequired(await Kit.CloneModeAsync());
        await Kit.CheckDbReportsNoErrorsOnWrittenBranchesAsync();
    }

    [IntegrationThinFact]
    public async Task A_copy_branch_of_a_1_GiB_image_on_a_thin_pool_grows_the_pool_by_at_least_900_MiB_and_reports_copy()
    {
        await Kit.CopyBranchOnAThinPoolIsAFullCopyAsync();
    }

    [IntegrationThinFact]
    public async Task A_copy_branch_on_a_thin_pool_is_still_a_copy_after_a_reset()
    {
        await Kit.CopyBranchOnAThinPoolStaysACopyAfterAResetAsync();
    }
}
