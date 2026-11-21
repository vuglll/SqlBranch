using System.Globalization;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

public sealed partial class ImageBuilder
{
    private async Task CleanLeftoverLoginsAsync(IEngineSession session, State current, CancellationToken token)
    {
        foreach (var id in await BuildLogins.ListIdsAsync(session, token).ConfigureAwait(false))
        {
            var name = ImagePaths.BuildDatabase(id);
            try
            {
                if (id == current.Id || !await EngineLocks.TryTakeAsync(session, EngineLocks.Build(id), token).ConfigureAwait(false))
                {
                    continue;
                }
                var login = Quoting.Literal(name);
                if (await session.ScalarAsync($"SELECT SUSER_ID({login})", token).ConfigureAwait(false) is null)
                {
                    continue; // Another build finished cleanup before this session acquired the lock.
                }
                var other = await session.ScalarAsync($"SELECT COUNT(*) FROM sys.databases WHERE owner_sid=SUSER_SID({login}) AND name <> {login}", token).ConfigureAwait(false);
                if (Convert.ToInt32(other, CultureInfo.InvariantCulture) != 0)
                {
                    current.Warnings.Add($"Leftover login {name} owns another database. It was left in place.");
                    continue;
                }

                var leftover = new State(current.Request, id) { Endpoint = current.Endpoint };
                var files = await FilesAsync(session, leftover, token).ConfigureAwait(false);
                if (files.Any(f => !InsideBuildDirectory(leftover, f.Physical)))
                {
                    current.Warnings.Add($"Leftover database {name} has files outside its build folder. It was left in place.");
                    continue;
                }
                var db = Quoting.Identifier(name);
                await session.ExecuteAsync($"IF SUSER_ID({login}) IS NOT NULL ALTER LOGIN {db} DISABLE", token).ConfigureAwait(false);
                await KillSessionsAsync(session, leftover, token).ConfigureAwait(false);
                if (files.Count > 0)
                {
                    await session.ExecuteAsync($"DROP DATABASE {db}", token).ConfigureAwait(false);
                }
                await RemoveAbandonedBuildFolderAsync(session, leftover, token).ConfigureAwait(false);
                await session.ExecuteAsync($"IF SUSER_ID({login}) IS NOT NULL DROP LOGIN {db}", token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                current.Warnings.Add($"Could not clean up leftover login {name}: {_redactor.Redact(ex.Message)}");
            }
        }
    }

    /// <summary>Removes a dead build's folder, except a credential marker whose credential still exists (design D4, "Keeping the marker").</summary>
    private async Task RemoveAbandonedBuildFolderAsync(IEngineSession session, State leftover, CancellationToken token)
    {
        var marker = Quoting.Shell($"{leftover.BuildDirectory}/{CredentialMarker.FileName}");
        var read = await ShellAsync($"if [ -f {marker} ]; then echo \"SQLBRANCH_CRED|x|$(base64 -w0 {marker})\"; fi; true", "reading the credential record", token).ConfigureAwait(false);
        var kept = BuildCredentials.ParseMarkers(read).Select(m => m.Marker).FirstOrDefault();
        if (kept is not null && await BuildCredentials.StillExistsAsync(session, kept, token).ConfigureAwait(false))
        {
            await ShellAsync($"find {Quoting.Shell(leftover.BuildDirectory)} -mindepth 1 ! -name {CredentialMarker.FileName} -delete", "removing the abandoned build folder", token).ConfigureAwait(false);
            return;
        }

        await ShellAsync($"rm -rf {Quoting.Shell(leftover.BuildDirectory)}", "removing the abandoned build folder", token).ConfigureAwait(false);
    }

    internal static async Task CheckGuestAsync(IEngineSession session, CancellationToken token)
    {
        var rows = await session.QueryAsync("SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND user_access <> 1", token).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var name = (string)row[0]!;
            var db = Quoting.Identifier(name);
            object? allowed;
            try
            {
                allowed = await session.ScalarAsync($"SELECT COUNT(*) FROM {db}.sys.database_permissions WHERE class = 0 AND grantee_principal_id = 2 AND permission_name = 'CONNECT' AND state IN ('G','W')", token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SqlBranchException(ErrorCodes.GuestDatabase, $"Guest permissions in '{name}' could not be read.");
            }
            if (Convert.ToInt32(allowed, CultureInfo.InvariantCulture) != 0)
            {
                throw new SqlBranchException(ErrorCodes.GuestDatabase, $"Database '{name}' grants CONNECT to guest.");
            }
        }
    }

    private async Task CreateScriptDatabaseAsync(IEngineSession session, State state, CancellationToken token)
    {
        await StepDoneAsync("check-scripts", token).ConfigureAwait(false);
        (state.EngineVersion, _) = await EngineStarter.ReadVersionAsync(session, token).ConfigureAwait(false);
        await ShellAsync($"mkdir -p {Quoting.Shell(ImagePaths.BuildRoot)} && mkdir {Quoting.Shell(state.BuildDirectory)} && {ImagePaths.MakeBuildReserve(state.BuildDirectory)}", "creating the build folder", token).ConfigureAwait(false);
        state.BuildDirectoryCreated = true;
        var db = Quoting.Identifier(state.Database);
        state.DatabaseMayExist = true;
        await session.ExecuteAsync($"CREATE DATABASE {db} ON PRIMARY (NAME=N'data', FILENAME={Quoting.Literal(state.BuildDirectory + "/data.mdf")}) LOG ON (NAME=N'log', FILENAME={Quoting.Literal(state.BuildDirectory + "/log.ldf")})", token).ConfigureAwait(false);
        state.Triggers = await DatabaseTriggerCatalog.ReadAsync(session, state.Database, token).ConfigureAwait(false);
        await StepDoneAsync("create-database", token).ConfigureAwait(false);
    }

    private async Task PrepareScriptLoginAsync(IEngineSession session, State state, CancellationToken token)
    {
        var db = Quoting.Identifier(state.Database);
        var password = PasswordGenerator.Generate();
        _redactor.Register(password);
        await session.ExecuteAsync($"CREATE LOGIN {db} WITH PASSWORD={Quoting.Literal(password)}, CHECK_POLICY=ON, DEFAULT_DATABASE={db}", token).ConfigureAwait(false);
        state.LoginPassword = password;
        await session.ExecuteAsync($"ALTER AUTHORIZATION ON DATABASE::{db} TO {db}", token).ConfigureAwait(false);
    }

    private async Task RunScriptsAsync(State state, CancellationToken token)
    {
        await using var restricted = await _connector.ConnectLoginAsync(state.Endpoint, state.Database, state.LoginPassword!, state.Database, token).ConfigureAwait(false);
        async Task RunAsync(ImageScript script)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var timerStop = new CancellationTokenSource();
            var timer = CancelAfterAsync(deadline, state.Request.ScriptTimeout, timerStop.Token);
            var line = 1;
            var start = _time.GetTimestamp();
            try
            {
                foreach (var batch in script.Batches)
                {
                    line = batch.Line;
                    await restricted.ExecuteAsync(batch.Sql, new Dictionary<string, object?>(), deadline.Token).ConfigureAwait(false);
                }
                state.ScriptTimes.Add((script.File.Name, _time.GetElapsedTime(start)));
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                var reason = deadline.IsCancellationRequested
                    ? FormattableString.Invariant($"time limit of {state.Request.ScriptTimeout!.Value.TotalMinutes:g} minutes exceeded")
                    : _redactor.Redact(ex.Message);
                throw new SqlBranchException(ErrorCodes.ScriptFailed, $"{script.File.Name}, batch at line {line}: {reason}");
            }
            finally
            {
                await timerStop.CancelAsync().ConfigureAwait(false);
                await timer.ConfigureAwait(false);
            }
        }

        foreach (var script in state.Request.SourceScripts)
        {
            await RunAsync(script).ConfigureAwait(false);
        }
        if (state.Request.SourceScripts.Count > 0)
        {
            await StepDoneAsync("run-scripts", token).ConfigureAwait(false);
        }
        foreach (var script in state.Request.PostScripts)
        {
            await RunAsync(script).ConfigureAwait(false);
        }
    }

