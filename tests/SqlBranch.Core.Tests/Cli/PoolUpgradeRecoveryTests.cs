using System.Text.Json;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

public sealed class PoolUpgradeRecoveryTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public static IEnumerable<object[]> KillPoints()
    {
        yield return ["lock", 1];
        foreach (var state in new[] { "running", "done" })
        {
            foreach (var step in new[] { "write", "chown", "chmod", "sync", "rename", "directory-sync" })
            {
                yield return [$"credentials:{state}:{step}", 1];
            }
        }
        yield return ["enumerate", 1];
        yield return ["enumerate", 2];
        yield return ["cleanup", 1];
        foreach (var item in new[] { "id:shop", "catalog:shop", "branch:dev1" })
        {
            foreach (var step in new[] { "write", "sync", "rename", "directory-sync" })
            {
                yield return [$"{item}:{step}", 1];
            }
        }
    }

    private static UpgradeWorld Pool(Clock clock)
    {
        var pool = new UpgradeWorld();
        pool.World.Clock = clock;
        pool.AddImage("shop");
        pool.AddBranch("dev1");
        pool.World.Connector.ParameterizedScalarHandler = (sql, _) => sql.Contains("state_desc", StringComparison.Ordinal) ? "ONLINE" : 0;
        return pool;
    }

    private static (int Exit, string Out, string Err) Upgrade(UpgradeWorld pool) => pool.Run("pool", "upgrade", "--yes", "--format", "json");

    [Theory]
    [MemberData(nameof(KillPoints))]
    public void Killed_at_each_upgrade_and_credentials_rewrite_step_reruns_with_identical_ids(string point, int occurrence)
    {
        var clock = new Clock();
        var pool = Pool(clock);
        var hits = 0;
        void Kill(string step)
        {
            if (step == point && ++hits == occurrence)
            {
                throw new IOException("Simulated client death");
            }
        }
        pool.AfterStep = Kill;
        pool.World.Connector.OnTakeLock = resource => { if (resource == EngineLocks.PoolUpgrade) { Kill("lock"); } };
        var killed = Upgrade(pool);
        Assert.NotEqual(0, killed.Exit);
        Assert.Equal(occurrence, hits);
        Assert.Empty(pool.World.Connector.HeldLocks);
        var recorded = CredentialsFile.Parse(pool.World.Host.CredentialsJson!, "pool");
        var priorCatalog = pool.CatalogOf("shop");
        var priorRecord = pool.Records["dev1"];
        Assert.Equal(0, pool.Run("image", "list").Exit);
        Assert.Equal(0, pool.Run("branch", "list").Exit);
        Assert.Equal(0, pool.Run("branch", "conn", "dev1").Exit);
        var status = pool.Run("status", "--format", "json");
        Assert.Equal(recorded.Upgrade?.State == UpgradeRecord.Running || pool.TemporaryFiles.Count > 0 ? 1 : 0, status.Exit);

        pool.AfterStep = null;
        pool.World.Connector.OnTakeLock = null;
        clock.Now = Start.AddDays(5);
        var rerun = Upgrade(pool);
        Assert.True(rerun.Exit == 0, rerun.Out + rerun.Err);
        var id = ImageVersions.Format("shop", recorded.Upgrade?.StartedAt ?? clock.Now);
        Assert.Equal(id, pool.Images["shop"].IdFile);
        Assert.Equal(id, ImageCatalogFile.TryParse(pool.CatalogOf("shop")!)!.Current);
        Assert.Equal(id, BranchRecord.FromJson(pool.Records["dev1"]).ImageVersion);
        if (priorCatalog is not null) { Assert.Equal(priorCatalog, pool.CatalogOf("shop")); }
        if (BranchRecord.FromJson(priorRecord).BaseKnown is not null) { Assert.Equal(priorRecord, pool.Records["dev1"]); }
        Assert.Empty(pool.TemporaryFiles);
        Assert.Equal(UpgradeRecord.Done, CredentialsFile.Parse(pool.World.Host.CredentialsJson!, "pool").Upgrade!.State);
        Assert.Equal(0, pool.Run("branch", "conn", "dev1").Exit);
        Assert.Equal(0, pool.Run("status").Exit);
    }

    // The upgrade records images in name order, so crm is recorded before shop. Killing after the first image's
    // last step (catalog:crm:directory-sync) is the spec's "after one image's version, before the other's".
    private static UpgradeWorld TwoImagePool(Clock clock)
    {
        var pool = Pool(clock);
        pool.AddImage("crm");
        return pool;
    }

    private static void KillAfterFirstImage(UpgradeWorld pool)
    {
        pool.AfterStep = step =>
        {
            if (step == "catalog:crm:directory-sync") { throw new IOException("Simulated client death"); }
        };
        Assert.NotEqual(0, Upgrade(pool).Exit);
        pool.AfterStep = null;
        Assert.Equal("crm@20261201T090000000Z", pool.Images["crm"].IdFile);
        Assert.Null(pool.Images["shop"].IdFile);
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Killed_between_two_images_a_rerun_keeps_the_recorded_id_and_gives_the_other_the_same_start_time(string mode)
    {
        var clock = new Clock();
        var pool = TwoImagePool(clock);
        pool.World.Host.EngineProbe = mode;
        KillAfterFirstImage(pool);

        clock.Now = Start.AddDays(2);
        var rerun = Upgrade(pool);

        Assert.True(rerun.Exit == 0, rerun.Out + rerun.Err);
        Assert.Equal("crm@20261201T090000000Z", pool.Images["crm"].IdFile);
        Assert.Equal("shop@20261201T090000000Z", pool.Images["shop"].IdFile);
        Assert.Equal("shop@20261201T090000000Z", ImageCatalogFile.TryParse(pool.CatalogOf("shop")!)!.Current);
        Assert.Equal("crm@20261201T090000000Z", ImageCatalogFile.TryParse(pool.CatalogOf("crm")!)!.Current);
        Assert.Equal(0, pool.Run("status").Exit);
        var create = pool.Run("branch", "create", "dev5", "--from", "crm", "--format", "json");
        Assert.True(create.Exit == 0, create.Out + create.Err);
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Create_after_a_killed_upgrade_is_refused_and_reads_still_work(string mode)
    {
        var pool = TwoImagePool(new Clock());
        pool.World.Host.EngineProbe = mode;
        KillAfterFirstImage(pool);

        var create = pool.Run("branch", "create", "dev5", "--from", "crm", "--format", "json");

        Assert.Equal(3, create.Exit);
        var error = JsonDocument.Parse(create.Out).RootElement.GetProperty("error");
        Assert.Equal("pool_upgrade_required", error.GetProperty("code").GetString());
        Assert.Contains("sqlbranch pool upgrade --yes", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("dev5", pool.Records.Keys);
        Assert.DoesNotContain("dev5", pool.Branches);
        var list = pool.Run("image", "list", "--format", "json");
        Assert.Equal(0, list.Exit);
        var names = JsonDocument.Parse(list.Out).RootElement.GetProperty("images").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["crm", "shop"], names);
    }

    [Theory]
    [InlineData(false, "thin")]
    [InlineData(true, "thin")]
    [InlineData(false, "copy")]
    [InlineData(true, "copy")]
    public void Create_during_an_unfinished_upgrade_and_other_writes_are_refused(bool live, string mode)
    {
        var pool = Pool(new Clock());
        Format2Pool.Apply(pool.World, UpgradeRecord.Running);
        pool.World.Host.EngineProbe = mode;
        if (live) { pool.World.Connector.HeldLocks.Add(EngineLocks.PoolUpgrade); }
        pool.World.Files["/backup/shop.bak"] = 100;
        string[][] commands =
        [
            ["branch", "create", "dev7", "--from", "shop"],
            ["branch", "reset", "dev1"],
            ["branch", "rm", "dev1"],
            ["branch", "prune"],
            ["image", "rm", "shop", "--force"],
            ["image", "build", "newshop", "--from-bak", "/backup/shop.bak"],
        ];
        var record = pool.Records["dev1"];
        foreach (var command in commands)
        {
            var run = pool.Run([.. command, "--format", "json"]);
            Assert.True(run.Exit == 3, string.Join(' ', command) + "\n" + run.Out + run.Err);
            var error = JsonDocument.Parse(run.Out).RootElement.GetProperty("error");
            Assert.Equal("pool_upgrade_required", error.GetProperty("code").GetString());
            Assert.Contains(live ? "is running" : "did not finish", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("sqlbranch pool upgrade --yes", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(record, pool.Records["dev1"]);
        Assert.Null(pool.CatalogOf("shop"));
        Assert.Empty(pool.Writes);
        Assert.DoesNotContain(pool.World.Connector.Statements, s => s.Contains("DROP DATABASE", StringComparison.Ordinal) || s.Contains("SINGLE_USER", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_keep_working_during_an_unfinished_upgrade_without_writing(bool live)
    {
        var pool = Pool(new Clock());
        Format2Pool.Apply(pool.World, UpgradeRecord.Running);
        if (live) { pool.World.Connector.HeldLocks.Add(EngineLocks.PoolUpgrade); }
        var record = pool.Records["dev1"];
        foreach (var command in new[] { new[] { "image", "list" }, ["branch", "list"], ["branch", "conn", "dev1"] })
        {
            var run = pool.Run([.. command, "--format", "json"]);
            Assert.True(run.Exit == 0, string.Join(' ', command) + "\n" + run.Out + run.Err);
        }
        Assert.Equal(live ? 0 : 1, pool.Run("status", "--format", "json").Exit);
        Assert.Equal(record, pool.Records["dev1"]);
        Assert.Empty(pool.Writes);
    }

    [Fact]
    public void Unreadable_catalog_can_be_removed_during_an_unfinished_upgrade_then_rerun_finishes()
    {
        var pool = Pool(new Clock());
        pool.Images["shop"] = pool.Images["shop"] with
        {
            CatalogBase64 = Convert.ToBase64String("broken catalog"u8.ToArray()),
            IdFile = "shop@20261201T090000000Z",
        };
        Assert.Equal(1, Upgrade(pool).Exit);
        var remove = pool.Run("image", "rm", "shop", "--force", "--format", "json");
        Assert.True(remove.Exit == 0, remove.Out + remove.Err);
        Assert.Empty(JsonDocument.Parse(remove.Out).RootElement.GetProperty("image").GetProperty("versions").EnumerateArray());
        Assert.DoesNotContain("shop", pool.Images.Keys);
        Assert.DoesNotContain(pool.Scripts, s => s.Contains("rm", StringComparison.Ordinal) && s.Contains("'/pool/images/.ids/shop'", StringComparison.Ordinal));
        Assert.Equal(0, Upgrade(pool).Exit);
        var branch = BranchRecord.FromJson(pool.Records["dev1"]);
        Assert.True(branch.BaseKnown);
        Assert.Null(branch.ImageVersion);
        Assert.Equal(0, pool.Run("branch", "conn", "dev1").Exit);
    }
}
