using System.Text.RegularExpressions;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Tests.Output;

public partial class ErrorCodeDocsTests
{
    [GeneratedRegex(@"^\|\s*`(?<code>[a-z_]+)`\s*\|\s*(?<exit>\d)\s*\|", RegexOptions.Multiline)]
    private static partial Regex ErrorRow();

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlBranch.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, relative));
    }

    [Fact]
    public void Every_error_code_is_documented_with_its_exit_code_and_nothing_else_is()
    {
        var documented = ErrorRow().Matches(RepoFile("docs/cli-output.md"))
            .ToDictionary(m => m.Groups["code"].Value, m => int.Parse(m.Groups["exit"].Value, System.Globalization.CultureInfo.InvariantCulture));
        var inCode = ErrorCodes.All.ToDictionary(c => c.Code, c => (int)c.ExitCode);

        Assert.Equal(inCode.OrderBy(p => p.Key), documented.OrderBy(p => p.Key));
    }

    [Fact]
    public void The_All_list_holds_every_public_error_code_field()
    {
        var byReflection = typeof(ErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ErrorCode))
            .Select(f => ((ErrorCode)f.GetValue(null)!).Code)
            .Order(StringComparer.Ordinal);

        Assert.Equal(byReflection, ErrorCodes.All.Select(c => c.Code).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Error_codes_are_unique()
    {
        Assert.Equal(ErrorCodes.All.Count, ErrorCodes.All.Select(c => c.Code).Distinct().Count());
    }
}
