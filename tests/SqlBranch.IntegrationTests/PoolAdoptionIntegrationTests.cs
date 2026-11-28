using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlBranch.Core.Engine;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 4.7 on the real engine, in whatever clone mode the pool has: items an older client wrote in the format-1 layout after the
/// upgrade are listed, adopted only by commands that write with them, and never adopted when the image is busy. The older client's
/// files are made by removing the catalog of an image or the base fields of a branch record, which is exactly what 0.2.2 leaves.
/// The thin and the copy class below run the same walk on their own engines.
/// </summary>
[Trait("Category", "Integration")]
public abstract class PoolAdoptionScenarios(ImageEngineFixture fixture, ITestOutputHelper output)
{
    private EngineWorld World => fixture.World!;

    private BranchKit Kit => new(fixture, output);

    private JsonElement Json((int Exit, string Out, string Err, TimeSpan Elapsed) run, int exit = 0)
    {
        output.WriteLine(run.Out + run.Err);
        Assert.True(run.Exit == exit, run.Out + run.Err);
        return JsonDocument.Parse(run.Out).RootElement.Clone();
    }

    private string Scripts(string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "001.sql"), "CREATE TABLE dbo.T (id int); INSERT dbo.T VALUES (1);");
        return folder;
    }

    private string? VersionOf(string image) =>
        Json(World.RunCli("image", "list", "--format", "json")).GetProperty("images").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == image).GetProperty("version").GetString();

    private JsonElement Build(string image) =>
        Json(World.RunCli("image", "build", image, "--from-scripts", Scripts(image), "--format", "json"));

    /// <summary>What 0.2.2 leaves after the upgrade: the image files and no catalog. The ID record of the name stays, as in the pool.</summary>
    private async Task LegacyAsync(string image) => await Kit.InPoolAsync($"rm -f /pool/images/{image}/catalog.json");

    [IntegrationFact]
    public async Task Walk_adoption_by_create_and_reset_and_the_refusals()
    {
        output.WriteLine("clone mode: " + await Kit.CloneModeAsync());
        await Format2BuildIntegrationTests.UseFormat2Async(World);

        // "Image written by 0.2.2 after the upgrade": list shows version null and changes nothing, create adopts.
        var first = Build("adcrm").GetProperty("image").GetProperty("version").GetString()!;
        await LegacyAsync("adcrm");
        var hashes = await Kit.ImageHashesAsync("adcrm");
        var stamp = await Kit.InPoolAsync("ls -l --time-style=full-iso /pool/images/adcrm");
        Assert.Null(VersionOf("adcrm"));
        Assert.Equal(hashes, await Kit.ImageHashesAsync("adcrm"));
        Assert.Equal(stamp, await Kit.InPoolAsync("ls -l --time-style=full-iso /pool/images/adcrm"));
        Assert.Equal("", await Kit.InPoolAsync("ls /pool/images/adcrm/catalog.json 2>/dev/null"));

        var created = Json(World.RunCli("branch", "create", "addev6", "--from", "adcrm", "--format", "json")).GetProperty("branch");
        var adopted = created.GetProperty("imageVersion").GetString()!;
        Assert.StartsWith("adcrm@", adopted, StringComparison.Ordinal);
        // The name was used before: its new ID is above the one it had.
        Assert.True(string.CompareOrdinal(adopted, first) > 0, $"{adopted} should be above {first}");
        Assert.Equal(adopted, VersionOf("adcrm"));
        Assert.Equal(1, Json(World.RunCli("image", "list", "--format", "json")).GetProperty("images").EnumerateArray().Count(i => i.GetProperty("name").GetString() == "adcrm"));
        await Kit.DropBranchAsync("addev6");

        // "Name used by an image an older client wrote": the build is refused and the files stay.
        Build("adold");
        await LegacyAsync("adold");
        var oldHashes = await Kit.ImageHashesAsync("adold");
        var refused = World.RunCli("image", "build", "adold", "--from-scripts", Scripts("adold"), "--format", "json");
        Assert.Equal(1, refused.Exit);
        Assert.Equal("image_exists", BranchKit.ErrorCode(refused.Out));
        Assert.Equal(oldHashes, await Kit.ImageHashesAsync("adold"));
        Assert.Equal("", await Kit.InPoolAsync("ls /pool/images/adold/catalog.json 2>/dev/null"));

        // "Remove an image not adopted yet": no version IDs, the name is gone.
        var removed = Json(World.RunCli("image", "rm", "adold", "--format", "json"));
        Assert.Empty(removed.GetProperty("image").GetProperty("versions").EnumerateArray());
        Assert.DoesNotContain(Json(World.RunCli("image", "list", "--format", "json")).GetProperty("images").EnumerateArray(), i => i.GetProperty("name").GetString() == "adold");

        // "Branch written by 0.2.2 after the upgrade": a record without base fields is bound by builtAt at reset.
        Build("adshop");
        var shopVersion = VersionOf("adshop");
        await Kit.CreateAsync("addev8", "adshop");
        var record = JsonNode.Parse(await Kit.InPoolAsync("cat /pool/branches/addev8/branch.json"))!.AsObject();
        record.Remove("imageVersion");
        record.Remove("baseKnown");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(record.ToJsonString()));
        await Kit.InPoolAsync($"printf %s '{encoded}' | base64 -d > /pool/branches/addev8/branch.json");
        var reset = Json(World.RunCli("branch", "reset", "addev8", "--format", "json")).GetProperty("branch");
        Assert.Equal(shopVersion, reset.GetProperty("imageVersion").GetString());
        var after = JsonNode.Parse(await Kit.InPoolAsync("cat /pool/branches/addev8/branch.json"))!.AsObject();
        Assert.Equal(shopVersion, after["imageVersion"]!.GetValue<string>());
        Assert.True(after["baseKnown"]!.GetValue<bool>());
        await Kit.DropBranchAsync("addev8");

        // "Image busy during adoption": another session holds the image, the create does not wait and changes nothing.
        Build("adbusy");
        await LegacyAsync("adbusy");
        await using (var holder = await new SqlClientEngineConnector().ConnectAsync(World.Endpoint, World.Password, CancellationToken.None))
        {
            Assert.True(await EngineLocks.TryTakeAsync(holder, EngineLocks.Image("adbusy"), CancellationToken.None));
            var busy = World.RunCli("branch", "create", "addev7", "--from", "adbusy", "--format", "json");
            Assert.Equal(1, busy.Exit);
            Assert.Equal("catalog_busy", BranchKit.ErrorCode(busy.Out));
            Assert.Contains("adbusy", BranchKit.ErrorMessage(busy.Out), StringComparison.Ordinal);
        }

        Assert.Equal("", await Kit.InPoolAsync("ls -d /pool/branches/addev7 2>/dev/null"));
        Assert.Equal("", await Kit.InPoolAsync("ls /pool/images/adbusy/catalog.json 2>/dev/null"));
        Assert.Null(VersionOf("adbusy"));
    }
}

/// <summary>The walk on the default managed pool (clone mode thin).</summary>
[Trait("Category", "Integration")]
public sealed class PoolAdoptionThinIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output)
    : PoolAdoptionScenarios(fixture, output), IClassFixture<ImageEngineFixture>
{
}

/// <summary>The walk on a pool that cannot share blocks (clone mode copy).</summary>
[Trait("Category", "Integration")]
public sealed class PoolAdoptionCopyIntegrationTests(CopyEngineFixture fixture, ITestOutputHelper output)
    : PoolAdoptionScenarios(fixture, output), IClassFixture<CopyEngineFixture>
{
}
