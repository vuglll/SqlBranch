using SqlBranch.Core.Engine;
using SqlBranch.Core.LogicalSources;

namespace SqlBranch.Core.Images;

internal static class BuildLogins
{
    public static async Task<IReadOnlyList<string>> ListIdsAsync(IEngineSession session, CancellationToken token, bool includeLogical = false)
    {
        const string prefix = StatusReporter.BuildPrefix;
        var rows = await session.QueryAsync("SELECT name FROM sys.sql_logins WHERE name LIKE 'sqlbranch[_]build[_]%' ORDER BY name", token).ConfigureAwait(false);
        return rows.Select(r => (string)r[0]!).Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..]).Where(id => (id.Length > 0 && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')) ||
                (includeLogical && BacpacOperation.IsLogicalId(id))).ToArray();
    }
}
