using SqlBranch.Core.Branches;
using SqlBranch.Core.Images;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

/// <summary>Tasks 6.5 and 6.6: the free-space check of a copy and <c>requireThin</c>, on the same fakes as the create.</summary>
public sealed partial class BranchCreateTests
{
    private const long GiB = 1L << 30;

    private void ImageOf(long bytes) =>
        _manifest = _manifest with { Files = [new ImageFile("Shop", "Shop.mdf", "data", bytes - 524_288), new ImageFile("Shop_log", "Shop_log.ldf", "log", 524_288)] };

    private bool FilesMade() =>
        Ran("mkdir '/pool/branches") || Ran("set -e; mkdir") || Ran("base64 -d") || Ran("FOR ATTACH") || Removed().Any();

    // ---- 6.5: free space for a copy ----

    [Fact]
    public void A_copy_create_of_a_10_GiB_image_with_4_GiB_free_exits_1_pool_full_naming_both_sizes_before_any_file_is_made()
    {
        _world.Host.EngineProbe = "copy";
        ImageOf(10 * GiB);
        _freeBytes = 4 * GiB;

        var run = Create("dev1", "--from", "shop");

        Assert.Equal(1, run.Exit);
        Assert.Equal("pool_full", ErrorCode(run.Json));
        Assert.Contains("10 GiB", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("4 GiB", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(FilesMade(), string.Join('\n', _timeline));
        Assert.True(Ran("SQLBRANCH_FREE="));
    }

    [Fact]
    public void A_copy_create_with_exactly_enough_room_goes_on_and_one_byte_less_is_refused()
    {
        _world.Host.EngineProbe = "copy";
        ImageOf(10 * GiB);
        _freeBytes = (10 * GiB) - 1;
        Assert.Equal(1, Create("dev1", "--from", "shop").Exit);

        _timeline.Clear();
        _freeBytes = 10 * GiB;
        Assert.Equal(0, Create("dev1", "--from", "shop").Exit);
    }

    [Fact]
    public void A_thin_create_needs_no_room_check_because_it_shares_the_images_blocks()
    {
        ImageOf(10 * GiB);
        _freeBytes = 1;

        var run = Create("dev1", "--from", "shop");

        Assert.Equal(0, run.Exit);
        Assert.False(Ran("SQLBRANCH_FREE="));
    }

    [Fact]
    public void A_copy_branch_on_a_thin_pool_is_checked_for_room_too()
    {
        ImageOf(10 * GiB);
        _freeBytes = 4 * GiB;

        var run = Create("bench1", "--from", "shop", "--copy");

        Assert.Equal(1, run.Exit);
        Assert.Equal("pool_full", ErrorCode(run.Json));
        Assert.False(FilesMade());
    }

    [Fact]
    public void A_free_space_reading_that_fails_fails_the_create_before_any_file_is_made()
    {
        _world.Host.EngineProbe = "copy";
        _failScript = script => script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal);

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Contains("free space", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(FilesMade());
    }

    [Fact]
    public async Task The_shared_free_space_check_is_the_one_a_reset_will_use_and_reads_the_pool_through_the_engine()
    {
        _freeBytes = 3 * GiB;

        Assert.Equal(3 * GiB, await PoolFreeSpace.FreeBytesAsync(_world.Runtime, ContainerLabelsEngine, CancellationToken.None));
        await PoolFreeSpace.RequireAsync(_world.Runtime, ContainerLabelsEngine, 3 * GiB, "x", CancellationToken.None);
        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            PoolFreeSpace.RequireAsync(_world.Runtime, ContainerLabelsEngine, (3 * GiB) + 1, "a reset", CancellationToken.None));
        Assert.Equal("pool_full", ex.Code.Code);
        Assert.Contains("a reset", ex.Message, StringComparison.Ordinal);
    }

    private const string ContainerLabelsEngine = SqlBranch.Core.Containers.ContainerLabels.EngineName;

    // ---- 6.6: requireThin ----

    [Fact]
    public void RequireThin_on_in_copy_mode_refuses_a_create_with_exit_3_thin_required_before_any_file_and_no_branch_exists()
    {
        _world.Environment["SQLBRANCH_REQUIRE_THIN"] = "true";
        _world.Host.EngineProbe = "copy";

        var run = Create("pr-142", "--from", "shop");

        Assert.Equal(3, run.Exit);
        Assert.Equal("thin_required", ErrorCode(run.Json));
        Assert.False(FilesMade(), string.Join('\n', _timeline));
        Assert.False(Ran("SQLBRANCH_FREE="));
    }

    [Fact]
    public void RequireThin_on_in_thin_mode_lets_a_create_through()
    {
        _world.Environment["SQLBRANCH_REQUIRE_THIN"] = "true";

        var run = Create();

        Assert.Equal(0, run.Exit);
        Assert.Equal("thin", run.Json.GetProperty("branch").GetProperty("cloneMode").GetString());
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void RequireThin_on_lets_a_copy_branch_through_in_both_clone_modes_and_makes_it_a_full_copy(string poolMode)
    {
        _world.Environment["SQLBRANCH_REQUIRE_THIN"] = "true";
        _world.Host.EngineProbe = poolMode;

        var run = Create("bench1", "--from", "shop", "--copy");

        Assert.Equal(0, run.Exit);
        Assert.Equal("copy", run.Json.GetProperty("branch").GetProperty("cloneMode").GetString());
        var clone = _timeline.Single(l => l.StartsWith("sh: set -e; mkdir '/pool/branches/bench1/data'", StringComparison.Ordinal));
        Assert.DoesNotContain("--reflink", clone, StringComparison.Ordinal);
        Assert.Equal("copy", WrittenRecord().CloneMode);
    }

    [Fact]
    public void RequireThin_off_lets_a_copy_mode_create_through()
    {
        _world.Host.EngineProbe = "copy";

        Assert.Equal(0, Create().Exit);
    }

    [Theory]
    [InlineData(true, CloneMode.Copy, false, true)]
    [InlineData(true, CloneMode.Copy, true, false)]
    [InlineData(true, CloneMode.Thin, false, false)]
    [InlineData(true, CloneMode.Thin, true, false)]
    [InlineData(false, CloneMode.Copy, false, false)]
    public void The_thin_guard_a_create_and_a_reset_share_refuses_only_a_branch_that_is_not_a_copy_branch_on_a_copy_pool(
        bool requireThin, CloneMode poolMode, bool explicitCopy, bool refused)
    {
        if (!refused)
        {
            BranchGuards.RequireThin(requireThin, poolMode, explicitCopy);
            return;
        }

        var ex = Assert.Throws<SqlBranchException>(() => BranchGuards.RequireThin(requireThin, poolMode, explicitCopy));
        Assert.Equal("thin_required", ex.Code.Code);
        Assert.Equal(SqlBranch.Core.Output.ExitCode.EnvironmentNotReady, ex.Code.ExitCode);
    }

    [Fact]
    public void The_create_command_has_a_copy_flag()
    {
        var help = _world.Run("branch", "create", "--help");

        Assert.Equal(0, help.Exit);
        Assert.Contains("--copy", help.Out, StringComparison.Ordinal);
    }
}
