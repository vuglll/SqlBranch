using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// A throw-away certificate authority for one test run (design D10). The test servers get a certificate it signed,
/// and only the SqlBranch test engine is told to trust it. Nothing here is written outside the run's own folder.
/// </summary>
public sealed class TestPki : IDisposable
{
    private readonly RSA _caKey = RSA.Create(2048);
    private readonly X509Certificate2 _ca;

    public TestPki(string name)
    {
        var request = new CertificateRequest($"CN={name}", _caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        _ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }

    public string CaPem => _ca.ExportCertificatePem();

    /// <summary>A server certificate for the given addresses and names, as PEM texts and its thumbprint.</summary>
    public (string CertPem, string KeyPem, string Thumbprint) Issue(string commonName, IEnumerable<IPAddress> addresses, IEnumerable<string> names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_ca, true, false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var address in addresses)
        {
            san.AddIpAddress(address);
        }

        foreach (var name in names)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7f;
        using var certificate = request.Create(_ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), serial);
        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), certificate.Thumbprint);
    }

    public void Dispose()
    {
        _ca.Dispose();
        _caKey.Dispose();
    }
}

/// <summary>
/// The docker calls of the fixtures. Every container a fixture makes carries the label <see cref="Label"/> with the run's id,
/// and is removed only after that label is read back, so no other container can be touched by name or by mistake.
/// </summary>
public static class FixtureDocker
{
    public const string Label = "sqlbranch-it-fixture";

    public static async Task<string> RunAsync(params string[] args)
    {
        var result = await EngineWorld.DockerAsync(args);
        // Only the first word of the command and docker's own text are reported: the arguments can hold the test keys.
        Assert.True(result.ExitCode == 0, $"docker {args[0]} failed: {result.StdErr}");
        return result.StdOut.Trim();
    }

    public static async Task<string> CreateAsync(string name, string runId, string image, string[] options, string[] command) =>
        await RunAsync(["create", "--name", name, "--label", $"{Label}={runId}", .. options, image, .. command]);

    public static async Task<string> BridgeAddressAsync(string name)
    {
        var ip = await RunAsync("inspect", "-f", "{{.NetworkSettings.Networks.bridge.IPAddress}}", name);
        Assert.NotEqual(string.Empty, ip);
        return ip;
    }

    public static async Task<int> HostPortAsync(string name, int containerPort)
    {
        var mapping = await RunAsync("port", name, containerPort.ToString(CultureInfo.InvariantCulture));
        return int.Parse(mapping.Split('\n')[0].Split(':')[^1], CultureInfo.InvariantCulture);
    }

    /// <summary>Copies a folder's files into a folder of the container, which is made if it is not there.</summary>
    public static async Task CopyFilesInAsync(string localFolder, string name, string containerFolder)
    {
        // The files keep their owner and mode inside the container, where the server runs as another user: make them readable.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(localFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            foreach (var file in Directory.EnumerateFiles(localFolder))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        await RunAsync("cp", Path.Combine(localFolder, "."), $"{name}:{containerFolder}");
    }

    /// <summary>Removes the container (and its anonymous volume) when it carries this run's label.</summary>
    public static async Task RemoveOwnAsync(string name, string runId)
    {
        var label = await EngineWorld.DockerAsync("inspect", "-f", "{{index .Config.Labels \"" + Label + "\"}}", name);
        if (label.ExitCode == 0 && label.StdOut.Trim() == runId)
        {
            await EngineWorld.DockerAsync("rm", "-f", "-v", name);
        }
    }

    public static string RandomText(int length) =>
        RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789", length);

    public static HttpClient ClientTrusting(string thumbprint) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => string.Equals(certificate?.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase),
    })
    { Timeout = TimeSpan.FromMinutes(5) };

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    public static byte[] Hmac(byte[] key, string text) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// A MinIO server with the test CA's certificate for its bridge address, on the default bridge next to the test engine.
/// The test keys are in this container's settings only: the stated test-only exception to "where storage secrets may appear".
/// </summary>
public sealed class MinioServer : IAsyncDisposable
{
    private const string DefaultImage = "cgr.dev/chainguard/minio@sha256:16b824a0016ffad3dfa8b059755f83800aa42ca68fe86f1782cabd0c5a16d322";

