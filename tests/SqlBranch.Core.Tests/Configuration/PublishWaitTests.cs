using System.Text.Json;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Output;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Configuration;

public sealed class PublishWaitTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Settings Load(Dictionary<string, string>? env = null) =>
        SettingsLoader.Load(new SettingsSources { Environment = env ?? [], WorkingDirectory = _tree.Dir("repo"), HomeDirectory = _tree.Dir("home") });

    [Fact]
    public void No_configuration_gives_ten_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(10), Load().PublishWait);

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("24h", 1440)]
    [InlineData("1440m", 1440)]
    [InlineData("007m", 7)]
    [InlineData("1m", 1)]
    public void Values_are_parsed(string value, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), Load(new() { ["SQLBRANCH_PUBLISH_WAIT"] = value }).PublishWait);

    [Fact]
    public void Environment_beats_the_file_and_the_file_beats_the_default()
    {
        _tree.File("repo/.sqlbranch.yml", "publishWait: 30m");
        Assert.Equal(TimeSpan.FromMinutes(30), Load().PublishWait);

        var s = Load(new() { ["SQLBRANCH_PUBLISH_WAIT"] = "2m" });
        Assert.Equal(TimeSpan.FromMinutes(2), s.PublishWait);
        Assert.Equal("SQLBRANCH_PUBLISH_WAIT", s.Origins["publishWait"]);
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("-5m")]
    [InlineData("10")]
    [InlineData("10s")]
    [InlineData("1.5h")]
    [InlineData("25h")]
    [InlineData("1441m")]
    [InlineData("0h")]
    [InlineData("ten")]
    public void Bad_values_are_refused_naming_the_setting_and_source(string value)
    {
        var e = Assert.Throws<ConfigException>(() => Load(new() { ["SQLBRANCH_PUBLISH_WAIT"] = value }));
        Assert.Contains("publishWait", e.Message, StringComparison.Ordinal);
        Assert.Contains("SQLBRANCH_PUBLISH_WAIT", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("-5m")]
    [InlineData("10")]
    [InlineData("10s")]
    [InlineData("1.5h")]
    [InlineData("25h")]
    [InlineData("1441m")]
    [InlineData("ten")]
    public void Refused_values_fail_branch_list_and_status_with_no_docker_call(string value)
    {
        foreach (var command in new[] { new[] { "branch", "list" }, ["status"] })
        {
            var world = new CliWorld();
            world.WithEngine();
            world.Environment["SQLBRANCH_PUBLISH_WAIT"] = value;

            var run = world.Run([.. command, "--format", "json"]);

            Assert.Equal(2, run.Exit);
            var error = JsonDocument.Parse(run.Out).RootElement.GetProperty("error");
            Assert.Equal("config_error", error.GetProperty("code").GetString());
            Assert.Contains("publishWait", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("SQLBRANCH_PUBLISH_WAIT", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Empty(world.Runtime.Calls);
        }
    }

    [Theory]
    [InlineData("24h")]
    [InlineData("1440m")]
    [InlineData("007m")]
    public void Largest_values_are_accepted_so_docker_unavailable_comes_first(string value)
    {
        var world = new CliWorld();
        world.Runtime.InfoFailure = new SqlBranchException(ErrorCodes.DockerUnavailable, "Docker is not running.");
        world.Environment["SQLBRANCH_PUBLISH_WAIT"] = value;

        var run = world.Run("branch", "list", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("docker_unavailable", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
