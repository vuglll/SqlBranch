using System.Text;
using System.Text.Json;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>
/// Task 4.7: items an older client wrote in the format-1 layout after the upgrade are listed, and adopted only by commands that write
/// with them (design D3a, D7). The older client's files are written directly into the fake pool.
/// </summary>
public sealed class PoolAdoptionTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.UtcTicks;
    }

    private static readonly DateTimeOffset UpgradeTime = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly UpgradeWorld _pool = new();
    private readonly Clock _clock;

    public PoolAdoptionTests() : this(UpgradeTime) { }

    private PoolAdoptionTests(DateTimeOffset now)
    {
        _clock = new Clock(now);
        _pool.World.Clock = _clock;
        // A database is never found by name, and a branch folder's database is online, so creates and resets run on the fake.
        _pool.World.Connector.ParameterizedScalarHandler = (sql, _) => sql.Contains("state_desc", StringComparison.Ordinal) ? "ONLINE" : 0;
    }

    private void Format2(string mode)
    {
        _pool.World.Host.EngineProbe = mode;
        _pool.World.Host.CredentialsJson = Format2Pool.Credentials();
    }

    private (int Exit, JsonElement Json) Run(params string[] args)
    {
        var run = _pool.Run([.. args, "--format", "json"]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement.Clone());
    }

    private static string Code(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString()!;

    private JsonElement ListedRow(string name) =>
        Run("image", "list").Json.GetProperty("images").EnumerateArray().Single(i => i.GetProperty("name").GetString() == name);

    private bool ScriptRan(string marker) => _pool.Scripts.Any(s => s.Contains(marker, StringComparison.Ordinal));

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Image_written_by_0_2_2_after_the_upgrade_is_listed_with_a_null_version_then_adopted_by_create(string mode)
    {
        Format2(mode);
        _clock.Now = new DateTimeOffset(2026, 12, 2, 8, 0, 0, TimeSpan.Zero);
        _pool.AddImage("crm");
        var files = _pool.Images["crm"];

        var before = Run("image", "list");
        Assert.Equal(0, before.Exit);
        var row = Assert.Single(before.Json.GetProperty("images").EnumerateArray());
        Assert.Equal("crm", row.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("version").ValueKind);
        Assert.Empty(_pool.Writes);
        Assert.Equal(files, _pool.Images["crm"]);

        var created = Run("branch", "create", "dev6", "--from", "crm");

        Assert.Equal(0, created.Exit);
        Assert.Equal("crm@20261202T080000000Z", created.Json.GetProperty("branch").GetProperty("imageVersion").GetString());
        Assert.Equal("crm@20261202T080000000Z", _pool.RecordOf("dev6").ImageVersion);
        Assert.True(_pool.RecordOf("dev6").BaseKnown);
        // The ID, then the catalog, then the record: the image is a version before the branch names it.
        Assert.Equal(["ids:crm", "catalog:crm", "branch:dev6"], _pool.Writes);
        var listed = Run("image", "list").Json.GetProperty("images");
        Assert.Equal("crm@20261202T080000000Z", Assert.Single(listed.EnumerateArray()).GetProperty("version").GetString());
        Assert.Equal(1, listed.GetArrayLength());
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Adopted_name_that_was_removed_and_reused_gets_an_id_above_the_one_it_had(string mode)
    {
        Format2(mode);
        _pool.AddImage("crm");
        _pool.Images["crm"] = _pool.Images["crm"] with { CatalogBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Format2Pool.Catalog("crm", "crm@20261201T020000000Z"))) };
        _pool.LastIds["crm"] = "crm@20261201T020000000Z";
        Assert.Equal(0, Run("image", "rm", "crm").Exit);
        Assert.DoesNotContain("crm", _pool.Images.Keys);

        // An older client builds the name again, and the clock is behind the ID the name had.
        _pool.AddImage("crm");
        _clock.Now = new DateTimeOffset(2026, 11, 30, 2, 0, 0, TimeSpan.Zero);
        var created = Run("branch", "create", "dev6", "--from", "crm");

        Assert.Equal(0, created.Exit);
        Assert.Equal("crm@20261201T020000001Z", created.Json.GetProperty("branch").GetProperty("imageVersion").GetString());
        Assert.Equal("crm@20261201T020000001Z", _pool.LastIds["crm"]);
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Branch_written_by_0_2_2_after_the_upgrade_is_bound_by_builtAt_at_reset(string mode)
    {
        _pool.World.Host.EngineProbe = mode;
        _pool.AddImage("shop");
        Assert.Equal(0, _pool.Run("pool", "upgrade", "--yes").Exit);
        Assert.Contains("shop@20261201T090000000Z", _pool.CatalogOf("shop"), StringComparison.Ordinal);
        _clock.Now = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
        _pool.AddBranch("dev8", createdAt: _clock.Now);
        _pool.World.Connector.Databases["dev8"] = ["/pool/branches/dev8/data/Shop.mdf"];
        var writes = _pool.Writes.Count;

        var reset = Run("branch", "reset", "dev8");

        Assert.Equal(0, reset.Exit);
        Assert.Equal("shop@20261201T090000000Z", reset.Json.GetProperty("branch").GetProperty("imageVersion").GetString());
        Assert.Equal("shop@20261201T090000000Z", _pool.RecordOf("dev8").ImageVersion);
        Assert.True(_pool.RecordOf("dev8").BaseKnown);
        // The image already had its first version: nothing but the record was written.
        // (the record is written twice by a reset: once marked broken, once final)
        Assert.All(_pool.Writes.Skip(writes), w => Assert.Equal("branch:dev8", w));
    }

    [Fact]
    public void Reset_of_a_branch_older_than_its_image_has_an_unknown_base_and_changes_nothing()
    {
        Format2("thin");
        _pool.AddImage("crm");
        _pool.AddBranch("old1", "crm", createdAt: _pool.Images["crm"].BuiltAt.AddDays(-1));
        _pool.World.Connector.Databases["old1"] = ["/pool/branches/old1/data/Shop.mdf"];
        var record = _pool.Records["old1"];

        var reset = Run("branch", "reset", "old1");

        Assert.Equal(1, reset.Exit);
        Assert.Equal("image_not_found", Code(reset.Json));
        Assert.Contains("sqlbranch branch rm old1", reset.Json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(record, _pool.Records["old1"]);
        Assert.Empty(_pool.Writes);
        Assert.DoesNotContain(_pool.World.Connector.Statements, s => s.Contains("DROP DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void Reset_of_a_legacy_branch_of_an_image_nobody_adopted_adopts_the_image_first()
    {
        Format2("copy");
        _clock.Now = new DateTimeOffset(2026, 12, 2, 8, 0, 0, TimeSpan.Zero);
        _pool.AddImage("shop");
        _pool.AddBranch("dev8", createdAt: new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        _pool.World.Connector.Databases["dev8"] = ["/pool/branches/dev8/data/Shop.mdf"];

        var reset = Run("branch", "reset", "dev8");

        Assert.Equal(0, reset.Exit);
        Assert.Equal("shop@20261202T080000000Z", reset.Json.GetProperty("branch").GetProperty("imageVersion").GetString());
        Assert.Equal(["ids:shop", "catalog:shop", "branch:dev8", "branch:dev8"], _pool.Writes);
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Name_used_by_an_image_an_older_client_wrote_is_image_exists_and_its_files_stay(string mode)
    {
        Format2(mode);
        _pool.AddImage("crm");
        _pool.World.Files["/work/crm.bak"] = 100;
        var files = _pool.Images["crm"];

        var run = Run("image", "build", "crm", "--from-bak", "crm.bak");

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_exists", Code(run.Json));
        Assert.Equal(files, _pool.Images["crm"]);
        Assert.Empty(_pool.Writes);
        Assert.False(ScriptRan("mkdir"));
        Assert.False(ScriptRan("catalog.json.new"));
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Remove_an_image_not_adopted_yet_reports_no_versions(string mode)
    {
        Format2(mode);
        _pool.AddImage("crm");

        var removed = Run("image", "rm", "crm");

        Assert.Equal(0, removed.Exit);
        Assert.Empty(removed.Json.GetProperty("image").GetProperty("versions").EnumerateArray());
        Assert.Empty(Run("image", "list").Json.GetProperty("images").EnumerateArray());
        // No ID was ever given, so no high-water mark changed.
        Assert.Empty(_pool.Writes);
        Assert.Empty(_pool.LastIds);
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public void Image_busy_during_adoption_is_catalog_busy_naming_the_image_and_nothing_changes(string mode)
    {
        Format2(mode);
        _pool.AddImage("crm");
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Image("crm"));
        var files = _pool.Images["crm"];

        var run = Run("branch", "create", "dev7", "--from", "crm");

        Assert.Equal(1, run.Exit);
        Assert.Equal("catalog_busy", Code(run.Json));
        Assert.Contains("'crm'", run.Json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("dev7", _pool.Records.Keys);
        Assert.False(ScriptRan("/pool/branches/dev7"));
        Assert.Empty(_pool.Writes);
        Assert.Equal(files, _pool.Images["crm"]);
    }

    [Fact]
    public void Adoption_that_finds_another_holder_is_catalog_busy_and_never_waits()
    {
        Format2("thin");
        _pool.World.Connector.TrackSessionLocks = false;
        _pool.AddImage("crm");
        // Another session takes the image in shared mode right after this command did: the exclusive try must fail at once.
        _pool.World.Connector.OnTakeLock = taken =>
        {
            if (taken == EngineLocks.Image("crm"))
            {
                _pool.World.Connector.HeldLocks.Add(taken);
            }
        };

        var run = Run("branch", "create", "dev7", "--from", "crm");

        Assert.Equal(1, run.Exit);
        Assert.Equal("catalog_busy", Code(run.Json));
        var attempts = _pool.World.Connector.Statements.Where(s => s.Contains("sp_getapplock", StringComparison.Ordinal)).ToList();
        Assert.All(attempts, s => Assert.Contains("@LockTimeout = 0", s, StringComparison.Ordinal));
        // Shared first, then the shared lock is given back and the exclusive one is tried.
        Assert.Contains("'Shared'", attempts.First(s => s.Contains("Shared", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains(EngineLocks.Image("crm"), _pool.World.Connector.ReleasedLocks);
        Assert.Empty(_pool.Writes);
        Assert.DoesNotContain("dev7", _pool.Records.Keys);
    }

    [Fact]
    public void Adoption_that_loses_the_shared_lock_after_publishing_says_so_and_creates_no_branch()
    {
        Format2("thin");
        _pool.World.Connector.TrackSessionLocks = false;
        _pool.AddImage("crm");
        // Takes: 1 shared (create), 2 exclusive (adoption). Another command holds the image right after, so the shared retake fails.
        var takes = 0;
        _pool.World.Connector.OnTakeLock = taken =>
        {
            if (taken == EngineLocks.Image("crm") && ++takes == 2)
            {
                _pool.World.Connector.HeldLocks.Add(taken);
            }
        };

        var run = Run("branch", "create", "dev7", "--from", "crm");

        Assert.Equal(1, run.Exit);
        Assert.Equal("catalog_busy", Code(run.Json));
        var message = run.Json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("was adopted as", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was changed", message, StringComparison.Ordinal);
        Assert.DoesNotContain("dev7", _pool.Records.Keys);
        Assert.Equal(["ids:crm", "catalog:crm"], _pool.Writes);
    }

    [Fact]
    public void Reset_of_a_legacy_branch_meets_a_busy_image_with_catalog_busy()
    {
        Format2("thin");
        _pool.AddImage("shop");
        _pool.AddBranch("dev8", createdAt: new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        _pool.World.Connector.Databases["dev8"] = ["/pool/branches/dev8/data/Shop.mdf"];
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Image("shop"));
        var record = _pool.Records["dev8"];

        var run = Run("branch", "reset", "dev8");

        Assert.Equal(1, run.Exit);
        Assert.Equal("catalog_busy", Code(run.Json));
        Assert.Equal(record, _pool.Records["dev8"]);
        Assert.Empty(_pool.Writes);
    }

    [Fact]
    public void Commands_that_only_read_never_adopt()
    {
        Format2("thin");
        _pool.AddImage("crm");
        _pool.AddBranch("dev1", "crm");
        var files = _pool.Images["crm"];
        var record = _pool.Records["dev1"];

        Assert.Equal(0, Run("image", "list").Exit);
        Assert.Equal(0, Run("branch", "list").Exit);
        Assert.Equal(0, _pool.Run("branch", "conn", "dev1").Exit);
        // image export of an unadopted image exits 0 without adopting: ImageExportTests.Image_export_of_an_unadopted_image_does_not_adopt_it.
        Assert.Equal(0, _pool.Run("status").Exit);

        Assert.Empty(_pool.Writes);
        Assert.Equal(files, _pool.Images["crm"]);
        Assert.Equal(record, _pool.Records["dev1"]);
        Assert.DoesNotContain(EngineLocks.Image("crm"), _pool.World.Connector.TakenLocks);
    }

    [Fact]
    public void Image_rm_of_an_adopted_image_lists_the_version_it_removes()
    {
        Format2("thin");
        _clock.Now = new DateTimeOffset(2026, 12, 2, 8, 0, 0, TimeSpan.Zero);
        _pool.AddImage("crm");
        Assert.Equal(0, Run("branch", "create", "dev6", "--from", "crm").Exit);

        var removed = Run("image", "rm", "crm", "--force");

        Assert.Equal(0, removed.Exit);
        Assert.Equal(["crm@20261202T080000000Z"], removed.Json.GetProperty("image").GetProperty("versions").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void Create_refused_by_requireThin_on_a_legacy_image_writes_nothing()
    {
        Format2("copy");
        _pool.World.Environment["SQLBRANCH_REQUIRE_THIN"] = "true";
        _pool.AddImage("crm");
        var files = _pool.Images["crm"];

        var run = Run("branch", "create", "dev7", "--from", "crm");

        Assert.Equal(3, run.Exit);
        Assert.Empty(_pool.Writes);
        Assert.Equal(files, _pool.Images["crm"]);
        Assert.DoesNotContain("dev7", _pool.Records.Keys);
    }

    [Fact]
    public void Reset_refused_by_requireThin_on_a_legacy_image_writes_nothing()
    {
        Format2("copy");
        _pool.World.Environment["SQLBRANCH_REQUIRE_THIN"] = "true";
        _pool.AddImage("shop");
        _pool.AddBranch("dev8", createdAt: new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        _pool.World.Connector.Databases["dev8"] = ["/pool/branches/dev8/data/Shop.mdf"];
        var record = _pool.Records["dev8"];

        var run = Run("branch", "reset", "dev8");

        Assert.Equal(3, run.Exit);
        Assert.Empty(_pool.Writes);
        Assert.Equal(record, _pool.Records["dev8"]);
    }

    [Fact]
    public void Reset_of_a_legacy_branch_whose_image_was_replaced_in_the_adoption_window_is_catalog_busy_and_writes_nothing()
    {
        Format2("thin");
        _pool.AddImage("shop");
        _pool.AddBranch("dev8", createdAt: new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        _pool.World.Connector.Databases["dev8"] = ["/pool/branches/dev8/data/Shop.mdf"];
        _pool.World.Connector.TrackSessionLocks = false;
        var record = _pool.Records["dev8"];
        // 'image rm --force' and a rebuild by an older client land between the shared lock and the exclusive one.
        var takes = 0;
        _pool.World.Connector.OnTakeLock = taken =>
        {
            // The first take is the shared one of the reset, the second the adoption's exclusive one.
            if (taken == EngineLocks.Image("shop") && ++takes == 2)
            {
                _pool.Images["shop"] = new UpgradeWorld.FakeImage("shop", true, null, null) { BuiltAt = new DateTimeOffset(2026, 12, 1, 9, 30, 0, TimeSpan.Zero) };
            }
        };

        var run = Run("branch", "reset", "dev8");

        Assert.Equal(1, run.Exit);
        Assert.Equal("catalog_busy", Code(run.Json));
        Assert.Equal(record, _pool.Records["dev8"]);
        Assert.Empty(_pool.Writes);
        Assert.DoesNotContain(_pool.World.Connector.Statements, s => s.Contains("DROP DATABASE", StringComparison.Ordinal));
    }
}
