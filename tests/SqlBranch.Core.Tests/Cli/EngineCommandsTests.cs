using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlBranch.Cli;
using SqlBranch.Cli.Commands;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Core.Tests.Cli;

public sealed class EngineCommandsTests
{
    private readonly CliWorld _world = new();

    private static string ErrorCode(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    [Theory]
    [InlineData("engine", "start")]
    [InlineData("engine", "stop")]
    [InlineData("status")]
    public void Commands_before_init_exit_3_not_initialized_and_name_init(params string[] command)
    {
        var run = _world.Run([.. command, "--format", "json"]);

        Assert.Equal(3, run.Exit);
        Assert.Equal("not_initialized", ErrorCode(run.Out));
        Assert.Contains("sqlbranch init", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("start", StringComparison.Ordinal) || c.StartsWith("stop", StringComparison.Ordinal));
    }

    [Fact]
    public void Image_and_branch_commands_use_the_same_guard_and_exit_3_before_init()
    {
        // The real image and branch commands arrive in later groups and start with CommandContext.RequireAsync.
        // These two stand in for them and must fail the same way.
        var app = new CliApp(_world.Redactor);
        foreach (var group in new[] { "image", "branch" })
        {
            var parent = new Command(group);
            app.Root.Subcommands.Add(parent);
            app.AddCommand(parent, new Command("list"), NoPromptTests.ProbeMetadata, _ =>
            {
                CommandContext.RequireAsync(_world.Services(), _world.Redactor, null, CancellationToken.None).GetAwaiter().GetResult();
                return CommandResult.Success($"{group} list", new JsonObject(), ["never reached"]);
            });
        }

        foreach (var group in new[] { "image", "branch" })
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = app.Run([group, "list", "--format", "json"], stdout, stderr);

            Assert.Equal(3, exit);
            Assert.Equal("not_initialized", ErrorCode(stdout.ToString()));
        }
    }

    [Fact]
    public void The_lookup_finds_a_managed_pool_file_with_no_engine_but_start_cannot_create_the_engine()
    {
        _world.Host.PoolFileExists = true;

        var run = _world.Run("engine", "start", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("not_initialized", ErrorCode(run.Out));
        Assert.Contains("the engine container does not", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("start", StringComparison.Ordinal));
    }

    [Fact]
    public void Start_starts_a_stopped_engine_and_waits_for_a_login()
    {
        _world.WithEngine(running: false);

        var run = _world.Run("engine", "start", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Contains("start sqlbranch-engine", _world.Runtime.Calls);
        var root = JsonDocument.Parse(run.Out).RootElement;
        Assert.Equal("running", root.GetProperty("engine").GetProperty("state").GetString());
        Assert.False(root.GetProperty("engine").GetProperty("alreadyRunning").GetBoolean());
        Assert.DoesNotContain(CliWorld.StoredPassword, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_on_a_running_engine_exits_0_and_starts_nothing()
    {
        _world.WithEngine(running: true);

        var run = _world.Run("engine", "start");

        Assert.Equal(0, run.Exit);
        Assert.Contains("already running", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("start sqlbranch-engine", _world.Runtime.Calls);
    }

    [Fact]
    public void Start_remounts_a_missing_managed_pool_before_it_starts_the_engine()
    {
        _world.WithEngine(running: false, backend: "loop-xfs", pool: ManagedPaths.Default.MountPoint);
        _world.Host.PoolFileExists = true;
        _world.Host.Mounted = false;

        var run = _world.Run("engine", "start");

        Assert.Equal(0, run.Exit);
        Assert.True(_world.Host.Mounted);
        var mount = _world.Host.Scripts.ToList().FindIndex(s => s.Contains("mount -o loop", StringComparison.Ordinal));
        Assert.True(mount >= 0);
        var calls = _world.Runtime.Calls;
        var helperRun = calls.FindIndex(c => c.StartsWith("run sqlbranch-helper-mount", StringComparison.Ordinal));
        Assert.True(helperRun >= 0 && helperRun < calls.IndexOf("start sqlbranch-engine"));
    }

    [Fact]
    public void Start_exits_3_pool_not_mounted_and_does_not_start_the_engine_when_the_remount_fails()
    {
        _world.WithEngine(running: false, backend: "loop-xfs", pool: ManagedPaths.Default.MountPoint);
        _world.Host.PoolFileExists = true;
        _world.Host.FailMount = true;

        var run = _world.Run("engine", "start", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("pool_not_mounted", ErrorCode(run.Out));
        Assert.DoesNotContain("start sqlbranch-engine", _world.Runtime.Calls);
        Assert.False(_world.Runtime.Containers["sqlbranch-engine"].Running);
    }

    [Fact]
    public void Stop_stops_a_running_engine()
    {
        _world.WithEngine(running: true);

        var run = _world.Run("engine", "stop");

        Assert.Equal(0, run.Exit);
        Assert.Contains("stop sqlbranch-engine", _world.Runtime.Calls);
        Assert.False(_world.Runtime.Containers["sqlbranch-engine"].Running);
    }

    [Fact]
    public void Stop_on_a_stopped_engine_exits_0_and_changes_nothing()
    {
        _world.WithEngine(running: false);

        var first = _world.Run("engine", "stop", "--format", "json");
        var second = _world.Run("engine", "stop");

        Assert.Equal(0, first.Exit);
        Assert.Equal(0, second.Exit);
        Assert.Contains("already stopped", second.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("stop sqlbranch-engine", _world.Runtime.Calls);
    }

    [Fact]
    public void A_foreign_container_with_the_engines_name_is_never_started_or_stopped()
    {
        _world.Runtime.Containers[ContainerLabels.EngineName] =
            new ContainerInfo(ContainerLabels.EngineName, "postgres:16", true, new Dictionary<string, string>(), []);

        foreach (var command in new[] { "start", "stop" })
        {
            var run = _world.Run("engine", command, "--format", "json");

            Assert.Equal(3, run.Exit);
            Assert.Equal("foreign_container", ErrorCode(run.Out));
        }

        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("start", StringComparison.Ordinal) || c.StartsWith("stop", StringComparison.Ordinal) || c.StartsWith("rm", StringComparison.Ordinal));
    }
}
