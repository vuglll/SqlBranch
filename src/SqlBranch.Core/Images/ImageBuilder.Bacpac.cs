using SqlBranch.Core.Engine;
using SqlBranch.Core.LogicalSources;

namespace SqlBranch.Core.Images;

internal sealed record BacpacProvenance(string File, string Sha256, string? Url);

public sealed partial class ImageBuilder
{
    internal async Task<ImageManifest> SealBacpacAsync(IEngineSession session, EngineEndpoint endpoint, string engineImage,
        BacpacOwnership ownership, BacpacOperation operation, ImageBuildRequest request, BacpacProvenance provenance,
        BacpacBuildProgress progress, CancellationToken token)
    {
        var state = new State(request, operation.Id)
        {
            Endpoint = endpoint,
            EngineImage = engineImage,
            Ownership = ownership,
            Operation = operation,
            Bacpac = provenance,
        };

        if (request.RunsScripts)
        {
            using var phase = progress.Measure("postScripts");
            state.Triggers = await DatabaseTriggerCatalog.ReadAsync(session, state.Database, token).ConfigureAwait(false);
            DatabaseTriggerCatalog.RequirePostScriptsAllowed(state.Triggers,
                "Disable them in the source database and export a new BACPAC, or omit the post-build scripts.");
            await CheckGuestAsync(session, token).ConfigureAwait(false);
            state.OriginalRecoveryModel = await ReadRecoveryModelAsync(session, state, token).ConfigureAwait(false);
            var password = PasswordGenerator.Generate();
            _redactor.Register(password);
            await ownership.EnableScriptLoginAsync(operation, password, token).ConfigureAwait(false);
            state.LoginPassword = password;
            await RunScriptsAsync(state, token).ConfigureAwait(false);
        }

        using (progress.Measure("seal"))
        {
            (state.EngineVersion, _) = await EngineStarter.ReadVersionAsync(session, token).ConfigureAwait(false);
            if (!request.RunsScripts)
            {
                state.Triggers = await DatabaseTriggerCatalog.ReadAsync(session, state.Database, token).ConfigureAwait(false);
                state.InitialLogBytes = (await FilesAsync(session, state, token).ConfigureAwait(false))
                    .Where(f => f.Type == "LOG").Sum(f => f.Bytes);
            }
            await FinishImageAsync(session, state, token).ConfigureAwait(false);
        }

        return state.Manifest!;
    }
}
