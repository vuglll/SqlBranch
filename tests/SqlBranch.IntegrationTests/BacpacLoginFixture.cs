using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;

namespace SqlBranch.IntegrationTests;

internal static class BacpacLoginFixture
{
    private static readonly string[] FixtureLogins = ["[report_reader]", "[sales_app]"];
    private static readonly XNamespace Ns = BacpacPackage.ModelNamespace;
    private static readonly CancellationToken None = CancellationToken.None;

    public static async Task<(string Native, string Azure)> CreateAsync(
        EngineWorld world, IEngineSession admin, string executable, string scratch, Action<string>? record = null)
    {
        var original = Path.Combine(scratch, "original.bacpac");
        await ExportAsync(world, admin, executable, scratch, original);
        using var source = ZipFile.OpenRead(original);
        var native = ReadXml(source, "model.xml");
        if (record is not null)
        {
            RecordModel("Native export", native, record);
        }

        MakeAzureShaped(native);
        if (record is not null)
        {
            RecordModel("Azure-shaped variant before detach", native, record);
        }

        var azure = Path.Combine(scratch, "azure.bacpac");
        WriteAzureFixture(source, azure, native);
        return (original, azure);
    }

    private static async Task ExportAsync(
        EngineWorld world, IEngineSession admin, string executable, string scratch, string original)
    {
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bacpac-login-users.sql"));
        var batches = fixture.ReplaceLineEndings("\n").Split("\nGO\n", StringSplitOptions.None);
        Assert.Equal(2, batches.Length);
        var reportPassword = PasswordGenerator.Generate();
        var salesPassword = PasswordGenerator.Generate();
        world.Redactor.Register(reportPassword);
        world.Redactor.Register(salesPassword);
        await admin.ExecuteAsync(batches[0]
            .Replace("{{REPORT_READER_PASSWORD}}", Quoting.Literal(reportPassword), StringComparison.Ordinal)
            .Replace("{{SALES_APP_PASSWORD}}", Quoting.Literal(salesPassword), StringComparison.Ordinal), None);
        await SqlPackageFixture.ExportAsync(world, admin, executable, scratch, original, batches[1], verifyExtraction: false);
    }

    private static List<XElement> Elements(XDocument document) =>
        document.Root!.Element(Ns + "Model")!.Elements(Ns + "Element").ToList();

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    private static XElement User(IEnumerable<XElement> elements, string name) =>
        elements.Single(e => Attr(e, "Type") == "SqlUser" && Attr(e, "Name") == name);

    private static bool IsLoginMapping(XElement element) =>
        (element.Name == Ns + "Property" && Attr(element, "Name") == "LoginIdentifierScript") ||
        (element.Name == Ns + "Relationship" && Attr(element, "Name") == "Login");

    private static void RecordModel(string heading, XDocument document, Action<string> record)
    {
        var elements = Elements(document);
        record(heading);
        foreach (var type in new[] { "SqlUser", "SqlLogin", "SqlRoleMembership", "SqlServerRoleMembership", "SqlPermissionStatement" })
        {
            record($"{type} elements: {elements.Count(e => Attr(e, "Type") == type)}");
        }
        foreach (var element in elements.Where(e =>
            Attr(e, "Type") is "SqlUser" or "SqlLogin" or "SqlRoleMembership" or "SqlPermissionStatement" ||
            e.DescendantsAndSelf().Any(node => IsFixtureLoginName(Attr(node, "Name")) || IsFixtureLoginName(Attr(node, "Value")))))
        {
            record(element.ToString(SaveOptions.DisableFormatting));
        }
    }

    private static bool IsFixtureLoginName(string? name) =>
        name is "report_reader" or "sales_app" || FixtureLogins.Contains(name, StringComparer.Ordinal);

    private static void MakeAzureShaped(XDocument document)
    {
        var elements = Elements(document);
        foreach (var name in FixtureLogins)
        {
            var user = User(elements, name);
            Assert.Contains(user.Elements(), IsLoginMapping);
            user.Elements().Where(IsLoginMapping).Remove();

            // SqlPackage can leave an unresolved LoginIdentifierScript without emitting a SqlLogin.
            var login = elements.SingleOrDefault(e => Attr(e, "Type") == "SqlLogin" && Attr(e, "Name") == name);
            if (login is null)
            {
                login = new XElement(Ns + "Element", new XAttribute("Type", "SqlLogin"), new XAttribute("Name", name));
                user.AddBeforeSelf(login);
            }
            if (name == "[sales_app]")
            {
                login.SetAttributeValue("Disambiguator", "3");
            }
            user.Add(LoginRelationship("Login", name, Attr(login, "Disambiguator")));
        }

        // The native export omits server role memberships. Model one against the Azure login identity.
        document.Root!.Element(Ns + "Model")!.Add(new XElement(Ns + "Element", new XAttribute("Type", "SqlServerRoleMembership"),
            LoginRelationship("Member", "[sales_app]", "3"), LoginRelationship("Role", "[dbcreator]")));
    }

