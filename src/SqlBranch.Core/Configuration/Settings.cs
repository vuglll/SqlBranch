using System.Globalization;

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

    /// <summary>How long a command waits for running image and branch operations before it exits 1 with <c>catalog_busy</c>.</summary>
    public TimeSpan PublishWait { get; init; } = SettingKeys.DefaultPublishWait;

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
    public const string PublishWait = "publishWait";

    /// <summary>The config file format version key. Not a setting.</summary>
    public const string Version = "version";

    public const int SupportedFileVersion = 1;
    public const int DefaultPort = 14330;
    public const string DefaultEngineImage = "mcr.microsoft.com/mssql/server:2022-latest";
    public static readonly TimeSpan DefaultPublishWait = TimeSpan.FromMinutes(10);
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
        new(PublishWait, "SQLBRANCH_PUBLISH_WAIT", "10m", "How long a command waits for running image and branch operations: `1m` to `1440m` or `1h` to `24h`."),
    ];

    public static SettingDefinition? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
}

/// <summary>Parses the <c>publishWait</c> value: a whole number of minutes (1 to 1440) or hours (1 to 24).</summary>
public static class PublishWaitValue
{
    public static bool TryParse(string? value, out TimeSpan wait)
    {
        wait = default;
        if (value is null)
        {
            return false;
        }

        var text = value.Trim();
        if (text.Length < 2 || text[^1] is not ('m' or 'h') || !text[..^1].All(char.IsAsciiDigit)
            || !int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var hours = text[^1] == 'h';
        if (number < 1 || number > (hours ? 24 : 1440))
        {
            return false;
        }

        wait = hours ? TimeSpan.FromHours(number) : TimeSpan.FromMinutes(number);
        return true;
    }
}
