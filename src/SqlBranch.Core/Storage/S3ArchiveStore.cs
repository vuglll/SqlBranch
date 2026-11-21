using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Storage;

/// <summary>
/// An archive in S3-compatible storage (design D5). The client is built only from the access key and secret SqlBranch read from its own
/// variables, an explicit region and an explicit endpoint, so no profile, environment variable or instance metadata of AWS is read, and
/// SDK logging is off. Upload is a multipart upload that is committed with <c>If-None-Match: *</c> unless <c>--force</c> was given, and
/// aborted on every failure and on cancellation.
/// </summary>
public sealed class S3ArchiveStore : IArchiveStore, IDisposable
{
    /// <summary>SDK retries of a failed request. Parts are in-memory (seekable), and complete and abort are idempotent.</summary>
    internal const int MaxRetries = 2;

    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly string _key;
    private readonly StorageErrors _errors;
    private readonly int? _partSizeOverride;

    /// <param name="url">A parsed <c>s3://</c> URL.</param>
    /// <param name="accessKeyId">The access key ID read from <c>SQLBRANCH_S3_ACCESS_KEY_ID</c>.</param>
    /// <param name="secretAccessKey">The secret read from <c>SQLBRANCH_S3_SECRET_ACCESS_KEY</c>.</param>
    /// <param name="options">The HTTP handler tests give.</param>
    /// <param name="redactor">Every message passes through it.</param>
    public S3ArchiveStore(BackupUrl url, string accessKeyId, string secretAccessKey, StorageOptions options, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redactor);
        if (url.Kind != BackupUrlKind.S3)
        {
            throw new ArgumentException("An s3:// URL is needed.", nameof(url));
        }

        DisplayUrl = url.Url;
        _partSizeOverride = options.PartSize;
        _errors = new StorageErrors(url.Url, redactor);

        var rest = url.Url["s3://".Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = rest[..slash];
        var segments = rest[(slash + 1)..].Split('/').Select(Uri.UnescapeDataString).ToArray();
        var hostOnly = authority.Contains(':', StringComparison.Ordinal) ? authority[..authority.IndexOf(':', StringComparison.Ordinal)] : authority;
        var virtualHost = BackupUrl.VirtualHostPattern().IsMatch(hostOnly);

        string endpoint;
        if (virtualHost)
        {
            // https://<bucket>.s3.<region>.amazonaws.com/<key>: the bucket is the first label of the host.
            var dot = authority.IndexOf('.', StringComparison.Ordinal);
            _bucket = authority[..dot];
            endpoint = authority[(dot + 1)..];
            _key = string.Join('/', segments);
        }
        else
        {
            _bucket = segments[0];
            endpoint = authority;
            _key = string.Join('/', segments.Skip(1));
        }

        var config = new AmazonS3Config
        {
            ServiceURL = $"https://{endpoint}",
            ForcePathStyle = !virtualHost,
            AuthenticationRegion = url.S3Region ?? BackupUrl.DefaultS3Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            // Two retries: the SDK re-sends a part, a completion or an abort, and each is idempotent with a seekable body.
            MaxErrorRetry = MaxRetries,
            DisableLogging = true,
            LogResponse = false,
            LogMetrics = false,
            Timeout = TimeSpan.FromMinutes(30),
        };
        config.HttpClientFactory = new HandlerClientFactory(options.Handler);

        _client = new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), config);
    }

    public string DisplayUrl { get; }

    public void Dispose() => _client.Dispose();

    public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            throw Map(ex, "check whether the archive exists");
        }
    }

    private Task<GetObjectMetadataResponse> GetMetadataAsync(CancellationToken cancellationToken) =>
        _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = _key }, cancellationToken);

    public async Task<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            return metadata.ContentLength;
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            throw Map(ex, "read the object metadata");
        }
    }

    public async Task<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = _key }, cancellationToken).ConfigureAwait(false);
            return new StorageReadStream(response.ResponseStream, response, ex => Map(ex, "read the archive"));
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            throw Map(ex, "read the archive");
        }
    }

    public async Task<IArchiveUpload> BeginUploadAsync(bool failIfExists, long expectedBytes, CancellationToken cancellationToken)
    {
        try
        {
            var started = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = _bucket, Key = _key }, cancellationToken).ConfigureAwait(false);
            return new Upload(this, started.UploadId, failIfExists, _partSizeOverride ?? PartSizing.For(expectedBytes));
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            throw Map(ex, "start the upload");
        }
    }

    private static bool IsFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is not SqlBranchException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested);

    /// <summary>412 <c>PreconditionFailed</c>, or 409 <c>ConditionalRequestConflict</c> (a concurrent write to the key) on a conditional complete.</summary>
    private static bool IsExistsRefusal(AmazonServiceException ex) =>
        ex.StatusCode == HttpStatusCode.PreconditionFailed
        || (ex.StatusCode == HttpStatusCode.Conflict && string.Equals(ex.ErrorCode, "ConditionalRequestConflict", StringComparison.Ordinal));

    private SqlBranchException Map(Exception ex, string doing) => ex switch
    {
        SqlBranchException known => known,
        _ when StorageHttp.FindRefusedRedirect(ex) is { } redirect => _errors.S3Redirect(doing, redirect),
        AmazonServiceException service => _errors.Unreachable(ex, doing, (int)service.StatusCode, service.ErrorCode),
        _ => _errors.Unreachable(ex, doing),
    };

    private sealed class Upload(S3ArchiveStore store, string uploadId, bool failIfExists, int partSize) : IArchiveUpload
    {
        private readonly List<PartETag> _parts = [];
        private bool _done;

        public int PartSize { get; } = partSize;

        public async Task UploadPartAsync(ReadOnlyMemory<byte> part, CancellationToken cancellationToken)
        {
            var number = _parts.Count + 1;
            try
            {
                using var body = new MemoryStream(part.ToArray(), writable: false);
                var response = await store._client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = store._bucket,
                    Key = store._key,
                    UploadId = uploadId,
                    PartNumber = number,
                    PartSize = part.Length,
                    InputStream = body,
                }, cancellationToken).ConfigureAwait(false);
                _parts.Add(new PartETag(number, response.ETag));
            }
            catch (Exception ex) when (IsFailure(ex, cancellationToken))
            {
                throw store.Map(ex, "upload a part of the archive");
            }
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            try
            {
                await store._client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = store._bucket,
                    Key = store._key,
                    UploadId = uploadId,
                    PartETags = _parts,
                    IfNoneMatch = failIfExists ? "*" : null,
                }, cancellationToken).ConfigureAwait(false);
                _done = true;
            }
            catch (AmazonServiceException ex) when (failIfExists && IsExistsRefusal(ex))
            {
                throw store._errors.DestinationExists();
            }
            catch (Exception ex) when (IsFailure(ex, cancellationToken))
            {
                throw store.Map(ex, "finish the upload");
            }
        }

        public async Task AbortAsync()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await store._client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = store._bucket, Key = store._key, UploadId = uploadId }, limit.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The export already failed; an upload that could not be aborted is left for the bucket's lifecycle rule, and the docs say so.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        public async ValueTask DisposeAsync() => await AbortAsync().ConfigureAwait(false);
    }

    private sealed class HandlerClientFactory(HttpMessageHandler? handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => StorageHttp.CreateClient(handler);
    }
}
