using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Storage;
using SqlBranch.Core.Tests.Fakes;
using static SqlBranch.Core.Tests.Fakes.StorageScenario;

namespace SqlBranch.Core.Tests.Storage;

/// <summary>The Azure Blob transport of design D5, through the real command, the real Azure SDK client and a fake blob service.</summary>
[Collection(StorageSdkSerial.Name)]
public sealed class AzureTransportTests
{
    private readonly StorageScenario _s = new();

    public AzureTransportTests()
    {
        _s.UseSas();
    }

    private static string CodeOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    private static (int Exit, string Out, string Err) RunWithSmallBlocks(StorageScenario scenario, params string[] args)
    {
        var world = scenario.World;
        var services = world.Services() with
        {
            StoreFactory = (url, secrets, redactor) => ArchiveStores.Create(url, secrets, new StorageOptions { Handler = world.Azure, RetryDelay = TimeSpan.Zero, PartSize = 100_000 }, redactor),
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = SqlBranch.Cli.CliApp.CreateDefault(world.Redactor, services).Run(["image", "export", "shop", .. args], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task An_export_stages_blocks_and_commits_once_after_the_last_block_with_ids_of_one_length()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseSas();

        var run = RunWithSmallBlocks(big, "--to", BlobUrl, "--format", "json");

        Assert.Equal(0, run.Exit);
        var service = big.World.Azure;
        var commit = Assert.Single(service.Commits);
        Assert.True(commit.Count > 3, "the archive should span several blocks");
        Assert.Single(commit.Select(id => id.Length).Distinct());
        Assert.Equal(commit.Count, commit.Distinct().Count());
        var requests = service.Requests;
        var lastBlock = requests.ToList().FindLastIndex(r => r.Method == "PUT" && r.Query.Contains("comp=block&", StringComparison.Ordinal));
        var commitIndex = requests.ToList().FindIndex(r => r.Query.Contains("comp=blocklist", StringComparison.Ordinal));
        Assert.True(lastBlock >= 0 && commitIndex > lastBlock, "the commit comes after the last block");
        Assert.Equal(1, requests.Count(r => r.Query.Contains("comp=blocklist", StringComparison.Ordinal)));
        Assert.Equal("*", requests[commitIndex].Headers["If-None-Match"]);

        var stored = Assert.Contains(BlobKey, service.Objects);
        await using var reader = await ImageArchiveReader.OpenAsync(new MemoryStream(stored), CancellationToken.None);
        Assert.Equal("shop", reader.Head.Manifest.Name);
        // Every block ID decodes to a 16-byte prefix and a 6-digit index.
        Assert.All(commit.Select((id, i) => (id, i)), pair =>
        {
            var raw = Convert.FromBase64String(pair.id);
            Assert.Equal(22, raw.Length);
            Assert.Equal(pair.i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), System.Text.Encoding.ASCII.GetString(raw[16..]));
        });
    }

    [Fact]
    public void The_sas_token_travels_in_every_request_query_with_the_leading_question_mark_dropped()
    {
        var run = _s.Export("--to", BlobUrl);

        Assert.Equal(0, run.Exit);
        Assert.NotEmpty(_s.World.Azure.Requests);
        Assert.All(_s.World.Azure.Requests, r =>
        {
            Assert.Contains(Sas, r.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("??", r.Uri.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("?" + "?", r.Query, StringComparison.Ordinal);
            Assert.False(r.Headers.ContainsKey("Authorization"));
        });
    }

    [Fact]
    public void A_token_without_a_leading_question_mark_works_the_same()
    {
        var plain = new StorageScenario();
        plain.UseSas(leadingQuestionMark: false);

        Assert.Equal(0, plain.Export("--to", BlobUrl).Exit);
        Assert.All(plain.World.Azure.Requests, r => Assert.Contains(Sas, r.Query, StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_blob_without_force_is_destination_exists_before_any_image_file_is_read()
    {
        _s.World.Azure.Objects[BlobKey] = [1, 2, 3];

        var run = _s.Export("--to", BlobUrl, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("destination_exists", CodeOf(run.Out));
        Assert.Empty(_s.Timeline);
        Assert.Equal([1, 2, 3], _s.World.Azure.Objects[BlobKey]);
        Assert.DoesNotContain(_s.World.Azure.Requests, r => r.Method == "PUT");
    }

    [Theory]
    [InlineData(409)]
    [InlineData(412)]
    public void A_blob_created_during_the_upload_is_destination_exists_for_both_status_codes_and_is_not_replaced(int status)
    {
        _s.World.Azure.AzureConflictStatus = status;
        _s.World.Azure.OnRequest = seen =>
        {
            if (seen.Method == "PUT" && seen.Query.Contains("comp=block&", StringComparison.Ordinal))
            {
                _s.World.Azure.Objects[BlobKey] = [9, 9, 9];
            }
        };

        var run = _s.Export("--to", BlobUrl, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("destination_exists", CodeOf(run.Out));
        Assert.Equal([9, 9, 9], _s.World.Azure.Objects[BlobKey]);
        Assert.Empty(_s.World.Azure.Commits);
    }

    [Fact]
    public void With_force_the_commit_is_unconditional_and_replaces_the_blob()
    {
        _s.World.Azure.Objects[BlobKey] = [1, 2, 3];

        var run = _s.Export("--to", BlobUrl, "--force");

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain(_s.World.Azure.Requests, r => r.Method == "HEAD");
        Assert.DoesNotContain(_s.World.Azure.Requests, r => r.Headers.ContainsKey("If-None-Match"));
        Assert.True(_s.World.Azure.Objects[BlobKey].Length > 3);
    }

    [Fact]
    public void A_failed_block_means_no_commit_and_no_blob()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseSas();
        var ids = new List<string>();
        big.World.Azure.Fail = seen =>
        {
            if (seen.Method != "PUT" || !seen.Query.Contains("comp=block&", StringComparison.Ordinal))
            {
                return false;
            }

            if (!ids.Contains(seen.Query))
            {
                ids.Add(seen.Query);
            }

            return ids.IndexOf(seen.Query) == 1;
        };

        var run = RunWithSmallBlocks(big, "--to", BlobUrl, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("storage_unreachable", CodeOf(run.Out));
        Assert.Empty(big.World.Azure.Commits);
        Assert.DoesNotContain(BlobKey, big.World.Azure.Objects.Keys);
        Assert.DoesNotContain(big.World.Azure.Requests, r => r.Query.Contains("comp=blocklist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_interleaved_force_uploads_to_one_blob_each_commit_only_their_own_block_ids()
    {
        var url = BackupUrl.Parse(BlobUrl, null, "--to", null, null);
        var secrets = StorageSecrets.Read(url, _s.World.Environment, _s.World.Redactor);
        var store = ArchiveStores.Create(url, secrets, new StorageOptions { Handler = _s.World.Azure, RetryDelay = TimeSpan.Zero }, _s.World.Redactor);
        var first = await store.BeginUploadAsync(failIfExists: false, 10, CancellationToken.None);
        var second = await store.BeginUploadAsync(failIfExists: false, 10, CancellationToken.None);

        await first.UploadPartAsync(new byte[] { 1, 1 }, CancellationToken.None);
        await second.UploadPartAsync(new byte[] { 2, 2 }, CancellationToken.None);
        await first.UploadPartAsync(new byte[] { 1, 1 }, CancellationToken.None);
        await second.UploadPartAsync(new byte[] { 2, 2 }, CancellationToken.None);
        await first.CompleteAsync(CancellationToken.None);
        await second.CompleteAsync(CancellationToken.None);

        var service = _s.World.Azure;
        Assert.Equal(2, service.Commits.Count);
        Assert.Empty(service.Commits[0].Intersect(service.Commits[1]));
        Assert.All(service.Commits, c => Assert.Equal(2, c.Count));
        // Each commit lists exactly the IDs its own upload staged.
        var staged = service.Requests.Where(r => r.Query.Contains("comp=block&", StringComparison.Ordinal))
            .Select(r => System.Web.HttpUtility.ParseQueryString(r.Query)["blockid"]!).ToList();
        Assert.Equal([staged[0], staged[2]], service.Commits[0]);
        Assert.Equal([staged[1], staged[3]], service.Commits[1]);
        Assert.Equal([2, 2, 2, 2], service.Objects[BlobKey]);
    }

    [Fact]
    public async Task The_blob_downloads_with_one_get_and_imports_through_the_command()
    {
        _s.World.Azure.Objects[BlobKey] = await _s.ArchiveAsync();

        var run = _s.Import(BlobUrl, "--as", "shop2", "--format", "json");

        Assert.True(run.Exit == 0, run.Out + run.Err);
        Assert.Equal(["GET"], _s.World.Azure.Requests.Select(r => r.Method));
        Assert.Equal(BlobUrl, JsonDocument.Parse(run.Out).RootElement.GetProperty("from").GetString());
    }

    [Fact]
    public void A_missing_blob_on_import_is_storage_unreachable_naming_the_url_and_changes_nothing()
    {
        var run = _s.Import("https://acme.blob.core.windows.net/images/nope.sbimg", "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("storage_unreachable", CodeOf(run.Out));
        Assert.Contains("https://acme.blob.core.windows.net/images/nope.sbimg", run.Out, StringComparison.Ordinal);
        Assert.Contains("404", run.Out, StringComparison.Ordinal);
        Assert.Empty(_s.World.Runtime.Calls);
        Assert.Empty(_s.Timeline);
    }

    [Fact]
    public void An_export_cancelled_mid_upload_commits_nothing()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseSas();
        using var cancel = new CancellationTokenSource();
        var blocks = 0;
        big.World.Azure.OnRequest = seen =>
        {
            if (seen.Method == "PUT" && seen.Query.Contains("comp=block&", StringComparison.Ordinal) && ++blocks == 2)
            {
                cancel.Cancel();
            }
        };
        var url = BackupUrl.Parse(BlobUrl, null, "--to", null, null);
        var store = ArchiveStores.Create(url, StorageSecrets.Read(url, big.World.Environment, big.World.Redactor),
            new StorageOptions { Handler = big.World.Azure, RetryDelay = TimeSpan.Zero, PartSize = 100_000 }, big.World.Redactor);
        var pool = new FoundPool(new PoolLocation(PoolKind.Directory, FakeInitHost.PoolDir), "plain", PoolSource.EngineLabels, big.World.Runtime.Containers[ContainerLabels.EngineName]);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new ImageExporter(big.World.Runtime).ExportToStoreAsync(pool, "shop", store, force: false, cancel.Token).GetAwaiter().GetResult());

        Assert.Empty(big.World.Azure.Commits);
        Assert.Empty(big.World.Azure.Objects);
    }

    [Fact]
    public void A_failed_force_export_onto_an_existing_blob_leaves_the_old_bytes_and_commits_nothing()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseSas();
        byte[] old = [1, 2, 3, 4];
        big.World.Azure.Objects[BlobKey] = old;
        var blocks = 0;
        big.World.Azure.Fail = seen => seen.Method == "PUT" && seen.Query.Contains("comp=block&", StringComparison.Ordinal) && ++blocks > 1;

        var run = RunWithSmallBlocks(big, "--to", BlobUrl, "--force", "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("storage_unreachable", CodeOf(run.Out));
        Assert.Equal(old, big.World.Azure.Objects[BlobKey]);
        Assert.Empty(big.World.Azure.Commits);
        Assert.DoesNotContain(big.World.Azure.Requests, r => r.Query.Contains("comp=blocklist", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cancelled_force_export_onto_an_existing_blob_leaves_the_old_bytes_and_commits_nothing()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseSas();
        byte[] old = [1, 2, 3, 4];
        big.World.Azure.Objects[BlobKey] = old;
        using var cancel = new CancellationTokenSource();
        var blocks = 0;
        big.World.Azure.OnRequest = seen =>
        {
            if (seen.Method == "PUT" && seen.Query.Contains("comp=block&", StringComparison.Ordinal) && ++blocks == 2)
            {
                cancel.Cancel();
            }
        };
        var url = BackupUrl.Parse(BlobUrl, null, "--to", null, null);
        var store = ArchiveStores.Create(url, StorageSecrets.Read(url, big.World.Environment, big.World.Redactor),
            new StorageOptions { Handler = big.World.Azure, RetryDelay = TimeSpan.Zero, PartSize = 100_000 }, big.World.Redactor);
        var pool = new FoundPool(new PoolLocation(PoolKind.Directory, FakeInitHost.PoolDir), "plain", PoolSource.EngineLabels, big.World.Runtime.Containers[ContainerLabels.EngineName]);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new ImageExporter(big.World.Runtime).ExportToStoreAsync(pool, "shop", store, force: true, cancel.Token).GetAwaiter().GetResult());

        Assert.Equal(old, big.World.Azure.Objects[BlobKey]);
        Assert.Empty(big.World.Azure.Commits);
    }

    [Fact]
    public void A_failing_block_is_sent_again_twice_before_the_export_fails()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseSas();
        var seenIds = new List<string>();
        big.World.Azure.Fail = seen =>
        {
            if (seen.Method == "PUT" && seen.Query.Contains("comp=block&", StringComparison.Ordinal))
            {
                seenIds.Add(seen.Query);
                return seenIds.Distinct().Count() > 1;
            }

            return false;
        };

        var run = RunWithSmallBlocks(big, "--to", BlobUrl, "--format", "json");

        Assert.Equal(1, run.Exit);
        // The second block fails and is tried three times in all, always under the same block ID.
        Assert.Equal(3, seenIds.GroupBy(q => q).Select(g => g.Count()).Max());
        Assert.Empty(big.World.Azure.Commits);
    }

    [Fact]
    public void A_commit_that_fails_once_is_sent_again_and_the_retry_still_carries_if_none_match()
    {
        var failed = false;
        _s.World.Azure.Fail = seen =>
        {
            if (seen.Query.Contains("comp=blocklist", StringComparison.Ordinal) && !failed)
            {
                failed = true;
                return true;
            }

            return false;
        };

        var run = _s.Export("--to", BlobUrl, "--format", "json");

        Assert.Equal(0, run.Exit);
        var commits = _s.World.Azure.Requests.Where(r => r.Query.Contains("comp=blocklist", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, commits.Count);
        Assert.All(commits, r => Assert.Equal("*", r.Headers["If-None-Match"]));
        Assert.Single(_s.World.Azure.Commits);
    }
}
