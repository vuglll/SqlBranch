using Microsoft.Data.SqlClient;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Branches;

/// <summary>A branch's connection string. It holds the engine password: only <c>branch conn</c> may print it.</summary>
/// <param name="Name">The branch.</param>
/// <param name="Server">The data source, for example <c>127.0.0.1,14330</c>.</param>
/// <param name="Database">The database name, which is the branch name.</param>
/// <param name="User">The login.</param>
/// <param name="ConnectionString">A connection string for Microsoft.Data.SqlClient that works from the machine running the CLI.</param>
/// <param name="Warnings">Set when the database is missing or not ONLINE.</param>
public sealed record BranchConnection(string Name, string Server, string Database, string User, string ConnectionString, IReadOnlyList<string>? Warnings = null);

/// <summary>
/// <c>branch conn</c>. The password is read from the pool's credentials file with
/// <see cref="EngineCredentials.ReadAsync"/>, which carries it as base64 so the runtime's masking of container output cannot
/// replace it, and is never taken from plain container output. The string points at 127.0.0.1 and the engine's published port.
/// </summary>
public sealed class BranchConnector(IContainerRuntime runtime, IEngineConnector connector, SecretRedactor redactor, string engineName = ContainerLabels.EngineName)
{
    public async Task<BranchConnection> ConnectionAsync(FoundPool pool, int fallbackPort, string name, CancellationToken cancellationToken)
    {
        // Before any SQL runs and before any file is touched.
        NameRules.ValidateBranch(name);

        await using var engine = await BranchEngine.OpenAsync(runtime, connector, redactor, engineName, pool, fallbackPort, cancellationToken).ConfigureAwait(false);
        var (exists, _) = await engine.ReadRecordAsync(name, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            throw BranchEngine.NotABranch(name);
        }

        // The string is still printed: a branch that is not online is something to know, not a reason to withhold it.
        var warnings = new List<string>();
        var state = await engine.DatabaseStateAsync(name, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            warnings.Add($"The engine has no database named '{name}', so this connection string will not work until the branch is repaired. 'sqlbranch branch reset {name}' recreates it from its image.");
        }
        else if (!BranchEngine.IsOnline(state))
        {
            warnings.Add($"The database of branch '{name}' is {state.ToLowerInvariant()}, not online, so this connection string will not work until it is brought back.");
        }

        return new BranchConnection(name, engine.Server, name, BranchCreator.LoginName, BuildConnectionString(engine.Server, name, engine.Password), warnings);
    }

    internal static string BuildConnectionString(string server, string name, string password)
    {
        return new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = name,
            UserID = BranchCreator.LoginName,
            Password = password,
            TrustServerCertificate = true,
        }.ConnectionString;
    }
}
