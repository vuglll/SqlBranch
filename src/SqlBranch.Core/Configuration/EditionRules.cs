using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlBranch.Core.Configuration;

/// <summary>Which editions need which SQL Server version.</summary>
public static partial class EditionRules
{
    /// <summary>The first major release with Standard Developer and Enterprise Developer.</summary>
    public const int MinimumYearForNewEditions = 2025;

    [GeneratedRegex(@":(?<year>(19|20)\d{2})-")]
    private static partial Regex YearTag();

    /// <summary>The SQL Server release year named by the image tag, or null when the tag does not say.</summary>
    public static int? ReleaseYearFromImage(string image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var match = YearTag().Match(image);
        return match.Success ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Rejects a newer edition on an image whose tag names an older release.
    /// A tag without a year is checked against the running engine later.
    /// </summary>
    public static void Check(EngineEdition edition, string image, string? origin = null)
    {
        if (edition == EngineEdition.Developer)
        {
            return;
        }

        if (ReleaseYearFromImage(image) is { } year && year < MinimumYearForNewEditions)
        {
            throw new ConfigException(
                $"edition {SettingNames.Of(edition)} needs SQL Server {MinimumYearForNewEditions} or later, but the engine image is {image}.",
                origin, SettingKeys.EngineEdition);
        }
    }

    /// <summary>The first major version number of SQL Server 2025 (<c>ProductMajorVersion</c>).</summary>
    public const int MinimumMajorVersionForNewEditions = 17;

    /// <summary>The <c>MSSQL_PID</c> value of an edition (confirmed against Microsoft's edition table on 2026-11-16).</summary>
    public static string ProductId(EngineEdition edition) => edition switch
    {
        EngineEdition.Developer => "Developer",
        EngineEdition.StandardDeveloper => "StandardDeveloper",
        EngineEdition.EnterpriseDeveloper => "EnterpriseDeveloper",
        _ => throw new ArgumentOutOfRangeException(nameof(edition)),
    };

    /// <summary>
    /// The check for an image whose tag has no year (design D8): the running engine's version must be
    /// SQL Server 2025 or later for a newer edition. Exit 2 otherwise.
    /// </summary>
    public static void CheckRunningVersion(EngineEdition edition, string image, string productVersion)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(productVersion);
        if (edition == EngineEdition.Developer || ReleaseYearFromImage(image) is not null)
        {
            return;
        }

        var major = int.TryParse(productVersion.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        if (major < MinimumMajorVersionForNewEditions)
        {
            throw new ConfigException(
                $"edition {SettingNames.Of(edition)} needs SQL Server {MinimumYearForNewEditions} or later, but the engine image {image} runs version {productVersion}.",
                null, SettingKeys.EngineEdition);
        }
    }
}
