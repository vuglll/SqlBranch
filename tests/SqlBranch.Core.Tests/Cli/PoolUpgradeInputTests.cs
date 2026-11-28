using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Cli.Commands;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>Task 4.1: the input of <c>pool upgrade</c> is checked before any Docker command, then the environment.</summary>
public sealed class PoolUpgradeInputTests
{
    private const string Format1Credentials = "{\"user\":\"sa\",\"password\":\"Stored#Pass1\"}";

    private static string Code(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    private static string Message(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("message").GetString()!;

    [Theory]
    [InlineData("", "--yes")]
    [InlineData("--yes=true", "--yes")]
    [InlineData("--yes true", "--yes")]
    [InlineData("--yes --yes", "--yes")]
    [InlineData("extra --yes", "extra")]
    [InlineData("--force", "--force")]
    [InlineData("--yes --force", "--force")]
    public void The_upgrade_input_table_is_refused_with_exit_2_before_any_docker_command(string arguments, string named)
    {
        var world = new CliWorld();
        world.WithEngine();
        var args = arguments.Length == 0 ? Array.Empty<string>() : arguments.Split(' ');

        var run = world.Run(["pool", "upgrade", .. args, "--format", "json"]);

        Assert.Equal(2, run.Exit);
        Assert.Equal("usage_error", Code(run.Out));
        Assert.Contains(named, Message(run.Out), StringComparison.Ordinal);
        Assert.Empty(world.Runtime.Calls);
        Assert.Empty(world.Runtime.Runs);
        Assert.Empty(world.Connector.Statements);
        Assert.Empty(world.Host.CredentialsRewrites);
    }

    [Fact]
    public void Missing_confirmation_says_the_upgrade_cannot_be_undone_and_names_the_flag()
    {
        var world = new CliWorld();
        world.WithEngine();

        var run = world.Run("pool", "upgrade", "--format", "json");

        Assert.Equal(2, run.Exit);
        var message = Message(run.Out);
        Assert.Contains("cannot be undone", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch pool upgrade --yes", message, StringComparison.Ordinal);
        Assert.Equal(Format1Credentials, world.Host.CredentialsJson);
        Assert.Empty(world.Runtime.Calls);
    }

    [Theory]
    [InlineData("--yes --format json")]
    [InlineData("--yes --quiet")]
    [InlineData("--yes -q")]
    [InlineData("--yes --config ci.sqlbranch.yml")]
    [InlineData("--format json --yes")]
    [InlineData("--config ci.sqlbranch.yml --quiet --yes --format json")]
    public void Global_options_are_accepted_next_to_yes(string arguments)
    {
        var world = new CliWorld();
        var app = CliApp.CreateDefault(world.Redactor, world.Services());

        var parse = app.Root.Parse(["pool", "upgrade", .. arguments.Split(' ')]);

        Assert.Empty(parse.Errors);
        Assert.Empty(parse.UnmatchedTokens);
        PoolCommands.CheckInput(parse);
    }

    [Fact]
    public void Upgrade_before_init_is_not_initialized_and_changes_nothing()
    {
        var world = new CliWorld();

        var run = world.Run("pool", "upgrade", "--yes", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("not_initialized", Code(run.Out));
        Assert.Contains("sqlbranch init", Message(run.Out), StringComparison.Ordinal);

        // Only the pool lookup's read-only probe ran.
        Assert.All(world.Runtime.Runs, r => Assert.StartsWith("sqlbranch-helper-stat-", r.Name, StringComparison.Ordinal));
        Assert.Empty(world.Host.CredentialsRewrites);
        Assert.Empty(world.Connector.Statements);
    }

    [Fact]
    public void Upgrade_with_the_engine_stopped_is_engine_stopped_and_changes_nothing()
    {
        var world = new CliWorld();
        world.WithEngine(running: false);

        var run = world.Run("pool", "upgrade", "--yes", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("engine_stopped", Code(run.Out));
        Assert.Contains("sqlbranch engine start", Message(run.Out), StringComparison.Ordinal);
        Assert.Equal(Format1Credentials, world.Host.CredentialsJson);
        Assert.Empty(world.Host.CredentialsRewrites);
        Assert.Empty(world.Connector.Statements);
        Assert.Empty(world.Runtime.Runs);
    }
}
