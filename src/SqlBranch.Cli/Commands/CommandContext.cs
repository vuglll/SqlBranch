using SqlBranch.Core.Configuration;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Cli.Commands;

/// <summary>
/// What every command except <c>init</c> and <c>doctor</c> starts with: the
/// settings, and the pool found by the lookup of design D2a. Commands for
/// images and branches call <see cref="RequireAsync"/> too, so they all exit 3
/// <c>not_initialized</c> before <c>init</c>.
/// </summary>
internal sealed class CommandContext
{
    private CommandContext(CliServices services, SecretRedactor redactor, Settings settings, FoundPool pool)
    {
        Services = services;
        Redactor = redactor;
        Settings = settings;
        Pool = pool;
    }

    public CliServices Services { get; }

    public SecretRedactor Redactor { get; }

    public Settings Settings { get; }

    /// <summary>The pool that exists. The engine may be missing, stopped or running.</summary>
    public FoundPool Pool { get; }

    public EngineStarter Starter => new(Services.Runtime, Services.Connector, Redactor, Services.Paths, Services.Timings, Services.EngineName);

    /// <summary>Loads the settings and finds the pool, or throws exit 3 <c>not_initialized</c>.</summary>
    public static async Task<CommandContext> RequireAsync(CliServices services, SecretRedactor redactor, string? configFile, CancellationToken cancellationToken)
    {
        var settings = services.LoadSettings(configFile);
        var finder = new PoolFinder(services.Runtime, services.Paths, services.DirectoryExistsCheck, services.EngineName, services.PoolVolume);
        var pool = await finder.RequireAsync(settings, services.DefaultPoolDirectory, cancellationToken).ConfigureAwait(false);
        return new CommandContext(services, redactor, settings, pool);
    }
}
