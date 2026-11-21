using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

public sealed record DatabaseTrigger(string Name, bool Enabled, bool HandlesOwnershipChange, int ObjectId, DateTime Modified);

public static class DatabaseTriggerCatalog
{
    public static async Task<IReadOnlyList<DatabaseTrigger>> ReadAsync(IEngineSession session, string database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var db = Quoting.Identifier(database);
        var rows = await session.QueryAsync($"""
            SELECT t.name, t.is_disabled, CONVERT(bit, CASE WHEN EXISTS (
                SELECT 1 FROM {db}.sys.trigger_events AS e
                WHERE e.object_id = t.object_id AND e.type_desc = N'ALTER_AUTHORIZATION_DATABASE'
            ) THEN 1 ELSE 0 END), t.object_id, t.modify_date
            FROM {db}.sys.triggers AS t
            WHERE t.parent_class = 0
            ORDER BY t.name
            """, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new DatabaseTrigger((string)r[0]!, !(bool)r[1]!, (bool)r[2]!, (int)r[3]!, (DateTime)r[4]!)).ToArray();
    }

    public static void RequirePostScriptsAllowed(IReadOnlyList<DatabaseTrigger> triggers,
        string guidance = "Disable them in the source database and take a new backup, or omit the post-build scripts.")
    {
        ArgumentNullException.ThrowIfNull(triggers);
        var blocked = triggers.Where(t => t.Enabled && t.HandlesOwnershipChange).Select(t => t.Name).ToArray();
        if (blocked.Length != 0)
        {
            throw new SqlBranchException(ErrorCodes.DdlTrigger,
                $"Cannot run post-build scripts. Enabled database triggers handle ownership changes: {string.Join(", ", blocked)}. {guidance}");
        }
    }
}
