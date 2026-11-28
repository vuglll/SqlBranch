using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Engine;
using SqlBranch.Testing;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// The test-fixtures scenarios of task 3.6 on a pool whose credentials file was rewritten to format 2 by the test
/// (<c>pool upgrade</c> does not exist yet). They hold in both clone modes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FixtureFormat2IntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>, IAsyncLifetime
{
    private EngineWorld World => fixture.World!;

    private BranchKit Kit => new(fixture, output);

    public Task InitializeAsync() => Format2BuildIntegrationTests.UseFormat2Async(World);

    public Task DisposeAsync() => Task.CompletedTask;

    private FixtureHost Host() => new(World.Runtime, new SqlClientEngineConnector(), World.Redactor,
        new SettingsSources { Environment = World.Environment, WorkingDirectory = fixture.ClientFolder, HomeDirectory = World.HomeDirectory },
        World.Paths, World.EngineName, World.PoolVolume, null);

    private static Task<SqlBranchDatabase> Create(string image, FixtureHost host, FixtureProcess process) =>
        SqlBranchDatabase.CreateAsync(image, new(), host, process, () => Guid.NewGuid().ToString("N")[..8]);

    private string VersionOf(string image)
    {
        var run = World.RunCli("image", "list", "--format", "json");
        Assert.Equal(0, run.Exit);
        return BranchKit.Parse(run.Out).GetProperty("images").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == image).GetProperty("version").GetString()!;
    }

    private JsonElement Row(string branch) =>
        BranchKit.Parse(World.RunCli("branch", "list", "--format", "json").Out).GetProperty("branches").EnumerateArray()
            .Single(b => b.GetProperty("name").GetString() == branch);

    private static async Task<long> CountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await BranchKit.CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item");
    }

    private static async Task DeleteSomeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await BranchKit.ExecuteAsync(connection, "DELETE TOP (10) dbo.Item");
    }

    [IntegrationFact]
    public async Task Fixture_reset_on_a_format_2_pool_restores_the_rows_of_the_recorded_version()
    {
        await Kit.EnsureImageAsync("fxf2reset");
        var version = VersionOf("fxf2reset");
        using var process = new FixtureProcess();
        await using var db = await Create("fxf2reset", Host(), process);

        Assert.Equal(100, await CountAsync(db.ConnectionString));
        await DeleteSomeAsync(db.ConnectionString);
        Assert.Equal(90, await CountAsync(db.ConnectionString));

        await db.ResetAsync();

        Assert.Equal(100, await CountAsync(db.ConnectionString));
        Assert.Equal(version, Row(db.Name).GetProperty("imageVersion").GetString());
    }

    [IntegrationFact]
    public async Task Fixture_reset_after_the_image_was_removed_throws_and_keeps_the_rows()
    {
        await Kit.EnsureImageAsync("fxf2gone");
        using var process = new FixtureProcess();
        await using var db = await Create("fxf2gone", Host(), process);
        await DeleteSomeAsync(db.ConnectionString);

        var removed = World.RunCli("image", "rm", "fxf2gone", "--force", "--format", "json");
        Assert.True(removed.Exit == 0, removed.Out + removed.Err);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.ResetAsync());

        AssertMessage(error.Message, db.Name, "fxf2gone");
        Assert.Equal(90, await CountAsync(db.ConnectionString));
    }

    [IntegrationFact]
    public async Task Fixture_reset_of_a_branch_with_an_unknown_base_throws_and_keeps_the_rows()
    {
        await Kit.EnsureImageAsync("fxf2unknown");
        using var process = new FixtureProcess();
        await using var db = await Create("fxf2unknown", Host(), process);
        await DeleteSomeAsync(db.ConnectionString);

        // What `pool upgrade` leaves for a branch created on a format-1 pool: no base version, base not known.
        var path = $"/pool/branches/{db.Name}/branch.json";
        var record = JsonNode.Parse(await Kit.InPoolAsync($"cat {path}"))!.AsObject();
        record["imageVersion"] = null;
        record["baseKnown"] = false;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(record.ToJsonString()));
        await Kit.InPoolAsync($"echo {encoded} | base64 -d > {path}.new && mv -f {path}.new {path}");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.ResetAsync());

        AssertMessage(error.Message, db.Name, "fxf2unknown");
        Assert.Equal(90, await CountAsync(db.ConnectionString));
    }

    private void AssertMessage(string message, string branch, string image)
    {
        Assert.Contains(branch, message, StringComparison.Ordinal);
        Assert.Contains($"'{image}'", message, StringComparison.Ordinal);
        Assert.Contains("no longer exists", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch branch create", message, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlbranch image build", message, StringComparison.Ordinal);
        Assert.DoesNotContain(World.Password, message, StringComparison.Ordinal);
    }
}
