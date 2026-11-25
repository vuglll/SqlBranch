using System.Text.Json;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

public sealed class PoolUpgradeBindingWaitTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.UtcTicks;
    }

    private readonly UpgradeWorld _pool = new();
    private readonly Clock _clock = new();
    private Action? _duringWait;

    public PoolUpgradeBindingWaitTests()
    {
        _pool.World.Clock = _clock;
        _pool.World.UpgradeDelay = (duration, token) =>
        {
            token.ThrowIfCancellationRequested();
            _clock.Now += duration;
            _duringWait?.Invoke();
            return Task.CompletedTask;
        };
    }

    private (int Exit, JsonElement Json, string Err) Upgrade(params string[] options)
    {
        var run = _pool.Run(["pool", "upgrade", "--yes", "--format", "json", .. options]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement.Clone(), run.Err);
    }

    private BranchRecord Record(string name) => BranchRecord.FromJson(_pool.Records[name]);
    private static string[] Names(JsonElement json, string kind) =>
        [.. json.GetProperty("branches").GetProperty(kind).EnumerateArray().Select(n => n.GetString()!)];

    // Feed the actual upgraded record and catalog into the existing database fake, then exercise real list/reset paths.
    private void CheckListAndReset(string name, bool removed, string? version, bool reset, string image = "shop")
    {
        var branch = new BranchWorld();
        branch.UseFormat2(null);
        branch.Catalog = _pool.CatalogOf(image);
        branch.ImageExists = _pool.Images.ContainsKey(image);
        branch.ImageName = image;
        branch.AddBranch(name, image);
        branch.Records[name] = _pool.Records[name];
        var list = branch.Run("branch", "list", "--format", "json");
        Assert.Equal(0, list.Exit);
        var row = JsonDocument.Parse(list.Out).RootElement.GetProperty("branches")[0];
        Assert.Equal(version, row.GetProperty("imageVersion").GetString());
        Assert.Equal(removed, row.GetProperty("imageRemoved").GetBoolean());
        var before = branch.Records[name];
        var result = branch.Run("branch", "reset", name, "--format", "json");
        Assert.Equal(reset ? 0 : 1, result.Exit);
        var json = JsonDocument.Parse(result.Out).RootElement;
        if (reset)
        {
            Assert.Equal(version, json.GetProperty("branch").GetProperty("imageVersion").GetString());
        }
        else
        {
            Assert.Equal("image_not_found", json.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(before, branch.Records[name]);
            Assert.False(branch.Ran("DROP DATABASE") || branch.Ran("SINGLE_USER") || branch.Ran("FOR ATTACH"));
        }
    }

    [Fact]
    public void Branch_older_than_its_image_has_an_unknown_base_and_list_and_reset_respect_it()
    {
        _pool.AddImage("shop");
        _pool.Images["shop"] = _pool.Images["shop"] with { BuiltAt = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero) };
        _pool.AddBranch("old1", createdAt: new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
        _pool.AddBranch("dev1", createdAt: new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal(["dev1"], Names(run.Json, "pinned"));
        Assert.Equal(["old1"], Names(run.Json, "unknownBase"));
        Assert.False(Record("old1").BaseKnown);
        CheckListAndReset("old1", false, null, false);
        CheckListAndReset("dev1", false, "shop@20261201T090000000Z", true);
    }

    [Fact]
    public void Branch_of_a_removed_image_stays_removed_after_the_name_is_reused()
    {
        _pool.AddBranch("pr-9");
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal(["pr-9"], Names(run.Json, "removedBase"));
        Assert.True(Record("pr-9").BaseKnown);
        Assert.Null(Record("pr-9").ImageVersion);
        CheckListAndReset("pr-9", true, null, false);
        _pool.AddImage("shop");
        _pool.Images["shop"] = _pool.Images["shop"] with
        {
            CatalogBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ImageVersions.FirstCatalog("shop", "shop@20261203T020000000Z").ToJson()))
        };
        CheckListAndReset("pr-9", true, null, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Imported_image_with_older_builtAt_pins_even_when_it_replaced_a_force_removed_image(bool replaced)
    {
        _pool.AddImage("crm");
        _pool.Images["crm"] = _pool.Images["crm"] with { BuiltAt = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero) };
        // Replacement: branch predates the local import. Ordinary import: branch follows it.
        _pool.AddBranch("pr-3", "crm", createdAt: new DateTimeOffset(2026, 11, replaced ? 12 : 16, 10, 0, 0, TimeSpan.Zero));
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal(["pr-3"], Names(run.Json, "pinned"));
        Assert.Empty(Names(run.Json, "removedBase"));
        CheckListAndReset("pr-3", false, "crm@20261201T090000000Z", true, "crm");
    }

    [Fact]
    public void A_branch_created_at_exactly_builtAt_is_pinned_and_no_file_time_is_read()
    {
        _pool.AddImage("shop");
        _pool.AddBranch("dev1", createdAt: _pool.Images["shop"].BuiltAt);
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal(["dev1"], Names(run.Json, "pinned"));
        Assert.DoesNotContain(_pool.Scripts, s => s.Contains("stat ", StringComparison.Ordinal));
    }

    [Fact]
    public void Upgrade_JSON_reports_the_exact_pinned_branch_and_warning()
    {
        _pool.AddImage("shop");
        _pool.AddBranch("dev1");
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal(1, run.Json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, run.Json.GetProperty("pool").GetProperty("previousFormat").GetInt32());
        Assert.Equal(2, run.Json.GetProperty("pool").GetProperty("format").GetInt32());
        Assert.Equal("shop@20261201T090000000Z", Assert.Single(run.Json.GetProperty("images").EnumerateArray()).GetProperty("version").GetString());
        Assert.Equal(["dev1"], Names(run.Json, "pinned"));
        Assert.Empty(Names(run.Json, "unknownBase"));
        Assert.Empty(Names(run.Json, "removedBase"));
        Assert.Contains("warning:", run.Err, StringComparison.Ordinal);
        Assert.Contains("SqlBranch.Testing 0.2.x", run.Err, StringComparison.Ordinal);
        var script = Assert.Single(_pool.Scripts, s => s.Contains("/pool/branches/dev1/branch.json.new", StringComparison.Ordinal) && !s.StartsWith(UpgradeTemporaryFiles.CleanupPrefix, StringComparison.Ordinal));
        Assert.Contains("&& sync '/pool/branches/dev1/branch.json.new' && mv -f", script, StringComparison.Ordinal);
        Assert.Contains("sync '/pool/branches/dev1'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Interrupted_binding_rerun_keeps_written_bases_and_reports_identical_groups()
    {
        _pool.AddImage("shop");
        _pool.AddBranch("a");
        _pool.AddBranch("b", createdAt: Start.AddYears(-1));
        _pool.AddBranch("c", "gone");
        _pool.FailWrite = s => s.Contains("/branches/c/", StringComparison.Ordinal) ? "No space left on device" : null;
        Assert.Equal(1, Upgrade().Exit);
        var a = _pool.Records["a"];
        var b = _pool.Records["b"];
        _pool.FailWrite = null;
        _clock.Now += TimeSpan.FromDays(1);
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal(a, _pool.Records["a"]);
        Assert.Equal(b, _pool.Records["b"]);
        Assert.Equal(["a"], Names(run.Json, "pinned"));
        Assert.Equal(["b"], Names(run.Json, "unknownBase"));
        Assert.Equal(["c"], Names(run.Json, "removedBase"));
        Assert.Equal(1, _pool.Writes.Count(w => w == "branch:a"));
        Assert.Equal(1, _pool.Writes.Count(w => w == "branch:b"));
    }

    [Fact]
    public void Branch_create_still_running_times_out_at_ten_minutes_naming_shop_and_dev4()
    {
        _pool.AddImage("shop");
        // The create has made the folder of dev4 but writes branch.json late, so there is no record yet.
        _pool.AddBranch("dev4");
        _pool.Records.Remove("dev4");
        _pool.World.Connector.HeldLocks.UnionWith([EngineLocks.Image("shop"), EngineLocks.Branch("dev4")]);
        var run = Upgrade();
        Assert.Equal(1, run.Exit);
        Assert.Equal(Start.AddMinutes(10), _clock.Now);
        Assert.Equal("catalog_busy", run.Json.GetProperty("error").GetProperty("code").GetString());
        var message = run.Json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("shop", message, StringComparison.Ordinal);
        Assert.Contains("dev4", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch pool upgrade --yes", message, StringComparison.Ordinal);
        Assert.Contains("Waiting for image 'shop', branch 'dev4'", run.Err, StringComparison.Ordinal);
        var status = _pool.Run("status", "--format", "json");
        Assert.Equal(1, status.Exit);
        Assert.Contains("The pool upgrade did not finish", status.Out, StringComparison.Ordinal);
        Assert.All(_pool.World.Connector.Statements.Where(s => s.Contains("sp_getapplock", StringComparison.Ordinal)), s => Assert.Contains("@LockTimeout = 0", s, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wait_set_in_config_file_and_environment_beats_the_file(bool environment)
    {
        using var tree = new SqlBranch.Core.Tests.Configuration.TempTree();
        var config = tree.File(".sqlbranch.yml", "publishWait: 30m");
        if (environment)
        {
            _pool.World.Environment["SQLBRANCH_PUBLISH_WAIT"] = "2m";
        }
        _pool.AddBranch("dev1");
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Branch("dev1"));
        _duringWait = () =>
        {
            if (_clock.Now >= Start.AddMinutes(environment ? 3 : 20))
            {
                _pool.World.Connector.HeldLocks.Remove(EngineLocks.Branch("dev1"));
            }
        };
        var run = Upgrade("--config", config);
        Assert.Equal(environment ? 1 : 0, run.Exit);
        Assert.Equal(Start.AddMinutes(environment ? 2 : 20), _clock.Now);
        if (environment)
        {
            Assert.Equal("catalog_busy", run.Json.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Upgrade_still_running_and_its_temporary_credentials_are_in_progress(bool temporary)
    {
        var checks = 0;
        void CheckStatus()
        {
            checks++;
            if (temporary)
            {
                _pool.World.Host.UpgradeTemporaryFiles.Add("/pool/engine/credentials.json.new");
            }

            var probesBefore = _pool.World.Connector.ProbedLocks.Count;
            var status = _pool.Run("status", "--format", "json");
            Assert.Equal(0, status.Exit);
            var json = JsonDocument.Parse(status.Out).RootElement;
            Assert.Empty(json.GetProperty("mismatches").EnumerateArray());
            Assert.Contains("pool upgrade is running", json.GetProperty("inProgress").EnumerateArray().Select(e => e.GetString()));
            if (temporary)
            {
                Assert.Contains("credentials.json.new", status.Out, StringComparison.Ordinal);
                _pool.World.Host.UpgradeTemporaryFiles.Clear();
            }

            // Status looked at the upgrade lock and never took it: only the upgrade itself holds it.
            Assert.Contains(EngineLocks.PoolUpgrade, _pool.World.Connector.ProbedLocks.Skip(probesBefore));
            Assert.Equal(1, _pool.World.Connector.TakenLocks.Count(l => l == EngineLocks.PoolUpgrade));
        }

        if (temporary)
        {
            // Paused in the write of the temporary credentials file, not in a lock wait.
            _pool.AddBranch("dev1");
            _pool.World.Connector.HeldLocks.Clear();
            _pool.OnCredentialsWrite = CheckStatus;
        }
        else
        {
            _pool.AddBranch("dev1");
            _pool.World.Connector.HeldLocks.Add(EngineLocks.Branch("dev1"));
            _duringWait = () =>
            {
                CheckStatus();
                _pool.World.Connector.HeldLocks.Remove(EngineLocks.Branch("dev1"));
            };
        }

        Assert.Equal(0, Upgrade().Exit);
        Assert.True(checks >= 1);
    }

    [Fact]
    public void Second_upgrade_while_one_runs_changes_nothing_and_first_finishes()
    {
        _pool.AddBranch("dev1");
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Branch("dev1"));
        _duringWait = () =>
        {
            var writes = _pool.Writes.ToArray();
            var second = Upgrade();
            Assert.Equal(1, second.Exit);
            Assert.Equal("catalog_busy", second.Json.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(writes, _pool.Writes);
            _pool.World.Connector.HeldLocks.Remove(EngineLocks.Branch("dev1"));
        };
        Assert.Equal(0, Upgrade().Exit);
    }

    [Fact]
    public void Names_without_image_directories_and_dot_folders_and_build_staging_are_not_polled()
    {
        foreach (var name in new[] { ".removing-crm", ".ids", "build/id", "sealing" })
        {
            _pool.AddImage(name);
            _pool.Images[name] = _pool.Images[name] with { HasManifest = name != "sealing" };
            _pool.World.Connector.HeldLocks.Add(EngineLocks.Image(name));
        }
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Image("unpublished"));
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal(Start, _clock.Now);
        Assert.Empty(run.Json.GetProperty("images").EnumerateArray());
        Assert.Equal([EngineLocks.PoolUpgrade], _pool.World.Connector.TakenLocks);
        Assert.DoesNotContain(_pool.World.Connector.Parameters, p => p.TryGetValue("resource", out var r) && r is string name && name.StartsWith("sqlbranch:image:", StringComparison.Ordinal));
        Assert.DoesNotContain("/pool/build/", SqlBranch.Core.Pools.PoolUpgrader.EnumerationScript, StringComparison.Ordinal);
        Assert.Contains("/pool/images/*/", SqlBranch.Core.Pools.PoolUpgrader.EnumerationScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_and_branch_published_during_wait_are_not_processed_and_acquired_locks_are_not_retried()
    {
        _pool.AddImage("shop");
        _pool.AddBranch("dev1");
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Branch("dev1"));
        _duringWait = () =>
        {
            _pool.AddImage("crm2");
            _pool.AddBranch("late", "crm2");
            _pool.World.Connector.HeldLocks.Remove(EngineLocks.Branch("dev1"));
        };
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal("shop", Assert.Single(run.Json.GetProperty("images").EnumerateArray()).GetProperty("name").GetString());
        Assert.Null(_pool.CatalogOf("crm2"));
        Assert.Null(Record("late").BaseKnown);
        Assert.Equal(1, _pool.World.Connector.Parameters.Count(p => p.GetValueOrDefault("resource") as string == EngineLocks.Image("shop")));
        Assert.DoesNotContain(EngineLocks.Image("crm2"), _pool.World.Connector.TakenLocks);
        Assert.DoesNotContain(EngineLocks.Branch("late"), _pool.World.Connector.TakenLocks);
    }
    [Fact]
    public void Metadata_is_refreshed_under_locks_and_removed_items_are_not_resurrected()
    {
        _pool.AddImage("shop");
        _pool.AddImage("gone");
        _pool.AddBranch("dev1");
        _pool.AddBranch("removed");
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Image("gone"));
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Branch("dev1"));
        _pool.World.Connector.HeldLocks.Add(EngineLocks.Branch("removed"));
        _duringWait = () =>
        {
            _pool.Images.Remove("gone");
            _pool.Records.Remove("removed");
            // A reset finished while the upgrade was waiting for the branch lock.
            _pool.Records["dev1"] = (Record("dev1") with { CreatedAt = Start.AddYears(-1) }).ToJson();
            _pool.World.Connector.HeldLocks.Remove(EngineLocks.Image("gone"));
            _pool.World.Connector.HeldLocks.Remove(EngineLocks.Branch("dev1"));
            _pool.World.Connector.HeldLocks.Remove(EngineLocks.Branch("removed"));
        };
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal(["dev1"], Names(run.Json, "unknownBase"));
        Assert.Equal("shop", Assert.Single(run.Json.GetProperty("images").EnumerateArray()).GetProperty("name").GetString());
        Assert.DoesNotContain("branch:removed", _pool.Writes);
        Assert.DoesNotContain("catalog:gone", _pool.Writes);
    }

    [Fact]
    public void An_unreadable_branch_record_is_left_alone_with_a_warning_and_the_upgrade_finishes()
    {
        _pool.AddImage("shop");
        _pool.AddBranch("dev1");
        _pool.AddBranch("broken");
        _pool.Records["broken"] = "{ not json";
        _pool.AddBranch("badimage");
        _pool.Records["badimage"] = Record("dev1").ToJson().Replace("\"shop\"", "\"Bad Name\"", StringComparison.Ordinal);
        var badimage = _pool.Records["badimage"];
        var run = Upgrade();
        Assert.Equal(0, run.Exit);
        Assert.Equal(["dev1"], Names(run.Json, "pinned"));
        Assert.Equal("{ not json", _pool.Records["broken"]);
        Assert.Equal(badimage, _pool.Records["badimage"]);
        Assert.DoesNotContain("branch:broken", _pool.Writes);
        Assert.DoesNotContain("branch:badimage", _pool.Writes);
        Assert.Contains("branch 'broken'", run.Err, StringComparison.Ordinal);
        Assert.Contains("sqlbranch branch rm broken", run.Err, StringComparison.Ordinal);
        Assert.Contains("sqlbranch branch rm badimage", run.Err, StringComparison.Ordinal);
        Assert.Equal(0, _pool.Run("status", "--format", "json").Exit);
        Assert.Contains("credentials:done", _pool.Writes);
    }

    [Fact]
    public void Wait_progress_is_printed_when_the_busy_set_changes_not_every_second()
    {
        _pool.AddImage("shop");
        _pool.AddBranch("dev1");
        _pool.World.Connector.HeldLocks.UnionWith([EngineLocks.Image("shop"), EngineLocks.Branch("dev1")]);
        var run = Upgrade();
        Assert.Equal(1, run.Exit);
        var waiting = run.Err.Split('\n').Count(l => l.StartsWith("Waiting for", StringComparison.Ordinal));
        Assert.InRange(waiting, 2, 25);
    }

    [Fact]
    public void Binding_preserves_other_record_fields_and_text_names_all_three_groups()
    {
        _pool.AddImage("shop");
        _pool.AddBranch("dev1");
        _pool.AddBranch("old1", createdAt: Start.AddYears(-1));
        _pool.AddBranch("pr-9", "gone");
        var original = Record("dev1") with
        {
            CloneMode = "copy", ExplicitCopy = true, State = "broken", Ephemeral = true,
            ExpiresAt = Start.AddDays(1), Labels = new Dictionary<string, string> { ["team"] = "storage" }
        };
        _pool.Records["dev1"] = original.ToJson();
        var run = _pool.Run("pool", "upgrade", "--yes");
        Assert.Equal(0, run.Exit);
        Assert.Equal((original with { BaseKnown = true, ImageVersion = "shop@20261201T090000000Z" }).ToJson(), _pool.Records["dev1"]);
        Assert.Contains("Branches pinned to the first version: dev1", run.Out, StringComparison.Ordinal);
        Assert.Contains("Branches with an unknown base (they cannot be reset): old1", run.Out, StringComparison.Ordinal);
        Assert.Contains("Branches whose image was already removed (they cannot be reset): pr-9", run.Out, StringComparison.Ordinal);
    }

}