    private readonly string _name;
    private readonly string _runId;
    private readonly string _thumbprint;
    private readonly string _keyId;
    private readonly string _secret;

    private MinioServer(string name, string runId, string address, int hostPort, string thumbprint, string keyId, string secret)
    {
        _name = name;
        _runId = runId;
        Address = address;
        HostPort = hostPort;
        _thumbprint = thumbprint;
        _keyId = keyId;
        _secret = secret;
    }

    /// <summary>The pinned image; <c>SQLBRANCH_IT_MINIO_IMAGE</c> replaces it when the pinned one can no longer be pulled.</summary>
    public static string Image =>
        Environment.GetEnvironmentVariable("SQLBRANCH_IT_MINIO_IMAGE") is { Length: > 0 } image ? image : DefaultImage;

    /// <summary>The address on the default bridge: what the engine reaches and what the certificate names.</summary>
    public string Address { get; private set; }

    public int HostPort { get; private set; }

    public static async Task<MinioServer> StartAsync(string role, string runId, TestPki pki, string keyId, string secret)
    {
        var name = $"sqlbranch-it-minio-{role}-{runId}";
        await FixtureDocker.CreateAsync(name, runId, Image,
            ["-v", "/data", "-p", "127.0.0.1::9000", "-e", $"MINIO_ROOT_USER={keyId}", "-e", $"MINIO_ROOT_PASSWORD={secret}"],
            ["server", "/data", "--certs-dir", "/certs"]);
        try
        {
            await FixtureDocker.RunAsync("start", name);

            // The address is known only once the container runs, and the certificate must name it: start, write the
            // certificate, restart. The address is read again after the restart and the certificate redone if it moved.
            var certified = string.Empty;
            var thumbprint = string.Empty;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var address = await FixtureDocker.BridgeAddressAsync(name);
                if (address == certified)
                {
                    break;
                }

                var (cert, key, print) = pki.Issue("sqlbranch-it-minio", [IPAddress.Parse(address), IPAddress.Loopback], ["localhost"]);
                var folder = Directory.CreateTempSubdirectory("sqlbranch-it-certs-");
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(folder.FullName, "public.crt"), cert);
                    await File.WriteAllTextAsync(Path.Combine(folder.FullName, "private.key"), key);
                    await FixtureDocker.CopyFilesInAsync(folder.FullName, name, "/certs");
                }
                finally
                {
                    folder.Delete(recursive: true);
                }

