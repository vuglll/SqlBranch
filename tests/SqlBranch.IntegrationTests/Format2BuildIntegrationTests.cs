using System.Text.Json;
using SqlBranch.Core.Engine;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 3.5 and 3.6 on a pool whose credentials file was rewritten to format 2 by the test (<c>pool upgrade</c> does not
/// exist yet): version IDs from builds and imports, and the branch base. Clone mode is whatever the pool has; the checks hold in
/// both modes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Format2BuildIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    private EngineWorld World => fixture.World!;

    private sealed class SetClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset First = new(2026, 12, 2, 2, 0, 0, TimeSpan.Zero);

    private async Task UseFormat2Async()
    {
        var json = CredentialsFile.Format2(World.Password, null).ToJson();
        var runner = new SqlBranch.Core.Containers.SystemProcessRunner();
        var result = await runner.RunAsync("docker",
            ["exec", "-i", "-u", "0", World.EngineName, "sh", "-c",
                "cat > /pool/engine/credentials.json.new && chown 10001:0 /pool/engine/credentials.json.new && chmod 600 /pool/engine/credentials.json.new && mv -f /pool/engine/credentials.json.new /pool/engine/credentials.json"],
            null, CancellationToken.None, json);
        Assert.True(result.ExitCode == 0, "could not rewrite the credentials file of the test engine");
    }

    private string Scripts(string name, params (string File, string Sql)[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, name)).FullName;
        foreach (var (file, sql) in files)
        {
            File.WriteAllText(Path.Combine(folder, file), sql);
        }

        return folder;
    }

    private JsonElement Json((int Exit, string Out, string Err, TimeSpan Elapsed) run, int exit = 0)
    {
        output.WriteLine(run.Out + run.Err);
        Assert.True(run.Exit == exit, run.Out + run.Err);
        return JsonDocument.Parse(run.Out).RootElement.Clone();
    }

    [IntegrationFact]
    public async Task Script_build_on_a_format_2_pool_publishes_a_version_and_branches_record_it_and_reset_keeps_it()
    {
        await UseFormat2Async();
        var folder = Scripts("f2-shop", ("001.sql", "CREATE TABLE dbo.T (id int); INSERT dbo.T VALUES (1);"));

        var built = Json(World.RunCliAt(new SetClock(First), "image", "build", "shopdev", "--from-scripts", folder, "--format", "json"));
        Assert.Equal("shopdev@20261202T020000000Z", built.GetProperty("image").GetProperty("version").GetString());

        var listed = Json(World.RunCli("image", "list", "--format", "json"));
        Assert.Equal("shopdev@20261202T020000000Z", listed.GetProperty("images").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "shopdev").GetProperty("version").GetString());

        var created = Json(World.RunCli("branch", "create", "d1", "--from", "shopdev", "--format", "json"));
        Assert.Equal("shopdev@20261202T020000000Z", created.GetProperty("branch").GetProperty("imageVersion").GetString());

        var reset = Json(World.RunCli("branch", "reset", "d1", "--format", "json"));
        Assert.Equal("shopdev@20261202T020000000Z", reset.GetProperty("branch").GetProperty("imageVersion").GetString());

        var row = Json(World.RunCli("branch", "list", "--format", "json")).GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("name").GetString() == "d1");
        Assert.Equal("shopdev@20261202T020000000Z", row.GetProperty("imageVersion").GetString());
        Assert.False(row.GetProperty("imageRemoved").GetBoolean());

        // Export and import of the version give the importing pool a local version ID.
        var archive = Path.Combine(fixture.ClientFolder, "shopdev.sbimg");
        Json(World.RunCli("image", "export", "shopdev", "--to", archive, "--format", "json"));
        var imported = Json(World.RunCliAt(new SetClock(First.AddHours(1)), "image", "import", archive, "--as", "shop3", "--format", "json"));
        Assert.Equal("shop3@20261202T030000000Z", imported.GetProperty("image").GetProperty("version").GetString());
        Json(World.RunCli("image", "import", archive, "--as", "shop3", "--format", "json"), exit: 1);

        var status = World.RunCli("status", "--format", "json");
        Assert.Equal(0, status.Exit);
        Json(World.RunCli("branch", "rm", "d1", "--format", "json"));
    }

    [IntegrationFact]
    public async Task A_removed_and_rebuilt_name_leaves_the_branch_on_its_old_version_and_reset_refuses_without_changing_it()
    {
        await UseFormat2Async();
        var folder = Scripts("f2-crm", ("001.sql", "CREATE TABLE dbo.T (id int); INSERT dbo.T VALUES (1);"));
        var old = Json(World.RunCliAt(new SetClock(First), "image", "build", "crm", "--from-scripts", folder, "--format", "json")).GetProperty("image").GetProperty("version").GetString()!;
        Json(World.RunCli("branch", "create", "dev1", "--from", "crm", "--format", "json"));

        Json(World.RunCli("image", "rm", "crm", "--force", "--format", "json"));
        // The same millisecond again: the per-name high-water mark gives the next one.
        var again = Json(World.RunCliAt(new SetClock(First), "image", "build", "crm", "--from-scripts", folder, "--format", "json")).GetProperty("image").GetProperty("version").GetString()!;
        Assert.Equal("crm@20261202T020000001Z", again);

        var row = Json(World.RunCli("branch", "list", "--format", "json")).GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("name").GetString() == "dev1");
        Assert.Equal(old, row.GetProperty("imageVersion").GetString());
        Assert.True(row.GetProperty("imageRemoved").GetBoolean());
        Assert.Equal("online", row.GetProperty("state").GetString());

        var refused = Json(World.RunCli("branch", "reset", "dev1", "--format", "json"), exit: 1);
        Assert.Equal("image_not_found", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("online", Json(World.RunCli("branch", "list", "--format", "json")).GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("name").GetString() == "dev1").GetProperty("state").GetString());
        Json(World.RunCli("branch", "rm", "dev1", "--format", "json"));
    }

    [IntegrationFact]
    public async Task A_failed_build_on_a_format_2_pool_adds_no_version_and_status_stays_clean()
    {
        await UseFormat2Async();
        var folder = Scripts("f2-bad", ("001_ok.sql", "CREATE TABLE dbo.T (id int);"), ("002_seed.sql", "INSERT dbo.Missing VALUES (1);"));

        var failed = World.RunCli("image", "build", "shopbad", "--from-scripts", folder, "--format", "json");
        output.WriteLine(failed.Out + failed.Err);
        Assert.Equal(1, failed.Exit);

        var listed = Json(World.RunCli("image", "list", "--format", "json"));
        Assert.DoesNotContain(listed.GetProperty("images").EnumerateArray(), i => i.GetProperty("name").GetString() == "shopbad");
        Assert.Equal(0, World.RunCli("status").Exit);
    }
}
