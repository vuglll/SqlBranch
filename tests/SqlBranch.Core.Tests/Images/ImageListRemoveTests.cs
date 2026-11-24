using System.Text;
using System.Text.Json;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

/// <summary>
/// <c>image list</c> and <c>image rm</c> on fakes. The pool is a small model behind the engine container's
/// <c>exec</c>: images with manifests, branch folders with <c>branch.json</c>, and <c>.removing-*</c> folders.
/// </summary>
public sealed class ImageListRemoveTests
{
    private readonly CliWorld _world = new();

    /// <summary>Image folders, by name.</summary>
    private readonly Dictionary<string, ImageManifest> _images = [];

    /// <summary>Raw <c>manifest.json</c> text that replaces the written one, for a damaged file.</summary>
    private readonly Dictionary<string, string> _rawManifests = [];

    /// <summary>Branch folders: name, then the text of its <c>branch.json</c>.</summary>
    private readonly Dictionary<string, string> _branches = [];

    private readonly HashSet<string> _removing = [];

    /// <summary>The <c>catalog.json</c> text of image folders on a format-2 pool, by name.</summary>
    private readonly Dictionary<string, string> _catalogs = [];

    private string? _credentialsLine;

    /// <summary>Every script run in the engine, in order, apart from the clone probe and the status listing.</summary>
    private readonly List<string> _scripts = [];

    private bool _failDelete;

    public ImageListRemoveTests()
    {
        _world.WithEngine();
        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (script == StatusReporter.ListingScript)
            {
                _world.Host.ImageFolders.Clear();
                _world.Host.ImageFolders.AddRange(_images.Keys);
                _world.Host.RemovingFolders.Clear();
                _world.Host.RemovingFolders.AddRange(_removing);
                _world.Host.BranchFolders.Clear();
                _world.Host.BranchFolders.AddRange(_branches.Keys.Select(b => (b, true, false)));
                foreach (var branch in _branches.Keys)
                {
                    _world.Connector.Databases[branch] = [$"/pool/branches/{branch}/data/db.mdf"];
                }

                return inner(container, command);
            }

            if (script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal))
            {
                return inner(container, command);
            }

            _scripts.Add(script);
            if (script == ImageCatalog.ListingScript)
            {
                return FakeContainerRuntime.Ok(Listing());
            }

            if (script.Contains("mv -T", StringComparison.Ordinal))
            {
                var name = NameIn(script, "/pool/images/");
                if (_removing.Contains(".removing-" + name))
                {
                    return FakeContainerRuntime.Ok("SQLBRANCH_LEFTOVER=yes");
                }

                _images.Remove(name);
                _catalogs.Remove(name);
                _removing.Add(".removing-" + name);
                return FakeContainerRuntime.Ok("SQLBRANCH_RENAMED=yes");
            }

            if (script.StartsWith("rm -rf '/pool/images/.removing-", StringComparison.Ordinal))
            {
                if (_failDelete)
                {
                    return FakeContainerRuntime.Fail(1, "rm: cannot remove '/pool/images/.removing-crm/data/crm.mdf': Read-only file system");
                }

                _removing.Remove(NameIn(script, "/pool/images/"));
                return FakeContainerRuntime.Ok();
            }

