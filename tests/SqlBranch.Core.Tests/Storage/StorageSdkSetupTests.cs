using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using System.Text.RegularExpressions;
using Amazon;
using Azure.Core.Diagnostics;
using SqlBranch.Core.Images;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Storage;
using SqlBranch.Core.Tests.Fakes;
using static SqlBranch.Core.Tests.Fakes.StorageScenario;

namespace SqlBranch.Core.Tests.Storage;

/// <summary>
/// The safe set-up of the storage SDKs (design D5, task 4.0): explicit credentials only, no default credential chain, SDK logging and
/// telemetry off, no marker of the user's own AWS or Azure set-up ever used or printed.
/// </summary>
[Collection(StorageSdkSerial.Name)]
public sealed class StorageSdkSetupTests : IDisposable
{
    private const string MarkerId = "AKIAMARKERENVIRONMENT";
    private const string MarkerSecret = "markerEnvSecretKey/ZZZ";
    private const string MarkerProfileId = "AKIAMARKERPROFILEFILE";
    private const string MarkerProfileSecret = "markerProfileSecretFile";

    private static readonly string[] PlantedVariables =
    [
        "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN", "AWS_PROFILE", "AWS_DEFAULT_PROFILE", "AWS_REGION", "AWS_DEFAULT_REGION",
        "AWS_ENDPOINT_URL", "AWS_ENDPOINT_URL_S3", "AWS_SHARED_CREDENTIALS_FILE", "AWS_CONFIG_FILE", "HOME", "USERPROFILE",
        "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_TENANT_ID", "AZURE_STORAGE_CONNECTION_STRING", "AZURE_STORAGE_KEY", "AZURE_STORAGE_SAS_TOKEN",
    ];

    private readonly Dictionary<string, string?> _saved = [];
    private readonly string _home = Directory.CreateTempSubdirectory("sqlbranch-home-").FullName;
    private readonly StorageScenario _s = new();

    public StorageSdkSetupTests()
    {
        foreach (var name in PlantedVariables)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
        }

