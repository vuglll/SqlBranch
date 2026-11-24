using System.Text;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

/// <summary>The image lookup of <c>branch create</c> on a format-2 pool (task 3.3).</summary>
public sealed partial class BranchCreateTests
{
    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void On_a_format_2_pool_create_reads_the_catalog_and_the_manifest_of_the_current_version()
    {
        Format2Pool.Apply(_world);
        _catalogFile = Base64(Format2Pool.Catalog("shop", Format2Pool.VersionId("shop")));

        var run = Create();

        Assert.Equal(0, run.Exit);
        Assert.True(Ran("catalog.json"));
        Assert.True(Ran("/pool/images/shop/./manifest.json"));
    }

    [Fact]
    public void On_a_format_2_pool_a_name_with_no_current_version_is_image_not_found_and_nothing_is_made()
    {
        Format2Pool.Apply(_world);
        _catalogFile = Base64(Format2Pool.Catalog("shop", null));

        var run = Create();

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_not_found", ErrorCode(run.Json));
        Assert.False(Ran("mkdir '/pool/branches"));
    }

    [Fact]
    public void On_a_format_2_pool_an_image_an_older_client_wrote_has_no_catalog_and_is_found_by_its_manifest()
    {
        Format2Pool.Apply(_world);

        var run = Create();

        Assert.Equal(0, run.Exit);
    }

    [Fact]
    public void On_a_format_1_pool_create_never_reads_a_catalog()
    {
        _catalogFile = Base64(Format2Pool.Catalog("shop", null));

        var run = Create();

        Assert.Equal(0, run.Exit);
        Assert.False(Ran("catalog.json"));
    }

    [Fact]
    public void On_a_format_2_pool_create_records_the_current_version_as_the_base_and_reports_it()
    {
        Format2Pool.Apply(_world);
        var version = Format2Pool.VersionId("shop");
        _catalogFile = Base64(Format2Pool.Catalog("shop", version));

        var run = Create("dev2", "--from", "shop");

        Assert.Equal(0, run.Exit);
        Assert.Equal(version, run.Json.GetProperty("branch").GetProperty("imageVersion").GetString());
        var record = WrittenRecord();
        Assert.Equal(version, record.ImageVersion);
        Assert.True(record.BaseKnown);
        var script = _timeline.Single(l => l.Contains("base64 -d", StringComparison.Ordinal));
        Assert.Contains("> '/pool/branches/dev2/branch.json.new'", script, StringComparison.Ordinal);
        Assert.Contains("mv -f '/pool/branches/dev2/branch.json.new' '/pool/branches/dev2/branch.json'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_format_2_pool_an_image_an_older_client_wrote_gives_a_record_without_base_fields()
    {
        Format2Pool.Apply(_world);

        var run = Create();

        Assert.Equal(0, run.Exit);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, run.Json.GetProperty("branch").GetProperty("imageVersion").ValueKind);
        Assert.DoesNotContain("baseKnown", _timeline.Single(l => l.Contains("base64 -d", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Null(WrittenRecord().BaseKnown);
    }

    [Fact]
    public void On_a_format_1_pool_create_reports_a_null_version_and_writes_no_base_fields()
    {
        var run = Create("dev3", "--from", "shop");

        Assert.Equal(0, run.Exit);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, run.Json.GetProperty("branch").GetProperty("imageVersion").ValueKind);
        var record = WrittenRecord();
        Assert.Null(record.ImageVersion);
        Assert.Null(record.BaseKnown);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(RecordScript().Match(_timeline.Single(l => l.Contains("base64 -d", StringComparison.Ordinal))).Groups["b64"].Value));
        Assert.DoesNotContain("imageVersion", json, StringComparison.Ordinal);
    }
    [Fact]
    public void Use_an_image_with_an_unreadable_catalog_refuses_create_without_writing()
    {
        Format2Pool.Apply(_world);
        _catalogFile = Base64("{not json");
        var run = Create();
        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", ErrorCode(run.Json));
        Assert.Contains("sqlbranch image rm shop", run.Json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_timeline, l => l.Contains("mkdir", StringComparison.Ordinal) || l.Contains("base64 -d", StringComparison.Ordinal) || l.Contains("DROP DATABASE", StringComparison.Ordinal));
        Assert.Empty(_world.Runtime.Copies);
    }

}