    private static async Task CancelAfterAsync(CancellationTokenSource deadline, TimeSpan? timeout, CancellationToken stop)
    {
        if (timeout is not { } remaining)
        {
            return;
        }
        try
        {
            // A single runtime timer cannot represent the full 365-day input range.
            while (remaining > TimeSpan.Zero)
            {
                var part = remaining > TimeSpan.FromDays(30) ? TimeSpan.FromDays(30) : remaining;
                await Task.Delay(part, stop).ConfigureAwait(false);
                remaining -= part;
            }
            await deadline.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Script completed before its deadline.
        }
    }

    private static async Task KillSessionsAsync(IEngineSession session, State state, CancellationToken token)
    {
        var ids = await session.QueryAsync($"SELECT session_id FROM sys.dm_exec_sessions WHERE original_login_name={Quoting.Literal(state.Database)}", token).ConfigureAwait(false);
        foreach (var row in ids)
        {
            var id = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
            await session.ExecuteAsync(FormattableString.Invariant($"IF EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE session_id={id} AND original_login_name={Quoting.Literal(state.Database)}) KILL {id}"), token).ConfigureAwait(false);
        }
    }

    private static async Task DropBuildLoginAsync(IEngineSession session, State state, CancellationToken token)
    {
        if (state.LoginPassword is null)
        {
            return;
        }
        await KillSessionsAsync(session, state, token).ConfigureAwait(false);
        await session.ExecuteAsync($"DROP LOGIN {Quoting.Identifier(state.Database)}", token).ConfigureAwait(false);
        state.LoginPassword = null;
    }

