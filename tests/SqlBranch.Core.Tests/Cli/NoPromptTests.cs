using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlBranch.Core.Output;
using SqlBranch.Cli;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>Console.In is process-wide, so these tests do not run in parallel with others.</summary>
[CollectionDefinition(nameof(ConsoleInput), DisableParallelization = true)]
public sealed class ConsoleInput;

[Collection(nameof(ConsoleInput))]
public sealed class NoPromptTests
{
    internal static CommandMetadata ProbeMetadata { get; } = new()
    {
        Purpose = "Exercise the test handler.", WhenToUse = "During this unit test.", Example = "sqlbranch probe", Destroys = "nothing",
    };

    private sealed class ThrowingReader : TextReader
    {
        public override int Peek() => throw new InvalidOperationException("A command tried to read standard input.");

        public override int Read() => throw new InvalidOperationException("A command tried to read standard input.");
    }

    /// <summary>Runs every registered command with its sample arguments and standard input that throws.</summary>
    private static async Task AssertNoCommandReadsInput(Func<CliApp> create)
    {
        var original = Console.In;
        Console.SetIn(new ThrowingReader());
        try
        {
            foreach (var command in create().Registered)
            {
                string[] args = [.. command.Path, .. command.SampleArgs, "--format", "json"];
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                var run = Task.Run(() => create().Run(args, stdout, stderr));

                var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
                var line = $"sqlbranch {string.Join(' ', args)}";
                Assert.True(finished == run, $"'{line}' did not finish.");

                // A usage error means the sample arguments are wrong and the handler never ran.
                var error = JsonDocument.Parse(stdout.ToString()).RootElement.TryGetProperty("error", out var e)
                    ? e.GetProperty("code").GetString()
                    : null;
                Assert.True(error != "usage_error", $"'{line}' hit a usage error; give the command valid sample arguments. {stdout}");
                Assert.DoesNotContain("standard input", stdout + stderr.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    /// <summary>Services with no Docker and no engine behind them: nothing here can reach the real <c>docker</c>.</summary>
    internal static CliServices FakeServices()
    {
        var runtime = new FakeContainerRuntime();
        var connector = new FakeEngineConnector();
        _ = new FakeInitHost(runtime, connector);
        return new CliServices(runtime, connector, new Dictionary<string, string>(), "/work", "/home/u",
            ManagedPaths.Default, new EngineTimings { ReadyTimeout = TimeSpan.FromMilliseconds(50), PollInterval = TimeSpan.FromMilliseconds(5) });
    }

    [Fact]
    public async Task No_registered_command_waits_for_standard_input()
    {
        var directory = Directory.CreateTempSubdirectory("sqlbranch-no-prompt-");
        try
        {
            // The sample archive of `image import` is read before the engine is looked for, so it must be a file.
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "shop.sbimg"), "not an archive");
            await AssertNoCommandReadsInput(() => CliApp.CreateDefault(new SecretRedactor(), FakeServices() with { WorkingDirectory = directory.FullName }));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task The_check_runs_commands_that_have_required_arguments()
    {
        var invoked = 0;
        CliApp Create()
        {
            var app = new CliApp(new SecretRedactor());
            var command = new Command("probe");
            command.Arguments.Add(new Argument<string>("name"));
            app.AddCommand(app.Root, command, NoPromptTests.ProbeMetadata, _ =>
            {
                Interlocked.Increment(ref invoked);
                return CommandResult.Success("probe", new JsonObject(), ["ok"], ["ok"]);
            }, ["demo"]);
            return app;
        }

        await AssertNoCommandReadsInput(Create);

        Assert.Equal(1, invoked);
    }

    [Fact]
    public async Task The_check_fails_when_sample_arguments_do_not_parse()
    {
        CliApp Create()
        {
            var app = new CliApp(new SecretRedactor());
            var command = new Command("probe");
            command.Arguments.Add(new Argument<string>("name"));
            app.AddCommand(app.Root, command, NoPromptTests.ProbeMetadata, _ => CommandResult.Success("probe", new JsonObject(), ["ok"], ["ok"]));
            return app;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => AssertNoCommandReadsInput(Create));
    }
}
