using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using SqlBranch.Core;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Cli.Commands;

/// <summary><c>sqlbranch branch create</c> and, in later task groups, the other branch commands.</summary>
internal static class BranchCommands
{
    public static void Register(CliApp app, Func<CliServices> getServices, SecretRedactor redactor)
    {
        var branch = new Command("branch", "Create, list, reset and remove branches: writable databases cloned from an image.");
        app.Root.Subcommands.Add(branch);

        var name = new Argument<string>("name") { Description = "The branch name, which is also its database name: 1 to 63 lowercase letters, digits, hyphens and underscores." };
        var from = new Option<string?>("--from") { Description = "The image to clone the branch from. Without it, the defaultImage setting is used." };
        var create = new Command("create", "Create a branch: a writable database holding the image's data.");
        create.Arguments.Add(name);
        create.Options.Add(from);
        var copy = new Option<bool>("--copy") { Description = "Make full copies of the image's files even when the pool can share blocks, for benchmarks." };
        create.Options.Add(copy);
        var ephemeral = new Option<bool>("--ephemeral") { Description = "Mark the branch for removal after its time limit." };
        var ttl = new Option<string?>("--ttl") { Description = "Time limit in minutes, hours or days (default 1h)." };
        var label = new Option<string[]>("--label") { Description = "Set a key=value label. May be repeated.", Arity = ArgumentArity.OneOrMore };
        create.Options.Add(ephemeral);
        create.Options.Add(ttl);
        create.Options.Add(label);
        app.AddCommand(branch, create, new CommandMetadata
        {
            Purpose = "Create a writable database from a sealed image.",
            WhenToUse = "Allocate a private database before running data-dependent tests.",
            Example = "sqlbranch branch create agent-run42 --from shop --ephemeral --ttl 1h",
            Destroys = "nothing",
        }, parse => Create(
            getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName, parse.GetValue(name)!, parse.GetValue(from), parse.GetValue(copy),
            parse.GetValue(ephemeral), parse.GetValue(ttl), parse.GetValue(label) ?? []),
            ["pr-142", "--from", "shop"]);

        var list = new Command("list", "List the branches with their image, clone mode, creation time, state and the space their own changed data uses.");
        var filter = new Option<string[]>("--label") { Description = "Show branches matching every key=value label.", Arity = ArgumentArity.OneOrMore };
        list.Options.Add(filter);
        app.AddCommand(branch, list, new CommandMetadata
        {
            Purpose = "List branches, labels and expiry times.",
            WhenToUse = "Confirm which branches exist and verify cleanup.",
            Example = "sqlbranch branch list --format json",
            Destroys = "nothing",
        }, parse => List(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName,
            parse.GetValue(filter) ?? []));

        app.AddCommand(branch, new Command("prune", "Remove expired short-lived branches."), new CommandMetadata
        {
            Purpose = "Remove expired short-lived branches.",
            WhenToUse = "Reclaim expired branches when authorized to maintain the pool.",
            Example = "sqlbranch branch prune",
            Destroys = "Expired short-lived branches and their data.",
        }, parse => Prune(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName));

        var conn = new Command("conn", "Print the connection string of a branch. It contains the engine password: this is the only command whose output does.");
        var connName = new Argument<string>("name") { Description = "The branch name." };
        conn.Arguments.Add(connName);
        app.AddCommand(branch, conn, new CommandMetadata
        {
            Purpose = "Return the connection string for a branch.",
            WhenToUse = "Pass credentials directly to the test process using a secret environment variable.",
            Example = "sqlbranch branch conn agent-run42 --quiet",
            Destroys = "nothing",
        }, parse => Conn(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName, parse.GetValue(connName)!), ["pr-142"]);

        var reset = new Command("reset", "Return a branch to the exact state of its image, keeping its name and connection string.");
        var resetName = new Argument<string>("name") { Description = "The branch name." };
        reset.Arguments.Add(resetName);
        app.AddCommand(branch, reset, new CommandMetadata
        {
            Purpose = "Restore a branch from its image.",
            WhenToUse = "Repeat a test on a clean copy of the baseline.",
            Example = "sqlbranch branch reset agent-run42",
            Destroys = "All database changes in the named branch.",
        }, parse => Reset(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName, parse.GetValue(resetName)!), ["pr-142"]);

        var rm = new Command("rm", "Remove a branch: close its connections, drop its database and delete its files.");
        var rmName = new Argument<string>("name") { Description = "The branch name." };
        rm.Arguments.Add(rmName);
        app.AddCommand(branch, rm, new CommandMetadata
        {
            Purpose = "Remove a branch and close its connections.",
            WhenToUse = "Release the branch created for this run.",
            Example = "sqlbranch branch rm agent-run42",
            Destroys = "The named branch and its database files.",
        }, parse => Remove(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName, parse.GetValue(rmName)!), ["pr-142"]);
    }

