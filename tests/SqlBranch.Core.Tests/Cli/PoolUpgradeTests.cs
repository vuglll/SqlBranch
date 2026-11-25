using System.Text.Json;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>Task 4.2: upgrade steps 0, 1, 3 and 5 of design D6 for an idle pool.</summary>
public sealed class PoolUpgradeTests
{
    private const string Format1Credentials = "{\"user\":\"sa\",\"password\":\"Stored#Pass1\"}";

    private sealed class SetClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.UtcTicks;
    }

    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly UpgradeWorld _pool = new();
    private readonly SetClock _clock = new(Start);

    public PoolUpgradeTests()
    {
        _pool.World.Clock = _clock;
        _pool.World.UpgradeDelay = (duration, _) => { _clock.Now += duration; return Task.CompletedTask; };
        _pool.AddImage("shop");
        _pool.AddImage("crm");
        _pool.AddBranch("dev1");
        _pool.AddBranch("old1");
    }

    private (int Exit, JsonElement Json, string Err) Json(params string[] more)
    {
        var run = _pool.Run(["pool", "upgrade", "--yes", .. more, "--format", "json"]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement.Clone(), run.Err);
    }

    private CredentialsFile Credentials() => CredentialsFile.Parse(_pool.World.Host.CredentialsJson!, "/data/sb");

    private static string[] Versions(JsonElement json) =>
        [.. json.GetProperty("images").EnumerateArray().Select(i => $"{i.GetProperty("name").GetString()}={i.GetProperty("version").GetString()}")];

    [Fact]
    public void First_version_ids_are_the_start_time_for_each_image_in_name_order()
    {
        var run = Json();

        Assert.Equal(0, run.Exit);
        Assert.Equal(["crm=crm@20261201T090000000Z", "shop=shop@20261201T090000000Z"], Versions(run.Json));
        Assert.Equal(1, run.Json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, run.Json.GetProperty("pool").GetProperty("format").GetInt32());
        Assert.Equal(1, run.Json.GetProperty("pool").GetProperty("previousFormat").GetInt32());
        Assert.Equal("shop@20261201T090000000Z", JsonDocument.Parse(_pool.CatalogOf("shop")!).RootElement.GetProperty("current").GetString());
        Assert.Equal("crm@20261201T090000000Z", _pool.Images["crm"].IdFile);
        Assert.Equal("shop@20261201T090000000Z", _pool.Images["shop"].IdFile);
    }

    [Fact]
    public void The_upgrade_writes_the_fence_then_each_high_water_mark_and_catalog_then_branch_records_then_the_done_record()
    {
        Json();

        Assert.Equal(
            ["credentials:running", "enumerate", "enumerate", "ids:crm", "catalog:crm", "ids:shop", "catalog:shop", "branch:dev1", "branch:old1", "credentials:done"],
            _pool.Writes);
        var final = Credentials();
        Assert.Equal(2, final.Format);
        Assert.Equal(UpgradeRecord.Done, final.Upgrade!.State);
        Assert.Equal(Start, final.Upgrade.StartedAt);
        Assert.Equal(CliWorld.StoredPassword, final.Password);
        Assert.Contains(EngineLocks.PoolUpgrade, _pool.World.Connector.TakenLocks);
        Assert.Contains(EngineLocks.Image("shop"), _pool.World.Connector.TakenLocks);
        Assert.Contains(EngineLocks.Branch("dev1"), _pool.World.Connector.TakenLocks);
    }

    [Fact]
    public void Upgrade_text_names_format_2_and_each_version_and_standard_error_has_the_warning()
    {
        var run = _pool.Run("pool", "upgrade", "--yes");

        Assert.Equal(0, run.Exit);
        Assert.Contains("format 2", run.Out, StringComparison.Ordinal);
        Assert.Contains("shop@20261201T090000000Z", run.Out, StringComparison.Ordinal);
        Assert.Contains("crm@20261201T090000000Z", run.Out, StringComparison.Ordinal);
        var warning = Assert.Single(run.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l.StartsWith("warning:", StringComparison.Ordinal));
        Assert.Contains("cannot be undone", warning, StringComparison.Ordinal);
        Assert.Contains("SqlBranch 0.2.x", warning, StringComparison.Ordinal);
        Assert.Contains("SqlBranch.Testing 0.2.x", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Upgrade_json_has_the_warning_on_standard_error_and_one_document_on_standard_output()
    {
        var run = Json();

        Assert.Equal(0, run.Exit);
        Assert.Contains("warning:", run.Err, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", run.Err, StringComparison.Ordinal);
        var branches = run.Json.GetProperty("branches");
        Assert.Equal(["dev1", "old1"], branches.GetProperty("pinned").EnumerateArray().Select(b => b.GetString()));
        Assert.Empty(branches.GetProperty("unknownBase").EnumerateArray());
        Assert.Empty(branches.GetProperty("removedBase").EnumerateArray());
    }

    [Fact]
    public void The_warning_is_printed_before_the_first_write()
    {
        _pool.World.Host.CredentialsRewriteFailure = "No space left on device";

        var run = _pool.Run("pool", "upgrade", "--yes");

        Assert.Contains("warning:", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Upgrading_twice_reports_format_2_to_format_2_and_changes_nothing()
    {
        Assert.Equal(0, Json().Exit);
        var credentials = _pool.World.Host.CredentialsJson;
        var rewrites = _pool.World.Host.CredentialsRewrites.Count;
        var writes = _pool.Writes.ToArray();
        _clock.Now = Start.AddHours(5);

        var again = Json();

        Assert.Equal(0, again.Exit);
        Assert.Equal(2, again.Json.GetProperty("pool").GetProperty("format").GetInt32());
        Assert.Equal(2, again.Json.GetProperty("pool").GetProperty("previousFormat").GetInt32());
        Assert.Empty(again.Json.GetProperty("images").EnumerateArray());
        Assert.Equal(credentials, _pool.World.Host.CredentialsJson);
        Assert.Equal(rewrites, _pool.World.Host.CredentialsRewrites.Count);
        Assert.Equal(writes, _pool.Writes);
        Assert.Contains("already uses format 2", _pool.Run("pool", "upgrade", "--yes").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pool_that_is_full_before_the_fence_stays_format_1_and_nothing_else_is_written()
    {
        _pool.World.Host.CredentialsRewriteFailure = "cat: write error: No space left on device";

        var run = Json();

        Assert.Equal(1, run.Exit);
        Assert.Equal("pool_full", run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(Format1Credentials, _pool.World.Host.CredentialsJson);
        Assert.Equal(["credentials:failed"], _pool.Writes);

        // The pool stays usable as format 1: status is clean and a branch can still be made (the scenario's "AND").
        _pool.World.Host.CredentialsRewriteFailure = null;
        var status = _pool.Run("status", "--format", "json");
        Assert.Equal(0, status.Exit);
        Assert.Equal(1, JsonDocument.Parse(status.Out).RootElement.GetProperty("poolFormat").GetInt32());
        var create = _pool.Run("branch", "create", "dev5", "--from", "crm", "--format", "json");
        Assert.True(create.Exit == 0, create.Out + create.Err);
        Assert.DoesNotContain(_pool.Scripts, s => s.Contains("catalog.json", StringComparison.Ordinal) || s.Contains(".ids", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pool_that_fills_while_writing_catalogs_leaves_the_fence_running_and_a_rerun_gives_the_same_ids()
    {
        _pool.FailWrite = script => script.Contains("/shop/", StringComparison.Ordinal) ? "No space left on device" : null;

        var failed = Json();

        Assert.Equal(1, failed.Exit);
        Assert.Equal("pool_full", failed.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("sqlbranch pool upgrade --yes", failed.Json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(UpgradeRecord.Running, Credentials().Upgrade!.State);
        Assert.Equal(["credentials:running", "enumerate", "enumerate", "ids:crm", "catalog:crm"], _pool.Writes);

        _pool.FailWrite = null;
        _clock.Now = Start.AddDays(3);
        var rerun = Json();

        Assert.Equal(0, rerun.Exit);
        Assert.Equal(["crm=crm@20261201T090000000Z", "shop=shop@20261201T090000000Z"], Versions(rerun.Json));
        Assert.Equal(1, rerun.Json.GetProperty("pool").GetProperty("previousFormat").GetInt32());
        Assert.Equal(UpgradeRecord.Done, Credentials().Upgrade!.State);
        Assert.Equal(Start, Credentials().Upgrade!.StartedAt);
    }

    [Fact]
    public void A_busy_image_or_branch_fails_with_catalog_busy_naming_it_and_leaves_the_fence_running()
    {
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Image("shop"));
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Branch("dev1"));

        var run = Json();

        Assert.Equal(1, run.Exit);
        Assert.Equal("catalog_busy", run.Json.GetProperty("error").GetProperty("code").GetString());
        var message = run.Json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("'shop'", message, StringComparison.Ordinal);
        Assert.Contains("'dev1'", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch pool upgrade --yes", message, StringComparison.Ordinal);
        Assert.Equal(["credentials:running", "enumerate"], _pool.Writes);
        Assert.Equal(UpgradeRecord.Running, Credentials().Upgrade!.State);
    }

    [Fact]
    public void A_second_upgrade_meeting_the_upgrade_lock_is_catalog_busy_and_writes_nothing()
    {
        _pool.World.Connector.HeldLocks.Add(EngineLocks.PoolUpgrade);

        var run = Json();

        Assert.Equal(1, run.Exit);
        Assert.Equal("catalog_busy", run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Another `sqlbranch pool upgrade` is running", run.Json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(Format1Credentials, _pool.World.Host.CredentialsJson);
        Assert.Empty(_pool.Writes);
        Assert.DoesNotContain("warning:", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_whose_catalog_cannot_be_read_stops_the_upgrade_and_names_image_rm()
    {
        _pool.Images["shop"] = _pool.Images["shop"] with { CatalogBase64 = Convert.ToBase64String("not json"u8.ToArray()) };

        var run = Json();

        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", run.Json.GetProperty("error").GetProperty("code").GetString());
        var message = run.Json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("shop", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch image rm shop", message, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog:shop", _pool.Writes);
    }

    [Fact]
    public void A_corrupt_version_record_names_the_file_and_the_remedy_and_writes_nothing_for_it()
    {
        _pool.Images["shop"] = _pool.Images["shop"] with { IdFile = "garbage" };

        var run = Json();

        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", run.Json.GetProperty("error").GetProperty("code").GetString());
        var message = run.Json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("/pool/images/.ids/shop", message, StringComparison.Ordinal);
        Assert.Contains("Delete that file", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch pool upgrade --yes", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ids:shop", _pool.Writes);
        Assert.DoesNotContain("catalog:shop", _pool.Writes);
    }

    [Fact]
    public void A_high_water_mark_above_the_start_time_keeps_counting_up()
    {
        _pool.Images["shop"] = _pool.Images["shop"] with { IdFile = "shop@20261201T090000500Z" };

        var run = Json();

        Assert.Equal(0, run.Exit);
        Assert.Equal(["crm=crm@20261201T090000000Z", "shop=shop@20261201T090000501Z"], Versions(run.Json));
    }

    [Fact]
    public void The_password_is_never_in_an_argument_an_environment_value_a_script_or_the_output()
    {
        var run = _pool.Run("pool", "upgrade", "--yes", "--format", "json");

        Assert.Equal(0, run.Exit);
        var password = UpgradeWorld.Sentinel;
        Assert.DoesNotContain(password, run.Out + run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(_pool.Scripts, s => s.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(_pool.World.Runtime.Runs, r =>
            r.Command.Any(a => a.Contains(password, StringComparison.Ordinal)) || r.Env.Values.Any(v => v.Contains(password, StringComparison.Ordinal)));
        Assert.DoesNotContain(_pool.World.Runtime.Runs, r => r.Stdin is null && FakeContainerRuntime.ScriptOf(r).Contains("credentials.json.new", StringComparison.Ordinal));
        Assert.Equal(2, _pool.World.Host.CredentialsRewrites.Count);
        Assert.All(_pool.World.Host.CredentialsRewrites, json => Assert.Contains(password, json, StringComparison.Ordinal));
    }
}
