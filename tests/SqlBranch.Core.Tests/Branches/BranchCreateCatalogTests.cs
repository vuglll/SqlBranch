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
}
