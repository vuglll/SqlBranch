using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlBranch.Cli;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Tests.Cli;

public sealed class CliAppTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
    }

    /// <summary>An app with one test command, "probe echo", that returns a result.</summary>
    private static CliApp AppWithProbe()
    {
        var app = new CliApp(new SecretRedactor());
        var probe = new Command("probe", "Test-only command group.");
        app.Root.Subcommands.Add(probe);
        var echo = new Command("echo", "Returns a fixed result.");
        echo.Options.Add(new Option<string>("--from"));
        app.AddCommand(probe, echo, NoPromptTests.ProbeMetadata, _ => CommandResult.Success("probe echo",
            new JsonObject { ["value"] = "hello" },
            textLines: ["value: hello", "extra detail"],
            quietLines: ["hello"]));
        return app;
    }

    private int Run(CliApp app, params string[] args) => app.Run(args, _stdout, _stderr);

    [Fact]
    public void Unknown_option_exits_2_with_a_usage_error()
    {
        var exit = Run(AppWithProbe(), "probe", "echo", "--frm", "shop");

        Assert.Equal(2, exit);
        Assert.Contains("--frm", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _stdout.ToString());
    }

    [Fact]
    public void Unknown_option_with_json_format_prints_a_json_error()
    {
        var exit = Run(AppWithProbe(), "probe", "echo", "--frm", "shop", "--format", "json");

        var doc = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(2, exit);
        Assert.Equal("probe echo", doc.GetProperty("command").GetString());
        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Equal("usage_error", doc.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Format_json_works_on_a_subcommand()
    {
        var exit = Run(AppWithProbe(), "probe", "echo", "--format", "json");

        var doc = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(0, exit);
        Assert.Equal("hello", doc.GetProperty("value").GetString());
        Assert.Equal(1, doc.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Format_value_is_case_insensitive()
    {
        Assert.Equal(0, Run(AppWithProbe(), "probe", "echo", "--format", "JSON"));
        Assert.True(JsonDocument.Parse(_stdout.ToString()).RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Unknown_format_value_exits_2()
    {
        Assert.Equal(2, Run(AppWithProbe(), "probe", "echo", "--format", "xml"));
    }

    [Fact]
    public void Quiet_prints_only_the_essential_result()
    {
        Assert.Equal(0, Run(AppWithProbe(), "probe", "echo", "--quiet"));
        Assert.Equal("hello" + Environment.NewLine, _stdout.ToString());
    }

    [Fact]
    public void Config_option_is_accepted_on_any_command()
    {
        Assert.Equal(0, Run(AppWithProbe(), "probe", "echo", "--config", "other.yml"));
    }

    [Fact]
    public void Version_prints_the_version()
    {
        Assert.Equal(0, Run(CliApp.CreateDefault(new SecretRedactor(), NoPromptTests.FakeServices()), "--version"));
        Assert.Equal(ProductInfo.Version, _stdout.ToString().Trim());
    }

    [Fact]
    public void No_command_exits_2()
    {
        Assert.Equal(2, Run(CliApp.CreateDefault(new SecretRedactor(), NoPromptTests.FakeServices())));
    }

    [Fact]
    public void A_handler_exception_becomes_operation_failed_with_the_secret_masked()
    {
        var redactor = new SecretRedactor();
        redactor.Register("Top$ecret1");
        var app = new CliApp(redactor);
        app.AddCommand(app.Root, new Command("boom"), NoPromptTests.ProbeMetadata, _ => throw new InvalidOperationException("login with Top$ecret1 failed"));

        var exit = Run(app, "boom");

        Assert.Equal(1, exit);
        Assert.Contains("login with *** failed", _stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Top$ecret1", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_exception_exits_2_with_config_error_and_names_file_and_key()
    {
        var app = new CliApp(new SecretRedactor());
        app.AddCommand(app.Root, new Command("probe"), NoPromptTests.ProbeMetadata, _ => throw new ConfigException("not valid.", "/repo/.sqlbranch.yml", "poolSize"));

        var exit = Run(app, "probe", "--format", "json");

        var doc = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(2, exit);
        Assert.Equal("config_error", doc.GetProperty("error").GetProperty("code").GetString());
        var message = doc.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains(".sqlbranch.yml", message, StringComparison.Ordinal);
        Assert.Contains("poolSize", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_coded_failure_reports_its_own_code_and_exit_code()
    {
        var app = new CliApp(new SecretRedactor());
        app.AddCommand(app.Root, new Command("probe"), NoPromptTests.ProbeMetadata, _ => throw new SqlBranchException(ErrorCodes.HostDiskFull, "needs 60 GB, 20 GB available"));

        Assert.Equal(3, Run(app, "probe", "--format", "json"));
        Assert.Equal("host_disk_full", JsonDocument.Parse(_stdout.ToString()).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Version_with_an_unknown_option_is_a_usage_error()
    {
        Assert.Equal(2, Run(CliApp.CreateDefault(new SecretRedactor(), NoPromptTests.FakeServices()), "--version", "--frm"));
        Assert.Contains("--frm", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Help_lists_format_values_in_lowercase()
    {
        Assert.Equal(0, Run(CliApp.CreateDefault(new SecretRedactor(), NoPromptTests.FakeServices()), "--help"));
        Assert.Contains("--format <text|json>", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--format", "json" }, OutputFormat.Json)]
    [InlineData(new[] { "--format=json" }, OutputFormat.Json)]
    [InlineData(new[] { "--format:JSON" }, OutputFormat.Json)]
    [InlineData(new[] { "--format", "text" }, OutputFormat.Text)]
    [InlineData(new string[0], OutputFormat.Text)]
    public void Format_is_found_on_a_line_that_does_not_parse(string[] args, OutputFormat expected)
    {
        Assert.Equal(expected, CliApp.DetectFormat(args));
    }
}
