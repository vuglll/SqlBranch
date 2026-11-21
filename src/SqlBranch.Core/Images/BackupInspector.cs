using System.Globalization;
using System.Text.RegularExpressions;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

/// <summary>One backup set in a backup file, from <c>RESTORE HEADERONLY</c>.</summary>
/// <param name="Position">The set's number in the file, from 1.</param>
/// <param name="BackupType">1 database, 2 log, 4 file, 5 differential database, 6 differential file, 7 partial, 8 differential partial.</param>
/// <param name="TypeDescription">The type in words, for messages.</param>
/// <param name="DatabaseName">The database the backup is of.</param>
/// <param name="Version">The SQL Server version that made the backup.</param>
/// <param name="RecoveryModel">The recovery model at backup time.</param>
public sealed record BackupSet(int Position, int BackupType, string TypeDescription, string DatabaseName, Version Version, string RecoveryModel);

/// <summary>One file of the backed-up database, from <c>RESTORE FILELISTONLY</c>.</summary>
/// <param name="LogicalName">The name inside the database.</param>
/// <param name="PhysicalName">Where the file was when the backup was made.</param>
/// <param name="Type"><c>D</c> data, <c>L</c> log, <c>F</c> full-text catalog, <c>S</c> FILESTREAM or memory-optimized container.</param>
/// <param name="FileGroup">The filegroup name, empty for a log file.</param>
/// <param name="SizeBytes">The size the restored file gets.</param>
public sealed record BackupFile(string LogicalName, string PhysicalName, string Type, string FileGroup, long SizeBytes);

/// <summary>What the engine says about one backup set of a file.</summary>
/// <param name="Set">The chosen set.</param>
/// <param name="SetCount">How many sets the file holds.</param>
/// <param name="Files">The database files that a restore creates.</param>
public sealed record BackupInspection(BackupSet Set, int SetCount, IReadOnlyList<BackupFile> Files)
{
    /// <summary>The total size of the files a restore creates.</summary>
    public long TotalBytes => Files.Sum(f => f.SizeBytes);

    /// <summary>The total size of the log files.</summary>
    public long LogBytes => Files.Where(f => f.Type == "L").Sum(f => f.SizeBytes);
}

/// <summary>The statement that read the backup when the engine reported an error; the same error number means different things in different stages.</summary>
public enum BackupStage
{
    /// <summary><c>RESTORE LABELONLY</c>: the media label.</summary>
    Label,

    /// <summary><c>RESTORE HEADERONLY</c>: the backup sets.</summary>
    Header,

    /// <summary><c>RESTORE FILELISTONLY</c>: the files of one set.</summary>
    FileList,

    /// <summary><c>RESTORE DATABASE</c> itself.</summary>
    Restore,
}

/// <summary>
/// Reads a backup file through the engine (<c>RESTORE LABELONLY</c>, <c>HEADERONLY</c> and
/// <c>FILELISTONLY</c>) and rejects what an image cannot be built from. The client never parses
/// the backup format. The checks are separate functions of the rows, so they run on recorded
/// results.
/// </summary>
public static partial class BackupInspector
{
    /// <summary>Errors that mean the engine does not read the file as a backup: 3241, 3242, 3243 (media family) and 3254 (the volume is empty, which is what a text file looks like). 3013 only says the statement terminated, so it is not enough on its own.</summary>
    private static readonly int[] NotABackupErrors = [3241, 3242, 3243, 3254];

    /// <summary>Cannot open the backup device; the message carries the operating system's reason.</summary>
    private const int CannotOpenError = 3201;

    /// <summary>The backup was made by a version of SQL Server this server cannot restore.</summary>
    private const int IncompatibleVersionError = 3169;

    [GeneratedRegex(@"version\s+(?<v>\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionInMessage();

    /// <summary>The media set has more media families than were given: a backup striped across files.</summary>
    private const int StripedError = 3132;

    public const string BackupParameter = "bak";

    public const string FileParameter = "file";

    public const string LabelSql = "RESTORE LABELONLY FROM DISK = @bak";

    public const string HeaderSql = "RESTORE HEADERONLY FROM DISK = @bak";

    public const string FileListSql = "RESTORE FILELISTONLY FROM DISK = @bak WITH FILE = @file";

    /// <summary>
    /// The <c>RESTORE_OPTIONS</c> clause that names the region of an S3 backup, or an empty string. The region was
    /// checked against <c>^[a-z0-9-]{1,32}$</c> before it got here, and is checked again so it can never leave the JSON text.
    /// </summary>
    public static string UrlOptions(BackupUrl url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Kind != BackupUrlKind.S3)
        {
            return string.Empty;
        }

        var region = url.S3Region ?? BackupUrl.DefaultS3Region;
        if (!RegionText().IsMatch(region))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, "--s3-region must be 1 to 32 characters: lowercase letters, digits and hyphens.");
        }

