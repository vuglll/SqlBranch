using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class BacpacLoginDetachSpikeTests(ITestOutputHelper output)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly SqlClientEngineConnector Connector = new();

    [IntegrationThinFact]
    public async Task Rewritten_native_and_azure_models_import_without_server_login_changes()
    {
        var executable = Environment.GetEnvironmentVariable("SQLBRANCH_IT_SQLPACKAGE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new SkipTestException("Set SQLBRANCH_IT_SQLPACKAGE to the installed SqlPackage executable.");
        }
        executable = Path.GetFullPath(executable);

        var results = Path.Combine(AppContext.BaseDirectory, "bacpac-login-detach-spike-results.txt");
        using var report = new StreamWriter(results) { AutoFlush = true };
        output.WriteLine($"Results file: {results}");
        void Record(string message)
        {
            report.WriteLine(message);
            output.WriteLine(message);
        }

        var scratch = Directory.CreateTempSubdirectory("sqlbranch-it-bacpac-detach-");
        try
        {
            var version = await SqlPackageFixture.Runner.RunAsync(executable, ["/Version"],
                SqlPackageFixture.ScratchEnvironment(scratch.FullName), None);
            Assert.Equal(0, version.ExitCode);
            Assert.True(Version.TryParse(version.StdOut.Trim(), out var parsedVersion));
            Record($"SqlPackage version: {parsedVersion}");

            await using var world = new EngineWorld();
            var init = world.RunCli("init");
            Assert.True(init.Exit == 0, init.Out + init.Err);
            var status = world.RunCli("status", "--format", "json");
            Assert.True(status.Exit == 0, status.Out + status.Err);
            var mode = BranchKit.Parse(status.Out).GetProperty("pool").GetProperty("cloneMode").GetString();
            Assert.True(mode is "thin" or "copy");
            Record($"Clone mode: {mode}");
            await using var admin = await Connector.ConnectAsync(world.Endpoint, world.Password, None);

            var (native, azure) = await BacpacLoginFixture.CreateAsync(world, admin, executable, scratch.FullName, Record);
            var copies = new[] { (Name: "native", Path: native), (Name: "azure", Path: azure) };

            // The import must leave one login absent and preserve the other and its server role.
            await admin.ExecuteAsync("DROP LOGIN [report_reader]", None);
            var pool = await world.FindPoolAsync();
            var failures = new List<string>();
            foreach (var copy in copies)
            {
                var before = await BacpacLoginFixture.ServerStateAsync(admin);
                Record($"{copy.Name} server state before import: {before}");
                var error = await Xunit.Record.ExceptionAsync(async () =>
                {
                    var probe = new ImportProbe(admin, Record);
                    var builder = new BacpacBuilder(world.Runtime, Connector, world.Redactor, SqlPackageFixture.Runner, world.EngineName)
                    {
                        Consumer = probe,
                    };
                    try
                    {
                        await builder.ImportAsync(pool, world.Port, copy.Name, copy.Path, executable,
                            world.Environment, scratch.FullName, None);
                    }
                    finally
                    {
                        var after = await BacpacLoginFixture.ServerStateAsync(admin);
                        Record($"{copy.Name} server state after import: {after}");
                        Assert.Equal(before, after);
                    }
                    Assert.IsType<BacpacLoginDatabaseState>(probe.State).AssertDetached();
                });
                if (error is not null)
                {
                    var failure = $"{copy.Name}: {world.Redactor.Redact(error.Message)}";
                    Record(failure);
                    failures.Add(failure);
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private sealed class ImportProbe(IEngineSession admin, Action<string> record) : IBacpacImportConsumer
    {
        public BacpacLoginDatabaseState? State { get; private set; }

        public async Task ConsumeAsync(BacpacImportTarget target, CancellationToken cancellationToken)
        {
            var state = await BacpacLoginDatabaseState.ReadAsync(admin, target.Database, cancellationToken);
            State = state;
            record($"Imported principals (name, type, authentication): {state.Principals}");
            record($"reports owner: {state.Owner}; dbo.Orders SELECT grants: {state.Grants}; sales_app roles: {state.Roles}; order rows: {state.Orders}");
        }
    }
}
