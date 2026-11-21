using System.Net;
using SqlBranch.Cli;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Storage;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Storage;

[Collection(StorageSdkSerial.Name)]
public sealed class BacpacReadTransportTests
{
    private const string S3Url = "s3://s3.amazonaws.com/images/shop.bacpac";
    private const string BlobUrl = "https://acme.blob.core.windows.net/images/shop.bacpac";
    private const string Key = "images/shop.bacpac";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reads_size_and_bytes_through_the_archive_store(bool azure)
    {
        using var handler = new FakeStorageService(azure ? StorageKind.Azure : StorageKind.S3);
        var bytes = "sample BACPAC body"u8.ToArray();
        handler.Objects[Key] = bytes;
        handler.ExpectedSasToken = StorageScenario.Sas;
        handler.ExpectedAccessKeyId = StorageScenario.KeyId;

        var (url, secrets, services, redactor) = Source(azure, handler);
        var source = services.BacpacReadSourceOf(redactor);
        Assert.Equal(bytes.LongLength, await source.HeadAsync(url, secrets, CancellationToken.None));
        await using var input = await source.GetAsync(url, secrets, CancellationToken.None);
        using var body = new MemoryStream();
        await input.CopyToAsync(body, CancellationToken.None);
        Assert.Equal(bytes, body.ToArray());

        Assert.Collection(handler.Requests,
            head => Assert.Equal("HEAD", head.Method),
            get => Assert.Equal("GET", get.Method));
        Assert.All(handler.Requests, request => Assert.Equal("https", request.Uri.Scheme));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.MovedPermanently)]
    [InlineData(false, HttpStatusCode.TemporaryRedirect)]
    [InlineData(true, HttpStatusCode.TemporaryRedirect)]
    public async Task Rejects_redirects_before_the_sdk_can_forward_credentials(bool azure, HttpStatusCode status)
    {
        using var handler = new RedirectHandler(status);
        var (url, secrets, services, redactor) = Source(azure, handler);
        var source = services.BacpacReadSourceOf(redactor);
        var error = await Assert.ThrowsAsync<SqlBranchException>(
            () => source.HeadAsync(url, secrets, CancellationToken.None).AsTask());
        Assert.Equal(ErrorCodes.StorageUnreachable, error.Code);
        Assert.Contains(url.Url, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(!azure, error.Message.Contains("Check --s3-region.", StringComparison.Ordinal));
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request => Assert.Equal(new Uri(url.Url).Host, request.Host));
        using var scratch = new BacpacScratch("l" + Guid.NewGuid().ToString("N"), "test-engine");
        var downloadError = await Assert.ThrowsAsync<SqlBranchException>(() =>
            BacpacDownload.StageAsync(source, url, secrets, scratch, CancellationToken.None));
        Assert.Equal(ErrorCodes.StorageUnreachable, downloadError.Code);
        Assert.Equal(!azure, downloadError.Message.Contains("Check --s3-region.", StringComparison.Ordinal));
        Assert.All(handler.Requests, request => Assert.Equal(new Uri(url.Url).Host, request.Host));
    }

    private static (BackupUrl Url, StorageSecrets Secrets, CliServices Services, SecretRedactor Redactor) Source(
        bool azure, HttpMessageHandler handler)
    {
        var redactor = new SecretRedactor();
        var url = BackupUrl.Parse(azure ? BlobUrl : S3Url);
        var secrets = StorageSecrets.Read(url, new Dictionary<string, string>
        {
            [StorageSecrets.S3KeyIdVariable] = StorageScenario.KeyId,
            [StorageSecrets.S3SecretVariable] = StorageScenario.Secret,
            [BackupUrl.SasVariable] = StorageScenario.Sas,
        }, redactor);
        var services = CliServices.Real(redactor) with
        {
            StoreFactory = (address, credentials, scrubber) =>
                ArchiveStores.Create(address, credentials, new StorageOptions { Handler = handler, RetryDelay = TimeSpan.Zero }, scrubber),
        };
        return (url, secrets, services, redactor);
    }

    private sealed class RedirectHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://redirect.invalid/other.bacpac?sig=secret");
            return Task.FromResult(response);
        }
    }
}
