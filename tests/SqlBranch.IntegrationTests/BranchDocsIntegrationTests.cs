using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>Task 6.16: the commands in docs/branches.md run as written, with the exit codes it states.</summary>
[Trait("Category", "Integration")]
public sealed partial class BranchDocsIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
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
        return Path.Combine(dir.FullName, "docs", "branches.md");
    }

    private sealed class ExampleClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

        Assert.True(commands.Count >= 25, "docs/branches.md documents too few commands.");
        Assert.Contains(commands, c => c.Args.SequenceEqual(["branch", "create", "bench1", "--from", "shop", "--copy"]));
        Assert.Contains(commands, c => c.Args.SequenceEqual(["branch", "conn", "pr-142", "--quiet"]));
        Assert.Contains(commands, c => c.Args.SequenceEqual(["image", "build", "shop-full", "--from-bak", "shop.bak", "--keep-recovery-model"]));

        // The document's shop.bak is a full backup of the database Shop; the test makes it on the test engine.
        var backup = await fixture.MakeBackupAsync("Shop");
        var world = fixture.World!;
        var passwordSeen = false;
        var created = DateTimeOffset.UnixEpoch;
        var time = new ExampleClock(created);
        try
        {
            foreach (var (args, exit) in commands)
            {
                var resolved = args.Select(a => a == "shop.bak" ? backup : a).ToArray();
                if (args is ["status", "--format", "json"])
                {
                    time = new ExampleClock(created.AddMinutes(31));
                }
                var run = world.RunCliAt(time, resolved);
                output.WriteLine($"sqlbranch {string.Join(' ', args)} -> {run.Exit} ({run.Elapsed.TotalSeconds:F1} s)");
                Assert.True(exit == run.Exit, $"sqlbranch {string.Join(' ', args)} exited {run.Exit}, the document says {exit}.\n{run.Out}\n{run.Err}");

                if (args is ["branch", "list", "--label", "run=17", ..])
                {
                    var entry = Assert.Single(BranchKit.Parse(run.Out).GetProperty("branches").EnumerateArray());
                    Assert.Equal("run17", entry.GetProperty("name").GetString());
                    Assert.False(entry.GetProperty("expired").GetBoolean());
                }
                else if (args is ["status", "--format", "json"])
                {
                    Assert.Equal("run17", Assert.Single(BranchKit.Parse(run.Out).GetProperty("expiredBranches").EnumerateArray()).GetString());
                }
                else if (args is ["branch", "prune", ..])
                {
                    Assert.Equal("run17", Assert.Single(BranchKit.Parse(run.Out).GetProperty("removed").EnumerateArray()).GetString());
                }

                // Only 'branch conn' prints the password, as the document says.
                var printsPassword = args is ["branch", "conn", ..] && run.Exit == 0;
                if (printsPassword)
                {
                    Assert.Contains(world.Password, run.Out, StringComparison.Ordinal);
                    passwordSeen = true;
                }
                else
                {
                    Assert.DoesNotContain(world.Password, run.Out + run.Err, StringComparison.Ordinal);
                }
            }

            Assert.True(passwordSeen);
        }
        finally
        {
            // Whatever a failed run left, so the next test of this engine finds nothing: branches by 'branch rm', then the image files.
            var kit = new BranchKit(fixture, output);
            foreach (var branch in new[] { "pr-142", "bench1", "run17" })
            {
                await kit.DropBranchAsync(branch);
            }

            await kit.RemoveImageFilesAsync("shop");
            await kit.RemoveImageFilesAsync("shop-full");
        }

        // Format 2 retains .ids high-water marks after image removal.
        // Nothing the documented commands made is left.
        var left = await world.Runtime.ExecAsync(world.EngineName, ["sh", "-c", "find /pool/images -mindepth 1 -maxdepth 1 ! -name .ids -printf '%f\\n'; ls -A /pool/branches; ls -A /pool/build; find /pool -name '*.bak'"], CancellationToken.None);
        Assert.Equal(string.Empty, left.StdOut.Trim());
    }
}
