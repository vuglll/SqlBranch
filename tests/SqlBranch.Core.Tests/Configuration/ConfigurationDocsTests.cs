using System.Text.RegularExpressions;
using SqlBranch.Core.Configuration;

namespace SqlBranch.Core.Tests.Configuration;

public partial class ConfigurationDocsTests
{
    [GeneratedRegex(@"^\|\s*`(?<key>[a-zA-Z]+)`\s*\|\s*`(?<env>SQLBRANCH_[A-Z_]+)`\s*\|\s*(?<default>[^|]+?)\s*\|", RegexOptions.Multiline)]
    private static partial Regex SettingRow();

    private static string Docs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlBranch.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, "docs", "configuration.md"));
    }

    [Fact]
    public void Documented_settings_match_the_settings_model()
    {
        var documented = SettingRow().Matches(Docs())
            .Select(m => (m.Groups["key"].Value, m.Groups["env"].Value, m.Groups["default"].Value.Trim('`')))
            .OrderBy(r => r.Item1, StringComparer.Ordinal)
            .ToList();
        var model = SettingKeys.All
            .Select(d => (d.Key, d.EnvironmentVariable, d.Default))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(model, documented);
    }
}
