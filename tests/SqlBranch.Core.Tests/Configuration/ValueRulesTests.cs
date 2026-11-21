using SqlBranch.Core.Configuration;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Tests.Configuration;

public class ValueRulesTests
{
    [Theory]
    [InlineData("60GB", 60_000_000_000L)]
    [InlineData("100 gb", 100_000_000_000L)]
    [InlineData("512MiB", 536_870_912L)]
    [InlineData("1.5TB", 1_500_000_000_000L)]
    [InlineData("1GiB", 1_073_741_824L)]
    public void Sizes_parse(string text, long bytes)
    {
        Assert.True(ByteSize.TryParse(text, out var size));
        Assert.Equal(bytes, size.Bytes);
    }

    [Theory]
    [InlineData("big")]
    [InlineData("60")]
    [InlineData("0GB")]
    [InlineData("-5GB")]
    [InlineData("60XB")]
    [InlineData("99999999999999999999TB")]
    [InlineData("99999999999TB")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_sizes_do_not_parse(string? text)
    {
        Assert.False(ByteSize.TryParse(text, out _));
    }

    [Theory]
    [InlineData("mcr.microsoft.com/mssql/server:2022-latest", 2022)]
    [InlineData("mcr.microsoft.com/mssql/server:2025-CU1-ubuntu-24.04", 2025)]
    [InlineData("mcr.microsoft.com/mssql/server:2019-latest", 2019)]
    public void Release_year_comes_from_the_tag(string image, int year)
    {
        Assert.Equal(year, EditionRules.ReleaseYearFromImage(image));
    }

    [Theory]
    [InlineData("registry.local/mssql:team")]
    [InlineData("mcr.microsoft.com/mssql/server:latest")]
    [InlineData("localhost:5000/mssql:custom")]
    public void Tags_without_a_year_give_no_release(string image)
    {
        Assert.Null(EditionRules.ReleaseYearFromImage(image));
    }

    [Theory]
    [InlineData("sb")]
    [InlineData("sqlbranch-pool")]
    [InlineData("/a:b")]
    [InlineData("relative/dir")]
    [InlineData("C:/pool")]
    public void A_pool_that_is_not_an_absolute_path_without_a_colon_is_exit_2_naming_pool_and_the_origin(string pool)
    {
        var ex = Assert.Throws<ConfigException>(() => SettingsLoader.Load(new SettingsSources
        {
            Environment = new Dictionary<string, string> { ["SQLBRANCH_POOL"] = pool },
            WorkingDirectory = "/work",
            HomeDirectory = null,
        }));

        Assert.Contains("pool", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SQLBRANCH_POOL", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ExitCode.UsageError, ConfigException.ErrorCode.ExitCode);
        Assert.Equal("pool", ex.Key);
        Assert.Equal("SQLBRANCH_POOL", ex.Origin);
    }

    [Theory]
    [InlineData("/data/sb")]
    [InlineData("/home/u/.sqlbranch/pool")]
    public void An_absolute_pool_path_is_accepted(string pool)
    {
        var settings = SettingsLoader.Load(new SettingsSources
        {
            Environment = new Dictionary<string, string> { ["SQLBRANCH_POOL"] = pool },
            WorkingDirectory = "/work",
            HomeDirectory = null,
        });

        Assert.Equal(pool, settings.Pool);
    }

    [Theory]
    [InlineData("abc", false)]
    [InlineData("short1A", false)]
    [InlineData("alllowercase1", false)]
    [InlineData("Passw0rd", true)]
    [InlineData("lower-and-1-digit", true)]
    [InlineData("UPPER!lower", true)]
    public void Password_policy(string password, bool acceptable)
    {
        Assert.Equal(acceptable, PasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void Weak_given_password_names_the_variable_but_not_the_value()
    {
        var ex = Assert.Throws<ConfigException>(() => PasswordPolicy.CheckGiven("abc"));
        Assert.Contains("SQLBRANCH_SA_PASSWORD", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_given_password_is_fine()
    {
        PasswordPolicy.CheckGiven(null);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1_536L, "1.5 KiB")]
    [InlineData(1_048_576L, "1 MiB")]
    [InlineData(21_474_836_480L, "20 GiB")]
    [InlineData(10_737_418_240L, "10 GiB")]
    [InlineData(1_099_511_627_776L, "1 TiB")]
    public void Binary_sizes_for_messages_use_the_largest_unit(long bytes, string expected)
    {
        Assert.Equal(expected, new ByteSize(bytes).ToBinary());
    }
}
