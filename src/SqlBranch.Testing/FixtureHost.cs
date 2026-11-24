using SqlBranch.Core;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Testing;

internal sealed class FixtureHost(IContainerRuntime runtime, IEngineConnector connector, SecretRedactor redactor,
    SettingsSources sources, ManagedPaths paths, string engineName = ContainerLabels.EngineName,
    string volume = ContainerLabels.PoolVolume, TimeProvider? clock = null)
{
    internal static FixtureHost Real()
    {
        var redactor = new SecretRedactor();
        return new(new DockerRuntime(new SystemProcessRunner(), redactor), new SqlClientEngineConnector(),
            redactor, SettingsSources.FromProcess(), ManagedPaths.Default);
    }

    internal async Task<(FoundPool Pool, Settings Settings)> DiscoverAsync(string? config, CancellationToken ct)
    {
        redactor.Register(sources.Environment.GetValueOrDefault(SettingKeys.SaPasswordVariable));
        var settings = SettingsLoader.Load(sources with { ConfigFile = config });
        var finder = new PoolFinder(runtime, paths, engineName: engineName, poolVolume: volume);
        var directory = Path.Combine(sources.HomeDirectory ?? sources.WorkingDirectory, ".sqlbranch", "pool");
        var pool = await finder.RequireAsync(settings, directory, ct).ConfigureAwait(false);
        ImageCatalog.RequireRunningEngine(pool);
        return (pool, settings);
    }

    internal async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            var (pool, settings) = await DiscoverAsync(null, ct).ConfigureAwait(false);
            await using var engine = await BranchEngine.OpenAsync(runtime, connector, redactor, engineName, pool, settings.Port, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    internal async Task<bool> PruneAsync(FoundPool pool, int port, CancellationToken ct)
    {
        try
        {
            var result = await new BranchPruner(runtime, connector, redactor, engineName, clock)
                .PruneAsync(pool, port, ct, owner: "testing").ConfigureAwait(false);
            foreach (var warning in result.Warnings)
            {
                WriteLog(warning);
            }

            foreach (var failure in result.Failed)
            {
                WriteLog($"Prune failed for {failure.Name}: {failure.Message}");
            }

            return result.Failed.Count == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteLog($"Could not prune expired fixtures: {ex.Message}");
            return false;
        }
    }

    internal async Task<(BranchCreateResult Result, string Connection)> CreateAsync(FoundPool pool, Settings settings,
        string image, string name, TimeSpan ttl, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        await using var engine = await BranchEngine.OpenAsync(runtime, connector, redactor, engineName, pool, settings.Port, ct).ConfigureAwait(false);
        var connection = BranchConnector.BuildConnectionString(engine.Server, name, engine.Password);
        var result = await new BranchCreator(runtime, connector, redactor, engineName, clock)
            .CreateAsync(engine, name, image, copy: false, settings.RequireThin, ttl, labels, ct).ConfigureAwait(false);
        return (result, connection);
    }

    internal Task ResetAsync(FoundPool pool, Settings settings, string name, CancellationToken ct) =>
        new BranchResetter(runtime, connector, redactor, engineName).ResetAsync(pool, settings.Port, name, ct, settings.RequireThin);

    internal Task RemoveAsync(FoundPool pool, Settings settings, string name) =>
        new BranchRemover(runtime, connector, redactor, engineName).RemoveAsync(pool, settings.Port, name, CancellationToken.None);

    internal void WriteLog(string message)
    {
        try { SqlBranchEnvironment.Log(redactor.Redact(message)); }
        catch (Exception) { /* A user log sink must not prevent cleanup. */ }
    }

    /// <summary>
    /// Turns a failure into the exception a test sees. The image is given for a create only. A reset passes none, so
    /// <c>image_not_found</c> on a reset keeps the resetter's own text (create a new branch): rebuilding the image would not
    /// restore the branch's old base (design D5).
    /// </summary>
    internal Exception Failure(Exception ex, string? image = null)
    {
        var message = redactor.Redact(ex.Message);
        if (ex is SqlBranchException error)
        {
            var fix = error.Code.Code switch
            {
                "not_initialized" => "sqlbranch init",
                "engine_stopped" => "sqlbranch engine start",
                "image_not_found" when image is not null => $"sqlbranch image build {image}",
                _ => null,
            };
            if (fix is not null)
            {
                return new SqlBranchUnavailableException(message.Contains(fix, StringComparison.Ordinal) ? message : $"{message} Run '{fix}'.", fix);
            }
        }
        // Raw provider exceptions and their inner exceptions can include credentials.
        return new InvalidOperationException(message);
    }
}
