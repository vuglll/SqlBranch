using System.Text.RegularExpressions;
using SqlBranch.Core.Engine;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>An engine with one login, <c>sa</c>, whose password <c>ALTER LOGIN</c> changes.</summary>
internal sealed partial class FakeEngineConnector : IEngineConnector
{
    public string? SaPassword { get; set; }

    /// <summary>Runs on every connection attempt, before it is judged.</summary>
    public Action? OnConnect { get; set; }

    public bool NeverReady { get; set; }

    public bool FailAlterLogin { get; set; }

    public bool FailVersion { get; set; }

    public string Version { get; set; } = "16.0.4325.4|Developer Edition (64-bit)";

    /// <summary>User databases and their files, as <c>sys.master_files</c> would list them.</summary>
    public Dictionary<string, List<string>> Databases { get; } = [];

    /// <summary>Databases whose DROP fails.</summary>
    public HashSet<string> FailDrop { get; } = [];

    /// <summary>The server credentials, as <c>sys.credentials</c> lists them. <c>CREATE CREDENTIAL</c> and <c>DROP CREDENTIAL</c> change it.</summary>
    public List<FakeCredential> Credentials { get; } = [];

    /// <summary>Names of credentials whose <c>DROP CREDENTIAL</c> fails.</summary>
    public HashSet<string> FailDropCredential { get; } = [];

    /// <summary>When set, <c>CREATE CREDENTIAL</c> throws it.</summary>
    public Exception? FailCreateCredential { get; set; }

    /// <summary>Application locks a session gave back, in order.</summary>
    public List<string> ReleasedLocks { get; } = [];

    /// <summary>How many credential lookups that follow a <c>CREATE CREDENTIAL</c> fail, as a dropped connection would.</summary>
    public int ReadBackFaults { get; set; }

    private bool _created;
    private int _nextCredentialId = 65536;

    /// <summary>Adds a credential made by hand, as a user would.</summary>
    public FakeCredential AddCredential(string name, string secret = "hand-made")
    {
        var credential = new FakeCredential(name, _nextCredentialId++, "2026-11-19T10:00:00.000", secret);
        Credentials.Add(credential);
        return credential;
    }

    /// <summary>How many sessions were opened, and how many of them were disposed: a disposed session gives back its application locks.</summary>
    public int SessionsOpened { get; private set; }

    public int SessionsDisposed { get; private set; }

    /// <summary>Application locks another session holds.</summary>
    public HashSet<string> HeldLocks { get; } = [];

    /// <summary>Model exclusive locks held by overlapping sessions until release or disposal.</summary>
    public bool TrackSessionLocks { get; set; }

    public Action<string>? OnTakeLock { get; set; }

    /// <summary>The parameters of every parameterized query, in order.</summary>
    public List<Dictionary<string, object?>> Parameters { get; } = [];

    /// <summary>Every statement run, in order.</summary>
    public List<string> Statements { get; } = [];

    /// <summary>Application locks a session took and kept, in order.</summary>
    public List<string> TakenLocks { get; } = [];

    /// <summary>Locks looked at with <c>APPLOCK_TEST</c>, which never takes them.</summary>
    public List<string> ProbedLocks { get; } = [];

    /// <summary>Answers a query with parameters that returns named rows (the RESTORE inspections and file lists).</summary>
    public Func<string, IReadOnlyDictionary<string, object?>, IReadOnlyList<IReadOnlyDictionary<string, object?>>>? RowsHandler { get; set; }

    /// <summary>Runs on every statement with parameters (the RESTORE itself); it may throw to fail the statement.</summary>
    public Action<string, IReadOnlyDictionary<string, object?>>? ParameterizedExecuteHandler { get; set; }

    /// <summary>Runs on every statement without parameters, before the fake's own handling; it may throw.</summary>
    public Action<string>? ExecuteHandler { get; set; }

    /// <summary>Answers a scalar query without parameters; null means the fake's default.</summary>
    public Func<string, object?>? ScalarHandler { get; set; }