            return FakeContainerRuntime.Ok();
        };
    }

    /// <summary>The folder name after <paramref name="prefix"/> in the first quoted path of a script.</summary>
    private static string NameIn(string script, string prefix)
    {
        if (script.Contains(".removing-", StringComparison.Ordinal) && script.StartsWith("rm -rf", StringComparison.Ordinal))
        {
            var start = script.IndexOf(".removing-", StringComparison.Ordinal);
            return script[start..].TrimEnd('\'');
        }

        var from = script.IndexOf("mv -T '" + prefix, StringComparison.Ordinal) + "mv -T '".Length + prefix.Length;
        return script[from..script.IndexOf('\'', from)];
    }

    /// <summary>What the listing script prints for a credentials file: only the format number, 1 when there is none.</summary>
    private static string FormatNumber(string credentialsJson)
    {
        using var document = JsonDocument.Parse(credentialsJson);
        return document.RootElement.TryGetProperty("format", out var f) ? f.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : "1";
    }

    private string Listing()
    {
        var lines = new List<string>();
        if (_credentialsLine is not null)
        {
            lines.Add($"SQLBRANCH_POOLCRED|{FormatNumber(_credentialsLine)}");
        }

        foreach (var (name, json) in _catalogs)
        {
            lines.Add($"SQLBRANCH_CATALOG|{name}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}");
        }

        foreach (var (name, manifest) in _images.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            var json = _rawManifests.GetValueOrDefault(name) ?? manifest.ToJson();
            lines.Add($"SQLBRANCH_IMAGE|{name}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}");
        }

        foreach (var (name, json) in _branches)
        {
            lines.Add($"SQLBRANCH_BRANCHJSON|{name}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}");
        }

        return string.Join('\n', lines);
    }

    private void AddImage(string name, string source, long dataBytes = 8L << 20, DateTimeOffset? built = null)
    {
        _images[name] = new ImageManifest(
            ImageManifest.CurrentFormat, name, source, "16.0.4125.3",
            [new ImageFile(source, source + ".mdf", "data", dataBytes), new ImageFile(source + "_log", source + "_log.ldf", "log", 524_288)],
            "FULL", "SIMPLE", 268_435_456, LogRebuilt: true,
            built ?? new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero),
            source + ".bak", new string('a', 64), new Dictionary<string, string>());
    }

    /// <summary>Makes the pool a format-2 pool, as the helper writes it (design D2).</summary>
    private void UseFormat2()
    {
        _credentialsLine = Format2Pool.Credentials();
        _world.Host.CredentialsJson = _credentialsLine;
    }

    private void AddVersion(string name, string source, string stamp = "20261201T090000000Z")
    {
        AddImage(name, source);
        _catalogs[name] = Format2Pool.Catalog(name, Format2Pool.VersionId(name, stamp));
    }

    private void AddBranch(string name, string image) =>
        _branches[name] = $"{{\"image\":\"{image}\",\"cloneMode\":\"thin\",\"created\":\"2026-11-16T11:00:00Z\"}}";

    private (int Exit, JsonElement Json, string Err) Json(params string[] args)
    {
        var run = _world.Run([.. args, "--format", "json"]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement, run.Err);
    }

    private static string Code(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString()!;

    private static string Message(JsonElement json) => json.GetProperty("error").GetProperty("message").GetString()!;

    private bool Ran(string marker) => _scripts.Any(s => s.Contains(marker, StringComparison.Ordinal));

    // ---- 5.11: image list ----

    [Fact]
    public void The_list_shows_each_image_with_its_source_size_build_time_and_branch_count()
    {
        AddImage("shop", "Shop", 100L << 20, new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero));
        AddImage("crm", "Crm", 8L << 20, new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
        AddBranch("pr-142", "shop");
        AddBranch("pr-143", "shop");
        AddBranch("other", "elsewhere");
        _removing.Add(".removing-old");

        var text = _world.Run("image", "list");
        var json = Json("image", "list");

        Assert.Equal(0, text.Exit);
        var lines = text.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("NAME", lines[0], StringComparison.Ordinal);
        Assert.Contains("SOURCE DATABASE", lines[0], StringComparison.Ordinal);
        Assert.Contains("BRANCHES", lines[0], StringComparison.Ordinal);
        var crm = lines.Single(l => l.StartsWith("crm", StringComparison.Ordinal));
        var shop = lines.Single(l => l.StartsWith("shop", StringComparison.Ordinal));
        Assert.EndsWith("0", crm, StringComparison.Ordinal);
        Assert.EndsWith("2", shop, StringComparison.Ordinal);
        Assert.Contains("Shop", shop, StringComparison.Ordinal);
        Assert.Contains("2026-11-16 10:30 UTC", shop, StringComparison.Ordinal);
        Assert.DoesNotContain("old", text.Out, StringComparison.Ordinal);

        Assert.Equal(0, json.Exit);
        var images = json.Json.GetProperty("images").EnumerateArray().ToList();
        Assert.Equal(["crm", "shop"], images.Select(i => i.GetProperty("name").GetString()!).ToArray());
        var shopJson = images.Single(i => i.GetProperty("name").GetString() == "shop");
        Assert.Equal(2, shopJson.GetProperty("branches").GetInt32());
        Assert.Equal(["pr-142", "pr-143"], shopJson.GetProperty("branchNames").EnumerateArray().Select(b => b.GetString()!).ToArray());
        Assert.Equal(0, images.Single(i => i.GetProperty("name").GetString() == "crm").GetProperty("branches").GetInt32());
        Assert.Equal("Shop", shopJson.GetProperty("sourceDatabase").GetString());
        Assert.Equal((100L << 20) + 524_288, shopJson.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("2026-11-16T10:30:00Z", shopJson.GetProperty("builtAt").GetString());
        Assert.Equal(2, shopJson.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void A_pool_without_images_lists_none_and_exits_0()
    {
        var text = _world.Run("image", "list");
        var json = Json("image", "list");

        Assert.Equal(0, text.Exit);
        Assert.Contains("No images", text.Out, StringComparison.Ordinal);
        Assert.Equal(0, json.Json.GetProperty("images").GetArrayLength());
    }

    [Fact]
    public void A_branch_file_that_cannot_be_read_counts_for_no_image_and_a_damaged_manifest_is_a_warning()
    {
        AddImage("shop", "Shop");
        AddImage("broken", "Broken");
        _rawManifests["broken"] = "{ not json";
        _branches["junk"] = "also not json";

        var run = _world.Run("image", "list");

        Assert.Equal(0, run.Exit);
        Assert.Contains("shop", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("broken", run.Out, StringComparison.Ordinal);
        Assert.Contains("/pool/images/broken", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_engine_cannot_list_and_exits_3()
    {
        _world.Runtime.Containers["sqlbranch-engine"] = _world.Runtime.Containers["sqlbranch-engine"] with { Running = false };

        var json = Json("image", "list");

        Assert.Equal(3, json.Exit);
        Assert.Equal("engine_stopped", Code(json.Json));
    }

    // ---- 5.12: image rm ----

    [Fact]
    public void An_unused_image_is_renamed_before_it_is_deleted_and_then_not_listed()
    {
        AddImage("crm", "Crm");
        AddImage("shop", "Shop");

        var json = Json("image", "rm", "crm");

        Assert.Equal(0, json.Exit);
        Assert.Equal("crm", json.Json.GetProperty("image").GetProperty("name").GetString());
        Assert.True(json.Json.GetProperty("image").GetProperty("removed").GetBoolean());
        var rename = _scripts.FindIndex(s => s.Contains("mv -T '/pool/images/crm' '/pool/images/.removing-crm'", StringComparison.Ordinal));
        var delete = _scripts.FindIndex(s => s == "rm -rf '/pool/images/.removing-crm'");
        Assert.True(rename >= 0 && delete > rename, "The rename must come first, then the delete.");
        Assert.Equal(["sqlbranch:image:crm"], _world.Connector.TakenLocks.ToArray());
        Assert.Empty(_removing);

        var list = Json("image", "list");
        Assert.Equal(["shop"], list.Json.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(0, _world.Run("status").Exit);
    }

    [Fact]
    public void An_unknown_image_exits_1_with_image_not_found_and_changes_nothing()
    {
        AddImage("shop", "Shop");

        var json = Json("image", "rm", "nope");

        Assert.Equal(1, json.Exit);
        Assert.Equal("image_not_found", Code(json.Json));
        Assert.Contains("nope", Message(json.Json), StringComparison.Ordinal);
        Assert.False(Ran("mv -T"));
        Assert.False(Ran("rm -rf"));
    }

    [Theory]
    [InlineData("../branches/pr-142")]
    [InlineData("Shop DB")]
    [InlineData("sqlbranch_x")]
    public void A_name_that_breaks_the_rules_exits_2_before_any_script_or_statement(string name)
    {
        AddImage("shop", "Shop");

        var json = Json("image", "rm", name);

        Assert.Equal(2, json.Exit);
        Assert.Equal("invalid_name", Code(json.Json));
        Assert.Empty(_scripts);
        Assert.Empty(_world.Connector.Statements);
        Assert.Empty(_world.Connector.Attempts);
    }

    [Fact]
    public void A_removal_that_is_already_half_done_for_the_name_is_not_overwritten()
    {
        AddImage("crm", "Crm");
        _removing.Add(".removing-crm");

        var json = Json("image", "rm", "crm");

        Assert.Equal(1, json.Exit);
        Assert.Contains("/pool/images/.removing-crm", Message(json.Json), StringComparison.Ordinal);
        Assert.True(_images.ContainsKey("crm"));
        Assert.False(Ran("rm -rf"));
    }

    [Fact]
    public void A_build_or_removal_of_the_same_name_that_is_running_makes_rm_exit_1_without_touching_files()
    {
        AddImage("crm", "Crm");
        _world.Connector.HeldLocks.Add("sqlbranch:image:crm");

        var json = Json("image", "rm", "crm");

        Assert.Equal(1, json.Exit);
        Assert.False(Ran("mv -T"));
        Assert.True(_images.ContainsKey("crm"));
    }

    [Fact]
    public void A_failed_delete_exits_1_names_the_leftover_and_status_then_reports_it()
    {
        AddImage("crm", "Crm");
        AddImage("shop", "Shop");
        _failDelete = true;

        var json = Json("image", "rm", "crm");

        Assert.Equal(1, json.Exit);
        Assert.Equal("operation_failed", Code(json.Json));
        Assert.Contains("/pool/images/.removing-crm", Message(json.Json), StringComparison.Ordinal);

        // The image is no longer listed, and a new branch could not be made from it.
        var list = Json("image", "list");
        Assert.Equal(["shop"], list.Json.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        var again = Json("image", "rm", "crm");
        Assert.Equal(1, again.Exit);
        Assert.Equal("image_not_found", Code(again.Json));

        // status exits 1 and lists the leftover path.
        var status = Json("status");
        Assert.Equal(1, status.Exit);
        var mismatch = status.Json.GetProperty("mismatches").EnumerateArray().Single();
        Assert.Equal("image_removal", mismatch.GetProperty("kind").GetString());
        Assert.Contains("/pool/images/.removing-crm", mismatch.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // ---- 5.13: the in-use check and --force ----

    [Fact]
    public void An_image_with_branches_is_refused_without_force_and_the_branches_are_listed()
    {
        AddImage("shop", "Shop");
        AddBranch("pr-142", "shop");
        AddBranch("pr-143", "shop");
        AddBranch("unrelated", "crm");

        var json = Json("image", "rm", "shop");

        Assert.Equal(1, json.Exit);
        Assert.Equal("image_in_use", Code(json.Json));
        var message = Message(json.Json);
        Assert.Contains("pr-142", message, StringComparison.Ordinal);
        Assert.Contains("pr-143", message, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated", message, StringComparison.Ordinal);
        Assert.Contains("--force", message, StringComparison.Ordinal);
        Assert.True(_images.ContainsKey("shop"));
        Assert.False(Ran("mv -T"));
    }

    [Fact]
    public void Force_removes_an_image_in_use_and_never_touches_a_branch()
    {
        AddImage("shop", "Shop");
        AddBranch("pr-142", "shop");

        var json = Json("image", "rm", "shop", "--force");
        var text = _world.Run("image", "list");

        Assert.Equal(0, json.Exit);
        Assert.True(json.Json.GetProperty("image").GetProperty("forced").GetBoolean());
        Assert.Equal(["pr-142"], json.Json.GetProperty("image").GetProperty("branches").EnumerateArray().Select(b => b.GetString()!).ToArray());
        Assert.False(_images.ContainsKey("shop"));
        Assert.True(_branches.ContainsKey("pr-142"));
        // The scripts of the removal name the image folders only; none of them names the branches area.
        Assert.DoesNotContain(_scripts.Where(s => s != ImageCatalog.ListingScript), s => s.Contains("/pool/branches", StringComparison.Ordinal));
        // The branch's database is untouched too.
        Assert.DoesNotContain(_world.Connector.Statements, s => s.Contains("DROP", StringComparison.OrdinalIgnoreCase) || s.Contains("pr-142", StringComparison.Ordinal));
        Assert.Contains("No images", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_on_an_unused_image_is_the_same_as_no_force()
    {
        AddImage("crm", "Crm");

        var json = Json("image", "rm", "crm", "--force");

        Assert.Equal(0, json.Exit);
        Assert.Empty(json.Json.GetProperty("image").GetProperty("branches").EnumerateArray());
    }

    // ---- format 2: the per-name catalog (task 3.3) ----

    [Fact]
    public void On_a_format_2_pool_the_list_reads_each_catalog_and_shows_the_version_of_each_row()
    {
        UseFormat2();
        AddVersion("shop", "Shop");
        AddImage("legacy", "Legacy");

        var text = _world.Run("image", "list");
        var json = Json("image", "list");

        Assert.Equal(0, json.Exit);
        var images = json.Json.GetProperty("images").EnumerateArray().ToList();
        Assert.Equal(["legacy", "shop"], images.Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal("shop@20261201T090000000Z", images.Single(i => i.GetProperty("name").GetString() == "shop").GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, images.Single(i => i.GetProperty("name").GetString() == "legacy").GetProperty("version").ValueKind);
        Assert.Contains("VERSION", text.Out, StringComparison.Ordinal);
        Assert.Contains("shop@20261201T090000000Z", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_format_1_pool_the_list_has_version_null_and_the_0_2_2_columns_even_when_a_catalog_is_there()
    {
        AddImage("shop", "Shop");
        _catalogs["shop"] = Format2Pool.Catalog("shop", Format2Pool.VersionId("shop"));

        var text = _world.Run("image", "list");
        var json = Json("image", "list");

        Assert.Equal(JsonValueKind.Null, json.Json.GetProperty("images")[0].GetProperty("version").ValueKind);
        Assert.DoesNotContain("VERSION", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_whose_catalog_has_no_current_version_does_not_exist_and_an_unreadable_catalog_is_a_warning()
    {
        UseFormat2();
        AddImage("gone", "Gone");
        _catalogs["gone"] = Format2Pool.Catalog("gone", null);
        AddImage("broken", "Broken");
        _catalogs["broken"] = "{not json";
        AddVersion("shop", "Shop");

        var json = Json("image", "list");

        Assert.Equal(["shop"], json.Json.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Contains("/pool/images/broken", json.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_listing_script_never_sends_the_credentials_file_through_stdout()
    {
        Assert.DoesNotContain("base64 -w0 /pool/engine/credentials.json", ImageCatalog.ListingScript, StringComparison.Ordinal);
        _credentialsLine = """{"format":2,"engine":{"user":"sa","password":"SENTINEL-PASSWORD"}}""";
        _world.Host.CredentialsJson = _credentialsLine;

        var listing = Listing();

        Assert.Contains("SQLBRANCH_POOLCRED|2", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_catalog_whose_dir_leaves_the_image_folder_is_an_unreadable_catalog()
    {
        UseFormat2();
        AddImage("shop", "Shop");
        var id = Format2Pool.VersionId("shop");
        _catalogs["shop"] = new ImageCatalogFile("shop", id, [new ImageVersionRef(id, "../..")]).ToJson();
        AddVersion("ok", "Ok");

        var json = Json("image", "list");

        Assert.Equal(["ok"], json.Json.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Contains("/pool/images/shop", json.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pool_format_above_2_makes_the_list_exit_1_with_operation_failed()
    {
        _credentialsLine = """{"format":3,"engine":{"user":"sa","password":"x"}}""";

        var json = Json("image", "list");

        Assert.Equal(1, json.Exit);
        Assert.Equal("operation_failed", Code(json.Json));
        Assert.Contains("format 3", Message(json.Json), StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_on_a_format_2_pool_removes_the_name_and_reports_the_version_ids()
    {
        UseFormat2();
        AddVersion("crm", "Crm");

        var rm = Json("image", "rm", "crm");
        var list = Json("image", "list");

        Assert.Equal(0, rm.Exit);
        Assert.Equal(["crm@20261201T090000000Z"], rm.Json.GetProperty("image").GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToArray());
        Assert.Empty(list.Json.GetProperty("images").EnumerateArray());
    }

    [Fact]
    public void Remove_on_a_format_1_pool_reports_no_versions()
    {
        AddImage("crm", "Crm");

        var rm = Json("image", "rm", "crm");

        Assert.Equal(0, rm.Exit);
        Assert.Equal(0, rm.Json.GetProperty("image").GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public void Remove_of_an_image_an_older_client_wrote_reports_no_versions()
    {
        UseFormat2();
        AddImage("crm", "Crm");

        var rm = Json("image", "rm", "crm");

        Assert.Equal(0, rm.Exit);
        Assert.Equal(0, rm.Json.GetProperty("image").GetProperty("versions").GetArrayLength());
        Assert.Empty(Json("image", "list").Json.GetProperty("images").EnumerateArray());
    }
}
