using System.Net;
using System.Text;
using System.Web;
using System.Xml.Linq;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>Which storage protocol the fake speaks.</summary>
internal enum StorageKind
{
    S3,
    Azure,
}

/// <summary>One request the fake storage service received.</summary>
internal sealed record SeenRequest(int Index, string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, long BodyBytes, byte[] Body)
{
    public string Query => Uri.Query;

    public string Authorization => Headers.GetValueOrDefault("Authorization", string.Empty);

    /// <summary>Everything a request shows to the service: the URL, every header and a text view of the body.</summary>
    public string Everything => $"{Method} {Uri} {string.Join(' ', Headers.Select(h => $"{h.Key}:{h.Value}"))} {Encoding.UTF8.GetString(Body.Length > 20_000 ? Body[..20_000] : Body)}";
}

/// <summary>
/// A storage service with no network: an HTTP handler that behaves as much of S3 (multipart upload, <c>If-None-Match</c>, abort, GET)
/// or Azure Blob Storage (stage block, commit block list with the conditional header, GET) as the clients need. It records every
/// request, can fail any of them by number, and can echo the request URL and a secret in its errors as a careless service would.
/// </summary>
internal sealed class FakeStorageService : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly StorageKind _kind;
    private readonly List<SeenRequest> _requests = [];
    private readonly Dictionary<string, Dictionary<int, byte[]>> _s3Parts = [];
    private readonly Dictionary<string, string> _s3UploadKeys = [];
    private readonly Dictionary<string, byte[]> _azureBlocks = [];
    private int _uploads;

    public FakeStorageService(StorageKind kind)
    {
        _kind = kind;
    }

    /// <summary>The objects by <c>bucket/key</c> or <c>container/blob</c> (for Azure with an account in the path, the whole path).</summary>
    public Dictionary<string, byte[]> Objects { get; } = [];

    /// <summary>The S3 access key ID the service accepts, or null to accept any.</summary>
    public string? ExpectedAccessKeyId { get; set; }

    /// <summary>The SAS token the service accepts (each pair must be in the query), or null to accept any.</summary>
    public string? ExpectedSasToken { get; set; }

    /// <summary>Whether the service honours <c>If-None-Match: *</c> on the commit.</summary>
    public bool HonoursIfNoneMatch { get; set; } = true;

    /// <summary>When true, a refused conditional S3 complete answers 409 <c>ConditionalRequestConflict</c> (a concurrent write) instead of 412.</summary>
    public bool S3ConflictInsteadOfPrecondition { get; set; }

    /// <summary>The status Azure answers a refused conditional commit with: 409 as Azurite, or 412.</summary>
    public int AzureConflictStatus { get; set; } = 409;

    /// <summary>Fails the request (with 500) when this returns true. The request is already recorded.</summary>
    public Func<SeenRequest, bool>? Fail { get; set; }

    /// <summary>Runs when a request arrives, before it is answered; a test uses it to make another client write an object mid-upload.</summary>
    public Action<SeenRequest>? OnRequest { get; set; }

    /// <summary>The text an error body carries besides the request URL, as a careless service echoes it.</summary>
    public string? EchoSecret { get; set; }

    /// <summary>When set, a GET body ends with a connection error after this many bytes.</summary>
    public int? CutBodyAt { get; set; }

    /// <summary>When true every request fails with a connection error.</summary>
    public bool Down { get; set; }

    /// <summary>S3 multipart uploads that were started and neither completed nor aborted.</summary>
    public List<string> OpenUploads { get; } = [];

    /// <summary>S3 uploads that were aborted.</summary>
    public List<string> Aborted { get; } = [];

    /// <summary>The block IDs of each commit, in order.</summary>
    public List<IReadOnlyList<string>> Commits { get; } = [];

    /// <summary>The sizes of every uploaded part or block, in order.</summary>
    public List<int> PartSizes { get; } = [];

    public IReadOnlyList<SeenRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The whole path of the object a URL names, without a leading slash.</summary>
    private static string KeyOf(Uri uri) => uri.AbsolutePath.TrimStart('/');

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (request.Content?.Headers.ContentEncoding.Contains("aws-chunked") == true)
        {
            body = DecodeAwsChunked(body);
        }

        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }
        }

        SeenRequest seen;
        lock (_gate)
        {
            seen = new SeenRequest(_requests.Count, request.Method.Method, request.RequestUri!, headers, body.Length, body);
            _requests.Add(seen);
        }

        if (Down)
        {
            throw new HttpRequestException("Connection refused");
        }

        OnRequest?.Invoke(seen);
        if (Fail?.Invoke(seen) == true)
        {
            return Error(HttpStatusCode.InternalServerError, "InternalError", seen);
        }

        lock (_gate)
        {
            return _kind == StorageKind.S3 ? HandleS3(seen) : HandleAzure(seen);
        }
    }

    /// <summary>Reads the body of a signed streaming upload (<c>aws-chunked</c>), as every S3 server does: <c>size;chunk-signature=...\r\n data \r\n</c> repeated, ending with size 0.</summary>
    private static byte[] DecodeAwsChunked(byte[] body)
    {
        using var output = new MemoryStream();
        var position = 0;
        while (position < body.Length)
        {
            var lineEnd = Array.IndexOf(body, (byte)'\r', position);
            var header = Encoding.ASCII.GetString(body, position, lineEnd - position);
            var size = Convert.ToInt32(header.Split(';')[0], 16);
            position = lineEnd + 2;
            if (size == 0)
            {
                break;
            }

            output.Write(body, position, size);
            position += size + 2;
        }

        return output.ToArray();
    }

    private HttpResponseMessage Error(HttpStatusCode status, string code, SeenRequest seen, bool noBody = false)
    {
        var message = $"Request {seen.Uri} failed.{(EchoSecret is null ? string.Empty : " Secret " + EchoSecret)}";
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "00000000-0000-0000-0000-000000000001");
        if (!noBody)
        {
            response.Content = new StringContent(
                _kind == StorageKind.S3
                    ? $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{System.Security.SecurityElement.Escape(message)}</Message><RequestId>1</RequestId></Error>"
                    : $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{System.Security.SecurityElement.Escape(message)}</Message></Error>",
                Encoding.UTF8, "application/xml");
        }

        return response;
    }

    // ---- S3 ----

    private HttpResponseMessage HandleS3(SeenRequest seen)
    {
        if (ExpectedAccessKeyId is { } id && !seen.Authorization.Contains($"Credential={id}/", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.Forbidden, "InvalidAccessKeyId", seen, noBody: seen.Method == "HEAD");
        }

        var query = HttpUtility.ParseQueryString(seen.Query);
        var host = seen.Uri.Host;
        var path = KeyOf(seen.Uri);
        // Virtual-host style: the bucket is the first label of the host.
        var key = host.Contains(".s3", StringComparison.Ordinal) && host.EndsWith("amazonaws.com", StringComparison.Ordinal)
            ? host[..host.IndexOf('.', StringComparison.Ordinal)] + "/" + path
            : path;
        var uploadId = query["uploadId"];

        switch (seen.Method)
        {
            case "HEAD":
                return Objects.TryGetValue(key, out var head) ? Ok(head, withBody: false) : Error(HttpStatusCode.NotFound, "NotFound", seen, noBody: true);
            case "GET":
                return Objects.TryGetValue(key, out var found) ? Ok(found, withBody: true) : Error(HttpStatusCode.NotFound, "NoSuchKey", seen);
            case "POST" when seen.Query.TrimStart('?').Split('&').Any(p => p == "uploads" || p.StartsWith("uploads=", StringComparison.Ordinal)):
            {
                var id2 = $"upload-{++_uploads}";
                _s3Parts[id2] = [];
                _s3UploadKeys[id2] = key;
                OpenUploads.Add(id2);
                return Xml($"<InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>b</Bucket><Key>k</Key><UploadId>{id2}</UploadId></InitiateMultipartUploadResult>");
            }

            case "PUT" when uploadId is not null && query["partNumber"] is { } number:
            {
                if (!_s3Parts.TryGetValue(uploadId, out var parts))
                {
                    return Error(HttpStatusCode.NotFound, "NoSuchUpload", seen);
                }

                parts[int.Parse(number, System.Globalization.CultureInfo.InvariantCulture)] = seen.Body;
                PartSizes.Add(seen.Body.Length);
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("ETag", $"\"part-{number}\"");
                return response;
            }

            case "POST" when uploadId is not null:
            {
                if (!_s3Parts.TryGetValue(uploadId, out var parts))
                {
                    return Error(HttpStatusCode.NotFound, "NoSuchUpload", seen);
                }

                var conditional = seen.Headers.GetValueOrDefault("If-None-Match") == "*";
                if (conditional && HonoursIfNoneMatch && Objects.ContainsKey(key))
                {
                    return S3ConflictInsteadOfPrecondition
                        ? Error(HttpStatusCode.Conflict, "ConditionalRequestConflict", seen)
                        : Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed", seen);
                }

                var listed = XDocument.Parse(Encoding.UTF8.GetString(seen.Body).TrimStart((char)0xFEFF)).Descendants().Where(e => e.Name.LocalName == "PartNumber")
                    .Select(e => int.Parse(e.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                Objects[key] = [.. listed.SelectMany(n => parts[n])];
                _s3Parts.Remove(uploadId);
                OpenUploads.Remove(uploadId);
                return Xml("<CompleteMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Location>l</Location><Bucket>b</Bucket><Key>k</Key><ETag>\"all\"</ETag></CompleteMultipartUploadResult>");
            }

            case "DELETE" when uploadId is not null:
                _s3Parts.Remove(uploadId);
                OpenUploads.Remove(uploadId);
                Aborted.Add(uploadId);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            default:
                return Error(HttpStatusCode.BadRequest, "InvalidRequest", seen);
        }
    }

    // ---- Azure ----

    private HttpResponseMessage HandleAzure(SeenRequest seen)
    {
        if (ExpectedSasToken is { } sas)
        {
            var query = seen.Query.TrimStart('?');
            var pairs = query.Split('&');
            if (!sas.Split('&').All(p => pairs.Contains(p, StringComparer.Ordinal)))
            {
                return Error(HttpStatusCode.Forbidden, "AuthenticationFailed", seen, noBody: seen.Method == "HEAD");
            }
        }

        var parameters = HttpUtility.ParseQueryString(seen.Query);
        var key = KeyOf(seen.Uri);
        switch (seen.Method)
        {
            case "HEAD":
                return Objects.TryGetValue(key, out var head) ? Ok(head, withBody: false) : Error(HttpStatusCode.NotFound, "BlobNotFound", seen, noBody: true);
            case "GET":
                return Objects.TryGetValue(key, out var found) ? Ok(found, withBody: true) : Error(HttpStatusCode.NotFound, "BlobNotFound", seen);
            case "PUT" when parameters["comp"] == "block":
                _azureBlocks[key + "|" + parameters["blockid"]] = seen.Body;
                PartSizes.Add(seen.Body.Length);
                return new HttpResponseMessage(HttpStatusCode.Created);
            case "PUT" when parameters["comp"] == "blocklist":
            {
                var conditional = seen.Headers.GetValueOrDefault("If-None-Match") == "*";
                if (conditional && HonoursIfNoneMatch && Objects.ContainsKey(key))
                {
                    var status = (HttpStatusCode)AzureConflictStatus;
                    return Error(status, status == HttpStatusCode.Conflict ? "BlobAlreadyExists" : "ConditionNotMet", seen);
                }

                var ids = XDocument.Parse(Encoding.UTF8.GetString(seen.Body).TrimStart((char)0xFEFF)).Root!.Elements().Select(e => e.Value).ToList();
                if (ids.Any(i => !_azureBlocks.ContainsKey(key + "|" + i)))
                {
                    return Error(HttpStatusCode.BadRequest, "InvalidBlockList", seen);
                }

                Commits.Add(ids);
                Objects[key] = [.. ids.SelectMany(i => _azureBlocks[key + "|" + i])];
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            default:
                return Error(HttpStatusCode.BadRequest, "InvalidRequest", seen);
        }
    }

    private HttpResponseMessage Ok(byte[] content, bool withBody)
    {
        HttpContent body = new ByteArrayContent([]);
        if (withBody)
        {
            body = CutBodyAt is { } cut ? new StreamContent(new CutStream(content, cut)) : new ByteArrayContent(content);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        response.Content.Headers.ContentLength = content.Length;
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        // An S3 ETag is the MD5 of the content in hex, and the client checks the download against it.
#pragma warning disable CA5351 // The protocol's own definition of an ETag, not a security use.
        response.Headers.TryAddWithoutValidation("ETag", $"\"{Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(content))}\"");
#pragma warning restore CA5351
        response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "00000000-0000-0000-0000-000000000001");
        return response;
    }

    private static HttpResponseMessage Xml(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml, Encoding.UTF8, "application/xml") };

    /// <summary>A body that fails after a number of bytes, as a dropped connection does.</summary>
    private sealed class CutStream(byte[] bytes, int cutAt) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= cutAt)
            {
                throw new IOException("The connection was reset.");
            }

            var n = Math.Min(Math.Min(buffer.Length, 16384), cutAt - _position);
            bytes.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
