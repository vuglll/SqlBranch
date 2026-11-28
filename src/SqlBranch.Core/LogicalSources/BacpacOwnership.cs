using System.Buffers;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;

namespace SqlBranch.Core.LogicalSources;

internal enum BacpacSealState
{
    Import,
    Sealing,
    HandedOff,
}

internal sealed class BacpacOperation(string id)
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdef");

    public string Id { get; } = id;
    public string Database => ImagePaths.BuildDatabase(Id);
    public string Directory => ImagePaths.Build(Id);
    public string Data => Directory + "/data.mdf";
    public string Log => Directory + "/log.ldf";
    public string RebuiltLog => $"{Directory}/{Database}_log.ldf";
    public BacpacSealState SealState { get; set; }
    public string StagedImage => Directory + "/image";
    public string? ImageIdentity { get; set; }
    public string Record => Directory + "/bacpac.owner";
    public string LoginSid => "0x" + Id[1..];
    public bool DirectoryCreated { get; set; }
    public bool InitialRecordWritten { get; set; }
    public bool LoginAttempted { get; set; }
    public Guid? DatabaseGuid { get; set; }
    public string? DirectoryIdentity { get; set; }

    public static bool IsLogicalId(string id) =>
        id.Length == 33 && id[0] == 'l' && Guid.TryParseExact(id.AsSpan(1), "N", out _) &&
        id.AsSpan(1).IndexOfAnyExcept(HexDigits) == -1;
}

/// <summary>Owns the database, login and build folder until the caller finishes using the import.</summary>
internal sealed class BacpacOwnership(IContainerRuntime runtime, ContainerInfo engine, IEngineSession session)
{
    private const string BuildRoot = ImagePaths.BuildRoot;
    private static readonly string RootCheck = $"[ \"$(realpath -e {BuildRoot})\" = {BuildRoot} ]";
    private static readonly string PoolCheck = $"mountpoint -q /pool && [ \"$(realpath -e /pool)\" = /pool ]";

