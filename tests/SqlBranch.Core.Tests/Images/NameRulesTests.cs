using SqlBranch.Core.Images;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Tests.Images;

public sealed class NameRulesTests
{
    [Theory]
    [InlineData("shop")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("pr-142")]
    [InlineData("shop_2024-01")]
    [InlineData("9lives")]
    public void Valid_names_pass_for_images_and_branches(string name)
    {
        NameRules.ValidateImage(name);
        NameRules.ValidateBranch(name);
    }

    [Fact]
    public void A_name_of_63_characters_passes_and_one_of_64_does_not()
    {
        NameRules.ValidateImage(new string('a', 63));
        NameRules.ValidateBranch(new string('a', 63));

        Assert.Equal(ErrorCodes.InvalidName, Assert.Throws<SqlBranchException>(() => NameRules.ValidateImage(new string('a', 64))).Code);
        Assert.Equal(ErrorCodes.InvalidName, Assert.Throws<SqlBranchException>(() => NameRules.ValidateBranch(new string('a', 64))).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Shop DB")]
    [InlineData("Shop")]
    [InlineData("shop db")]
    [InlineData("-shop")]
    [InlineData("_shop")]
    [InlineData("shop.bak")]
    [InlineData("../branches/pr-142")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("shop\n")]
    [InlineData("sh'op")]
    [InlineData("sh]op")]
    [InlineData("shöp")]
    [InlineData("..")]
    public void Invalid_names_are_refused_with_exit_2_and_the_rule(string name)
    {
        foreach (var validate in new Action<string?>[] { NameRules.ValidateImage, NameRules.ValidateBranch })
        {
            var ex = Assert.Throws<SqlBranchException>(() => validate(name));
            Assert.Equal(ErrorCodes.InvalidName, ex.Code);
            Assert.Equal(ExitCode.UsageError, ex.Code.ExitCode);
            Assert.Contains("1 to 63 characters", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_null_name_is_refused()
    {
        Assert.Equal(ErrorCodes.InvalidName, Assert.Throws<SqlBranchException>(() => NameRules.ValidateImage(null)).Code);
    }

    [Theory]
    [InlineData("sqlbranch_x")]
    [InlineData("sqlbranch_build_ab12")]
    [InlineData("sqlbranch_")]
    public void The_sqlbranch_prefix_is_reserved_for_images_and_branches(string name)
    {
        foreach (var validate in new Action<string?>[] { NameRules.ValidateImage, NameRules.ValidateBranch })
        {
            var ex = Assert.Throws<SqlBranchException>(() => validate(name));
            Assert.Equal(ErrorCodes.InvalidName, ex.Code);
            Assert.Contains("'sqlbranch_' is reserved", ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("master")]
    [InlineData("model")]
    [InlineData("msdb")]
    [InlineData("tempdb")]
    public void System_database_names_are_refused_for_branches_but_not_for_images(string name)
    {
        var ex = Assert.Throws<SqlBranchException>(() => NameRules.ValidateBranch(name));
        Assert.Equal(ErrorCodes.InvalidName, ex.Code);
        Assert.Contains("system database", ex.Message, StringComparison.Ordinal);

        NameRules.ValidateImage(name);
    }

    [Fact]
    public void A_name_that_merely_contains_the_prefix_or_a_system_name_is_fine()
    {
        NameRules.ValidateBranch("my-sqlbranch_x");
        NameRules.ValidateBranch("master2");
        NameRules.ValidateImage("sqlbranch");
    }
}
