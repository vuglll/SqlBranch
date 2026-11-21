using System.CommandLine;
using System.Text.Json.Nodes;
using SqlBranch.Core;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Cli.Commands;

/// <summary><c>sqlbranch teardown --force</c>: removes everything SqlBranch created.</summary>
internal static class TeardownCommand
{
    public static void Register(CliApp app, Func<CliServices> getServices, SecretRedactor redactor)
    {
        var force = new Option<bool>("--force")
        {
            Description = "Confirm that the engine, every image and branch, and the pool are to be removed.",
        };
        var command = new Command("teardown", "Remove the engine, every image and branch, every other database on the engine and the pool SqlBranch created. Needs --force.");
        command.Options.Add(force);
        app.AddCommand(app.Root, command, new CommandMetadata
        {
            Purpose = "Remove the SqlBranch installation and its data.",
            WhenToUse = "Decommission the entire development engine with explicit authorization.",
            Example = "sqlbranch teardown --force",
            Destroys = "The engine, every database on it, all images and branches, and the SqlBranch-managed pool data.",
        }, parse => Run(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName, parse.GetValue(force)),
            ["--force"]);
    }

    private static CommandResult Run(CliServices services, SecretRedactor redactor, string? configFile, bool force)
    {
        // Nothing is looked up, started or removed without --force.
        if (!force)
        {
            throw new SqlBranchException(ErrorCodes.UsageError,
                "teardown removes the engine, every image and branch, every database on the engine and the pool. " +
                "Run 'sqlbranch teardown --force' to confirm. Nothing was removed.");
        }

        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var runner = new TeardownRunner(services.Runtime, services.Connector, redactor, services.Paths, services.Timings, services.EngineName, services.PoolVolume);
        var outcome = runner.RunAsync(context.Pool, context.Settings, CancellationToken.None).GetAwaiter().GetResult();
        return Describe(services, context.Pool, outcome);
    }

    private static CommandResult Describe(CliServices services, FoundPool pool, TeardownOutcome outcome)
    {
        var text = new List<string>();
        text.AddRange(outcome.Databases.Select(d => $"Removed database: {d}"));
        text.AddRange(outcome.DatabaseFiles.Select(f => $"Removed database file: {f}"));
        text.AddRange(outcome.ImportBackups.Select(b => $"Removed import backup: {b}"));
        if (outcome.EngineRemoved)
        {
            text.Add($"Removed engine container: {services.EngineName}");
        }

        if (outcome.FoldersRemoved && pool.Location.Kind == PoolKind.Directory)
        {
            text.Add($"Removed SqlBranch's folders and marker from {pool.Location}. The directory and every other file in it are kept.");
        }
        else if (outcome.FoldersRemoved)
        {
            text.Add($"Removed SqlBranch's folders and marker from {pool.Location}");
        }

        var paths = services.Paths;
        if (outcome.PoolFileRemoved)
        {
            text.Add($"Removed pool file: {paths.PoolFile} (mount point {paths.MountPoint})");
        }

        if (outcome.VolumeRemoved)
        {
            text.Add($"Removed pool volume: {services.PoolVolume}");
        }

        var payload = new JsonObject
        {
            ["removed"] = new JsonObject
            {
                ["databases"] = Array(outcome.Databases),
                ["databaseFiles"] = Array(outcome.DatabaseFiles),
                ["importBackups"] = Array(outcome.ImportBackups),
                ["engineContainer"] = outcome.EngineRemoved,
                ["poolFolders"] = outcome.FoldersRemoved,
                ["poolFile"] = outcome.PoolFileRemoved ? paths.PoolFile : null,
                ["volume"] = outcome.VolumeRemoved ? services.PoolVolume : null,
            },
            ["left"] = Array(outcome.Left),
        };

        if (outcome.Left.Count > 0)
        {
            return CommandResult.Failure("teardown", ErrorCodes.OperationFailed,
                $"Teardown did not finish. Left: {string.Join("; ", outcome.Left)}. Run 'sqlbranch teardown --force' again once the cause is gone.",
                payload, outcome.Notes, text);
        }

        if (text.Count == 0)
        {
            text.Add("Nothing was left to remove.");
        }

        text.Add("SqlBranch is removed.");
        return CommandResult.Success("teardown", payload, text, warnings: outcome.Notes);
    }

    private static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode?)i)]);
}
