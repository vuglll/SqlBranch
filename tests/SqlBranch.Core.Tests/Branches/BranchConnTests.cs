using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

/// <summary>Task 6.9: <c>branch conn</c> on fakes. Nothing here reaches the real docker.</summary>
public sealed class BranchConnTests
{
    private readonly CliWorld _world = new();
    private readonly List<string> _timeline = [];
    private string? _record = "{\"image\":\"shop\",\"cloneMode\":\"thin\",\"createdAt\":\"2026-11-17T09:30:00+00:00\",\"labels\":{}}";

    public BranchConnTests()
    {
        _world.WithEngine();
        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            _timeline.Add("sh: " + script);
            if (script.Contains("SQLBRANCH_RECORD=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_RECORD=" + (_record is null ? "none" : Convert.ToBase64String(Encoding.UTF8.GetBytes(_record))));
            }

            return inner(container, command);
        };
        _world.Connector.ExecuteHandler = sql => _timeline.Add("sql: " + sql);
        _world.Connector.ParameterizedScalarHandler = (sql, _) => sql.Contains("state_desc", StringComparison.Ordinal) ? "ONLINE" : 1;
    }

    private static SqlConnectionStringBuilder Parsed(string connectionString) => new(connectionString);

    [Fact]
    public void The_text_output_is_a_connection_string_with_the_engine_password_for_127_0_0_1_and_the_engine_port()
    {
        var run = _world.Run("branch", "conn", "pr-142");

        Assert.Equal(0, run.Exit);
        var line = run.Out.TrimEnd('\n');
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        var parsed = Parsed(line);
        Assert.Equal("127.0.0.1,14330", parsed.DataSource);
        Assert.Equal("pr-142", parsed.InitialCatalog);
        Assert.Equal("sa", parsed.UserID);
        Assert.Equal(CliWorld.StoredPassword, parsed.Password);
        Assert.True(parsed.TrustServerCertificate);
        Assert.DoesNotContain("***", run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_output_has_the_connection_string_with_the_password_and_the_parts_without_it()
    {
        var run = _world.Run("branch", "conn", "pr-142", "--format", "json");

        Assert.Equal(0, run.Exit);
        var json = JsonDocument.Parse(run.Out).RootElement;
        Assert.Equal("branch conn", json.GetProperty("command").GetString());
        Assert.True(json.GetProperty("ok").GetBoolean());
        var parsed = Parsed(json.GetProperty("connectionString").GetString()!);
        Assert.Equal(CliWorld.StoredPassword, parsed.Password);
        Assert.Equal("pr-142", parsed.InitialCatalog);
        var branch = json.GetProperty("branch");
        Assert.Equal("pr-142", branch.GetProperty("name").GetString());
        Assert.Equal("127.0.0.1,14330", branch.GetProperty("server").GetString());
        Assert.Equal("pr-142", branch.GetProperty("database").GetString());
        Assert.Equal("sa", branch.GetProperty("user").GetString());
        // The password is in connectionString once and nowhere else.
        Assert.Equal(1, run.Out.Split(CliWorld.StoredPassword).Length - 1);
    }

    [Fact]
    public void Quiet_prints_the_connection_string_alone_on_one_line_and_so_does_a_json_free_text_run()
    {
        var quiet = _world.Run("branch", "conn", "pr-142", "--quiet");
        var text = _world.Run("branch", "conn", "pr-142");

        Assert.Equal(0, quiet.Exit);
        Assert.Equal(text.Out, quiet.Out);
        Assert.Single(quiet.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("pr-142", Parsed(quiet.Out.Trim()).InitialCatalog);
        Assert.Equal(string.Empty, quiet.Err);
    }

    [Fact]
    public void A_password_with_a_semicolon_and_a_quote_still_parses_back_to_the_same_password()
    {
        const string Tricky = "A;b'c\"d=e#1";
        _world.Connector.SaPassword = Tricky;
        _world.Host.CredentialsJson = JsonSerializer.Serialize(new { user = "sa", password = Tricky });

        var run = _world.Run("branch", "conn", "pr-142", "--quiet");

        Assert.Equal(0, run.Exit);
        Assert.Equal(Tricky, Parsed(run.Out.Trim()).Password);
    }

    [Theory]
    [InlineData(null, "has no database")]
    [InlineData("OFFLINE", "offline")]
    [InlineData("RECOVERY_PENDING", "recovery_pending")]
    public void A_missing_or_not_online_database_still_prints_the_string_and_warns(string? state, string words)
    {
        _world.Connector.ParameterizedScalarHandler = (sql, _) => sql.Contains("state_desc", StringComparison.Ordinal) ? state : 1;

        var run = _world.Run("branch", "conn", "pr-142", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Equal("pr-142", Parsed(JsonDocument.Parse(run.Out).RootElement.GetProperty("connectionString").GetString()!).InitialCatalog);
        Assert.Contains(words, run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_online_database_prints_no_warning()
    {
        _world.Connector.ParameterizedScalarHandler = (sql, _) => sql.Contains("state_desc", StringComparison.Ordinal) ? "ONLINE" : 1;

        var run = _world.Run("branch", "conn", "pr-142");

        Assert.Equal(0, run.Exit);
        Assert.Equal(string.Empty, run.Err);
    }

    [Fact]
    public void An_unknown_branch_exits_1_branch_not_found_and_prints_no_password()
    {
        _record = null;

        var run = _world.Run("branch", "conn", "nope", "--format", "json");

        Assert.Equal(1, run.Exit);
        var json = JsonDocument.Parse(run.Out).RootElement;
        Assert.Equal("branch_not_found", json.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(CliWorld.StoredPassword, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../images/shop")]
    [InlineData("master")]
    [InlineData("sqlbranch_build_x")]
    [InlineData("Upper")]
    public void A_name_that_breaks_the_rules_exits_2_before_any_file_or_sql(string name)
    {
        var run = _world.Run("branch", "conn", name, "--format", "json");

        Assert.Equal(2, run.Exit);
        Assert.Equal("invalid_name", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_timeline);
        Assert.Empty(_world.Connector.Attempts);
    }

    [Fact]
    public void Before_init_conn_exits_3_not_initialized()
    {
        var world = new CliWorld();

        var run = world.Run("branch", "conn", "pr-142", "--format", "json");

        Assert.Equal(3, run.Exit);
    }

    [Fact]
    public void No_other_branch_command_output_contains_the_password()
    {
        _world.Connector.ParameterizedScalarHandler = (_, _) => 1;
        foreach (var args in new[] { new[] { "branch", "list" }, new[] { "branch", "create", "pr-1", "--from", "shop" }, new[] { "status" } })
        {
            var run = _world.Run([.. args, "--format", "json"]);
            Assert.DoesNotContain(CliWorld.StoredPassword, run.Out + run.Err, StringComparison.Ordinal);
        }
    }
}
