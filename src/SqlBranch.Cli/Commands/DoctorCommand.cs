using System.CommandLine;
using System.Text.Json.Nodes;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Containers;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Cli.Commands;

/// <summary><c>sqlbranch doctor</c>. Works before <c>init</c>.</summary>
internal static class DoctorCommand
{
    public static void Register(CliApp app, Func<CliServices> getServices, SecretRedactor redactor)
    {
        var command = new Command("doctor", "Check Docker, the engine image, the pool, the port, free space, the host and optional SqlPackage. Works before init.");
        var sqlPackage = new Option<string?>("--sqlpackage") { Description = "Path to SqlPackage. Defaults to searching PATH." };
        command.Options.Add(sqlPackage);
        app.AddCommand(app.Root, command, new CommandMetadata
        {
            Purpose = "Check Docker and the configured engine prerequisites.",
            WhenToUse = "Diagnose setup problems before or after init.",
            Example = "sqlbranch doctor",
            Destroys = "nothing",
        }, parse => Run(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName, parse.GetValue(sqlPackage)));
    }

    private static CommandResult Run(CliServices services, SecretRedactor redactor, string? configFile, string? sqlPackagePath)
    {
        var settings = services.LoadSettings(configFile);
        var runner = new DoctorRunner(services.Runtime, services.Paths, services.DirectoryExistsCheck, services.PortIsFreeCheck, services.EngineName, services.PoolVolume);
        var report = runner.RunAsync(settings, services.DefaultPoolDirectory, CancellationToken.None).GetAwaiter().GetResult();
        var tool = new SqlPackageTool(services.SqlPackageProcess ?? new SystemProcessRunner(inheritEnvironment: false));
        var installation = tool.FindAsync(sqlPackagePath, services.Environment,
            services.WorkingDirectory, CancellationToken.None).GetAwaiter().GetResult();
        var (status, detail) = (installation, sqlPackagePath) switch
        {
            (not null, _) => (CheckStatus.Pass, $"{installation.Path} (version {installation.Version})"),
            (null, null) => (CheckStatus.Optional, $"Not installed. Needed only for --from-bacpac. {SqlPackageTool.InstallGuidance}"),
            (null, _) => (CheckStatus.Fail, $"Could not read a version from '{sqlPackagePath}'. {SqlPackageTool.InstallGuidance}"),
        };
        var check = new DoctorCheck("sqlpackage", "SqlPackage", status, redactor.Redact(detail));
        return Describe(new DoctorReport([.. report.Checks, check]));
    }

    internal static CommandResult Describe(DoctorReport report)
    {
        var text = report.Checks.Select(c => $"[{Word(c.Status)}] {c.Title}: {c.Detail}").ToList();
        var payload = new JsonObject
        {
            ["checks"] = new JsonArray([.. report.Checks.Select(c => (JsonNode?)new JsonObject
            {
                ["id"] = c.Id,
                ["title"] = c.Title,
                ["status"] = Word(c.Status),
                ["detail"] = c.Detail,
            })]),
        };

        if (!report.AnyFailed)
        {
            var warnings = report.Checks.Count(c => c.Status == CheckStatus.Warn);
            text.Add(warnings > 0 ? $"No check failed; {warnings} warning{(warnings == 1 ? string.Empty : "s")}."
                : report.Checks.Any(c => c.Status == CheckStatus.Optional) ? "All required checks passed."
                : "All checks passed.");
            return CommandResult.Success("doctor", payload, text);
        }

        var failed = report.Checks.Where(c => c.Status == CheckStatus.Fail).ToList();
        var code = failed.Any(c => c.Id == "sqlpackage") ? ErrorCodes.SqlPackageMissing
            : failed.Any(c => c.Id == "docker") ? ErrorCodes.DockerUnavailable
            : ErrorCodes.CheckFailed;
        return CommandResult.Failure("doctor", code,
            "doctor: " + string.Join(" ", failed.Select(c => $"{c.Title}: {c.Detail}")), payload, textLines: text);
    }

    private static string Word(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "pass",
        CheckStatus.Warn => "warn",
        CheckStatus.Optional => "optional",
        _ => "fail",
    };
}
