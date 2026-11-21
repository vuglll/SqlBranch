using System.Text.Json;
using System.Text.Json.Nodes;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Tests.Output;

public sealed class ResultRendererTests : IDisposable
{
    private readonly SecretRedactor _redactor = new();
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
    }

    private int Render(CommandResult result, OutputFormat format = OutputFormat.Json, bool quiet = false) =>
        new ResultRenderer(_redactor).Render(result, format, quiet, 42, _stdout, _stderr);

    private JsonElement StdoutJson() => JsonDocument.Parse(_stdout.ToString()).RootElement;

    [Fact]
    public void Success_document_has_the_envelope_and_the_payload()
    {
        var branch = new JsonObject { ["name"] = "pr-142", ["cloneMode"] = "thin" };
        var exit = Render(CommandResult.Success("branch create", new JsonObject { ["branch"] = branch }));

        var doc = StdoutJson();
        Assert.Equal(0, exit);
        Assert.Equal(1, doc.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("branch create", doc.GetProperty("command").GetString());
        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.Equal(42, doc.GetProperty("elapsedMs").GetInt64());
        Assert.Equal("pr-142", doc.GetProperty("branch").GetProperty("name").GetString());
        Assert.False(doc.TryGetProperty("error", out _));
    }

    [Fact]
    public void Failure_document_has_error_code_and_message()
    {
        var exit = Render(CommandResult.Failure("branch create", ErrorCodes.ImageNotFound, "Image 'missing' does not exist."));

        var doc = StdoutJson();
        Assert.Equal(1, exit);
        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Equal("image_not_found", doc.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Image 'missing' does not exist.", doc.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("usage_error", 2)]
    [InlineData("config_error", 2)]
    [InlineData("not_initialized", 3)]
    [InlineData("docker_unavailable", 3)]
    [InlineData("pool_full", 1)]
    public void Exit_code_follows_the_error_code(string code, int expected)
    {
        var errorCode = ErrorCodes.All.Single(c => c.Code == code);
        Assert.Equal(expected, Render(CommandResult.Failure("x", errorCode, "m"), OutputFormat.Text));
    }

    [Fact]
    public void Payload_cannot_overwrite_the_envelope()
    {
        Assert.Throws<ArgumentException>(() =>
            CommandResult.Success("x", new JsonObject { ["ok"] = false }));
    }

    [Fact]
    public void Warning_goes_to_stderr_and_stdout_stays_valid_json()
    {
        Render(CommandResult.Success("status", new JsonObject { ["engine"] = "running" },
            warnings: ["The clone mode is copy."]));

        Assert.Equal("running", StdoutJson().GetProperty("engine").GetString());
        Assert.Contains("warning: The clone mode is copy.", _stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("warning", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_success_goes_to_stdout_only()
    {
        Render(CommandResult.Success("image list", textLines: ["shop  2 branches"]), OutputFormat.Text);

        Assert.Equal("shop  2 branches" + Environment.NewLine, _stdout.ToString());
        Assert.Equal(string.Empty, _stderr.ToString());
    }

    [Fact]
    public void Text_failure_goes_to_stderr_only()
    {
        var exit = Render(CommandResult.Failure("branch rm", ErrorCodes.BranchNotFound, "Branch 'nope' does not exist."),
            OutputFormat.Text);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, _stdout.ToString());
        Assert.Contains("error: Branch 'nope' does not exist.", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Quiet_prints_only_the_essential_lines()
    {
        var result = CommandResult.Success("branch conn",
            textLines: ["Branch pr-142", "Server=127.0.0.1,14330;Database=pr-142"],
            quietLines: ["Server=127.0.0.1,14330;Database=pr-142"]);

        Render(result, OutputFormat.Text, quiet: true);

        Assert.Equal("Server=127.0.0.1,14330;Database=pr-142" + Environment.NewLine, _stdout.ToString());
    }

    [Fact]
    public void Error_messages_and_warnings_are_redacted_in_both_formats()
    {
        _redactor.Register("S3cret!Pass");
        var result = CommandResult.Failure("init", ErrorCodes.InitFailed, "docker run -e MSSQL_SA_PASSWORD=S3cret!Pass failed",
            warnings: ["retrying with S3cret!Pass"]);

        Render(result, OutputFormat.Json);
        Render(result, OutputFormat.Text);

        var all = _stdout.ToString() + _stderr.ToString();
        Assert.DoesNotContain("S3cret!Pass", all, StringComparison.Ordinal);
        Assert.Contains("MSSQL_SA_PASSWORD=***", all, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_text_result_prints_its_report_on_standard_output_and_the_error_on_standard_error()
    {
        _redactor.Register("Top$ecret1");
        var result = CommandResult.Failure("status", ErrorCodes.Mismatch, "status found 1 mismatch: pr-7",
            textLines: ["Engine: running", "  pr-7: Top$ecret1 is missing"]);

        var exit = Render(result, OutputFormat.Text);

        Assert.Equal(1, exit);
        Assert.Equal($"Engine: running{Environment.NewLine}  pr-7: *** is missing{Environment.NewLine}", _stdout.ToString());
        Assert.Equal($"error: status found 1 mismatch: pr-7{Environment.NewLine}", _stderr.ToString());
    }
}
