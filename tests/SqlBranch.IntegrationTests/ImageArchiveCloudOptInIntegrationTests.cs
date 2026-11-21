using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 5.4: export to a real Azure Blob or S3 location, import it as a new name and query a branch. Run by hand before the 0.3
/// release; skipped unless <c>SQLBRANCH_IT=1</c>, the <c>SQLBRANCH_IT_*_EXPORT_URL</c> variable and F1's secrets are set. The URL names an
/// object that does not exist yet (the test does not use <c>--force</c>, so it never replaces anything). The secrets come from the
/// environment and are never printed.
/// </summary>
[Trait("Category", "Cloud")]
[Trait("Category", "Integration")]
public sealed class ImageArchiveCloudOptInIntegrationTests(ITestOutputHelper output)
{
    private static string Variable(string name) => Environment.GetEnvironmentVariable(name)!;

    private async Task RoundTripAsync(string url, string[] extraArgs, Dictionary<string, string> secrets)
    {
        await using var world = new EngineWorld("4GB");
        var init = world.RunCli("init");
        Assert.True(init.Exit == 0, init.Out + init.Err);
        foreach (var (key, value) in secrets)
        {
            world.Environment[key] = value;
        }

        var folder = Path.Combine(Path.GetTempPath(), "sqlbranch-it-cloud-" + DockerHost.NewId());
        Directory.CreateDirectory(folder);
        try
        {
            var bak = Path.Combine(folder, "Shop.bak");
            await MakeBackupAsync(world, bak);
            var build = world.RunCli("image", "build", "cloud-shop", "--from-bak", bak, "--format", "json");
            Assert.True(build.Exit == 0, Redacted(build.Out + build.Err, secrets.Values));

            var export = world.RunCli(["image", "export", "cloud-shop", "--to", url, .. extraArgs, "--format", "json"]);
            output.WriteLine($"image export -> {export.Exit} ({export.Elapsed.TotalSeconds:F1} s)");
            Assert.True(export.Exit == 0, Redacted(export.Out + export.Err, secrets.Values));

            var import = world.RunCli(["image", "import", url, "--as", "cloud-copy", .. extraArgs, "--format", "json"]);
            output.WriteLine($"image import -> {import.Exit} ({import.Elapsed.TotalSeconds:F1} s)");
            Assert.True(import.Exit == 0, Redacted(import.Out + import.Err, secrets.Values));

            var branch = world.RunCli("branch", "create", "cloud-branch", "--from", "cloud-copy", "--format", "json");
            Assert.True(branch.Exit == 0, branch.Out + branch.Err);
            foreach (var secret in secrets.Values)
            {
                Assert.False((export.Out + export.Err + import.Out + import.Err).Contains(secret, StringComparison.Ordinal), "A storage secret is in an output.");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task MakeBackupAsync(EngineWorld world, string path)
    {
        await using var session = await new SqlBranch.Core.Engine.SqlClientEngineConnector().ConnectAsync(world.Endpoint, world.Password, CancellationToken.None);
        foreach (var sql in new[]
        {
            "CREATE DATABASE [Shop]",
            "EXEC [Shop].sys.sp_executesql N'CREATE TABLE dbo.Item (Id int NOT NULL PRIMARY KEY); INSERT dbo.Item VALUES (1), (2), (3)'",
            "BACKUP DATABASE [Shop] TO DISK = N'/tmp/Shop.bak' WITH INIT",
        })
        {
            await session.ExecuteAsync(sql, CancellationToken.None);
        }

        var copy = await EngineWorld.DockerAsync("cp", $"{world.EngineName}:/tmp/Shop.bak", path);
        Assert.True(copy.ExitCode == 0, copy.StdErr);
        await session.ExecuteAsync("DROP DATABASE [Shop]", CancellationToken.None);
    }

    private static string Redacted(string text, IEnumerable<string> secrets) =>
        secrets.Aggregate(text, (current, secret) => current.Replace(secret, "***", StringComparison.Ordinal));

    [CloudFact("SQLBRANCH_IT_AZURE_EXPORT_URL", "SQLBRANCH_AZURE_SAS_TOKEN")]
    public async Task An_image_round_trips_through_real_azure_blob_storage() =>
        await RoundTripAsync(Variable("SQLBRANCH_IT_AZURE_EXPORT_URL"), [],
            new Dictionary<string, string> { ["SQLBRANCH_AZURE_SAS_TOKEN"] = Variable("SQLBRANCH_AZURE_SAS_TOKEN") });

    [CloudFact("SQLBRANCH_IT_S3_EXPORT_URL", "SQLBRANCH_IT_S3_REGION", "SQLBRANCH_S3_ACCESS_KEY_ID", "SQLBRANCH_S3_SECRET_ACCESS_KEY")]
    public async Task An_image_round_trips_through_real_s3() =>
        await RoundTripAsync(Variable("SQLBRANCH_IT_S3_EXPORT_URL"), ["--s3-region", Variable("SQLBRANCH_IT_S3_REGION")],
            new Dictionary<string, string>
            {
                ["SQLBRANCH_S3_ACCESS_KEY_ID"] = Variable("SQLBRANCH_S3_ACCESS_KEY_ID"),
                ["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = Variable("SQLBRANCH_S3_SECRET_ACCESS_KEY"),
            });
}
