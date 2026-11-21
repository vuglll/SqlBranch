using System.Text;
using System.Text.Json;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

/// <summary>Task 6.8: <c>branch list</c> on fakes. The script's answers and the engine's database rows are set by each test.</summary>
public sealed class BranchListTests
{
    private readonly CliWorld _world = new();
    private readonly List<string> _images = ["shop"];
    private readonly List<string> _scripts = [];

    /// <summary>The branch folders: name, record as the file holds it, own bytes as the script prints them.</summary>
    private readonly List<(string Name, string Record, string Own)> _branches = [];

    /// <summary>The databases of the engine with files under the branches folder: name, state, files.</summary>
    private readonly List<(string Name, string State, string[] Files)> _databases = [];

    public BranchListTests()
    {
        _world.WithEngine();
        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (!script.Contains("SQLBRANCH_BR|", StringComparison.Ordinal))
            {
                return inner(container, command);
            }

            _scripts.Add(script);
            var lines = _images.Select(i => $"SQLBRANCH_IMG|{i}")
                .Concat(_branches.Select(b => $"SQLBRANCH_BR|{b.Name}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(b.Record))}|{b.Own}"));
            return FakeContainerRuntime.Ok(string.Join('\n', lines) + "\n");
        };
        _world.Connector.RowsHandler = (_, parameters) =>
        {
            Assert.Equal("/pool/branches/", parameters["prefix"]);
            return [.. _databases.SelectMany(d => d.Files.Select(f => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = d.Name,
                ["state"] = d.State,
                ["file_name"] = f,
            }))];
        };
    }

    private static string Record(string image, string mode, string created = "2026-11-17T09:30:00+00:00", string? state = null) =>
        $"{{\"image\":\"{image}\",\"cloneMode\":\"{mode}\",\"createdAt\":\"{created}\",\"labels\":{{}}{(state is null ? string.Empty : $",\"state\":\"{state}\"")}}}";

    private void Branch(string name, string image = "shop", string mode = "thin", string own = "1048576", string state = "ONLINE", string? recordedState = null, bool attached = true)
    {
        _branches.Add((name, Record(image, mode, state: recordedState), own));
        if (attached)
        {
            _databases.Add((name, state, [$"/pool/branches/{name}/data/Shop.mdf", $"/pool/branches/{name}/data/Shop_log.ldf"]));
        }
    }

    private (int Exit, JsonElement Json, string Out) Json()
    {
        var run = _world.Run("branch", "list", "--format", "json");
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement, run.Out);
    }

    [Theory]
    [InlineData("run=17", null, "a,b")]
    [InlineData("sqlbranch.owner=testing", null, "b")]
    [InlineData("run=17", "sqlbranch.owner=testing", "b")]
    [InlineData("run=17", "run=18", "")]
    [InlineData("run=99", null, "")]
    public void Label_filters_require_every_exact_match(string first, string? second, string expected)
    {
        _branches.Add(("a", """{"image":"shop","labels":{"run":"17"}}""", "0"));
        _branches.Add(("b", """{"image":"shop","labels":{"run":"17","sqlbranch.owner":"testing"}}""", "0"));
        Branch("unlabeled");
        string[] more = second is null ? [] : ["--label", second];
        var run = _world.Run(["branch", "list", "--label", first, .. more, "--format", "json"]);
        Assert.Equal(0, run.Exit);
        using var document = JsonDocument.Parse(run.Out);
        var names = document.RootElement.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("name").GetString());
        Assert.Equal(expected, string.Join(',', names));
    }

    [Fact]
    public void Invalid_label_filter_exits_with_usage_error_before_listing()
    {
        var run = _world.Run("branch", "list", "--label", "Bad Key=x", "--format", "json");
        Assert.Equal(2, run.Exit);
        using var document = JsonDocument.Parse(run.Out);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_scripts);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddHours(1);
    }

    [Fact]
    public void Expired_branch_is_still_online()
    {
        _world.Clock = new Clock();
        _branches.Add(("old", """{"image":"shop","ephemeral":true,"expiresAt":"1970-01-01T00:30:00Z","labels":{"run":"17"}}""", "1"));
        _databases.Add(("old", "ONLINE", ["/pool/branches/old/data/shop.mdf"]));
        var entry = Entry(Json().Json, "old");
        Assert.Equal("online", entry.GetProperty("state").GetString());
        Assert.True(entry.GetProperty("ephemeral").GetBoolean());
        Assert.True(entry.GetProperty("expired").GetBoolean());
        Assert.Equal("1970-01-01T00:30:00Z", entry.GetProperty("expiresAt").GetString());
        Assert.Equal("17", entry.GetProperty("labels").GetProperty("run").GetString());
    }

    private static JsonElement Entry(JsonElement json, string name) =>
        json.GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("name").GetString() == name);

    [Fact]
    public void The_list_shows_name_image_clone_mode_created_time_state_and_own_size_in_json()
    {
        Branch("pr-142", own: "52428800");
        Branch("bench1", mode: "copy", own: "1073741824");

        var run = Json();

        Assert.Equal(0, run.Exit);
        Assert.Equal(["bench1", "pr-142"], run.Json.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("name").GetString()!).ToArray());
        var thin = Entry(run.Json, "pr-142");
        Assert.Equal("shop", thin.GetProperty("image").GetString());
        Assert.False(thin.GetProperty("imageRemoved").GetBoolean());
        Assert.Equal("thin", thin.GetProperty("cloneMode").GetString());
        Assert.Equal("2026-11-17T09:30:00Z", thin.GetProperty("createdAt").GetString());
        Assert.Equal("online", thin.GetProperty("state").GetString());
        Assert.Equal(52_428_800, thin.GetProperty("ownBytes").GetInt64());
        var copy = Entry(run.Json, "bench1");
        Assert.Equal("copy", copy.GetProperty("cloneMode").GetString());
        Assert.Equal(1_073_741_824, copy.GetProperty("ownBytes").GetInt64());
    }

    [Fact]
    public void The_text_list_has_a_header_one_row_per_branch_and_marks_a_removed_image()
    {
        Branch("pr-142", image: "shop", own: "52428800");
        Branch("pr-2", image: "gone");

        var run = _world.Run("branch", "list");

        Assert.Equal(0, run.Exit);
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("NAME", lines[0], StringComparison.Ordinal);
        foreach (var header in new[] { "IMAGE", "CLONE MODE", "CREATED", "STATE", "OWN DATA" })
        {
            Assert.Contains(header, lines[0], StringComparison.Ordinal);
        }

        var pr142 = lines.Single(l => l.StartsWith("pr-142", StringComparison.Ordinal));
        Assert.Contains("shop", pr142, StringComparison.Ordinal);
        Assert.DoesNotContain("(removed)", pr142, StringComparison.Ordinal);
        Assert.Contains("2026-11-17 09:30 UTC", pr142, StringComparison.Ordinal);
        Assert.Contains("online", pr142, StringComparison.Ordinal);
        Assert.Contains("50 MiB", pr142, StringComparison.Ordinal);
        Assert.Contains("gone (removed)", lines.Single(l => l.StartsWith("pr-2", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_branch_whose_image_has_no_manifest_is_listed_with_the_image_marked_removed_and_state_online()
    {
        _images.Clear();
        Branch("pr-142");

        var run = Json();

        Assert.Equal(0, run.Exit);
        var entry = Entry(run.Json, "pr-142");
        Assert.Equal("shop", entry.GetProperty("image").GetString());
        Assert.True(entry.GetProperty("imageRemoved").GetBoolean());
        Assert.Equal("online", entry.GetProperty("state").GetString());
    }

    [Fact]
    public void A_database_set_offline_is_offline()
    {
        Branch("pr-1", state: "OFFLINE");

        Assert.Equal("offline", Entry(Json().Json, "pr-1").GetProperty("state").GetString());
    }

    [Fact]
    public void A_branch_json_with_no_attached_database_is_broken()
    {
        Branch("dead", attached: false);

        Assert.Equal("broken", Entry(Json().Json, "dead").GetProperty("state").GetString());
    }

    [Fact]
    public void A_database_of_the_same_name_whose_files_are_elsewhere_does_not_make_the_branch_online()
    {
        Branch("scratch", attached: false);
        _databases.Add(("scratch", "ONLINE", ["/pool/branches/other/data/Shop.mdf"]));

        Assert.Equal("broken", Entry(Json().Json, "scratch").GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("SUSPECT")]
    [InlineData("RECOVERY_PENDING")]
    [InlineData("RESTORING")]
    public void An_engine_state_other_than_online_or_offline_is_broken(string engineState)
    {
        Branch("odd", state: engineState);

        Assert.Equal("broken", Entry(Json().Json, "odd").GetProperty("state").GetString());
    }

    [Fact]
    public void A_record_that_says_broken_is_broken_even_when_a_database_is_attached()
    {
        Branch("pr-142", recordedState: "broken");

        Assert.Equal("broken", Entry(Json().Json, "pr-142").GetProperty("state").GetString());
    }

    [Fact]
    public void A_record_that_cannot_be_read_is_broken_with_unknown_image_and_a_warning()
    {
        _branches.Add(("garbled", "not json at all", "-"));
        _databases.Add(("garbled", "ONLINE", ["/pool/branches/garbled/data/Shop.mdf"]));

        var run = Json();
        var err = _world.Run("branch", "list", "--format", "json").Err;

        Assert.Equal(0, run.Exit);
        var entry = Entry(run.Json, "garbled");
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("image").ValueKind);
        Assert.False(entry.GetProperty("imageRemoved").GetBoolean());
        Assert.Equal("unknown", entry.GetProperty("cloneMode").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("ownBytes").ValueKind);
        Assert.Equal("broken", entry.GetProperty("state").GetString());
        Assert.Contains("warning: The record of branch garbled", err, StringComparison.Ordinal);
        Assert.Equal(2, err.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("warning:", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_database_without_a_branch_folder_is_not_a_branch_and_is_not_listed()
    {
        Branch("pr-142");
        _databases.Add(("orphan", "ONLINE", ["/pool/branches/orphan/data/Shop.mdf"]));

        var run = Json();

        Assert.Equal(["pr-142"], run.Json.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public void A_folder_that_breaks_the_name_rules_is_not_listed()
    {
        _branches.Add(("Bad Name", Record("shop", "thin"), "1"));
        Branch("pr-142");

        Assert.Equal(["pr-142"], Json().Json.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public void No_branches_say_how_to_make_one_and_give_an_empty_json_list()
    {
        var text = _world.Run("branch", "list");
        var json = Json();

        Assert.Equal(0, text.Exit);
        Assert.Contains("sqlbranch branch create", text.Out, StringComparison.Ordinal);
        Assert.Equal(0, json.Json.GetProperty("branches").GetArrayLength());
    }

    [Fact]
    public void An_own_size_that_could_not_be_read_is_null_with_a_warning_and_a_dash_in_text()
    {
        Branch("pr-142", own: "-");

        var json = Json();
        var text = _world.Run("branch", "list");

        Assert.Equal(JsonValueKind.Null, Entry(json.Json, "pr-142").GetProperty("ownBytes").ValueKind);
        Assert.Contains("warning: The changed-data size of branch pr-142", text.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_listing_script_reads_the_pool_only_and_never_writes_and_the_password_is_never_in_the_output()
    {
        Branch("pr-142");

        var run = _world.Run("branch", "list", "--format", "json");

        Assert.DoesNotContain(CliWorld.StoredPassword, run.Out + run.Err, StringComparison.Ordinal);
        var script = Assert.Single(_scripts);
        Assert.Contains("filefrag -v", script, StringComparison.Ordinal);
        Assert.Contains("du -sB1", script, StringComparison.Ordinal);
        foreach (var write in new[] { "rm ", "mv ", "cp ", "mkdir", "chmod", "chown", "> /" })
        {
            Assert.DoesNotContain(write, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Before_init_the_list_exits_3_not_initialized_and_with_a_stopped_engine_it_exits_3_engine_stopped()
    {
        var stopped = new CliWorld();
        stopped.WithEngine(running: false);
        var run = stopped.Run("branch", "list", "--format", "json");
        Assert.Equal(3, run.Exit);
        Assert.Contains("engine_stopped", run.Out, StringComparison.Ordinal);

        var fresh = new CliWorld();
        var before = fresh.Run("branch", "list", "--format", "json");
        Assert.Equal(3, before.Exit);
        Assert.Contains("not_initialized", before.Out, StringComparison.Ordinal);
    }
}
