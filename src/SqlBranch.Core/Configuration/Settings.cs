namespace SqlBranch.Core.Configuration;

/// <summary>The resolved settings for one command.</summary>
public sealed record Settings
{
    /// <summary>The pool directory, or null for the backend's default location.</summary>
    public string? Pool { get; init; }

    public PoolBackend PoolBackend { get; init; } = PoolBackend.Auto;

    public ByteSize PoolSize { get; init; } = SettingKeys.DefaultPoolSize;

    public string EngineImage { get; init; } = SettingKeys.DefaultEngineImage;

    public EngineEdition EngineEdition { get; init; } = EngineEdition.Developer;

    public int Port { get; init; } = SettingKeys.DefaultPort;

    public bool RequireThin { get; init; }

    /// <summary>The image <c>branch create</c> uses without <c>--from</c>, or null.</summary>
    public string? DefaultImage { get; init; }

    /// <summary>For each setting that was given, where it came from: a flag, a variable or a file path.</summary>
    public IReadOnlyDictionary<string, string> Origins { get; init; } = new Dictionary<string, string>();
}

/// <summary>One documented setting.</summary>
public sealed record SettingDefinition(string Key, string EnvironmentVariable, string Default, string Description);

/// <summary>The settings SqlBranch knows, with their environment variables and defaults.</summary>
public static class SettingKeys
{
    public const string Pool = "pool";
    public const string PoolBackend = "poolBackend";
    public const string PoolSize = "poolSize";
    public const string EngineImage = "engineImage";
    public const string EngineEdition = "engineEdition";
    public const string Port = "port";
    public const string RequireThin = "requireThin";
    public const string DefaultImage = "defaultImage";

    /// <summary>The config file format version key. Not a setting.</summary>
    public const string Version = "version";

    public const int SupportedFileVersion = 1;
    public const int DefaultPort = 14330;
    public const string DefaultEngineImage = "mcr.microsoft.com/mssql/server:2022-latest";
    public static readonly ByteSize DefaultPoolSize = new(60_000_000_000);

    /// <summary>The engine password variable. It is a secret, not a setting.</summary>
    public const string SaPasswordVariable = "SQLBRANCH_SA_PASSWORD";

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(Pool, "SQLBRANCH_POOL", "(see below)", "Directory for the pool when it is used as it is."),
        new(PoolBackend, "SQLBRANCH_POOL_BACKEND", "auto", "`auto`, `loop-xfs` or `plain`."),
        new(PoolSize, "SQLBRANCH_POOL_SIZE", "60GB", "Size of a managed pool, set when it is created."),
        new(EngineImage, "SQLBRANCH_ENGINE_IMAGE", DefaultEngineImage, "SQL Server container image for the engine."),
        new(EngineEdition, "SQLBRANCH_ENGINE_EDITION", "developer", "`developer`, `standard-developer` or `enterprise-developer`."),
        new(Port, "SQLBRANCH_PORT", "14330", "Engine port on 127.0.0.1."),
        new(RequireThin, "SQLBRANCH_REQUIRE_THIN", "false", "Refuse to create thin-mode branches as full copies."),
        new(DefaultImage, "SQLBRANCH_DEFAULT_IMAGE", "(none)", "Image `branch create` uses without `--from`."),
    ];

    public static SettingDefinition? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
}