    /// <summary>Answers a scalar query with parameters that is not a lock; null means the fake's default.</summary>
    public Func<string, IReadOnlyDictionary<string, object?>, object?>? ParameterizedScalarHandler { get; set; }

    /// <summary>The password of every connection attempt, in order.</summary>
    public List<string> Attempts { get; } = [];

    [GeneratedRegex(@"ALTER LOGIN sa WITH PASSWORD = N'(?<p>.*)'$", RegexOptions.Singleline)]
    private static partial Regex Alter();

    [GeneratedRegex(@"^CREATE CREDENTIAL \[(?<n>(?:[^\]]|\]\])*)\] WITH IDENTITY = '(?<i>[^']*)', SECRET = N'(?<s>.*)'$", RegexOptions.Singleline)]
    private static partial Regex CreateCredential();

    [GeneratedRegex(@"^DROP CREDENTIAL \[(?<n>(?:[^\]]|\]\])*)\]$")]
    private static partial Regex DropCredential();

    [GeneratedRegex(@"DROP DATABASE \[(?<n>(?:[^\]]|\]\])*)\];$")]
    private static partial Regex Drop();

    public Func<string, IReadOnlyList<object?[]>>? QueryHandler { get; set; }

    public Task<IEngineSession> ConnectLoginAsync(EngineEndpoint endpoint, string login, string password, string database, CancellationToken cancellationToken)
    {
        Assert.Equal(database, login);
        Assert.StartsWith("sqlbranch_build_", login, StringComparison.Ordinal);
        Assert.NotEqual(SaPassword, password);
        return Task.FromResult<IEngineSession>(new Session(this));
    }

