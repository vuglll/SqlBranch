namespace SqlBranch.Testing.Tests;

public sealed class ReadmeSnippetTests
{
    [Fact]
    public void Readme_use_it_in_tests_snippet_matches_the_xunit_sample()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SqlBranch.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);

        var readme = File.ReadAllText(Path.Combine(root.FullName, "README.md")).Replace("\r\n", "\n");
        var heading = readme.IndexOf("\n## Use it in tests\n", StringComparison.Ordinal);
        Assert.True(heading >= 0, "README.md has no \"Use it in tests\" section.");
        var open = readme.IndexOf("```csharp\n", heading, StringComparison.Ordinal);
        Assert.True(open >= 0, "The README section has no csharp snippet.");
        var start = open + "```csharp\n".Length;
        var end = readme.IndexOf("\n```", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "The README snippet is not closed.");
        var snippet = Normalize(readme[start..end]);

        var sample = File.ReadAllText(Path.Combine(root.FullName, "samples", "Xunit", "BranchTests.cs")).Replace("\r\n", "\n");
        var first = sample.IndexOf("    [SampleFact]\n", StringComparison.Ordinal);
        Assert.True(first >= 0, "The sample has no [SampleFact] test.");
        var last = sample.IndexOf("\n    }\n", first, StringComparison.Ordinal);
        Assert.True(last >= 0, "The sample test method is not closed.");
        var method = sample[first..(last + "\n    }".Length)]
            .Replace("[SampleFact]", "[Fact]", StringComparison.Ordinal);
        var expected = Normalize(string.Join('\n', method.Split('\n').Select(line => line.StartsWith("    ", StringComparison.Ordinal) ? line[4..] : line)));

        Assert.Equal(expected, snippet);
    }

    private static string Normalize(string text) => text.Trim().Replace("\r\n", "\n");
}
