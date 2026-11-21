using SqlBranch.Core.Output;

namespace SqlBranch.Core.Configuration;

/// <summary>A setting is invalid. Commands turn this into exit code 2.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException()
    {
    }

    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ConfigException(string message, string? origin, string? key)
        : base(Compose(message, origin, key))
    {
        Origin = origin;
        Key = key;
    }

    /// <summary>Where the bad value came from: a file path, an environment variable or a flag.</summary>
    public string? Origin { get; }

    /// <summary>The offending key, when there is one.</summary>
    public string? Key { get; }

    /// <summary>The error code commands report for this exception.</summary>
    public static ErrorCode ErrorCode => ErrorCodes.ConfigError;

    private static string Compose(string message, string? origin, string? key) =>
        (origin, key) switch
        {
            (not null, not null) => $"{origin}: {key}: {message}",
            (not null, null) => $"{origin}: {message}",
            (null, not null) => $"{key}: {message}",
            _ => message,
        };
}
