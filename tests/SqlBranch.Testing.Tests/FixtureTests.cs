using System.Text;
using Microsoft.Data.SqlClient;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Images;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Testing.Tests;

public sealed class FixtureTests : IDisposable
{
    private const string Password = "Stored#Pass1";
    private readonly FakeContainerRuntime _runtime = new();
    private readonly FakeEngineConnector _connector = new();
    private readonly FixtureProcess _process = new();
    private readonly FakeInitHost _engine;
    private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("sb-fixture-");
    private readonly Dictionary<string, string> _environment = [];
    private readonly List<string> _log = [];
    private readonly Action<string> _oldLog = SqlBranchEnvironment.Log;
    private string _manifest;
    private readonly HashSet<string> _collisions = [];
    private int _prunes;
    private bool _failPrune;

    public FixtureTests()
    {
        _engine = new(_runtime, _connector);
        _engine.AddExistingEngine("test-image", "plain", FakeInitHost.PoolDir, port: "");
        _manifest = Encode(new ImageManifest(1, "shop", "shop", "16",
            [new ImageFile("shop", "shop.mdf", "data", 8192)], "SIMPLE", "SIMPLE", 8192,
            true, DateTimeOffset.UnixEpoch, null, null, new Dictionary<string, string>()).ToJson());
        var previous = _runtime.ExecHandler;
        _runtime.ExecHandler = (name, args) =>
        {
            var script = args[^1];
            if (script == BranchLister.ListingScript)
            {
                _prunes++;
                if (_failPrune)
                {
                    // Return the expired candidate. Its remove will fail on reading its record.
                    var record = new BranchRecord("shop", "thin", DateTimeOffset.UnixEpoch,
                        new Dictionary<string, string> { ["sqlbranch.owner"] = "testing" }, Ephemeral: true, ExpiresAt: DateTimeOffset.UnixEpoch);
                    return FakeContainerRuntime.Ok("SQLBRANCH_BR|t-expired|" + Encode(record.ToJson()) + "|0");
                }
                return FakeContainerRuntime.Ok();
            }
            if (script.Contains("SQLBRANCH_RECORD=", StringComparison.Ordinal))
            {
                return _failPrune ? FakeContainerRuntime.Fail() : FakeContainerRuntime.Ok("SQLBRANCH_RECORD=none");
            }
            if (script.Contains("SQLBRANCH_MANIFEST=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_MANIFEST=" + _manifest);
            }
            if (script.Contains("SQLBRANCH_BRANCH=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_BRANCH=" + (_collisions.Any(n => script.Contains(n, StringComparison.Ordinal)) ? "branch" : "none"));
            }
            return previous(name, args);
        };
        _connector.ParameterizedScalarHandler = (sql, _) =>
            sql.Contains("sys.databases", StringComparison.Ordinal) || sql.Contains("sys.master_files", StringComparison.Ordinal) ? 0 : 1;
        SqlBranchEnvironment.Log = _log.Add;
    }

    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
    }

    private FixtureHost Host() => new(_runtime, _connector, new SecretRedactor(), new SettingsSources
    {
        WorkingDirectory = Path.Combine(_home.FullName, "bin", "Debug", "net10.0"),
        HomeDirectory = _home.FullName,
        Environment = _environment,
    }, ManagedPaths.Default, clock: new Clock());

    private Task<SqlBranchDatabase> Create(SqlBranchOptions? options = null, Func<string>? suffix = null, CancellationToken ct = default) =>
        SqlBranchDatabase.CreateAsync("shop", options ?? new(), Host(), _process, suffix ?? (() => Guid.NewGuid().ToString("N")[..8]), ct);

    [Fact]
    public async Task Invalid_options_are_rejected_before_discovery()
    {
        SqlBranchOptions[] invalid = [new() { Prefix = "Orders" }, new() { Prefix = new string('a', 55) },
            new() { TimeLimit = TimeSpan.Zero }, new() { TimeLimit = TimeSpan.FromDays(366) },
            new() { Labels = new Dictionary<string, string> { ["sqlbranch.owner"] = "mine" } },
            new() { Labels = new Dictionary<string, string> { ["bad key"] = "x" } }];
        foreach (var options in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Create(options));
        }
        Assert.Empty(_runtime.Calls);
        Assert.Empty(_connector.Statements);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Name_collisions_retry_at_most_three_times(int taken)
    {
        var i = 0;
        string Next() => (++i).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var n in Enumerable.Range(1, taken))
        {
            _collisions.Add($"t-{n:x8}");
        }
        if (taken == 4)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(suffix: Next));
            Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
            Assert.Equal(4, i);
        }
        else
        {
            await using var db = await Create(suffix: Next);
            Assert.Equal("t-00000002", db.Name);
            Assert.Equal(new DateTimeOffset(2026, 12, 1, 11, 0, 0, TimeSpan.Zero), db.ExpiresAt);
        }
    }

    [Fact]
    public async Task Failed_prune_is_logged_and_retried_until_success()
    {
        _failPrune = true;
        await using var a = await Create();
        Assert.Contains(_log, message => message.Contains("t-expired", StringComparison.Ordinal));
        _failPrune = false;
        await using var b = await Create();
        await using var c = await Create();
        Assert.Equal(2, _prunes);
    }

    [Fact]
    public async Task Live_limit_waits_and_cancellation_leaves_capacity_intact()
    {
        _process.Limit = 2;
        await using var a = await Create();
        await using var b = await Create();
        using var cancel = new CancellationTokenSource();
        var cancelled = Create(ct: cancel.Token);
        Assert.False(cancelled.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var third = Create();
        Assert.False(third.IsCompleted);
        await b.DisposeAsync();
        await using var c = await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Availability_and_fix_commands_describe_missing_components()
    {
        Assert.True(await Host().IsAvailableAsync(default));
        _engine.AddExistingEngine("test-image", "plain", FakeInitHost.PoolDir, running: false);
        Assert.False(await Host().IsAvailableAsync(default));
        Assert.Equal("sqlbranch engine start", (await Assert.ThrowsAsync<SqlBranchUnavailableException>(() => Create())).FixCommand);
        _runtime.Containers.Clear();
        Assert.Equal("sqlbranch init", (await Assert.ThrowsAsync<SqlBranchUnavailableException>(() => Create())).FixCommand);
        _runtime.InfoFailure = new IOException("Docker missing");
        Assert.False(await Host().IsAvailableAsync(default));
        _runtime.InfoFailure = null;
        _engine.AddExistingEngine("test-image", "plain", FakeInitHost.PoolDir);
        _manifest = "none";
        Assert.Equal("sqlbranch image build shop", (await Assert.ThrowsAsync<SqlBranchUnavailableException>(() => Create())).FixCommand);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_with_a_removed_or_unknown_base_throws_naming_a_new_branch_and_without_the_password(bool unknown)
    {
        await using var db = await Create();
        _engine.CredentialsJson = SqlBranch.Core.Engine.CredentialsFile.Format2(Password, null).ToJson();
        var record = new BranchRecord("shop", "thin", DateTimeOffset.UnixEpoch, new Dictionary<string, string>(),
            ImageVersion: unknown ? null : "shop@20261201T090000000Z", BaseKnown: !unknown);
        var previous = _runtime.ExecHandler;
        var changed = false;
        _runtime.ExecHandler = (name, args) =>
        {
            var script = args[^1];
            if (script.Contains("SQLBRANCH_RECORD=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_RECORD=" + Encode(record.ToJson()));
            }

            if (script.Contains("SQLBRANCH_CATALOGFILE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_CATALOGFILE=" + Encode(new ImageCatalogFile("shop", "shop@20261203T020000000Z", [new ImageVersionRef("shop@20261203T020000000Z", ".")]).ToJson()));
            }

            changed |= script.Contains("base64 -d", StringComparison.Ordinal) || script.Contains("/next", StringComparison.Ordinal);
            return previous(name, args);
        };
        _connector.Statements.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.ResetAsync());

        Assert.Contains(db.Name, error.Message, StringComparison.Ordinal);
        Assert.Contains("'shop'", error.Message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch branch create", error.Message, StringComparison.Ordinal);
        Assert.Contains("no longer exists", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlbranch image build", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, error.Message, StringComparison.Ordinal);
        Assert.False(changed);
        Assert.DoesNotContain(_connector.Statements, s => s.Contains("DROP DATABASE", StringComparison.Ordinal) || s.Contains("SINGLE_USER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_tolerates_missing_branch_and_connection_failure()
    {
        var missing = await Create();
        await missing.DisposeAsync();
        var stopped = await Create();
        _engine.AddExistingEngine("test-image", "plain", FakeInitHost.PoolDir, running: false);
        _connector.NeverReady = true;
        await stopped.DisposeAsync();
        Assert.Contains(_log, s => s.Contains(stopped.Name, StringComparison.Ordinal) && s.Contains("sqlbranch branch prune", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configuration_and_redaction_follow_the_cli()
    {
        Directory.CreateDirectory(Path.Combine(_home.FullName, "bin", "Debug", "net10.0"));
        File.WriteAllText(Path.Combine(_home.FullName, ".sqlbranch.yml"), "port: 15000");
        await using var a = await Create();
        Assert.EndsWith(",15000", new SqlConnectionStringBuilder(a.ConnectionString).DataSource, StringComparison.Ordinal);
        _environment["SQLBRANCH_PORT"] = "16000";
        await using var b = await Create();
        Assert.EndsWith(",16000", new SqlConnectionStringBuilder(b.ConnectionString).DataSource, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, b.ToString(), StringComparison.Ordinal);
        Assert.Contains(b.Name, b.ToString(), StringComparison.Ordinal);
        _connector.ExecuteHandler = sql =>
        {
            if (sql.Contains("FOR ATTACH", StringComparison.Ordinal))
            {
                throw new IOException("Attach failed " + Password, new InvalidOperationException(Password));
            }
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Create());
        Assert.DoesNotContain(Password, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_log, message => message.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unfinished_pool_upgrade_refuses_create_without_creating_a_fixture()
    {
        _engine.CredentialsJson = Format2Pool.Credentials(SqlBranch.Core.Engine.UpgradeRecord.Running);
        var error = await Assert.ThrowsAsync<SqlBranchUnavailableException>(() => Create());
        Assert.Equal("sqlbranch pool upgrade --yes", error.FixCommand);
        Assert.Contains("did not finish", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_connector.Statements, s => s.Contains("CREATE DATABASE", StringComparison.Ordinal));
        Assert.DoesNotContain(_runtime.Calls, s => s.StartsWith("copy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Availability_during_an_unfinished_upgrade_logs_the_fix_and_recovers_after_completion()
    {
        _engine.CredentialsJson = Format2Pool.Credentials(SqlBranch.Core.Engine.UpgradeRecord.Running);
        Assert.False(await Host().IsAvailableAsync(default));
        Assert.Contains(_log, s => s.Contains("sqlbranch pool upgrade --yes", StringComparison.Ordinal));
        _engine.CredentialsJson = Format2Pool.Credentials();
        Assert.True(await Host().IsAvailableAsync(default));
    }

    [Fact]
    public async Task Reset_and_dispose_during_an_unfinished_upgrade_leave_the_branch()
    {
        const string mode = "thin";
        _engine.EngineProbe = mode;
        var db = await Create();
        var record = new BranchRecord("shop", mode, DateTimeOffset.UnixEpoch, new Dictionary<string, string>());
        _connector.Databases[db.Name] = [$"/pool/branches/{db.Name}/data/shop.mdf"];
        _engine.BranchFolders.Add((db.Name, true, false));
        _engine.BranchRecords[db.Name] = Encode(record.ToJson());
        _engine.CredentialsJson = Format2Pool.Credentials(SqlBranch.Core.Engine.UpgradeRecord.Running);
        _connector.Statements.Clear();
        var scripts = new List<string>();
        var previous = _runtime.ExecHandler;
        _runtime.ExecHandler = (name, args) => { scripts.Add(args[^1]); return previous(name, args); };

        var error = await Assert.ThrowsAsync<SqlBranchUnavailableException>(() => db.ResetAsync());
        Assert.Contains("did not finish", error.Message, StringComparison.Ordinal);
        Assert.Equal("sqlbranch pool upgrade --yes", error.FixCommand);
        await db.DisposeAsync();

        Assert.Contains(db.Name, _connector.Databases.Keys);
        Assert.Equal(Encode(record.ToJson()), _engine.BranchRecords[db.Name]);
        Assert.DoesNotContain(_connector.Statements, s => s.Contains("DROP DATABASE", StringComparison.Ordinal) || s.Contains("SINGLE_USER", StringComparison.Ordinal));
        Assert.DoesNotContain(scripts, s => s.Contains("rm -", StringComparison.Ordinal) || s.Contains("base64 -d", StringComparison.Ordinal));
        Assert.Contains(_log, s => s.Contains(db.Name, StringComparison.Ordinal) && s.Contains("sqlbranch pool upgrade --yes", StringComparison.Ordinal) && s.Contains("after it expires", StringComparison.Ordinal));
        Assert.DoesNotContain(Password, error.Message + string.Join(' ', _log), StringComparison.Ordinal);
    }

    private static class Format2Pool
    {
        public static string Credentials(string? state = SqlBranch.Core.Engine.UpgradeRecord.Done) =>
            SqlBranch.Core.Engine.CredentialsFile.Format2(Password, state is null ? null
                : new SqlBranch.Core.Engine.UpgradeRecord(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero), state)).ToJson();
    }

    public void Dispose()
    {
        SqlBranchEnvironment.Log = _oldLog;
        _process.Dispose();
        _home.Delete(recursive: true);
    }
}