                certified = address;
                thumbprint = print;
                await FixtureDocker.RunAsync("restart", name);
            }

            var server = new MinioServer(name, runId, certified, await FixtureDocker.HostPortAsync(name, 9000), thumbprint, keyId, secret);
            await server.WaitUntilReadyAsync();
            return server;
        }
        catch
        {
            await FixtureDocker.RemoveOwnAsync(name, runId);
            throw;
        }
    }

    /// <summary>A handler for the Core storage clients that reaches this server and trusts only its test certificate.</summary>
    public HttpMessageHandler CreateHandler() => FixtureStorageTransport.CreateHandler(_thumbprint, HostPort);

    internal string CertificateThumbprint => _thumbprint;

    private async Task WaitUntilReadyAsync()
    {
        using var http = FixtureDocker.ClientTrusting(_thumbprint);
        var until = DateTime.UtcNow.AddSeconds(60);
        var last = string.Empty;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(new Uri($"https://127.0.0.1:{HostPort.ToString(CultureInfo.InvariantCulture)}/minio/health/ready"));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                last = ex.GetBaseException().Message;
            }

            Assert.True(DateTime.UtcNow < until, $"MinIO did not become ready ({last}): {(await EngineWorld.DockerAsync("logs", "--tail", "8", _name)).StdErr}");
            await Task.Delay(500);
        }
    }

    /// <summary>Makes a bucket and puts an object in it, signed with the test keys (AWS signature version 4).</summary>
    public async Task PutAsync(string path, byte[] body)
    {
        using var http = FixtureDocker.ClientTrusting(_thumbprint);
        var now = DateTime.UtcNow;
        var stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payload = FixtureDocker.Hex(SHA256.HashData(body));
        var host = $"127.0.0.1:{HostPort.ToString(CultureInfo.InvariantCulture)}";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonical = $"PUT\n{path}\n\nhost:{host}\nx-amz-content-sha256:{payload}\nx-amz-date:{stamp}\n\n{signedHeaders}\n{payload}";
        var scope = $"{day}/us-east-1/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{stamp}\n{scope}\n{FixtureDocker.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var signingKey = FixtureDocker.Hmac(FixtureDocker.Hmac(FixtureDocker.Hmac(FixtureDocker.Hmac(
            Encoding.UTF8.GetBytes("AWS4" + _secret), day), "us-east-1"), "s3"), "aws4_request");
        var signature = FixtureDocker.Hex(FixtureDocker.Hmac(signingKey, toSign));

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"https://{host}{path}")) { Content = new ByteArrayContent(body) };
        request.Headers.TryAddWithoutValidation("x-amz-date", stamp);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payload);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={_keyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        using var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"MinIO refused PUT {path}: {(int)response.StatusCode}");
    }

    public async ValueTask DisposeAsync() => await FixtureDocker.RemoveOwnAsync(_name, _runId);
}

/// <summary>
/// Azurite as a Blob source, under the conditions spike 1.2 found: HTTPS on port 443, a certificate for
/// <c>&lt;account&gt;.blob.core.windows.net</c> (the engine gets a hosts entry for the name), and a SAS with write permission,
/// since the engine leases the blob.
/// </summary>
public sealed class AzuriteServer : IAsyncDisposable
{
    public const string SasVersion = "2022-11-02";

    private const string Image = "mcr.microsoft.com/azure-storage/azurite@sha256:c26024d62bf4a5b88a40968afd217234c00e1fc9a4790dc6105d1ca7979f6eec";

    private readonly string _name;
    private readonly string _runId;
    private readonly string _thumbprint;
    private readonly byte[] _key;

    private AzuriteServer(string name, string runId, string account, byte[] key, string thumbprint, string address, int hostPort)
    {
        _name = name;
        _runId = runId;
        Account = account;
        _key = key;
        _thumbprint = thumbprint;
        Address = address;
        HostPort = hostPort;
    }

    public static string ImageName => Environment.GetEnvironmentVariable("SQLBRANCH_IT_AZURITE_IMAGE") is { Length: > 0 } image ? image : Image;

    public string Account { get; }

    public string Host => $"{Account}.blob.core.windows.net";

    public string Address { get; }

    public int HostPort { get; }

    public static async Task<AzuriteServer> StartAsync(string runId, TestPki pki)
    {
        var account = "sbit" + runId;
        var name = $"sqlbranch-it-azurite-{runId}";
        var key = RandomNumberGenerator.GetBytes(32);
        var (cert, privateKey, thumbprint) = pki.Issue($"{account}.blob.core.windows.net", [IPAddress.Loopback], [$"{account}.blob.core.windows.net"]);
        await FixtureDocker.CreateAsync(name, runId, ImageName,
            ["-v", "/data", "-p", "127.0.0.1::443", "-e", $"AZURITE_ACCOUNTS={account}:{Convert.ToBase64String(key)}"],
            ["azurite-blob", "--blobHost", "0.0.0.0", "--blobPort", "443", "--location", "/data",
                "--cert", "/certs/public.crt", "--key", "/certs/private.key", "--loose", "--skipApiVersionCheck"]);
        try
        {
            var folder = Directory.CreateTempSubdirectory("sqlbranch-it-certs-");
            try
            {
                await File.WriteAllTextAsync(Path.Combine(folder.FullName, "public.crt"), cert);
                await File.WriteAllTextAsync(Path.Combine(folder.FullName, "private.key"), privateKey);
                await FixtureDocker.CopyFilesInAsync(folder.FullName, name, "/certs");
            }
            finally
            {
                folder.Delete(recursive: true);
            }

            await FixtureDocker.RunAsync("start", name);
            var server = new AzuriteServer(name, runId, account, key, thumbprint, await FixtureDocker.BridgeAddressAsync(name), await FixtureDocker.HostPortAsync(name, 443));
            await server.WaitUntilReadyAsync();
            return server;
        }
        catch
        {
            await FixtureDocker.RemoveOwnAsync(name, runId);
            throw;
        }
    }

