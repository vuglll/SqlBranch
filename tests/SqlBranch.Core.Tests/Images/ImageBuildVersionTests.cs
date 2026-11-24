using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

/// <summary>Task 3.5: a build publishes its first version through the catalog on a format-2 pool, after sealing.</summary>
public sealed partial class ImageBuildTests
{
    private sealed class SetClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Noon = new(2026, 12, 1, 2, 0, 0, TimeSpan.Zero);

    [GeneratedRegex(@"printf %s '(?<b64>[A-Za-z0-9+/=]+)' \| base64 -d > '[^']*catalog.json.new'")]
    private static partial Regex CatalogWrite();

    private string PublishedCatalog()
    {
        var script = _timeline.Single(l => CatalogWrite().IsMatch(l));
        return Encoding.UTF8.GetString(Convert.FromBase64String(CatalogWrite().Match(script).Groups["b64"].Value));
    }

    [Fact]
    public void On_a_format_2_pool_the_build_publishes_version_one_after_the_manifest_and_reports_it()
    {
        Format2Pool.Apply(_world);
        _world.Clock = new SetClock(Noon);

        var run = Build();

        Assert.Equal(0, run.Exit);
        Assert.Equal("shop@20261201T020000000Z", run.Json.GetProperty("image").GetProperty("version").GetString());
        Assert.True(IndexOf("base64 -d > '/pool/images/shop/manifest.json'") < IndexOf("/pool/images/.ids/shop.new"));
        Assert.True(IndexOf("/pool/images/.ids/shop.new") < IndexOf("catalog.json.new"));
        Assert.Contains("mv -f '/pool/images/.ids/shop.new' '/pool/images/.ids/shop'", _timeline.Single(l => l.Contains(".ids/shop.new", StringComparison.Ordinal)), StringComparison.Ordinal);
        var catalog = JsonDocument.Parse(PublishedCatalog()).RootElement;
        Assert.Equal("shop@20261201T020000000Z", catalog.GetProperty("current").GetString());
        Assert.Equal(".", catalog.GetProperty("versions")[0].GetProperty("dir").GetString());
        Assert.Contains("mv -f '/pool/images/shop/catalog.json.new' '/pool/images/shop/catalog.json'", _timeline.Single(l => CatalogWrite().IsMatch(l)), StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_format_1_pool_the_build_has_a_null_version_and_writes_no_id_file_or_catalog()
    {
        _world.Clock = new SetClock(Noon);

        var run = Build();

        Assert.Equal(0, run.Exit);
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("image").GetProperty("version").ValueKind);
        Assert.DoesNotContain(_timeline, l => l.Contains(".ids", StringComparison.Ordinal) || l.Contains("catalog.json", StringComparison.Ordinal));
    }

    [Fact]
    public void A_name_removed_and_rebuilt_in_one_millisecond_gets_the_next_millisecond()
    {
        Format2Pool.Apply(_world);
        _world.Clock = new SetClock(Noon);
        _lastId = "shop@20261201T020000000Z";

        var run = Build();

        Assert.Equal(0, run.Exit);
        Assert.Equal("shop@20261201T020000001Z", run.Json.GetProperty("image").GetProperty("version").GetString());
    }

    [Fact]
    public void A_clock_moved_back_still_gives_an_increasing_id()
    {
        Format2Pool.Apply(_world);
        _world.Clock = new SetClock(new DateTimeOffset(2026, 11, 30, 2, 0, 0, TimeSpan.Zero));
        _lastId = "shop@20261201T020000000Z";

        var run = Build();

        Assert.Equal("shop@20261201T020000001Z", run.Json.GetProperty("image").GetProperty("version").GetString());
    }

    [Fact]
    public void A_failed_catalog_write_adds_no_version_and_the_image_folder_goes()
    {
        Format2Pool.Apply(_world);
        _failScript = s => s.Contains("catalog.json.new", StringComparison.Ordinal) && s.Contains("base64", StringComparison.Ordinal);

        var run = Build();

        Assert.Equal(1, run.Exit);
        Assert.True(Ran("rm -rf '/pool/images/shop'"));
        Assert.Equal("operation_failed", ErrorCode(run.Json));
    }
    [Fact]
    public void Build_of_a_name_with_an_unreadable_catalog_fails_without_writing()
    {
        Format2Pool.Apply(_world);
        _catalogFile = Convert.ToBase64String(Encoding.UTF8.GetBytes("{not json"));
        var run = Build();
        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", ErrorCode(run.Json));
        Assert.Contains("sqlbranch image rm shop", run.Json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Copies);
        Assert.Empty(RemovedFolders());
        Assert.False(Ran("mkdir"));
    }

    [Fact]
    public void Removing_an_unreadable_catalog_then_rebuilding_in_the_same_millisecond_keeps_the_high_water_mark()
    {
        Format2Pool.Apply(_world);
        _world.Clock = new SetClock(Noon);
        _lastId = "shop@20261201T020000000Z";
        _catalogFile = Convert.ToBase64String(Encoding.UTF8.GetBytes("{not json"));
        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : "";
            if (script == SqlBranch.Core.Images.ImageCatalog.ListingScript)
            {
                return FakeContainerRuntime.Ok(_catalogFile == "none" ? "SQLBRANCH_POOLCRED|2"
                    : $"SQLBRANCH_POOLCRED|2\nSQLBRANCH_CATALOG|shop|{_catalogFile}");
            }
            if (script.Contains("mv -T '/pool/images/shop'", StringComparison.Ordinal))
            {
                _catalogFile = "none";
                return FakeContainerRuntime.Ok("SQLBRANCH_RENAMED=yes");
            }
            Assert.False(script.Contains("rm ", StringComparison.Ordinal) && script.Contains("/pool/images/.ids", StringComparison.Ordinal));
            return inner(container, command);
        };

        var removed = _world.Run("image", "rm", "shop", "--format", "json");
        Assert.Equal(0, removed.Exit);
        Assert.Empty(JsonDocument.Parse(removed.Out).RootElement.GetProperty("image").GetProperty("versions").EnumerateArray());
        Assert.Empty(JsonDocument.Parse(_world.Run("image", "list", "--format", "json").Out).RootElement.GetProperty("images").EnumerateArray());
        Assert.Equal("shop@20261201T020000000Z", _lastId);
        var rebuilt = Build();
        Assert.Equal(0, rebuilt.Exit);
        Assert.Equal("shop@20261201T020000001Z", rebuilt.Json.GetProperty("image").GetProperty("version").GetString());
    }

}
