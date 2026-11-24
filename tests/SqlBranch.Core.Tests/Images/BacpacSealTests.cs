using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Output;
using SqlBranch.Core.Tests.Configuration;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

public sealed class BacpacSealTests
{
    private static readonly string[] BackupFields = ["backupFile", "backupSha256", "backupUrl"];
    [Theory]
    [InlineData(false, "SIMPLE")]
    [InlineData(true, "FULL")]
    public void Runs_scripts_in_order_and_seals_the_rebuilt_log_before_publishing(bool keepRecovery, string recovery)
    {
        using var fixture = new Fixture();
        var result = fixture.Build("json", keepRecovery);
        Assert.Equal(0, result.Exit);
        using var json = JsonDocument.Parse(result.Out);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2048, root.GetProperty("sourceSizeBytes").GetInt64());
        var phases = root.GetProperty("phaseMilliseconds");
        Assert.Equal(120, phases.GetProperty("import").GetInt64());
        Assert.Equal(15, phases.GetProperty("seal").GetInt64());
        Assert.False(phases.TryGetProperty("rewrite", out _));
        Assert.Equal("source.bacpac", Path.GetFileName(fixture.ImportPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.ImportPath)));
        Assert.Empty(root.GetProperty("detachedUsers").EnumerateArray());
        Assert.Equal(0, root.GetProperty("removedLogins").GetInt32());
        Assert.Equal(0, root.GetProperty("removedServerObjects").GetInt32());
        Assert.True(phases.TryGetProperty("postScripts", out _));
        Assert.True(phases.TryGetProperty("cleanup", out _));
        Assert.Contains($"{fixture.Bytes} bytes", result.Err, StringComparison.Ordinal);
        Assert.Contains("can take hours", result.Err, StringComparison.Ordinal);

        var manifest = Assert.IsType<ImageManifest>(fixture.Manifest);
        Assert.Equal("bacpac", manifest.SourceType);
        Assert.Equal("shop", manifest.SourceDatabase);
        Assert.Equal("shop.bacpac", manifest.BacpacFile);
        Assert.Equal(fixture.Sha256, manifest.BacpacSha256);
        Assert.Null(manifest.BacpacUrl);
        Assert.Null(manifest.BackupFile);
        Assert.Null(manifest.BackupSha256);
        Assert.Null(manifest.BackupUrl);
        Assert.Equal("FULL", manifest.OriginalRecoveryModel);
        Assert.Equal(recovery, manifest.SealedRecoveryModel);
        Assert.Equal(["z-seed.sql", "a-mask.sql"], manifest.PostScripts.Select(script => script.Name));
        Assert.True(manifest.LogRebuilt);
        Assert.Equal(8_388_608, manifest.OriginalLogSizeBytes);
        Assert.Equal(524_288, Assert.Single(manifest.Files, f => f.Type == "log").SizeBytes);
        fixture.AssertOrder("notice", "import", "ALTER LOGIN", "seed rows", "mask rows", "sp_detach_db",
            "FOR ATTACH_REBUILD_LOG", $"SET RECOVERY {recovery}", "TO [sa]", "sp_detach_db", "chmod 444", "|handed-off", "DROP LOGIN", "stage manifest", "publish", "clear record");
        fixture.AssertClean();
        Assert.Contains(fixture.Timeline, line => line.Contains(
            $"mv -nT -- '{fixture.Operation.StagedImage}' '/pool/images/shop'", StringComparison.Ordinal));
        Assert.Equal(keepRecovery ? 0 : 2, fixture.Timeline.Count(s => s.Contains("SET RECOVERY SIMPLE", StringComparison.Ordinal)));

        var list = fixture.World.Run("image", "list");
        Assert.Contains("shop.bacpac", list.Out, StringComparison.Ordinal);
        using var listing = JsonDocument.Parse(fixture.World.Run("image", "list", "--format", "json").Out);
        var listedImage = listing.RootElement.GetProperty("images")[0];
        Assert.Equal("shop.bacpac", listedImage.GetProperty("bacpacFile").GetString());
        Assert.Equal(fixture.Sha256, listedImage.GetProperty("bacpacSha256").GetString());
        Assert.Equal(JsonValueKind.Null, listedImage.GetProperty("bacpacUrl").ValueKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Version_goes_into_the_build_json_and_the_catalog_is_published_with_the_manifest_on_format_2(bool format2)
    {
        using var fixture = new Fixture();
        if (format2)
        {
            Format2Pool.Apply(fixture.World);
        }

        var result = fixture.Build("json");

        Assert.Equal(0, result.Exit);
        using var json = JsonDocument.Parse(result.Out);
        var version = json.RootElement.GetProperty("image").GetProperty("version");
        if (!format2)
        {
            Assert.Equal(JsonValueKind.Null, version.ValueKind);
            Assert.DoesNotContain(fixture.Timeline, l => l.Contains("catalog.json.new' &&", StringComparison.Ordinal) || l.Contains("/.ids/", StringComparison.Ordinal));
            return;
        }

        Assert.Matches(@"^shop@\d{8}T\d{9}Z$", version.GetString());
        Assert.Contains(fixture.Timeline, l => l.Contains("SQLBRANCH_CATALOGFILE=", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Timeline, l => l.Contains("echo exists", StringComparison.Ordinal));
        fixture.AssertOrder("/pool/images/.ids/shop.new", "catalog.json.new", "mv -nT -- ");
        var catalogWrite = fixture.Timeline.Single(l => l.Contains("catalog.json.new", StringComparison.Ordinal) && l.Contains("base64", StringComparison.Ordinal));
        Assert.Contains($"{fixture.Operation.StagedImage}/catalog.json.new", catalogWrite, StringComparison.Ordinal);
        Assert.Contains($"mv -f '{fixture.Operation.StagedImage}/catalog.json.new' '{fixture.Operation.StagedImage}/catalog.json'", catalogWrite, StringComparison.Ordinal);
        fixture.AssertClean();
    }

    [Theory]
    [InlineData("rebuild-log", false)]
    [InlineData("seal", false)]
    [InlineData("seal", true)]
    public async Task Seal_failure_remains_primary_when_cleaning_up(string step, bool cleanupFails)
    {
        using var fixture = new Fixture(cleanupFails ? "cleanup" : null);
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.BuildWithSealFailureAsync(step));
        Assert.Equal(ErrorCodes.OperationFailed, error.Code);
        const string injected = "Injected sealing failure.";
        if (cleanupFails)
        {
            Assert.StartsWith(injected + " BACPAC cleanup", error.Message, StringComparison.Ordinal);
            Assert.Contains(fixture.Operation.Directory, error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(injected, error.Message);
            fixture.AssertClean();
        }
        Assert.DoesNotContain("publish", fixture.Timeline);
        Assert.Equal(step == "seal", fixture.Timeline.Any(line => line.Contains("rm -f -- ./manifest.json", StringComparison.Ordinal)));
        Assert.Equal(step == "rebuild-log", fixture.Timeline.Any(line => line.Contains("rm -f --", StringComparison.Ordinal) &&
            line.Contains(Quoting.Shell(fixture.Operation.Data), StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("import", "operation_failed", "import")]
    [InlineData("download", "storage_unreachable", "download")]
    [InlineData("trigger", "ddl_trigger", "postScripts")]
    [InlineData("script", "script_failed", "postScripts")]
    [InlineData("script and cleanup", "operation_failed", "cleanup")]
    [InlineData("rebuild", "operation_failed", "seal")]
    [InlineData("move", "operation_failed", "seal")]
    [InlineData("cleanup", "operation_failed", "cleanup")]
    [InlineData("manifest", "operation_failed", "seal")]
    public void Failed_phases_are_timed_and_withhold_publication(string failure, string code, string phase)
    {
        using var fixture = new Fixture(failure);
        var result = fixture.Build("json", remote: failure == "download");
        Assert.Equal(1, result.Exit);
        using var json = JsonDocument.Parse(result.Out);
        var root = json.RootElement;
        Assert.Equal(code, root.GetProperty("error").GetProperty("code").GetString());
        Assert.True(root.GetProperty("phaseMilliseconds").GetProperty(phase).GetInt64() > 0);
        Assert.Null(fixture.Manifest);
        Assert.DoesNotContain("private diagnostic", result.Out + result.Err, StringComparison.Ordinal);

        if (failure == "trigger")
        {
            Assert.Contains("on_owner", result.Out, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Timeline, line => line.Contains(" ENABLE", StringComparison.Ordinal));
            Assert.DoesNotContain("seed rows", fixture.Timeline);
            Assert.Single(fixture.Timeline, line => line.Contains("ALTER AUTHORIZATION", StringComparison.Ordinal)); // Import owner only.
        }
        if (failure is "move" or "manifest")
        {
            Assert.Contains(fixture.Timeline, line => line.Contains("rm -f -- ./manifest.json", StringComparison.Ordinal));
        }
        if (failure == "script and cleanup")
        {
            Assert.Contains("a-mask.sql, batch at line 1", result.Out, StringComparison.Ordinal);
            Assert.Contains(fixture.Operation.Directory, result.Out, StringComparison.Ordinal);
            Assert.Contains(fixture.Operation.Database, result.Out, StringComparison.Ordinal);
        }
        if (failure is "script" or "script and cleanup")
        {
            Assert.Contains("Masking SQL failed", root.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(CliWorld.StoredPassword, result.Out + result.Err, StringComparison.Ordinal);
        }
        if (failure != "manifest")
        {
            Assert.DoesNotContain("publish", fixture.Timeline);
        }
    }

    [Theory]
    [InlineData("published identity", "destination identity could not be verified")]
    [InlineData("final cleanup", "cleanup could not verify or remove")]
    public void Published_image_succeeds_with_a_warning_and_the_next_build_sweeps_the_leftover(string failure, string warning)
    {
        using var fixture = new Fixture(failure);
        var result = fixture.Build("json");
        Assert.Equal(0, result.Exit);
        Assert.Equal("shop", Assert.IsType<ImageManifest>(fixture.Manifest).Name);
        Assert.Contains("warning:", result.Err, StringComparison.Ordinal);
        Assert.Contains(warning, result.Err, StringComparison.Ordinal);
        Assert.Contains(fixture.Operation.Directory, result.Err, StringComparison.Ordinal);
        Assert.Contains("sqlbranch status", result.Err, StringComparison.Ordinal);

        var status = fixture.World.Run("status", "--format", "json");
        Assert.Equal(1, status.Exit);
        Assert.Contains("bacpac_build", status.Out, StringComparison.Ordinal);
        Assert.Contains(fixture.Operation.Id, status.Out, StringComparison.Ordinal);
        var abandoned = fixture.Operation;

        Assert.Equal(0, fixture.Build("json", image: "next").Exit);
        fixture.AssertOrder("publish", "rmdir -- " + Quoting.Shell(abandoned.StagedImage), "clear record", "mkdir -m 700", "publish");
        fixture.AssertClean();
    }

    [Fact]
    public void Remote_build_reports_size_before_get_and_records_query_free_provenance()
    {
        using var fixture = new Fixture();
        var result = fixture.Build("json", remote: true);
        Assert.Equal(0, result.Exit);
        var manifest = Assert.IsType<ImageManifest>(fixture.Manifest);
        Assert.Equal("s3://s3.amazonaws.com/db/shop.bacpac", manifest.BacpacUrl);
        Assert.Equal("shop.bacpac", manifest.BacpacFile);
        Assert.Equal(fixture.Sha256, manifest.BacpacSha256);
        Assert.Null(manifest.BackupFile);
        Assert.Null(manifest.BackupSha256);
        Assert.Null(manifest.BackupUrl);
        using var output = JsonDocument.Parse(result.Out);
        using var listing = JsonDocument.Parse(fixture.World.Run("image", "list", "--format", "json").Out);
        Assert.All(new[] { output.RootElement.GetProperty("image"), listing.RootElement.GetProperty("images")[0] }, image =>
        {
            foreach (var field in BackupFields)
            {
                Assert.Equal(JsonValueKind.Null, image.GetProperty(field).ValueKind);
            }
        });
        fixture.AssertOrder("notice", "GET package", "import");
    }

    [Fact]
    public void Retry_sweeps_the_durable_stage_left_before_publication()
    {
        using var fixture = new Fixture();
        var abandoned = fixture.LeaveAbandonedStage();
        var status = fixture.World.Run("status", "--format", "json");
        Assert.Equal(1, status.Exit);
        Assert.True(status.Out.Contains("bacpac_build", StringComparison.Ordinal), status.Out + status.Err);
        Assert.Contains(abandoned.Id, status.Out, StringComparison.Ordinal);
        Assert.Null(fixture.Manifest);

        var retry = fixture.Build("json");

        Assert.Equal(0, retry.Exit);
        fixture.AssertOrder("rmdir -- " + Quoting.Shell(abandoned.StagedImage), "clear record", "mkdir -m 700", "publish");
        Assert.NotEqual(abandoned.Id, fixture.Operation.Id);
        fixture.AssertClean();
    }

    [Fact]
    public void Text_reports_package_size_and_phase_times_on_success_and_failure()
    {
        using var success = new Fixture();
        var result = success.Build("text");
        Assert.Equal(0, result.Exit);
        Assert.Contains("is built from shop.bacpac", result.Out, StringComparison.Ordinal);
        Assert.Contains("import: 120 ms", result.Out, StringComparison.Ordinal);
        Assert.Contains("seal: 15 ms", result.Out, StringComparison.Ordinal);
        Assert.Contains($"{success.Bytes} bytes", result.Out, StringComparison.Ordinal);

        using var failure = new Fixture("import");
        result = failure.Build("text");
        Assert.Equal(1, result.Exit);
        Assert.Contains("import: 120 ms", result.Out, StringComparison.Ordinal);
        Assert.Contains("can take hours", result.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("seal:", result.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("json", true)]
    [InlineData("text", false)]
    public void Rewrite_reports_detachment_keeps_original_provenance_and_cleans_scratch(string format, bool remote)
    {
        using var fixture = new Fixture(mapped: true);
        var original = fixture.PackageBytes;
        var digest = Convert.ToHexStringLower(SHA256.HashData(original));
        fixture.PoolFreeBytes = original.Length;
        fixture.OnImport = path =>
        {
            Assert.Equal("rewritten.bacpac", Path.GetFileName(path));
            var source = Path.Combine(Path.GetDirectoryName(path)!, "source.bacpac");
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.True(new FileInfo(path).Length < original.Length);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        };
        var result = fixture.Build(format, remote: remote);

        Assert.True(result.Exit == 0, result.Out + result.Err);
        var notice = FormattableString.Invariant($"Writing a BACPAC scratch copy of about {original.Length} bytes to detach login-mapped users.");
        Assert.Contains(notice, result.Err, StringComparison.Ordinal);
        fixture.AssertOrder("notice", notice, "import");
        Assert.Equal(original, fixture.PackageBytes);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.ImportPath)));
        var manifest = Assert.IsType<ImageManifest>(fixture.Manifest);
        Assert.Equal("shop.bacpac", manifest.BacpacFile);
        Assert.Equal(digest, manifest.BacpacSha256);
        Assert.Equal(remote ? "s3://s3.amazonaws.com/db/shop.bacpac" : null, manifest.BacpacUrl);
        if (format == "json")
        {
            Assert.DoesNotContain(notice, result.Out, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(result.Out);
            var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(original.Length, root.GetProperty("sourceSizeBytes").GetInt64());
            Assert.Equal(digest, root.GetProperty("image").GetProperty("bacpacSha256").GetString());
            Assert.Equal(["report_reader", "sales_app"], root.GetProperty("detachedUsers").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(2, root.GetProperty("removedLogins").GetInt32());
            Assert.Equal(1, root.GetProperty("removedServerObjects").GetInt32());
            Assert.True(root.GetProperty("phaseMilliseconds").TryGetProperty("rewrite", out _));
        }
        else
        {
            Assert.Contains("Detached database users: report_reader, sales_app", result.Out, StringComparison.Ordinal);
            Assert.Contains("Removed server logins: 2", result.Out, StringComparison.Ordinal);
            Assert.Contains("Removed server objects: 1", result.Out, StringComparison.Ordinal);
            Assert.Contains("rewrite:", result.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pool_check_uses_original_compressed_length_even_when_rewrite_would_be_smaller()
    {
        using var fixture = new Fixture(mapped: true);
        fixture.PoolFreeBytes = fixture.Bytes - 1;
        var result = fixture.Build("json");
        Assert.Equal(1, result.Exit);
        Assert.Contains("pool_full", result.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("import", fixture.Timeline);
        Assert.Null(fixture.Manifest);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("cancelled")]
    [InlineData("invalid copy")]
    public async Task Rewrite_failure_cleans_owned_files_and_leaves_a_concurrent_build_alone(string failure)
    {
        using var fixture = new Fixture(mapped: true);
        using var concurrent = new BacpacScratch("l" + Guid.NewGuid().ToString("N"), "owned");
        await using (var package = concurrent.CreatePackage())
        {
            package.WriteByte(42);
        }
        await using (var package = concurrent.CreateRewrittenPackage())
        {
            package.WriteByte(43);
        }

        using var cancellation = new CancellationTokenSource();
        string? scratch = null;
        var progress = fixture.BuildProgress();
        Task<ImageBuildResult> Build() => fixture.BuildAsync(progress, file =>
        {
            scratch = Path.GetDirectoryName(((FileStream)file).Name)!;
            Assert.True(File.Exists(Path.Combine(scratch, "source.bacpac")));
            Assert.True(File.Exists(Path.Combine(scratch, "rewritten.bacpac")));
            return new RewriteOutput(() =>
            {
                if (failure == "cancelled")
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                if (failure == "full")
                {
                    throw new IOException("No space left on device");
                }
                // Keep the ZIP writes in memory so reopening the owned file sees a damaged package.
            });
        }, cancellation.Token);

        if (failure == "cancelled")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(Build);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<SqlBranchException>(Build);
            Assert.Equal(failure == "full" ? ErrorCodes.TempFull : ErrorCodes.BacpacInvalid, error.Code);
        }

        Assert.True(progress.PhaseMilliseconds["rewrite"] > 0);
        Assert.NotNull(scratch);
        Assert.False(Directory.Exists(scratch));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(concurrent.Package));
        Assert.Equal(new byte[] { 43 }, File.ReadAllBytes(concurrent.RewrittenPackage));
        Assert.DoesNotContain("import", fixture.Timeline);
        Assert.DoesNotContain("publish", fixture.Timeline);
    }

    [Theory]
    [InlineData("source.bacpac")]
    [InlineData("rewritten.bacpac")]
    public void Replaced_scratch_file_blocks_publication_and_preserves_both_paths(string name)
    {
        using var fixture = new Fixture(mapped: true);
        string? replaced = null;
        fixture.OnImport = path =>
        {
            replaced = Path.Combine(Path.GetDirectoryName(path)!, name);
            File.Move(replaced, replaced + ".moved");
            File.WriteAllText(replaced, "replacement");
        };
        try
        {
            var result = fixture.Build("json");
            Assert.Equal(1, result.Exit);
            Assert.Contains("operation_failed", result.Out, StringComparison.Ordinal);
            Assert.Contains("Could not verify ownership of client scratch", result.Out, StringComparison.Ordinal);
            Assert.NotNull(replaced);
            Assert.Equal("replacement", File.ReadAllText(replaced));
            Assert.True(File.Exists(replaced + ".moved"));
            Assert.DoesNotContain("publish", fixture.Timeline);
            fixture.AssertClean();
        }
        finally
        {
            if (replaced is not null)
            {
                Directory.Delete(Path.GetDirectoryName(replaced)!, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Checksum_valid_copy_with_a_login_fails_before_creating_an_import_target()
    {
        using var tree = new TempTree();
        var replacement = tree.File("still-mapped.bacpac", "");
        BacpacPackageTests.Write(replacement, """<Element Type="SqlLogin" Name="[remaining_login]" />""", checksum: "compute");
        var token = CancellationToken.None;
        Assert.True((await BacpacPackage.ValidateAsync(replacement, token)).NeedsRewrite);
        var contents = await File.ReadAllBytesAsync(replacement, token);
        using var fixture = new Fixture(mapped: true);
        string? scratch = null;

        var error = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.BuildAsync(fixture.BuildProgress(), file =>
        {
            scratch = Path.GetDirectoryName(((FileStream)file).Name);
            file.Write(contents);
            return new MemoryStream();
        }, token));

        Assert.Equal(ErrorCodes.OperationFailed, error.Code);
        Assert.Contains("still requires login detachment", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Timeline, line => line.StartsWith("CREATE DATABASE", StringComparison.Ordinal));
        Assert.Null(fixture.ImportPath);
        Assert.Null(fixture.Manifest);
        Assert.NotNull(scratch);
        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public void Import_failure_removes_both_packages()
    {
        using var fixture = new Fixture("import", mapped: true);
        var result = fixture.Build("json");
        Assert.Equal(1, result.Exit);
        Assert.Equal("rewritten.bacpac", Path.GetFileName(fixture.ImportPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.ImportPath)));
        Assert.DoesNotContain("publish", fixture.Timeline);
    }

    private sealed class RewriteOutput(Action onWrite) : MemoryStream
    {
        private bool _written;

        public override void Write(byte[] buffer, int offset, int count)
        {
            BeforeWrite();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BeforeWrite();
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BeforeWrite();
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void BeforeWrite()
        {
            if (!_written)
            {
                _written = true;
                onWrite();
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const string Identity = "5:91";
        private const string Url = "s3://s3.amazonaws.com/db/shop.bacpac";
        private readonly Guid _databaseGuid = Guid.NewGuid();
        private readonly TempTree _tree = new();
        private readonly Clock _clock = new();
        private readonly string? _failure;
        private readonly string _package;
        private readonly string[] _scripts;
        private BacpacOperation? _operation;
        private string _record = "";
        private bool _database;
        private bool _login;
        private bool _rebuilt;
        private bool _filesMoved;
        private bool _failedAfterPublication;

        public BacpacOperation Operation => _operation!;
        public CliWorld World { get; } = new();
        public List<string> Timeline { get; } = [];
        private ImageManifest? StagedManifest { get; set; }
        public ImageManifest? Manifest { get; private set; }
        public byte[] PackageBytes => File.ReadAllBytes(_package);
        public long PoolFreeBytes { get; set; } = 1_000_000_000;
        public string? ImportPath { get; private set; }
        public Action<string>? OnImport { get; set; }
        public long Bytes => new FileInfo(_package).Length;
        public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_package)));

        public Fixture(string? failure = null, bool mapped = false)
        {
            _failure = failure;
            _package = _tree.File("shop.bacpac", "");
            const string mappedModel = """
                <Element Type="SqlLogin" Name="[report_reader]" />
                <Element Type="SqlLogin" Name="[sales_app]" />
                <Element Type="SqlServerRoleMembership">
                    <Relationship Name="Member"><Entry><References Name="[sales_app]" /></Entry></Relationship>
                </Element>
                """;
            BacpacPackageTests.Write(_package, mapped ? mappedModel + BacpacPackageTests.User("report_reader", "LoginIdentifierScript")
                + BacpacPackageTests.User("sales_app", "LoginIdentifierScript") : "", checksum: mapped ? "compute" : null);
            var zip = File.ReadAllBytes(_package);
            var originalLength = zip.Length;
            Array.Resize(ref zip, 2048);
            BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(originalLength - 2), (ushort)(zip.Length - originalLength));
            File.WriteAllBytes(_package, zip);
            _scripts = [_tree.File("z-seed.sql", "seed rows"), _tree.File("a-mask.sql", "mask rows")];
            foreach (var file in _scripts.Append(_package))
            {
                World.Files[file] = new FileInfo(file).Length;
            }
            World.Clock = _clock;
            World.WithEngine();
            var engine = World.Runtime.Containers[ContainerLabels.EngineName] with
            {
                Id = "owned", Hostname = "test-host", PublishedPorts = ["127.0.0.1:14330"],
            };
            World.Runtime.Containers[ContainerLabels.EngineName] = engine;
            World.Runtime.Containers[engine.Id!] = engine;
            World.Runtime.ExecHandler = Shell;
            World.Connector.ScalarHandler = sql => sql switch
            {
                var s when Has(s, "MachineName") => engine.Hostname,
                var s when Has(s, "ProductVersion") => World.Connector.Version,
                var s when Has(s, "database_guid") => _databaseGuid,
                var s when Has(s, "CASE WHEN DB_ID") && !Has(s, "SUSER_ID") => !_filesMoved
                    ? (_database ? 1 : 0) : throw new InvalidOperationException("Database lookup after ownership handoff"),
                var s when Has(s, "sys.sql_logins") => _login ? 1 : 0,
                _ => 0,
            };
            World.Connector.ParameterizedScalarHandler = (_, _) => "FULL";
            World.Connector.ParameterizedExecuteHandler = (sql, _) =>
            {
                Timeline.Add(sql.Trim());
                if (Has(sql, "sp_detach_db"))
                {
                    _database = false;
                }
                if (_failure is "script" or "script and cleanup" && Has(sql, "mask rows"))
                {
                    _clock.Advance(4);
                    throw new EngineSqlException($"Masking SQL failed for {CliWorld.StoredPassword}", [50000]);
                }
            };
            World.Connector.ExecuteHandler = sql =>
            {
                Timeline.Add(World.Redactor.Redact(sql));
                if (sql.StartsWith("CREATE DATABASE", StringComparison.Ordinal))
                {
                    if (Has(sql, "FOR ATTACH_REBUILD_LOG"))
                    {
                        if (_failure == "rebuild")
                        {
                            _clock.Advance(7);
                            throw new InvalidOperationException("private diagnostic");
                        }
                        _rebuilt = true;
                    }
                    _database = true;
                }
                if (_failure == "script and cleanup" && Has(sql, "DROP LOGIN"))
                {
                    _clock.Advance(3);
                    throw new InvalidOperationException("private diagnostic");
                }
                _login = Has(sql, "CREATE LOGIN") || (_login && !Has(sql, "DROP LOGIN"));
                _database &= !Has(sql, "DROP DATABASE");
            };
            World.Connector.QueryHandler = sql =>
            {
                if (Has(sql, "sys.trigger_events") && _failure == "trigger")
                {
                    _clock.Advance(4);
                    return [["on_owner", false, true, 1, DateTime.UnixEpoch]];
                }
                return sql switch
                {
                    StatusReporter.DatabasesSql => [],
                    var s when Has(s, "sys.master_files") => [[Operation.Data], [_rebuilt ? Operation.RebuiltLog : Operation.Log]],
                    var s when Has(s, "state_desc") => [["ONLINE", false, "MULTI_USER"]],
                    _ => [],
                };
            };
            World.Connector.RowsHandler = (_, _) =>
                [FileRow("data", Operation.Data, "ROWS", 8_388_608), FileRow(_rebuilt ? Operation.Database + "_log" : "log",
                    _rebuilt ? Operation.RebuiltLog : Operation.Log, "LOG", _rebuilt ? 524_288 : 8_388_608)];
            World.SqlPackageProcess.Handler = args =>
            {
                if (args.Contains("/Version"))
                {
                    return new ProcessOutput(0, "170.5.96.0", "");
                }
                const string option = "/SourceFile:";
                ImportPath = Assert.Single(args, a => a.StartsWith(option, StringComparison.Ordinal))[option.Length..];
                OnImport?.Invoke(ImportPath);
                Timeline.Add("import");
                _clock.Advance(120);
                return _failure == "import" ? new ProcessOutput(1, "", "private diagnostic") : new ProcessOutput(0, "", "");
            };
        }

        public BacpacOperation LeaveAbandonedStage()
        {
            _operation = new BacpacOperation("l" + Guid.NewGuid().ToString("N"));
            _record = $"owned|{_operation.Id}|1|{_databaseGuid:D}|{Identity}|handed-off|5:92";
            return _operation;
        }

        private ContainerResult Shell(string _, IReadOnlyList<string> args)
        {
            var sql = args[2];
            Timeline.Add(sql);
            if (sql == StatusReporter.ListingScript)
            {
                const string space = "SQLBRANCH_SPACE|1000 200 800";
                return FakeContainerRuntime.Ok(_record.Length == 0 ? space : $"SQLBRANCH_BUILD|{Operation.Id}\n{space}");
            }
            if (Has(sql, "basename \"$d\"") && Has(sql, "bacpac.owner"))
            {
                return FakeContainerRuntime.Ok(_record.Length == 0 ? "" : Operation.Id);
            }
            if (Has(sql, "mkdir -m 700"))
            {
                _operation = new BacpacOperation(sql[(sql.LastIndexOf('/') + 1)..^1]);
                _filesMoved = false;
                _rebuilt = false;
            }
            if (_operation is not null && sql.EndsWith("stat -c '%d:%i' " + Quoting.Shell(Operation.Directory), StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok(Identity);
            }
            if (Has(sql, "head -c 512") && !Has(sql, "saved=$("))
            {
                return FakeContainerRuntime.Ok(_record);
            }
            if (Has(sql, "mv -T --"))
            {
                var start = sql.IndexOf("printf %s '", StringComparison.Ordinal) + "printf %s '".Length;
                _record = sql[start..sql.IndexOf('\'', start)];
            }
            if (Has(sql, "SQLBRANCH_FREE="))
            {
                return FakeContainerRuntime.Ok(FormattableString.Invariant($"SQLBRANCH_FREE={PoolFreeBytes}"));
            }
            if (Has(sql, "SQLBRANCH_PROBE"))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_PROBE=copy");
            }
            if (_failure == "cleanup" && _filesMoved && Has(sql, "! -name image") && !_login)
            {
                _clock.Advance(3);
                return FakeContainerRuntime.Fail();
            }
            if (Has(sql, "saved=$(head -c 512"))
            {
                _clock.Advance(3);
                if (_failure == "cleanup" || (_failure == "final cleanup" && !_failedAfterPublication))
                {
                    _failedAfterPublication = true;
                    return FakeContainerRuntime.Fail();
                }
                Timeline.Add("clear record");
                _record = "";
            }
            if (_operation is not null && sql.EndsWith("stat -c '%d:%i' " + Quoting.Shell(Operation.StagedImage), StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("5:92");
            }
            if (Has(sql, "chmod 444") && !Has(sql, "base64 -d"))
            {
                _clock.Advance(15);
                if (_failure == "move")
                {
                    return FakeContainerRuntime.Fail();
                }
                _filesMoved = true;
            }
            if (Has(sql, "base64 -d"))
            {
                Timeline.Add("stage manifest");
                if (_failure == "manifest")
                {
                    return FakeContainerRuntime.Fail();
                }

                var start = sql.IndexOf("printf %s '", StringComparison.Ordinal) + "printf %s '".Length;
                var encoded = sql[start..sql.IndexOf('\'', start)];
                StagedManifest = ImageManifest.FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            }
            if (Has(sql, "mv -nT --"))
            {
                Assert.NotEqual(Manifest?.Name, StagedManifest?.Name);
                Assert.False(_login);
                Assert.EndsWith("|handed-off|5:92", _record, StringComparison.Ordinal);
                Timeline.Add("publish");
                Manifest = StagedManifest;
            }
            if (_failure == "published identity" && !_failedAfterPublication &&
                sql.StartsWith("[ -d '/pool/images/shop' ]", StringComparison.Ordinal))
            {
                _failedAfterPublication = true;
                return FakeContainerRuntime.Fail();
            }
            if (sql == ImageCatalog.ListingScript && Manifest is not null)
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_IMAGE|shop|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Manifest.ToJson())));
            }
            return FakeContainerRuntime.Ok();
        }

        public (int Exit, string Out, string Err) Build(string format, bool keep = false, bool remote = false, string image = "shop")
        {
            World.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = "test-id";
            World.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = "test-key";
            var services = World.Services() with { BacpacReadSource = new ReadSource(_package, _clock, Timeline, _failure == "download") };
            using var output = new StringWriter();
            using var error = new NoticeWriter(Timeline);
            string[] args = ["image", "build", image, "--from-bacpac", remote ? Url : _package, "--sqlpackage", _package,
                "--post-script", _scripts[0], "--post-script", _scripts[1], "--format", format];
            var exit = SqlBranch.Cli.CliApp.CreateDefault(World.Redactor, services).Run(keep ? [.. args, "--keep-recovery-model"] : args, output, error);
            return (exit, output.ToString(), error.ToString());
        }

        public BacpacBuildProgress BuildProgress() => new(_clock);

        public Task<ImageBuildResult> BuildAsync(BacpacBuildProgress progress, Func<Stream, Stream> wrapOutput, CancellationToken token)
        {
            var engine = World.Runtime.Containers[ContainerLabels.EngineName];
            var pool = PoolFinder.LocationOf(engine, World.Services().Paths);
            var builder = new BacpacBuilder(World.Runtime, World.Connector, World.Redactor, World.SqlPackageProcess)
            {
                WrapRewriteOutput = file =>
                {
                    _clock.Advance(7);
                    return wrapOutput(file);
                },
            };
            return builder.BuildAsync(new FoundPool(pool, "plain", PoolSource.EngineLabels, engine), 14330,
                new ImageBuildRequest("shop", "", 0), _package, _package, World.Environment, _tree.Root, token, progress);
        }

        public Task<ImageBuildResult> BuildWithSealFailureAsync(string step)
        {
            var engine = World.Runtime.Containers[ContainerLabels.EngineName];
            var pool = PoolFinder.LocationOf(engine, World.Services().Paths);
            var builder = new BacpacBuilder(World.Runtime, World.Connector, World.Redactor, World.SqlPackageProcess)
            {
                AfterSealStep = (current, _) => current == step
                    ? Task.FromException(new SqlBranchException(ErrorCodes.OperationFailed, "Injected sealing failure."))
                    : Task.CompletedTask,
            };
            return builder.BuildAsync(new FoundPool(pool, "plain", PoolSource.EngineLabels, engine), 14330,
                new ImageBuildRequest("shop", "", 0), _package, _package, World.Environment, _tree.Root, CancellationToken.None);
        }

        public void AssertClean()
        {
            Assert.False(_database);
            Assert.False(_login);
            Assert.Empty(_record);
        }

        public void AssertOrder(params string[] fragments)
        {
            var index = -1;
            foreach (var fragment in fragments)
            {
                index = Timeline.FindIndex(index + 1, line => Has(line, fragment));
                Assert.True(index >= 0, $"Missing or out of order: {fragment}");
            }
        }

        public void Dispose() => _tree.Dispose();

        private static bool Has(string text, string fragment) => text.Contains(fragment, StringComparison.Ordinal);

        private static Dictionary<string, object?> FileRow(string name, string path, string type, long bytes) =>
            new() { ["name"] = name, ["physical_name"] = path, ["type_desc"] = type, ["bytes"] = bytes };
    }

    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(int milliseconds) => _milliseconds += milliseconds;
    }

    private sealed class NoticeWriter(List<string> timeline) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            if (value?.Contains("can take hours", StringComparison.Ordinal) is true)
            {
                timeline.Add("notice");
            }
            if (value?.StartsWith("Writing a BACPAC scratch copy", StringComparison.Ordinal) is true)
            {
                timeline.Add(value);
            }
            base.WriteLine(value);
        }
    }

    private sealed class ReadSource(string package, Clock clock, List<string> timeline, bool fail) : IBacpacReadSource
    {
        public ValueTask<long> HeadAsync(BackupUrl url, SqlBranch.Core.Secrets.StorageSecrets secrets, CancellationToken cancellationToken)
            => ValueTask.FromResult(new FileInfo(package).Length);

        public ValueTask<Stream> GetAsync(BackupUrl url, SqlBranch.Core.Secrets.StorageSecrets secrets, CancellationToken cancellationToken)
        {
            timeline.Add("GET package");
            clock.Advance(9);
            return fail ? throw new IOException("private diagnostic") : ValueTask.FromResult<Stream>(File.OpenRead(package));
        }
    }
}
