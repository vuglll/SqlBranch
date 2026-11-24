using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

/// <summary>Task 3.5: an import publishes its first version through the catalog on a format-2 pool, after sealing.</summary>
public sealed partial class ImageImportTests
{
    private sealed class SetClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task On_a_format_2_pool_an_import_publishes_a_local_version_id_after_the_manifest()
    {
        Format2Pool.Apply(_world);
        _world.Clock = new SetClock(new DateTimeOffset(2026, 12, 1, 2, 0, 0, TimeSpan.Zero));

        var result = await ImportAsync(await ValidAsync());

        Assert.Equal("shop2@20261201T020000000Z", result.Version);
        var manifest = _timeline.FindIndex(l => l.Contains("base64 -d > '/pool/images/shop2/manifest.json'", StringComparison.Ordinal));
        var id = _timeline.FindIndex(l => l.Contains("/pool/images/.ids/shop2.new", StringComparison.Ordinal));
        var catalog = _timeline.FindIndex(l => l.Contains("catalog.json.new", StringComparison.Ordinal));
        var cleanup = _timeline.FindIndex(l => l.Contains("rm -rf '/pool/build/abc123def456'", StringComparison.Ordinal));
        Assert.True(manifest >= 0 && manifest < id && id < catalog && catalog < cleanup);
        // The archive's manifest is not changed by the version: the name is the only difference.
        Assert.Equal(_manifest.ToJson().Replace("\"shop\",", "\"shop2\",", StringComparison.Ordinal), result.Manifest.ToJson());
    }

    [Fact]
    public async Task On_a_format_1_pool_an_import_has_no_version_and_writes_no_catalog()
    {
        var result = await ImportAsync(await ValidAsync());

        Assert.Null(result.Version);
        Assert.False(Ran(".ids"));
        Assert.False(Ran("catalog.json"));
    }

    [Fact]
    public async Task A_failed_catalog_write_fails_the_import_and_removes_the_image_folder_it_made()
    {
        Format2Pool.Apply(_world);
        _failScript = s => s.Contains("catalog.json.new", StringComparison.Ordinal) && s.Contains("base64", StringComparison.Ordinal);

        await FailsAsync(await ValidAsync());

        Assert.True(Ran("rm -rf '/pool/images/shop2'"));
    }
}
