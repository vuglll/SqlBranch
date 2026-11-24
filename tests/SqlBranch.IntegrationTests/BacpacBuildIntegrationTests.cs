using System.Security.Cryptography;
using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Core;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Output;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class BacpacBuildIntegrationTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly SqlClientEngineConnector Connector = new();
    private static readonly SystemProcessRunner Runner = new(inheritEnvironment: false);

    [IntegrationThinFact]
    public async Task Imports_seventeen_rows_with_a_restricted_login_and_reads_them_in_a_branch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Admin.ExecuteAsync("CREATE DATABASE [F2Foreign]", None);
        var runner = new ImportRunner(async (file, args, env, token) =>
        {
            var login = Argument(args, "/TargetUser:");
            var password = Argument(args, "/TargetPassword:");
            Assert.True(password != fixture.World.Password && args.Count(a => a.Contains(password, StringComparison.Ordinal)) == 1);
            Assert.True(env is not null && env.Keys.All(k =>
                k is "HOME" or "USERPROFILE" or "TMPDIR" or "TMP" or "TEMP" or "DOTNET_CLI_HOME" ||
                k.Equals("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("DOTNET_ROOT_", StringComparison.OrdinalIgnoreCase)));
            await using (var owner = await Connector.ConnectLoginAsync(fixture.World.Endpoint, login, password, login, token))
            {
                Assert.Equal("dbo", await owner.ScalarAsync("SELECT USER_NAME()", token));
                Assert.Equal(0, await owner.ScalarAsync("SELECT HAS_DBACCESS(N'F2Foreign')", token));
                Assert.Equal(0, await owner.ScalarAsync("""
                    SELECT COUNT(*) FROM sys.fn_my_permissions(NULL, 'SERVER')
                    WHERE permission_name IN ('CREATE ANY DATABASE', 'ALTER ANY LOGIN', 'CONTROL SERVER')
                    """, token));
            }
            return await Runner.RunAsync(file, args, env, token);
        });
        var before = await File.ReadAllBytesAsync(fixture.Package);
        var nativeBackup = Path.Combine(fixture.Client.FullName, "import-result.bak");
        var build = fixture.Builder(runner, consumer: new Consumer(async (target, token) =>
        {
            Assert.Equal(17, await fixture.Admin.ScalarAsync($"SELECT COUNT(*) FROM [{target.Database}].dbo.Items", token));
            await fixture.Admin.ExecuteAsync($"BACKUP DATABASE [{target.Database}] TO DISK=N'/tmp/f2-import-result.bak' WITH INIT, COPY_ONLY", token);
            var copy = await EngineWorld.DockerAsync("cp", $"{fixture.World.EngineName}:/tmp/f2-import-result.bak", nativeBackup);
            Assert.Equal(0, copy.ExitCode);
        }));
        await fixture.ImportAsync(build, "logical");
        await fixture.AssertCleanAsync();
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Package));
        // Use the existing backup route to verify branch data until task 2.4 connects the logical import to sealing.
        Assert.Equal(0, fixture.World.RunCli("image", "build", "branchbase", "--from-bak", nativeBackup).Exit);
        Assert.Equal(0, fixture.World.RunCli("branch", "create", "f2branch", "--from", "branchbase").Exit);
        Assert.Equal(17, await fixture.Admin.ScalarAsync("SELECT COUNT(*) FROM [f2branch].dbo.Items", None));
    }

    [IntegrationThinFact]
    public async Task A_bacpac_build_and_an_import_on_a_format_2_pool_publish_versions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Format2BuildIntegrationTests.UseFormat2Async(fixture.World);

        var built = fixture.World.RunCli("image", "build", "sales", "--from-bacpac", fixture.Package, "--sqlpackage", fixture.Executable, "--format", "json");
        Assert.True(built.Exit == 0, built.Out + built.Err);
        using (var json = JsonDocument.Parse(built.Out))
        {
            Assert.Matches(@"^sales@\d{8}T\d{9}Z$", json.RootElement.GetProperty("image").GetProperty("version").GetString());
        }
        await fixture.AssertCleanAsync();

        var scripts = Directory.CreateDirectory(Path.Combine(fixture.Client.FullName, "f2-scripts")).FullName;
        await File.WriteAllTextAsync(Path.Combine(scripts, "001.sql"), "CREATE TABLE dbo.T (id int); INSERT dbo.T VALUES (1);");
        Assert.Equal(0, fixture.World.RunCli("image", "build", "shopsrc", "--from-scripts", scripts).Exit);
        var archive = Path.Combine(fixture.Client.FullName, "shop.sbimg");
        Assert.Equal(0, fixture.World.RunCli("image", "export", "shopsrc", "--to", archive).Exit);
        var imported = fixture.World.RunCli("image", "import", archive, "--as", "shop3", "--format", "json");
        Assert.True(imported.Exit == 0, imported.Out + imported.Err);

        var list = fixture.World.RunCli("image", "list", "--format", "json");
        Assert.Equal(0, list.Exit);
        using var listed = JsonDocument.Parse(list.Out);
        var images = listed.RootElement.GetProperty("images").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!);
        foreach (var name in new[] { "sales", "shop3" })
        {
            Assert.Equal(JsonValueKind.String, images[name].GetProperty("version").ValueKind);
        }
        await fixture.AssertCleanAsync();
    }

    [IntegrationThinFact]
    public async Task Rolls_back_after_partial_import_failure_and_cancellation()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancel = new CancellationTokenSource();
        foreach (var cancelImport in new[] { false, true })
        {
            var imported = false;
            var runner = new ImportRunner(async (file, args, env, token) =>
            {
                var output = await Runner.RunAsync(file, args, env, token);
                Assert.Equal(0, output.ExitCode);
                var database = Argument(args, "/TargetDatabaseName:");
                Assert.Equal(17, await fixture.Admin.ScalarAsync($"SELECT COUNT(*) FROM [{database}].dbo.Items", token));
                imported = true;
                if (cancelImport)
                {
                    cancel.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                var password = Argument(args, "/TargetPassword:");
                return new ProcessOutput(1, "", Uri.EscapeDataString(password));
            });
            if (cancelImport)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ImportAsync(fixture.Builder(runner), "cancelled", cancel.Token));
            }
            else
            {
                var error = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.ImportAsync(fixture.Builder(runner), "partial"));
                Assert.Equal(ErrorCodes.OperationFailed, error.Code);
                Assert.Contains("Phase: run SqlPackage.", error.Message, StringComparison.Ordinal);
                Assert.Contains("SqlPackage exit code: 1.", error.Message, StringComparison.Ordinal);
                Assert.Contains("SqlPackage diagnostic codes: [].", error.Message, StringComparison.Ordinal);
            }
            Assert.True(imported);
            await fixture.AssertCleanAsync();
        }
    }

    [IntegrationThinFact]
    public async Task Preserves_a_foreign_collision_and_refuses_to_drop_a_target_with_an_outside_file()
    {
        await using var fixture = await Fixture.CreateAsync();
        var occupied = "l" + Guid.NewGuid().ToString("N");
        var collision = ImagePaths.BuildDatabase(occupied);
        await fixture.Admin.ExecuteAsync($"CREATE DATABASE [{collision}]; EXEC [{collision}].sys.sp_executesql " +
            "N'CREATE TABLE dbo.Sentinel (Id int); INSERT dbo.Sentinel VALUES (42);';", None);
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.ImportAsync(fixture.Builder(Runner, occupied), "collision"));
        Assert.Equal(ErrorCodes.OperationFailed, error.Code);
        Assert.Contains("already occupied", error.Message, StringComparison.Ordinal);
        Assert.Equal(42, await fixture.Admin.ScalarAsync($"SELECT Id FROM [{collision}].dbo.Sentinel", None));
        Assert.Equal(0, await fixture.Admin.ScalarAsync($"SELECT COUNT(*) FROM sys.sql_logins WHERE name=N'{collision}'", None));

        const string outside = "/pool/f2-sentinel.ndf";
        var escaped = "l" + Guid.NewGuid().ToString("N");
        var runner = new ImportRunner(async (file, args, env, token) =>
        {
            var output = await Runner.RunAsync(file, args, env, token);
            Assert.Equal(0, output.ExitCode);
            var database = Argument(args, "/TargetDatabaseName:");
            await fixture.Admin.ExecuteAsync($"ALTER DATABASE [{database}] ADD FILE (NAME=N'sentinel', FILENAME=N'{outside}')", token);
            return output;
        });
        error = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.ImportAsync(fixture.Builder(runner, escaped), "outside"));
        Assert.Equal(ErrorCodes.OperationFailed, error.Code);
        Assert.Contains(ImagePaths.BuildDatabase(escaped), error.Message, StringComparison.Ordinal);
        var present = await fixture.World.Runtime.ExecAsync(fixture.World.EngineName, ["test", "-f", outside], None);
        Assert.True(present.Succeeded);
        Assert.Equal(42, await fixture.Admin.ScalarAsync($"SELECT Id FROM [{collision}].dbo.Sentinel", None));
        Assert.Equal(1, await fixture.Admin.ScalarAsync($"SELECT CONVERT(int, is_disabled) FROM sys.sql_logins WHERE name=N'{ImagePaths.BuildDatabase(escaped)}'", None));
        Assert.Equal(1, fixture.World.RunCli("status").Exit);
    }

    [IntegrationThinFact]
    public async Task Reports_cleanup_failure_without_exposing_tool_output_or_publishing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operation = "l" + Guid.NewGuid().ToString("N");
        var owner = new Consumer(async (target, token) =>
        {
            var marker = await fixture.World.Runtime.ExecAsync(fixture.World.EngineName, ["touch", target.BuildDirectory + "/keep"], token);
            Assert.True(marker.Succeeded);
        });
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.ImportAsync(fixture.Builder(Runner, operation, owner), "cleanup"));
        Assert.Equal(ErrorCodes.OperationFailed, error.Code);
        Assert.Contains(operation, error.Message, StringComparison.Ordinal);
        Assert.True((await fixture.World.Runtime.ExecAsync(fixture.World.EngineName, ["test", "-f", ImagePaths.Build(operation) + "/keep"], None)).Succeeded);
        Assert.Equal(0, await fixture.Admin.ScalarAsync("SELECT COUNT(*) FROM sys.sql_logins WHERE name LIKE 'sqlbranch[_]build[_]l%'", None));
        var status = fixture.World.RunCli("status", "--format", "json");
        Assert.Equal(1, status.Exit);
        Assert.Contains("bacpac_build", status.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.World.Password, status.Out + status.Err, StringComparison.Ordinal);
        Assert.Equal(0, fixture.World.RunCli("image", "list").Exit);
        Assert.False((await fixture.World.Runtime.ExecAsync(fixture.World.EngineName, ["test", "-e", ImagePaths.Manifest("cleanup")], None)).Succeeded);
    }

    [IntegrationThinFact]
    public async Task A_later_build_sweeps_a_killed_import_and_preserves_a_live_one()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new SkipTestException("The abrupt-termination fixture uses a POSIX process wrapper.");
        }
        await using var fixture = await Fixture.CreateAsync();
        var wrapper = Path.Combine(fixture.Client.FullName, "wait-for-kill");
        var executable = "'" + fixture.Executable.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        await File.WriteAllTextAsync(wrapper, $"#!/bin/sh\nif [ \"$1\" = /Version ]; then exec {executable} /Version; fi\n" +
            ": > \"${0%/*}/${4#/TargetDatabaseName:}.ready\"\nexec /bin/sleep 300\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var abandoned = fixture.World.StartCli("image", "build", "abandoned", "--from-bacpac", fixture.Package, "--sqlpackage", wrapper);
        var abandonedLogin = Assert.Single(await fixture.WaitForLoginsAsync(1));
        using var live = fixture.World.StartCli("image", "build", "live", "--from-bacpac", fixture.Package, "--sqlpackage", wrapper);
        var liveLogin = Assert.Single(await fixture.WaitForLoginsAsync(2), login => login != abandonedLogin);
        abandoned.Kill();

        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        while (fixture.World.RunCli("status", "--format", "json").Exit == 0)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Status still considers both builds active.");
            await Task.Delay(100);
        }
        var status = fixture.World.RunCli("status", "--format", "json");
        using var report = JsonDocument.Parse(status.Out);
        Assert.Equal("mismatch", report.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(liveLogin[StatusReporter.BuildPrefix.Length..], report.RootElement.GetProperty("inProgress").GetRawText(), StringComparison.Ordinal);
        Assert.Contains(abandonedLogin, report.RootElement.GetProperty("mismatches").GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.World.Password, status.Out + status.Err, StringComparison.Ordinal);

        await fixture.ImportAsync(fixture.Builder(Runner), "sweep");
        Assert.Equal(liveLogin, Assert.Single(await fixture.BuildLoginsAsync()));
        Assert.Equal(1, await fixture.Admin.ScalarAsync($"SELECT COUNT(*) FROM sys.databases WHERE name=N'{liveLogin}'", None));
        Assert.Equal(0, await fixture.Admin.ScalarAsync($"SELECT COUNT(*) FROM sys.databases WHERE name=N'{abandonedLogin}'", None));

        live.Kill();
        await fixture.RemoveKilledScratchAsync(abandonedLogin, liveLogin);
    }

    [IntegrationThinFact]
    public async Task A_kill_after_cleanup_leaves_a_reported_stage_and_the_same_image_can_be_retried()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string image = "interrupted-publish";
        var ready = Path.Combine(fixture.Client.FullName, "publish-ready");
        string[] args = ["image", "build", image, "--from-bacpac", fixture.Package, "--sqlpackage", fixture.Executable, "--format", "json"];
        using var client = fixture.World.StartCli(new Dictionary<string, string> { ["SQLBRANCH_HARNESS_PUBLISH_READY"] = ready }, args);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        while (!File.Exists(ready))
        {
            if (client.HasExited)
            {
                var early = await client.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail("The build exited before publication. " + fixture.World.Redactor.Redact(early.Out + early.Err));
            }
            await Task.Delay(50, deadline.Token);
        }

        Assert.Empty(await fixture.BuildLoginsAsync());
        Assert.True((await fixture.World.Runtime.ExecAsync(fixture.World.EngineName, ["test", "!", "-e", ImagePaths.Image(image)], None)).Succeeded);
        client.Kill();
        await client.WaitAsync(TimeSpan.FromSeconds(15));

        string operation;
        while (true)
        {
            var status = fixture.World.RunCli("status", "--format", "json");
            if (status.Exit == 1)
            {
                using var json = JsonDocument.Parse(status.Out);
                var abandoned = Assert.Single(json.RootElement.GetProperty("mismatches").EnumerateArray(),
                    item => item.GetProperty("kind").GetString() == "bacpac_build");
                operation = abandoned.GetProperty("name").GetString()!;
                Assert.True(BacpacOperation.IsLogicalId(operation));
                break;
            }
            Assert.Equal(0, status.Exit);
            await Task.Delay(100, deadline.Token);
        }

        var staged = await fixture.World.Runtime.ExecAsync(fixture.World.EngineName,
            ["sh", "-c", $"test -f {Quoting.Shell(ImagePaths.Build(operation) + "/bacpac.owner")} && " +
                $"test -f {Quoting.Shell(ImagePaths.Build(operation) + "/image/manifest.json")}"], None);
        Assert.True(staged.Succeeded);
        var retry = fixture.World.RunCli(args);
        Assert.True(retry.Exit == 0, retry.Out + retry.Err);
        Assert.True((await fixture.World.Runtime.ExecAsync(fixture.World.EngineName, ["test", "-f", ImagePaths.Manifest(image)], None)).Succeeded);
        await fixture.AssertCleanAsync();
    }

    [IntegrationThinFact]
    public async Task Masks_in_order_and_branches_with_default_and_preserved_recovery_and_a_rebuilt_log()
    {
        await using var fixture = await Fixture.CreateAsync("ALTER TABLE dbo.Items ADD Email nvarchar(100) NULL;");
        var seed = Path.Combine(fixture.Client.FullName, "z-seed.sql");
        var mask = Path.Combine(fixture.Client.FullName, "a-mask.sql");
        await File.WriteAllTextAsync(seed, """
            UPDATE dbo.Items SET Email=CONCAT(Id, '@source.invalid');
            """);
        await File.WriteAllTextAsync(mask, """
            UPDATE dbo.Items SET Email=CONCAT(Id, '@masked.invalid') WHERE Email LIKE '%@source.invalid';
            """);
        var runner = new ImportRunner(async (file, args, env, token) =>
        {
            var result = await Runner.RunAsync(file, args, env, token);
            if (result.ExitCode == 0)
            {
                var db = Quoting.Identifier(Argument(args, "/TargetDatabaseName:"));
                await fixture.Admin.ExecuteAsync($"ALTER DATABASE {db} SET RECOVERY FULL; ALTER DATABASE {db} MODIFY FILE (NAME=log, SIZE=64MB);", token);
            }
            return result;
        });

        foreach (var (image, recovery) in new[] { ("simple", "SIMPLE"), ("full", "FULL") })
        {
            var args = new List<string> { "--post-script", seed, "--post-script", mask };
            if (recovery == "FULL")
            {
                args.Add("--keep-recovery-model");
            }
            var build = fixture.BuildCli(image, fixture.Package, runner, [.. args]);
            Assert.True(build.Exit == 0, build.Out);
            using var json = JsonDocument.Parse(build.Out);
            var manifest = json.RootElement.GetProperty("image");
            Assert.Equal(["z-seed.sql", "a-mask.sql"],
                manifest.GetProperty("postScripts").EnumerateArray().Select(script => script.GetProperty("name").GetString()));
            Assert.Equal("FULL", manifest.GetProperty("originalRecoveryModel").GetString());
            Assert.Equal(recovery, manifest.GetProperty("sealedRecoveryModel").GetString());
            Assert.True(manifest.GetProperty("logRebuilt").GetBoolean());
            var log = Assert.Single(manifest.GetProperty("files").EnumerateArray(), file => file.GetProperty("type").GetString() == "log");
            Assert.InRange(log.GetProperty("sizeBytes").GetInt64(), 1, 2_097_152);
            Assert.Equal(67_108_864, manifest.GetProperty("originalLogSizeBytes").GetInt64());
            var branch = image + "branch";
            Assert.Equal(0, fixture.World.RunCli("branch", "create", branch, "--from", image).Exit);
            Assert.Equal(17, await fixture.Admin.ScalarAsync($"SELECT COUNT(*) FROM [{branch}].dbo.Items WHERE Email LIKE '%@masked.invalid'", None));
            Assert.Equal(recovery, await fixture.Admin.ScalarAsync($"SELECT recovery_model_desc FROM sys.databases WHERE name=N'{branch}'", None));
            Assert.Equal(1, await fixture.Admin.ScalarAsync(
                $"SELECT COUNT(*) FROM [{branch}].sys.database_principals WHERE name=N'dbo' AND sid=SUSER_SID(N'sa')", None));
        }
        await fixture.AssertCleanAsync();
    }

    [IntegrationThinFact]
    public async Task Branch_dbo_sid_matches_sa_without_post_scripts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var build = fixture.BuildCli("owned", fixture.Package, Runner, []);
        Assert.True(build.Exit == 0, build.Out);
        var branch = fixture.World.RunCli("branch", "create", "ownedbranch", "--from", "owned");
        Assert.True(branch.Exit == 0, branch.Out + branch.Err);
        Assert.Equal(1, await fixture.Admin.ScalarAsync(
            "SELECT COUNT(*) FROM [ownedbranch].sys.database_principals WHERE name=N'dbo' AND sid=SUSER_SID(N'sa')", None));
        await fixture.AssertCleanAsync();
    }

    [IntegrationThinFact]
    public async Task Refuses_an_imported_ownership_trigger_before_running_post_scripts()
    {
        await using var fixture = await Fixture.CreateAsync("""
            CREATE TRIGGER on_owner ON DATABASE
            FOR ALTER_AUTHORIZATION_DATABASE
            AS
            BEGIN
                SET NOCOUNT ON;
                THROW 50000, 'Ownership changed', 1;
            END;
            """);
        var script = Path.Combine(fixture.Client.FullName, "must-not-run.sql");
        await File.WriteAllTextAsync(script, "THROW 50000, 'Post-script ran unexpectedly', 1;");
        var build = fixture.BuildCli("trigger", fixture.Package, Runner, ["--post-script", script]);
        Assert.Equal(1, build.Exit);
        using var json = JsonDocument.Parse(build.Out);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("ddl_trigger", error.GetProperty("code").GetString());
        Assert.Contains("on_owner", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await fixture.AssertUnpublishedAsync("trigger");
        await fixture.AssertCleanAsync();
    }

    [IntegrationThinFact]
    public async Task Rolls_back_seal_failures_without_publishing_a_manifest()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var step in new[] { "rebuild-log", "seal" })
        {
            var builder = new BacpacBuilder(fixture.World.Runtime, Connector, fixture.World.Redactor, Runner, fixture.World.EngineName)
            {
                AfterSealStep = async (current, _) =>
                {
                    if (current == step)
                    {
                        await fixture.AssertUnpublishedAsync("rollback");
                        throw new SqlBranchException(ErrorCodes.OperationFailed, "Injected sealing failure.");
                    }
                },
            };
            var error = await Assert.ThrowsAsync<SqlBranchException>(() => fixture.BuildAsync(builder, "rollback"));
            Assert.Equal("Injected sealing failure.", error.Message);
            await fixture.AssertUnpublishedAsync("rollback");
            await fixture.AssertCleanAsync();
        }
        var success = await fixture.BuildAsync(fixture.Builder(Runner), "rollback");
        Assert.Equal("bacpac", success.Manifest.SourceType);
        await fixture.AssertCleanAsync();
    }

    [IntegrationThinFact]
    public async Task Local_and_url_bacpac_provenance_survives_export_import_and_reexport()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string url = "https://fixture.blob.core.windows.net/db/real.bacpac";
        var package = await File.ReadAllBytesAsync(fixture.Package);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(package));
        foreach (var remote in new[] { false, true })
        {
            var image = remote ? "remote" : "local";
            var revived = image + "again";
            var originalArchive = Path.Combine(fixture.Client.FullName, image + ".sbimg");
            var renamedArchive = Path.Combine(fixture.Client.FullName, revived + ".sbimg");
            var build = fixture.BuildCli(image, remote ? url : fixture.Package, Runner, [],
                remote ? new FixtureReadSource(package) : null);
            Assert.True(build.Exit == 0, build.Out);
            Assert.Equal(0, fixture.World.RunCli("image", "export", image, "--to", originalArchive).Exit);
            Assert.Equal(0, fixture.World.RunCli("image", "import", originalArchive, "--as", revived).Exit);
            Assert.Equal(0, fixture.World.RunCli("image", "export", revived, "--to", renamedArchive).Exit);
            await using var first = await ImageArchiveReader.OpenAsync(File.OpenRead(originalArchive), None);
            await using var second = await ImageArchiveReader.OpenAsync(File.OpenRead(renamedArchive), None);
            var manifest = second.Head.Manifest;
            Assert.Equal(1, ImageArchive.CurrentFormat);
            Assert.Equal("bacpac", manifest.SourceType);
            Assert.Equal("real.bacpac", manifest.BacpacFile);
            Assert.Equal(sha256, manifest.BacpacSha256);
            Assert.Equal(remote ? url : null, manifest.BacpacUrl);
            Assert.Equal((first.Head.Manifest with { Name = revived }).ToJson(), manifest.ToJson());
            Assert.All(manifest.Files, file => Assert.True(file.Type is "data" or "log"));
            Assert.Equal(0, fixture.World.RunCli("branch", "create", revived + "branch", "--from", revived).Exit);
            Assert.Equal(17, await fixture.Admin.ScalarAsync($"SELECT COUNT(*) FROM [{revived}branch].dbo.Items", None));
        }
        Assert.Equal(package, await File.ReadAllBytesAsync(fixture.Package));
        await fixture.AssertCleanAsync();
    }

    private sealed class FixtureReadSource(byte[] bytes) : IBacpacReadSource
    {
        public ValueTask<long> HeadAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken) =>
            ValueTask.FromResult(bytes.LongLength);

        public ValueTask<Stream> GetAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private static string Argument(IReadOnlyList<string> args, string prefix) =>
        args.Single(arg => arg.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    private sealed class ImportRunner(
        Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?, CancellationToken, Task<ProcessOutput>> import) : IProcessRunner
    {
        public Task<ProcessOutput> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, string? standardInput = null) =>
            arguments.Contains("/Action:Import") ? import(fileName, arguments, environment, cancellationToken)
                : Runner.RunAsync(fileName, arguments, environment, cancellationToken, standardInput);

        public IStreamingProcess Start(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, StreamDirection direction, CancellationToken cancellationToken) =>
            Runner.Start(fileName, arguments, environment, direction, cancellationToken);
    }

    private sealed class Consumer(Func<BacpacImportTarget, CancellationToken, Task> consume) : IBacpacImportConsumer
    {
        public Task ConsumeAsync(BacpacImportTarget target, CancellationToken cancellationToken) => consume(target, cancellationToken);
    }

    private sealed class Fixture(EngineWorld world, DirectoryInfo client, IEngineSession admin, FoundPool pool, string executable) : IAsyncDisposable
    {
        public EngineWorld World { get; } = world;
        public DirectoryInfo Client { get; } = client;
        public IEngineSession Admin { get; } = admin;
        public string Executable { get; } = executable;
        public string Package => Path.Combine(Client.FullName, "real.bacpac");

        public static async Task<Fixture> CreateAsync(string? additionalSql = null)
        {
            var path = Environment.GetEnvironmentVariable("SQLBRANCH_IT_SQLPACKAGE");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new SkipTestException("Set SQLBRANCH_IT_SQLPACKAGE to the installed SqlPackage executable.");
            }
            path = Path.GetFullPath(path);
            var world = new EngineWorld("2GB");
            var client = Directory.CreateTempSubdirectory("sqlbranch-it-local-bacpac-");
            IEngineSession? admin = null;
            try
            {
                Assert.Equal(0, world.RunCli("init").Exit);
                admin = await Connector.ConnectAsync(world.Endpoint, world.Password, None);
                var found = await world.FindPoolAsync();
                var fixture = new Fixture(world, client, admin, found, path);
                var setupSql = """
                    CREATE TABLE dbo.Items (Id int NOT NULL PRIMARY KEY);
                    INSERT dbo.Items SELECT TOP (17) ROW_NUMBER() OVER (ORDER BY object_id) FROM sys.all_objects;
                    """;
                if (additionalSql is not null)
                {
                    setupSql += $"\nEXEC sys.sp_executesql {Quoting.Literal(additionalSql)};";
                }
                await SqlPackageFixture.ExportAsync(world, admin, path, client.FullName, fixture.Package, setupSql);
                return fixture;
            }
            catch
            {
                if (admin is not null)
                {
                    await admin.DisposeAsync();
                }
                await world.DisposeAsync();
                client.Delete(recursive: true);
                throw;
            }
        }

        public BacpacBuilder Builder(IProcessRunner runner, string? id = null, IBacpacImportConsumer? consumer = null) =>
            new(World.Runtime, Connector, World.Redactor, runner, World.EngineName)
            {
                NewId = () => id ?? "l" + Guid.NewGuid().ToString("N"),
                Consumer = consumer,
            };

        public Task ImportAsync(BacpacBuilder builder, string image, CancellationToken token = default) =>
            builder.ImportAsync(pool, World.Port, image, Package, Executable, World.Environment, Client.FullName, token);

        public Task<ImageBuildResult> BuildAsync(BacpacBuilder builder, string image) =>
            builder.BuildAsync(pool, World.Port, new ImageBuildRequest(image, "", 0), Package,
                Executable, World.Environment, Client.FullName, None);

        public (int Exit, string Out) BuildCli(string image, string source, IProcessRunner runner, string[] options,
            IBacpacReadSource? readSource = null)
        {
            var environment = new Dictionary<string, string>(World.Environment)
            {
                [BackupUrl.SasVariable] = "sig=fixture-only",
            };
            var services = new CliServices(World.Runtime, Connector, environment, Client.FullName,
                World.HomeDirectory, World.Paths, EngineTimings.Default, World.EngineName,
                PoolVolume: World.PoolVolume, SqlPackageProcess: runner, BacpacReadSource: readSource);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = CliApp.CreateDefault(World.Redactor, services).Run(
                ["image", "build", image, "--from-bacpac", source, "--sqlpackage", Executable, "--format", "json", .. options],
                stdout, stderr);
            return (exit, stdout.ToString());
        }

        public async Task AssertUnpublishedAsync(string image)
        {
            var result = await World.Runtime.ExecAsync(World.EngineName,
                ["sh", "-c", $"test ! -e {Quoting.Shell(ImagePaths.Manifest(image))}"], None);
            Assert.True(result.Succeeded, "The unpublished image has a manifest.");
        }

        public async Task<string[]> BuildLoginsAsync() =>
            (await Admin.QueryAsync("SELECT name FROM sys.sql_logins WHERE name LIKE 'sqlbranch[_]build[_]l%' ORDER BY name", None))
                .Select(row => (string)row[0]!).ToArray();

        public async Task<string[]> WaitForLoginsAsync(int expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            while (true)
            {
                var logins = await BuildLoginsAsync();
                if (logins.Length == expected && logins.All(login => File.Exists(Path.Combine(Client.FullName, login + ".ready"))))
                {
                    return logins;
                }
                await Task.Delay(100, deadline.Token);
            }
        }

        public async Task AssertCleanAsync()
        {
            Assert.Empty(await BuildLoginsAsync());
            Assert.Equal(0, await Admin.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]l%'", None));
            Assert.Equal(0, World.RunCli("status").Exit);
        }

        public async Task RemoveKilledScratchAsync(params string[] logins)
        {
            var engineId = pool.Engine!.Id;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var expected = logins.Select(login => "sqlbranch-bacpac-" + login[StatusReporter.BuildPrefix.Length..] + "-").ToArray();
            while (true)
            {
                var dirs = BacpacScratch.FindAbandoned(engineId);
                if (expected.All(prefix => dirs.Any(name => name.StartsWith(prefix, StringComparison.Ordinal))))
                {
                    foreach (var name in dirs.Where(name => expected.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))))
                    {
                        Directory.Delete(Path.Combine(Path.GetTempPath(), name), recursive: true);
                    }
                    return;
                }
                await Task.Delay(100, deadline.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Admin.DisposeAsync();
            await World.DisposeAsync();
            Client.Delete(recursive: true);
        }
    }
}
