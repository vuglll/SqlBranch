using System.CommandLine;
using System.Text.Json.Nodes;
using SqlBranch.Cli.Skills;
using SqlBranch.Core.Output;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Cli.Commands;

internal static class SkillCommand
{
    public static void Register(CliApp app, string workingDirectory, Action<string, string>? write = null, Action<string, string, string?>? install = null)
    {
        var skill = new Command("skill", "Install the SqlBranch coding-agent skill into a project.");
        app.Root.Subcommands.Add(skill);
        var export = new Command("export", "Write the generated skill and its command references.");
        var dir = new Option<string>("--dir") { Required = true, Description = "Destination skill directory." };
        var force = new Option<bool>("--force") { Description = "Replace existing generated files." };
        export.Options.Add(dir);
        export.Options.Add(force);
        app.AddCommand(skill, export, new CommandMetadata
        {
            Purpose = "Export the agent skill and command reference files.",
            WhenToUse = "Install the skill in a project or refresh its generated files after a CLI change.",
            Example = "sqlbranch skill export --dir .claude/skills/sqlbranch",
            Destroys = "With --force, replaces existing generated files. Removes stale export staging directories for this target.",
        }, parse =>
        {
            var files = SkillGenerator.Generate(app.Registered);
            var written = SkillExporter.Export(workingDirectory, parse.GetValue(dir)!, parse.GetValue(force), files, write, install);
            return CommandResult.Success("skill export", new JsonObject
            {
                ["written"] = new JsonArray([.. written.Select(p => (JsonNode?)p)]),
            }, written);
        }, ["--dir", ".claude/skills/sqlbranch"]);
    }
}
