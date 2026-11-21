using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Pools;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// One engine for the whole class: the first start under emulation takes
/// minutes, so every check that needs a running engine shares it.
/// </summary>
public sealed class EngineFixture : IAsyncLifetime
{
    public EngineWorld? World { get; private set; }

    public (int Exit, string Out, string Err, TimeSpan Elapsed) FirstInitText { get; private set; }

    public (int Exit, string Out, string Err, TimeSpan Elapsed) RepeatInitJson { get; private set; }

    public string InspectAfterInit { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnableVariable) != "1")
        {
            return;
        }

        World = new EngineWorld();
        FirstInitText = World.RunCli("init");
        if (FirstInitText.Exit == 0)
        {
            InspectAfterInit = (await EngineWorld.DockerAsync("inspect", World.EngineName)).StdOut;
            RepeatInitJson = World.RunCli("init", "--format", "json");
        }
    }

    public async Task DisposeAsync()
    {
        if (World is not null)
        {
            await World.DisposeAsync();
        }
    }
}

[Trait("Category", "Integration")]
public sealed class EngineInitIntegrationTests(EngineFixture fixture, ITestOutputHelper output) : IClassFixture<EngineFixture>
{
    private EngineWorld World => fixture.World!;

    [IntegrationFact]
    public async Task Init_starts_an_engine_that_takes_the_login_and_publishes_only_loopback()
    {
        Assert.True(fixture.FirstInitText.Exit == 0, fixture.FirstInitText.Out + fixture.FirstInitText.Err);
        output.WriteLine($"first init (pull cached, pool 2 GB, engine start included): {fixture.FirstInitText.Elapsed.TotalSeconds:F1} s");
        output.WriteLine(fixture.FirstInitText.Out);

        Assert.True(await World.CanLoginAsync(World.Password));

        var port = await EngineWorld.DockerAsync("port", World.EngineName);
        var lines = port.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal([$"1433/tcp -> 127.0.0.1:{World.Port}"], lines);

        var credentials = await PoolWork.RunAsync(World.Runtime, DockerHost.Image, World.Location,
            "stat -c '%a %u' /pool/engine/credentials.json", readOnly: true, CancellationToken.None);
        Assert.Equal("600 10001", credentials.LastLine);
    }

    [IntegrationThinFact]
    public async Task Init_sets_the_8_KiB_copy_on_write_hint_on_the_branches_folder_and_the_engine_user_owns_it()
    {
        SkipTestException.UnlessManaged(World);
        Assert.True(fixture.FirstInitText.Exit == 0, fixture.FirstInitText.Out + fixture.FirstInitText.Err);
        Assert.DoesNotContain("copy-on-write hint", fixture.FirstInitText.Err, StringComparison.Ordinal);

        var read = await PoolWork.RunAsync(World.Runtime, ManagedPool.HelperImage, World.Paths.Location,
            CowHint.ReadScript + "; stat -c 'OWNER=%u:%g' /pool/branches", readOnly: true, CancellationToken.None);

        output.WriteLine(read.StdOut + read.StdErr);
        Assert.Contains($"SQLBRANCH_COWSIZE={CowHint.Bytes}", read.StdOut, StringComparison.Ordinal);
        Assert.Contains("OWNER=10001:0", read.StdOut, StringComparison.Ordinal);
    }

