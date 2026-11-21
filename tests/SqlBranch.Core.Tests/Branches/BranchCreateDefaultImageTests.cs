using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

/// <summary>Task 6.13: <c>defaultImage</c> for <c>branch create</c>, on the same fakes as the create.</summary>
public sealed partial class BranchCreateTests
{
    [Fact]
    public void Without_from_a_create_uses_the_default_image()
    {
        _world.Environment["SQLBRANCH_DEFAULT_IMAGE"] = "shop";

        var run = Create("dev1");

        Assert.Equal(0, run.Exit);
        var branch = run.Json.GetProperty("branch");
        Assert.Equal("dev1", branch.GetProperty("name").GetString());
        Assert.Equal("shop", branch.GetProperty("image").GetString());
        Assert.Contains("cp --reflink=always -- '/pool/images/shop/data/Shop.mdf' '/pool/branches/dev1/data/Shop.mdf'", _timeline.Single(l => l.Contains("mkdir '/pool/branches/dev1/data'", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal("shop", WrittenRecord().Image);
    }

    [Fact]
    public void A_from_flag_wins_over_the_default_image()
    {
        _world.Environment["SQLBRANCH_DEFAULT_IMAGE"] = "other";

        var run = Create("dev1", "--from", "shop");

        Assert.Equal(0, run.Exit);
        Assert.Equal("shop", run.Json.GetProperty("branch").GetProperty("image").GetString());
        Assert.DoesNotContain(_timeline, l => l.Contains("/pool/images/other", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_from_and_without_a_default_image_a_create_exits_2_naming_the_from_flag_and_does_nothing()
    {
        var run = Create("dev1");

        Assert.Equal(2, run.Exit);
        Assert.Equal("usage_error", ErrorCode(run.Json));
        Assert.Contains("--from", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("defaultImage", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Empty(_timeline);
        Assert.Empty(_world.Connector.Attempts);
    }

    [Fact]
    public void The_missing_image_is_a_usage_error_even_before_init()
    {
        var world = new CliWorld();

        var run = world.Run("branch", "create", "dev1", "--format", "json");

        Assert.Equal(2, run.Exit);
        Assert.Contains("--from", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_image_that_breaks_the_naming_rules_exits_2_invalid_name_and_does_nothing()
    {
        _world.Environment["SQLBRANCH_DEFAULT_IMAGE"] = "../images/shop";

        var run = Create("dev1");

        Assert.Equal(2, run.Exit);
        Assert.Equal("invalid_name", ErrorCode(run.Json));
        Assert.Empty(_timeline);
    }

    [Fact]
    public void A_bad_branch_name_is_still_refused_first_when_the_default_image_is_missing()
    {
        var run = Create("master");

        Assert.Equal(2, run.Exit);
        Assert.Equal("invalid_name", ErrorCode(run.Json));
    }
}
