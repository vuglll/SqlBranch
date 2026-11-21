using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 4.17 against a real engine. Every test uses an <see cref="EngineWorld"/>: its own engine name,
/// port, pool folder under <c>/var/lib/sqlbranch/it-&lt;id&gt;</c> and pool volume name, so the product's
/// <c>sqlbranch-engine</c>, 14330, <c>/var/lib/sqlbranch/pool.xfs</c> and <c>sqlbranch-pool</c> are never looked at.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TeardownIntegrationTests(ITestOutputHelper output)
{
    private static async Task<string> ListFolderAsync(DockerRuntime runtime, string folder)
    {
        // A read-only look at a folder under the managed root, from a helper that is removed after.
        var name = $"sqlbranch-it-ls-{DockerHost.NewId()}";
        var spec = new ContainerSpec
        {
            Name = name,
            Image = ManagedPool.HelperImage,
            RemoveWhenDone = true,
            Mounts = [new MountSpec(MountKind.Bind, ManagedPaths.DefaultRoot, ManagedPaths.DefaultRoot, ReadOnly: true)],
            Command = ["sh", "-c", $"if [ -d '{folder}' ]; then ls -A '{folder}'; else echo SQLBRANCH_NOFOLDER; fi"],
        };
        try
        {
            return (await runtime.RunAsync(spec, CancellationToken.None)).StdOut.Trim();
        }
        finally
        {
            await runtime.RemoveAsync(name, CancellationToken.None);
        }
    }

    private static async Task AssertNoLabeledContainerOf(EngineWorld world)
    {
        var names = await DockerHost.ManagedContainerNamesAsync(world.Runtime);
        Assert.DoesNotContain(names, n => n.Contains(world.Id, StringComparison.Ordinal));
    }

    [IntegrationFact]
    public async Task An_empty_plain_pool_keeps_its_directory_and_readme_txt()
    {
        await using var world = new EngineWorld();
        await using var directory = new HostPoolDirectory(world.Runtime);
        world.Environment["SQLBRANCH_POOL_BACKEND"] = "plain";
        world.Environment["SQLBRANCH_POOL"] = directory.Path;

        var init = world.RunCli("init", "--format", "json");
        output.WriteLine(init.Out + init.Err);
        Assert.True(init.Exit == 0, init.Out + init.Err);
        var add = await PoolWork.RunAsync(world.Runtime, DockerHost.Image, directory.Location, "echo keep > /pool/readme.txt", readOnly: false, CancellationToken.None);
        Assert.True(add.Succeeded, add.StdErr);

        var run = world.RunCli("teardown", "--force");

        output.WriteLine(run.Out + run.Err);
        Assert.Equal(0, run.Exit);
        Assert.True(Directory.Exists(directory.Path));
        var left = await PoolWork.RunAsync(world.Runtime, DockerHost.Image, directory.Location, "ls -A /pool", readOnly: true, CancellationToken.None);
        Assert.Equal("readme.txt", left.StdOut.Trim());
        Assert.Null(await world.Runtime.InspectAsync(world.EngineName, CancellationToken.None));
        await AssertNoLabeledContainerOf(world);

        var again = world.RunCli("teardown", "--force", "--format", "json");
        Assert.Equal(3, again.Exit);
        Assert.Equal("not_initialized", JsonDocument.Parse(again.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [IntegrationThinFact]
    public async Task An_empty_managed_pool_is_removed_with_its_mount_point_and_a_user_database_is_listed()
    {
        SkipTestException.UnlessManagedRequested();
        await using var world = new EngineWorld();
        var init = world.RunCli("init", "--format", "json");
        Assert.True(init.Exit == 0, init.Out + init.Err);
        await using (var session = await new SqlClientEngineConnector().ConnectAsync(world.Endpoint, world.Password, CancellationToken.None))
        {
            await session.ExecuteAsync("CREATE DATABASE scratch", CancellationToken.None);
        }

        var run = world.RunCli("teardown", "--force", "--format", "json");

        output.WriteLine(run.Out + run.Err);
        Assert.Equal(0, run.Exit);
        var removed = JsonDocument.Parse(run.Out).RootElement.GetProperty("removed");
        Assert.Equal(["scratch"], removed.GetProperty("databases").EnumerateArray().Select(e => e.GetString()));
        var pool = new ManagedPool(world.Runtime, world.Paths);
        Assert.False(await pool.FileExistsAsync(CancellationToken.None));
        Assert.False(await pool.IsMountedAsync(CancellationToken.None));
        // The unmount leaves no empty mount point behind: the test's own folder is empty.
        Assert.Equal(string.Empty, await ListFolderAsync(world.Runtime, world.Paths.Root));
        Assert.Null(await world.Runtime.InspectAsync(world.EngineName, CancellationToken.None));
        await AssertNoLabeledContainerOf(world);
        Assert.DoesNotContain(world.Password, run.Out + run.Err, StringComparison.Ordinal);

        Assert.Equal(3, world.RunCli("teardown", "--force").Exit);
    }

    [IntegrationThinFact]
    public async Task A_managed_pool_that_was_unmounted_with_the_engine_stopped_is_still_removed_and_lists_the_user_database()
    {
        SkipTestException.UnlessManagedRequested();
        await using var world = new EngineWorld();
        Assert.Equal(0, world.RunCli("init").Exit);
        await using (var session = await new SqlClientEngineConnector().ConnectAsync(world.Endpoint, world.Password, CancellationToken.None))
        {
            await session.ExecuteAsync("CREATE DATABASE scratch", CancellationToken.None);
        }

        Assert.Equal(0, world.RunCli("engine", "stop").Exit);
        var pool = new ManagedPool(world.Runtime, world.Paths);
        await pool.UnmountAsync(CancellationToken.None);
        Assert.False(await pool.IsMountedAsync(CancellationToken.None));

        var run = world.RunCli("teardown", "--force");

        output.WriteLine(run.Out + run.Err);
        Assert.Equal(0, run.Exit);
        Assert.Contains("Removed database: scratch", run.Out, StringComparison.Ordinal);
        Assert.False(await pool.FileExistsAsync(CancellationToken.None));
        Assert.False(await pool.IsMountedAsync(CancellationToken.None));
        Assert.Equal(string.Empty, await ListFolderAsync(world.Runtime, world.Paths.Root));
        await AssertNoLabeledContainerOf(world);
    }
}

/// <summary>Task 4.18: the commands in docs/engine.md run as written, with the exit codes it states.</summary>
[Trait("Category", "Integration")]
public sealed partial class EngineDocsIntegrationTests(ITestOutputHelper output)
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^sqlbranch (?<args>[^#]*?)\s*(#\s*exit (?<exit>\d))?\s*$")]
    private static partial System.Text.RegularExpressions.Regex CommandLine();

    private static string DocsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlBranch.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "docs", "engine.md");
    }

    [IntegrationFact]
    public async Task The_documented_commands_run_as_written_on_an_empty_pool_with_the_documented_exit_codes()
    {
        var commands = new List<(string[] Args, int Exit)>();
        var inBlock = false;
        foreach (var line in File.ReadAllLines(DocsPath()))
        {
            if (line.StartsWith("```sh", StringComparison.Ordinal))
            {
                inBlock = true;
            }
            else if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inBlock = false;
            }
            else if (inBlock && CommandLine().Match(line) is { Success: true } match)
            {
                commands.Add(([.. match.Groups["args"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
                    int.Parse(match.Groups["exit"].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        Assert.True(commands.Count >= 8, "docs/engine.md documents too few commands.");
        Assert.Contains(commands, c => c.Args.SequenceEqual(["teardown", "--force"]));

        await using var world = new EngineWorld();
        foreach (var (args, exit) in commands)
        {
            var run = world.RunCli(args);
            output.WriteLine($"sqlbranch {string.Join(' ', args)} -> {run.Exit} ({run.Elapsed.TotalSeconds:F1} s)");
            Assert.True(exit == run.Exit, $"sqlbranch {string.Join(' ', args)} exited {run.Exit}, the document says {exit}.\n{run.Out}\n{run.Err}");
            Assert.DoesNotContain(world.Password, run.Out + run.Err, StringComparison.Ordinal);
        }

        Assert.False(await new ManagedPool(world.Runtime, world.Paths).FileExistsAsync(CancellationToken.None));
    }
}