    [IntegrationThinFact]
    public void The_first_init_names_what_it_created_and_the_non_secret_facts()
    {
        SkipTestException.UnlessManaged(World);
        var text = fixture.FirstInitText.Out;
        Assert.Contains($"Pool file: {World.Paths.PoolFile} (2 GB)", text, StringComparison.Ordinal);
        Assert.Contains($"Mount point: {World.Paths.MountPoint}", text, StringComparison.Ordinal);
        Assert.Contains($"Engine container: {World.EngineName}", text, StringComparison.Ordinal);
        Assert.Contains("Clone mode: thin", text, StringComparison.Ordinal);
        Assert.Contains("Pool backend: loop-xfs", text, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task A_repeated_init_exits_0_and_keeps_the_engine_and_the_pool()
    {
        var before = (await EngineWorld.DockerAsync("inspect", "--format", "{{.Id}}", World.EngineName)).StdOut.Trim();
        var poolFile = World.IsPlain ? 0 : await new ManagedPool(World.Runtime, World.Paths).FileSizeAsync(CancellationToken.None);

        var repeat = fixture.RepeatInitJson;

        Assert.True(repeat.Exit == 0, repeat.Out + repeat.Err);
        Assert.True(JsonDocument.Parse(repeat.Out).RootElement.GetProperty("repeated").GetBoolean());
        Assert.Equal(before, (await EngineWorld.DockerAsync("inspect", "--format", "{{.Id}}", World.EngineName)).StdOut.Trim());
        Assert.Equal(World.IsPlain ? 0 : 2_000_000_000, poolFile);
        Assert.True(await World.CanLoginAsync(World.Password));
    }

    [IntegrationFact]
    public async Task A_changed_port_or_password_is_refused_with_exit_2_and_the_engine_is_untouched()
    {
        var other = new Dictionary<string, string>(World.Environment) { ["SQLBRANCH_PORT"] = (World.Port + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var saved = new Dictionary<string, string>(World.Environment);

        World.Environment["SQLBRANCH_PORT"] = other["SQLBRANCH_PORT"];
        var port = World.RunCli("init");
        World.Environment["SQLBRANCH_PORT"] = saved["SQLBRANCH_PORT"];
        World.Environment["SQLBRANCH_SA_PASSWORD"] = "Other#Passw0rd1";
        var password = World.RunCli("init");
        World.Environment["SQLBRANCH_SA_PASSWORD"] = saved["SQLBRANCH_SA_PASSWORD"];

        Assert.Equal(2, port.Exit);
        Assert.Contains("port", port.Err, StringComparison.Ordinal);
        Assert.Equal(2, password.Exit);
        Assert.DoesNotContain("Other#Passw0rd1", password.Err + password.Out, StringComparison.Ordinal);
        Assert.True(await World.CanLoginAsync(World.Password));
    }

    [IntegrationFact]
    public async Task The_password_is_absent_from_the_init_output_and_from_docker_inspect_before_and_after_a_restart()
    {
        foreach (var text in new[] { fixture.FirstInitText.Out, fixture.FirstInitText.Err, fixture.RepeatInitJson.Out, fixture.RepeatInitJson.Err })
        {
            Assert.DoesNotContain(World.Password, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(World.Password, fixture.InspectAfterInit, StringComparison.Ordinal);
        var bootstrap = BootstrapPasswordOf(fixture.InspectAfterInit);
        Assert.NotEqual(World.Password, bootstrap);

        // D9: the container must not re-apply MSSQL_SA_PASSWORD when it starts again.
        await World.Runtime.StopAsync(World.EngineName, CancellationToken.None);
        await World.Runtime.StartAsync(World.EngineName, CancellationToken.None);
        var ready = System.Diagnostics.Stopwatch.StartNew();
        var up = false;
        while (ready.Elapsed < TimeSpan.FromSeconds(180) && !(up = await World.CanLoginAsync(World.Password)))
        {
            await Task.Delay(2000);
        }

        output.WriteLine($"restart: real password accepted after {ready.Elapsed.TotalSeconds:F1} s: {up}");
        Assert.True(up, "A login with the real password failed after the restart.");
        Assert.False(await World.CanLoginAsync(bootstrap), "The bootstrap password works again after the restart: the container re-applied MSSQL_SA_PASSWORD.");
        var after = (await EngineWorld.DockerAsync("inspect", World.EngineName)).StdOut;
        Assert.DoesNotContain(World.Password, after, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task Status_reports_the_running_engine_and_the_pool_and_never_the_password()
    {
        var text = World.RunCli("status");
        var json = World.RunCli("status", "--format", "json");
        output.WriteLine(text.Out);

        Assert.True(text.Exit == 0, text.Out + text.Err);
        Assert.Equal(0, json.Exit);
        Assert.Contains("Engine: running", text.Out, StringComparison.Ordinal);
        Assert.Contains(World.IsPlain ? "Clone mode: copy" : "Clone mode: thin", text.Out, StringComparison.Ordinal);
        Assert.Contains(World.IsPlain ? "Pool backend: plain" : "Pool backend: loop-xfs", text.Out, StringComparison.Ordinal);
        Assert.Contains("Images: 0", text.Out, StringComparison.Ordinal);
        Assert.Contains("Branches: 0", text.Out, StringComparison.Ordinal);
        var root = JsonDocument.Parse(json.Out).RootElement;
        Assert.Equal("running", root.GetProperty("engine").GetProperty("state").GetString());
        Assert.Contains("Developer", root.GetProperty("engine").GetProperty("edition").GetString(), StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("mismatches").EnumerateArray());
        var pool = root.GetProperty("pool");
        if (!World.IsPlain)
        {
            // A 2 GB XFS file: a little less than 2 GB is usable, and almost all of it is free.
            Assert.InRange(pool.GetProperty("totalBytes").GetInt64(), 1_800_000_000, 2_000_000_000);
            Assert.True(pool.GetProperty("freeBytes").GetInt64() > 1_500_000_000);
        }

        foreach (var o in new[] { text.Out, text.Err, json.Out, json.Err })
        {
            Assert.DoesNotContain(World.Password, o, StringComparison.Ordinal);
        }

        // The status probe and listing leave nothing in the pool.
        var left = await PoolWork.RunAsync(World.Runtime, DockerHost.Image, World.Location,
            "ls -A /pool/.probe 2>/dev/null | wc -l", readOnly: true, CancellationToken.None);
        Assert.Equal("0", left.LastLine);
    }

    [IntegrationThinFact]
    public void Doctor_on_the_initialized_machine_passes_or_warns_and_never_prints_the_password()
    {
        SkipTestException.UnlessManaged(World);
        var text = World.RunCli("doctor");
        var json = World.RunCli("doctor", "--format", "json");
        output.WriteLine(text.Out);

        Assert.Equal(0, text.Exit);
        Assert.Equal(0, json.Exit);
        var checks = JsonDocument.Parse(json.Out).RootElement.GetProperty("checks").EnumerateArray().ToDictionary(
            c => c.GetProperty("id").GetString()!, c => c.GetProperty("status").GetString()!);
        Assert.Equal("pass", checks["docker"]);
        Assert.Equal("pass", checks["pool"]);
        Assert.Equal("pass", checks["port"]);
        Assert.Equal("pass", checks["cloneMode"]);
        // 2 GB of pool is under 10 GiB free and over 1 GiB free.
        Assert.Equal("warn", checks["space"]);
        foreach (var o in new[] { text.Out, text.Err, json.Out, json.Err })
        {
            Assert.DoesNotContain(World.Password, o, StringComparison.Ordinal);
        }
    }

    [IntegrationFact]
    public async Task Engine_stop_is_safe_to_repeat_and_engine_start_brings_the_engine_back()
    {
        var stop = World.RunCli("engine", "stop");
        var stopAgain = World.RunCli("engine", "stop");
        var stopped = World.RunCli("status", "--format", "json");
        var start = World.RunCli("engine", "start", "--format", "json");
        var running = World.RunCli("status");
        output.WriteLine($"engine start after stop: {start.Elapsed.TotalSeconds:F1} s");

        Assert.Equal(0, stop.Exit);
        Assert.Equal(0, stopAgain.Exit);
        Assert.Contains("already stopped", stopAgain.Out, StringComparison.Ordinal);
        Assert.Equal(3, stopped.Exit);
        Assert.Equal("engine_stopped", JsonDocument.Parse(stopped.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, start.Exit);
        Assert.Equal(0, running.Exit);
        Assert.True(await World.CanLoginAsync(World.Password));
        foreach (var o in new[] { stop.Out, stopped.Out, start.Out, start.Err, running.Out })
        {
            Assert.DoesNotContain(World.Password, o, StringComparison.Ordinal);
        }
    }

    [IntegrationThinFact]
    public async Task Engine_start_remounts_an_unmounted_managed_pool_before_it_starts_the_engine()
    {
        SkipTestException.UnlessManaged(World);
        World.RunCli("engine", "stop");
        var pool = new ManagedPool(World.Runtime, World.Paths);
        await pool.UnmountAsync(CancellationToken.None);
        Assert.False(await pool.IsMountedAsync(CancellationToken.None));

        var status = World.RunCli("status", "--format", "json");
        var start = World.RunCli("engine", "start");

        // A pool that is merely unmounted still counts as initialized.
        Assert.Equal(3, status.Exit);
        Assert.Equal("engine_stopped", JsonDocument.Parse(status.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(start.Exit == 0, start.Out + start.Err);
        Assert.True(await pool.IsMountedAsync(CancellationToken.None));
        Assert.True(await World.CanLoginAsync(World.Password));
    }

    private static string BootstrapPasswordOf(string inspect)
    {
        using var document = JsonDocument.Parse(inspect);
        foreach (var entry in document.RootElement[0].GetProperty("Config").GetProperty("Env").EnumerateArray())
        {
            var text = entry.GetString() ?? string.Empty;
            if (text.StartsWith("MSSQL_SA_PASSWORD=", StringComparison.Ordinal))
            {
                return text["MSSQL_SA_PASSWORD=".Length..];
            }
        }

        throw new InvalidOperationException("The engine has no MSSQL_SA_PASSWORD variable.");
    }
}

[Trait("Category", "Integration")]
public sealed class EngineInitFailureIntegrationTests(ITestOutputHelper output)
{
    [IntegrationFact]
    public async Task A_failed_engine_start_after_the_pool_is_mounted_undoes_everything_and_exits_1()
    {
        await using var world = new EngineWorld();
        using var busy = new TcpListener(IPAddress.Loopback, world.Port);
        busy.Start();

        var run = world.RunCli("init", "--format", "json");

        output.WriteLine(run.Out);
        Assert.Equal(1, run.Exit);
        Assert.Equal("init_failed", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        var pool = new ManagedPool(world.Runtime, world.Paths);
        Assert.False(await pool.FileExistsAsync(CancellationToken.None));
        Assert.False(await pool.IsMountedAsync(CancellationToken.None));
        Assert.Null(await world.Runtime.InspectAsync(world.EngineName, CancellationToken.None));
        Assert.DoesNotContain(await DockerHost.ManagedContainerNamesAsync(world.Runtime), n => n == world.EngineName);
        Assert.DoesNotContain(world.Password, run.Out + run.Err, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task A_container_with_the_engines_name_that_sqlbranch_did_not_create_exits_3_and_is_left_running()
    {
        await using var world = new EngineWorld();
        // The test's own stand-in for a foreign container, removed by the test itself and by name only.
        var start = await EngineWorld.DockerAsync("run", "-d", "--name", world.EngineName, "--label", "io.sqlbranch.it-foreign=true", "ubuntu:24.04", "sleep", "600");
        Assert.True(start.ExitCode == 0, start.StdErr);
        try
        {
            var run = world.RunCli("init", "--format", "json");

            Assert.Equal(3, run.Exit);
            Assert.Equal("foreign_container", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains(world.EngineName, run.Out, StringComparison.Ordinal);
            var state = await EngineWorld.DockerAsync("inspect", "--format", "{{.State.Running}}", world.EngineName);
            Assert.Equal("true", state.StdOut.Trim());
            Assert.False(await new ManagedPool(world.Runtime, world.Paths).FileExistsAsync(CancellationToken.None));
        }
        finally
        {
            await EngineWorld.DockerAsync("rm", "--force", world.EngineName);
        }
    }
}

[Trait("Category", "Integration")]
public sealed class BeforeInitIntegrationTests
{
    [IntegrationFact]
    public async Task Every_command_but_init_and_doctor_exits_3_not_initialized_and_nothing_is_created()
    {
        await using var world = new EngineWorld();

        foreach (var command in new[] { new[] { "status" }, ["engine", "start"], ["engine", "stop"] })
        {
            var run = world.RunCli([.. command, "--format", "json"]);

            Assert.Equal(3, run.Exit);
            Assert.Equal("not_initialized", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains("sqlbranch init", run.Out, StringComparison.Ordinal);
        }

        var pool = new ManagedPool(world.Runtime, world.Paths);
        Assert.False(await pool.FileExistsAsync(CancellationToken.None));
        Assert.DoesNotContain(await DockerHost.ManagedContainerNamesAsync(world.Runtime), n => n.Contains(world.Id, StringComparison.Ordinal));
    }

    [IntegrationFact]
    public async Task Doctor_works_before_init_and_creates_no_pool_and_no_engine()
    {
        await using var world = new EngineWorld();

        var run = world.RunCli("doctor", "--format", "json");

        Assert.Equal(0, run.Exit);
        var checks = JsonDocument.Parse(run.Out).RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("status").GetString()!);
        Assert.Equal("pass", checks["docker"]);
        Assert.Equal("pass", checks["pool"]);
        Assert.Equal("pass", checks["port"]);
        Assert.DoesNotContain(world.Password, run.Out + run.Err, StringComparison.Ordinal);
        Assert.False(await new ManagedPool(world.Runtime, world.Paths).FileExistsAsync(CancellationToken.None));
        Assert.Null(await world.Runtime.InspectAsync(world.EngineName, CancellationToken.None));
    }

    [IntegrationFact]
    public async Task Doctor_fails_with_exit_3_naming_the_port_when_another_process_holds_it()
    {
        await using var world = new EngineWorld();
        using var busy = new TcpListener(IPAddress.Loopback, world.Port);
        busy.Start();

        var run = world.RunCli("doctor", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Contains(world.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), run.Out, StringComparison.Ordinal);
        Assert.Equal("check_failed", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}

[Trait("Category", "Integration")]
public sealed class EngineRecreateIntegrationTests(ITestOutputHelper output)
{
    [IntegrationFact]
    public async Task An_engine_recreated_over_an_existing_pool_keeps_the_real_password_out_of_docker_inspect_and_the_login_still_works()
    {
        await using var world = new EngineWorld();
        var first = world.RunCli("init", "--format", "json");
        Assert.True(first.Exit == 0, first.Out + first.Err);

        // The test's own engine, removed by name the way a user's 'docker rm -f' would.
        var removed = await EngineWorld.DockerAsync("rm", "--force", world.EngineName);
        Assert.True(removed.ExitCode == 0, removed.StdErr);
        Assert.Null(await world.Runtime.InspectAsync(world.EngineName, CancellationToken.None));

        var second = world.RunCli("init", "--format", "json");
        output.WriteLine(second.Out + second.Err);
        Assert.True(second.Exit == 0, second.Out + second.Err);

        var inspect = (await EngineWorld.DockerAsync("inspect", world.EngineName)).StdOut;
        Assert.Contains("MSSQL_SA_PASSWORD", inspect, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Password, inspect, StringComparison.Ordinal);
        Assert.True(await world.CanLoginAsync(world.Password));
        Assert.DoesNotContain(world.Password, second.Out + second.Err, StringComparison.Ordinal);
    }
}

[Trait("Category", "Integration")]
public sealed class ForeignVolumeIntegrationTests(ITestOutputHelper output)
{
    [IntegrationFact]
    public async Task A_volume_without_the_label_under_the_worlds_pool_volume_name_is_not_a_pool_and_nothing_in_it_is_touched()
    {
        await using var world = new EngineWorld(plain: false);
        // The test creates this unlabeled volume itself, under this world's own name, and removes it at the end.
        var create = await EngineWorld.DockerAsync("volume", "create", world.PoolVolume);
        Assert.True(create.ExitCode == 0, create.StdErr);
        try
        {
            var location = new PoolLocation(PoolKind.Volume, world.PoolVolume);
            var seed = await PoolWork.RunAsync(world.Runtime, DockerHost.Image, location,
                "mkdir -p /pool/engine /pool/images && echo mine > /pool/engine/keep.txt && echo mine > /pool/.sqlbranch-pool", readOnly: false, CancellationToken.None);
            Assert.True(seed.Succeeded, seed.StdErr);

            var teardown = world.RunCli("teardown", "--force", "--format", "json");
            var doctor = world.RunCli("doctor", "--format", "json");
            output.WriteLine(teardown.Out + doctor.Out);

            Assert.Equal(3, teardown.Exit);
            Assert.Equal("not_initialized", JsonDocument.Parse(teardown.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
            var listing = await PoolWork.RunAsync(world.Runtime, DockerHost.Image, location, "ls -A /pool /pool/engine", readOnly: true, CancellationToken.None);
            Assert.Contains("keep.txt", listing.StdOut, StringComparison.Ordinal);
            Assert.Contains(".sqlbranch-pool", listing.StdOut, StringComparison.Ordinal);
            Assert.Contains("images", listing.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain(".sqlbranch-doctor", listing.StdOut, StringComparison.Ordinal);
            Assert.True(await world.Runtime.VolumeExistsAsync(world.PoolVolume, CancellationToken.None));
        }
        finally
        {
            await EngineWorld.DockerAsync("volume", "rm", world.PoolVolume);
        }
    }
}
