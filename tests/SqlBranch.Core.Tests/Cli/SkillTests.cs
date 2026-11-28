using System.CommandLine;
using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Cli.Commands;
using SqlBranch.Cli.Skills;
using SqlBranch.Core.Tests.Configuration;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

public sealed class SkillTests
{
    private readonly CliWorld _world = new();

    private CliApp App => CliApp.CreateDefault(_world.Redactor, _world.Services());

    private static IEnumerable<Command> Leaves(Command cmd) =>
        cmd.Subcommands.Count == 0 ? [cmd] : cmd.Subcommands.SelectMany(Leaves);

    [Fact]
    public void Every_command_declares_complete_guidance()
    {
        var app = App;
        Assert.Equal(Leaves(app.Root), app.Registered.Select(c => c.Command));
        foreach (var cmd in app.Registered)
        {
            var name = string.Join(' ', cmd.Path);
            var m = cmd.Metadata;
            foreach (var (field, value) in new[] { (nameof(m.Purpose), m.Purpose), (nameof(m.WhenToUse), m.WhenToUse),
                (nameof(m.Example), m.Example), (nameof(m.Destroys), m.Destroys) })
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{name} has empty metadata field {field}");
            }
            Assert.StartsWith($"sqlbranch {name}", m.Example, StringComparison.Ordinal);
            Assert.Empty(app.Root.Parse(m.Example.Split(' ').Skip(1).ToArray()).Errors);
        }
    }

    [Fact]
    public void Guide_is_deterministic_short_and_links_to_all_commands()
    {
        var app = App;
        for (var i = app.Registered.Count; i < 20; i++)
        {
            var name = FormattableString.Invariant($"test-{i}");
            app.AddCommand(app.Root, new Command(name), NoPromptTests.ProbeMetadata with { Example = $"sqlbranch {name}" },
                _ => SqlBranch.Core.Output.CommandResult.Success(name));
        }
        var commands = app.Registered;
        var files = SkillGenerator.Generate(commands);
        Assert.Equal(files.ToArray(), SkillGenerator.Generate(commands).ToArray());
        var guide = files["SKILL.md"];
        var lines = guide.Split('\n');
        Assert.True(lines.Length < 200);
        Assert.Equal(6, lines.Count(l => l.StartsWith("## ", StringComparison.Ordinal)));
        foreach (var heading in new[] { "When to use", "Setup check", "Getting a branch", "Working in a branch", "Cleaning up", "Guardrails" })
        {
            Assert.Contains("## " + heading + "\n", guide, StringComparison.Ordinal);
        }
        foreach (var rule in new[] { "only in branches you created", "Never run destructive SQL against a server other than the SqlBranch engine",
            "Reset or remove your branches", "Never weaken a test", "Never run `sqlbranch pool upgrade` unless the user asked for it" })
        {
            Assert.Contains(rule, guide, StringComparison.Ordinal);
        }
        foreach (var cmd in commands)
        {
            var path = "reference/" + (cmd.Path.Count == 1 ? "setup" : cmd.Path[0]) + ".md";
            Assert.Contains("(" + path + ")", guide, StringComparison.Ordinal);
            Assert.Contains("## sqlbranch " + string.Join(' ', cmd.Path) + "\n", files[path], StringComparison.Ordinal);
        }
        Assert.Equal("nothing", commands.Single(c => c.Path.SequenceEqual(["branch", "list"])).Metadata.Destroys);
        Assert.Contains("Expired short-lived branches and their data", files["reference/branch.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_guardrail_text_has_five_rules_and_the_fifth_names_pool_upgrade_and_says_it_cannot_be_undone()
    {
        var guide = SkillGenerator.Generate(App.Registered)["SKILL.md"];
        var guardrails = guide[guide.IndexOf("## Guardrails\n", StringComparison.Ordinal)..];
        guardrails = guardrails[..guardrails.IndexOf("**Command reference**", StringComparison.Ordinal)];

        var rules = guardrails.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToArray();

        Assert.Equal(5, rules.Length);
        Assert.Contains("`sqlbranch pool upgrade`", rules[4], StringComparison.Ordinal);
        Assert.Contains("cannot be undone", rules[4], StringComparison.Ordinal);
        Assert.Contains("0.2.x", rules[4], StringComparison.Ordinal);
    }

    [Fact]
    public void The_pool_upgrade_reference_entry_shows_the_command_and_says_it_destroys_compatibility_and_cannot_be_undone()
    {
        var files = SkillGenerator.Generate(App.Registered);

        var entry = files["reference/pool.md"];

        Assert.Contains("## sqlbranch pool upgrade\n", entry, StringComparison.Ordinal);
        Assert.Contains("sqlbranch pool upgrade --yes", entry, StringComparison.Ordinal);
        var destroys = entry[entry.IndexOf("**Destroys**", StringComparison.Ordinal)..];
        Assert.Contains("compatibility with SqlBranch 0.2.x tools", destroys, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", destroys, StringComparison.Ordinal);
        Assert.Contains("(reference/pool.md)", files["SKILL.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void Committed_skill_matches_generated_files()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlBranch.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var target = Path.Combine(dir.FullName, "skills", "sqlbranch");
        foreach (var (file, content) in SkillGenerator.Generate(App.Registered))
        {
            var path = Path.Combine(target, file);
            Assert.True(File.Exists(path) && File.ReadAllText(path).ReplaceLineEndings("\n") == content,
                $"{file} differs. Regenerate with: {SkillGenerator.Regenerate}");
        }
        // An obsolete reference is also drift. Ignore the user's unrelated notes.
        var expected = SkillGenerator.Generate(App.Registered).Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(target, "reference"), "*.md"))
        {
            var relative = "reference/" + Path.GetFileName(path);
            Assert.True(expected.Contains(relative), $"Obsolete {relative}. Remove it and run: {SkillGenerator.Regenerate}");
        }
    }

    [Fact]
    public void Documented_export_preserves_other_files_and_works_without_docker()
    {
        using var tree = new TempTree();
        var app = CliApp.CreateDefault(_world.Redactor, _world.Services() with { WorkingDirectory = tree.Root });
        const string relative = ".claude/skills/sqlbranch";
        var dir = Path.Combine(tree.Root, relative);
        Directory.CreateDirectory(dir + ".sqlbranch-tmp-1a2b3c4d");
        Directory.CreateDirectory(dir + ".sqlbranch-tmp-keep");
        Directory.CreateDirectory(dir + ".sqlbranch-tmp-ABCDEF12");
        var unrelated = Directory.CreateDirectory(Path.Combine(tree.Root, ".claude/skills/other"));
        var (exit, output) = Run(app, relative, false);
        Assert.Equal(0, exit);
        var files = SkillGenerator.Generate(app.Registered);
        Assert.Equal(files.Keys, output.GetProperty("written").EnumerateArray().Select(e => e.GetString()));
        Assert.False(Directory.Exists(dir + ".sqlbranch-tmp-1a2b3c4d"));
        Assert.True(Directory.Exists(dir + ".sqlbranch-tmp-keep"));
        Assert.True(Directory.Exists(dir + ".sqlbranch-tmp-ABCDEF12"));
        Assert.True(unrelated.Exists);
        var skill = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(skill, "old");
        var notes = Path.Combine(dir, "notes.md");
        File.WriteAllText(notes, "keep");
        var conflict = Run(app, relative, false);
        Assert.Equal(1, conflict.Exit);
        Assert.Equal("skill_exists", Error(conflict.Output));
        Assert.Contains("--force", conflict.Output.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(skill));
        Assert.Equal(0, Run(app, relative, true).Exit);
        foreach (var (file, content) in files)
        {
            Assert.Equal(content, File.ReadAllText(Path.Combine(dir, file)));
        }
        Assert.Equal("keep", File.ReadAllText(notes));
        Assert.Empty(_world.Runtime.Calls);
    }

    [Fact]
    public void File_parent_and_readonly_parent_report_usage_error()
    {
        using var tree = new TempTree();
        var block = tree.File("out", "keep");
        var result = Run(App, Path.Combine(block, "skill"), true);
        Assert.Equal(2, result.Exit);
        Assert.Equal("usage_error", Error(result.Output));
        Assert.Equal("keep", File.ReadAllText(block));
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var parent = Directory.CreateDirectory(Path.Combine(tree.Root, "parent")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(parent, "skill")).FullName;
        var mode = File.GetUnixFileMode(parent);
        try
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var denied = Run(App, target, true);
            Assert.Equal(2, denied.Exit);
            Assert.Equal("usage_error", Error(denied.Output));
            Assert.Empty(Directory.GetFileSystemEntries(target));
            Assert.Single(Directory.GetFileSystemEntries(parent));
        }
        finally { File.SetUnixFileMode(parent, mode); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_write_or_install_restores_the_destination(bool installing)
    {
        using var tree = new TempTree();
        var target = Path.Combine(tree.Root, "skill");
        var old = tree.File("skill/SKILL.md", "old");
        var refFile = tree.File("skill/reference/skill.md", "old reference");
        var app = new CliApp(_world.Redactor);
        // Include a third file to exercise staging and a failure after replacement.
        StatusCommand.Register(app, () => _world.Services(), _world.Redactor);
        var count = 0;
        void Write(string path, string body)
        {
            if (!installing && ++count == 3)
            {
                throw new IOException("staging failure");
            }
            File.WriteAllText(path, body);
        }
        void Move(string source, string dest, string? backup)
        {
            if (++count == 3)
            {
                throw new IOException("installation failure " + dest);
            }
            SkillExporter.InstallFile(source, dest, backup);
        }
        SkillCommand.Register(app, tree.Root, Write, installing ? Move : null);
        var result = Run(app, target, true);
        Assert.Equal(1, result.Exit);
        Assert.Equal("operation_failed", Error(result.Output));
        Assert.Contains("SKILL.md", result.Output.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(old));
        Assert.Equal("old reference", File.ReadAllText(refFile));
        Assert.Equal(2, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
        Assert.Single(Directory.GetFileSystemEntries(tree.Root));
    }

    private static string? Error(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString();

    private static (int Exit, JsonElement Output) Run(CliApp app, string dir, bool force)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        List<string> args = ["skill", "export", "--dir", dir, "--format", "json"];
        if (force)
        {
            args.Add("--force");
        }
        var exit = app.Run(args, stdout, stderr);
        return (exit, JsonDocument.Parse(stdout.ToString()).RootElement.Clone());
    }
}
