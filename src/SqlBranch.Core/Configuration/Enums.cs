namespace SqlBranch.Core.Configuration;

/// <summary>How the pool is provided.</summary>
public enum PoolBackend
{
    /// <summary>Use the directory when it can share blocks, otherwise a managed XFS file, otherwise copy mode.</summary>
    Auto,

    /// <summary>Always a managed XFS file.</summary>
    LoopXfs,

    /// <summary>Always the directory as it is.</summary>
    Plain,
}

/// <summary>The SQL Server development edition the engine runs.</summary>
public enum EngineEdition
{
    /// <summary>Developer: Enterprise features, every version.</summary>
    Developer,

    /// <summary>Standard Developer: Standard limits, SQL Server 2025 and later.</summary>
    StandardDeveloper,

    /// <summary>Enterprise Developer, SQL Server 2025 and later.</summary>
    EnterpriseDeveloper,
}

/// <summary>Names used in settings for the enums, and back.</summary>
public static class SettingNames
{
    public static string Of(PoolBackend backend) => backend switch
    {
        PoolBackend.Auto => "auto",
        PoolBackend.LoopXfs => "loop-xfs",
        PoolBackend.Plain => "plain",
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    public static string Of(EngineEdition edition) => edition switch
    {
        EngineEdition.Developer => "developer",
        EngineEdition.StandardDeveloper => "standard-developer",
        EngineEdition.EnterpriseDeveloper => "enterprise-developer",
        _ => throw new ArgumentOutOfRangeException(nameof(edition)),
    };

    public static IReadOnlyList<string> Backends { get; } =
        [.. Enum.GetValues<PoolBackend>().Select(Of)];

    public static IReadOnlyList<string> Editions { get; } =
        [.. Enum.GetValues<EngineEdition>().Select(Of)];

    public static bool TryParseBackend(string value, out PoolBackend backend) =>
        TryParse(value, Enum.GetValues<PoolBackend>(), Of, out backend);

    public static bool TryParseEdition(string value, out EngineEdition edition) =>
        TryParse(value, Enum.GetValues<EngineEdition>(), Of, out edition);

    private static bool TryParse<T>(string value, T[] all, Func<T, string> name, out T result)
        where T : struct
    {
        foreach (var candidate in all)
        {
            if (string.Equals(name(candidate), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }
}
