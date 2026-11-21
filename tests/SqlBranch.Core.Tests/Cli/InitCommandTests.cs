using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

public sealed class InitCommandTests
{
    private const string Password = "Given#Passw0rd";

    private readonly FakeContainerRuntime _runtime = new();
    private readonly FakeEngineConnector _connector = new();
    private readonly FakeInitHost _host;
    private readonly Dictionary<string, string> _environment = [];

    public InitCommandTests()
    {
        _host = new FakeInitHost(_runtime, _connector);
    }

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var services = new CliServices(_runtime, _connector, _environment, "/work", "/home/u", ManagedPaths.Default,
            new EngineTimings { ReadyTimeout = TimeSpan.FromMilliseconds(100), PollInterval = TimeSpan.FromMilliseconds(5) });
        var redactor = new SecretRedactor();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.CreateDefault(redactor, services).Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Init_prints_the_engine_the_pool_and_the_clone_mode_and_never_the_password()
    {
        _environment[SettingKeys.SaPasswordVariable] = Password;
        _environment["SQLBRANCH_POOL_BACKEND"] = "plain";
        _environment["SQLBRANCH_POOL"] = FakeInitHost.PoolDir;

        var text = Run("init");
        var json = Run("init", "--format", "json");

        Assert.Equal(0, text.Exit);
        Assert.Contains("Engine: SQL Server 16.0.4325.4, Developer Edition (64-bit)", text.Out, StringComparison.Ordinal);
        Assert.Contains("Clone mode: copy", text.Out, StringComparison.Ordinal);
        Assert.Contains("Engine container: sqlbranch-engine on 127.0.0.1:14330", text.Out, StringComparison.Ordinal);
        Assert.Equal(0, json.Exit);
        var root = JsonDocument.Parse(json.Out).RootElement;
        Assert.Equal("init", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("repeated").GetBoolean());
        Assert.Equal("plain", root.GetProperty("pool").GetProperty("backend").GetString());
        Assert.Equal(14330, root.GetProperty("engine").GetProperty("port").GetInt32());
        foreach (var output in new[] { text.Out, text.Err, json.Out, json.Err })
        {
            Assert.DoesNotContain(Password, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_weak_password_exits_2_before_any_container_is_touched()
    {
        _environment[SettingKeys.SaPasswordVariable] = "abc";

        var run = Run("init", "--format", "json");

        Assert.Equal(2, run.Exit);
        Assert.Equal("config_error", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public void Docker_not_available_exits_3()
    {
        _runtime.InfoFailure = new SqlBranchException(ErrorCodes.DockerUnavailable, "The 'docker' command was not found.");

        var run = Run("init", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("docker_unavailable", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void The_config_flag_is_used_for_the_settings()
    {
        var dir = Directory.CreateTempSubdirectory("sqlbranch-init-");
        try
        {
            var file = Path.Combine(dir.FullName, "custom.yml");
            File.WriteAllText(file, $"version: 1\nport: 14999\npoolBackend: plain\npool: {FakeInitHost.PoolDir}\n");

            var run = Run("init", "--config", file);

            Assert.Equal(0, run.Exit);
            Assert.Contains(_runtime.Runs, r => r.Name == ContainerLabels.EngineName && r.Ports.Single().HostPort == 14999);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_failed_first_init_exits_1_with_init_failed()
    {
        _environment["SQLBRANCH_POOL_BACKEND"] = "plain";
        _connector.FailAlterLogin = true;

        var run = Run("init", "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("init_failed", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_runtime.Containers);
    }

    [Fact]
    public void A_foreign_container_exits_3()
    {
        _runtime.Containers[ContainerLabels.EngineName] =
            new ContainerInfo(ContainerLabels.EngineName, "postgres:16", true, new Dictionary<string, string>(), []);

        var run = Run("init", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("foreign_container", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(_runtime.Containers[ContainerLabels.EngineName].Running);
    }

    [Fact]
    public void Init_names_the_edition_and_says_the_engine_is_for_development_and_test_only()
    {
        _environment["SQLBRANCH_POOL_BACKEND"] = "plain";
        _environment["SQLBRANCH_POOL"] = FakeInitHost.PoolDir;

        var text = Run("init");
        var json = Run("init", "--format", "json");

        Assert.Equal(0, text.Exit);
        Assert.Contains("Edition: Developer Edition (64-bit). This engine is for development and test use only.", text.Out, StringComparison.Ordinal);
        var notice = JsonDocument.Parse(json.Out).RootElement.GetProperty("notice").GetString();
        Assert.Contains("Developer Edition (64-bit)", notice, StringComparison.Ordinal);
        Assert.Contains("development and test use only", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_warns_on_standard_error_when_the_host_is_not_x86_64_and_when_the_mode_is_copy_and_still_exits_0()
    {
        _environment["SQLBRANCH_POOL_BACKEND"] = "plain";
        _environment["SQLBRANCH_POOL"] = FakeInitHost.PoolDir;
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");

        var text = Run("init");
        var json = Run("init", "--format", "json");

        Assert.Equal(0, text.Exit);
        Assert.Contains("warning: The container host is aarch64, not x86-64 Linux", text.Err, StringComparison.Ordinal);
        Assert.Contains("emulation", text.Err, StringComparison.Ordinal);
        Assert.Contains("warning: The clone mode is copy", text.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("warning:", text.Out, StringComparison.Ordinal);
        // JSON on standard output stays one document.
        Assert.Equal(0, json.Exit);
        Assert.True(JsonDocument.Parse(json.Out).RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("aarch64", json.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_prints_no_host_warning_on_x86_64_with_thin_mode()
    {
        _environment["SQLBRANCH_POOL_BACKEND"] = "plain";
        _environment["SQLBRANCH_POOL"] = FakeInitHost.PoolDir;
        _host.DirectoryReflink = true;
        _host.EngineProbe = "thin";

        var run = Run("init");

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain("warning:", run.Err, StringComparison.Ordinal);
        Assert.Contains("development and test use only", run.Out, StringComparison.Ordinal);
    }
}
