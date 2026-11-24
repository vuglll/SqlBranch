using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Nodes;
using SqlBranch.Core;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Cli.Commands;

/// <summary><c>sqlbranch pool upgrade --yes</c>.</summary>
internal static class PoolCommands
{
    private const string YesFlag = "--yes";

    public static void Register(CliApp app, Func<CliServices> getServices, SecretRedactor redactor)
    {
        var pool = new Command("pool", "Change the format of the pool.");
        app.Root.Subcommands.Add(pool);

        var yes = new Option<bool>(YesFlag) { Description = "Confirm the upgrade. It takes no value and cannot be undone." };
        var upgrade = new Command("upgrade", "Move the pool to format 2. This cannot be undone, and SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x packages can no longer use the pool. Needs --yes.");
        upgrade.Options.Add(yes);
        app.AddCommand(pool, upgrade, new CommandMetadata
        {
            Purpose = "Upgrade the pool to format 2 (image versions and branch bases). Run it only when the user asked for it.",
            WhenToUse = "Only when the user or maintainer explicitly asks for it, after stopping every SqlBranch 0.2.x tool and test run. Never run it on your own initiative.",
            Example = "sqlbranch pool upgrade --yes",
            Destroys = "The pool's compatibility with SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x packages. It cannot be undone.",
        }, parse =>
        {
            // Everything about the command line is checked before the first Docker command.
            CheckInput(parse);
            return Upgrade(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName, app.WriteProgress);
        }, [YesFlag]);
    }

    /// <summary>
    /// <c>--yes</c> must be there once and carry no value. The parser also accepts <c>--yes=true</c>, <c>--yes true</c> and
    /// <c>--yes --yes</c> for a boolean option, so the raw tokens are looked at (task 4.1).
    /// </summary>
    internal static void CheckInput(ParseResult parse)
    {
        var tokens = parse.Tokens;
        var flags = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != TokenType.Option || tokens[i].Value != YesFlag)
            {
                continue;
            }

            flags++;
            if (i + 1 < tokens.Count && tokens[i + 1].Type == TokenType.Argument)
            {
                throw new SqlBranchException(ErrorCodes.UsageError,
                    $"{YesFlag} takes no value: write 'sqlbranch pool upgrade --yes', not '{YesFlag} {tokens[i + 1].Value}'. Nothing was changed.");
            }
        }

        if (flags == 0)
        {
            throw new SqlBranchException(ErrorCodes.UsageError,
                "The pool upgrade cannot be undone, and SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x packages can no longer use the pool after it. " +
                "Run 'sqlbranch pool upgrade --yes' to confirm. Nothing was changed.");
        }

        if (flags > 1)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"{YesFlag} was given more than once: write it once. Nothing was changed.");
        }
    }

    private static CommandResult Upgrade(CliServices services, SecretRedactor redactor, string? configFile, Action<string> progress)
    {
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();

        // not_initialized, then engine_stopped: the upgrade reads and locks the pool through the running engine.
        ImageCatalog.RequireRunningEngine(context.Pool);

        var result = new PoolUpgrader(services.Runtime, services.Connector, redactor, services.EngineName, services.Clock)
            .UpgradeAsync(context.Pool, context.Settings.Port, progress, CancellationToken.None).GetAwaiter().GetResult();
        return Describe(result);
    }

    internal static CommandResult Describe(PoolUpgradeResult result)
    {
        var payload = new JsonObject
        {
            ["pool"] = new JsonObject { ["format"] = 2, ["previousFormat"] = result.PreviousFormat },
            ["images"] = new JsonArray([.. result.Images.Select(i => (JsonNode?)new JsonObject { ["name"] = i.Name, ["version"] = i.Version })]),
            ["branches"] = new JsonObject
            {
                ["pinned"] = Names(result.Branches.Pinned),
                ["unknownBase"] = Names(result.Branches.UnknownBase),
                ["removedBase"] = Names(result.Branches.RemovedBase),
            },
        };

        if (result.PreviousFormat >= 2)
        {
            return CommandResult.Success("pool upgrade", payload, ["The pool already uses format 2. Nothing was changed."]);
        }

        var text = new List<string> { "The pool now uses format 2 (it used format 1)." };
        text.AddRange(result.Images.Select(i => $"Image {i.Name}: version {i.Version}"));
        AddBranches(text, "pinned to the first version", result.Branches.Pinned);
        AddBranches(text, "with an unknown base (they cannot be reset)", result.Branches.UnknownBase);
        AddBranches(text, "whose image was already removed (they cannot be reset)", result.Branches.RemovedBase);
        return CommandResult.Success("pool upgrade", payload, text);
    }

    private static void AddBranches(List<string> text, string what, IReadOnlyList<string> names)
    {
        if (names.Count > 0)
        {
            text.Add($"Branches {what}: {string.Join(", ", names)}");
        }
    }

    private static JsonArray Names(IReadOnlyList<string> names) => new([.. names.Select(n => (JsonNode?)n)]);
}