        return $"RESTORE_OPTIONS = '{{\"s3\": {{\"region\":\"{region}\"}}}}'";
    }

    [GeneratedRegex(@"\A[a-z0-9-]{1,32}\z")]
    private static partial Regex RegionText();

    /// <summary>The statements that read a backup at a URL: the URL is the parameter <c>@bak</c> and there is no <c>FILE</c>, because a URL restore takes set 1 only.</summary>
    public static string UrlStatement(string verb, BackupUrl url)
    {
        var options = UrlOptions(url);
        return $"RESTORE {verb} FROM URL = @bak{(options.Length == 0 ? string.Empty : " WITH " + options)}";
    }

    private const string OsErrorPattern = @"Operating system error (?<n>\d+)";

    [GeneratedRegex(OsErrorPattern, RegexOptions.IgnoreCase)]
    private static partial Regex OsError();

    /// <summary>Inspects set <paramref name="backupSet"/> of the backup file at <paramref name="enginePath"/> (a path inside the engine container). Messages call the file <paramref name="displayName"/>, the name the user knows it by.</summary>
    public static async Task<BackupInspection> InspectAsync(
        IEngineSession session,
        string enginePath,
        int backupSet,
        string engineVersion,
        CancellationToken cancellationToken,
        string? displayName = null,
        BackupUrl? url = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var shown = displayName ?? enginePath;

        var labels = await ReadAsync(session, url is null ? LabelSql : UrlStatement("LABELONLY", url), Parameters(enginePath, null), shown, BackupStage.Label, engineVersion, cancellationToken, url).ConfigureAwait(false);
        CheckNotStriped(labels);

        var headers = (await ReadAsync(session, url is null ? HeaderSql : UrlStatement("HEADERONLY", url), Parameters(enginePath, null), shown, BackupStage.Header, engineVersion, cancellationToken, url).ConfigureAwait(false))
            .Select(ParseSet).ToList();
        var set = SelectSet(headers, backupSet);
        CheckType(set);
        CheckVersion(set, engineVersion);

        var files = (await ReadAsync(session, url is null ? FileListSql : UrlStatement("FILELISTONLY", url), Parameters(enginePath, url is null ? set.Position : null), shown, BackupStage.FileList, engineVersion, cancellationToken, url).ConfigureAwait(false))
            .Select(ParseFile).ToList();
        CheckFiles(files);
        return new BackupInspection(set, headers.Count, files);
    }

    private static Dictionary<string, object?> Parameters(string path, int? file)
    {
        var parameters = new Dictionary<string, object?> { [BackupParameter] = path };
        if (file is { } position)
        {
            parameters[FileParameter] = position;
        }

        return parameters;
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(
        IEngineSession session, string sql, Dictionary<string, object?> parameters, string path, BackupStage stage, string engineVersion, CancellationToken cancellationToken, BackupUrl? url = null)
    {
        try
        {
            return await session.QueryRowsAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (EngineSqlException ex)
        {
            throw MapError(ex, path, stage, engineVersion, url);
        }
    }

    /// <summary>
    /// Turns an engine error from reading a backup into the error a user sees. 3013 alone does not mean the file is
    /// not a backup: the engine adds it to every failed RESTORE, so only the specific numbers decide.
    /// </summary>
    public static SqlBranchException MapError(EngineSqlException error, string path, BackupStage stage = BackupStage.Label, string? engineVersion = null, BackupUrl? url = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Has(StripedError))
        {
            return new SqlBranchException(ErrorCodes.UnsupportedBackupType,
                "The backup is striped across several files. Only a backup in one file is supported; make a backup to a single file and try again.");
        }

        // A URL that cannot be read: 3201 (and 3078 for a storage account that does not resolve). The reason is the
        // operating system error number inside the engine's text, so the mapping does not depend on its wording.
        if (url is not null && (error.Has(CannotOpenError) || error.Has(InvalidDeviceNameError)))
        {
            return UnreachableError(error, url);
        }

        // 3201: the file could not be opened. The operating system says why (permission, missing file, I/O error).
        if (error.Has(CannotOpenError))
        {
            return new SqlBranchException(ErrorCodes.OperationFailed, $"The engine could not open the backup {FileName(path)}: {error.Message}");
        }

        // 3169: a restore of a backup the engine's version cannot take. Both versions are in the engine's message.
        if (error.Has(IncompatibleVersionError))
        {
            return VersionError(VersionsIn(error.Message, engineVersion), "The engine cannot restore this backup.");
        }

        // The label was read, so the file is a backup, and the engine still cannot make sense of its sets: that is
        // what a backup in a format from a newer SQL Server looks like to HEADERONLY (error 3241).
        if (stage == BackupStage.Header && error.Has(3241))
        {
            return VersionError((null, engineVersion), "The engine can read the backup's label but not its contents, which is what a backup made by a newer SQL Server looks like.");
        }

        if (NotABackupErrors.Any(error.Has))
        {
            return new SqlBranchException(ErrorCodes.NotABackup,
                $"The engine cannot read {FileName(path)} as a SQL Server backup. Check that it is a full database backup made by BACKUP DATABASE, and that it is not damaged or a different kind of file.");
        }

        return stage == BackupStage.Restore
            ? new SqlBranchException(ErrorCodes.OperationFailed, $"The restore failed: {error.Message}")
            : new SqlBranchException(ErrorCodes.OperationFailed, $"The engine could not read the backup {FileName(path)}: {error.Message}");
    }

    /// <summary>The backup's and the server's version from a 3169 message ("backed up on a server running version A ... running version B"); the second falls back to what the caller knows.</summary>
    private static (string? Backup, string? Engine) VersionsIn(string message, string? engineVersion)
    {
        var found = VersionInMessage().Matches(message).Select(m => m.Groups["v"].Value).ToList();
        return (found.ElementAtOrDefault(0), found.ElementAtOrDefault(1) ?? engineVersion);
    }

    private static SqlBranchException VersionError((string? Backup, string? Engine) versions, string lead)
    {
        string Name(string? text, string fallback) =>
            Version.TryParse(text, out var v) ? "SQL Server " + SqlServerVersions.Describe(v) : fallback;

        var backup = Name(versions.Backup, "a different version of SQL Server than the engine's");
        var engine = Name(versions.Engine, "the engine's version");
        return new SqlBranchException(ErrorCodes.VersionMismatch,
            $"{lead} The backup was made by {backup}; the engine runs {engine}. " +
            "An engine cannot restore a backup from a newer version. Use an engine image of the backup's SQL Server version or later: set engineImage (or SQLBRANCH_ENGINE_IMAGE) to it, " +
            "run 'sqlbranch teardown --force' and then 'sqlbranch init', because the engine image is fixed when the engine is created. " + ManagedInstanceHint);
    }

    /// <summary>Added to every version-mismatch message: a Managed Instance backup is not told apart from any other.</summary>
    public const string ManagedInstanceHint =
        "A backup from Azure SQL Managed Instance restores only to the SQL Server version of the instance's update policy, and a backup from an Always-up-to-date instance cannot be restored to SQL Server.";

    private const int InvalidDeviceNameError = 3078;

    /// <summary>The words for the operating system errors the engine reports for a URL (recorded in the spike results of 2026-11-19).</summary>
    private static string ReasonOf(EngineSqlException error)
    {
        var number = OsError().Match(error.Message) is { Success: true } m ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : -1;
        if (number == 12175 || error.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return "the engine does not trust the endpoint's certificate. For a private or test endpoint, put the certificate authority's file in " +
                "/var/opt/mssql/security/ca-certificates in the engine container and restart the engine ('sqlbranch engine stop', then 'sqlbranch engine start')";
        }

        return number switch
        {
            5 => "access was denied. Check the storage secret and that it may read the backup",
            2 => "the backup was not found. Check the container or bucket and the file name",
            86 => "no storage credential matched the URL",
            12007 or 12029 => "the engine could not connect to the storage endpoint. Check the host name, the port and the network of the engine container",
            -1 => "the engine could not open the backup",
            _ => $"the engine could not open the backup (operating system error {number.ToString(CultureInfo.InvariantCulture)})",
        };
    }

    private static SqlBranchException UnreachableError(EngineSqlException error, BackupUrl url) =>
        new(ErrorCodes.BackupUnreachable, $"The engine could not read the backup at {url.Url}: {ReasonOf(error)}. The engine said: {error.Message}");

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>A backup striped across files has a media family count above 1.</summary>
    public static void CheckNotStriped(IReadOnlyList<IReadOnlyDictionary<string, object?>> labelRows)
    {
        ArgumentNullException.ThrowIfNull(labelRows);
        if (labelRows.Any(r => Int(r, "FamilyCount") > 1))
        {
            throw new SqlBranchException(ErrorCodes.UnsupportedBackupType,
                "The backup is striped across several files. Only a backup in one file is supported; make a backup to a single file and try again.");
        }
    }

    /// <summary>The set the user chose, or an error naming how many sets the file holds.</summary>
    public static BackupSet SelectSet(IReadOnlyList<BackupSet> sets, int number)
    {
        ArgumentNullException.ThrowIfNull(sets);
        return sets.FirstOrDefault(s => s.Position == number)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                sets.Count == 0
                    ? "The backup file holds no backup sets."
                    : $"The backup file holds {sets.Count} backup set{(sets.Count == 1 ? string.Empty : "s")}, so --backup-set {number.ToString(CultureInfo.InvariantCulture)} does not exist. Choose a number from 1 to {sets.Count.ToString(CultureInfo.InvariantCulture)}.");
    }

    /// <summary>Only a full database backup (type 1) can become an image.</summary>
    public static void CheckType(BackupSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.BackupType != 1)
        {
            throw new SqlBranchException(ErrorCodes.UnsupportedBackupType,
                $"Backup set {set.Position.ToString(CultureInfo.InvariantCulture)} is a {set.TypeDescription.ToLowerInvariant()} backup. Only full database backups are supported. " +
                "If the file holds several backup sets, choose the full one with --backup-set.");
        }
    }

    /// <summary>A backup from a newer SQL Server major version than the engine cannot be restored.</summary>
    public static void CheckVersion(BackupSet set, string engineVersion)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!Version.TryParse(engineVersion, out var engine) || set.Version.Major <= engine.Major)
        {
            return;
        }

        throw new SqlBranchException(ErrorCodes.VersionMismatch,
            $"The backup was made by SQL Server {SqlServerVersions.Describe(set.Version)}, which is newer than the engine's SQL Server {SqlServerVersions.Describe(engine)}. " +
            $"An engine cannot restore a backup from a newer version. Use an engine image of SQL Server {SqlServerVersions.Year(set.Version.Major)} or later: set engineImage (or SQLBRANCH_ENGINE_IMAGE) to it, " +
            "run 'sqlbranch teardown --force' and then 'sqlbranch init', because the engine image is fixed when the engine is created. " + ManagedInstanceHint);
    }

    /// <summary>FILESTREAM and memory-optimized filegroups cannot be cloned like plain files.</summary>
    public static void CheckFiles(IReadOnlyList<BackupFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.FirstOrDefault(f => f.Type == "S") is { } container)
        {
            // FILELISTONLY marks FILESTREAM and memory-optimized containers with the same type, S.
            throw new SqlBranchException(ErrorCodes.UnsupportedFeature,
                $"The database has a FILESTREAM or memory-optimized filegroup ('{container.FileGroup}', file '{container.LogicalName}'). " +
                "Images do not support FILESTREAM or memory-optimized filegroups; back up a database without them.");
        }
    }

    internal static BackupSet ParseSet(IReadOnlyDictionary<string, object?> row) => new(
        Int(row, "Position"),
        Int(row, "BackupType"),
        Str(row, "BackupTypeDescription") is { Length: > 0 } text ? text : "unsupported",
        Str(row, "DatabaseName"),
        new Version(Int(row, "SoftwareVersionMajor"), Int(row, "SoftwareVersionMinor"), Int(row, "SoftwareVersionBuild")),
        Str(row, "RecoveryModel"));

    internal static BackupFile ParseFile(IReadOnlyDictionary<string, object?> row) => new(
        Str(row, "LogicalName"),
        Str(row, "PhysicalName"),
        Str(row, "Type"),
        Str(row, "FileGroupName"),
        Long(row, "Size"));

    private static string Str(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

    private static int Int(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

    private static long Long(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;
}

/// <summary>Names of SQL Server versions.</summary>
public static class SqlServerVersions
{
    /// <summary>The marketing year for a major version number, or the number itself when unknown.</summary>
    public static string Year(int major) => major switch
    {
        10 => "2008",
        11 => "2012",
        12 => "2014",
        13 => "2016",
        14 => "2017",
        15 => "2019",
        16 => "2022",
        17 => "2025",
        _ => major.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>For example <c>2025 (17.0.1000)</c>.</summary>
    public static string Describe(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var build = version.Build >= 0 ? $".{version.Build.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        return $"{Year(version.Major)} ({version.Major.ToString(CultureInfo.InvariantCulture)}.{Math.Max(version.Minor, 0).ToString(CultureInfo.InvariantCulture)}{build})";
    }
}
