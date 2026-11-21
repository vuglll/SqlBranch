using System.Text.Json;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

public sealed class StatusCommandTests
{
    private readonly CliWorld _world = new();

    public StatusCommandTests()
    {
        _world.WithEngine();
    }

    private (int Exit, string Out, string Err, JsonElement Json) Json()
    {
        var run = _world.Run("status", "--format", "json");
        return (run.Exit, run.Out, run.Err, JsonDocument.Parse(run.Out).RootElement);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddHours(1);
    }

    [Fact]
    public void Expiry_is_reported_without_a_mismatch()
    {
        _world.Clock = new Clock();
        _world.Host.BranchFolders.Add(("old", true, false));
        _world.Connector.Databases["old"] = ["/pool/branches/old/data/shop.mdf"];
        var record = """{"ephemeral":true,"expiresAt":"1970-01-01T00:30:00Z"}""";
        _world.Host.BranchRecords["old"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(record));
        var run = Json();
        Assert.Equal(0, run.Exit);
        Assert.Equal("old", Assert.Single(run.Json.GetProperty("expiredBranches").EnumerateArray()).GetString());
        Assert.Contains("sqlbranch branch prune", _world.Run("status").Out, StringComparison.Ordinal);
    }

    private static string[] MismatchNames(JsonElement root) =>
        [.. root.GetProperty("mismatches").EnumerateArray().Select(m => m.GetProperty("name").GetString()!)];

    private static string[] MismatchKinds(JsonElement root) =>
        [.. root.GetProperty("mismatches").EnumerateArray().Select(m => m.GetProperty("kind").GetString()!)];

    [Fact]
    public void A_clean_pool_reports_the_engine_the_pool_and_the_counts_and_exits_0()
    {
        _world.Host.ImageFolders.Add("shop");
        _world.Host.BranchFolders.Add(("pr-1", true, false));
        _world.Host.BranchFolders.Add(("pr-2", true, false));
        _world.Connector.Databases["pr-1"] = ["/pool/branches/pr-1/data/shop.mdf", "/pool/branches/pr-1/data/shop_log.ldf"];
        _world.Connector.Databases["pr-2"] = ["/pool/branches/pr-2/data/shop.mdf"];

        var text = _world.Run("status");
        var json = Json();

        Assert.Equal(0, text.Exit);
        Assert.Contains("Engine: running", text.Out, StringComparison.Ordinal);
        Assert.Contains("Engine version: SQL Server 16.0.4325.4", text.Out, StringComparison.Ordinal);
        Assert.Contains("Engine edition: Developer Edition (64-bit)", text.Out, StringComparison.Ordinal);
        Assert.Contains("Pool: /data/sb", text.Out, StringComparison.Ordinal);
        Assert.Contains("Pool backend: plain", text.Out, StringComparison.Ordinal);
        Assert.Contains("Clone mode: thin", text.Out, StringComparison.Ordinal);
        Assert.Contains("Images: 1", text.Out, StringComparison.Ordinal);
        Assert.Contains("Branches: 2", text.Out, StringComparison.Ordinal);
        Assert.Contains("Pool space: 100 GB total, 40 GB used, 60 GB free", text.Out, StringComparison.Ordinal);
        Assert.Equal(0, json.Exit);
        Assert.Equal(60_000_000_000, json.Json.GetProperty("pool").GetProperty("freeBytes").GetInt64());
        Assert.Equal(2, json.Json.GetProperty("branches").GetInt32());
        Assert.Empty(MismatchNames(json.Json));
    }