        Directory.CreateDirectory(Path.Combine(_home, ".aws"));
        File.WriteAllText(Path.Combine(_home, ".aws", "credentials"),
            $"[default]\naws_access_key_id = {MarkerProfileId}\naws_secret_access_key = {MarkerProfileSecret}\n[markerprofile]\naws_access_key_id = {MarkerProfileId}\naws_secret_access_key = {MarkerProfileSecret}\n");
        File.WriteAllText(Path.Combine(_home, ".aws", "config"), "[default]\nregion = marker-profile-1\n[profile markerprofile]\nregion = marker-profile-1\n");
        Set("HOME", _home);
        Set("USERPROFILE", _home);
        Set("AWS_ACCESS_KEY_ID", MarkerId);
        Set("AWS_SECRET_ACCESS_KEY", MarkerSecret);
        Set("AWS_SESSION_TOKEN", "markerSessionToken");
        Set("AWS_PROFILE", "markerprofile");
        Set("AWS_DEFAULT_PROFILE", "markerprofile");
        Set("AWS_REGION", "marker-env-1");
        Set("AWS_DEFAULT_REGION", "marker-env-2");
        Set("AWS_ENDPOINT_URL", "https://marker-endpoint.invalid");
        Set("AWS_ENDPOINT_URL_S3", "https://marker-s3-endpoint.invalid");
        Set("AWS_SHARED_CREDENTIALS_FILE", Path.Combine(_home, ".aws", "credentials"));
        Set("AWS_CONFIG_FILE", Path.Combine(_home, ".aws", "config"));
        Set("AZURE_CLIENT_ID", "marker-azure-client");
        Set("AZURE_CLIENT_SECRET", "marker-azure-secret");
        Set("AZURE_TENANT_ID", "marker-azure-tenant");
        Set("AZURE_STORAGE_CONNECTION_STRING", "AccountName=markeraccount;AccountKey=markerAccountKey==");
        Set("AZURE_STORAGE_KEY", "markerAccountKey==");
        Set("AZURE_STORAGE_SAS_TOKEN", "sv=marker&sig=markerSasFromEnvironment");
    }

    private static readonly string[] Markers =
    [
        MarkerId, MarkerSecret, MarkerProfileId, MarkerProfileSecret, "markerSessionToken", "markerprofile", "marker-env-1", "marker-env-2",
        "marker-profile-1", "marker-endpoint.invalid", "marker-s3-endpoint.invalid", "marker-azure", "markeraccount", "markerAccountKey", "markerSasFromEnvironment",
    ];

    private static void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value);

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// With SDK logging on, the AWS SDK's internal-configuration lookups name the profile in AWS_PROFILE ("There is no retry_mode set in the
    /// profile named ...") although they read none of its credentials, region or endpoint. Those lines are SDK log output, which the tool
    /// never enables; the canary below shows the capture sees them, and every other line must be free of markers.
    /// </summary>
    private static string WithoutInternalConfigLookups(string log) =>
        string.Join('\n', log.Split('\n').Where(l => !l.StartsWith("ProfileInternalConfiguration ", StringComparison.Ordinal)
            && !l.StartsWith("EnvironmentVariableInternalConfiguration ", StringComparison.Ordinal)));

    private static void AssertNoMarker(string text, string where)
    {
        foreach (var marker in Markers)
        {
            Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{where} shows the marker '{marker}'");
        }
    }

    /// <summary>Runs the action with everything written to the console, to the trace and to the SDK event sources captured.</summary>
    private static string Captured(Action action)
    {
        var captured = new StringBuilder();
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var writer = new StringWriter(captured);
        using var errors = new StringWriter(captured);
        using var listener = new AzureEventSourceListener((e, message) => captured.AppendLine(message), EventLevel.Verbose);
        using var trace = new TextWriterTraceListener(writer);
        Trace.Listeners.Add(trace);
        // The console is redirected first: the AWS console logger keeps the writer it finds when logging is switched on.
        Console.SetOut(writer);
        Console.SetError(errors);
        var logTo = AWSConfigs.LoggingConfig.LogTo;
        var responses = AWSConfigs.LoggingConfig.LogResponses;
        // Console alone: the SDK does not log when the two options are combined.
        AWSConfigs.LoggingConfig.LogTo = LoggingOptions.Console;
        AWSConfigs.LoggingConfig.LogResponses = ResponseLoggingOption.Always;
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Trace.Listeners.Remove(trace);
            AWSConfigs.LoggingConfig.LogTo = logTo;
            AWSConfigs.LoggingConfig.LogResponses = responses;
        }

        writer.Flush();
        return captured.ToString();
    }

    [Fact]
    public async Task S3_requests_are_signed_with_the_sqlbranch_variables_only_and_no_aws_marker_is_used_or_printed()
    {
        _s.UseS3Secrets();
        _s.World.S3.Objects.Clear();
        (int Exit, string Out, string Err) export = default;
        (int Exit, string Out, string Err) import = default;
        var log = Captured(() =>
        {
            export = _s.Export("--to", S3Url, "--s3-region", "eu-west-1");
            import = _s.Import(S3Url, "--as", "shop2", "--s3-region", "eu-west-1");
        });

        Assert.True(export.Exit == 0, export.Out + export.Err);
        Assert.True(import.Exit == 0, import.Out + import.Err);
        var requests = _s.World.S3.Requests;
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
        {
            Assert.Contains($"Credential={KeyId}/", r.Authorization, StringComparison.Ordinal);
            Assert.Contains("/eu-west-1/s3/", r.Authorization, StringComparison.Ordinal);
            Assert.Equal("s3.amazonaws.com", r.Uri.Host);
            Assert.False(r.Headers.ContainsKey("x-amz-security-token"), "no session token of the environment is sent");
            AssertNoMarker(r.Everything, "a request");
            Assert.DoesNotContain(Secret, r.Everything, StringComparison.Ordinal);
        });
        AssertNoMarker(export.Out + export.Err + import.Out + import.Err + WithoutInternalConfigLookups(log), "the output");
        foreach (var text in new[] { export.Out, export.Err, import.Out, import.Err, log })
        {
            Assert.DoesNotContain(KeyId, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public void Azure_requests_carry_the_sas_token_only_and_no_azure_credential_of_the_environment_is_used_or_printed()
    {
        _s.UseSas();
        (int Exit, string Out, string Err) export = default;
        (int Exit, string Out, string Err) import = default;
        var log = Captured(() =>
        {
            export = _s.Export("--to", BlobUrl);
            import = _s.Import(BlobUrl, "--as", "shop2");
        });

        Assert.True(export.Exit == 0, export.Out + export.Err);
        Assert.True(import.Exit == 0, import.Out + import.Err);
        Assert.All(_s.World.Azure.Requests, r =>
        {
            Assert.Contains(Sas, r.Query, StringComparison.Ordinal);
            Assert.False(r.Headers.ContainsKey("Authorization"), "no bearer token or shared key is sent");
            Assert.Equal("acme.blob.core.windows.net", r.Uri.Host);
            AssertNoMarker(r.Everything, "a request");
        });
        AssertNoMarker(export.Out + export.Err + import.Out + import.Err + WithoutInternalConfigLookups(log), "the output");
        foreach (var text in new[] { export.Out, export.Err, import.Out, import.Err, log })
        {
            Assert.DoesNotContain("AbC123secretSig", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Sas, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_failing_export_prints_no_secret_and_no_marker_either()
    {
        _s.UseS3Secrets();
        _s.World.S3.Fail = _ => true;
        (int Exit, string Out, string Err) run = default;

        var log = Captured(() => run = _s.Export("--to", S3Url, "--format", "json"));

        Assert.Equal(1, run.Exit);
        AssertNoMarker(run.Out + run.Err + WithoutInternalConfigLookups(log), "the output");
        Assert.DoesNotContain(Secret, run.Out + run.Err + log, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyId, run.Out + run.Err + log, StringComparison.Ordinal);
    }

    [Fact]
    public void The_capture_used_by_these_tests_does_see_console_trace_and_azure_sdk_logging_when_they_are_on()
    {
        var log = Captured(() =>
        {
            Console.Out.WriteLine("canary-console");
            Console.Error.WriteLine("canary-error");
            Trace.WriteLine("canary-trace");
            var options = new global::Azure.Storage.Blobs.BlobClientOptions
            {
                Transport = new global::Azure.Core.Pipeline.HttpClientTransport(new HttpClient(_s.World.Azure, disposeHandler: false)),
            };
            options.Retry.MaxRetries = 0;
            options.Diagnostics.IsLoggingEnabled = true;
            var client = new global::Azure.Storage.Blobs.Specialized.BlockBlobClient(new Uri(BlobUrl), new global::Azure.AzureSasCredential(Sas), options);
            client.ExistsAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("canary-console", log, StringComparison.Ordinal);
        Assert.Contains("canary-error", log, StringComparison.Ordinal);
        Assert.Contains("canary-trace", log, StringComparison.Ordinal);
        Assert.Contains("acme.blob.core.windows.net", log, StringComparison.Ordinal);
    }

    [Fact]
    public void The_capture_used_by_these_tests_does_see_aws_sdk_logging_when_it_is_on()
    {
        var log = Captured(() =>
        {
            var config = new Amazon.S3.AmazonS3Config
            {
                ServiceURL = "https://s3.amazonaws.com",
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 0,
                DisableLogging = false,
                LogResponse = true,
                HttpClientFactory = new CanaryClientFactory(_s.World.S3),
            };
            using var client = new Amazon.S3.AmazonS3Client(new Amazon.Runtime.BasicAWSCredentials("AKIACANARY", "canarysecretvalue"), config);
            try
            {
                client.GetObjectMetadataAsync(new Amazon.S3.Model.GetObjectMetadataRequest { BucketName = "images", Key = "canary.sbimg" }).GetAwaiter().GetResult();
            }
            catch (Amazon.S3.AmazonS3Exception)
            {
                // The service answers 404; the request line is what the canary looks for.
            }
        });

        Assert.Contains("GetObjectMetadataRequest", log, StringComparison.Ordinal);
        Assert.Contains("|DEBUG|", log, StringComparison.Ordinal);
    }

    private sealed class CanaryClientFactory(HttpMessageHandler handler) : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig clientConfig) => new(handler, disposeHandler: false);
    }

    [Fact]
    public void The_source_holds_no_reference_to_a_default_credential_chain_or_a_profile()
    {
        var root = FindRepositoryRoot();
        var forbidden = new[]
        {
            "DefaultAzureCredential", "ChainedTokenCredential", "ManagedIdentityCredential", "EnvironmentCredential", "AzureCliCredential",
            "FallbackCredentialsFactory", "DefaultAWSCredentialsIdentityResolver", "EnvironmentVariablesAWSCredentials", "StoredProfileAWSCredentials",
            "InstanceProfileAWSCredentials", "CredentialProfileStoreChain", "SharedCredentialsFile", "FallbackRegionFactory", "AWSConfigs.AWSProfileName",
            "ProfileManager", "Azure.Identity", "ConnectionString", "SharedKeyCredential",
        };
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden)
            {
                // Microsoft.Data.SqlClient's own "ConnectionString" is the one name that is allowed (SQL connections).
                if (word == "ConnectionString")
                {
                    continue;
                }

                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetRelativePath(root, file)} references {word}");
            }
        }

        // The S3 client is built in one place, only from BasicAWSCredentials.
        var constructions = files.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"new AmazonS3Client\([^;]*;").Select(m => m.Value)).ToList();
        var construction = Assert.Single(constructions);
        Assert.Contains("new BasicAWSCredentials(", construction, StringComparison.Ordinal);
        // The Azure client is built in one place, only from an AzureSasCredential.
        var blobClients = files.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"new BlockBlobClient\([^;]*;").Select(m => m.Value)).ToList();
        Assert.Contains("new AzureSasCredential(", Assert.Single(blobClients), StringComparison.Ordinal);
        Assert.DoesNotContain(files, f => Regex.IsMatch(File.ReadAllText(f), @"new (BlobServiceClient|BlobContainerClient|BlobClient)\("));
    }

    [Fact]
    public void The_project_files_reference_no_azure_identity_package()
    {
        var root = FindRepositoryRoot();
        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("Azure.Identity", File.ReadAllText(project), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Sdk_logging_and_telemetry_are_off_in_the_client_options_of_both_stores()
    {
        var root = FindRepositoryRoot();
        var s3 = File.ReadAllText(Path.Combine(root, "src", "SqlBranch.Core", "Storage", "S3ArchiveStore.cs"));
        Assert.Contains("DisableLogging = true", s3, StringComparison.Ordinal);
        Assert.Contains("LogResponse = false", s3, StringComparison.Ordinal);
        Assert.Contains("LogMetrics = false", s3, StringComparison.Ordinal);
        var azure = File.ReadAllText(Path.Combine(root, "src", "SqlBranch.Core", "Storage", "AzureArchiveStore.cs"));
        Assert.Contains("IsLoggingEnabled = false", azure, StringComparison.Ordinal);
        Assert.Contains("IsTelemetryEnabled = false", azure, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_sdk_exception_reaches_the_user_through_the_redactor_and_never_as_an_sdk_type()
    {
        _s.UseS3Secrets();
        _s.World.S3.Down = true;

        var run = _s.Export("--to", S3Url, "--format", "json");

        Assert.Equal(1, run.Exit);
        Assert.Contains("storage_unreachable", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Amazon.", run.Out + run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_that_an_exception_message_carries_is_masked_by_the_redactor()
    {
        var redactor = new SecretRedactor();
        redactor.Register(Secret);
        var errors = new StorageErrors("https://acme.blob.core.windows.net/images/shop.sbimg", redactor);

        var error = errors.Unreachable(new InvalidOperationException($"GET https://acme.blob.core.windows.net/images/shop.sbimg?{Sas} failed, key {Secret}"), "read the archive");

        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AbC123secretSig", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sv=2024", error.Message, StringComparison.Ordinal);
        Assert.Contains("https://acme.blob.core.windows.net/images/shop.sbimg", error.Message, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SqlBranch.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}
