using System.Globalization;
using System.Text.Json.Nodes;
using System.CommandLine;
using SqlBranch.Core;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Cli.Commands;

/// <summary><c>sqlbranch engine start</c> and <c>sqlbranch engine stop</c>.</summary>
internal static class EngineCommands
{
    public static void Register(CliApp app, Func<CliServices> getServices, SecretRedactor redactor)
    {
        var engine = new Command("engine", "Start or stop the engine container.");
        app.Root.Subcommands.Add(engine);

        app.AddCommand(engine, new Command("start", "Start the engine and wait until it accepts connections."), new CommandMetadata
        {
            Purpose = "Start the engine and wait for SQL Server.",
            WhenToUse = "Resume a stopped engine before opening a branch.",
            Example = "sqlbranch engine start",
            Destroys = "nothing",
        }, parse => Start(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName));
        app.AddCommand(engine, new Command("stop", "Stop the engine. Safe to repeat."), new CommandMetadata
        {
            Purpose = "Stop the SQL Server engine.",
            WhenToUse = "Shut down an idle development engine after coordinating with its users.",
            Example = "sqlbranch engine stop",
            Destroys = "Active connections are closed when the engine stops.",
        }, parse => Stop(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName));
    }

    private static CommandResult Start(CliServices services, SecretRedactor redactor, string? configFile)
    {
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var engine = context.Pool.Engine ?? throw MissingEngine(context);

        var wasRunning = engine.Running;
        var access = context.Starter.StartAsync(engine, context.Pool, context.Settings.Port, null, null, CancellationToken.None).GetAwaiter().GetResult();
        // The engine was created with the edition its label names; that, not today's setting, is what it runs.
        var created = SettingNames.TryParseEdition(engine.Labels.GetValueOrDefault(ContainerLabels.Edition) ?? string.Empty, out var labeled)
            ? labeled
            : context.Settings.EngineEdition;
        var (version, edition) = EngineStarter.ReadVersionAsync(services.Connector, access.Endpoint, access.Password, created, engine.Image, CancellationToken.None).GetAwaiter().GetResult();

        var port = access.Endpoint.Port.ToString(CultureInfo.InvariantCulture);
        var payload = new JsonObject
        {
            ["engine"] = new JsonObject
            {
                ["container"] = services.EngineName,
                ["state"] = "running",
                ["alreadyRunning"] = wasRunning,
                ["version"] = version,
                ["edition"] = edition,
                ["host"] = access.Endpoint.Host,
                ["port"] = access.Endpoint.Port,
            },
            ["pool"] = new JsonObject
            {
                ["location"] = context.Pool.Location.ToString(),
                ["backend"] = context.Pool.Backend,
            },
        };

        return CommandResult.Success("engine start", payload,
        [
            wasRunning ? "The engine is already running." : "The engine is running.",
            $"Engine: SQL Server {version}, {edition}",
            $"Engine container: {services.EngineName} on 127.0.0.1:{port}",
            $"Pool: {context.Pool.Location}",
        ], [$"127.0.0.1,{port}"]);
    }

    private static CommandResult Stop(CliServices services, SecretRedactor redactor, string? configFile)
    {
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var running = context.Pool.Engine is { Running: true };
        if (running)
        {
            services.Runtime.StopAsync(services.EngineName, CancellationToken.None).GetAwaiter().GetResult();
        }

        var payload = new JsonObject
        {
            ["engine"] = new JsonObject { ["container"] = services.EngineName, ["state"] = "stopped", ["wasRunning"] = running },
        };
        return CommandResult.Success("engine stop", payload,
            [running ? "The engine is stopped." : "The engine is already stopped."]);
    }

    /// <summary>The pool exists but its engine container does not: only <c>init</c> can create it.</summary>
    private static SqlBranchException MissingEngine(CommandContext context) =>
        new(ErrorCodes.NotInitialized,
            $"The pool {context.Pool.Location} exists but the engine container does not. Run 'sqlbranch init' to create the engine again.");
}
