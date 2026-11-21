using System.Text.Json;
using SqlBranch.Core.Pools;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Tasks 7.1 to 7.4: the engine commands with an image and branches in the pool. One engine for the class
/// (its own <c>it-&lt;id&gt;</c> name, port, pool folder and volume), with the image <c>shop</c> built once from a backup of a database
/// with 100 rows. Each test makes its own branches and removes them again.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EngineWithImagesIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    private BranchKit Kit => new(fixture, output);

    private EngineWorld World => fixture.World!;

    private async Task<string> EngineContainerIdentityAsync()
    {
        var inspect = await EngineWorld.DockerAsync("inspect", "-f", "{{.Id}} {{.State.StartedAt}}", World.EngineName);
        Assert.Equal(0, inspect.ExitCode);
        return inspect.StdOut.Trim();
    }

    private async Task<long> RowsAsync(string branch)
    {
        await using var connection = await Kit.ConnectAsync(branch);
        return await BranchKit.CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item");
    }

    private async Task AddRowAsync(string branch, int id)
    {
        await using var connection = await Kit.ConnectAsync(branch);
        await BranchKit.ExecuteAsync(connection, $"INSERT dbo.Item (Id, Name) VALUES ({id}, N'written'); CHECKPOINT;");
    }

    [IntegrationFact]
    public async Task Init_run_again_with_one_image_and_two_branches_exits_0_and_keeps_the_engine_the_image_and_the_branches()
    {
        Assert.True(fixture.Init.Exit == 0, fixture.Init.Out + fixture.Init.Err);
        await Kit.EnsureImageAsync("shop");
        try
        {
            await Kit.CreateAsync("pr-a", "shop");
            await Kit.CreateAsync("pr-b", "shop");
            await AddRowAsync("pr-a", 2001);
            var before = await EngineContainerIdentityAsync();

            var again = World.RunCli("init", "--format", "json");
            output.WriteLine(again.Out + again.Err);

            Assert.Equal(0, again.Exit);
            Assert.Equal(before, await EngineContainerIdentityAsync());
            var branches = BranchKit.Parse(World.RunCli("branch", "list", "--format", "json").Out).GetProperty("branches").EnumerateArray().ToList();
            Assert.Equal(["pr-a", "pr-b"], branches.Select(b => b.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
            Assert.All(branches, b => Assert.Equal("online", b.GetProperty("state").GetString()));
            var images = BranchKit.Parse(World.RunCli("image", "list", "--format", "json").Out).GetProperty("images").EnumerateArray().ToList();
            Assert.Contains(images, i => i.GetProperty("name").GetString() == "shop");
            Assert.Equal(101, await RowsAsync("pr-a"));
            Assert.Equal(100, await RowsAsync("pr-b"));
            Kit.AssertStatusIsClean();
        }
        finally
        {
            await Kit.DropBranchAsync("pr-a");
            await Kit.DropBranchAsync("pr-b");
        }
    }

    [IntegrationFact]
    public async Task The_password_is_in_no_output_of_any_command_but_branch_conn_including_lists_warnings_and_a_forced_error()
    {
        await Kit.EnsureImageAsync("shop");
        var password = World.Password;
        var seen = new List<string>();

        void Check(params string[] args) => CheckExpecting(null, null, args);

        // A forced error: both formats must exit with the spec's code, and in JSON carry the spec's error.code.
        void CheckError(int exit, string? code, params string[] args) => CheckExpecting(exit, code, args);

        void CheckExpecting(int? exit, string? code, string[] args)
        {
            foreach (var format in new[] { Array.Empty<string>(), ["--format", "json"] })
            {
                var run = World.RunCli([.. args, .. format]);
                seen.Add($"{string.Join(' ', args)} {string.Join(' ', format)} -> {run.Exit}");
                Assert.DoesNotContain(password, run.Out + run.Err, StringComparison.Ordinal);
                if (exit is { } expected)
                {
                    Assert.True(run.Exit == expected, $"{string.Join(' ', args)} {string.Join(' ', format)} exited {run.Exit}, expected {expected}: {run.Out}{run.Err}");
                    if (code is not null && format.Length > 0)
                    {
                        Assert.Equal(code, BranchKit.ErrorCode(run.Out));
                    }
                }
            }
        }

        try
        {
            // The first init (text), whose output the fixture kept.
            Assert.True(fixture.Init.Exit == 0, fixture.Init.Out + fixture.Init.Err);
            Assert.DoesNotContain(password, fixture.Init.Out + fixture.Init.Err, StringComparison.Ordinal);
            Check("init");
            Check("status");
            Check("doctor");
            Check("image", "list");
            Check("branch", "list");

            // Mutating commands run once in each format, on names of their own.
            foreach (var format in new[] { Array.Empty<string>(), ["--format", "json"] })
            {
                var tag = format.Length == 0 ? "text" : "json";
                var create = World.RunCli(["branch", "create", $"pw-{tag}", "--from", "shop", .. format]);
                Assert.True(create.Exit == 0, create.Out + create.Err);
                // --copy on a thin pool is the command's warning path.
                var copy = World.RunCli(["branch", "create", $"pwc-{tag}", "--from", "shop", "--copy", .. format]);
                Assert.True(copy.Exit == 0, copy.Out + copy.Err);
                var reset = World.RunCli(["branch", "reset", $"pw-{tag}", .. format]);
                Assert.True(reset.Exit == 0, reset.Out + reset.Err);
                foreach (var run in new[] { create, copy, reset })
                {
                    Assert.DoesNotContain(password, run.Out + run.Err, StringComparison.Ordinal);
                }
            }

            Check("branch", "list");
            Check("status");

            // Successful image build and removal of a throwaway image, and a successful branch rm, in both formats.
            var bak = Path.Combine(fixture.ClientFolder, "Shop.bak");
            if (!File.Exists(bak))
            {
                bak = await fixture.MakeBackupAsync("Shop");
            }

            foreach (var format in new[] { Array.Empty<string>(), ["--format", "json"] })
            {
                var tag = format.Length == 0 ? "text" : "json";
                var image = $"pwimg-{tag}";
                var build = World.RunCli(["image", "build", image, "--from-bak", bak, .. format]);
                Assert.True(build.Exit == 0, build.Out + build.Err);
                var rmBranch = World.RunCli(["branch", "rm", $"pw-{tag}", .. format]);
                Assert.True(rmBranch.Exit == 0, rmBranch.Out + rmBranch.Err);
                var rmImage = World.RunCli(["image", "rm", image, .. format]);
                Assert.True(rmImage.Exit == 0, rmImage.Out + rmImage.Err);
                foreach (var run in new[] { build, rmBranch, rmImage })
                {
                    Assert.DoesNotContain(password, run.Out + run.Err, StringComparison.Ordinal);
                }
            }

            // Forced errors.
            CheckError(1, "branch_not_found", "branch", "rm", "nope");
            CheckError(1, "branch_not_found", "branch", "conn", "nope");
            CheckError(1, "image_not_found", "branch", "create", "bad", "--from", "no-such-image");
            CheckError(2, "usage_error", "image", "build", "bad", "--from-bak", "/no/such/file.bak");
            CheckError(1, "image_in_use", "image", "rm", "shop");
            CheckError(2, "usage_error", "teardown");
            CheckError(2, "invalid_name", "branch", "create", "Bad Name", "--from", "shop");

            // A mismatch in status: pw-text and pw-json were removed above, so drop pwc-text behind the tool's back.
            await fixture.SqlAsync("ALTER DATABASE [pwc-text] SET SINGLE_USER WITH ROLLBACK IMMEDIATE", "DROP DATABASE [pwc-text]");
            Check("status");

            // The engine restarting.
            foreach (var format in new[] { new[] { "--format", "json" }, [] })
            {
                var stop = World.RunCli(["engine", "stop", .. format]);
                var start = World.RunCli(["engine", "start", .. format]);
                Assert.Equal(0, stop.Exit);
                Assert.Equal(0, start.Exit);
                Assert.DoesNotContain(password, stop.Out + stop.Err + start.Out + start.Err, StringComparison.Ordinal);
            }

            // The one command that does print it, so the checks above are not vacuous.
            var conn = World.RunCli("branch", "conn", "pwc-json");
            Assert.Equal(0, conn.Exit);
            Assert.Contains(password, conn.Out, StringComparison.Ordinal);
            output.WriteLine(string.Join(Environment.NewLine, seen));
        }
        finally
        {
            foreach (var name in new[] { "pw-text", "pw-json", "pwc-text", "pwc-json" })
            {
                await Kit.DropBranchAsync(name);
            }
        }
    }

    [IntegrationFact]
    public async Task A_row_written_to_a_branch_survives_engine_stop_and_start_and_an_unmount_followed_by_engine_start()
    {
        await Kit.EnsureImageAsync("shop");
        try
        {
            await Kit.CreateAsync("pr-keep", "shop");
            await AddRowAsync("pr-keep", 3001);
            Assert.Equal(101, await RowsAsync("pr-keep"));

            Assert.Equal(0, World.RunCli("engine", "stop").Exit);
            var start = World.RunCli("engine", "start");
            output.WriteLine($"engine start: {start.Elapsed.TotalSeconds:F1} s");
            Assert.True(start.Exit == 0, start.Out + start.Err);
            Assert.Equal(101, await RowsAsync("pr-keep"));
            Kit.AssertStatusIsClean();

            // The managed pool is unmounted while the engine is stopped, and engine start mounts it again. A plain pool has no mount.
            if (!World.IsPlain)
            {
                Assert.Equal(0, World.RunCli("engine", "stop").Exit);
                var pool = new ManagedPool(World.Runtime, World.Paths);
                await pool.UnmountAsync(CancellationToken.None);
                Assert.False(await pool.IsMountedAsync(CancellationToken.None));
                var restart = World.RunCli("engine", "start");
                Assert.True(restart.Exit == 0, restart.Out + restart.Err);
                Assert.True(await pool.IsMountedAsync(CancellationToken.None));
                Assert.Equal(101, await RowsAsync("pr-keep"));
                Kit.AssertStatusIsClean();
            }
        }
        finally
        {
            await Kit.DropBranchAsync("pr-keep");
        }
    }

    [IntegrationFact]
    public async Task Status_exits_1_and_names_pr_7_after_the_branch_database_is_dropped_behind_the_tools_back()
    {
        await Kit.EnsureImageAsync("shop");
        try
        {
            await Kit.CreateAsync("pr-7", "shop");
            Kit.AssertStatusIsClean();

            await fixture.SqlAsync("ALTER DATABASE [pr-7] SET SINGLE_USER WITH ROLLBACK IMMEDIATE", "DROP DATABASE [pr-7]");

            var text = World.RunCli("status");
            var json = World.RunCli("status", "--format", "json");
            output.WriteLine(text.Out + text.Err);
            output.WriteLine(json.Out + json.Err);
            Assert.Equal(1, text.Exit);
            Assert.Equal(1, json.Exit);
            Assert.Contains("pr-7", text.Out + text.Err, StringComparison.Ordinal);
            var named = JsonDocument.Parse(json.Out).RootElement.GetProperty("mismatches").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString()).ToList();
            Assert.Contains("pr-7", named);
        }
        finally
        {
            await Kit.DropBranchAsync("pr-7");
        }

        Kit.AssertStatusIsClean();
    }
}

