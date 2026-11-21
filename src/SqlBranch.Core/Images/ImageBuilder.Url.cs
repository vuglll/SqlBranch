using System.Globalization;
using System.Text;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

/// <summary>The steps of a build from a backup URL and the life of its storage credential (design D2 and D4).</summary>
public sealed partial class ImageBuilder
{
    /// <summary>The longest SAS token an engine below SQL Server 2022 takes in a credential.</summary>
    private const int LegacySasLimit = 256;

    private static readonly string ListMarkersScript = BuildCredentials.ListMarkersLoop + "; echo SQLBRANCH_CRED_DONE";

    private async Task BuildUrlSourceAsync(IEngineSession session, State state, CancellationToken token)
    {
        var url = state.Request.Url!;
        var secrets = state.Request.Secrets ?? throw new InvalidOperationException("A URL build needs its storage secret.");

        // check-source: what this engine can read, before anything is created.
        (state.EngineVersion, _) = await EngineStarter.ReadVersionAsync(session, token).ConfigureAwait(false);
        if (Version.TryParse(state.EngineVersion, out var engine) && engine.Major < 16)
        {
            var named = $"SQL Server {SqlServerVersions.Describe(engine)}";
            if (url.Kind == BackupUrlKind.S3)
            {
                throw new SqlBranchException(ErrorCodes.UnsupportedSource,
                    $"An s3:// URL needs SQL Server 2022 or later, and the engine runs {named}. " +
                    "Set engineImage (or SQLBRANCH_ENGINE_IMAGE) to a SQL Server 2022 or later image, run 'sqlbranch teardown --force' and then 'sqlbranch init', because the engine image is fixed when the engine is created.");
            }

            if (secrets.Secret.Length > LegacySasLimit)
            {
                throw new SqlBranchException(ErrorCodes.ConfigError,
                    $"The SAS token in {BackupUrl.SasVariable} is longer than {LegacySasLimit.ToString(CultureInfo.InvariantCulture)} characters, which an engine below SQL Server 2022 does not accept, and the engine runs {named}. " +
                    "Create a shorter SAS token, or use an engine image of SQL Server 2022 or later.");
            }
        }

        await StepDoneAsync("check-source", token).ConfigureAwait(false);

        // build-folder: the file path makes this folder inside "stage".
        state.BuildDirectoryCreated = true;
        await ShellAsync($"mkdir -p {Quoting.Shell(ImagePaths.BuildRoot)} && mkdir {Quoting.Shell(state.BuildDirectory)} && {ImagePaths.MakeBuildReserve(state.BuildDirectory)}", "creating the build folder", token).ConfigureAwait(false);
        await StepDoneAsync("build-folder", token).ConfigureAwait(false);

        // credential: lock, re-sweep, conflict check, intent marker, create, full marker.
        await CreateCredentialAsync(session, state, url, secrets, token).ConfigureAwait(false);
        await StepDoneAsync("credential", token).ConfigureAwait(false);

        // inspect: the engine reads the URL; the secret is only in the credential.
        state.BackupPath = url.Url;
        try
        {
            state.Inspection = await BackupInspector.InspectAsync(session, url.Url, 1, state.EngineVersion, token, url.BackupFile, url).ConfigureAwait(false);
        }
        catch (SqlBranchException ex)
        {
            throw Redacted(ex);
        }

        await StepDoneAsync("inspect", token).ConfigureAwait(false);

        await RequireSpaceAsync(state.Inspection.TotalBytes, "the restored database files", token).ConfigureAwait(false);
        await StepDoneAsync("check-restore-space", token).ConfigureAwait(false);

        try
        {
            await RestoreAsync(session, state, token).ConfigureAwait(false);
        }
        catch (SqlBranchException ex)
        {
            throw Redacted(ex);
        }

        state.Triggers = await DatabaseTriggerCatalog.ReadAsync(session, state.Database, token).ConfigureAwait(false);
        await StepDoneAsync("restore", token).ConfigureAwait(false);

        // drop-credential: right after the restore, before any post-build script can run.
        await DropCredentialAsync(session, state, token).ConfigureAwait(false);
        await StepDoneAsync("drop-credential", token).ConfigureAwait(false);
    }

    private SqlBranchException Redacted(SqlBranchException ex) => new(ex.Code, _redactor.Redact(ex.Message));