    public async Task VerifyEngineAsync(CancellationToken token)
    {
        var current = await runtime.InspectAsync(engine.Id!, token).ConfigureAwait(false);
        var machine = await session.ScalarAsync("SELECT CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'))", token).ConfigureAwait(false);
        if (current is not { IsManaged: true, Running: true } || current.Id != engine.Id ||
            current.Name != engine.Name || machine is not string hostname ||
            !string.Equals(hostname, current.Hostname, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlBranchException("Could not verify that SQL is running on the owned SqlBranch engine.");
        }
        await ShellAsync(PoolCheck, token).ConfigureAwait(false);
    }

    public async Task CreateAsync(BacpacOperation op, CancellationToken token)
    {
        await VerifyEngineAsync(token).ConfigureAwait(false);
        var db = Quoting.Identifier(op.Database);
        var name = Quoting.Literal(op.Database);
        if (await session.ScalarAsync($"SELECT CASE WHEN DB_ID({name}) IS NULL AND SUSER_ID({name}) IS NULL THEN 0 ELSE 1 END", token)
            .ConfigureAwait(false) is not 0)
        {
            throw new SqlBranchException($"The import database or login {op.Database} is already occupied.");
        }

        // Let a completed mkdir report ownership even if Ctrl+C arrived while Docker was returning.
        var directory = Quoting.Shell(op.Directory);
        await ShellAsync($"set -e; {Require(PoolCheck)}; mkdir -p {BuildRoot}; {Require(RootCheck)}; mkdir -m 700 {directory}",
            CancellationToken.None).ConfigureAwait(false);
        op.DirectoryCreated = true;
        op.DirectoryIdentity = (await ShellAsync($"{DirectoryCheck(op)} && stat -c '%d:%i' {directory}", CancellationToken.None).ConfigureAwait(false)).Trim();
        await WriteRecordAsync(op, CancellationToken.None).ConfigureAwait(false);
        op.InitialRecordWritten = true;
        token.ThrowIfCancellationRequested();

        await session.ExecuteAsync($"CREATE DATABASE {db} ON PRIMARY (NAME=N'data', FILENAME={Quoting.Literal(op.Data)}) " +
            $"LOG ON (NAME=N'log', FILENAME={Quoting.Literal(op.Log)});", CancellationToken.None).ConfigureAwait(false);
        op.DatabaseGuid = await GetDatabaseGuidAsync(name, CancellationToken.None).ConfigureAwait(false)
            ?? throw new SqlBranchException($"Could not verify the identity of the new database {op.Database}.");
        await WriteRecordAsync(op, CancellationToken.None).ConfigureAwait(false);
        await VerifyTargetAsync(op, token).ConfigureAwait(false);
        await session.ExecuteAsync(
            $"ALTER DATABASE {db} SET TRUSTWORTHY OFF; ALTER DATABASE {db} SET DB_CHAINING OFF; " +
            "IF EXISTS (SELECT 1 FROM sys.configurations WHERE name = 'contained database authentication' AND value_in_use = 1) " +
            $"ALTER DATABASE {db} SET CONTAINMENT = PARTIAL;", token).ConfigureAwait(false);
    }

    public async Task CreateLoginAsync(BacpacOperation op, string password, CancellationToken token)
    {
        var db = Quoting.Identifier(op.Database);
        // Commit intent before SQL. A lost reply can be recovered using the known SQL login SID.
        op.LoginAttempted = true;
        await WriteRecordAsync(op, CancellationToken.None).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await session.ExecuteAsync($"CREATE LOGIN {db} WITH PASSWORD={Quoting.Literal(password)}, SID={op.LoginSid}, " +
            $"CHECK_POLICY=ON, DEFAULT_DATABASE={db};", token).ConfigureAwait(false);
        await session.ExecuteAsync($"ALTER AUTHORIZATION ON DATABASE::{db} TO {db};", token).ConfigureAwait(false);
    }

    public async Task VerifyTargetAsync(BacpacOperation op, CancellationToken token)
    {
        if (op.DatabaseGuid is null || op.DatabaseGuid != await GetDatabaseGuidAsync(Quoting.Literal(op.Database), token).ConfigureAwait(false))
        {
            throw new SqlBranchException($"Could not verify ownership of BACPAC database {op.Database}.");
        }

        await VerifyFilesAsync(op, token).ConfigureAwait(false);
        await VerifyRecordAsync(op, token).ConfigureAwait(false);
    }

    private async Task VerifyFilesAsync(BacpacOperation op, CancellationToken token)
    {
        var files = await session.QueryAsync(
            $"SELECT physical_name FROM sys.master_files WHERE database_id=DB_ID({Quoting.Literal(op.Database)}) ORDER BY file_id", token).ConfigureAwait(false);
        if (files.Count != 2 || !Equals(files[0][0], op.Data) ||
            files[1][0] is not string log || (log != op.Log && (op.SealState != BacpacSealState.Sealing || log != op.RebuiltLog)))
        {
            throw new SqlBranchException($"The files of BACPAC database {op.Database} are outside its expected build layout.");
        }
        await ShellAsync($"{OwnedDirectory(op)} && {RegularFile(op.Data)} && {RegularFile(log)}", token).ConfigureAwait(false);
    }

    public async Task EnableScriptLoginAsync(BacpacOperation op, string password, CancellationToken token)
    {
        await VerifyTargetAsync(op, token).ConfigureAwait(false);
        var db = Quoting.Identifier(op.Database);
        var name = Quoting.Literal(op.Database);
        await session.ExecuteAsync(
            $"IF SUSER_SID({name})={op.LoginSid} AND EXISTS (SELECT 1 FROM sys.databases WHERE name={name} AND owner_sid={op.LoginSid}) " +
            $"BEGIN ALTER LOGIN {db} WITH PASSWORD={Quoting.Literal(password)}; ALTER LOGIN {db} ENABLE; END " +
            "ELSE THROW 50000, 'Build login or database owner changed.', 1;", token).ConfigureAwait(false);
    }

    public async Task BeginSealAsync(BacpacOperation op, CancellationToken token)
    {
        await VerifyEngineAsync(token).ConfigureAwait(false);
        await VerifyTargetAsync(op, token).ConfigureAwait(false);
        var rebuilt = Quoting.Shell(op.RebuiltLog);
        await ShellAsync($"{OwnedDirectory(op)} && [ ! -e {rebuilt} ] && [ ! -L {rebuilt} ]", token).ConfigureAwait(false);
        op.SealState = BacpacSealState.Sealing;
        await WriteRecordAsync(op, CancellationToken.None).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    public async Task StageImageAsync(BacpacOperation op, IReadOnlyList<SealFile> files, CancellationToken token)
    {
        await VerifyDetachedFilesAsync(op, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var path = Quoting.Shell(op.StagedImage);
        var noCancel = CancellationToken.None;
        op.ImageIdentity = (await ShellAsync($"set -e; {Require(OwnedDirectory(op))}; mkdir -- {path}; stat -c '%d:%i' {path}",
            noCancel).ConfigureAwait(false)).Trim();
        if (!IsDirectoryIdentity(op.ImageIdentity))
        {
            throw new SqlBranchException($"Could not identify the staging image for {op.Id}.");
        }
        await WriteRecordAsync(op, noCancel).ConfigureAwait(false);
        await ShellAsync($"{Require(OwnedImage(op))}; {ImageSealer.MoveFilesScript(op.StagedImage, files)}", noCancel).ConfigureAwait(false);
        await ShellAsync($"{OwnedImage(op)} && {EmptyExceptRecord(op, includeImage: true)}", noCancel).ConfigureAwait(false);

        // SQL is detached and its files now belong to the recorded staging directory.
        op.SealState = BacpacSealState.HandedOff;
        await WriteRecordAsync(op, noCancel).ConfigureAwait(false);
    }

    public async Task<bool> PublishImageAsync(BacpacOperation op, ImageManifest manifest, ImageCatalogFile? catalog = null)
    {
        var token = CancellationToken.None;
        await VerifyRecordAsync(op, token).ConfigureAwait(false);
        var root = Quoting.Shell(ImagePaths.ImagesRoot);
        var source = Quoting.Shell(op.StagedImage);
        var target = Quoting.Shell(ImagePaths.Image(manifest.Name));
        var write = ImageSealer.ManifestScript(manifest, op.StagedImage + "/manifest.json");
        // On a format-2 pool the catalog goes into the staged folder with the manifest, so the one rename publishes both.
        var catalogWrite = catalog is null ? string.Empty : $"; {Require(ImageVersions.CatalogScript(op.StagedImage, catalog))}";
        await ShellAsync($"set -eC; {Require(OwnedImage(op))}; {Require(write)}{catalogWrite}", token).ConfigureAwait(false);
        await ShellAsync($"set -e; {Require(OwnedImage(op))}; mkdir -p {root}; {Require(CanonicalDirectory(root))}; " +
            $"[ \"$(stat -c %d {source})\" = \"$(stat -c %d {root})\" ]; " +
            $"{Require(OwnedImage(op))}; mv -nT -- {source} {target}; {Require(Missing(source))}",
            token).ConfigureAwait(false);

        // A successful no-clobber move with no source remaining commits publication.
        try
        {
            await ShellAsync($"{CanonicalDirectory(target)} && " +
                $"[ \"$(stat -c '%d:%i' {target})\" = {Quoting.Shell(op.ImageIdentity!)} ]", token).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Leave the operation record available for a later sweep after publication.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    internal static string RemoveStagedImageScript(BacpacOperation op)
    {
        var image = Quoting.Shell(op.StagedImage);
        const string data = "./data";
        const string manifest = "./manifest.json";
        var names = new[] { op.Data, op.Log, op.RebuiltLog }.Select(path => Path.GetFileName(path)!).ToArray();
        var files = names.Select(name => "./" + name).ToArray();
        var checks = files.Select(file => AnyOf(Missing(Quoting.Shell(file)), RegularFile(file)));
        var imageEntries = OnlyEntries(".", "data", "manifest.json", ImageCatalogFile.FileName, ImageCatalogFile.TemporaryName);
        var dataEntries = OnlyEntries(".", names);
        var dataPath = Quoting.Shell(op.StagedImage + "/data");
        var verifyData = $"[ \"$(pwd -P)\" = {dataPath} ] && {dataEntries} && {string.Join(" && ", checks)}";
        return $"set -e; {Require(OwnedDirectory(op))}; if {Missing(image)}; then exit 0; fi; " +
            $"{Require(OwnedImage(op))}; (cd -P -- {image}; " +
            $"{Require($"[ \"$(stat -c '%d:%i' .)\" = {Quoting.Shell(op.ImageIdentity ?? "")} ]")}; " +
            $"{Require(imageEntries)}; {Require(AnyOf(Missing(manifest), RegularFile(manifest)))}; " +
            $"if {Present(data)}; then {Require($"[ -d {data} ] && [ ! -L {data} ]")}; " +
            $"(cd -P -- {data}; {Require(verifyData)}; rm -f -- {string.Join(' ', files.Select(Quoting.Shell))}); rmdir -- {data}; fi; " +
            $"rm -f -- {manifest} ./{ImageCatalogFile.FileName} ./{ImageCatalogFile.TemporaryName}); {Require(OwnedImage(op))}; rmdir -- {image}";
    }

    public async Task RemoveOriginalLogAsync(BacpacOperation op, CancellationToken token)
    {
        await VerifyDetachedFilesAsync(op, token).ConfigureAwait(false);
        await ShellAsync($"{OwnedDirectory(op)} && {RegularFile(op.Log)} && rm -- {Quoting.Shell(op.Log)}", token).ConfigureAwait(false);
    }

    public async Task RecordRebuiltAsync(BacpacOperation op)
    {
        var token = CancellationToken.None;
        await VerifyRecordAsync(op, token).ConfigureAwait(false);
        await VerifyFilesAsync(op, token).ConfigureAwait(false);
        op.DatabaseGuid = await GetDatabaseGuidAsync(Quoting.Literal(op.Database), token).ConfigureAwait(false)
            ?? throw new SqlBranchException($"Could not verify the identity of the rebuilt database {op.Database}.");
        await WriteRecordAsync(op, token).ConfigureAwait(false);
    }

    public async Task VerifyDetachedFilesAsync(BacpacOperation op, CancellationToken token)
    {
        if (op.SealState != BacpacSealState.Sealing || !await IsDetachedAsync(op, token).ConfigureAwait(false))
        {
            throw new SqlBranchException($"Could not verify detach of BACPAC database {op.Database}.");
        }

        await VerifyRecordAsync(op, token).ConfigureAwait(false);
        await ShellAsync($"{OwnedDirectory(op)} && {RegularFile(op.Data)} && {AnyOf(RegularFile(op.Log), RegularFile(op.RebuiltLog))}",
            token).ConfigureAwait(false);
    }

    public async Task DisableLoginAsync(BacpacOperation op, CancellationToken token)
    {
        if (!await VerifyLoginAsync(op, token).ConfigureAwait(false))
        {
            return;
        }

        var db = Quoting.Identifier(op.Database);
        var name = Quoting.Literal(op.Database);
        // Only this exact login can be disabled. Reap connections authenticated as its original SID.
        await session.ExecuteAsync(
            $"IF SUSER_SID({name})={op.LoginSid} ALTER LOGIN {db} DISABLE; ELSE THROW 50000, 'Build login identity changed.', 1; " +
            $"DECLARE @id smallint, @t datetime, @sql nvarchar(40); WHILE 1=1 BEGIN SET @id=NULL; " +
            $"SELECT TOP (1) @id=session_id, @t=login_time FROM sys.dm_exec_sessions WHERE original_security_id={op.LoginSid} AND status <> 'KILLED/ROLLBACK'; " +
            $"IF @id IS NULL BREAK; IF EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE session_id=@id AND original_security_id={op.LoginSid} AND login_time=@t) " +
            "BEGIN SET @sql=N'KILL '+CONVERT(nvarchar(6), @id); EXEC (@sql); END; END;", token).ConfigureAwait(false);
    }

    private async Task<bool> VerifyLoginAsync(BacpacOperation op, CancellationToken token)
    {
        var name = Quoting.Literal(op.Database);
        var found = await session.ScalarAsync(
            $"SELECT CASE WHEN SUSER_ID({name}) IS NULL THEN 0 " +
            $"WHEN EXISTS (SELECT 1 FROM sys.sql_logins WHERE name={name} AND sid={op.LoginSid}) " +
            $"AND NOT EXISTS (SELECT 1 FROM sys.databases WHERE owner_sid={op.LoginSid} AND name<>{name}) THEN 1 ELSE 2 END", token)
            .ConfigureAwait(false);
        return found switch
        {
            0 => false,
            1 when op.LoginAttempted => true,
            _ => throw new SqlBranchException($"Could not verify ownership of BACPAC login {op.Database}."),
        };
    }

    public async Task<bool> TryCleanupAsync(BacpacOperation op, bool keepStagedImage = false)
    {
        var token = CancellationToken.None;
        try
        {
            await VerifyEngineAsync(token).ConfigureAwait(false);
            var folder = Quoting.Shell(op.Directory);
            var record = Quoting.Shell(op.Record);
            if (op.DirectoryCreated && !op.InitialRecordWritten)
            {
                // No SQL was created. If the write completed but lost its reply, use the record below.
                var removed = await ShellAsync($"set -e; {Require(OwnedDirectory(op))}; " +
                    $"if {Present(record)}; then exit 0; fi; {RemoveTemporaryRecord(op)}; rmdir -- {folder}; printf removed",
                    token).ConfigureAwait(false);
                if (removed == "removed")
                {
                    return true;
                }
            }

            var current = RecordContent(op);
            var recorded = await ReadRecordAsync(op, token).ConfigureAwait(false);
            if (op.DirectoryIdentity is null || (recorded != current && !IsEarlierRecord(op, recorded)))
            {
                throw new SqlBranchException($"Could not verify build folder ownership for {op.Id}.");
            }
            await ShellAsync($"set -e; {Require(OwnedDirectory(op))}; {Require(RegularFile(op.Record))}; " +
                RemoveTemporaryRecord(op), token).ConfigureAwait(false);
            if (recorded != current && op.SealState != BacpacSealState.HandedOff)
            {
                await WriteRecordAsync(op, token).ConfigureAwait(false);
                recorded = current;
            }
            await DisableLoginAsync(op, token).ConfigureAwait(false);
            var db = Quoting.Identifier(op.Database);
            var name = Quoting.Literal(op.Database);
            if (op.SealState != BacpacSealState.HandedOff && !await IsDetachedAsync(op, token).ConfigureAwait(false))
            {
                await VerifyTargetAsync(op, token).ConfigureAwait(false);
                var guid = Quoting.Literal(op.DatabaseGuid!.Value.ToString("D"));
                await session.ExecuteAsync(
                    $"IF NOT EXISTS (SELECT 1 FROM sys.database_recovery_status WHERE database_id=DB_ID({name}) AND database_guid={guid}) " +
                    $"OR (SELECT COUNT(*) FROM sys.master_files WHERE database_id=DB_ID({name}) AND " +
                    $"((file_id=1 AND physical_name COLLATE Latin1_General_100_BIN2={Quoting.Literal(op.Data)}) OR " +
                    $"(type=1 AND physical_name COLLATE Latin1_General_100_BIN2 IN ({Quoting.Literal(op.Log)}" +
                    $"{(op.SealState == BacpacSealState.Sealing ? ", " + Quoting.Literal(op.RebuiltLog) : "")}))))<>2 " +
                    $"OR (SELECT COUNT(*) FROM sys.master_files WHERE database_id=DB_ID({name}))<>2 " +
                    $"THROW 50000, 'Build database identity changed.', 1; " +
                    $"IF DATABASEPROPERTYEX({name}, 'Status')='ONLINE' ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    $"DROP DATABASE {db};", token).ConfigureAwait(false);
            }
            if (await VerifyLoginAsync(op, token).ConfigureAwait(false))
            {
                await session.ExecuteAsync(
                    $"IF SUSER_SID({name})={op.LoginSid} DROP LOGIN {db}; ELSE THROW 50000, 'Build login identity changed.', 1;",
                    token).ConfigureAwait(false);
            }

            // A clean detach leaves only these exact files. Never traverse an unexpected path.
            if (op.SealState == BacpacSealState.Sealing)
            {
                var paths = new[] { op.Data, op.Log, op.RebuiltLog };
                var checks = string.Join("; ", paths.Select(path =>
                {
                    var quoted = Quoting.Shell(path);
                    return Require(AnyOf(Missing(quoted), RegularFile(path)));
                }));
                var files = string.Join(' ', paths.Select(Quoting.Shell));
                await ShellAsync($"set -e; {Require(OwnedDirectory(op))}; {Require(RegularFile(op.Record))}; {checks}; rm -f -- {files}",
                    token).ConfigureAwait(false);
            }
            if (keepStagedImage)
            {
                await ShellAsync($"{OwnedImage(op)} && {EmptyExceptRecord(op, includeImage: true)}", token).ConfigureAwait(false);
                return true;
            }
            if (op.ImageIdentity is not null)
            {
                await ShellAsync(RemoveStagedImageScript(op), token).ConfigureAwait(false);
            }
            // If rmdir fails, restore the verified ownership record so the folder can be swept.
            await ShellAsync(
                $"set -e; {Require(OwnedDirectory(op))}; {Require(RegularFile(op.Record))}; {Require(EmptyExceptRecord(op))}; " +
                $"saved=$(head -c 512 -- {record}); {Require($"[ \"$saved\" = {Quoting.Shell(recorded)} ]")}; " +
                $"rm -- {record}; if rmdir -- {folder}; then exit 0; fi; " +
                $"{Require(OwnedDirectory(op))}; umask 077; set -C; printf %s \"$saved\" > {record}; exit 1", token).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Return only verified identifiers to the caller, without diagnostic payloads.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    public async Task SweepAsync(CancellationToken token)
    {
        var ids = await ShellAsync(
            $"if {RootCheck}; then for d in {BuildRoot}/l*; do [ -f \"$d/bacpac.owner\" ] && basename \"$d\"; done; fi; true",
            token).ConfigureAwait(false);
        foreach (var id in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(BacpacOperation.IsLogicalId))
        {
            if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Build(id), token).ConfigureAwait(false))
            {
                continue;
            }
            try
            {
                var op = new BacpacOperation(id);
                var info = await ReadRecordAsync(op, token).ConfigureAwait(false);
                var parts = info.Split('|');
                if ((parts.Length != 5 && (parts.Length is not (6 or 7) || parts[5] is not ("seal" or "handed-off"))) ||
                    (parts.Length == 7 && !IsDirectoryIdentity(parts[6])) ||
                    parts[0] != engine.Id || parts[1] != id ||
                    parts[2] is not ("0" or "1") ||
                    (parts[3] != "-" && !Guid.TryParseExact(parts[3], "D", out _)) ||
                    await session.ScalarAsync($"SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE original_security_id={op.LoginSid}", token)
                        .ConfigureAwait(false) is not 0)
                {
                    continue;
                }

                op.LoginAttempted = parts[2] == "1";
                op.SealState = parts.Length == 5 ? BacpacSealState.Import
                    : parts[5] == "seal" ? BacpacSealState.Sealing : BacpacSealState.HandedOff;
                op.DatabaseGuid = parts[3] == "-" ? null : Guid.ParseExact(parts[3], "D");
                op.DirectoryIdentity = parts[4];
                op.ImageIdentity = parts.Length == 7 ? parts[6] : null;
                await TryCleanupAsync(op).ConfigureAwait(false);
            }
            catch (SqlBranchException)
            {
                // An ambiguous record remains available to status.
            }
            finally
            {
                await EngineLocks.ReleaseAsync(session, EngineLocks.Build(id), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<Guid?> GetDatabaseGuidAsync(string nameLiteral, CancellationToken token) =>
        await session.ScalarAsync(
            $"SELECT database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID({nameLiteral})", token).ConfigureAwait(false)
            is Guid guid ? guid : null;

    private async Task<bool> IsDetachedAsync(BacpacOperation op, CancellationToken token) =>
        await session.ScalarAsync(
            $"SELECT CASE WHEN DB_ID({Quoting.Literal(op.Database)}) IS NULL THEN 0 ELSE 1 END", token).ConfigureAwait(false) is 0;

    private string RecordContent(BacpacOperation op) =>
        RecordContent(op, op.SealState);

    private string RecordContent(BacpacOperation op, BacpacSealState state, bool includeImage = true)
    {
        var prefix = $"{engine.Id}|{op.Id}|{(op.LoginAttempted ? 1 : 0)}|{op.DatabaseGuid?.ToString("D") ?? "-"}|{op.DirectoryIdentity}";
        var stage = includeImage && op.ImageIdentity is { } identity ? "|" + identity : "";
        return state switch
        {
            BacpacSealState.Sealing => prefix + "|seal" + stage,
            BacpacSealState.HandedOff => prefix + "|handed-off" + stage,
            _ => prefix,
        };
    }

    private bool IsEarlierRecord(BacpacOperation op, string record)
    {
        if (op.SealState == BacpacSealState.HandedOff)
        {
            // A failed handoff record write can leave the last verified seal record in place.
            return record == RecordContent(op, BacpacSealState.Sealing);
        }
        if (op.SealState == BacpacSealState.Sealing && op.ImageIdentity is not null &&
            record == RecordContent(op, BacpacSealState.Sealing, includeImage: false))
        {
            return true;
        }
        var parts = record.Split('|');
        return parts.Length == 5 && parts[0] == engine.Id && parts[1] == op.Id && parts[4] == op.DirectoryIdentity &&
            (parts[2] == "0" || (op.LoginAttempted && parts[2] == "1")) &&
            (parts[3] == "-" || parts[3] == op.DatabaseGuid?.ToString("D"));
    }

    private async Task WriteRecordAsync(BacpacOperation op, CancellationToken token)
    {
        var record = Quoting.Shell(op.Record);
        var temporary = Quoting.Shell(op.Record + ".new");
        var discardTemporary = Quoting.Shell($"rm -f -- {temporary}");
        await ShellAsync($"set -e; {Require(OwnedDirectory(op))}; umask 077; " +
            $"(set -C; (trap {discardTemporary} 0; printf %s {Quoting.Shell(RecordContent(op))}; " +
            $"mv -T -- {temporary} {record}; trap - 0) > {temporary})", token).ConfigureAwait(false);
    }

    private Task<string> ReadRecordAsync(BacpacOperation op, CancellationToken token) =>
        ShellAsync($"{DirectoryCheck(op)} && {RegularFile(op.Record)} && head -c 512 -- {Quoting.Shell(op.Record)}", token);

    private async Task VerifyRecordAsync(BacpacOperation op, CancellationToken token)
    {
        if (op.DirectoryIdentity is null || await ReadRecordAsync(op, token).ConfigureAwait(false) != RecordContent(op))
        {
            throw new SqlBranchException($"Could not verify build folder ownership for {op.Id}.");
        }
    }

    private static string DirectoryCheck(BacpacOperation op) =>
        $"{RootCheck} && [ \"$(realpath -e {Quoting.Shell(op.Directory)})\" = {Quoting.Shell(op.Directory)} ] && [ -d {Quoting.Shell(op.Directory)} ]";

    private static string OwnedDirectory(BacpacOperation op) =>
        $"{DirectoryCheck(op)} && [ \"$(stat -c '%d:%i' {Quoting.Shell(op.Directory)})\" = {Quoting.Shell(op.DirectoryIdentity ?? "")} ]";

    private static string EmptyExceptRecord(BacpacOperation op, bool includeImage = false) =>
        includeImage ? OnlyEntries(Quoting.Shell(op.Directory), "bacpac.owner", "image")
            : OnlyEntries(Quoting.Shell(op.Directory), "bacpac.owner");

    private static string OnlyEntries(string path, params string[] allowed) =>
        $"unexpected=$(find {path} -mindepth 1 -maxdepth 1 {string.Join(' ', allowed.Select(name => $"! -name {name}"))} -print -quit) && [ \"$unexpected\" = '' ]";

    private static string OwnedImage(BacpacOperation op)
    {
        var image = Quoting.Shell(op.StagedImage);
        return $"{OwnedDirectory(op)} && {CanonicalDirectory(image)} && " +
            $"[ \"$(stat -c '%d:%i' {image})\" = {Quoting.Shell(op.ImageIdentity ?? "")} ]";
    }

    private static string CanonicalDirectory(string quoted) =>
        $"[ -d {quoted} ] && [ \"$(realpath -e {quoted})\" = {quoted} ]";

    private static string Missing(string quoted) => $"[ ! -e {quoted} ] && [ ! -L {quoted} ]";

    private static string Present(string quoted) => AnyOf($"[ -e {quoted} ]", $"[ -L {quoted} ]");

    private static string RemoveTemporaryRecord(BacpacOperation op)
    {
        var path = op.Record + ".new";
        var quoted = Quoting.Shell(path);
        return $"if {Present(quoted)}; then {Require(RegularFile(path))}; rm -- {quoted}; fi";
    }

    private static bool IsDirectoryIdentity(string value) =>
        value.Split(':') is [var device, var inode] && device.Length > 0 && inode.Length > 0 &&
        device.All(char.IsAsciiDigit) && inode.All(char.IsAsciiDigit);

    internal static string Require(string p) => $"{{ {p}; }} || exit 1";

    // In sh, && and || have equal precedence and group left to right, so `a && b || c && d` runs d even when
    // a && b succeeded. Group each alternative so only one of them has to hold.
    internal static string AnyOf(string first, string second) => $"{{ {{ {first}; }} || {{ {second}; }}; }}";

    private static string RegularFile(string path)
    {
        var quoted = Quoting.Shell(path);
        return $"[ -f {quoted} ] && [ ! -L {quoted} ] && [ \"$(stat -c %h {quoted})\" = 1 ]";
    }

    public async Task<string> ShellAsync(string script, CancellationToken token)
    {
        var result = await runtime.ExecAsync(engine.Id!, ["sh", "-c", script], token).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SqlBranchException("The engine could not verify or access the BACPAC build paths.");
        }
        return result.StdOut;
    }
}
