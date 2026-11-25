using System.Text;
using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>Task 4.6: commands read the pool format again after their locks and report environment errors in the D8 order.</summary>
public sealed class PoolFormatRecheckTests
{
    private static UpgradeWorld Pool(string mode = "thin")
    {
        var pool = new UpgradeWorld();
        pool.World.Host.EngineProbe = mode;
        pool.World.Connector.ParameterizedScalarHandler = (sql, _) => sql.Contains("state_desc", StringComparison.Ordinal) ? "ONLINE" : 0;
        pool.AddImage("shop");
        pool.AddImage("crm");
        pool.AddBranch("dev1");
        pool.World.Connector.Databases["dev1"] = ["/pool/branches/dev1/data/Shop.mdf", "/pool/branches/dev1/data/Shop_log.ldf"];
        return pool;
    }

    private static (int Exit, string Code, string Message) Failure((int Exit, string Out, string Err) run)
    {
        var error = JsonDocument.Parse(run.Out).RootElement.GetProperty("error");
        return (run.Exit, error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    /// <summary>Makes a pool upgrade start when the command takes <paramref name="resource"/>: the fence is written and the upgrade holds its locks.</summary>
    private static void UpgradeStartsAt(UpgradeWorld pool, string resource, params string[] held)
    {
        pool.World.Connector.OnTakeLock = taken =>
        {
            if (taken != resource) { return; }
            pool.World.Host.CredentialsJson = Format2Pool.Credentials(UpgradeRecord.Running);
            foreach (var lockName in held) { pool.World.Connector.HeldLocks.Add(lockName); }
        };
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Create_meets_an_image_lock_held_by_a_running_upgrade(string mode)
    {
        var pool = Pool(mode);
        UpgradeStartsAt(pool, EngineLocks.Branch("dev7"), EngineLocks.PoolUpgrade, EngineLocks.Image("shop"));

        var run = Failure(pool.Run("branch", "create", "dev7", "--from", "shop", "--format", "json"));

        Assert.Equal(3, run.Exit);
        Assert.Equal("pool_upgrade_required", run.Code);
        Assert.Contains("A pool upgrade is running", run.Message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch pool upgrade --yes", run.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("being built or removed", run.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("building or removing", run.Message, StringComparison.Ordinal);
        Assert.False(pool.World.Connector.Databases.ContainsKey("dev7"));
        Assert.DoesNotContain(pool.Scripts, s => s.Contains("/pool/branches/dev7", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Reset_meets_an_image_lock_taken_by_an_upgrade_that_started_after_its_first_read(string mode)
    {
        var pool = Pool(mode);
        UpgradeStartsAt(pool, EngineLocks.Branch("dev1"), EngineLocks.PoolUpgrade, EngineLocks.Image("shop"));
        var record = pool.Records["dev1"];

        var run = Failure(pool.Run("branch", "reset", "dev1", "--format", "json"));

        Assert.Equal(3, run.Exit);
        Assert.Equal("pool_upgrade_required", run.Code);
        Assert.Contains("sqlbranch pool upgrade --yes", run.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("building or removing", run.Message, StringComparison.Ordinal);
        Assert.True(pool.World.Connector.Databases.ContainsKey("dev1"));
        Assert.Equal(record, pool.Records["dev1"]);
        Assert.DoesNotContain(pool.World.Connector.Statements, s => s.Contains("DROP DATABASE", StringComparison.Ordinal) || s.Contains("SINGLE_USER", StringComparison.Ordinal));
        Assert.DoesNotContain(pool.Scripts, s => s.Contains("rm -rf", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Create_meets_an_image_lock_on_a_format_1_pool_and_says_what_0_2_2_said(string mode)
    {
        var pool = Pool(mode);
        pool.World.Connector.HeldLocks.Add(EngineLocks.Image("shop"));

        var run = Failure(pool.Run("branch", "create", "dev7", "--from", "shop", "--format", "json"));

        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", run.Code);
        Assert.Equal("Another command is building or removing the image 'shop'. Wait for it to finish, then run the command again.", run.Message);
        using var status = JsonDocument.Parse(pool.Run("status", "--format", "json").Out);
        Assert.Equal(1, status.RootElement.GetProperty("poolFormat").GetInt32());
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void An_upgrade_that_starts_while_a_create_waits_for_its_lock_stops_the_create_and_then_finishes(string mode)
    {
        var pool = Pool(mode);
        UpgradeStartsAt(pool, EngineLocks.Branch("dev7"));

        var run = Failure(pool.Run("branch", "create", "dev7", "--from", "shop", "--format", "json"));

        Assert.Equal(3, run.Exit);
        Assert.Equal("pool_upgrade_required", run.Code);
        Assert.False(pool.World.Connector.Databases.ContainsKey("dev7"));
        Assert.DoesNotContain(pool.Scripts, s => s.Contains("/pool/branches/dev7", StringComparison.Ordinal));

        pool.World.Connector.OnTakeLock = null;
        pool.World.Connector.HeldLocks.Clear();
        Assert.Equal(0, pool.Run("pool", "upgrade", "--yes").Exit);
    }

    [Fact]
    public void Image_rm_that_reads_format_1_and_meets_an_upgrade_after_its_lock_exits_3()
    {
        var pool = Pool();
        UpgradeStartsAt(pool, EngineLocks.Image("crm"));

        var run = Failure(pool.Run("image", "rm", "crm", "--format", "json"));

        Assert.Equal(3, run.Exit);
        Assert.Equal("pool_upgrade_required", run.Code);
        Assert.Contains("crm", pool.Images.Keys);
    }

    [Theory]
    [InlineData(true, false, "is running")]
    [InlineData(true, true, "is running")]
    public void Image_rm_of_an_unreadable_catalog_during_a_live_upgrade_exits_3_not_a_busy_lock(bool live, bool imageLocked, string text)
    {
        var pool = Pool();
        pool.Images["shop"] = pool.Images["shop"] with { CatalogBase64 = Convert.ToBase64String("broken"u8.ToArray()) };
        Format2Pool.Apply(pool.World, UpgradeRecord.Running);
        if (live) { pool.World.Connector.HeldLocks.Add(EngineLocks.PoolUpgrade); }
        if (imageLocked) { pool.World.Connector.HeldLocks.Add(EngineLocks.Image("shop")); }

        var run = Failure(pool.Run("image", "rm", "shop", "--force", "--format", "json"));

        Assert.Equal(3, run.Exit);
        Assert.Equal("pool_upgrade_required", run.Code);
        Assert.Contains(text, run.Message, StringComparison.Ordinal);
        Assert.Contains("shop", pool.Images.Keys);
    }

    [Fact]
    public void Image_rm_of_an_unreadable_catalog_after_a_dead_upgrade_still_works_and_a_busy_lock_keeps_its_text()
    {
        var pool = Pool();
        pool.Images["shop"] = pool.Images["shop"] with { CatalogBase64 = Convert.ToBase64String("broken"u8.ToArray()) };
        Format2Pool.Apply(pool.World, UpgradeRecord.Running);
        pool.World.Connector.HeldLocks.Add(EngineLocks.Image("shop"));

        var busy = Failure(pool.Run("image", "rm", "shop", "--force", "--format", "json"));
        Assert.Equal(1, busy.Exit);
        Assert.Contains("Another command is working on the image 'shop'", busy.Message, StringComparison.Ordinal);

        pool.World.Connector.HeldLocks.Clear();
        Assert.Equal(0, pool.Run("image", "rm", "shop", "--force", "--format", "json").Exit);
        Assert.DoesNotContain("shop", pool.Images.Keys);
    }

    [Fact]
    public async Task A_stopped_engine_during_an_unfinished_upgrade_is_reported_before_the_upgrade()
    {
        var pool = Pool();
        Format2Pool.Apply(pool.World, UpgradeRecord.Running);
        await pool.World.Runtime.StopAsync(ContainerLabels.EngineName, CancellationToken.None);

        var run = Failure(pool.Run("branch", "create", "dev5", "--from", "crm", "--format", "json"));

        Assert.Equal(3, run.Exit);
        Assert.Equal("engine_stopped", run.Code);
    }

    [Fact]
    public void Docker_stopped_during_an_unfinished_upgrade_is_reported_before_the_upgrade()
    {
        var pool = Pool();
        Format2Pool.Apply(pool.World, UpgradeRecord.Running);
        pool.World.Runtime.InfoFailure = new SqlBranchException(ErrorCodes.DockerUnavailable, "Docker is not running.");

        var run = Failure(pool.Run("branch", "create", "dev5", "--from", "crm", "--format", "json"));

        Assert.Equal(3, run.Exit);
        Assert.Equal("docker_unavailable", run.Code);
    }

    [Fact]
    public void A_bad_name_during_an_unfinished_upgrade_is_a_usage_error()
    {
        var pool = Pool();
        Format2Pool.Apply(pool.World, UpgradeRecord.Running);

        var run = Failure(pool.Run("branch", "create", "Dev5", "--from", "crm", "--format", "json"));

        Assert.Equal(2, run.Exit);
        Assert.Equal("invalid_name", run.Code);
    }

    private static readonly PoolLocation Location = new(PoolKind.Directory, "/data/sb");

    private static string Reply(string prefix, string json) => prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\n";

    [Fact]
    public async Task The_re_read_goes_through_docker_exec_and_starts_no_container()
    {
        var runtime = new FakeContainerRuntime();
        runtime.Containers["sqlbranch-engine"] = new ContainerInfo("sqlbranch-engine", "img", true,
            new Dictionary<string, string> { [ContainerLabels.Managed] = "true" }, []);
        runtime.ExecHandler = (_, command) =>
        {
            Assert.Contains("credentials.json", command[2], StringComparison.Ordinal);
            return FakeContainerRuntime.Ok(Reply("SQLBRANCH_POOLFORMAT=", Format2Pool.Credentials(UpgradeRecord.Running)));
        };

        var file = await EngineCredentials.ReadThroughEngineAsync(runtime, "sqlbranch-engine", "img", Location, CancellationToken.None);

        Assert.Equal(2, file!.Format);
        Assert.Equal(UpgradeRecord.Running, file.Upgrade!.State);
        Assert.Empty(runtime.Runs);
    }

    [Fact]
    public async Task The_re_read_falls_back_to_a_work_container_when_the_engine_user_cannot_read_the_file()
    {
        var runtime = new FakeContainerRuntime();
        runtime.Containers["sqlbranch-engine"] = new ContainerInfo("sqlbranch-engine", "img", true,
            new Dictionary<string, string> { [ContainerLabels.Managed] = "true" }, []);
        runtime.ExecHandler = (_, _) => FakeContainerRuntime.Fail(1, "cat: permission denied");
        runtime.RunHandler = _ => FakeContainerRuntime.Ok(Reply("SQLBRANCH_CREDENTIALS=", Format2Pool.Credentials()));

        var file = await EngineCredentials.ReadThroughEngineAsync(runtime, "sqlbranch-engine", "img", Location, CancellationToken.None);

        Assert.Equal(2, file!.Format);
        Assert.Single(runtime.Runs);
    }
}
