using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SqlBranch.Core.Configuration;

/// <summary>Where settings can come from for one command.</summary>
public sealed record SettingsSources
{
    /// <summary>The environment and discovery paths used by CLI and library callers.</summary>
    public static SettingsSources FromProcess()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith("SQLBRANCH_", StringComparison.Ordinal) && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        return new SettingsSources
        {
            Environment = environment,
            WorkingDirectory = System.Environment.CurrentDirectory,
            HomeDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? home : null,
        };
    }

    /// <summary>Values from command-line flags, by setting key.</summary>
    public IReadOnlyDictionary<string, string> Flags { get; init; } = new Dictionary<string, string>();

    /// <summary>The environment, by variable name.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>The directory the command runs in; the repository file is searched from here upward.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The <c>--config</c> file, which replaces the repository file.</summary>
    public string? ConfigFile { get; init; }

    /// <summary>The user's home directory, for <c>~/.sqlbranch/config.yml</c>.</summary>
    public string? HomeDirectory { get; init; }
}

/// <summary>
/// Resolves settings. Order, first wins: flag, <c>SQLBRANCH_</c> variable,
/// repository file, user file, default.
/// </summary>
public static class SettingsLoader
{
    public const string RepositoryFileName = ".sqlbranch.yml";

    public static Settings Load(SettingsSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var layers = new List<(string Origin, IReadOnlyDictionary<string, string> Values)>();

        foreach (var (key, value) in sources.Flags)
        {
            if (SettingKeys.Find(key) is null)
            {
                throw new ArgumentException($"Unknown setting key '{key}' passed as a flag.", nameof(sources));
            }

            layers.Add(($"--{ToFlagName(key)}", new Dictionary<string, string> { [key] = value }));
        }

        var fromEnvironment = new Dictionary<string, string>();
        foreach (var definition in SettingKeys.All)
        {
            if (sources.Environment.TryGetValue(definition.EnvironmentVariable, out var value) && !string.IsNullOrEmpty(value))
            {
                layers.Add((definition.EnvironmentVariable, new Dictionary<string, string> { [definition.Key] = value }));
            }
        }

        var repositoryFile = sources.ConfigFile is not null
            ? ResolveExplicitFile(sources.ConfigFile, sources.WorkingDirectory)
            : FindRepositoryFile(sources.WorkingDirectory);
        if (repositoryFile is not null)
        {
            layers.Add((repositoryFile, ReadFile(repositoryFile)));
        }

        if (sources.HomeDirectory is not null)
        {
            var userFile = Path.Combine(sources.HomeDirectory, ".sqlbranch", "config.yml");
            if (File.Exists(userFile))
            {
                layers.Add((userFile, ReadFile(userFile)));
            }
        }

        return Resolve(layers);
    }

