using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Storage;

/// <summary>
/// An archive in Azure Blob Storage (design D5). The client gets only an <see cref="AzureSasCredential"/> made from the token SqlBranch
/// read from its own variable, so no default credential, environment or managed identity is tried; SDK logging and telemetry are off.
/// Upload stages 64 MiB blocks and commits them once. The block IDs hold a random prefix of this export, so two exports to one blob never
/// commit each other's blocks. Without <c>--force</c> the commit carries <c>If-None-Match: *</c>.
/// </summary>
public sealed class AzureArchiveStore : IArchiveStore
{
    private const int PrefixBytes = 16;

    private readonly BlockBlobClient _blob;
    private readonly StorageErrors _errors;
    private readonly int? _partSizeOverride;

    /// <param name="url">A parsed <c>https://</c> blob URL.</param>
    /// <param name="sasToken">The SAS token read from <c>SQLBRANCH_AZURE_SAS_TOKEN</c>, without a leading <c>?</c>.</param>
    /// <param name="options">The HTTP handler tests give.</param>
    /// <param name="redactor">Every message passes through it.</param>
    public AzureArchiveStore(BackupUrl url, string sasToken, StorageOptions options, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redactor);
        if (url.Kind != BackupUrlKind.AzureBlob)
        {
            throw new ArgumentException("An https:// blob URL is needed.", nameof(url));
        }

        DisplayUrl = url.Url;
        _partSizeOverride = options.PartSize;
        _errors = new StorageErrors(url.Url, redactor);

        var client = new BlobClientOptions
        {
            Diagnostics =
            {
                IsLoggingEnabled = false,
                IsLoggingContentEnabled = false,
                IsTelemetryEnabled = false,
                IsDistributedTracingEnabled = false,
            },
        };
        // Two retries: StageBlock (a seekable body) and CommitBlockList (same block IDs) are idempotent.
        client.Retry.MaxRetries = S3ArchiveStore.MaxRetries;
        client.Retry.Mode = RetryMode.Exponential;
        client.Retry.Delay = options.RetryDelay ?? TimeSpan.FromMilliseconds(800);
        client.Retry.MaxDelay = options.RetryDelay is { } fixedDelay ? fixedDelay : TimeSpan.FromSeconds(10);
        client.Retry.NetworkTimeout = TimeSpan.FromMinutes(30);
        client.Transport = new HttpClientTransport(options.Handler is { } handler ? StorageHttp.CreateClient(handler) : StorageHttp.SharedClient);

        _blob = new BlockBlobClient(new Uri(url.Url), new AzureSasCredential(sasToken), client);
    }

    public async Task<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return metadata.Value.ContentLength;
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            throw Map(ex, "read the object metadata");
        }
    }

    public string DisplayUrl { get; }

    public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _blob.ExistsAsync(cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            throw Map(ex, "check whether the archive exists");
        }
    }

    public async Task<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _blob.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new StorageReadStream(response.Value.Content, null, ex => Map(ex, "read the archive"));
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            throw Map(ex, "read the archive");
        }
    }

    public Task<IArchiveUpload> BeginUploadAsync(bool failIfExists, long expectedBytes, CancellationToken cancellationToken) =>
        Task.FromResult<IArchiveUpload>(new Upload(this, failIfExists));

    private static bool IsFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is not SqlBranchException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private SqlBranchException Map(Exception ex, string doing) => ex switch
    {
        SqlBranchException known => known,
        RequestFailedException service => _errors.Unreachable(ex, doing, service.Status, service.ErrorCode),
        _ => _errors.Unreachable(ex, doing),
    };

    /// <summary>The block ID of block <paramref name="index"/>: a random prefix of this export and the index, all of one length.</summary>
    internal static string BlockId(byte[] prefix, int index) =>
        Convert.ToBase64String([.. prefix, .. Encoding.ASCII.GetBytes(index.ToString("D6", CultureInfo.InvariantCulture))]);

    private sealed class Upload(AzureArchiveStore store, bool failIfExists) : IArchiveUpload
    {
        private readonly byte[] _prefix = RandomNumberGenerator.GetBytes(PrefixBytes);
        private readonly List<string> _ids = [];

        public int PartSize => store._partSizeOverride ?? PartSizing.DefaultPartSize;

        public async Task UploadPartAsync(ReadOnlyMemory<byte> part, CancellationToken cancellationToken)
        {
            var id = BlockId(_prefix, _ids.Count);
            try
            {
                using var body = new MemoryStream(part.ToArray(), writable: false);
                await store._blob.StageBlockAsync(id, body, cancellationToken: cancellationToken).ConfigureAwait(false);
                _ids.Add(id);
            }
            catch (Exception ex) when (IsFailure(ex, cancellationToken))
            {
                throw store.Map(ex, "upload a block of the archive");
            }
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            try
            {
                var options = new CommitBlockListOptions();
                if (failIfExists)
                {
                    options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
                }

                await store._blob.CommitBlockListAsync(_ids, options, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (failIfExists && ex.Status is 409 or 412)
            {
                // Azure answers an If-None-Match: * commit onto an existing blob with 412, or, as Azurite does, 409 BlobAlreadyExists.
                throw store._errors.DestinationExists();
            }
            catch (Exception ex) when (IsFailure(ex, cancellationToken))
            {
                throw store.Map(ex, "finish the upload");
            }
        }

        /// <summary>Azure has no call that drops staged blocks of a blob never committed; the service removes them after a week.</summary>
        public Task AbortAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