    /// <summary>A handler for the Core storage clients that reaches this server and trusts only its test certificate.</summary>
    public HttpMessageHandler CreateHandler() => FixtureStorageTransport.CreateHandler(_thumbprint, HostPort);

    internal string CertificateThumbprint => _thumbprint;

    private async Task WaitUntilReadyAsync()
    {
        using var http = FixtureDocker.ClientTrusting(_thumbprint);
        var until = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(new Uri($"https://127.0.0.1:{HostPort.ToString(CultureInfo.InvariantCulture)}/{Account}?comp=list"));
                return;
            }
            catch (HttpRequestException)
            {
                // Not up yet.
            }

            Assert.True(DateTime.UtcNow < until, "Azurite did not become ready.");
            await Task.Delay(500);
        }
    }

    private static string Expiry() => DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private string Sign(string text) => Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(text)));

    /// <summary>An account SAS the fixture uses to make the container and upload blobs.</summary>
    private string AccountSas()
    {
        var expiry = Expiry();
        const string permissions = "rwdlac";
        var toSign = $"{Account}\n{permissions}\nb\nsco\n\n{expiry}\n\nhttps\n{SasVersion}\n\n";
        return $"sv={SasVersion}&ss=b&srt=sco&sp={permissions}&se={Uri.EscapeDataString(expiry)}&spr=https&sig={Uri.EscapeDataString(Sign(toSign))}";
    }

    /// <summary>
    /// The container SAS a build uses: read, write and list. Write is needed because the engine leases the blob (spike 1.2).
    /// Returns the token as the environment variable takes it, and the signature value.
    /// </summary>
    public (string Token, string Signature) ContainerSas(string container)
    {
        var expiry = Expiry();
        const string permissions = "rwl";
        var toSign = string.Join('\n', permissions, string.Empty, expiry, $"/blob/{Account}/{container}", string.Empty, string.Empty, "https", SasVersion, "c",
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        var signature = Sign(toSign);
        return ($"sv={SasVersion}&spr=https&se={Uri.EscapeDataString(expiry)}&sr=c&sp={permissions}&sig={Uri.EscapeDataString(signature)}", signature);
    }

    public async Task CreateContainerAsync(string container)
    {
        using var http = FixtureDocker.ClientTrusting(_thumbprint);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"https://127.0.0.1:{HostPort.ToString(CultureInfo.InvariantCulture)}/{Account}/{container}?restype=container&{AccountSas()}"));
        request.Content = new ByteArrayContent([]);
        using var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"Azurite refused to make container {container}: {(int)response.StatusCode}");
    }

    public async Task PutBlobAsync(string container, string blob, byte[] body)
    {
        using var http = FixtureDocker.ClientTrusting(_thumbprint);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"https://127.0.0.1:{HostPort.ToString(CultureInfo.InvariantCulture)}/{Account}/{container}/{blob}?{AccountSas()}"));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        using var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"Azurite refused blob {blob}: {(int)response.StatusCode}");
    }

    public async ValueTask DisposeAsync() => await FixtureDocker.RemoveOwnAsync(_name, _runId);
}