    private static CommandResult Reset(CliServices services, SecretRedactor redactor, string? configFile, string name)
    {
        // The rules come first, so a bad name exits 2 even before init.
        NameRules.ValidateBranch(name);
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var reset = new BranchResetter(services.Runtime, services.Connector, redactor, services.EngineName)
            .ResetAsync(context.Pool, context.Settings.Port, name, CancellationToken.None, context.Settings.RequireThin).GetAwaiter().GetResult();
        return DescribeReset(reset);
    }

    internal static CommandResult DescribeReset(BranchCreateResult reset)
    {
        var mode = CloneModeNames.Of(reset.Mode);
        var text = new List<string>
        {
            $"Branch {reset.Name} is reset to the image {reset.Image}.",
            $"Clone mode: {mode}",
            $"Server: {reset.Server}",
            $"Database: {reset.Database}",
            "The connection string is unchanged.",
        };
        if (reset.ImageVersion is not null)
        {
            text.Insert(1, $"Image version: {reset.ImageVersion}");
        }

        if (reset.Mode == CloneMode.Copy)
        {
            text.Insert(2, "The branch's files are full copies of the image's files.");
        }

        var payload = new JsonObject
        {
            ["branch"] = new JsonObject
            {
                ["name"] = reset.Name,
                ["image"] = reset.Image,
                ["imageVersion"] = reset.ImageVersion,
                ["cloneMode"] = mode,
                ["server"] = reset.Server,
                ["database"] = reset.Database,
                ["user"] = reset.User,
            },
        };
        return CommandResult.Success("branch reset", payload, text, [reset.Name]);
    }

