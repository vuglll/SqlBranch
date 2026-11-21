using System.Text.Json;
using SqlBranch.Core.Tests.Fakes;
using static SqlBranch.Core.Tests.Fakes.StorageScenario;

namespace SqlBranch.Core.Tests.Storage;

/// <summary><c>storage_unreachable</c> (task 4.4): every storage request fails in turn, and no secret reaches any output.</summary>
[Collection(StorageSdkSerial.Name)]
public sealed class StorageUnreachableTests
{
    private static string CodeOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    private static StorageScenario Scenario(bool azure)
    {
        var s = new StorageScenario();
        if (azure)
        {
            s.UseSas();
        }
        else
        {
            s.UseS3Secrets();
        }

        return s;
    }

    private static FakeStorageService Service(StorageScenario s, bool azure) => azure ? s.World.Azure : s.World.S3;

    private static void AssertClean(string text, bool azure, string url)
    {
        foreach (var secret in azure ? new[] { Sas, "AbC123secretSig", "sv=2024-11-04", "sp=rcw" } : [KeyId, Secret])
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("?", text.Replace(url + "?", string.Empty, StringComparison.Ordinal).Replace("?", string.Empty, StringComparison.Ordinal) + "x", StringComparison.Ordinal);
        Assert.DoesNotContain(url + "?", text, StringComparison.Ordinal);
    }

    /// <summary>A request without what changes between runs (the upload ID and the block ID).</summary>
    private static string Shape(SeenRequest seen) =>
        seen.Method + " " + string.Join('&', seen.Query.TrimStart('?').Split('&').Where(p => !p.StartsWith("uploadId=", StringComparison.Ordinal) && !p.StartsWith("blockid=", StringComparison.Ordinal)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_export_request_that_fails_gives_storage_unreachable_with_the_url_and_no_secret(bool azure)
    {
        var url = azure ? BlobUrl : S3Url;
        var probe = Scenario(azure);
        Assert.Equal(0, probe.Export("--to", url).Exit);
        var shapes = Service(probe, azure).Requests.Select(Shape).ToList();
        var count = shapes.Count;
        Assert.True(count >= 3);

        for (var failing = 0; failing < count; failing++)
        {
            foreach (var format in new[] { "json", "text" })
            {
                var s = Scenario(azure);
                var service = Service(s, azure);
                // The SDK sends a failing request again, so the service fails every attempt of that request, not one index.
                service.Fail = seen => Shape(seen) == shapes[failing];

                var run = s.Export("--to", url, "--format", format);

                Assert.True(run.Exit == 1, $"request {failing} ({format}): {run.Out}{run.Err}");
                Assert.Contains(format == "json" ? "storage_unreachable" : "error:", run.Out + run.Err, StringComparison.Ordinal);
                Assert.Contains(url, run.Out + run.Err, StringComparison.Ordinal);
                Assert.Contains("500", run.Out + run.Err, StringComparison.Ordinal);
                AssertClean(run.Out + run.Err, azure, url);
                Assert.Empty(service.Objects);
                Assert.Empty(service.Commits);
                Assert.Empty(service.OpenUploads);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_import_request_that_fails_gives_storage_unreachable_with_no_docker_command_and_no_secret(bool azure)
    {
        var url = azure ? BlobUrl : S3Url;
        var key = azure ? BlobKey : S3Key;
        var s = Scenario(azure);
        var service = Service(s, azure);
        service.Objects[key] = await s.ArchiveAsync();
        service.Fail = _ => true;

        foreach (var format in new[] { "json", "text" })
        {
            var run = s.Import(url, "--format", format);

            Assert.Equal(1, run.Exit);
            Assert.Contains(format == "json" ? "storage_unreachable" : "error:", run.Out + run.Err, StringComparison.Ordinal);
            Assert.Contains(url, run.Out + run.Err, StringComparison.Ordinal);
            AssertClean(run.Out + run.Err, azure, url);
            Assert.Empty(s.World.Runtime.Calls);
            Assert.Empty(s.Timeline);
        }
    }

    [Fact]
    public async Task A_wrong_s3_secret_on_import_is_storage_unreachable_and_the_pool_is_untouched()
    {
        var s = Scenario(azure: false);
        s.World.S3.Objects[S3Key] = await s.ArchiveAsync();
        s.World.S3.ExpectedAccessKeyId = "AKIDSOMEONEELSE";

        var run = s.Import(S3Url, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("storage_unreachable", CodeOf(run.Out));
        Assert.Contains("403", run.Out, StringComparison.Ordinal);
        Assert.Empty(s.World.Runtime.Calls);
        Assert.Empty(s.Timeline);
        AssertClean(run.Out + run.Err, azure: false, S3Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_server_that_cannot_be_reached_is_storage_unreachable_with_the_reason(bool azure)
    {
        var s = Scenario(azure);
        Service(s, azure).Down = true;
        var url = azure ? BlobUrl : S3Url;

        var run = s.Export("--to", url, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("storage_unreachable", CodeOf(run.Out));
        Assert.Contains(url, run.Out, StringComparison.Ordinal);
        Assert.True(run.Out.Contains("Connection refused", StringComparison.Ordinal), run.Out);
        AssertClean(run.Out + run.Err, azure, url);
    }

    [Fact]
    public async Task A_download_that_breaks_in_the_middle_is_storage_unreachable_and_the_import_cleans_up()
    {
        var s = Scenario(azure: false);
        var archive = await s.ArchiveAsync();
        s.World.S3.Objects[S3Key] = archive;
        s.World.S3.CutBodyAt = archive.Length / 2;

        var run = s.Import(S3Url, "--as", "shop2", "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("storage_unreachable", CodeOf(run.Out));
        AssertClean(run.Out + run.Err, azure: false, S3Url);
    }
}