    /// <summary>The nearest <c>.sqlbranch.yml</c> from the directory upward, or null.</summary>
    public static string? FindRepositoryFile(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, RepositoryFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ResolveExplicitFile(string path, string workingDirectory)
    {
        var full = Path.GetFullPath(path, workingDirectory);
        if (!File.Exists(full))
        {
            throw new ConfigException("the file given with --config does not exist.", full, null);
        }

        return full;
    }

    /// <summary>Reads one config file into raw values, checking keys, types and version.</summary>
    public static IReadOnlyDictionary<string, string> ReadFile(string path)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StreamReader(path);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"not valid YAML at line {ex.Start.Line}.", path, null);
        }
        catch (IOException ex)
        {
            throw new ConfigException($"cannot be read: {ex.Message}", path, null);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
        {
            return values;
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
        {
            throw new ConfigException("the file must be a mapping of setting names to values.", path, null);
        }

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
            {
                throw new ConfigException("setting names must be plain text, not a list, mapping or empty.", path, null);
            }

            if (key.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigException(
                    $"passwords are not read from config files. Set {SettingKeys.SaPasswordVariable} before the first init instead.",
                    path, key);
            }

            if (key != SettingKeys.Version && SettingKeys.Find(key) is null)
            {
                throw new ConfigException(
                    $"unknown key. Known keys: {string.Join(", ", SettingKeys.All.Select(d => d.Key))}, version.", path, key);
            }

            if (valueNode is not YamlScalarNode scalar)
            {
                throw new ConfigException("expected a single value, not a list or mapping.", path, key);
            }

            values[key] = scalar.Value ?? string.Empty;
        }

        if (values.Remove(SettingKeys.Version, out var version) &&
            version.Trim() != SettingKeys.SupportedFileVersion.ToString(CultureInfo.InvariantCulture))
        {
            throw new ConfigException(
                $"unsupported version '{version}'. This SqlBranch reads version {SettingKeys.SupportedFileVersion}.",
                path, SettingKeys.Version);
        }

        return values;
    }

    private static Settings Resolve(List<(string Origin, IReadOnlyDictionary<string, string> Values)> layers)
    {
        // Every layer is checked, not only the winning value, so a bad value in a
        // lower layer is reported instead of waiting to surface later.
        foreach (var (layerOrigin, layerValues) in layers)
        {
            foreach (var (key, value) in layerValues)
            {
                CheckValue(key, value, layerOrigin);
            }
        }

        var origins = new Dictionary<string, string>(StringComparer.Ordinal);

        string? Raw(string key)
        {
            foreach (var (origin, values) in layers)
            {
                if (values.TryGetValue(key, out var value))
                {
                    origins[key] = origin;
                    return value;
                }
            }

            return null;
        }

        var settings = new Settings();

        if (Raw(SettingKeys.Pool) is { } pool)
        {
            settings = settings with { Pool = NonEmpty(pool, SettingKeys.Pool) };
        }

        if (Raw(SettingKeys.PoolBackend) is { } backend)
        {
            if (!SettingNames.TryParseBackend(backend, out var parsed))
            {
                throw Invalid(SettingKeys.PoolBackend, backend, $"allowed values are {string.Join(", ", SettingNames.Backends)}.");
            }

            settings = settings with { PoolBackend = parsed };
        }

        if (Raw(SettingKeys.PoolSize) is { } size)
        {
            if (!ByteSize.TryParse(size, out var parsed))
            {
                throw Invalid(SettingKeys.PoolSize, size, "expected a size such as 60GB or 100GiB.");
            }

            settings = settings with { PoolSize = parsed };
        }

        if (Raw(SettingKeys.EngineImage) is { } image)
        {
            settings = settings with { EngineImage = NonEmpty(image, SettingKeys.EngineImage) };
        }

        if (Raw(SettingKeys.EngineEdition) is { } edition)
        {
            if (!SettingNames.TryParseEdition(edition, out var parsed))
            {
                throw Invalid(SettingKeys.EngineEdition, edition,
                    $"only development editions are supported: {string.Join(", ", SettingNames.Editions)}.");
            }

            settings = settings with { EngineEdition = parsed };
        }

        if (Raw(SettingKeys.Port) is { } port)
        {
            if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 65535)
            {
                throw Invalid(SettingKeys.Port, port, "expected a port number from 1 to 65535.");
            }

            settings = settings with { Port = parsed };
        }

        if (Raw(SettingKeys.RequireThin) is { } requireThin)
        {
            if (!bool.TryParse(requireThin.Trim(), out var parsed))
            {
                throw Invalid(SettingKeys.RequireThin, requireThin, "expected true or false.");
            }

            settings = settings with { RequireThin = parsed };
        }

        if (Raw(SettingKeys.DefaultImage) is { } defaultImage)
        {
            settings = settings with { DefaultImage = NonEmpty(defaultImage, SettingKeys.DefaultImage) };
        }

        EditionRules.Check(settings.EngineEdition, settings.EngineImage, origins.GetValueOrDefault(SettingKeys.EngineEdition));

        return settings with { Origins = origins };

        string NonEmpty(string value, string key) =>
            string.IsNullOrWhiteSpace(value) ? throw Invalid(key, value, "must not be empty.") : value.Trim();

        ConfigException Invalid(string key, string value, string why) =>
            new($"'{value}' is not valid; {why}", origins.GetValueOrDefault(key), key);
    }

    private static void CheckValue(string key, string value, string origin)
    {
        ConfigException Bad(string why) => new($"'{value}' is not valid; {why}", origin, key);

        switch (key)
        {
            case SettingKeys.Pool or SettingKeys.EngineImage or SettingKeys.DefaultImage when string.IsNullOrWhiteSpace(value):
                throw Bad("must not be empty.");
            case SettingKeys.Pool when !IsAbsolutePoolPath(value):
                throw Bad("expected an absolute path on the container host, such as /data/sqlbranch, without a colon (a bind mount cannot take one). " +
                    "A bare name would be taken for a Docker volume.");
            case SettingKeys.PoolBackend when !SettingNames.TryParseBackend(value, out _):
                throw Bad($"allowed values are {string.Join(", ", SettingNames.Backends)}.");
            case SettingKeys.PoolSize when !ByteSize.TryParse(value, out _):
                throw Bad("expected a size such as 60GB or 100GiB.");
            case SettingKeys.EngineEdition when !SettingNames.TryParseEdition(value, out _):
                throw Bad($"only development editions are supported: {string.Join(", ", SettingNames.Editions)}.");
            case SettingKeys.Port when !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535:
                throw Bad("expected a port number from 1 to 65535.");
            case SettingKeys.RequireThin when !bool.TryParse(value.Trim(), out _):
                throw Bad("expected true or false.");
        }
    }

    private static bool IsAbsolutePoolPath(string value) =>
        value.Trim() is ['/', ..] path && !path.Contains(':', StringComparison.Ordinal);

    private static string ToFlagName(string key) =>
        string.Concat(key.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : c.ToString()));
}