    private static CommandResult Prune(CliServices services, SecretRedactor redactor, string? configFile)
    {
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var result = new BranchPruner(services.Runtime, services.Connector, redactor, services.EngineName, services.Clock)
            .PruneAsync(context.Pool, context.Settings.Port, CancellationToken.None).GetAwaiter().GetResult();
        var payload = new JsonObject
        {
            ["removed"] = new JsonArray([.. result.Removed.Select(n => (JsonNode?)n)]),
            ["skipped"] = new JsonArray([.. result.Skipped.Select(n => (JsonNode?)n)]),
            ["failed"] = new JsonArray([.. result.Failed.Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name, ["path"] = f.Path, ["message"] = f.Message,
            })]),
        };
        var lines = new List<string> { result.Removed.Count == 0 ? "No branches removed." : $"Removed: {string.Join(", ", result.Removed)}" };
        if (result.Skipped.Count > 0)
        {
            lines.Add($"Skipped: {string.Join(", ", result.Skipped)}");
        }

        lines.AddRange(result.Failed.Select(f => $"{f.Path}: {f.Message}"));
        return result.Failed.Count == 0
            ? CommandResult.Success("branch prune", payload, lines, warnings: result.Warnings)
            : CommandResult.Failure("branch prune", ErrorCodes.OperationFailed, "One or more expired branches could not be removed.", payload, result.Warnings, lines);
    }

    private static CommandResult Remove(CliServices services, SecretRedactor redactor, string? configFile, string name)
    {
        // The rules come first, so a bad name exits 2 even before init.
        NameRules.ValidateBranch(name);
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var removal = new BranchRemover(services.Runtime, services.Connector, redactor, services.EngineName)
            .RemoveAsync(context.Pool, context.Settings.Port, name, CancellationToken.None).GetAwaiter().GetResult();
        return DescribeRemoval(removal);
    }

    internal static CommandResult DescribeRemoval(BranchRemoval removal)
    {
        var payload = new JsonObject
        {
            ["branch"] = new JsonObject
            {
                ["name"] = removal.Name,
                ["databaseDropped"] = removal.DatabaseDropped,
            },
        };
        return CommandResult.Success("branch rm", payload, [$"Branch {removal.Name} is removed."], [removal.Name], removal.Warnings);
    }

    private static CommandResult Conn(CliServices services, SecretRedactor redactor, string? configFile, string name)
    {
        // The rules come first, so a bad name exits 2 even before init.
        NameRules.ValidateBranch(name);
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var connection = new BranchConnector(services.Runtime, services.Connector, redactor, services.EngineName)
            .ConnectionAsync(context.Pool, context.Settings.Port, name, CancellationToken.None).GetAwaiter().GetResult();
        return DescribeConnection(connection);
    }

    /// <summary>The one result that carries the engine password. Text, quiet and JSON all print the string as it is: the renderer redacts only errors and warnings.</summary>
    internal static CommandResult DescribeConnection(BranchConnection connection)
    {
        var payload = new JsonObject
        {
            ["branch"] = new JsonObject
            {
                ["name"] = connection.Name,
                ["server"] = connection.Server,
                ["database"] = connection.Database,
                ["user"] = connection.User,
            },
            ["connectionString"] = connection.ConnectionString,
        };
        return CommandResult.Success("branch conn", payload, [connection.ConnectionString], [connection.ConnectionString], connection.Warnings ?? []);
    }

    private static CommandResult Create(CliServices services, SecretRedactor redactor, string? configFile, string name, string? from, bool copy,
        bool ephemeral, string? ttl, string[] labelArguments)
    {
        // The rules come first, so a bad name exits 2 even before init and before any SQL runs or file is touched.
        NameRules.ValidateBranch(name);
        if (!ephemeral && ttl is not null)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, "--ttl requires --ephemeral.");
        }

        TimeSpan? limit = ephemeral ? TimeSpan.FromHours(1) : null;
        if (ttl is not null)
        {
            limit = Duration.Parse(ttl) ?? throw new SqlBranchException(ErrorCodes.UsageError,
                "--ttl must be a positive whole number followed by m, h or d, up to 365d.");
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in ParseLabels(labelArguments, allowReserved: false))
        {
            labels[key] = value;
        }

        var image = from ?? services.LoadSettings(configFile).DefaultImage
            ?? throw new SqlBranchException(ErrorCodes.UsageError,
                "No image is named. Pass --from <image>, or set defaultImage in .sqlbranch.yml (or SQLBRANCH_DEFAULT_IMAGE) to use one by default.");
        NameRules.ValidateImage(image);
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var created = new BranchCreator(services.Runtime, services.Connector, redactor, services.EngineName, services.Clock)
            .CreateAsync(context.Pool, context.Settings.Port, name, image, CancellationToken.None, copy, context.Settings.RequireThin, limit, labels).GetAwaiter().GetResult();
        return Describe(created);
    }

    private static List<KeyValuePair<string, string>> ParseLabels(string[] arguments, bool allowReserved)
    {
        var labels = new List<KeyValuePair<string, string>>();
        foreach (var argument in arguments)
        {
            var separator = argument.IndexOf('=');
            if (separator < 1)
            {
                throw new SqlBranchException(ErrorCodes.UsageError, "--label requires key=value.");
            }

            var key = argument[..separator];
            var value = argument[(separator + 1)..];
            if (BranchLabelRules.Problem(key, value, allowReserved) is { } problem)
            {
                throw new SqlBranchException(ErrorCodes.UsageError, problem);
            }

            labels.Add(new(key, value));
        }

        return labels;
    }

    private static CommandResult List(CliServices services, SecretRedactor redactor, string? configFile, string[] labelArguments)
    {
        var filters = ParseLabels(labelArguments, allowReserved: true);
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var listing = new BranchLister(services.Runtime, services.Connector, redactor, services.EngineName, services.Clock)
            .ListAsync(context.Pool, context.Settings.Port, CancellationToken.None, filters).GetAwaiter().GetResult();
        return DescribeList(listing, filters.Count > 0);
    }

    internal static CommandResult DescribeList(BranchListing listing, bool filtered = false)
    {
        var text = new List<string>();
        if (listing.Branches.Count == 0)
        {
            text.Add(filtered ? "No branches match the label filter." : "No branches. Create one with 'sqlbranch branch create <name> --from <image>'.");
        }
        else
        {
            var rows = listing.Branches.Select(b => new[]
            {
                b.Name,
                b.Image is null ? "-" : b.ImageRemoved ? $"{b.ImageVersion ?? b.Image} (removed)" : b.ImageVersion ?? b.Image,
                b.CloneMode,
                b.CreatedAt is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "-",
                b.State,
                b.OwnBytes is { } own ? new ByteSize(own).ToBinary() : "-",
                b.Ephemeral ? "yes" : "no",
                b.ExpiresAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "-",
                b.Expired ? "yes" : "no",
                string.Join(", ", b.Labels.Select(pair => $"{pair.Key}={pair.Value}")),
            }).ToList();
            string[] header = ["NAME", "IMAGE", "CLONE MODE", "CREATED", "STATE", "OWN DATA", "EPHEMERAL", "EXPIRES AT", "EXPIRED", "LABELS"];
            var widths = header.Select((h, c) => Math.Max(h.Length, rows.Max(r => r[c].Length))).ToArray();
            string Line(string[] cells) => string.Join("  ", cells.Select((cell, c) => cell.PadRight(widths[c]))).TrimEnd();
            text.Add(Line(header));
            text.AddRange(rows.Select(Line));
        }

        var payload = new JsonObject
        {
            ["branches"] = new JsonArray([.. listing.Branches.Select(b => (JsonNode?)new JsonObject
            {
                ["name"] = b.Name,
                ["image"] = b.Image,
                ["imageVersion"] = b.ImageVersion,
                ["imageRemoved"] = b.ImageRemoved,
                ["cloneMode"] = b.CloneMode,
                ["createdAt"] = b.CreatedAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["state"] = b.State,
                ["ownBytes"] = b.OwnBytes,
                ["ephemeral"] = b.Ephemeral,
                ["expiresAt"] = b.ExpiresAt?.UtcDateTime,
                ["expired"] = b.Expired,
                ["labels"] = System.Text.Json.JsonSerializer.SerializeToNode(b.Labels),
            })]),
        };
        return CommandResult.Success("branch list", payload, text, [.. listing.Branches.Select(b => b.Name)], listing.Warnings);
    }

    internal static CommandResult Describe(BranchCreateResult created)
    {
        var mode = CloneModeNames.Of(created.Mode);
        var text = new List<string>
        {
            $"Branch {created.Name} is created from the image {created.Image}.",
            $"Clone mode: {mode}",
            $"Server: {created.Server}",
            $"Database: {created.Database}",
            $"User: {created.User}",
            $"Run 'sqlbranch branch conn {created.Name}' for the connection string.",
        };
        if (created.ImageVersion is not null)
        {
            text.Insert(1, $"Image version: {created.ImageVersion}");
        }

        if (created.Mode == CloneMode.Copy)
        {
            text.Insert(2, "The branch's files are full copies of the image's files.");
        }

        var payload = new JsonObject
        {
            ["branch"] = new JsonObject
            {
                ["name"] = created.Name,
                ["ephemeral"] = created.ExpiresAt.HasValue,
                ["expiresAt"] = created.ExpiresAt?.UtcDateTime,
                ["labels"] = System.Text.Json.JsonSerializer.SerializeToNode(created.Labels),
                ["image"] = created.Image,
                ["imageVersion"] = created.ImageVersion,
                ["cloneMode"] = mode,
                ["server"] = created.Server,
                ["database"] = created.Database,
                ["user"] = created.User,
            },
        };
        return CommandResult.Success("branch create", payload, text, [created.Name]);
    }
}
