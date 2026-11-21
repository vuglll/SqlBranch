using System.Text.RegularExpressions;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

/// <summary>Where a URL backup lives.</summary>
public enum BackupUrlKind
{
    /// <summary>An <c>https://</c> Azure Blob Storage URL.</summary>
    AzureBlob,

    /// <summary>An <c>s3://</c> URL, path style or virtual-host style.</summary>
    S3,
}

/// <summary>
/// A <c>--from-bak</c> value that is a URL, checked and split into the parts a
/// build needs. Nothing here touches the network, <c>docker</c> or SQL, and no
/// message ever echoes a query string, so a pasted SAS URL cannot leak.
/// </summary>
public sealed partial record BackupUrl(
    BackupUrlKind Kind,
    string Url,
    string CredentialName,
    string BackupFile,
    string? S3Region)
{
    /// <summary>The longest URL the engine accepts for a backup device.</summary>
    public const int MaxUrlLength = 259;

    /// <summary>The longest credential name the engine accepts.</summary>
    public const int MaxCredentialNameLength = 128;

    /// <summary>The region used for an S3 URL when <c>--s3-region</c> is not given.</summary>
    public const string DefaultS3Region = "us-east-1";

    /// <summary>The variable that holds a SAS token.</summary>
    public const string SasVariable = "SQLBRANCH_AZURE_SAS_TOKEN";

    [GeneratedRegex(@"\A[a-z0-9-]{1,32}\z")]
    private static partial Regex RegionPattern();

    [GeneratedRegex(@"\A.+\.s3([.-][a-z0-9-]+)*\.amazonaws\.com(\.cn)?\z", RegexOptions.IgnoreCase)]
    internal static partial Regex VirtualHostPattern();

    /// <summary>Whether the value is a URL (<c>http://</c>, <c>https://</c> or <c>s3://</c>, any case) and not a file path.</summary>
    public static bool IsUrl(string? value) =>
        value is not null &&
        (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase));

    /// <summary>The option whose value is a backup URL in <c>image build</c>.</summary>
    public const string BuildOption = "--from-bak";

    /// <summary>Throws exit 2 <c>usage_error</c> when <c>--s3-region</c> is given but cannot apply or is malformed.</summary>
    /// <param name="region">The <c>--s3-region</c> value, or null when it was not given.</param>
    /// <param name="url">The parsed URL, or null when the option's value is not a URL.</param>
    /// <param name="optionName">The option that carries the URL, for the message: <c>--from-bak</c>, <c>--to</c>, or the import source.</param>
    public static void CheckRegionOption(string? region, BackupUrl? url, string optionName = BuildOption)
    {
        if (region is null)
        {
            return;
        }

        if (url is not { Kind: BackupUrlKind.S3 })
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"--s3-region applies only to an s3:// URL given with {optionName}.");
        }

        if (!RegionPattern().IsMatch(region))
        {
            throw new SqlBranchException(ErrorCodes.UsageError,
                "--s3-region must be 1 to 32 characters: lowercase letters, digits and hyphens.");
        }
    }

    /// <summary>
    /// Parses a backup or archive URL, or throws exit 2 <c>usage_error</c>. <c>image build</c> passes <c>--from-bak</c> and the
    /// engine's limits (<see cref="MaxUrlLength"/> and <see cref="MaxCredentialNameLength"/>); export and import pass their option
    /// and no limits (<c>null</c>), because the limits belong to SQL Server and the client does that I/O (design D1).
    /// </summary>
    /// <param name="value">The URL as given.</param>
    /// <param name="s3Region">The <c>--s3-region</c> value, or null.</param>
    /// <param name="optionName">The option that carries the URL, for messages. <c>--from-bak</c> means a backup; any other, an archive.</param>
    /// <param name="maxUrlLength">The longest URL accepted, or null for no limit.</param>
    /// <param name="maxCredentialNameLength">The longest credential name accepted, or null for no limit.</param>
    public static BackupUrl Parse(
        string value,
        string? s3Region = null,
        string optionName = BuildOption,
        int? maxUrlLength = MaxUrlLength,
        int? maxCredentialNameLength = MaxCredentialNameLength)
    {
        var noun = optionName == BuildOption ? "backup" : "archive";
        var fileNoun = optionName == BuildOption ? "a backup file" : "a file";
        if (!IsUrl(value))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"{optionName} is not a URL.");
        }

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var scheme = value[..(schemeEnd - 3)].ToLowerInvariant();
        if (scheme == "http")
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"Only https:// and s3:// {noun} URLs are supported, not http://.");
        }

        if (value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            throw new SqlBranchException(ErrorCodes.UsageError,
                $"The {noun} URL must not have a query string or fragment. Give the SAS token in {SasVariable} instead; it is never read from the URL.");
        }

        if (maxUrlLength is { } urlLimit && value.Length > urlLimit)
        {
            throw new SqlBranchException(ErrorCodes.UsageError,
                $"The {noun} URL is {value.Length} characters; the limit is {urlLimit}.");
        }

        var rest = value[schemeEnd..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? string.Empty : rest[slash..];
        if (authority.Length == 0)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"The {noun} URL has no host.");
        }

        if (authority.Contains('@', StringComparison.Ordinal))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"The {noun} URL must not contain user information.");
        }

        var kind = scheme == "s3" ? BackupUrlKind.S3 : BackupUrlKind.AzureBlob;
        var hostOnly = authority.Contains(':', StringComparison.Ordinal) ? authority[..authority.IndexOf(':', StringComparison.Ordinal)] : authority;
        var virtualHost = kind == BackupUrlKind.S3 && VirtualHostPattern().IsMatch(hostOnly);
        var needed = kind == BackupUrlKind.S3 && virtualHost ? 1 : 2;
        var what = kind == BackupUrlKind.AzureBlob ? "a container and a file" : virtualHost ? "a file" : "a bucket and a file";

        if (path.EndsWith('/'))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"The {noun} URL must name {fileNoun}; it ends in '/'.");
        }

        var segments = path.Length == 0 ? [] : path[1..].Split('/');
        if (segments.Length < needed || segments.Any(s => s.Length == 0))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"The {noun} URL must name {what}.");
        }

        // Blob: the container, the documented credential name. S3: the URL up to its last '/'.
        var name = kind == BackupUrlKind.AzureBlob
            ? $"{scheme}://{authority}/{segments[0]}"
            : $"{scheme}://{authority}{path[..path.LastIndexOf('/')]}";
        if (maxCredentialNameLength is { } nameLimit && name.Length > nameLimit)
        {
            throw new SqlBranchException(ErrorCodes.UsageError,
                $"The credential name for this URL is {name.Length} characters; the limit is {nameLimit}. Use a shorter folder path.");
        }

        var url = $"{scheme}://{rest}";
        var parsed = new BackupUrl(kind, url, name, Uri.UnescapeDataString(segments[^1]), kind == BackupUrlKind.S3 ? s3Region ?? DefaultS3Region : null);
        CheckRegionOption(s3Region, parsed, optionName);
        return parsed;
    }
}
