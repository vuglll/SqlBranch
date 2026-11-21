using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Cli.Commands;

/// <summary><c>sqlbranch init</c>.</summary>
internal static class InitCommand
{
    public static void Register(CliApp app, Func<CliServices> getServices, SecretRedactor redactor)
    {
        var command = new Command("init", "Create the pool and the engine, or check that they are running. Safe to repeat.");
        app.AddCommand(app.Root, command, new CommandMetadata
        {
            Purpose = "Initialize the pool and SQL Server engine.",
            WhenToUse = "Set up a development engine before creating images.",
            Example = "sqlbranch init",
            Destroys = "nothing",
        }, parse =>
            Run(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName));
    }

    private static CommandResult Run(CliServices services, SecretRedactor redactor, string? configFile)
    {
        // The password is checked and registered before settings, files or containers are touched.
        PasswordPolicy.CheckGiven(services.GivenPassword);
        redactor.Register(services.GivenPassword);
        var settings = services.LoadSettings(configFile);

        var initializer = new EngineInitializer(services.Runtime, services.Connector, redactor, services.Paths, services.Timings, engineName: services.EngineName, poolVolume: services.PoolVolume);
        var outcome = initializer.InitializeAsync(settings, services.GivenPassword, services.DefaultPoolDirectory, CancellationToken.None)
            .GetAwaiter().GetResult();
        return Describe(outcome);
    }

    internal static CommandResult Describe(InitOutcome outcome)
    {
        var mode = CloneModeNames.Of(outcome.Mode);
        var port = outcome.Port.ToString(CultureInfo.InvariantCulture);
        var text = new List<string>
        {
            outcome.Repeated ? "SqlBranch is already initialized. The engine is running." : "SqlBranch is ready.",
            $"Engine: SQL Server {outcome.Version}, {outcome.Edition}",
            $"Engine container: {outcome.Container} on 127.0.0.1:{port}",
            $"Pool: {outcome.Location}",
            $"Pool backend: {outcome.Backend}",
            $"Clone mode: {mode}",
        };

        if (outcome.Created.Count > 0)
        {
            text.Add("Created:");
            text.AddRange(outcome.Created.Select(line => "  " + line));
        }

        if (outcome.Mode == CloneMode.Copy)
        {
            text.Add("Branch creation will copy every file in full.");
        }

        var notice = $"Edition: {outcome.Edition}. This engine is for development and test use only. Never use it for production.";
        text.Add(notice);

        // Warnings about the host go to standard error and never stop init.
        var warnings = new List<string>(outcome.Notes ?? []);
        if (DoctorRunner.ArchitectureWarning(outcome.HostArchitecture) is { } architecture)
        {
            warnings.Add(architecture);
        }

        if (outcome.Mode == CloneMode.Copy)
        {
            warnings.Add(DoctorRunner.CopyModeWarning);
        }

        var payload = new JsonObject
        {
            ["repeated"] = outcome.Repeated,
            ["engine"] = new JsonObject
            {
                ["container"] = outcome.Container,
                ["version"] = outcome.Version,
                ["edition"] = outcome.Edition,
                ["host"] = "127.0.0.1",
                ["port"] = outcome.Port,
            },
            ["pool"] = new JsonObject
            {
                ["location"] = outcome.Location.ToString(),
                ["backend"] = outcome.Backend,
                ["cloneMode"] = mode,
            },
            ["created"] = new JsonArray([.. outcome.Created.Select(c => (JsonNode?)c)]),
            ["notice"] = notice,
        };

        return CommandResult.Success("init", payload, text, [$"127.0.0.1,{port}"], warnings);
    }
}
