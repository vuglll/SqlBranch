using System.Text.Json;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>Task 4.8. Run on the default thin pool and with SQLBRANCH_IT_POOL_BACKEND=plain for copy mode.</summary>
[Trait("Category", "Integration")]
public sealed class UnreadableCatalogIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    private EngineWorld World => fixture.World!;

    private JsonElement Json(params string[] args)
    {
        var run = World.RunCli([.. args, "--format", "json"]);
        output.WriteLine(run.Out + run.Err);
        Assert.Equal(0, run.Exit);
        return JsonDocument.Parse(run.Out).RootElement.Clone();
    }

    [IntegrationFact]
    public async Task List_status_and_rm_of_an_image_with_a_corrupted_catalog()
    {
        await Format2BuildIntegrationTests.UseFormat2Async(World);
        var scripts = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "corrupt-catalog")).FullName;
        File.WriteAllText(Path.Combine(scripts, "001.sql"), "CREATE TABLE dbo.T (id int);");
        Json("image", "build", "crm", "--from-scripts", scripts);
        var mark = await World.Runtime.ExecAsync(World.EngineName, ["cat", "/pool/images/.ids/crm"], CancellationToken.None);
        Assert.True(mark.Succeeded);
        var corrupt = await World.Runtime.ExecAsync(World.EngineName,
            ["sh", "-c", "printf '{not json' > /pool/images/crm/catalog.json"], CancellationToken.None);
        Assert.True(corrupt.Succeeded);

        var row = Assert.Single(Json("image", "list").GetProperty("images").EnumerateArray());
        Assert.Equal("crm", row.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("version").ValueKind);
        Assert.False(row.GetProperty("catalogReadable").GetBoolean());
        var text = World.RunCli("image", "list");
        Assert.Equal(0, text.Exit);
        Assert.Contains("catalog unreadable", text.Out, StringComparison.Ordinal);
        var status = World.RunCli("status", "--format", "json");
        Assert.Equal(1, status.Exit);
        Assert.Contains("sqlbranch image rm crm", status.Out, StringComparison.Ordinal);

        Assert.Empty(Json("image", "rm", "crm").GetProperty("image").GetProperty("versions").EnumerateArray());
        Assert.Empty(Json("image", "list").GetProperty("images").EnumerateArray());
        var kept = await World.Runtime.ExecAsync(World.EngineName, ["cat", "/pool/images/.ids/crm"], CancellationToken.None);
        Assert.True(kept.Succeeded);
        Assert.Equal(mark.StdOut, kept.StdOut);
        Assert.Equal(0, World.RunCli("status").Exit);
        var rebuilt = Json("image", "build", "crm", "--from-scripts", scripts).GetProperty("image").GetProperty("version").GetString();
        Assert.True(string.CompareOrdinal(rebuilt, mark.StdOut.Trim()) > 0);
    }
}
