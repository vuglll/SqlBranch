using System.Text.Json;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Storage;
using SqlBranch.Core.Tests.Fakes;
using static SqlBranch.Core.Tests.Fakes.StorageScenario;

namespace SqlBranch.Core.Tests.Storage;

/// <summary>The S3 transport of design D5, through the real command, the real AWS SDK client and a fake S3 service.</summary>
[Collection(StorageSdkSerial.Name)]
public sealed class S3TransportTests
{
    private readonly StorageScenario _s = new();

    public S3TransportTests()
    {
        _s.UseS3Secrets();
    }

    private static string CodeOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task An_export_writes_one_object_whose_archive_imports_back_and_commits_with_if_none_match()
    {
        var run = _s.Export("--to", S3Url, "--s3-region", "eu-west-1", "--format", "json");

        Assert.Equal(0, run.Exit);
        var json = JsonDocument.Parse(run.Out).RootElement;
        Assert.Equal(S3Url, json.GetProperty("export").GetProperty("to").GetString());
        var stored = Assert.Contains(S3Key, _s.World.S3.Objects);
        var put = _s.World.S3.Requests.First(r => r.Method == "PUT");
        Assert.True(stored.Length == json.GetProperty("export").GetProperty("archiveBytes").GetInt64(), string.Join("\n", put.Headers.Select(h => h.Key + ": " + h.Value)));
        await using var reader = await ImageArchiveReader.OpenAsync(new MemoryStream(stored), CancellationToken.None);
        Assert.Equal("shop", reader.Head.Manifest.Name);

        // The existence check first (HEAD), then multipart, then one conditional complete.
        var methods = _s.World.S3.Requests.Select(r => r.Method).ToList();
        Assert.Equal("HEAD", methods[0]);
        var complete = _s.World.S3.Requests[^1];
        Assert.Equal("POST", complete.Method);
        Assert.Equal("*", complete.Headers["If-None-Match"]);
        Assert.Empty(_s.World.S3.OpenUploads);
        Assert.All(_s.World.S3.Requests, r => Assert.Contains($"Credential={KeyId}/", r.Authorization, StringComparison.Ordinal));
        Assert.All(_s.World.S3.Requests, r => Assert.Contains("/eu-west-1/s3/", r.Authorization, StringComparison.Ordinal));
        Assert.All(_s.World.S3.Requests, r => Assert.Equal("s3.amazonaws.com", r.Uri.Host));
    }

    [Fact]
    public async Task The_archive_in_s3_imports_through_the_command_and_the_manifest_is_the_exported_one_with_the_new_name()
    {
        _s.World.S3.Objects[S3Key] = await _s.ArchiveAsync();

        var run = _s.Import(S3Url, "--as", "shop2", "--format", "json");

        Assert.True(run.Exit == 0, run.Out + run.Err);
        var json = JsonDocument.Parse(run.Out).RootElement;
        Assert.Equal(S3Url, json.GetProperty("from").GetString());
        Assert.Equal("shop2", json.GetProperty("image").GetProperty("name").GetString());
        Assert.Equal(["GET"], _s.World.S3.Requests.Select(r => r.Method));
    }

