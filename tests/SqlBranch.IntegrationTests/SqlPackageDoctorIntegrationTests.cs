using System.Text.Json;
using SqlBranch.Core.Containers;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SqlPackageDoctorIntegrationTests(ITestOutputHelper output)
{
    [IntegrationThinFact]
    public async Task Doctor_uses_the_real_executable_and_reports_an_invalid_override()
    {
        var executable = Environment.GetEnvironmentVariable("SQLBRANCH_IT_SQLPACKAGE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new SkipTestException("Set SQLBRANCH_IT_SQLPACKAGE to the installed SqlPackage executable.");
        }

        if (OperatingSystem.IsWindows())
        {
            throw new SkipTestException("Windows SqlPackage verification is reserved for the 0.3 release gate.");
        }

        executable = Path.GetFullPath(executable);
        var probe = await new SystemProcessRunner(inheritEnvironment: false)
            .RunAsync(executable, ["/Version"], null, CancellationToken.None);
        Assert.Equal(0, probe.ExitCode);
        Assert.True(Version.TryParse(probe.StdOut.Trim(), out var version));
        output.WriteLine($"SqlPackage /Version returned {version}.");

        await using var world = new EngineWorld(plain: true);
        var directory = Directory.CreateTempSubdirectory("sqlbranch-it-sqlpackage-doctor-");
        try
        {
            var alias = Path.Combine(directory.FullName, "tool path");
            Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(executable)!);
            world.Environment["PATH"] = alias;

            var search = world.RunCli("doctor", "--format", "json");
            Assert.True(search.Exit == 0, search.Out + search.Err);
            Assert.Equal($"{Path.Combine(alias, "sqlpackage")} (version {version})", Detail(search.Out, "pass"));

            var chosen = world.RunCli("doctor", "--sqlpackage", executable, "--format", "json");
            Assert.True(chosen.Exit == 0, chosen.Out + chosen.Err);
            Assert.Equal($"{executable} (version {version})", Detail(chosen.Out, "pass"));

            var chosenText = world.RunCli("doctor", "--sqlpackage", executable);
            Assert.True(chosenText.Exit == 0, chosenText.Out + chosenText.Err);
            Assert.Contains($"{executable} (version {version})", chosenText.Out, StringComparison.Ordinal);

            var nonExecutable = Path.Combine(directory.FullName, "non-executable");
            File.WriteAllText(nonExecutable, "fixture");
            File.SetUnixFileMode(nonExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            foreach (var unavailable in new[] { Path.Combine(directory.FullName, "missing"), nonExecutable })
            {
                var missing = world.RunCli("doctor", "--sqlpackage", unavailable, "--format", "json");
                Assert.Equal(3, missing.Exit);
                using var failure = JsonDocument.Parse(missing.Out);
                Assert.Equal("sqlpackage_missing", failure.RootElement.GetProperty("error").GetProperty("code").GetString());
                Assert.Contains("dotnet tool install -g microsoft.sqlpackage", Detail(missing.Out, "fail"), StringComparison.Ordinal);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string? Detail(string json, string expectedStatus)
    {
        using var report = JsonDocument.Parse(json);
        var check = report.RootElement.GetProperty("checks").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "sqlpackage");
        Assert.Equal(expectedStatus, check.GetProperty("status").GetString());
        return check.GetProperty("detail").GetString();
    }
}
