using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 5.5: the Blob cases against Azurite, which spike 1.2 showed works as a restore source over HTTPS on port 443 with a
/// certificate for the account's host name and a SAS that includes write permission.
/// </summary>
[Collection(CloudSourceGroup.Name)]
[Trait("Category", "Integration")]
public sealed class AzuriteSourceIntegrationTests(CloudSourceFixture fixture, ITestOutputHelper output)
{
    private EngineWorld World => fixture.World;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<string> InPoolAsync(string script) => await new BranchKit(fixture.Engine, output).InPoolAsync(script);

    private void AssertNoSecret(string label, string text)
    {
        Assert.False(text.Contains(fixture.SasToken, StringComparison.Ordinal), $"The SAS token is in: {label}");
        Assert.False(text.Contains(fixture.SasSignature, StringComparison.Ordinal), $"The SAS signature is in: {label}");
        Assert.False(text.Contains(Uri.EscapeDataString(fixture.SasSignature), StringComparison.Ordinal), $"The SAS signature is in: {label}");
    }

    private async Task AssertNoCredentialAsync() =>
        Assert.Equal(0, await fixture.CountCredentialsAsync("name LIKE N'https://%'"));

    [IntegrationFact]
    public async Task A_blob_build_lists_the_image_records_the_url_and_leaves_no_credential_or_secret()
    {
        var url = fixture.BlobUrl("shop.bak");
        var run = World.RunCli("image", "build", "blobshop", "--from-bak", url, "--format", "json");
        output.WriteLine($"image build -> {run.Exit} ({run.Elapsed.TotalSeconds:F1} s)");
        try
        {
            Assert.True(run.Exit == 0, run.Out + run.Err);
            var listed = Parse(World.RunCli("image", "list", "--format", "json").Out).GetProperty("images").EnumerateArray()
                .Single(i => i.GetProperty("name").GetString() == "blobshop");
            Assert.Equal(url, listed.GetProperty("backupUrl").GetString());
            Assert.Equal("shop.bak", listed.GetProperty("backupFile").GetString());
            Assert.Equal(JsonValueKind.Null, listed.GetProperty("backupSha256").ValueKind);
            var manifest = await InPoolAsync("cat /pool/images/blobshop/manifest.json");
            Assert.Equal(url, Parse(manifest).GetProperty("backupUrl").GetString());

            await AssertNoCredentialAsync();
            Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/build"));
            Assert.Equal(string.Empty, await InPoolAsync("find /pool -name '*.bak'"));
            Assert.Equal(0, World.RunCli("status").Exit);

            var inspect = await EngineWorld.DockerAsync("inspect", World.EngineName);
            foreach (var (label, text) in new[] { ("stdout", run.Out), ("stderr", run.Err), ("manifest", manifest), ("engine settings", inspect.StdOut) })
            {
                AssertNoSecret(label, text);
            }
        }
        finally
        {
            World.RunCli("image", "rm", "blobshop", "--force");
            await InPoolAsync("rm -rf /pool/images/blobshop");
        }
    }

    [IntegrationFact]
    public async Task A_wrong_sas_signature_is_backup_unreachable_without_a_credential_or_an_image()
    {
        var run = fixture.RunCliWith(new Dictionary<string, string> { ["SQLBRANCH_AZURE_SAS_TOKEN"] = fixture.WrongSasToken },
            "image", "build", "blobwrong", "--from-bak", fixture.BlobUrl("shop.bak"), "--format", "json");
        output.WriteLine(run.Out);

        Assert.Equal(1, run.Exit);
        Assert.Equal("backup_unreachable", Parse(run.Out).GetProperty("error").GetProperty("code").GetString());
        Assert.False((run.Out + run.Err).Contains(fixture.WrongSasToken, StringComparison.Ordinal), "The wrong SAS token is in the output.");
        Assert.Equal(0, World.RunCli("status").Exit);
        await AssertNoCredentialAsync();
        Assert.DoesNotContain("blobwrong", World.RunCli("image", "list").Out, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task A_missing_blob_is_backup_unreachable_and_names_the_url()
    {
        var url = fixture.BlobUrl("nope.bak");
        var run = World.RunCli("image", "build", "blobmissing", "--from-bak", url, "--format", "json");
        output.WriteLine(run.Out);

        Assert.Equal(1, run.Exit);
        var error = Parse(run.Out).GetProperty("error");
        Assert.Equal("backup_unreachable", error.GetProperty("code").GetString());
        Assert.Contains(url, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertNoSecret("stdout", run.Out + run.Err);
        await AssertNoCredentialAsync();
        Assert.Equal(0, World.RunCli("status").Exit);
    }

    [IntegrationFact]
    public async Task A_credential_made_by_hand_is_not_replaced_and_the_build_exits_1_credential_conflict()
    {
        var name = fixture.BlobCredentialName;
        await fixture.Engine.SqlAsync($"CREATE CREDENTIAL [{name}] WITH IDENTITY = N'SHARED ACCESS SIGNATURE', SECRET = N'sv=hand-made'");
        try
        {
            var before = Convert.ToString(await fixture.Engine.ScalarAsync($"SELECT CONVERT(varchar(20), credential_id) + N'|' + CONVERT(varchar(33), create_date, 126) FROM sys.credentials WHERE name = N'{name}'"), CultureInfo.InvariantCulture);
            var run = World.RunCli("image", "build", "blobconflict", "--from-bak", fixture.BlobUrl("shop.bak"), "--format", "json");
            output.WriteLine(run.Out);

            Assert.Equal(1, run.Exit);
            var error = Parse(run.Out).GetProperty("error");
            Assert.Equal("credential_conflict", error.GetProperty("code").GetString());
            Assert.Contains(name, error.GetProperty("message").GetString(), StringComparison.Ordinal);
            var after = Convert.ToString(await fixture.Engine.ScalarAsync($"SELECT CONVERT(varchar(20), credential_id) + N'|' + CONVERT(varchar(33), create_date, 126) FROM sys.credentials WHERE name = N'{name}'"), CultureInfo.InvariantCulture);
            Assert.Equal(before, after);
        }
        finally
        {
            await fixture.Engine.SqlAsync($"IF EXISTS (SELECT 1 FROM sys.credentials WHERE name = N'{name}') DROP CREDENTIAL [{name}]");
        }
    }
}
