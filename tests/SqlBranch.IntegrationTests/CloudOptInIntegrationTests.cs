using System.Text.Json;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>A test that needs a real cloud account: it runs only with SQLBRANCH_IT=1 and every variable it names.</summary>
public sealed class CloudFactAttribute : FactAttribute
{
    public CloudFactAttribute(params string[] variables)
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnableVariable) != "1")
        {
            Skip = "Needs Docker. Set SQLBRANCH_IT=1 to run it.";
        }
        else if (variables.Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList() is { Count: > 0 } missing)
        {
            Skip = "Needs a cloud account. Set " + string.Join(", ", missing) + " to run it.";
        }
    }
}

/// <summary>
/// Task 5.4: one Azure Blob build and one S3 build from a real account, on an engine newly created by <c>sqlbranch init</c> with
/// nothing in its certificate folder (the engine scenario "Engine trusts public storage certificates"). Run by hand before the
/// 0.3 release; skipped without the variables. The secrets come from the environment and are never printed.
/// </summary>
[Trait("Category", "Cloud")]
[Trait("Category", "Integration")]
public sealed class CloudOptInIntegrationTests(ITestOutputHelper output)
{
    private static string Variable(string name) => Environment.GetEnvironmentVariable(name)!;

    private async Task BuildAsync(string url, string[] extraArgs, Dictionary<string, string> secrets)
    {
        await using var world = new EngineWorld("4GB");
        var init = world.RunCli("init");
        Assert.True(init.Exit == 0, init.Out + init.Err);
        var certificates = await world.Runtime.ExecAsync(world.EngineName, ["sh", "-c", "ls -A /var/opt/mssql/security/ca-certificates 2>/dev/null | wc -l"], CancellationToken.None);
        Assert.Equal("0", certificates.StdOut.Trim());
        foreach (var (key, value) in secrets)
        {
            world.Environment[key] = value;
        }

        var build = world.RunCli(["image", "build", "cloud", "--from-bak", url, .. extraArgs, "--format", "json"]);
        output.WriteLine($"image build -> {build.Exit} ({build.Elapsed.TotalSeconds:F1} s)");
        Assert.True(build.Exit == 0, RedactedOut(build.Err + build.Out, secrets.Values));
        var image = JsonDocument.Parse(build.Out).RootElement.GetProperty("image");
        Assert.Equal(url, image.GetProperty("backupUrl").GetString());
        await using var session = await new SqlBranch.Core.Engine.SqlClientEngineConnector().ConnectAsync(world.Endpoint, world.Password, CancellationToken.None);
        Assert.Equal(0, Convert.ToInt32(await session.ScalarAsync("SELECT COUNT(*) FROM sys.credentials WHERE name LIKE N'https://%' OR name LIKE N's3://%'", CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture));
        foreach (var secret in secrets.Values)
        {
            Assert.False((build.Out + build.Err).Contains(secret, StringComparison.Ordinal), "A storage secret is in the output of the build.");
        }
    }

    private static string RedactedOut(string text, IEnumerable<string> secrets) =>
        secrets.Aggregate(text, (current, secret) => current.Replace(secret, "***", StringComparison.Ordinal));

    [CloudFact("SQLBRANCH_IT_AZURE_URL", "SQLBRANCH_AZURE_SAS_TOKEN")]
    public async Task A_build_from_real_azure_blob_storage_works_on_a_newly_created_engine() =>
        await BuildAsync(Variable("SQLBRANCH_IT_AZURE_URL"), [],
            new Dictionary<string, string> { ["SQLBRANCH_AZURE_SAS_TOKEN"] = Variable("SQLBRANCH_AZURE_SAS_TOKEN") });

    [CloudFact("SQLBRANCH_IT_S3_URL", "SQLBRANCH_IT_S3_REGION", "SQLBRANCH_S3_ACCESS_KEY_ID", "SQLBRANCH_S3_SECRET_ACCESS_KEY")]
    public async Task A_build_from_real_s3_works_on_a_newly_created_engine() =>
        await BuildAsync(Variable("SQLBRANCH_IT_S3_URL"), ["--s3-region", Variable("SQLBRANCH_IT_S3_REGION")],
            new Dictionary<string, string>
            {
                ["SQLBRANCH_S3_ACCESS_KEY_ID"] = Variable("SQLBRANCH_S3_ACCESS_KEY_ID"),
                ["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = Variable("SQLBRANCH_S3_SECRET_ACCESS_KEY"),
            });
}
