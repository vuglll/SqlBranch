using System.Diagnostics;
using System.Text.Json;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Engine;
using SqlBranch.Testing;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 4.6 on the real engine, in whatever clone mode the pool has: the exec read of the 600 credentials file, the commands that
/// never upgrade, the fixture throughput on both formats, and list and export on a format-2 pool. The upgrade cannot be undone,
/// so the walk is one test on the fixture's own engine, in the order the scenarios need. The thin and the copy class below run the same
/// scenarios on their own engines.
/// </summary>
[Trait("Category", "Integration")]
public abstract class PoolFormatRecheckScenarios(ImageEngineFixture fixture, ITestOutputHelper output)
{
    private EngineWorld World => fixture.World!;

    private BranchKit Kit => new(fixture, output);

    private JsonElement Json((int Exit, string Out, string Err, TimeSpan Elapsed) run, int exit = 0)
    {
        output.WriteLine(run.Out + run.Err);
        Assert.True(run.Exit == exit, run.Out + run.Err);
        return JsonDocument.Parse(run.Out).RootElement.Clone();
    }

    private int PoolFormat() => Json(World.RunCli("status", "--format", "json")).GetProperty("poolFormat").GetInt32();

    private FixtureHost Host() => new(World.Runtime, new SqlClientEngineConnector(), World.Redactor,
        new SettingsSources { Environment = World.Environment, WorkingDirectory = fixture.ClientFolder, HomeDirectory = World.HomeDirectory },
        World.Paths, World.EngineName, World.PoolVolume, null);

    [IntegrationFact]
    public async Task The_engine_user_reads_the_600_credentials_file_through_docker_exec_and_no_container_is_started()
    {
        Assert.Equal("600 10001", await Kit.InPoolAsync("stat -c '%a %u' /pool/engine/credentials.json"));

        // The default user of docker exec is the engine's own user, the owner of the file.
        var exec = await EngineWorld.DockerAsync("exec", World.EngineName, "sh", "-c", "cat /pool/engine/credentials.json > /dev/null");
        Assert.Equal(0, exec.ExitCode);

        var before = (await EngineWorld.DockerAsync("ps", "-a", "-q", "--filter", "name=sqlbranch-work-")).StdOut;
        var file = await EngineCredentials.ReadThroughEngineAsync(World.Runtime, World.EngineName, EngineWorld.EngineImage, World.Location, CancellationToken.None);
        Assert.NotNull(file);
        Assert.Equal(World.Password, file.Password);
        Assert.Equal(before, (await EngineWorld.DockerAsync("ps", "-a", "-q", "--filter", "name=sqlbranch-work-")).StdOut);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <summary>20 sequential fixture creates and disposes; the median in milliseconds.</summary>
    private async Task<double> FixtureMedianAsync(string image)
    {
        using var process = new FixtureProcess();
        var times = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            await using (await SqlBranchDatabase.CreateAsync(image, new(), Host(), process, () => Guid.NewGuid().ToString("N")[..8]))
            {
            }

            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        return Median(times);
    }

    [IntegrationFact]
    public async Task Walk_other_commands_do_not_upgrade_then_throughput_then_list_and_export_on_format_2()
    {
        output.WriteLine("clone mode: " + await Kit.CloneModeAsync());
        await Kit.EnsureImageAsync("rcshop");
        await Kit.EnsureImageAsync("rccrm");
        Assert.Equal(1, PoolFormat());

        // "Other commands do not upgrade" and "Create on a format-1 pool"
        Json(World.RunCli("branch", "create", "rcdev2", "--from", "rccrm", "--format", "json"));
        Json(World.RunCli("branch", "reset", "rcdev2", "--format", "json"));
        Assert.Equal(1, PoolFormat());
        Json(World.RunCli("branch", "create", "rcdev3", "--from", "rcshop", "--format", "json"));
        Assert.Equal(1, PoolFormat());
        await Kit.DropBranchAsync("rcdev3");
        await Kit.DropBranchAsync("rcdev2");

        // Fixture throughput on format 1.
        var format1 = await FixtureMedianAsync("rcshop");
        output.WriteLine($"fixture create+dispose median over 20, format 1: {format1:F0} ms");

        Json(World.RunCli("pool", "upgrade", "--yes", "--format", "json"));
        Assert.Equal(2, PoolFormat());

        var format2 = await FixtureMedianAsync("rcshop");
        output.WriteLine($"fixture create+dispose median over 20, format 2: {format2:F0} ms");
        output.WriteLine(format2 > format1 * 1.10
            ? $"FLAG: format 2 is {(format2 / format1 - 1) * 100:F1}% slower than format 1 (more than 10%)"
            : $"format 2 is within 10% of format 1 ({(format2 / format1 - 1) * 100:F1}%)");

        // "List and export on a format-2 pool": the upgraded pool holds shop and crm, and crm is removed after the upgrade.
        Json(World.RunCli("image", "rm", "rccrm", "--format", "json"));
        var listed = Json(World.RunCli("image", "list", "--format", "json")).GetProperty("images").EnumerateArray().ToArray();
        Assert.Contains(listed, i => i.GetProperty("name").GetString() == "rcshop");
        Assert.DoesNotContain(listed, i => i.GetProperty("name").GetString() == "rccrm");
        var archive = Path.Combine(fixture.ClientFolder, "rcshop.sbimg");
        Json(World.RunCli("image", "export", "rcshop", "--to", archive, "--format", "json"));

        // The import goes to a genuinely fresh format-1 pool: a second engine of its own, never touched by an upgrade.
        await using var fresh = new EngineWorld("4GB", plain: false);
        {
            Json(fresh.RunCli("init", "--format", "json"));
            Assert.Equal(1, Json(fresh.RunCli("status", "--format", "json")).GetProperty("poolFormat").GetInt32());
            Json(fresh.RunCli("image", "import", archive, "--as", "rcshop2", "--format", "json"));
            Assert.Equal(1, Json(fresh.RunCli("status", "--format", "json")).GetProperty("poolFormat").GetInt32());

            var original = Json(World.RunCli("image", "list", "--format", "json")).GetProperty("images").EnumerateArray()
                .Single(i => i.GetProperty("name").GetString() == "rcshop");
            var imported = Json(fresh.RunCli("image", "list", "--format", "json")).GetProperty("images").EnumerateArray()
                .Single(i => i.GetProperty("name").GetString() == "rcshop2");
            static string Comparable(JsonElement row) => JsonSerializer.Serialize(row.EnumerateObject()
                .Where(p => p.Name is not ("name" or "version" or "branches" or "branchNames" or "catalogReadable"))
                .ToDictionary(p => p.Name, p => p.Value));
            Assert.Equal(Comparable(original), Comparable(imported));
        }
    }
}

/// <summary>The walk on the default managed pool (clone mode thin).</summary>
[Trait("Category", "Integration")]
public sealed class PoolFormatRecheckThinIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output)
    : PoolFormatRecheckScenarios(fixture, output), IClassFixture<ImageEngineFixture>
{
}

/// <summary>The walk on a pool that cannot share blocks (clone mode copy).</summary>
[Trait("Category", "Integration")]
public sealed class PoolFormatRecheckCopyIntegrationTests(CopyEngineFixture fixture, ITestOutputHelper output)
    : PoolFormatRecheckScenarios(fixture, output), IClassFixture<CopyEngineFixture>
{
}
