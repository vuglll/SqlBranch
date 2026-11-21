using System.Text.Json;
using SqlBranch.Core.Images;
using SqlBranch.Core.Tests.Configuration;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

public sealed class ImageScriptTests
{
    [Fact]
    public void Ordinal_source_files_are_read_once_and_GO_splits_batches_with_line_numbers()
    {
        using var files = new TempTree();
        files.File("002.sql", "SELECT 2");
        files.File("old/000.sql", "ignored");
        files.File("notes.md", "ignored");
        files.File("001.sql", "SELECT 1\r\n gO \r\nSELECT 11");
        var scripts = ImageScript.ReadFolder(files.Root);
        Assert.Equal(["001.sql", "002.sql"], scripts.Select(s => s.File.Name));
        Assert.Equal([1, 3], scripts[0].Batches.Select(b => b.Line));
        Assert.Equal(["SELECT 1", "SELECT 11"], scripts[0].Batches.Select(b => b.Sql.Trim()));
    }

    [Theory]
    [InlineData("both")]
    [InlineData("neither")]
    [InlineData("missing-folder")]
    [InlineData("empty-folder")]
    [InlineData("unreadable-folder")]
    [InlineData("missing-post")]
    [InlineData("unreadable-post")]
    [InlineData("repeat-batch")]
    [InlineData("0")]
    [InlineData("366d")]
    public void Bad_script_inputs_fail_before_any_Docker_call(string example)
    {
        using var files = new TempTree();
        var file = files.File("seed.sql", string.Concat(Enumerable.Repeat("\n", 11)) + "GO 5");
        var empty = files.Dir("empty");
        string[] args = example switch
        {
            "both" => ["--from-scripts", files.Root, "--from-bak", "shop.bak"],
            "neither" => [],
            "missing-folder" => ["--from-scripts", Path.Combine(files.Root, "missing")],
            "empty-folder" => ["--from-scripts", empty],
            "unreadable-folder" => ["--from-scripts", file],
            "missing-post" => ["--from-bak", "shop.bak", "--post-script", Path.Combine(files.Root, "missing.sql")],
            "unreadable-post" => ["--from-bak", "shop.bak", "--post-script", empty],
            "repeat-batch" => ["--from-scripts", files.Root],
            _ => ["--from-bak", "shop.bak", "--script-timeout", example],
        };
        var world = new CliWorld();
        var result = world.Run(["image", "build", "shop", .. args, "--format", "json"]);
        Assert.Equal(2, result.Exit);
        using var doc = JsonDocument.Parse(result.Out);
        Assert.Equal("usage_error", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(world.Runtime.Calls);
        if (example == "repeat-batch")
        {
            Assert.Contains("seed.sql, line 12", result.Out, StringComparison.Ordinal);
        }
    }
}
