using System.Net;
using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Configuration;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

// The subprocess probe changes process-wide environment variables.
[CollectionDefinition(nameof(BacpacDownloadEnvironment), DisableParallelization = true)]
public sealed class BacpacDownloadEnvironment;

[Collection(nameof(BacpacDownloadEnvironment))]
public sealed class BacpacDownloadTests
{
    private const string AzureUrl = "https://acme.blob.core.windows.net/db/shop.bacpac";
    private const string S3Url = "s3://s3.amazonaws.com/db/shop.bacpac";
    private const string Signature = "synthetic%21signature";
    private const string KeyId = "synthetic-s3-id";
    private const string Key = "synthetic-s3-key";
    private static readonly CancellationToken None = CancellationToken.None;

    [UnixFact]
    public async Task Credentials_are_excluded_from_inherited_subprocess_environment()
    {
        string[] variables = [
            BackupUrl.SasVariable, StorageSecrets.S3KeyIdVariable, StorageSecrets.S3SecretVariable, SettingKeys.SaPasswordVariable,
        ];
        var previous = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var variable in variables)
            {
                Environment.SetEnvironmentVariable(variable, "synthetic-fixture-credential");
            }
            const string probe = """
                test -n "$PATH" &&
                test -z "${SQLBRANCH_AZURE_SAS_TOKEN+x}${SQLBRANCH_S3_ACCESS_KEY_ID+x}${SQLBRANCH_S3_SECRET_ACCESS_KEY+x}${SQLBRANCH_SA_PASSWORD+x}"
                """;
            var process = await new SystemProcessRunner().RunAsync("/bin/sh", ["-c", probe], null, None);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            for (var i = 0; i < variables.Length; i++)
            {
                Environment.SetEnvironmentVariable(variables[i], previous[i]);
            }
        }
    }

    [UnixTheory]
    [InlineData(AzureUrl, null, null)]
    [InlineData(S3Url, "ap-south-1", "ap-south-1")]
    [InlineData(S3Url, null, "us-east-1")]
    public async Task Validated_credentials_and_url_reach_head_and_get_after_init_and_name_checks(
        string url, string? region, string? expectedRegion)
    {
        using var fixture = new Fixture();
        var package = fixture.Tree.File("fixture.bacpac", "");
        BacpacPackageTests.Write(package, "");
        var original = await File.ReadAllBytesAsync(package);
        using var body = new Body(original);
        string? directory = null;
        fixture.Source.Head = _ => ValueTask.FromResult(original.LongLength);
        fixture.Source.Get = (_, _) =>
        {
            directory = fixture.Scratch();
            CheckUnixMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            CheckUnixMode(Path.Combine(directory, "source.bacpac"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            CheckUnixMode(Path.Combine(directory, ".sqlbranch-bacpac"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return ValueTask.FromResult<Stream>(body);
        };
        fixture.SimulateImport(original);
        var run = fixture.Run(url, region);

        // Stop after import to verify acquisition and cleanup independently of the sealing tests.
        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", Code(run.Out));
        Assert.Contains("Phase: mask and seal the imported database.", run.Out, StringComparison.Ordinal);
        Assert.Equal(["lookup", "verify engine", "name lock", "image exists", "HEAD", "GET"], fixture.Trace.Take(6));
        Assert.Equal(2, fixture.World.SqlPackageProcess.Calls.Count);
        Assert.All(fixture.Source.Calls, call =>
        {
            Assert.Equal(url, call.Url.Url);
            Assert.Equal(expectedRegion, call.Url.S3Region);
            Assert.Empty(new Uri(call.Url.Url).Query);
            Assert.True(call.Token.CanBeCanceled);
            Assert.True(call.Secrets.Secret == (url == AzureUrl ? "sig=" + Signature : KeyId + ":" + Key));
        });
        Assert.True(body.Closed);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(original, await File.ReadAllBytesAsync(package));
        Assert.Contains(fixture.World.Connector.Statements, s => s.Contains("DROP DATABASE", StringComparison.Ordinal));
        fixture.NoPublication();
        fixture.NoStorageSecrets(run.Out + run.Err);
    }

    [Theory]
    [InlineData(AzureUrl, "eu-west-1", false, "usage_error")]
    [InlineData(S3Url, "US East", false, "usage_error")]
    [InlineData(AzureUrl + "?sig=synthetic%21signature", null, false, "usage_error")]
    [InlineData(AzureUrl, null, true, "config_error")]
    [InlineData(S3Url, null, true, "config_error")]
    public void Invalid_inputs_never_reach_Docker_or_the_read_source(string url, string? region, bool removeSecrets, string code)
    {
        using var fixture = new Fixture();
        if (removeSecrets)
        {
            fixture.World.Environment.Remove(BackupUrl.SasVariable);
            fixture.World.Environment.Remove(StorageSecrets.S3SecretVariable);
        }
        var run = fixture.Run(url, region);

        Assert.Equal(2, run.Exit);
        Assert.Equal(code, Code(run.Out));
        Assert.Empty(fixture.Trace);
        Assert.Empty(fixture.World.Runtime.Calls);
        Assert.Empty(fixture.Source.Calls);
        fixture.NoStorageSecrets(run.Out + run.Err);
    }

    [Theory]
    [InlineData("not initialized", 3, "not_initialized")]
    [InlineData("existing", 1, "image_exists")]
    [InlineData("missing tool", 3, "sqlpackage_missing")]
    public void Readiness_existing_shop_and_tool_failures_prevent_storage_requests(string scenario, int exit, string code)
    {
        using var fixture = new Fixture();
        if (scenario == "not initialized")
        {
            fixture.World.Runtime.Containers.Clear();
        }
        fixture.ExistingImage = scenario == "existing";
        if (scenario == "missing tool")
        {
            fixture.World.SqlPackageProcess.Throw = new System.ComponentModel.Win32Exception();
        }

        var run = fixture.Run(AzureUrl, useExplicitSqlPackage: scenario != "missing tool");

        Assert.Equal(exit, run.Exit);
        Assert.Equal(code, Code(run.Out));
        Assert.Empty(fixture.Source.Calls);
        Assert.Equal(scenario == "missing tool" ? 1 : 0, fixture.World.SqlPackageProcess.Calls.Count);
        if (scenario == "missing tool")
        {
            Assert.Equal(["/Version"], Assert.Single(fixture.World.SqlPackageProcess.Calls).Args);
            Assert.Contains("dotnet tool install -g microsoft.sqlpackage", run.Out, StringComparison.Ordinal);
        }
        if (fixture.ExistingImage)
        {
            Assert.Equal(["lookup", "verify engine", "name lock", "image exists"], fixture.Trace);
        }
        Assert.DoesNotContain("detachedUsers", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("removedLogins", run.Out, StringComparison.Ordinal);
        fixture.NoPublication();
    }

    [Fact]
    public void A_leftover_folder_without_catalog_or_manifest_is_image_exists_on_a_format_2_pool()
    {
        using var fixture = new Fixture();
        SqlBranch.Core.Tests.Fakes.Format2Pool.Apply(fixture.World);
        fixture.ExistingImage = true;

        var run = fixture.Run(AzureUrl);

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_exists", Code(run.Out));
        Assert.Empty(fixture.Source.Calls);
        Assert.Equal(["lookup", "verify engine", "name lock", "image exists"], fixture.Trace);
        fixture.NoPublication();
    }

    [Theory]
    [InlineData("redirect", "storage_unreachable", 1)]
    [InlineData("403", "storage_unreachable", 2)]
    [InlineData("certificate", "storage_unreachable", 1)]
    [InlineData("invalid", "bacpac_invalid", 2)]
    [InlineData("disconnect", "storage_unreachable", 2)]
    [InlineData("provider disk error", "storage_unreachable", 2)]
    [InlineData("short", "storage_unreachable", 2)]
    [InlineData("grew", "storage_unreachable", 2)]
    [InlineData("unknown size", "storage_unreachable", 1)]
    public void Refusals_preserve_the_source_and_suppress_provider_diagnostics(string kind, string code, int requests)
    {
        using var fixture = new Fixture();
        var original = new byte[70_000];
        using var body = new Body(original);
        var diagnostic = $"https://second.example/db/shop.bacpac?sig={Signature}&key={KeyId}&secret={Key}";
        fixture.Source.Head = _ => kind switch
        {
            "redirect" => throw new HttpRequestException(diagnostic, null, HttpStatusCode.TemporaryRedirect),
            "certificate" => throw new System.Security.Authentication.AuthenticationException(diagnostic),
            "short" => ValueTask.FromResult(original.LongLength + 1),
            "grew" => ValueTask.FromResult(original.LongLength - 1),
            "unknown size" => ValueTask.FromResult(-1L),
            _ => ValueTask.FromResult(original.LongLength),
        };
        string? directory = null;
        long? lengthAtLimit = null;
        fixture.Source.Get = (_, _) =>
        {
            directory = fixture.Scratch();
            return kind == "403"
                ? throw new HttpRequestException(diagnostic, new InvalidOperationException(diagnostic), HttpStatusCode.Forbidden)
                : ValueTask.FromResult<Stream>(body);
        };
        body.BeforeRead = (read, requested, _) =>
        {
            if (read == 2 && kind is "disconnect" or "provider disk error")
            {
                throw new IOException(kind == "disconnect" ? diagnostic : "No space left on device " + diagnostic);
            }
            if (read == 3 && kind == "grew")
            {
                Assert.Equal(1, requested);
                lengthAtLimit = new FileInfo(Path.Combine(directory!, "source.bacpac")).Length;
            }
        };

        var run = fixture.Run(AzureUrl);

        Assert.Equal(1, run.Exit);
        Assert.Equal(code, Code(run.Out));
        fixture.NoStorageSecrets(run.Out + run.Err);
        Assert.DoesNotContain("second.example", run.Out + run.Err, StringComparison.Ordinal);
        if (code == "storage_unreachable")
        {
            var message = JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("message").GetString();
            Assert.Contains(AzureUrl + ".", message, StringComparison.Ordinal);
        }
        Assert.Equal(requests, fixture.Source.Calls.Count);
        Assert.Equal(requests == 2, directory is not null);
        Assert.False(Directory.Exists(directory));
        Assert.All(original, b => Assert.Equal(0, b));
        Assert.Single(fixture.World.SqlPackageProcess.Calls);
        if (kind is "invalid" or "disconnect" or "provider disk error" or "short" or "grew")
        {
            Assert.True(body.Closed);
        }
        if (kind == "grew")
        {
            Assert.True(lengthAtLimit is >= 0 && lengthAtLimit <= original.LongLength - 1);
        }
        fixture.NoEngineMutation();
        fixture.NoPublication();
    }

    [Fact]
    public async Task A_concurrent_shop_builder_cannot_download_and_failure_releases_the_name()
    {
        using var fixture = new Fixture();
        using var entered = new SemaphoreSlim(0);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.Get = async (_, token) =>
        {
            entered.Release();
            await resume.Task.WaitAsync(token);
            throw new HttpRequestException("test disconnect");
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = fixture.ImportAsync(deadline.Token);
        await entered.WaitAsync(deadline.Token);
        try
        {
            Assert.Contains(EngineLocks.Image("shop"), fixture.World.Connector.HeldLocks);
            var competing = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.ImportAsync(deadline.Token));
            Assert.Equal(ErrorCodes.ImageExists, competing.Code);
            Assert.Equal(2, fixture.Source.Calls.Count);
        }
        finally
        {
            resume.TrySetResult();
        }
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => first);
        Assert.Equal(ErrorCodes.StorageUnreachable, error.Code);
        Assert.Null(error.InnerException);
        Assert.Empty(fixture.World.Connector.HeldLocks);

        fixture.Source.Get = (_, _) => ValueTask.FromResult<Stream>(new Body([0]));
        var retry = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.ImportAsync(deadline.Token));
        Assert.Equal(ErrorCodes.BacpacInvalid, retry.Code);
        Assert.Equal(["HEAD", "GET", "HEAD", "GET"], fixture.Source.Calls.Select(c => c.Method));
        Assert.Empty(fixture.World.Connector.HeldLocks);
        fixture.NoEngineMutation();
        fixture.NoPublication();
    }

    [Fact]
    public async Task Ctrl_C_during_streaming_closes_the_response_and_removes_only_its_scratch()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        using var other = new BacpacScratch("l" + Guid.NewGuid().ToString("N"), fixture.Engine.Id!);
        await using (var otherPackage = new StreamWriter(other.CreatePackage()))
        {
            await otherPackage.WriteAsync("other build");
        }
        var original = new byte[70_000];
        using var body = new Body(original);
        string? scratch = null;
        fixture.Source.Head = _ => ValueTask.FromResult(original.LongLength);
        fixture.Source.Get = (_, _) =>
        {
            scratch = fixture.Scratch();
            return ValueTask.FromResult<Stream>(body);
        };
        body.BeforeRead = (read, _, token) =>
        {
            if (read == 2)
            {
                Assert.Equal(cancel.Token, token);
                Assert.InRange(new FileInfo(Path.Combine(scratch!, "source.bacpac")).Length, 1, original.LongLength - 1);
                Assert.True(File.Exists(other.Package));
                cancel.Cancel();
                token.ThrowIfCancellationRequested();
            }
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ImportAsync(cancel.Token));

        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.True(body.Closed);
        Assert.NotNull(scratch);
        Assert.False(Directory.Exists(scratch));
        Assert.Equal("other build", await File.ReadAllTextAsync(other.Package));
        Assert.Empty(fixture.World.Connector.HeldLocks);
        fixture.NoEngineMutation();
        fixture.NoPublication();
    }

    [Fact]
    public async Task A_local_write_reporting_disk_full_has_its_own_code_and_releases_scratch()
    {
        using var fixture = new Fixture();
        using var body = new Body([0]);
        fixture.Source.Get = (_, _) => ValueTask.FromResult<Stream>(body);
        var scratch = new BacpacScratch(fixture.Id, fixture.Engine.Id!);
        var url = BackupUrl.Parse(S3Url);
        using (scratch)
        {
            var error = await Assert.ThrowsAsync<SqlBranchException>(() => BacpacDownload.StageAsync(fixture.Source,
                url, StorageSecrets.Read(url, fixture.World.Environment, fixture.World.Redactor), scratch, None,
                () => new FullDisk()));
            Assert.Equal(ErrorCodes.TempFull, error.Code);
            Assert.Contains("downloading", error.Message, StringComparison.Ordinal);
        }
        Assert.True(body.Closed);
        Assert.False(Directory.Exists(scratch.PathName));
    }

    [UnixFact]
    public async Task Cleanup_refuses_a_replaced_directory_even_with_the_original_ownership_marker()
    {
        using var fixture = new Fixture();
        string? original = null;
        string? moved = null;
        fixture.Source.Get = (_, _) =>
        {
            original = fixture.Scratch();
            moved = original + "-moved";
            Directory.Move(original, moved);
            Directory.CreateDirectory(original);
            const string marker = ".sqlbranch-bacpac";
            File.Move(Path.Combine(moved, marker), Path.Combine(original, marker));
            File.WriteAllText(Path.Combine(original, "sentinel"), "foreign");
            return ValueTask.FromResult<Stream>(new Body([0]));
        };

        try
        {
            var error = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.ImportAsync(None));

            Assert.Equal(ErrorCodes.OperationFailed, error.Code);
            Assert.NotNull(original);
            Assert.Contains(Path.GetFileName(original), error.Message, StringComparison.Ordinal);
            Assert.Equal("foreign", await File.ReadAllTextAsync(Path.Combine(original, "sentinel")));
            Assert.True(File.Exists(Path.Combine(moved!, "source.bacpac")));
            fixture.NoEngineMutation();
            fixture.NoPublication();
        }
        finally
        {
            if (Directory.Exists(original))
            {
                Directory.Delete(original, recursive: true);
            }
            if (Directory.Exists(moved))
            {
                Directory.Delete(moved, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cleanup_refuses_a_replaced_package_and_reports_the_scratch_identifier()
    {
        using var fixture = new Fixture();
        var scratch = new BacpacScratch(fixture.Id, fixture.Engine.Id!);
        try
        {
            await using (var package = new StreamWriter(scratch.CreatePackage()))
            {
                await package.WriteAsync("original");
            }
            var moved = scratch.Package + ".original";
            File.Move(scratch.Package, moved);
            await File.WriteAllTextAsync(scratch.Package, "replacement");

            var error = Assert.Throws<SqlBranchException>(scratch.Dispose);

            Assert.Contains(scratch.Identifier, error.Message, StringComparison.Ordinal);
            Assert.Equal("replacement", await File.ReadAllTextAsync(scratch.Package));
            Assert.Equal("original", await File.ReadAllTextAsync(moved));
        }
        finally
        {
            Directory.Delete(scratch.PathName, recursive: true);
        }
    }

    private static string? Code(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString();

    private static void CheckUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    private sealed class Fixture : IDisposable
    {
        public TempTree Tree { get; } = new();
        public CliWorld World { get; } = new();
        public ContainerInfo Engine { get; }
        public RecordingSource Source { get; }
        public List<string> Trace { get; } = [];
        public string Id { get; } = "l" + Guid.NewGuid().ToString("N");
        public bool ExistingImage { get; set; }
        private readonly List<string> _scripts = [];

        public Fixture()
        {
            World.WithEngine();
            Engine = World.Runtime.Containers[ContainerLabels.EngineName] with
            {
                Id = Guid.NewGuid().ToString("N"), Hostname = "test-host", PublishedPorts = ["127.0.0.1:14330"],
            };
            World.Runtime.Containers[Engine.Name] = Engine;
            World.Runtime.Containers[Engine.Id!] = Engine;
            World.Environment[BackupUrl.SasVariable] = "?sig=" + Signature;
            World.Environment[StorageSecrets.S3KeyIdVariable] = KeyId;
            World.Environment[StorageSecrets.S3SecretVariable] = Key;
            World.Runtime.OnInspect = name => Trace.Add(name == Engine.Id ? "verify engine" : "lookup");
            World.Connector.TrackSessionLocks = true;
            World.Connector.OnTakeLock = name =>
            {
                if (name == EngineLocks.Image("shop"))
                {
                    Trace.Add("name lock");
                }
            };
            World.Connector.ScalarHandler = sql => sql.Contains("MachineName", StringComparison.Ordinal) ? Engine.Hostname : 0;
            World.Runtime.ExecHandler = (_, args) =>
            {
                var script = args[2];
                _scripts.Add(script);
                if (script.EndsWith("then echo exists; fi", StringComparison.Ordinal) &&
                    script.Contains("'/pool/images/shop'", StringComparison.Ordinal))
                {
                    Trace.Add("image exists");
                    return FakeContainerRuntime.Ok(ExistingImage ? "exists" : "");
                }
                return FakeContainerRuntime.Ok();
            };
            Source = new RecordingSource(Trace);
            Tree.File("sqlpackage", "");
        }

        public (int Exit, string Out, string Err) Run(string url, string? region = null, bool useExplicitSqlPackage = true)
        {
            var services = World.Services() with { BacpacReadSource = Source, WorkingDirectory = Tree.Root };
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            string[] sqlPackageArgs = useExplicitSqlPackage ? ["--sqlpackage", Path.Combine(Tree.Root, "sqlpackage")] : [];
            string[] regionArgs = region is null ? [] : ["--s3-region", region];
            var exit = CliApp.CreateDefault(World.Redactor, services).Run(
                ["image", "build", "shop", "--from-bacpac", url, .. sqlPackageArgs,
                 .. regionArgs, "--format", "json"], stdout, stderr);
            return (exit, stdout.ToString(), stderr.ToString());
        }

        public Task ImportAsync(CancellationToken token)
        {
            var pool = new FoundPool(PoolFinder.LocationOf(Engine, World.Services().Paths), "plain", PoolSource.EngineLabels, Engine);
            var url = BackupUrl.Parse(AzureUrl);
            return new BacpacBuilder(World.Runtime, World.Connector, World.Redactor, World.SqlPackageProcess)
            {
                ReadSource = Source,
                NewId = () => Id,
            }.ImportRemoteAsync(pool, 14330, "shop", url, StorageSecrets.Read(url, World.Environment, World.Redactor),
                Path.Combine(Tree.Root, "sqlpackage"), World.Environment, Tree.Root, token);
        }

        private string OperationId => World.Connector.TakenLocks.Last(x =>
            x.StartsWith("sqlbranch:build:", StringComparison.Ordinal))["sqlbranch:build:".Length..];

        public string Scratch() => Assert.Single(Directory.GetDirectories(Path.GetTempPath(), "sqlbranch-bacpac-" + OperationId + "-*"));

        public void SimulateImport(byte[] original)
        {
            const string directoryIdentity = "5:91";
            var guid = Guid.NewGuid();
            var loginAttempted = false;
            var recordExec = World.Runtime.ExecHandler;
            World.Runtime.ExecHandler = (container, args) =>
            {
                var result = recordExec(container, args);
                var script = args[2];
                if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
                {
                    return FakeContainerRuntime.Ok("SQLBRANCH_FREE=1048576\n");
                }
                if (script.Contains("stat -c '%d:%i'", StringComparison.Ordinal) && !script.Contains("head -c 512", StringComparison.Ordinal))
                {
                    return FakeContainerRuntime.Ok(directoryIdentity);
                }
                if (script.Contains("head -c 512", StringComparison.Ordinal))
                {
                    return FakeContainerRuntime.Ok($"{Engine.Id}|{OperationId}|{(loginAttempted ? 1 : 0)}|{guid:D}|{directoryIdentity}");
                }
                return result;
            };
            World.Connector.ScalarHandler = sql => sql switch
            {
                var s when s.Contains("MachineName", StringComparison.Ordinal) => Engine.Hostname,
                var s when s.Contains("database_guid", StringComparison.Ordinal) => guid,
                var s when s.Contains("CASE WHEN DB_ID", StringComparison.Ordinal) &&
                    !s.Contains("SUSER_ID", StringComparison.Ordinal) => 1,
                var s when s.Contains("ProductVersion", StringComparison.Ordinal) => null,
                _ => 0,
            };
            World.Connector.FailVersion = true;
            World.Connector.QueryHandler = sql => sql.Contains("sys.master_files", StringComparison.Ordinal)
                ? [[ImagePaths.Build(OperationId) + "/data.mdf"], [ImagePaths.Build(OperationId) + "/log.ldf"]]
                : [];
            World.Connector.ExecuteHandler = sql => loginAttempted |= sql.StartsWith("CREATE LOGIN", StringComparison.Ordinal);
            World.SqlPackageProcess.Handler = args =>
            {
                if (!args.Contains("/Version"))
                {
                    const string option = "/SourceFile:";
                    var path = Assert.Single(args, a => a.StartsWith(option, StringComparison.Ordinal))[option.Length..];
                    Assert.Equal(original, File.ReadAllBytes(path));
                }
                return new ProcessOutput(0, "170.5.96.0", "");
            };
        }

        public void NoPublication()
        {
            Assert.DoesNotContain(_scripts, s => s.Contains("manifest", StringComparison.Ordinal));
            Assert.DoesNotContain(_scripts, s => s.Contains("bacpac.owner", StringComparison.Ordinal) &&
                World.SqlPackageProcess.Calls.Count <= 1);
        }

        public void NoEngineMutation() =>
            Assert.DoesNotContain(World.Connector.Statements, s => s.StartsWith("CREATE", StringComparison.Ordinal));

        public void NoStorageSecrets(string output)
        {
            var captured = output + string.Join('\n', World.SqlPackageProcess.Calls.SelectMany(c =>
                c.Args.Concat(c.Env?.Values ?? []))) + string.Join('\n', _scripts) + string.Join('\n', World.Connector.Statements);
            foreach (var secret in new[] { Signature, Uri.UnescapeDataString(Signature), KeyId, Key })
            {
                Assert.False(captured.Contains(secret, StringComparison.Ordinal), "A storage secret reached a diagnostic or subprocess.");
            }
        }

        public void Dispose() => Tree.Dispose();
    }

    private sealed class RecordingSource(List<string> trace) : IBacpacReadSource
    {
        public List<(string Method, BackupUrl Url, StorageSecrets Secrets, CancellationToken Token)> Calls { get; } = [];
        public Func<CancellationToken, ValueTask<long>> Head { get; set; } = _ => ValueTask.FromResult(1L);
        public Func<BackupUrl, CancellationToken, ValueTask<Stream>> Get { get; set; } = (_, _) => throw new InvalidOperationException();

        public ValueTask<long> HeadAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken)
        {
            Calls.Add(("HEAD", url, secrets, cancellationToken));
            trace.Add("HEAD");
            return Head(cancellationToken);
        }

        public ValueTask<Stream> GetAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken)
        {
            Calls.Add(("GET", url, secrets, cancellationToken));
            trace.Add("GET");
            return Get(url, cancellationToken);
        }
    }

    private sealed class Body(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public Action<int, int, CancellationToken>? BeforeRead { get; set; }
        public bool Closed { get; private set; }
        private int _reads;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BeforeRead?.Invoke(++_reads, buffer.Length, cancellationToken);
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FullDisk : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("No space left on device"));
    }
}
