using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Engine;
using SqlBranch.Testing;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class TestingLibraryIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    private EngineWorld World => fixture.World!;

    private FixtureHost Host(TimeProvider? clock = null) => new(World.Runtime, new SqlClientEngineConnector(), World.Redactor,
        new SettingsSources { Environment = World.Environment, WorkingDirectory = fixture.ClientFolder, HomeDirectory = World.HomeDirectory },
        World.Paths, World.EngineName, World.PoolVolume, clock);

    private static Task<SqlBranchDatabase> Create(FixtureHost host, FixtureProcess process, SqlBranchOptions? options = null) =>
        SqlBranchDatabase.CreateAsync("shop", options ?? new(), host, process, () => Guid.NewGuid().ToString("N")[..8]);

    [IntegrationFact]
    public async Task Query_reset_and_dispose_with_an_open_pooled_connection()
    {
        var kit = new BranchKit(fixture, output);
        await kit.EnsureImageAsync("shop");
        using var process = new FixtureProcess();
        var host = Host();
        Assert.True(await host.IsAvailableAsync(CancellationToken.None));
        var db = await Create(host, process, new() { Labels = new Dictionary<string, string> { ["suite"] = "orders" } });
        await using var lifetime = db;
        Assert.Matches("^t-[0-9a-f]{8}$", db.Name);
        Assert.Equal(World.IsPlain ? "copy" : "thin", db.CloneMode);
        var cs = db.ConnectionString;
        var expiry = db.ExpiresAt;
        await using (var connection = new SqlConnection(cs))
        {
            await connection.OpenAsync();
            Assert.Equal(100, await BranchKit.CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
            await BranchKit.ExecuteAsync(connection, "DELETE dbo.Item");
        }
        await db.ResetAsync();
        Assert.Equal(cs, db.ConnectionString);
        var listed = List().Single(b => b.GetProperty("name").GetString() == db.Name);
        Assert.Equal(expiry, listed.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal("orders", listed.GetProperty("labels").GetProperty("suite").GetString());
        Assert.Equal("testing", listed.GetProperty("labels").GetProperty("sqlbranch.owner").GetString());
        await using var open = new SqlConnection(db.ConnectionString);
        await open.OpenAsync();
        Assert.Equal(100, await BranchKit.CountAsync(open, "SELECT COUNT(*) FROM dbo.Item"));
        await db.DisposeAsync();
        Assert.DoesNotContain(List(), b => b.GetProperty("name").GetString() == db.Name);
        var dir = await World.Runtime.ExecAsync(World.EngineName, ["test", "!", "-e", "/pool/branches/" + db.Name], CancellationToken.None);
        Assert.Equal(0, dir.ExitCode);
        kit.AssertStatusIsClean();
    }

    private System.Text.Json.JsonElement.ArrayEnumerator List()
    {
        var run = World.RunCli("branch", "list", "--format", "json");
        Assert.True(run.Exit == 0, run.Err);
        return BranchKit.Parse(run.Out).GetProperty("branches").EnumerateArray();
    }

    private sealed class PastClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddHours(-2);
    }

    [IntegrationFact]
    public async Task New_process_prunes_only_expired_fixture_branches()
    {
        var kit = new BranchKit(fixture, output);
        await kit.EnsureImageAsync("shop");
        using var oldProcess = new FixtureProcess();
        var past = new PastClock();
        var cli = World.RunCliAt(past, "branch", "create", "tmp-cli", "--from", "shop", "--ephemeral", "--ttl", "1h");
        Assert.True(cli.Exit == 0, cli.Err);
        await using var expired = await Create(Host(past), oldProcess);
        await using var live = await Create(Host(), oldProcess);
        using var client = World.StartCli("fixture-create", "shop");
        var fresh = await client.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(fresh.Exit == 0, fresh.Err);
        Assert.Matches("^t-[0-9a-f]{8}$", fresh.Out.Trim());
        var names = List().Select(b => b.GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain(expired.Name, names);
        Assert.Contains("tmp-cli", names);
        Assert.Contains(live.Name, names);
        await using var connection = new SqlConnection(live.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(100, await BranchKit.CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
        await kit.DropBranchAsync("tmp-cli");
    }

    [IntegrationFact]
    public async Task Twenty_parallel_creates_return_distinct_queryable_branches()
    {
        var kit = new BranchKit(fixture, output);
        await kit.EnsureImageAsync("shop");
        using var process = new FixtureProcess();
        var host = Host();
        var clock = Stopwatch.StartNew();
        var creates = Enumerable.Range(0, 20).Select(_ => Create(host, process)).ToArray();
        try
        {
            var branches = await Task.WhenAll(creates);
            var elapsed = clock.Elapsed;
            Assert.Equal(20, branches.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count());
            await Task.WhenAll(branches.Select(async db =>
            {
                await using var connection = new SqlConnection(db.ConnectionString);
                await connection.OpenAsync();
                Assert.Equal(100, await BranchKit.CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
            }));
            output.WriteLine($"20 live {branches[0].CloneMode} branches. Create: {elapsed.TotalSeconds:F1}s. Create and query: {clock.Elapsed.TotalSeconds:F1}s.");
        }
        finally
        {
            foreach (var create in creates.Where(t => t.IsCompletedSuccessfully))
            {
                await (await create).DisposeAsync();
            }
        }
        kit.AssertStatusIsClean();
    }
}
