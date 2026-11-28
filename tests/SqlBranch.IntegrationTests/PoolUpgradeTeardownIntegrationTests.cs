using System.Diagnostics;
using System.Text.Json;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class PoolUpgradeTeardownIntegrationTests(ITestOutputHelper output)
{
    private JsonElement Json(EngineWorld world, params string[] args)
    {
        var run = world.RunCli([.. args, "--format", "json"]);
        output.WriteLine(run.Out + run.Err);
        Assert.True(run.Exit == 0, run.Out + run.Err);
        return JsonDocument.Parse(run.Out).RootElement.Clone();
    }

    private async Task SeedAsync(EngineWorld world)
    {
        var folder = Directory.CreateTempSubdirectory("sqlbranch-teardown-upgrade-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "001.sql"), "CREATE TABLE dbo.Item (Id int); INSERT dbo.Item VALUES (1);");
            Json(world, "image", "build", "shop", "--from-scripts", folder.FullName);
            Json(world, "branch", "create", "dev1", "--from", "shop");
        }
        finally { folder.Delete(recursive: true); }
    }

    [IntegrationThinFact]
    public async Task Teardown_during_an_unfinished_upgrade()
    {
        SkipTestException.UnlessManagedRequested();
        await using var world = new EngineWorld(plain: false, keepFormat1: true);
        Json(world, "init");
        await SeedAsync(world);
        Assert.Equal("thin", Json(world, "status").GetProperty("pool").GetProperty("cloneMode").GetString());
        // Hold one existing image so the real upgrade cannot finish after writing the fence.
        await using (var session = await new SqlClientEngineConnector().ConnectAsync(world.Endpoint, world.Password, CancellationToken.None))
        {
            Assert.True(await EngineLocks.TryTakeAsync(session, EngineLocks.Image("shop"), CancellationToken.None));
            using var upgrade = world.StartCli("pool", "upgrade", "--yes");
            var wait = Stopwatch.StartNew();
            while ((await EngineCredentials.ReadFileAsync(world.Runtime, EngineWorld.EngineImage, world.Location, CancellationToken.None))?.Upgrade?.State != UpgradeRecord.Running)
            {
                Assert.False(upgrade.HasExited);
                Assert.True(wait.Elapsed < TimeSpan.FromMinutes(2));
                await Task.Delay(200);
            }
            upgrade.Kill();
            await upgrade.WaitAsync(TimeSpan.FromSeconds(30));
        }
        var temporary = await PoolWork.RunAsync(world.Runtime, EngineWorld.EngineImage, world.Location,
            "mkdir -p /pool/images/.ids; touch /pool/engine/credentials.json.new /pool/images/shop/catalog.json.new /pool/images/.ids/shop.new /pool/branches/dev1/branch.json.new; echo shop@20261201T090000000Z > /pool/images/.ids/shop",
            readOnly: false, CancellationToken.None);
        Assert.True(temporary.Succeeded, temporary.StdErr);
        Json(world, "teardown", "--force");
        Assert.Null(await world.Runtime.InspectAsync(world.EngineName, CancellationToken.None));
        var managed = new ManagedPool(world.Runtime, world.Paths);
        Assert.False(await managed.FileExistsAsync(CancellationToken.None));
        Assert.False(await managed.IsMountedAsync(CancellationToken.None));
        Json(world, "init");
        Assert.Equal(1, Json(world, "status").GetProperty("poolFormat").GetInt32());
    }

    [IntegrationFact]
    public async Task Teardown_of_a_format_2_pool()
    {
        await using var world = new EngineWorld(plain: true);
        Json(world, "init");
        await SeedAsync(world);
        Assert.Equal("copy", Json(world, "status").GetProperty("pool").GetProperty("cloneMode").GetString());
        Json(world, "pool", "upgrade", "--yes");
        Assert.Equal(2, Json(world, "status").GetProperty("poolFormat").GetInt32());
        Json(world, "teardown", "--force");
        Assert.Null(await world.Runtime.InspectAsync(world.EngineName, CancellationToken.None));
        // A plain pool keeps the caller's directory; all SqlBranch contents, including .ids, must be gone.
        var left = await PoolWork.RunAsync(world.Runtime, EngineWorld.EngineImage, world.Location,
            "ls -A /pool", readOnly: true, CancellationToken.None);
        Assert.True(left.Succeeded, left.StdErr);
        Assert.Equal(string.Empty, left.StdOut.Trim());
    }
}