    private static XElement LoginRelationship(string relationship, string name, string? disambiguator = null)
    {
        var reference = new XElement(Ns + "References", new XAttribute("Name", name));
        reference.SetAttributeValue("Disambiguator", disambiguator);
        return new XElement(Ns + "Relationship", new XAttribute("Name", relationship), new XElement(Ns + "Entry", reference));
    }

    private static XDocument ReadXml(ZipArchive package, string entry)
    {
        using var stream = package.GetEntry(entry)!.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void WriteAzureFixture(ZipArchive source, string path, XDocument model)
    {
        using var modelBytes = new MemoryStream();
        model.Save(modelBytes, SaveOptions.DisableFormatting);
        var bytes = modelBytes.ToArray();
        var origin = ReadXml(source, "Origin.xml");
        var checksum = origin.Root!.Element(Ns + "Checksums")?.Elements(Ns + "Checksum")
            .SingleOrDefault(c => string.Equals(Attr(c, "Uri"), "/model.xml", StringComparison.OrdinalIgnoreCase));
        if (checksum is not null)
        {
            checksum.Value = Convert.ToHexString(SHA256.HashData(bytes));
        }

        using var copy = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            using var destination = copy.CreateEntry(entry.FullName).Open();
            switch (entry.FullName)
            {
                case "model.xml":
                    destination.Write(bytes);
                    break;
                case "Origin.xml":
                    origin.Save(destination, SaveOptions.DisableFormatting);
                    break;
                default:
                    using (var data = entry.Open())
                    {
                        data.CopyTo(destination);
                    }
                    break;
            }
        }
    }

    public static async Task<string> ServerStateAsync(IEngineSession admin) =>
        (string)(await admin.ScalarAsync("""
            SELECT (
                SELECT
                    JSON_QUERY((SELECT principal_id, name, type_desc, sid, is_disabled, create_date, modify_date,
                                       default_database_name, default_language_name, owning_principal_id, is_fixed_role
                                FROM sys.server_principals ORDER BY principal_id FOR JSON PATH, INCLUDE_NULL_VALUES)) AS principals,
                    JSON_QUERY((SELECT role_principal_id, member_principal_id
                                FROM sys.server_role_members ORDER BY role_principal_id, member_principal_id FOR JSON PATH)) AS roleMemberships,
                    JSON_QUERY((SELECT class, major_id, grantee_principal_id, grantor_principal_id, type, state
                                FROM sys.server_permissions ORDER BY class, major_id, grantee_principal_id, grantor_principal_id, type
                                FOR JSON PATH)) AS permissions
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            )
            """, None))!;
}

internal sealed record BacpacLoginDatabaseState(string Principals, object? Owner, string Grants, string Roles, object? Orders)
{
    public static async Task<BacpacLoginDatabaseState> ReadAsync(
        IEngineSession admin, string database, CancellationToken cancellationToken)
    {
        var db = Quoting.Identifier(database);
        var principals = JsonSerializer.Serialize(await admin.QueryAsync($"""
            SELECT name, type_desc, authentication_type_desc FROM {db}.sys.database_principals
            WHERE name IN (N'report_reader', N'sales_app', N'no_login_control') ORDER BY name
            """, cancellationToken));
        var owner = await admin.ScalarAsync($"""
            SELECT p.name FROM {db}.sys.schemas AS s
            JOIN {db}.sys.database_principals AS p ON p.principal_id = s.principal_id
            WHERE s.name = N'reports'
            """, cancellationToken);
        var grants = JsonSerializer.Serialize(await admin.QueryAsync($"""
            SELECT p.name, g.state_desc FROM {db}.sys.database_permissions AS g
            JOIN {db}.sys.database_principals AS p ON p.principal_id = g.grantee_principal_id
            WHERE p.name IN (N'sales_app', N'no_login_control') AND g.permission_name = N'SELECT'
              AND g.class = 1 AND g.major_id = OBJECT_ID(N'{db}.dbo.Orders') AND g.minor_id = 0
            ORDER BY p.name
            """, cancellationToken));
        var roles = JsonSerializer.Serialize(await admin.QueryAsync($"""
            SELECT r.name FROM {db}.sys.database_role_members AS m
            JOIN {db}.sys.database_principals AS r ON r.principal_id = m.role_principal_id
            JOIN {db}.sys.database_principals AS p ON p.principal_id = m.member_principal_id
            WHERE p.name = N'sales_app' ORDER BY r.name
            """, cancellationToken));
        var orders = await admin.ScalarAsync($"SELECT COUNT(*) FROM {db}.dbo.Orders", cancellationToken);

        return new(principals, owner, grants, roles, orders);
    }

    public void AssertDetached()
    {
        Assert.Equal("""
            [["no_login_control","SQL_USER","NONE"],["report_reader","SQL_USER","NONE"],["sales_app","SQL_USER","NONE"]]
            """, Principals);
        Assert.Equal("report_reader", Owner);
        Assert.Equal("""[["no_login_control","GRANT"],["sales_app","GRANT"]]""", Grants);
        Assert.Equal("""[["order_readers"]]""", Roles);
        Assert.Equal(3, Orders);
    }
}