/// <summary>
/// Task 7.5: <c>teardown --force</c> on a managed pool that holds an image, a branch and a user database <c>scratch</c>. Each test builds
/// its own engine world (its own <c>it-&lt;id&gt;</c> engine, port, pool folder and volume) and tears down only that.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TeardownWithImagesIntegrationTests(ITestOutputHelper output)
{
    private enum Start
    {
        Running,
        EngineStopped,
        PoolUnmounted,
    }

    private async Task RunScenarioAsync(Start start, bool json = false)
    {
        if (start == Start.PoolUnmounted)
        {
            SkipTestException.UnlessManagedRequested();
        }

        var fixture = new ImageEngineFixture();
        try
        {
            await fixture.InitializeAsync();
            var world = fixture.World!;
            Assert.True(fixture.Init.Exit == 0, fixture.Init.Out + fixture.Init.Err);
            var kit = new BranchKit(fixture, output);
            await kit.EnsureImageAsync("shop");
            await kit.CreateAsync("pr-t", "shop");
            await fixture.SqlAsync("CREATE DATABASE [scratch]");
            var pool = new ManagedPool(world.Runtime, world.Paths);

            if (start != Start.Running)
            {
                Assert.Equal(0, world.RunCli("engine", "stop").Exit);
            }

            if (start == Start.PoolUnmounted)
            {
                await pool.UnmountAsync(CancellationToken.None);
                Assert.False(await pool.IsMountedAsync(CancellationToken.None));
            }

            var text = json ? world.RunCli("teardown", "--force", "--format", "json") : world.RunCli("teardown", "--force");
            output.WriteLine(text.Out + text.Err);
            Assert.Equal(0, text.Exit);
            if (json)
            {
                Assert.Contains("scratch", text.Out, StringComparison.Ordinal);
                Assert.Contains("pr-t", text.Out, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Removed database: scratch", text.Out, StringComparison.Ordinal);
                Assert.Contains("Removed database: pr-t", text.Out, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(world.Password, text.Out + text.Err, StringComparison.Ordinal);

            Assert.Null(await world.Runtime.InspectAsync(world.EngineName, CancellationToken.None));
            var names = await DockerHost.ManagedContainerNamesAsync(world.Runtime);
            Assert.DoesNotContain(names, n => n.Contains(world.Id, StringComparison.Ordinal));

            // No labeled container is left that mounts this world's pool folder or volume (a leaked helper or work container),
            // whatever it is called. Only this world's own folder and volume are compared, so other tests running in parallel do not matter.
            foreach (var name in names)
            {
                var mounts = await EngineWorld.DockerAsync("inspect", "-f", "{{range .Mounts}}{{.Source}}|{{.Name}}{{println}}{{end}}", name);
                if (mounts.ExitCode != 0)
                {
                    continue; // removed between the list and the inspect
                }

                foreach (var line in mounts.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split('|');
                    Assert.False(
                        parts[0] == world.Paths.Root || parts[0].StartsWith(world.Paths.Root + "/", StringComparison.Ordinal) || (parts.Length > 1 && parts[1] == world.PoolVolume),
                        $"container {name} still mounts this world's pool: {line}");
                }
            }

            Assert.False(await pool.FileExistsAsync(CancellationToken.None));
            Assert.False(await pool.IsMountedAsync(CancellationToken.None));

            // Everything is gone, so a second teardown has nothing to find.
            Assert.Equal(3, world.RunCli("teardown", "--force").Exit);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationFact]
    public Task Teardown_force_with_an_image_a_branch_and_a_user_database_lists_scratch_and_leaves_no_container_and_no_pool_file() =>
        RunScenarioAsync(Start.Running);

    [IntegrationFact]
    public Task Teardown_force_does_the_same_with_the_engine_stopped() => RunScenarioAsync(Start.EngineStopped);

    [IntegrationThinFact]
    public Task Teardown_force_does_the_same_after_the_pool_was_unmounted() => RunScenarioAsync(Start.PoolUnmounted);

    [IntegrationFact]
    public Task Teardown_force_in_json_names_the_removed_databases_and_prints_no_password() => RunScenarioAsync(Start.Running, json: true);
}

/// <summary>Task 7.6: a <c>poolSize</c> of 100GB gives a pool of 99 to 101 GB. Needs Docker Desktop and 100 GB free in its VM.</summary>
[Trait("Category", "Integration")]
public sealed class LargePoolIntegrationTests(ITestOutputHelper output)
{
    private const long Gigabyte = 1_000_000_000;

    [IntegrationThinFact]
    public async Task A_pool_size_of_100GB_gives_a_pool_total_between_99_and_101_GB_on_Docker_Desktop()
    {
        SkipTestException.UnlessManagedRequested();
        var info = await EngineWorld.DockerAsync("info", "--format", "{{.OperatingSystem}}");
        if (!info.StdOut.Contains("Docker Desktop", StringComparison.Ordinal))
        {
            throw new SkipTestException($"The container host is '{info.StdOut.Trim()}', not Docker Desktop.");
        }

        await using var world = new EngineWorld("100GB");
        var free = await new ManagedPool(world.Runtime, world.Paths).FreeBytesAsync(CancellationToken.None);
        output.WriteLine($"free space in the container host: {free} bytes ({free / Gigabyte} GB)");
        if (free < 100 * Gigabyte)
        {
            throw new SkipTestException($"The container host has {free / Gigabyte} GB free, and a 100GB pool needs 100 GB.");
        }

        var init = world.RunCli("init", "--format", "json");
        output.WriteLine(init.Out + init.Err);
        Assert.Equal(0, init.Exit);

        var status = world.RunCli("status", "--format", "json");
        Assert.Equal(0, status.Exit);
        var total = JsonDocument.Parse(status.Out).RootElement.GetProperty("pool").GetProperty("totalBytes").GetInt64();
        output.WriteLine($"pool total: {total} bytes");
        Assert.InRange(total, 99 * Gigabyte, 101 * Gigabyte);

        // Remove the sparse pool file now rather than only at dispose.
        Assert.Equal(0, world.RunCli("teardown", "--force").Exit);
        Assert.False(await new ManagedPool(world.Runtime, world.Paths).FileExistsAsync(CancellationToken.None));
    }
}
