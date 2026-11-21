using System.Collections;
using System.Text;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;

namespace SqlBranch.IntegrationTests;

internal static class SqlPackageFixture
{
    internal static readonly SystemProcessRunner Runner = new(inheritEnvironment: false);

    public static async Task<ProcessOutput> ExportAsync(
        EngineWorld world, IEngineSession admin, string executable, string scratch, string package, string setupSql,
        bool contained = false, bool verifyExtraction = true)
    {
        var name = "f2_export_" + Guid.NewGuid().ToString("N");
        var password = PasswordGenerator.Generate();
        var redactor = world.Redactor;
        redactor.Register(password);
        redactor.Register(world.Password);
        redactor.Register(Uri.EscapeDataString(world.Password));
        var token = CancellationToken.None;
        try
        {
            try
            {
                await admin.ExecuteAsync($"CREATE DATABASE [{name}]", token);
                if (contained)
                {
                    await admin.ExecuteAsync($"ALTER DATABASE [{name}] SET CONTAINMENT = PARTIAL", token);
                }
                await admin.ExecuteAsync($"CREATE LOGIN [{name}] WITH PASSWORD='{password}', CHECK_POLICY=ON", token);
                // The owner connects as dbo without adding a login-mapped user to the exported model.
                await admin.ExecuteAsync($"ALTER AUTHORIZATION ON DATABASE::[{name}] TO [{name}]", token);
                await admin.ExecuteAsync($"EXEC [{name}].sys.sp_executesql {Quoting.Literal(setupSql)}", token);

                List<string> arguments =
                [
                    "/Action:Export", $"/TargetFile:{package}",
                    FormattableString.Invariant($"/SourceServerName:127.0.0.1,{world.Port}"),
                    $"/SourceDatabaseName:{name}", $"/SourceUser:{name}", $"/SourcePassword:{password}",
                    "/SourceTrustServerCertificate:True", "/Diagnostics:False",
                ];
                if (!verifyExtraction)
                {
                    arguments.Add("/p:VerifyExtraction=False");
                }

                AssertSecretAbsent(string.Join(' ', arguments), world.Password);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var result = await Runner.RunAsync(executable, arguments, ScratchEnvironment(scratch), deadline.Token);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"SqlPackage exited with code {result.ExitCode}: {result.StdErr}\n{result.StdOut}".Trim());
                }
                Assert.True(File.Exists(package), "SqlPackage export did not create the fixture package.");
                await AssertSecretsAbsentAsync(world, scratch, password, result, deadline.Token);
                return result;
            }
            finally
            {
                // Drop the database first so cleanup needs no ownership change that could fire a fixture trigger.
                try
                {
                    await admin.ExecuteAsync(
                        $"IF DB_ID(N'{name}') IS NOT NULL ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                        $"DROP DATABASE IF EXISTS [{name}];", token);
                }
                finally
                {
                    await admin.ExecuteAsync($"IF SUSER_ID(N'{name}') IS NOT NULL DROP LOGIN [{name}]", token);
                }
                Assert.Null(await admin.ScalarAsync($"SELECT SUSER_ID(N'{name}')", token));
            }
        }
        catch (Exception error)
        {
            var cause = error.GetBaseException();
            throw new InvalidOperationException(
                $"SqlPackage fixture export failed ({cause.GetType().Name}): {redactor.Redact(cause.Message)}");
        }
    }

    public static Dictionary<string, string> ScratchEnvironment(string scratch)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = scratch, ["USERPROFILE"] = scratch, ["DOTNET_CLI_HOME"] = scratch,
            ["TMPDIR"] = scratch, ["TMP"] = scratch, ["TEMP"] = scratch,
        };
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (name.Equals("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("DOTNET_ROOT_", StringComparison.OrdinalIgnoreCase))
            {
                environment[name] = (string)entry.Value!;
            }
        }
        return environment;
    }

    public static async Task AssertSecretsAbsentAsync(
        EngineWorld world, string scratch, string password, ProcessOutput result, CancellationToken token)
    {
        AssertSecretAbsent(result.StdOut + result.StdErr, password);
        AssertSecretAbsent(result.StdOut + result.StdErr, world.Password);
        foreach (var file in Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file, token);
            foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
            {
                AssertSecretAbsent(encoding.GetString(bytes), password);
                AssertSecretAbsent(encoding.GetString(bytes), world.Password);
            }
            Assert.False(Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase), "Unexpected SqlPackage diagnostic log.");
        }
    }

    public static void AssertSecretAbsent(string text, string secret) =>
        Assert.False(text.Contains(secret, StringComparison.Ordinal), "A credential was found outside its intended scope.");
}