    private async Task CreateCredentialAsync(IEngineSession session, State state, BackupUrl url, SqlBranch.Core.Secrets.StorageSecrets secrets, CancellationToken token)
    {
        var name = url.CredentialName;
        var resource = EngineLocks.Credential(name);

        // The wait is a client loop: the engine's own lock wait would hit the 120 s command timeout and cannot be cancelled cleanly.
        var announced = false;
        while (!await EngineLocks.TryTakeAsync(session, resource, token).ConfigureAwait(false))
        {
            if (!announced)
            {
                Progress?.Invoke($"Waiting for another build that uses {name}");
                announced = true;
            }

            await (Delay ?? DefaultDelay)(CredentialPoll, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }

        state.CredentialLockTaken = true;

        // Markers of dead builds with this name, now that no other build can be making this credential.
        await SweepCredentialsAsync(session, state, name, takeLock: false, token).ConfigureAwait(false);

        if (await BuildCredentials.FindAsync(session, name, token).ConfigureAwait(false) is not null)
        {
            throw new SqlBranchException(ErrorCodes.CredentialConflict,
                $"The engine already has a credential named {name} that SqlBranch did not create, so the build will not replace it. " +
                "Drop it with DROP CREDENTIAL if nothing needs it, or use a backup in another folder.");
        }

        await WriteMarkerAsync(state, new CredentialMarker(name), token).ConfigureAwait(false);

        state.CredentialCreateSent = true;
        try
        {
            await session.ExecuteAsync(BuildCredentials.CreateSql(name, secrets.Identity, secrets.Secret), token).ConfigureAwait(false);
        }
        catch (EngineSqlException ex) when (!ex.OutcomeUnknown)
        {
            // A real server refusal: no credential of this build exists, whatever else has that name.
            // A timeout, a cancel or a lost connection falls through to the next clause with CredentialCreateSent still set.
            state.CredentialCreateSent = false;
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"Creating the storage credential {name} failed: {_redactor.Redact(ex.Message)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A transport failure: the outcome is unknown, so cleanup may read the credential back.
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"Creating the storage credential {name} failed: {_redactor.Redact(ex.Message)}");
        }

        var found = await BuildCredentials.FindAsync(session, name, token).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed, $"The storage credential {name} was created but the engine does not list it.");
        state.CredentialId = found.Id;
        state.CredentialDate = found.CreateDate;
        await WriteMarkerAsync(state, new CredentialMarker(name, found.Id, found.CreateDate), token).ConfigureAwait(false);
    }

    private static Task DefaultDelay(TimeSpan time, CancellationToken token) => Task.Delay(time, token);

    private async Task WriteMarkerAsync(State state, CredentialMarker marker, CancellationToken token)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(marker.ToJson()));
        var path = $"{state.BuildDirectory}/{CredentialMarker.FileName}";
        await ShellAsync($"printf %s {Quoting.Shell(encoded)} | base64 -d > {Quoting.Shell(path)}", "recording the storage credential", token).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the credential this build made, matched by name, id and creation time, then deletes the marker and
    /// gives back the lock. A credential that is gone, or that is no longer the one this build made, needs no drop.
    /// </summary>
    private async Task DropCredentialAsync(IEngineSession session, State state, CancellationToken token)
    {
        if (state.CredentialId is null || state.CredentialDropped)
        {
            return;
        }

        var name = state.Request.Url!.CredentialName;
        var marker = new CredentialMarker(name, state.CredentialId, state.CredentialDate);
        try
        {
            if (await BuildCredentials.StillExistsAsync(session, marker, token).ConfigureAwait(false))
            {
                await session.ExecuteAsync(BuildCredentials.DropSql(name), token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Dropping the storage credential {name} failed: {_redactor.Redact(ex.Message)}. " +
                "If it is still in the engine, the next 'sqlbranch image build' removes it, and 'sqlbranch status' lists it.");
        }

        state.CredentialDropped = true;
        await RemoveMarkerAsync(state.Id, token).ConfigureAwait(false);
        await ReleaseCredentialLockAsync(session, state, token).ConfigureAwait(false);
    }

    private static async Task ReleaseCredentialLockAsync(IEngineSession session, State state, CancellationToken token)
    {
        if (state.CredentialLockTaken)
        {
            state.CredentialLockTaken = false;
            await EngineLocks.ReleaseAsync(session, EngineLocks.Credential(state.Request.Url!.CredentialName), token).ConfigureAwait(false);
        }
    }

    /// <summary>The marker goes first; the folder goes with it when nothing else is in it.</summary>
    private async Task RemoveMarkerAsync(string buildId, CancellationToken token)
    {
        var folder = Quoting.Shell(ImagePaths.Build(buildId));
        await ShellAsync($"rm -f {Quoting.Shell($"{ImagePaths.Build(buildId)}/{CredentialMarker.FileName}")}; rmdir {folder} 2>/dev/null || true", "removing the credential record", token).ConfigureAwait(false);
    }

    /// <summary>The credential part of the failure cleanup. A failed drop is added to <paramref name="problems"/> and keeps the marker.</summary>
    private async Task DropCredentialInCleanupAsync(IEngineSession session, State state, List<string> problems)
    {
        if (state.CredentialDropped || state.Request.Url is not { } url)
        {
            return;
        }

        if (!state.CredentialCreateSent)
        {
            // Nothing was created, so the lock (if the build got it) is all there is to give back.
            await TryReleaseAsync(session, state).ConfigureAwait(false);
            return;
        }

        try
        {
            if (state.CredentialId is null)
            {
                // CREATE returned (or its outcome is unknown) and the read-back did not happen. The name was absent when
                // this build took its lock and the lock is still held, so a credential of that name now is this build's.
                // An engine error from CREATE never gets here: it clears CredentialCreateSent.
                if (await BuildCredentials.FindAsync(session, url.CredentialName, CancellationToken.None).ConfigureAwait(false) is { } found)
                {
                    state.CredentialId = found.Id;
                    state.CredentialDate = found.CreateDate;
                }
                else
                {
                    return;
                }
            }

            await DropCredentialAsync(session, state, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failed drop is reported with the build's result and keeps the marker; it must not hide the build's own failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var fix = state.CredentialId is null
                ? $"it may still exist; drop it by hand with DROP CREDENTIAL {Quoting.Identifier(url.CredentialName)} if nothing needs it"
                : "the next 'sqlbranch image build' removes it";
            problems.Add($"the storage credential {url.CredentialName} ({_redactor.Redact(ex.Message)}); {fix}");
        }
    }

    private static async Task TryReleaseAsync(IEngineSession session, State state)
    {
        try
        {
            await ReleaseCredentialLockAsync(session, state, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The session ends with the build and gives the lock back then.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Intentionally empty.
        }
    }

    /// <summary>
    /// Drops the credentials that earlier builds recorded and left (design D4, step 1). <paramref name="onlyName"/> limits it to
    /// the markers of one name, for the sweep under the build's own credential lock.
    /// </summary>
    private async Task SweepCredentialsAsync(IEngineSession session, State current, string? onlyName, bool takeLock, CancellationToken token)
    {
        IReadOnlyList<(string BuildId, CredentialMarker Marker)> markers;
        try
        {
            markers = BuildCredentials.ParseMarkers(await ShellAsync(ListMarkersScript, "listing credential records", token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            current.Warnings.Add($"Could not look for leftover credentials: {_redactor.Redact(ex.Message)}");
            return;
        }

        foreach (var (buildId, marker) in markers)
        {
            if (buildId == current.Id || (onlyName is not null && !string.Equals(marker.Name, onlyName, StringComparison.Ordinal)) ||
                !buildId.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                continue;
            }

            var took = false;
            try
            {
                // A build that still runs owns its marker.
                if (await EngineLocks.IsHeldAsync(session, EngineLocks.Build(buildId), token).ConfigureAwait(false))
                {
                    continue;
                }

                if (takeLock)
                {
                    // Tried without waiting: a held lock means a running build uses this name.
                    if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Credential(marker.Name), token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    took = true;
                }

                // An intent marker proves nothing about a credential of that name, so it is only deleted.
                if (marker.IsFull && await BuildCredentials.StillExistsAsync(session, marker, token).ConfigureAwait(false))
                {
                    await session.ExecuteAsync(BuildCredentials.DropSql(marker.Name), token).ConfigureAwait(false);
                }

                await RemoveMarkerAsync(buildId, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                current.Warnings.Add($"Could not remove the leftover credential {marker.Name} of an earlier build: {_redactor.Redact(ex.Message)}");
            }
            finally
            {
                if (took)
                {
                    try
                    {
                        await EngineLocks.ReleaseAsync(session, EngineLocks.Credential(marker.Name), CancellationToken.None).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // The session ends with the build and gives the lock back then.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                        // Intentionally empty.
                    }
                }
            }
        }
    }
}
