using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using SqlBranch.Core;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Cli.Commands;

/// <summary><c>sqlbranch status</c>.</summary>
internal static class StatusCommand
{
    public static void Register(CliApp app, Func<CliServices> getServices, SecretRedactor redactor)
    {
        var command = new Command("status", "Show the engine, the pool and anything that does not match. Exits 1 when it lists a mismatch.");
        app.AddCommand(app.Root, command, new CommandMetadata
        {
            Purpose = "Report engine and pool state, including mismatches.",
            WhenToUse = "Check the engine before starting work or after cleanup.",
            Example = "sqlbranch status --format json",
            Destroys = "nothing",
        }, parse => Run(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName));
    }

    private static CommandResult Run(CliServices services, SecretRedactor redactor, string? configFile)
    {
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var pool = context.Pool;
        var engine = pool.Engine ?? throw new SqlBranchException(ErrorCodes.NotInitialized,
            $"The pool {pool.Location} exists but the engine container does not. Run 'sqlbranch init' to create the engine again.");

        if (!engine.Running)
        {
            return Stopped(services, pool);
        }

        var report = new StatusReporter(services.Runtime, services.Connector, redactor, services.EngineName, services.Clock)
            .ReportAsync(pool, context.Settings.Port, CancellationToken.None).GetAwaiter().GetResult();
        return Describe(report);
    }

    private static CommandResult Stopped(CliServices services, FoundPool pool)
    {
        var payload = new JsonObject
        {
            ["engine"] = new JsonObject { ["container"] = services.EngineName, ["state"] = "stopped" },
            ["pool"] = new JsonObject { ["location"] = pool.Location.ToString(), ["backend"] = pool.Backend },
        };
        return CommandResult.Failure("status", ErrorCodes.EngineStopped,
            $"The engine is stopped. The pool is {pool.Location}. Run 'sqlbranch engine start' to start it.",
            payload,
            textLines:
            [
                "Engine: stopped",
                $"Engine container: {services.EngineName}",
                $"Pool: {pool.Location}",
                $"Pool backend: {pool.Backend}",
            ]);
    }

    internal static CommandResult Describe(StatusReport report)
    {
        var mode = CloneModeNames.Of(report.Mode);
        var port = report.Port.ToString(CultureInfo.InvariantCulture);
        var space = report.Space;
        var text = new List<string>
        {
            "Engine: running",
            $"Engine version: SQL Server {report.Version}",
            $"Engine edition: {report.Edition}",
            $"Engine container: {report.Container} on 127.0.0.1:{port}",
            $"Pool: {report.Pool.Location}",
            $"Pool backend: {report.Pool.Backend}",
            $"Pool format: {report.PoolFormat.ToString(CultureInfo.InvariantCulture)}",
            $"Clone mode: {mode}",
        };

        if (report.Mode == CloneMode.Copy)
        {
            text.Add("Branch creation will copy every file in full.");
        }

        text.Add($"Images: {report.Images}");
        text.Add($"Branches: {report.Branches}");
        if (report.ExpiredBranches.Count > 0)
        {
            text.Add($"Expired branches: {string.Join(", ", report.ExpiredBranches)}. Run 'sqlbranch branch prune' to remove them.");
        }
        text.Add($"Pool space: {new ByteSize(Math.Max(space.Total, 1)).ToGigabytes()} total, {Gb(space.Used)} used, {Gb(space.Free)} free");

        foreach (var note in report.InProgress)
        {
            text.Add($"In progress: {note}");
        }

        var payload = new JsonObject
        {
            ["engine"] = new JsonObject
            {
                ["container"] = report.Container,
                ["state"] = "running",
                ["version"] = report.Version,
                ["edition"] = report.Edition,
                ["host"] = "127.0.0.1",
                ["port"] = report.Port,
            },
            ["pool"] = new JsonObject
            {
                ["location"] = report.Pool.Location.ToString(),
                ["backend"] = report.Pool.Backend,
                ["cloneMode"] = mode,
                ["totalBytes"] = space.Total,
                ["usedBytes"] = space.Used,
                ["freeBytes"] = space.Free,
            },
            ["poolFormat"] = report.PoolFormat,
            ["images"] = report.Images,
            ["branches"] = report.Branches,
            ["expiredBranches"] = new JsonArray([.. report.ExpiredBranches.Select(n => (JsonNode?)n)]),
            ["inProgress"] = new JsonArray([.. report.InProgress.Select(n => (JsonNode?)n)]),
            ["mismatches"] = new JsonArray([.. report.Mismatches.Select(m => (JsonNode?)new JsonObject
            {
                ["kind"] = m.Kind,
                ["name"] = m.Name,
                ["message"] = m.Message,
            })]),
        };

        if (report.Mismatches.Count == 0)
        {
            text.Add("Mismatches: none");
            return CommandResult.Success("status", payload, text);
        }

        text.Add($"Mismatches: {report.Mismatches.Count}");
        text.AddRange(report.Mismatches.Select(m => $"  {m.Name}: {m.Message}"));
        return CommandResult.Failure("status", ErrorCodes.Mismatch,
            $"status found {report.Mismatches.Count} mismatch{(report.Mismatches.Count == 1 ? string.Empty : "es")}: " +
            string.Join("; ", report.Mismatches.Select(m => $"{m.Name} ({m.Kind})")),
            payload, textLines: text);
    }

    private static string Gb(long bytes) => new ByteSize(Math.Max(bytes, 1)).ToGigabytes();
}
