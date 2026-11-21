using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed partial class SqlPackageImportIntegrationTests(ITestOutputHelper output)
{
    private const string MappedUser = "f2_mapped_user";
    private const string MappedLogin = "f2_fixture_login";
    private const string ContainedUser = "f2_contained_user";
    private const string Sentinel = "f2_sentinel";
    private static readonly string[] MissingLoginDiagnosticCodes = ["SQL72012", "SQL72014", "SQL72045"];
    private static readonly CancellationToken NoCancellation = CancellationToken.None;
    private static readonly SqlClientEngineConnector Connector = new();
    private static readonly SystemProcessRunner Runner = SqlPackageFixture.Runner;

    [IntegrationThinFact]
    public async Task Restricted_import_compares_mapped_and_contained_users_and_cleans_up()
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

        var version = await Runner.RunAsync(executable, ["/Version"], null, NoCancellation);
        Assert.Equal(0, version.ExitCode);
        Assert.True(Version.TryParse(version.StdOut.Trim(), out var parsedVersion));
        output.WriteLine($"SqlPackage /Version returned {parsedVersion}.");

        await using var world = new EngineWorld(plain: true);
        var init = world.RunCli("init");
        Assert.True(init.Exit == 0, init.Out + init.Err);
        await using var admin = await Connector.ConnectAsync(world.Endpoint, world.Password, NoCancellation);
        output.WriteLine($"SQL Server ProductVersion is {await admin.ScalarAsync("SELECT SERVERPROPERTY('ProductVersion')", NoCancellation)}.");
        await admin.ExecuteAsync("EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;", NoCancellation);
        await admin.ExecuteAsync($"CREATE DATABASE [{Sentinel}]", NoCancellation);
        await admin.ExecuteAsync($"CREATE TABLE [{Sentinel}].dbo.Marker (Value int); INSERT [{Sentinel}].dbo.Marker VALUES (42);", NoCancellation);

        var mappedPassword = PasswordGenerator.Generate();
        var containedPassword = PasswordGenerator.Generate();
        world.Redactor.Register(mappedPassword);
        world.Redactor.Register(containedPassword);
        await CreateLoginAsync(admin, MappedLogin, mappedPassword);
        var scratch = Directory.CreateTempSubdirectory("sqlbranch-it-bacpac-");
        try
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(scratch.FullName));
            var scratchEnvironment = SqlPackageFixture.ScratchEnvironment(scratch.FullName);
            var childEnvironment = await Runner.RunAsync("/usr/bin/env", [], scratchEnvironment, NoCancellation);
            Assert.Equal(0, childEnvironment.ExitCode);
            Assert.True(scratchEnvironment.Select(entry => $"{entry.Key}={entry.Value}").Order()
                .SequenceEqual(childEnvironment.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order()),
                "The runner should give the child only the scratch environment.");

            var mappedPackage = Path.Combine(scratch.FullName, "mapped.bacpac");
            var containedPackage = Path.Combine(scratch.FullName, "contained.bacpac");
            await ExportFixtureAsync(world, admin, executable, scratch.FullName, mappedPackage,
                $"CREATE USER [{MappedUser}] FOR LOGIN [{MappedLogin}]", contained: false);
            await ExportFixtureAsync(world, admin, executable, scratch.FullName, containedPackage,
                $"CREATE USER [{ContainedUser}] WITH PASSWORD='{containedPassword}'", contained: true);

            var mappedProperties = InspectModel(mappedPackage, MappedUser);
            var containedProperties = InspectModel(containedPackage, ContainedUser);
            var withLogin = await ImportAsync(world, admin, executable, scratch.FullName, mappedPackage, "mapped with login", contained: false);
            await admin.ExecuteAsync($"DROP LOGIN [{MappedLogin}]", NoCancellation);
            var withoutLogin = await ImportAsync(world, admin, executable, scratch.FullName, mappedPackage, "mapped without login", contained: false);
            var control = await ImportAsync(world, admin, executable, scratch.FullName, containedPackage, "contained control", contained: true);

            var corruptPackage = Path.Combine(scratch.FullName, "corrupt.bacpac");
            await File.WriteAllTextAsync(corruptPackage, "invalid package");
            var failure = await ImportAsync(world, admin, executable, scratch.FullName, corruptPackage, "corrupt control", contained: false);

            Assert.Contains("LoginIdentifierScript", mappedProperties);
            Assert.DoesNotContain("Password", mappedProperties);
            Assert.Contains("Password", containedProperties);
            Assert.DoesNotContain("LoginIdentifierScript", containedProperties);
            Assert.Equal(0, withLogin.Exit);
            Assert.Equal(MappedUser, withLogin.User);
            Assert.Equal("INSTANCE", withLogin.Authentication);
            Assert.Equal(MappedLogin, withLogin.ServerLogin);
            Assert.Equal(1, withoutLogin.Exit);
            Assert.Equal(MissingLoginDiagnosticCodes, withoutLogin.DiagnosticCodes.Order(StringComparer.Ordinal));
            Assert.Null(withoutLogin.User);
            Assert.Null(withoutLogin.ServerLogin);
            Assert.Equal(0, control.Exit);
            Assert.Equal(ContainedUser, control.User);
            Assert.Equal("DATABASE", control.Authentication);
            Assert.Null(control.ServerLogin);
            Assert.NotEqual(0, failure.Exit);
            Assert.Null(failure.User);
            Assert.Equal(42, await admin.ScalarAsync($"SELECT Value FROM [{Sentinel}].dbo.Marker", NoCancellation));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
        Assert.False(Directory.Exists(scratch.FullName));
    }

    private async Task ExportFixtureAsync(
        EngineWorld world, IEngineSession admin, string executable, string scratch, string package, string userSql, bool contained)
    {
        // The mapped user depends on a server login outside the extracted model.
        var result = await SqlPackageFixture.ExportAsync(world, admin, executable, scratch, package,
            $"CREATE TABLE dbo.Items (Id int PRIMARY KEY); INSERT dbo.Items VALUES (1); {userSql};",
            contained, verifyExtraction: contained);
        output.WriteLine($"Fixture export {(contained ? "contained" : "mapped")}: {Summarize(result)}");
    }

    private async Task<(int Exit, string[] DiagnosticCodes, string? User, string? Authentication, string? ServerLogin)> ImportAsync(
        EngineWorld world, IEngineSession admin, string executable, string scratch, string package, string scenario, bool contained)
    {
        var id = Guid.NewGuid().ToString("N");
        var name = ImagePaths.BuildDatabase(id);
        var directory = ImagePaths.Build(id);
        var password = PasswordGenerator.Generate();
        var makeRoot = await world.Runtime.ExecAsync(world.EngineName, ["mkdir", "-p", ImagePaths.BuildRoot], NoCancellation);
        Assert.True(makeRoot.Succeeded);
        var makeDirectory = await world.Runtime.ExecAsync(world.EngineName, ["mkdir", directory], NoCancellation);
        Assert.True(makeDirectory.Succeeded);
        try
        {
            await admin.ExecuteAsync(
                $"CREATE DATABASE [{name}] ON PRIMARY (NAME=N'data', FILENAME=N'{directory}/data.mdf') LOG ON (NAME=N'log', FILENAME=N'{directory}/log.ldf')",
                NoCancellation);
            if (contained)
            {
                await admin.ExecuteAsync($"ALTER DATABASE [{name}] SET CONTAINMENT = PARTIAL", NoCancellation);
            }
            await CreateLoginAsync(admin, name, password);
            await admin.ExecuteAsync($"ALTER AUTHORIZATION ON DATABASE::[{name}] TO [{name}]", NoCancellation);

            await using (var restricted = await Connector.ConnectLoginAsync(world.Endpoint, name, password, name, NoCancellation))
            {
                Assert.Equal("dbo", await restricted.ScalarAsync("SELECT USER_NAME()", NoCancellation));
                Assert.Equal(0, await restricted.ScalarAsync("SELECT HAS_DBACCESS('f2_sentinel')", NoCancellation));
                Assert.Equal(0, await restricted.ScalarAsync("""
                    SELECT COUNT(*) FROM sys.fn_my_permissions(NULL, 'SERVER')
                    WHERE permission_name IN ('CREATE ANY DATABASE', 'ALTER ANY LOGIN', 'CONTROL SERVER')
                    """, NoCancellation));
            }
            var engineSettings = await EngineWorld.DockerAsync("inspect", "--type", "container", world.EngineName);
            Assert.Equal(0, engineSettings.ExitCode);
            SqlPackageFixture.AssertSecretAbsent(engineSettings.StdOut + engineSettings.StdErr, password);
            SqlPackageFixture.AssertSecretAbsent(engineSettings.StdOut + engineSettings.StdErr, world.Password);

            var result = await RunToolAsync(world, executable, scratch, password,
                ["/Action:Import", $"/SourceFile:{package}", $"/TargetServerName:{Server(world)}", $"/TargetDatabaseName:{name}",
                 $"/TargetUser:{name}", $"/TargetPassword:{password}", "/TargetTrustServerCertificate:True"]);

            var rows = await admin.QueryAsync($"""
                SELECT p.name, p.authentication_type_desc, l.name, p.type_desc
                FROM [{name}].sys.database_principals p
                LEFT JOIN sys.server_principals l ON l.sid = p.sid AND p.authentication_type = 1
                WHERE p.name IN ('{MappedUser}', '{ContainedUser}')
                """, NoCancellation);
            var principal = rows.SingleOrDefault();
            output.WriteLine($"{scenario}: {Summarize(result)}; principal {DescribePrincipal(principal)}");
            if (result.ExitCode == 0)
            {
                Assert.Equal(1, await admin.ScalarAsync($"SELECT COUNT(*) FROM [{name}].dbo.Items", NoCancellation));
            }
            var loginHealth = await admin.QueryAsync($"SELECT name FROM sys.sql_logins WHERE name IN ('{MappedLogin}', '{name}') ORDER BY name", NoCancellation);
            Assert.Equal(scenario == "mapped with login" ? new[] { MappedLogin, name } : [name],
                loginHealth.Select(row => (string)row[0]!));
            return (result.ExitCode, ReadDiagnosticCodes(result), principal?[0] as string, principal?[1] as string, principal?[2] as string);
        }
        finally
        {
            await DropDatabaseAndLoginAsync(admin, name);
            var removed = await world.Runtime.ExecAsync(world.EngineName, ["rmdir", directory], NoCancellation);
            Assert.True(removed.Succeeded, "The import database files should be gone after dropping the database.");
        }
    }

    private static string DescribePrincipal(object?[]? principal) => principal is null
        ? "absent"
        : $"{principal[0]} ({principal[3]}, {principal[1]}, server login {principal[2] ?? "absent"})";

    private string?[] InspectModel(string package, string user)
    {
        using var zip = ZipFile.OpenRead(package);
        using var stream = zip.GetEntry("model.xml")!.Open();
        var model = XDocument.Load(stream);
        var element = model.Descendants()
            .SingleOrDefault(node => node.Name.LocalName == "Element" && (string?)node.Attribute("Type") == "SqlUser"
                && (string?)node.Attribute("Name") == $"[{user}]");
        Assert.NotNull(element);
        var properties = element.Elements().Where(node => node.Name.LocalName == "Property")
            .Select(node => (string?)node.Attribute("Name")).ToArray();
        output.WriteLine($"{Path.GetFileName(package)} model.xml: SqlUser [{user}]; properties={string.Join(", ", properties)}.");
        return properties;
    }

    private static async Task<ProcessOutput> RunToolAsync(
        EngineWorld world, string executable, string scratch, string password, string[] arguments)
    {
        SqlPackageFixture.AssertSecretAbsent(string.Join(' ', arguments), world.Password);
        Assert.True(arguments.Count(argument => argument.Contains(password, StringComparison.Ordinal)) == 1,
            "Expected the throwaway password in one tool argument.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var result = await Runner.RunAsync(executable, [.. arguments, "/Diagnostics:False"],
            SqlPackageFixture.ScratchEnvironment(scratch), deadline.Token);
        await SqlPackageFixture.AssertSecretsAbsentAsync(world, scratch, password, result, deadline.Token);
        return result;
    }

    private static string Summarize(ProcessOutput result)
    {
        var captured = result.StdOut + result.StdErr;
        var codes = ReadDiagnosticCodes(result);
        var names = new[] { MappedUser, MappedLogin, ContainedUser }
            .Where(name => captured.Contains(name, StringComparison.OrdinalIgnoreCase));
        return $"exit {result.ExitCode}, diagnostic codes [{string.Join(", ", codes)}], fixture names [{string.Join(", ", names)}]";
    }

    private static string[] ReadDiagnosticCodes(ProcessOutput result) =>
        DiagnosticCode().Matches(result.StdOut + result.StdErr).Select(match => match.Value).Distinct().ToArray();

    [GeneratedRegex(@"\bSQL\d{5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticCode();

    private static string Server(EngineWorld world) => string.Create(CultureInfo.InvariantCulture, $"127.0.0.1,{world.Port}");

    private static Task CreateLoginAsync(IEngineSession admin, string name, string password) =>
        admin.ExecuteAsync($"CREATE LOGIN [{name}] WITH PASSWORD='{password}', CHECK_POLICY=ON", NoCancellation);

    private static async Task DropDatabaseAndLoginAsync(IEngineSession admin, string name)
    {
        try
        {
            await admin.ExecuteAsync($"USE master; IF DB_ID(N'{name}') IS NOT NULL ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE IF EXISTS [{name}];", NoCancellation);
        }
        finally
        {
            await admin.ExecuteAsync($"IF SUSER_ID(N'{name}') IS NOT NULL DROP LOGIN [{name}]", NoCancellation);
        }
        Assert.Null(await admin.ScalarAsync($"SELECT SUSER_ID(N'{name}')", NoCancellation));
        Assert.Null(await admin.ScalarAsync($"SELECT DB_ID(N'{name}')", NoCancellation));
    }
}
