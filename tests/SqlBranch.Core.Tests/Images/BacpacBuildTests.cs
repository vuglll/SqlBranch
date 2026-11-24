using System.Globalization;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Configuration;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

public sealed class BacpacBuildTests
{
    [Theory]
    [InlineData("invalid", "bacpac_invalid")]
    [InlineData("damaged mapped package", "bacpac_invalid")]
    [InlineData("dangling login", "unsupported_feature")]
    [InlineData("one byte short", "pool_full")]
    [InlineData("exact boundary", "operation_failed")]
    public async Task Refusals_precede_engine_mutation_and_exact_boundary_reaches_create(string kind, string code)
    {
        using var tree = new TempTree();
        var file = tree.File("shop.bacpac", "text");
        if (kind != "invalid")
        {
            const string dangling = """
                <Element Type="SqlLogin" Name="[app]" />
                <Element Type="SqlCredential" Name="[credential]">
                    <Relationship Name="Identity"><Entry><References Name="[app]" /></Entry></Relationship>
                </Element>
                """;
            var model = kind switch
            {
                "dangling login" => dangling,
                "damaged mapped package" => BacpacPackageTests.User("report_reader", "LoginIdentifierScript"),
                _ => "",
            };
            BacpacPackageTests.Write(file, model, checksum: kind == "damaged mapped package" ? new string('0', 64) : null);
        }

        var bytes = new FileInfo(file).Length;
        var free = kind == "one byte short" ? bytes - 1 : bytes;
        var world = new CliWorld();
        world.WithEngine();
        var engine = world.Runtime.Containers[ContainerLabels.EngineName] with { Id = "owned", Hostname = "test-host", PublishedPorts = ["127.0.0.1:14330"] };
        world.Runtime.Containers[engine.Id!] = engine;
        var foldersAttempted = 0;
        world.Runtime.ExecHandler = (_, args) =>
        {
            var script = args[2];
            if (script.Contains("mkdir -m 700", StringComparison.Ordinal))
            {
                foldersAttempted++;
                return FakeContainerRuntime.Fail();
            }
            return FakeContainerRuntime.Ok(script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal)
                ? $"SQLBRANCH_FREE={free.ToString(CultureInfo.InvariantCulture)}\n" : "");
        };
        world.Connector.ScalarHandler = sql => sql.Contains("MachineName", StringComparison.Ordinal) ? "test-host" : 0;
        var pool = PoolFinder.LocationOf(engine, world.Services().Paths);
        var progress = new BacpacBuildProgress();
        var builder = new BacpacBuilder(world.Runtime, world.Connector, world.Redactor, world.SqlPackageProcess);
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => builder.BuildAsync(new FoundPool(pool, "plain", PoolSource.EngineLabels, engine),
            14330, new ImageBuildRequest("shop", "", 0), file, file, world.Environment, tree.Root, CancellationToken.None, progress));

        Assert.Equal(code, error.Code.Code);
        Assert.Equal(kind == "dangling login", progress.PhaseMilliseconds.ContainsKey("rewrite"));
        Assert.Equal(kind == "exact boundary" ? 1 : 0, foldersAttempted);
        Assert.DoesNotContain(world.Connector.Statements, s => s.StartsWith("CREATE", StringComparison.Ordinal));
        Assert.Single(world.SqlPackageProcess.Calls);
        if (kind == "one byte short")
        {
            Assert.Contains($"{bytes} bytes", error.Message, StringComparison.Ordinal);
            Assert.Contains($"{free} bytes", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Msg 1105, Level 17", "pool_full")]
    [InlineData("Msg 9002, Level 17", "pool_full")]
    [InlineData("Operating system error 28: No space left on device", "pool_full")]
    [InlineData("System.IO.IOException: No space left on device", "temp_full")]
    [InlineData("SHARD_MAP_MANAGER is not supported", "unsupported_feature")]
    [InlineData("Deployment failed", "operation_failed")]
    public void Later_failures_use_allowlisted_output(string diagnostic, string code)
    {
        const string syntheticSecret = "unit-test-secret%21";
        var redactor = new SecretRedactor();
        redactor.Register("SQL72045");
        var failure = BacpacBuilder.ImportFailure(
            new ProcessOutput(1, syntheticSecret + " SQL72014", diagnostic + " SQL72045 SQL72014 SQL720141"), redactor);
        Assert.Equal(code, failure.Code.Code);
        Assert.Contains("import", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(syntheticSecret, failure.Message, StringComparison.Ordinal);
        if (code == "operation_failed")
        {
            Assert.Contains("Phase: run SqlPackage.", failure.Message, StringComparison.Ordinal);
            Assert.Contains("Exception type: SqlBranchException.", failure.Message, StringComparison.Ordinal);
            Assert.Contains("SqlPackage exit code: 1.", failure.Message, StringComparison.Ordinal);
            Assert.Contains("SqlPackage diagnostic codes: [SQL72014, ***].", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(diagnostic, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Import_failure_reports_sql_error_numbers_but_not_their_text()
    {
        const string output = "*** Error SQL72014: Core Microsoft SqlClient Data Provider: Msg 15247, Level 16, State 1, Line 1 " +
            "User does not have permission to perform this action.\nError SQL72045: Script execution error.\n" +
            "Msg 15247, Level 16, State 1 repeated. Msg 15151, Level 16, State 1, Line 2 Cannot alter the login 'private_name'.";
        var failure = BacpacBuilder.ImportFailure(new ProcessOutput(1, output, ""), new SecretRedactor());
        Assert.Equal("operation_failed", failure.Code.Code);
        Assert.Contains("SQL Server error numbers: [15247, 15151].", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private_name", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unexpected_failure_reports_the_phase_and_only_safe_diagnostics(bool toolReturned)
    {
        const string rawOutput = "private tool output with unit-secret%21";
        const string rawSqlError = "private SQL failure with unit-secret!";
        const string id = "l0123456789abcdef0123456789abcdef";
        const string directoryIdentity = "5:91";
        using var tree = new TempTree();
        var file = tree.File("shop.bacpac", "");
        BacpacPackageTests.Write(file, "");
        var world = new CliWorld();
        world.WithEngine();
        var engine = world.Runtime.Containers[ContainerLabels.EngineName] with
        {
            Id = "owned", Hostname = "test-host", PublishedPorts = ["127.0.0.1:14330"],
        };
        world.Runtime.Containers[engine.Id!] = engine;
        var op = new BacpacOperation(id) { DatabaseGuid = Guid.NewGuid() };
        var loginAttempted = false;
        world.Runtime.ExecHandler = (_, args) =>
        {
            var script = args[2];
            if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_FREE=1048576\n");
            }
            if (script.EndsWith("stat -c '%d:%i' " + Quoting.Shell(op.Directory), StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok(directoryIdentity);
            }
            if (script.Contains("head -c 512", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok($"{engine.Id}|{id}|{(loginAttempted ? 1 : 0)}|{op.DatabaseGuid:D}|{directoryIdentity}");
            }
            return FakeContainerRuntime.Ok();
        };
        world.Connector.ScalarHandler = sql => sql switch
        {
            var s when s.Contains("MachineName", StringComparison.Ordinal) => engine.Hostname,
            var s when s.Contains("database_guid", StringComparison.Ordinal) => op.DatabaseGuid,
            _ => 0,
        };
        world.Connector.QueryHandler = sql => sql.Contains("sys.master_files", StringComparison.Ordinal) ? [[op.Data], [op.Log]] : [];
        world.Connector.ExecuteHandler = sql => loginAttempted |= sql.StartsWith("CREATE LOGIN", StringComparison.Ordinal);
        world.SqlPackageProcess.Handler = args => args.Contains("/Version")
            ? new ProcessOutput(0, "170.5.96.0", "")
            : toolReturned ? new ProcessOutput(0, "SQL72012", rawOutput) : throw new InvalidOperationException(rawOutput);
        world.Redactor.Register("50000");
        var pool = PoolFinder.LocationOf(engine, world.Services().Paths);
        var builder = new BacpacBuilder(world.Runtime, world.Connector, world.Redactor, world.SqlPackageProcess)
        {
            NewId = () => id,
            Consumer = new FailingConsumer(new EngineSqlException(rawSqlError, [3201, 50000])),
        };
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => builder.ImportAsync(new FoundPool(pool, "plain", PoolSource.EngineLabels, engine),
            14330, "shop", file, file, world.Environment, tree.Root, CancellationToken.None));

        Assert.Equal(ErrorCodes.OperationFailed, error.Code);
        Assert.DoesNotContain(rawOutput, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rawSqlError, error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        if (toolReturned)
        {
            Assert.Contains("Phase: consume the imported database.", error.Message, StringComparison.Ordinal);
            Assert.Contains("Exception type: EngineSqlException.", error.Message, StringComparison.Ordinal);
            Assert.Contains("SqlPackage exit code: 0.", error.Message, StringComparison.Ordinal);
            Assert.Contains("SqlPackage diagnostic codes: [SQL72012].", error.Message, StringComparison.Ordinal);
            Assert.Contains("SQL Server error numbers: [3201, ***].", error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Phase: run SqlPackage.", error.Message, StringComparison.Ordinal);
            Assert.Contains("Exception type: InvalidOperationException.", error.Message, StringComparison.Ordinal);
            Assert.Contains("SqlPackage exit code: unavailable.", error.Message, StringComparison.Ordinal);
            Assert.Contains("SqlPackage diagnostic codes: [].", error.Message, StringComparison.Ordinal);
        }
    }

    private sealed class FailingConsumer(EngineSqlException error) : IBacpacImportConsumer
    {
        public Task ConsumeAsync(BacpacImportTarget target, CancellationToken cancellationToken) => Task.FromException(error);
    }
    [Fact]
    public async Task A_bacpac_build_checks_an_unreadable_catalog_even_when_the_image_folder_exists()
    {
        using var tree = new TempTree();
        var file = tree.File("shop.bacpac", "");
        BacpacPackageTests.Write(file, "");
        var world = new CliWorld();
        world.WithEngine();
        Format2Pool.Apply(world);
        var engine = world.Runtime.Containers[ContainerLabels.EngineName] with { Id = "owned", Hostname = "test-host", PublishedPorts = ["127.0.0.1:14330"] };
        world.Runtime.Containers[engine.Id!] = engine;
        var scripts = new List<string>();
        world.Runtime.ExecHandler = (_, args) =>
        {
            var script = args[2];
            scripts.Add(script);
            return FakeContainerRuntime.Ok(script.Contains("SQLBRANCH_CATALOGFILE=", StringComparison.Ordinal)
                ? "SQLBRANCH_CATALOGFILE=" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{not json"))
                : script.Contains("echo exists", StringComparison.Ordinal) ? "exists" : "");
        };
        world.Connector.ScalarHandler = sql => sql.Contains("MachineName", StringComparison.Ordinal) ? "test-host" : 0;
        var pool = PoolFinder.LocationOf(engine, world.Services().Paths);
        var builder = new BacpacBuilder(world.Runtime, world.Connector, world.Redactor, world.SqlPackageProcess);
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => builder.BuildAsync(new FoundPool(pool, "plain", PoolSource.EngineLabels, engine),
            14330, new ImageBuildRequest("shop", "", 0), file, file, world.Environment, tree.Root, CancellationToken.None, new BacpacBuildProgress()));
        Assert.Equal(ErrorCodes.OperationFailed, error.Code);
        Assert.Contains("sqlbranch image rm shop", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(scripts, s => s.Contains("mkdir", StringComparison.Ordinal) || s.Contains("rm ", StringComparison.Ordinal));
        Assert.DoesNotContain(world.Connector.Statements, s => s.StartsWith("CREATE", StringComparison.Ordinal));
        Assert.Empty(world.Runtime.Copies);
        Assert.Empty(world.SqlPackageProcess.Calls);
    }

}