    [Fact]
    public void An_existing_object_without_force_is_destination_exists_before_any_image_file_is_read()
    {
        _s.World.S3.Objects[S3Key] = [1, 2, 3];

        var run = _s.Export("--to", S3Url, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("destination_exists", CodeOf(run.Out));
        Assert.Contains("--force", run.Out, StringComparison.Ordinal);
        Assert.Empty(_s.Timeline);
        Assert.Equal([1, 2, 3], _s.World.S3.Objects[S3Key]);
        Assert.Equal(["HEAD"], _s.World.S3.Requests.Select(r => r.Method));
    }

    [Fact]
    public void With_force_no_existence_check_runs_and_the_commit_is_not_conditional_and_replaces_the_object()
    {
        _s.World.S3.Objects[S3Key] = [1, 2, 3];

        var run = _s.Export("--to", S3Url, "--force");

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain(_s.World.S3.Requests, r => r.Method == "HEAD");
        Assert.DoesNotContain(_s.World.S3.Requests, r => r.Headers.ContainsKey("If-None-Match"));
        Assert.True(_s.World.S3.Objects[S3Key].Length > 3);
    }

    [Fact]
    public void An_object_created_during_the_upload_is_destination_exists_and_is_not_replaced()
    {
        _s.World.S3.OnRequest = seen =>
        {
            if (seen.Method == "PUT")
            {
                _s.World.S3.Objects[S3Key] = [9, 9, 9];
            }
        };

        var run = _s.Export("--to", S3Url, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("destination_exists", CodeOf(run.Out));
        Assert.Equal([9, 9, 9], _s.World.S3.Objects[S3Key]);
        Assert.Single(_s.World.S3.Aborted);
        Assert.Empty(_s.World.S3.OpenUploads);
    }

    [Fact]
    public void A_failed_part_aborts_the_multipart_upload_and_leaves_no_object()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseS3Secrets();
        // Parts of 100 KB: the random data makes several parts; the second one fails.
        big.World.S3.Fail = seen => seen.Method == "PUT" && seen.Query.Contains("partNumber=2", StringComparison.Ordinal);
        var run = RunWithSmallParts(big, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("storage_unreachable", CodeOf(run.Out));
        Assert.Single(big.World.S3.Aborted);
        Assert.Empty(big.World.S3.OpenUploads);
        Assert.DoesNotContain(S3Key, big.World.S3.Objects.Keys);
        Assert.DoesNotContain(big.World.S3.Requests, r => r.Method == "POST" && r.Query.Contains("uploadId", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cancelled_export_aborts_the_upload_and_leaves_no_object()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseS3Secrets();
        using var cancel = new CancellationTokenSource();
        big.World.S3.OnRequest = seen =>
        {
            if (seen.Method == "PUT" && seen.Query.Contains("partNumber=2", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        };
        var store = ArchiveStores.Create(BackupUrl.Parse(S3Url, null, "--to", null, null), SecretsFor(big), new StorageOptions { Handler = big.World.S3, RetryDelay = TimeSpan.Zero, PartSize = 100_000 }, big.World.Redactor);

        var pool = new FoundPool(new PoolLocation(PoolKind.Directory, FakeInitHost.PoolDir), "plain", PoolSource.EngineLabels, big.World.Runtime.Containers[SqlBranch.Core.Containers.ContainerLabels.EngineName]);
        var export = new ImageExporter(big.World.Runtime).ExportToStoreAsync(pool, "shop", store, force: false, cancel.Token);

        Assert.ThrowsAny<OperationCanceledException>(() => export.GetAwaiter().GetResult());
        Assert.Single(big.World.S3.Aborted);
        Assert.Empty(big.World.S3.OpenUploads);
        Assert.Empty(big.World.S3.Objects);
    }

    [Fact]
    public void A_failed_force_export_onto_an_existing_object_leaves_the_old_bytes_and_commits_nothing()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseS3Secrets();
        byte[] old = [1, 2, 3, 4];
        big.World.S3.Objects[S3Key] = old;
        big.World.S3.Fail = seen => seen.Method == "PUT" && seen.Query.Contains("partNumber=2", StringComparison.Ordinal);

        var run = RunWithSmallParts(big, "--force", "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("storage_unreachable", CodeOf(run.Out));
        Assert.Equal(old, big.World.S3.Objects[S3Key]);
        Assert.DoesNotContain(big.World.S3.Requests, r => r.Method == "POST" && r.Query.Contains("uploadId", StringComparison.Ordinal));
        Assert.Single(big.World.S3.Aborted);
        Assert.Empty(big.World.S3.OpenUploads);
    }

    [Fact]
    public void A_cancelled_force_export_onto_an_existing_object_leaves_the_old_bytes_and_commits_nothing()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseS3Secrets();
        byte[] old = [1, 2, 3, 4];
        big.World.S3.Objects[S3Key] = old;
        using var cancel = new CancellationTokenSource();
        big.World.S3.OnRequest = seen =>
        {
            if (seen.Method == "PUT" && seen.Query.Contains("partNumber=2", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        };
        var store = ArchiveStores.Create(BackupUrl.Parse(S3Url, null, "--to", null, null), SecretsFor(big), new StorageOptions { Handler = big.World.S3, RetryDelay = TimeSpan.Zero, PartSize = 100_000 }, big.World.Redactor);
        var pool = new FoundPool(new PoolLocation(PoolKind.Directory, FakeInitHost.PoolDir), "plain", PoolSource.EngineLabels, big.World.Runtime.Containers[SqlBranch.Core.Containers.ContainerLabels.EngineName]);

        var export = new ImageExporter(big.World.Runtime).ExportToStoreAsync(pool, "shop", store, force: true, cancel.Token);

        Assert.ThrowsAny<OperationCanceledException>(() => export.GetAwaiter().GetResult());
        Assert.Equal(old, big.World.S3.Objects[S3Key]);
        Assert.DoesNotContain(big.World.S3.Requests, r => r.Method == "POST" && r.Query.Contains("uploadId", StringComparison.Ordinal));
        Assert.Single(big.World.S3.Aborted);
        Assert.Empty(big.World.S3.OpenUploads);
    }

    [Fact]
    public void A_conditional_request_conflict_on_complete_is_destination_exists_without_force()
    {
        _s.World.S3.S3ConflictInsteadOfPrecondition = true;
        _s.World.S3.OnRequest = seen =>
        {
            if (seen.Method == "PUT")
            {
                _s.World.S3.Objects[S3Key] = [9, 9, 9];
            }
        };

        var run = _s.Export("--to", S3Url, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal("destination_exists", CodeOf(run.Out));
        Assert.Equal([9, 9, 9], _s.World.S3.Objects[S3Key]);
        Assert.Single(_s.World.S3.Aborted);
    }

    [Fact]
    public void A_failing_part_is_sent_again_twice_before_the_export_fails()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseS3Secrets();
        big.World.S3.Fail = seen => seen.Method == "PUT" && seen.Query.Contains("partNumber=2", StringComparison.Ordinal);

        var run = RunWithSmallParts(big, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Equal(3, big.World.S3.Requests.Count(r => r.Method == "PUT" && r.Query.Contains("partNumber=2", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_part_that_fails_once_is_retried_and_the_export_succeeds_with_the_conditional_complete_intact()
    {
        var big = new StorageScenario(dataBytes: 600_000, logBytes: 100_000);
        big.UseS3Secrets();
        var failures = 0;
        big.World.S3.Fail = seen => seen.Method == "PUT" && seen.Query.Contains("partNumber=2", StringComparison.Ordinal) && failures++ == 0;

        var run = RunWithSmallParts(big, "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Contains(S3Key, big.World.S3.Objects.Keys);
        Assert.Equal("*", Assert.Single(big.World.S3.Requests, r => r.Method == "POST" && r.Query.Contains("uploadId", StringComparison.Ordinal)).Headers["If-None-Match"]);
    }

    [Fact]
    public void A_complete_that_fails_once_is_sent_again_and_the_retry_still_carries_if_none_match()
    {
        var failed = false;
        _s.World.S3.Fail = seen =>
        {
            if (seen.Method == "POST" && seen.Query.Contains("uploadId", StringComparison.Ordinal) && !failed)
            {
                failed = true;
                return true;
            }

            return false;
        };

        var run = _s.Export("--to", S3Url, "--format", "json");

        Assert.Equal(0, run.Exit);
        var completes = _s.World.S3.Requests.Where(r => r.Method == "POST" && r.Query.Contains("uploadId", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, completes.Count);
        Assert.All(completes, r => Assert.Equal("*", r.Headers["If-None-Match"]));
    }

    [Fact]
    public void A_server_that_ignores_if_none_match_is_protected_only_by_the_upfront_check()
    {
        _s.World.S3.HonoursIfNoneMatch = false;
        _s.World.S3.OnRequest = seen =>
        {
            if (seen.Method == "PUT")
            {
                _s.World.S3.Objects[S3Key] = [9, 9, 9];
            }
        };

        var run = _s.Export("--to", S3Url);

        Assert.Equal(0, run.Exit);
        Assert.True(_s.World.S3.Objects[S3Key].Length > 3);
    }

    [Theory]
    [InlineData(600L, 9000)]
    [InlineData(1L, 9000)]
    [InlineData(4000L, 9000)]
    public void The_part_size_keeps_an_image_of_any_size_within_9000_parts(long gib, int maxParts)
    {
        var bytes = gib << 30;
        var size = PartSizing.For(bytes);

        Assert.True(size >= PartSizing.DefaultPartSize);
        Assert.True((bytes + size - 1) / size <= maxParts, $"{gib} GiB needs more than {maxParts} parts of {size} bytes");
    }

    [Fact]
    public void The_part_size_for_600_gib_is_above_64_mib_and_for_100_gib_is_64_mib()
    {
        Assert.True(PartSizing.For(600L << 30) > (64 << 20));
        Assert.Equal(64 << 20, PartSizing.For(100L << 30));
        Assert.Equal(64 << 20, PartSizing.For(500L << 30));
        Assert.Equal(0, PartSizing.For(600L << 30) % (1 << 20));
    }

    [Fact]
    public void A_virtual_host_style_url_puts_the_bucket_in_the_host_and_the_key_in_the_path()
    {
        _s.World.S3.Objects["images/nightly/shop.sbimg"] = [1];

        var run = _s.Export("--to", "s3://images.s3.eu-west-1.amazonaws.com/nightly/shop.sbimg", "--s3-region", "eu-west-1");

        Assert.Equal(1, run.Exit);
        var head = _s.World.S3.Requests[0];
        Assert.Equal("images.s3.eu-west-1.amazonaws.com", head.Uri.Host);
        Assert.Equal("/nightly/shop.sbimg", head.Uri.AbsolutePath);
    }

    [Fact]
    public void A_path_style_url_with_a_port_goes_to_that_host_and_port_over_https()
    {
        var run = _s.Export("--to", "s3://172.17.0.5:9000/images/nightly/shop.sbimg");

        Assert.Equal(0, run.Exit);
        Assert.All(_s.World.S3.Requests, r =>
        {
            Assert.Equal("https://172.17.0.5:9000/", r.Uri.GetLeftPart(UriPartial.Authority) + "/");
            Assert.StartsWith("/images/nightly/shop.sbimg", r.Uri.AbsolutePath, StringComparison.Ordinal);
        });
    }

    // ---- helpers ----

    private static (int Exit, string Out, string Err) RunWithSmallParts(StorageScenario scenario, params string[] extra)
    {
        // The real command with a store that cuts parts of 100 KB, so a small archive spans several parts.
        var world = scenario.World;
        var services = world.Services() with
        {
            StoreFactory = (url, secrets, redactor) => ArchiveStores.Create(url, secrets, new StorageOptions { Handler = world.S3, RetryDelay = TimeSpan.Zero, PartSize = 100_000 }, redactor),
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = SqlBranch.Cli.CliApp.CreateDefault(world.Redactor, services).Run(["image", "export", "shop", "--to", S3Url, .. extra], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static SqlBranch.Core.Secrets.StorageSecrets SecretsFor(StorageScenario scenario) =>
        SqlBranch.Core.Secrets.StorageSecrets.Read(BackupUrl.Parse(S3Url, null, "--to", null, null), scenario.World.Environment, scenario.World.Redactor);
}
