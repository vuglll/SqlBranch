using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>Task 5.15: the commands in docs/images.md run as written, with the exit codes it states.</summary>
[Trait("Category", "Integration")]
public sealed partial class ImageDocsIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    [GeneratedRegex(@"^sqlbranch (?<args>[^#]*?)\s*(#\s*exit (?<exit>\d))?\s*$")]
    private static partial Regex CommandLine();

    private static string DocsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlBranch.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "docs", "images.md");
    }

    [IntegrationFact]
    public async Task The_documented_commands_run_as_written_with_the_documented_exit_codes()
    {
        Assert.True(fixture.Init.Exit == 0, fixture.Init.Out + fixture.Init.Err);
        var commands = new List<(string[] Args, int Exit)>();
        var inBlock = false;
        foreach (var line in File.ReadAllLines(DocsPath()))
        {
            if (line.StartsWith("```sh", StringComparison.Ordinal))
            {
                inBlock = true;
            }
            else if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inBlock = false;
            }
            else if (inBlock && CommandLine().Match(line) is { Success: true } match)
            {
                commands.Add(([.. match.Groups["args"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
                    int.Parse(match.Groups["exit"].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        Assert.True(commands.Count >= 10, "docs/images.md documents too few commands.");
        Assert.Contains(commands, c => c.Args.SequenceEqual(["image", "rm", "shop", "--force"]));

        // The document's shop.bak is a full backup of the database Shop; the test makes it on the test engine.
        var backup = await fixture.MakeBackupAsync("Shop");
        var scripts = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "db")).FullName;
        await File.WriteAllTextAsync(Path.Combine(scripts, "001_schema.sql"), "CREATE TABLE dbo.Item(Id int);");
        await File.WriteAllTextAsync(Path.Combine(scripts, "002_seed.sql"), "INSERT dbo.Item VALUES(1);");
        var mask = Path.Combine(fixture.ClientFolder, "mask.sql");
        var markdown = File.ReadAllText(DocsPath());
        var sample = Regex.Match(markdown, @"```sql\r?\n(UPDATE dbo\.Item[^\r\n]+)\r?\n```");
        Assert.True(sample.Success);
        await File.WriteAllTextAsync(mask, sample.Groups[1].Value);
        var world = fixture.World!;
        foreach (var (args, exit) in commands)
        {
            var resolved = args.Select(a => a switch { "shop.bak" => backup, "db" => scripts, "mask.sql" => mask, _ => a }).ToArray();
            var run = world.RunCli(resolved);
            output.WriteLine($"sqlbranch {string.Join(' ', args)} -> {run.Exit} ({run.Elapsed.TotalSeconds:F1} s)");
            Assert.True(exit == run.Exit, $"sqlbranch {string.Join(' ', args)} exited {run.Exit}, the document says {exit}.\n{run.Out}\n{run.Err}");
            Assert.DoesNotContain(world.Password, run.Out + run.Err, StringComparison.Ordinal);
        }

        // Nothing the documented commands built is left: the pool has no images and no temporary files.
        var left = await world.Runtime.ExecAsync(world.EngineName, ["sh", "-c", "ls -A /pool/images; ls -A /pool/build; find /pool -name '*.bak'"], CancellationToken.None);
        Assert.Equal(string.Empty, left.StdOut.Trim());
    }
}
