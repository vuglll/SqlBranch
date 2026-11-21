using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Storage;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

[Collection(CloudSourceGroup.Name)]
[Trait("Category", "Integration")]
public sealed class BacpacProviderIntegrationTests(CloudSourceFixture fixture, ITestOutputHelper output)
{
    private static readonly string[] BackupFields = ["backupFile", "backupSha256", "backupUrl"];
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly SqlClientEngineConnector Connector = new();

    private EngineWorld World => fixture.World;

    [SqlPackageIntegrationFact]
    public async Task Detaches_login_mapped_users_from_local_and_url_packages()
    {
        var executable = SqlPackagePath();
        var kit = new BranchKit(fixture.Engine, output);
        await using var admin = await Connector.ConnectAsync(World.Endpoint, World.Password, None);
        var scratch = Directory.CreateTempSubdirectory("sqlbranch-it-login-build-");
        try
        {
            var (native, azure) = await BacpacLoginFixture.CreateAsync(World, admin, executable, scratch.FullName);

            // Give the target login its own SID, disabled state and role, independent of the export.
            var password = PasswordGenerator.Generate();
            World.Redactor.Register(password);
            await admin.ExecuteAsync($"""
                DROP LOGIN [report_reader];
                DROP LOGIN [sales_app];
                CREATE LOGIN [sales_app] WITH PASSWORD = {Quoting.Literal(password)};
                ALTER LOGIN [sales_app] DISABLE;
                ALTER SERVER ROLE [bacpac_spike_server_role] ADD MEMBER [sales_app];
                """, None);
            var serverBefore = await BacpacLoginFixture.ServerStateAsync(admin);

            const BackupUrlKind kind = BackupUrlKind.S3;
            var url = ObjectUrl(kind, "login-build/azure.bacpac");
            var azureBytes = await File.ReadAllBytesAsync(azure);
            await PutAsync(kind, "login-build/azure.bacpac", azureBytes);
            using var handler = fixture.Minio!.CreateHandler();
            var transport = ArchiveStores.CreateReadSource(
                (address, secrets, redactor) => ArchiveStores.Create(address, secrets, new StorageOptions { Handler = handler }, redactor),
                World.Redactor);

            var packages = new[]
            {
                (Path: native, Url: (BackupUrl?)null, Bytes: await File.ReadAllBytesAsync(native), Logins: 0, ServerObjects: 0),
                (Path: azure, Url: url, Bytes: azureBytes, Logins: 2, ServerObjects: 1),
            };
            var mode = World.IsPlain ? "copy" : "thin";
            foreach (var package in packages)
            {
                var basename = Path.GetFileName(package.Path);
                var image = "login-" + Path.GetFileNameWithoutExtension(basename);
                var branch = image + "-branch";
                var imported = image + "-imported";
                var archive = Path.Combine(scratch.FullName, image + ".sbimg");
                var digest = Convert.ToHexStringLower(SHA256.HashData(package.Bytes));
                try
                {
                    var args = BuildArgs(image, package.Url?.Url ?? package.Path, executable);
                    var build = package.Url is null
                        ? World.RunCli(args)
                        : World.RunCliWithStorage(_ => handler, null, null, null, args);
                    AssertNoSecrets(build.Out + build.Err);
                    output.WriteLine($"{image} build took {build.Elapsed.TotalSeconds:F1} s");
                    Assert.True(build.Exit == 0, build.Out + build.Err);
                    var result = BranchKit.Parse(build.Out);
                    Assert.Equal(["report_reader", "sales_app"], result.GetProperty("detachedUsers").EnumerateArray()
                        .Select(user => user.GetString()!).Order(StringComparer.Ordinal).ToArray());
                    Assert.Equal(package.Logins, result.GetProperty("removedLogins").GetInt32());
                    Assert.Equal(package.ServerObjects, result.GetProperty("removedServerObjects").GetInt32());
                    Assert.True(result.GetProperty("phaseMilliseconds").GetProperty("rewrite").GetInt64() >= 0);
                    Assert.Equal(package.Bytes.LongLength, result.GetProperty("sourceSizeBytes").GetInt64());
                    var builtImage = result.GetProperty("image");
                    Assert.Equal(mode, builtImage.GetProperty("cloneMode").GetString());
                    Assert.Equal(digest, builtImage.GetProperty("bacpacSha256").GetString());
                    Assert.Equal(package.Bytes, await File.ReadAllBytesAsync(package.Path));
                    if (package.Url is { } remote)
                    {
                        await using var stored = await OpenPackageAsync(transport, remote);
                        using var copy = new MemoryStream();
                        await stored.CopyToAsync(copy, None);
                        Assert.Equal(package.Bytes, copy.ToArray());
                    }

                    Assert.Equal(serverBefore, await BacpacLoginFixture.ServerStateAsync(admin));
                    var created = await kit.CreateAsync(branch, image);
                    Assert.Equal(mode, created.GetProperty("cloneMode").GetString());
                    (await BacpacLoginDatabaseState.ReadAsync(admin, branch, None)).AssertDetached();
                    Assert.Equal(serverBefore, await BacpacLoginFixture.ServerStateAsync(admin));

                    var export = World.RunCli("image", "export", image, "--to", archive);
                    Assert.True(export.Exit == 0, export.Out + export.Err);
                    var import = World.RunCli("image", "import", archive, "--as", imported);
                    Assert.True(import.Exit == 0, import.Out + import.Err);
                    var manifest = await ImageArchiveScenarios.AssertImportedManifestAsync(kit, image, imported);
                    Assert.Equal("bacpac", (string?)manifest["sourceType"]);
                    Assert.Equal(basename, (string?)manifest["bacpacFile"]);
                    Assert.Equal(digest, (string?)manifest["bacpacSha256"]);
                    Assert.Equal(package.Url?.Url, (string?)manifest["bacpacUrl"]);
                    kit.AssertStatusIsClean();
                }
                finally
                {
                    await kit.DropBranchAsync(branch);
                    World.RunCli("image", "rm", imported, "--force");
                    World.RunCli("image", "rm", image, "--force");
                }
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
            await admin.ExecuteAsync("""
                IF SUSER_ID(N'report_reader') IS NOT NULL DROP LOGIN [report_reader];
                IF SUSER_ID(N'sales_app') IS NOT NULL DROP LOGIN [sales_app];
                IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'bacpac_spike_server_role' AND type = 'R')
                    DROP SERVER ROLE [bacpac_spike_server_role];
                """, None);
        }
    }

    [IntegrationTheory]
    [InlineData(BackupUrlKind.S3)]
    [InlineData(BackupUrlKind.AzureBlob)]
    public async Task Builds_a_queryable_branch_and_reports_provider_failures(BackupUrlKind kind)
    {
        var executable = SqlPackagePath();
        var tag = kind == BackupUrlKind.S3 ? "s3-bacpac" : "blob-bacpac";
        var url = ObjectUrl(kind, tag + ".bacpac");
        var missing = ObjectUrl(kind, tag + "-missing.bacpac");
        var invalid = ObjectUrl(kind, tag + "-invalid.bacpac");
        var branch = tag + "-branch";
        var client = Directory.CreateTempSubdirectory("sqlbranch-it-provider-bacpac-");
        using var handler = kind == BackupUrlKind.S3 ? fixture.Minio!.CreateHandler() : fixture.Azurite!.CreateHandler();
        IArchiveStore Store(BackupUrl address, StorageSecrets secrets, SecretRedactor redactor) =>
            ArchiveStores.Create(address, secrets, new StorageOptions { Handler = handler }, redactor);
        var transport = ArchiveStores.CreateReadSource(Store, World.Redactor);
        var kit = new BranchKit(fixture.Engine, output);

        try
        {
            await using var admin = await Connector.ConnectAsync(World.Endpoint, World.Password, None);
            var package = Path.Combine(client.FullName, "source.bacpac");
            await SqlPackageFixture.ExportAsync(World, admin, executable, client.FullName, package, """
                CREATE TABLE dbo.Items (Id int NOT NULL PRIMARY KEY);
                INSERT dbo.Items SELECT TOP (17) ROW_NUMBER() OVER (ORDER BY object_id) FROM sys.all_objects;
                """);
            var bytes = await File.ReadAllBytesAsync(package);
            var invalidBytes = "not a BACPAC"u8.ToArray();
            await PutAsync(kind, tag + ".bacpac", bytes);
            await PutAsync(kind, tag + "-invalid.bacpac", invalidBytes);

            var success = Build(tag, url, World.Environment);
            Assert.True(success.Exit == 0, success.Out + success.Err);
            using var built = JsonDocument.Parse(success.Out);
            var image = built.RootElement.GetProperty("image");
            Assert.Equal(World.IsPlain ? "copy" : "thin", image.GetProperty("cloneMode").GetString());
            Assert.Equal(url.Url, image.GetProperty("bacpacUrl").GetString());
            Assert.All(BackupFields,
                field => Assert.Equal(JsonValueKind.Null, image.GetProperty(field).ValueKind));
            Assert.Equal(bytes.LongLength, built.RootElement.GetProperty("sourceSizeBytes").GetInt64());
            var digest = SHA256.HashData(bytes);
            Assert.Equal(Convert.ToHexStringLower(digest), image.GetProperty("bacpacSha256").GetString());

            await kit.CreateAsync(branch, tag);
            await using (var session = await kit.ConnectAsync(branch))
            {
                Assert.Equal(17, await BranchKit.CountAsync(session, "SELECT COUNT(*) FROM dbo.Items"));
            }

            var rejected = new Dictionary<string, string>(World.Environment);
            if (kind == BackupUrlKind.S3)
            {
                rejected[StorageSecrets.S3SecretVariable] = fixture.WrongS3Secret;
            }
            else
            {
                rejected[BackupUrl.SasVariable] = fixture.WrongSasToken;
            }

            AssertFailure(Build(tag + "-forbidden", url, rejected), "storage_unreachable", url);
            AssertFailure(Build(tag + "-missing", missing, World.Environment), "storage_unreachable", missing);
            AssertFailure(Build(tag + "-invalid", invalid, World.Environment), "bacpac_invalid");
            foreach (var suffix in new[] { "-forbidden", "-missing", "-invalid" })
            {
                await AssertUnpublishedAsync(tag + suffix);
            }

            Assert.Equal(digest, await DigestAsync(transport, url));
            Assert.Equal(SHA256.HashData(invalidBytes), await DigestAsync(transport, invalid));
            Assert.Empty(BacpacScratch.FindAbandoned((await World.FindPoolAsync()).Engine!.Id));
            Assert.Equal(0, World.RunCli("status").Exit);
        }
        finally
        {
            client.Delete(recursive: true);
            await kit.DropBranchAsync(branch);
            World.RunCli("image", "rm", tag, "--force");
        }

        (int Exit, string Out, string Err) Build(string image, BackupUrl source, IReadOnlyDictionary<string, string> settings)
        {
            var services = new CliServices(World.Runtime, Connector, settings, client.FullName,
                World.HomeDirectory, World.Paths, EngineTimings.Default, World.EngineName,
                PoolVolume: World.PoolVolume, StoreFactory: Store);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = CliApp.CreateDefault(World.Redactor, services).Run(BuildArgs(image, source.Url, executable), stdout, stderr);
            var combined = stdout.ToString() + stderr.ToString();
            AssertNoSecrets(combined);
            return (exit, stdout.ToString(), stderr.ToString());
        }
    }

    [IntegrationTheory]
    [InlineData(BackupUrlKind.S3)]
    [InlineData(BackupUrlKind.AzureBlob)]
    public async Task Ctrl_C_during_partial_download_preserves_the_concurrent_build_scratch(BackupUrlKind kind)
    {
        if (OperatingSystem.IsWindows() || !ImageArchiveScenarios.PerlAvailable)
        {
            throw new SkipTestException("The Ctrl+C case needs POSIX SIGINT and perl on PATH.");
        }

        var executable = SqlPackagePath();
        var image = kind == BackupUrlKind.S3 ? "s3-bacpac-cancel" : "blob-bacpac-cancel";
        var key = image + ".bacpac";
        var url = ObjectUrl(kind, key);
        await PutAsync(kind, key, new byte[64 * 1024]);

        var temp = Directory.CreateTempSubdirectory("sqlbranch-it-download-interrupt-");
        try
        {
            var ready = Path.Combine(temp.FullName, "get-ready");
            var (thumbprint, port) = kind == BackupUrlKind.S3
                ? (fixture.Minio!.CertificateThumbprint, fixture.Minio.HostPort)
                : (fixture.Azurite!.CertificateThumbprint, fixture.Azurite.HostPort);
            var overrides = new Dictionary<string, string>
            {
                ["TMPDIR"] = temp.FullName,
                ["SQLBRANCH_HARNESS_DOWNLOAD_READY"] = ready,
                ["SQLBRANCH_HARNESS_STORAGE_CERT"] = thumbprint,
                ["SQLBRANCH_HARNESS_STORAGE_PORT"] = port.ToString(CultureInfo.InvariantCulture),
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            using var build = World.StartCliInterruptible(overrides, BuildArgs(image, url.Url, executable));
            await WaitForPartialDownloadAsync(build, ready);
            var scratch = Assert.Single(temp.EnumerateDirectories("sqlbranch-bacpac-*"));
            var partial = new FileInfo(Path.Combine(scratch.FullName, "source.bacpac"));
            Assert.InRange(partial.Length, 1, 64 * 1024 - 1);

            var otherReady = Path.Combine(temp.FullName, "other-ready");
            overrides["SQLBRANCH_HARNESS_DOWNLOAD_READY"] = otherReady;
            using var other = World.StartCliInterruptible(overrides, BuildArgs(image + "-other", url.Url, executable));
            await WaitForPartialDownloadAsync(other, otherReady);
            var otherScratch = Assert.Single(temp.EnumerateDirectories("sqlbranch-bacpac-*"), d => d.Name != scratch.Name);
            var otherPartial = new FileInfo(Path.Combine(otherScratch.FullName, "source.bacpac"));
            var otherLength = otherPartial.Length;
            Assert.InRange(otherLength, 1, 64 * 1024 - 1);

            await InterruptAsync(build);
            Assert.False(Directory.Exists(scratch.FullName));
            Assert.False(other.HasExited);
            Assert.Equal(otherScratch.FullName, Assert.Single(temp.EnumerateDirectories()).FullName);
            otherPartial.Refresh();
            Assert.Equal(otherLength, otherPartial.Length);

            await InterruptAsync(other);
            Assert.Empty(temp.EnumerateDirectories());
            await AssertUnpublishedAsync(image);
            Assert.Equal(0, World.RunCli("status").Exit);

            async Task WaitForPartialDownloadAsync(CliProcess client, string marker)
            {
                while (!File.Exists(marker))
                {
                    if (client.HasExited)
                    {
                        var early = await client.WaitAsync(TimeSpan.FromSeconds(5));
                        AssertNoSecrets(early.Out + early.Err);
                        Assert.Fail("The build exited before the second body read. " + early.Out + early.Err);
                    }
                    await Task.Delay(50, deadline.Token);
                }
            }

            async Task InterruptAsync(CliProcess client)
            {
                client.Interrupt();
                var result = await client.WaitAsync(TimeSpan.FromMinutes(1));
                AssertNoSecrets(result.Out + result.Err);
                AssertFailure(result, "operation_failed");
                Assert.Contains("The BACPAC build was cancelled.", BranchKit.ErrorMessage(result.Out), StringComparison.Ordinal);
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static string SqlPackagePath() =>
        Environment.GetEnvironmentVariable("SQLBRANCH_IT_SQLPACKAGE") is { } executable && !string.IsNullOrWhiteSpace(executable)
            ? Path.GetFullPath(executable)
            : throw new SkipTestException("Set SQLBRANCH_IT_SQLPACKAGE to the installed SqlPackage executable.");

    private BackupUrl ObjectUrl(BackupUrlKind kind, string key) =>
        BackupUrl.Parse(kind == BackupUrlKind.S3 ? fixture.S3Url(key) : fixture.BlobUrl(key));

    private Task PutAsync(BackupUrlKind kind, string key, byte[] bytes) =>
        kind == BackupUrlKind.S3
            ? fixture.Minio!.PutAsync($"/{CloudSourceFixture.Bucket}/{key}", bytes)
            : fixture.Azurite!.PutBlobAsync(CloudSourceFixture.BlobContainer, key, bytes);

    private static string[] BuildArgs(string image, string source, string executable) =>
        ["image", "build", image, "--from-bacpac", source, "--sqlpackage", executable, "--format", "json"];

    private async Task AssertUnpublishedAsync(string image)
    {
        var check = await World.Runtime.ExecAsync(World.EngineName, ["test", "!", "-e", ImagePaths.Manifest(image)], None);
        Assert.True(check.Succeeded, "A failed build left a publication marker.");
    }

    private void AssertNoSecrets(string outputText)
    {
        foreach (var secret in new[] { fixture.S3KeyId, fixture.S3Secret, fixture.WrongS3Secret, fixture.SasToken,
                     fixture.SasSignature, fixture.WrongSasToken })
        {
            SqlPackageFixture.AssertSecretAbsent(outputText, secret);
            SqlPackageFixture.AssertSecretAbsent(outputText, Uri.EscapeDataString(secret));
        }

        Assert.True(World.Redactor.Redact(outputText) == outputText, "A registered credential appeared in CLI output.");
        Assert.DoesNotContain("sig=", outputText, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertFailure((int Exit, string Out, string Err) result, string code, BackupUrl? url = null)
    {
        Assert.Equal(1, result.Exit);
        Assert.Equal(code, BranchKit.ErrorCode(result.Out));
        if (url is not null)
        {
            var message = BranchKit.ErrorMessage(result.Out);
            Assert.Contains(url.Url, message, StringComparison.Ordinal);
            Assert.DoesNotContain('?', message);
        }
    }

    private async Task<byte[]> DigestAsync(IBacpacReadSource transport, BackupUrl url)
    {
        await using var stream = await OpenPackageAsync(transport, url);
        return await SHA256.HashDataAsync(stream, None);
    }

    private ValueTask<Stream> OpenPackageAsync(IBacpacReadSource transport, BackupUrl url) =>
        transport.GetAsync(url, StorageSecrets.Read(url, World.Environment, World.Redactor), None);
}