    private static async Task<IReadOnlyList<(string Logical, string Physical, string Type, long Bytes)>> FreezeAndCheckAsync(IEngineSession session, State state, CancellationToken token)
    {
        var db = Quoting.Identifier(state.Database);
        var name = Quoting.Literal(state.Database);
        if (state.Ownership is { } ownership)
        {
            await ownership.DisableLoginAsync(state.Operation!, token).ConfigureAwait(false);
        }
        else
        {
            await session.ExecuteAsync($"ALTER LOGIN {db} DISABLE", token).ConfigureAwait(false);
        }
        async Task CheckStateAsync()
        {
            var rows = await session.QueryAsync($"SELECT state_desc, is_read_only, user_access_desc FROM sys.databases WHERE name={name}", token).ConfigureAwait(false);
            if (rows.Count != 1 || rows[0] is not ["ONLINE", false, "MULTI_USER"])
            {
                throw new SqlBranchException(ErrorCodes.ScriptFailed, $"Script left database {state.Database} in an unsupported state: {string.Join(", ", rows.Select(r => $"{r[0]}, {(r[1] is true ? "READ_ONLY" : "READ_WRITE")}, {r[2]}"))}.");
            }
        }

        await CheckStateAsync().ConfigureAwait(false);
        await session.ExecuteAsync($"ALTER DATABASE {db} SET DISABLE_BROKER WITH ROLLBACK IMMEDIATE", token).ConfigureAwait(false);
        if (state.Ownership is null)
        {
            await KillSessionsAsync(session, state, token).ConfigureAwait(false);
        }
        await CheckStateAsync().ConfigureAwait(false);
        state.Triggers = await RequireTriggersUnchangedAsync(session, state, token).ConfigureAwait(false);
        var files = await FilesAsync(session, state, token).ConfigureAwait(false);
        var outside = files.Where(f => !InsideBuildDirectory(state, f.Physical)).Select(f => f.Physical).ToArray();
        if (outside.Length > 0)
        {
            throw new SqlBranchException(ErrorCodes.UnsupportedFeature, $"Database files outside the build folder: {string.Join(", ", outside)}.");
        }
        var groups = await session.QueryAsync($"SELECT name FROM {db}.sys.filegroups WHERE type IN ('FD','FX')", token).ConfigureAwait(false);
        if (groups.Count != 0)
        {
            throw new SqlBranchException(ErrorCodes.UnsupportedFeature, $"Unsupported filegroup '{groups[0][0]}'.");
        }
        state.InitialLogBytes = files.Where(f => f.Type == "LOG").Sum(f => f.Bytes);
        return files;
    }

    /// <summary>
    /// Reads the database's DDL triggers and fails with <c>ddl_trigger</c> when one is not in the snapshot taken
    /// before the scripts ran: new, changed or re-enabled. Triggers that disappeared are harmless and allowed.
    /// </summary>
    private static async Task<IReadOnlyList<DatabaseTrigger>> RequireTriggersUnchangedAsync(IEngineSession session, State state, CancellationToken token)
    {
        var current = await DatabaseTriggerCatalog.ReadAsync(session, state.Database, token).ConfigureAwait(false);
        foreach (var trigger in current)
        {
            if (!state.Triggers.Contains(trigger))
            {
                throw new SqlBranchException(ErrorCodes.DdlTrigger, $"Script created or changed database trigger '{trigger.Name}'.");
            }
        }
        return current;
    }

    private static bool InsideBuildDirectory(State state, string path) =>
        path.StartsWith(state.BuildDirectory + "/", StringComparison.Ordinal) &&
        !path.Split('/').Any(p => p is "." or "..");
}