    public Task<IEngineSession> ConnectAsync(EngineEndpoint endpoint, string password, CancellationToken cancellationToken)
    {
        Attempts.Add(password);
        OnConnect?.Invoke();
        if (NeverReady || SaPassword is null || !string.Equals(password, SaPassword, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Login failed for user 'sa'.");
        }

        SessionsOpened++;
        return Task.FromResult<IEngineSession>(new Session(this));
    }

    private sealed class Session(FakeEngineConnector owner) : IEngineSession
    {
        private readonly List<string> _locks = [];

        public Task<object?> ScalarAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            owner.Statements.Add(sql);
            owner.Parameters.Add(new Dictionary<string, object?>(parameters));
            if (sql.Contains("APPLOCK_TEST", StringComparison.Ordinal))
            {
                // Looks at the lock and never takes it: 1 when it could be granted, 0 when another session holds it.
                var probed = Convert.ToString(parameters["resource"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                owner.ProbedLocks.Add(probed);
                return Task.FromResult<object?>(owner.HeldLocks.Contains(probed) ? 0 : 1);
            }

            if (sql.Contains("sp_releaseapplock", StringComparison.Ordinal) && !sql.Contains("sp_getapplock", StringComparison.Ordinal))
            {
                var resource = Convert.ToString(parameters["resource"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                owner.ReleasedLocks.Add(resource);
                if (_locks.Remove(resource))
                {
                    owner.HeldLocks.Remove(resource);
                }
                return Task.FromResult<object?>(0);
            }

            if (sql.Contains("sp_getapplock", StringComparison.Ordinal))
            {
                var resource = Convert.ToString(parameters["resource"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (owner.HeldLocks.Contains(resource))
                {
                    return Task.FromResult<object?>(-1);
                }

                if (!sql.Contains("sp_releaseapplock", StringComparison.Ordinal))
                {
                    owner.TakenLocks.Add(resource);
                    if (owner.TrackSessionLocks)
                    {
                        owner.HeldLocks.Add(resource);
                        _locks.Add(resource);
                    }
                    owner.OnTakeLock?.Invoke(resource);
                }

                return Task.FromResult<object?>(0);
            }

            return Task.FromResult(owner.ParameterizedScalarHandler is { } handler ? handler(sql, parameters) : 1);
        }

        public Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
        {
            owner.Statements.Add(sql);
            if (owner.ScalarHandler is { } scalar && scalar(sql) is { } answer)
            {
                return Task.FromResult<object?>(answer);
            }

            if (sql.Contains("SERVERPROPERTY", StringComparison.Ordinal))
            {
                if (owner.FailVersion)
                {
                    throw new InvalidOperationException("The version query failed.");
                }

                return Task.FromResult<object?>(owner.Version);
            }

            return Task.FromResult<object?>(1);
        }

        public Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken)
        {
            owner.Statements.Add(sql);
            if (owner.QueryHandler is not null)
            {
                return Task.FromResult(owner.QueryHandler(sql));
            }
            if (sql == SqlBranch.Core.Engine.TeardownRunner.UserDatabasesSql)
            {
                return Task.FromResult<IReadOnlyList<object?[]>>([.. owner.Databases.Keys.Order(StringComparer.Ordinal).Select(n => new object?[] { n })]);
            }

            IReadOnlyList<object?[]> rows = sql.Contains("sys.master_files", StringComparison.Ordinal)
                ? [.. owner.Databases.SelectMany(d => d.Value.Select(f => new object?[] { d.Key, f }))]
                : [];
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(
            string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            owner.Statements.Add(sql);
            owner.Parameters.Add(new Dictionary<string, object?>(parameters));
            if (sql.Contains("FROM sys.credentials", StringComparison.Ordinal))
            {
                var name = (string)parameters["name"]!;
                if (owner._created && owner.ReadBackFaults > 0)
                {
                    owner.ReadBackFaults--;
                    throw new InvalidOperationException("The connection was lost.");
                }

                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([.. owner.Credentials.Where(c => c.Name == name).Select(c =>
                    (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["credential_id"] = c.Id, ["create_date"] = c.CreateDate })]);
            }

            return Task.FromResult(owner.RowsHandler is { } handler ? handler(sql, parameters) : []);
        }

        public Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            owner.Statements.Add(sql);
            owner.Parameters.Add(new Dictionary<string, object?>(parameters));
            owner.ParameterizedExecuteHandler?.Invoke(sql, parameters);
            return Task.CompletedTask;
        }

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            owner.Statements.Add(sql);
            owner.ExecuteHandler?.Invoke(sql);
            if (owner.FailAlterLogin)
            {
                throw new InvalidOperationException("ALTER LOGIN failed.");
            }

            var create = CreateCredential().Match(sql);
            if (create.Success)
            {
                if (owner.FailCreateCredential is { } failure)
                {
                    throw failure;
                }

                owner._created = true;
                var created = owner.AddCredential(create.Groups["n"].Value.Replace("]]", "]", StringComparison.Ordinal), create.Groups["s"].Value.Replace("''", "'", StringComparison.Ordinal));
                owner.Credentials[^1] = created with { CreateDate = "2026-11-19T12:00:00.000" };
                return Task.CompletedTask;
            }

            var dropCredential = DropCredential().Match(sql);
            if (dropCredential.Success)
            {
                var credential = dropCredential.Groups["n"].Value.Replace("]]", "]", StringComparison.Ordinal);
                if (owner.FailDropCredential.Contains(credential))
                {
                    throw new InvalidOperationException($"Cannot drop the credential '{credential}'.");
                }

                owner.Credentials.RemoveAll(c => c.Name == credential);
                return Task.CompletedTask;
            }

            var drop = Drop().Match(sql);
            if (drop.Success)
            {
                var name = drop.Groups["n"].Value.Replace("]]", "]", StringComparison.Ordinal);
                if (owner.FailDrop.Contains(name))
                {
                    throw new InvalidOperationException($"Cannot drop the database '{name}'.");
                }

                owner.Databases.Remove(name);
                return Task.CompletedTask;
            }

            var match = Alter().Match(sql);
            if (match.Success)
            {
                owner.SaPassword = match.Groups["p"].Value.Replace("''", "'", StringComparison.Ordinal);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.SessionsDisposed++;
            foreach (var resource in _locks)
            {
                owner.HeldLocks.Remove(resource);
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>One row of <c>sys.credentials</c> in the fake engine.</summary>
internal sealed record FakeCredential(string Name, int Id, string CreateDate, string Secret);