    [Fact]
    public void Copy_mode_is_stated_with_the_consequence()
    {
        _world.Host.EngineProbe = "copy";

        var run = _world.Run("status");

        Assert.Equal(0, run.Exit);
        Assert.Contains("Clone mode: copy", run.Out, StringComparison.Ordinal);
        Assert.Contains("Branch creation will copy every file in full.", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_branch_folder_without_a_database_exits_1_and_says_the_database_is_missing()
    {
        _world.Host.BranchFolders.Add(("pr-7", true, false));

        var text = _world.Run("status");
        var json = Json();

        Assert.Equal(1, text.Exit);
        Assert.Contains("pr-7", text.Out, StringComparison.Ordinal);
        Assert.Contains("database pr-7 is missing", text.Out, StringComparison.Ordinal);
        Assert.Equal(1, json.Exit);
        Assert.Equal("mismatch", json.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(["pr-7"], MismatchNames(json.Json));
        Assert.Equal(["branch_missing_database"], MismatchKinds(json.Json));
    }

    [Fact]
    public void A_database_with_files_in_the_branches_area_and_no_folder_is_a_mismatch()
    {
        _world.Connector.Databases["pr-9"] = ["/pool/branches/pr-9/data/shop.mdf"];

        var json = Json();

        Assert.Equal(1, json.Exit);
        Assert.Equal(["pr-9"], MismatchNames(json.Json));
        Assert.Equal(["database_missing_folder"], MismatchKinds(json.Json));
    }

    [Fact]
    public void A_user_database_outside_the_branches_area_is_never_a_mismatch()
    {
        _world.Connector.Databases["scratch"] = ["/var/opt/mssql/data/scratch.mdf", "/var/opt/mssql/data/scratch_log.ldf"];
        _world.Connector.Databases["tools"] = ["/pool/engine/data/tools.mdf"];

        var json = Json();

        Assert.Equal(0, json.Exit);
        Assert.Empty(MismatchNames(json.Json));
        Assert.DoesNotContain("scratch", _world.Run("status").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interrupted_build_lists_the_temporary_database_and_the_staging_folder()
    {
        _world.Connector.Databases["sqlbranch_build_a1b2c3"] = ["/pool/build/a1b2c3/shop.mdf"];
        _world.Host.BuildFolders.Add("a1b2c3");

        var text = _world.Run("status");
        var json = Json();

        Assert.Equal(1, text.Exit);
        Assert.Contains("sqlbranch_build_a1b2c3", text.Out, StringComparison.Ordinal);
        Assert.Contains("/pool/build/a1b2c3", text.Out, StringComparison.Ordinal);
        Assert.Equal(["build_database", "build_folder"], MismatchKinds(json.Json));
    }

    [Fact]
    public void A_build_whose_lock_is_held_is_in_progress_and_does_not_count()
    {
        _world.Connector.Databases["sqlbranch_build_a1b2c3"] = ["/pool/build/a1b2c3/shop.mdf"];
        _world.Host.BuildFolders.Add("a1b2c3");
        _world.Connector.HeldLocks.Add(EngineLocks.Build("a1b2c3"));

        var text = _world.Run("status");
        var json = Json();

        Assert.Equal(0, text.Exit);
        Assert.Contains("In progress: image build a1b2c3 is running", text.Out, StringComparison.Ordinal);
        Assert.Equal(0, json.Exit);
        Assert.Equal("image build a1b2c3 is running", json.Json.GetProperty("inProgress")[0].GetString());
    }

    [Fact]
    public void A_killed_imports_build_folder_is_listed_as_a_build_leftover_and_a_running_import_is_not()
    {
        // An import works in /pool/build/<id>/ under the build lock of its id, with no temporary database: status needs no change for it.
        _world.Host.BuildFolders.Add("abc123def456");

        var dead = Json();

        Assert.Equal(1, dead.Exit);
        Assert.Equal(["build_folder"], MismatchKinds(dead.Json));
        Assert.Equal(["/pool/build/abc123def456"], MismatchNames(dead.Json));

        _world.Connector.HeldLocks.Add(EngineLocks.Build("abc123def456"));
        var running = Json();

        Assert.Equal(0, running.Exit);
        Assert.Empty(MismatchNames(running.Json));
        Assert.Equal("image build abc123def456 is running", running.Json.GetProperty("inProgress")[0].GetString());
    }

    [Fact]
    public void Leftover_reset_files_are_listed_unless_the_reset_is_running()
    {
        _world.Host.BranchFolders.Add(("pr-142", true, true));
        _world.Connector.Databases["pr-142"] = ["/pool/branches/pr-142/data/shop.mdf"];

        var dead = Json();
        _world.Connector.HeldLocks.Add(EngineLocks.Branch("pr-142"));
        var running = Json();

        Assert.Equal(1, dead.Exit);
        Assert.Equal(["reset_files"], MismatchKinds(dead.Json));
        Assert.Equal(["pr-142"], MismatchNames(dead.Json));
        Assert.Equal(0, running.Exit);
        Assert.Equal(1, running.Json.GetProperty("inProgress").GetArrayLength());
    }

    [Fact]
    public void A_branch_being_created_is_in_progress_not_a_mismatch()
    {
        _world.Host.BranchFolders.Add(("pr-3", false, false));
        _world.Connector.HeldLocks.Add(EngineLocks.Branch("pr-3"));

        var json = Json();

        Assert.Equal(0, json.Exit);
        Assert.Empty(MismatchNames(json.Json));
    }

    [Fact]
    public void A_partly_removed_image_is_listed()
    {
        _world.Host.RemovingFolders.Add(".removing-shop");

        var text = _world.Run("status");
        var json = Json();

        Assert.Equal(1, text.Exit);
        Assert.Contains("/pool/images/.removing-shop", text.Out, StringComparison.Ordinal);
        Assert.Equal(["image_removal"], MismatchKinds(json.Json));
    }

    [Fact]
    public void Several_kinds_of_leftover_are_all_listed()
    {
        _world.Host.BranchFolders.Add(("pr-7", true, false));
        _world.Host.RemovingFolders.Add(".removing-old");
        _world.Host.BuildFolders.Add("dead01");
        _world.Connector.Databases["scratch"] = ["/var/opt/mssql/data/scratch.mdf"];

        var json = Json();

        Assert.Equal(1, json.Exit);
        Assert.Equal(["branch_missing_database", "build_folder", "image_removal"], MismatchKinds(json.Json));
    }

    [Fact]
    public void A_stopped_engine_exits_3_and_still_shows_the_pool()
    {
        _world.Runtime.Containers["sqlbranch-engine"] = _world.Runtime.Containers["sqlbranch-engine"] with { Running = false };

        var text = _world.Run("status");
        var json = Json();

        Assert.Equal(3, text.Exit);
        Assert.Contains("Engine: stopped", text.Out, StringComparison.Ordinal);
        Assert.Contains("Pool: /data/sb", text.Out, StringComparison.Ordinal);
        Assert.Equal("engine_stopped", json.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("stopped", json.Json.GetProperty("engine").GetProperty("state").GetString());
        Assert.Equal("/data/sb", json.Json.GetProperty("pool").GetProperty("location").GetString());
        Assert.DoesNotContain(_world.Connector.Statements, s => s.Length > 0);
    }

    [Fact]
    public void A_managed_pool_is_reported_with_its_backend_and_the_mount()
    {
        var world = new CliWorld();
        world.WithEngine(backend: "loop-xfs", pool: ManagedPaths.Default.MountPoint);

        var json = JsonDocument.Parse(world.Run("status", "--format", "json").Out).RootElement;

        Assert.Equal("loop-xfs", json.GetProperty("pool").GetProperty("backend").GetString());
        Assert.Equal(ManagedPaths.Default.MountPoint, json.GetProperty("pool").GetProperty("location").GetString());
    }

    [Fact]
    public void The_password_is_absent_from_every_output()
    {
        _world.Host.BranchFolders.Add(("pr-7", true, false));
        _world.Connector.Databases["scratch"] = ["/x.mdf"];
        var outputs = new List<string>();
        foreach (var args in new[] { new[] { "status" }, ["status", "--format", "json"], ["status", "--quiet"] })
        {
            var run = _world.Run(args);
            outputs.AddRange([run.Out, run.Err]);
        }

        Assert.All(outputs, o => Assert.DoesNotContain(CliWorld.StoredPassword, o, StringComparison.Ordinal));
    }
}
