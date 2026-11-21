using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Output;
using SqlBranch.Core.Tests.Configuration;

namespace SqlBranch.Core.Tests.Images;

public sealed class BacpacPackageTests
{
    private static readonly XNamespace Ns = BacpacPackage.ModelNamespace;
    private static readonly CancellationToken None = CancellationToken.None;
    [Theory]
    [InlineData("text")]
    [InlineData("dacpac")]
    [InlineData("archive")]
    [InlineData("truncated")]
    [InlineData("damaged entry")]
    [InlineData("DTD")]
    [InlineData("bad checksum")]
    public async Task Refuses_non_bacpac_or_damaged_bytes(string kind)
    {
        using var tree = new TempTree();
        var path = tree.File("shop.bacpac", "not a package");
        switch (kind)
        {
            case "dacpac":
                Write(path, isBacpac: false);
                break;
            case "archive":
                File.Delete(path);
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    Add(zip, "manifest.json", "{}");
                }
                break;
            case "truncated":
                Write(path);
                using (var file = File.OpenWrite(path))
                {
                    file.SetLength(file.Length - 50);
                }
                break;
            case "damaged entry":
                Write(path);
                var bytes = await File.ReadAllBytesAsync(path);
                var position = bytes.AsSpan().IndexOf("fixture bytes"u8);
                Assert.True(position >= 0);
                bytes[position] ^= 1;
                await File.WriteAllBytesAsync(path, bytes);
                break;
            case "DTD":
                Write(path, modelPrefix: "<!DOCTYPE DataSchemaModel [<!ENTITY x SYSTEM 'file:///tmp/private'>]>");
                break;
            case "bad checksum":
                Write(path, User("report_reader", "LoginIdentifierScript"), checksum: new string('0', 64));
                break;
        }
        var original = await File.ReadAllBytesAsync(path);
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => BacpacPackage.ValidateAsync(path, CancellationToken.None));
        Assert.Equal(ErrorCodes.BacpacInvalid, error.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Reports_compressed_size_and_skips_rewriting_built_in_contained_and_without_login_users()
    {
        using var tree = new TempTree();
        var path = tree.File("valid", "");
        Write(path, User("contained", "Password") + User("dbo", "LoginIdentifierScript") +
            User("guest", "LoginIdentifierScript") + User("sys", "LoginIdentifierScript") +
            User("INFORMATION_SCHEMA", "LoginIdentifierScript") + User("control", "IsWithoutLogin"));
        Assert.Equal((new FileInfo(path).Length, false), await BacpacPackage.ValidateAsync(path, None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Detaches_native_annotation_and_azure_relationship_and_keeps_database_objects(bool checksum)
    {
        using var scratch = NewScratch();
        const string databaseObjects = """
            <Element Type="SqlPermissionStatement" Name="[Grant.Select.Object].[app]">
                <Relationship Name="Grantee"><Entry><References Name="[app]" Disambiguator="3" /></Entry></Relationship>
            </Element>
            <Element Type="SqlRoleMembership">
                <Relationship Name="Member"><Entry><References Name="[app]" Disambiguator="3" /></Entry></Relationship>
            </Element>
            <Element Type="SqlSchema" Name="[reports]">
                <Relationship Name="Authorizer"><Entry><References Name="[app]" Disambiguator="3" /></Entry></Relationship>
            </Element>
            """;
        await StageAsync(scratch, """
            <Element Type="SqlUser" Name="[native]">
                <Property Name="LoginIdentifierScript"><Value><![CDATA[different_login]]></Value></Property>
                <Property Name="AuthenticationType" Value="1" />
                <Relationship Name="Login"><Entry>
                    <Annotation Type="PersistedResolvableAnnotation" Name="[different_login]">
                        <Property Name="TargetTypeStorage" Value="SqlLogin" />
                    </Annotation>
                </Entry></Relationship>
            </Element>
            <Element Type="SqlLogin" Name="[app]" Disambiguator="3" />
            <Element Type="SqlLogin" Name="[standalone]" />
            <Element Type="SqlUser" Name="[app]" Disambiguator="3">
                <Property Name="AuthenticationType" Value="1" />
                <Relationship Name="Login"><Entry><References Name="[app]" Disambiguator="3" /></Entry></Relationship>
            </Element>
            <Element Type="SqlUser" Name="[dbo]"><Property Name="LoginIdentifierScript" Value="dbo" /></Element>
            <Element Type="SqlUser" Name="[contained]"><Property Name="Password" Value="fixture" /></Element>
            <Element Type="SqlUser" Name="[control]"><Property Name="IsWithoutLogin" Value="True" /></Element>
            <Element Type="SqlUserDefinedServerRole" Name="[server_role]"/>
            <Element Type="SqlServerRoleMembership" Name="[server_member]">
                <Relationship Name="Member"><Entry><References Name="[app]" Disambiguator="3" /></Entry></Relationship>
                <Relationship Name="Role"><Entry><References Name="[server_role]" /></Entry></Relationship>
            </Element>
            <Element Type="SqlPermissionStatement" Name="[server_permission]">
                <Relationship Name="Grantee">
                    <Entry><References Name="[app]" Disambiguator="3" /></Entry>
                    <Entry><References Name="[standalone]" /></Entry>
                </Relationship>
                <Relationship Name="SecuredObject"><Entry><References Name="[server_role]" /></Entry></Relationship>
            </Element>
            """ + databaseObjects, checksum ? "compute" : null);

        var result = await BacpacPackage.RewriteAsync(scratch, None);

        Assert.Equal(["app", "native"], result.Users);
        Assert.Equal(2, result.RemovedLogins);
        Assert.Equal(2, result.RemovedServerObjects);
        Assert.False((await BacpacPackage.ValidateAsync(scratch.RewrittenPackage, None)).NeedsRewrite);
        using var original = ZipFile.OpenRead(scratch.Package);
        using var rewritten = ZipFile.OpenRead(scratch.RewrittenPackage);
        foreach (var name in new[] { "[Content_Types].xml", "Data/dbo.Items/TableData-000-00000.BCP" })
        {
            Assert.Equal(Read(original, name), Read(rewritten, name));
        }
        var elements = Elements(rewritten);
        var oldElements = Elements(original);
        foreach (var name in new[] { "[app]", "[native]" })
        {
            var user = Assert.Single(elements, e => (string?)e.Attribute("Name") == name);
            Assert.True(XNode.DeepEquals(new XElement(Ns + "Property",
                new XAttribute("Name", "IsWithoutLogin"), new XAttribute("Value", "True")), Assert.Single(user.Elements())));
        }
        Assert.DoesNotContain(elements, e => (string?)e.Attribute("Type") is "SqlLogin" or "SqlServerRoleMembership" ||
            (string?)e.Attribute("Name") == "[server_permission]");
        foreach (var preserved in oldElements.Where(e => (string?)e.Attribute("Type") is "SqlSchema" or "SqlRoleMembership" ||
            (string?)e.Attribute("Name") is "[Grant.Select.Object].[app]" or "[contained]" or "[dbo]" or "[control]"))
        {
            Assert.Contains(elements, e => XNode.DeepEquals(e, preserved));
        }
        var rewrittenChecksums = XDocument.Parse(Read(rewritten, "Origin.xml")).Descendants(Ns + "Checksum")
            .ToDictionary(e => (string)e.Attribute("Uri")!, e => e.Value);
        Assert.Equal("retain this checksum", rewrittenChecksums["/other.xml"]);
        if (checksum)
        {
            using var model = rewritten.GetEntry("model.xml")!.Open();
            Assert.Equal(Convert.ToHexString(await SHA256.HashDataAsync(model)), rewrittenChecksums["/model.xml"]);
        }
        else
        {
            Assert.Single(rewrittenChecksums);
        }
    }

    [Fact]
    public async Task Detaches_case_variant_of_builtin_user()
    {
        using var scratch = NewScratch();
        await StageAsync(scratch, User("guest", "LoginIdentifierScript") + User("Guest", "LoginIdentifierScript"));

        var result = await BacpacPackage.RewriteAsync(scratch, None);

        Assert.Equal("Guest", Assert.Single(result.Users));
        using var zip = ZipFile.OpenRead(scratch.RewrittenPackage);
        var users = Elements(zip).ToDictionary(e => (string)e.Attribute("Name")!);
        Assert.Equal("IsWithoutLogin", (string?)Assert.Single(users["[Guest]"].Elements()).Attribute("Name"));
        Assert.Equal("LoginIdentifierScript", (string?)Assert.Single(users["[guest]"].Elements()).Attribute("Name"));
    }

    [Theory]
    [InlineData("SqlPermissionStatement", "[Grant.Impersonate.User].[auditor].[dbo].[app]", "SecuredObject")]
    [InlineData("SqlPermissionStatement", "[impersonate_grant]", "SecuredObject")]
    [InlineData("SqlProcedure", "[dbo].[run_as_app]", "User")]
    public async Task Preserves_objects_referencing_a_user_that_shares_a_login_identity(string type, string name, string relationship)
    {
        using var scratch = NewScratch();
        var grantee = type == "SqlPermissionStatement"
            ? """<Relationship Name="Grantee"><Entry><References Name="[auditor]" /></Entry></Relationship>"""
            : "";
        var databaseObject = $"""
            <Element Type="{type}" Name="{name}">
                <Relationship Name="{relationship}"><Entry><References Name="[app]" Disambiguator="3" /></Entry></Relationship>
                {grantee}
            </Element>
            """;
        await StageAsync(scratch, """
            <Element Type="SqlLogin" Name="[app]" Disambiguator="3" />
            <Element Type="SqlUser" Name="[app]" Disambiguator="3">
                <Relationship Name="Login"><Entry><References Name="[app]" Disambiguator="3" /></Entry></Relationship>
            </Element>
            <Element Type="SqlLogin" Name="[auditor]" />
            <Element Type="SqlUser" Name="[auditor]">
                <Relationship Name="Login"><Entry><References Name="[auditor]" /></Entry></Relationship>
            </Element>
            """ + databaseObject);

        var result = await BacpacPackage.RewriteAsync(scratch, None);

        Assert.Equal(["app", "auditor"], result.Users);
        Assert.Equal(2, result.RemovedLogins);
        Assert.Equal(0, result.RemovedServerObjects);
        using var original = ZipFile.OpenRead(scratch.Package);
        using var rewritten = ZipFile.OpenRead(scratch.RewrittenPackage);
        var expected = Assert.Single(Elements(original), e => (string?)e.Attribute("Name") == name);
        Assert.Contains(Elements(rewritten), e => XNode.DeepEquals(expected, e));
    }

    [Fact]
    public async Task Removes_server_wide_grant_without_a_securable_reference()
    {
        using var scratch = NewScratch();
        await StageAsync(scratch, """
            <Element Type="SqlLogin" Name="[app]" />
            <Element Type="SqlPermissionStatement" Name="[Grant.ConnectSql.Server].[app].[sa]">
                <Property Name="Permission" Value="1037" />
                <Relationship Name="Grantee"><Entry><References Name="[app]" /></Entry></Relationship>
                <Relationship Name="SecuredObject"><Entry /></Relationship>
            </Element>
            """);

        var result = await BacpacPackage.RewriteAsync(scratch, None);
        Assert.Empty(result.Users);
        Assert.Equal(1, result.RemovedLogins);
        Assert.Equal(1, result.RemovedServerObjects);
        using var zip = ZipFile.OpenRead(scratch.RewrittenPackage);
        Assert.Empty(Elements(zip));
    }

    [Fact]
    public async Task Refuses_server_permission_that_targets_a_removed_login_but_grants_to_another_principal()
    {
        using var scratch = NewScratch();
        await StageAsync(scratch, """
            <Element Type="SqlLogin" Name="[app]" />
            <Element Type="SqlPermissionStatement" Name="[Grant.Impersonate.Login].[other].[sa].[app]">
                <Relationship Name="Grantee"><Entry><References Name="[other]" /></Entry></Relationship>
                <Relationship Name="SecuredObject"><Entry><References Name="[app]" /></Entry></Relationship>
            </Element>
            """);

        var error = await Assert.ThrowsAsync<SqlBranchException>(() => BacpacPackage.RewriteAsync(scratch, None));
        Assert.Equal(ErrorCodes.UnsupportedFeature, error.Code);
        Assert.Contains("SqlPermissionStatement [Grant.Impersonate.Login].[other].[sa].[app]", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(scratch.RewrittenPackage));
    }

    [Theory]
    [InlineData("References", "3", true)]
    [InlineData("References", "4", false)]
    [InlineData("Annotation", "3", true)]
    public async Task Refuses_retained_references_to_the_removed_identity(string reference, string disambiguator, bool matches)
    {
        using var scratch = NewScratch();
        var target = reference == "References"
            ? $"<References Name='[app]' Disambiguator='{disambiguator}' />"
            : $"""
               <Annotation Type="PersistedResolvableAnnotation" Name="[app]" Disambiguator="{disambiguator}">
                   <Property Name="TargetTypeStorage" Value="SqlLogin" />
               </Annotation>
               """;
        await StageAsync(scratch, $"""
            <Element Type="SqlLogin" Name="[app]" Disambiguator="3" />
            <Element Type="SqlCredential" Name="[private_credential]">
                <Relationship Name="Identity"><Entry>{target}</Entry></Relationship>
            </Element>
            """);
        if (matches)
        {
            var error = await Assert.ThrowsAsync<SqlBranchException>(() => BacpacPackage.RewriteAsync(scratch, None));
            Assert.Equal(ErrorCodes.UnsupportedFeature, error.Code);
            Assert.Contains("SqlCredential [private_credential]", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(scratch.RewrittenPackage));
        }
        else
        {
            var result = await BacpacPackage.RewriteAsync(scratch, None);
            Assert.Equal(1, result.RemovedLogins);
            Assert.Equal(0, result.RemovedServerObjects);
            Assert.Empty(result.Users);
            using var zip = ZipFile.OpenRead(scratch.RewrittenPackage);
            Assert.Equal("[private_credential]", (string?)Assert.Single(Elements(zip)).Attribute("Name"));
        }
    }

    [Theory]
    [InlineData("AuthenticationType", "3")]
    [InlineData("AuthenticationType", "4")]
    [InlineData("UserType", "2")]
    public async Task Windows_and_external_users_are_unsupported(string property, string value)
    {
        using var tree = new TempTree();
        var path = tree.File("unsupported", "");
        Write(path, $"""
            <Element Type="SqlUser" Name="[enterprise]"><Property Name="{property}" Value="{value}" /></Element>
            """);
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => BacpacPackage.ValidateAsync(path, None));
        Assert.Equal(ErrorCodes.UnsupportedFeature, error.Code);
        Assert.Contains("[enterprise]", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("IsMappedToWindowsLogin")]
    [InlineData("IsMappedToExternalLogin")]
    public async Task Login_relationship_resolves_non_sql_login_by_identity(string property)
    {
        using var scratch = NewScratch();
        await StageAsync(scratch, $"""
            <Element Type="SqlLogin" Name="[app]" />
            <Element Type="SqlLogin" Name="[app]" Disambiguator="3"><Property Name="{property}" Value="True" /></Element>
            <Element Type="SqlUser" Name="[enterprise]">
                <Relationship Name="Login"><Entry><References Name="[app]" Disambiguator="3" /></Entry></Relationship>
            </Element>
            """);
        var error = await Assert.ThrowsAsync<SqlBranchException>(() => BacpacPackage.RewriteAsync(scratch, None));
        Assert.Equal(ErrorCodes.UnsupportedFeature, error.Code);
        Assert.Contains("[enterprise]", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(scratch.RewrittenPackage));
    }

    [Fact]
    public async Task User_with_same_name_and_different_disambiguator_does_not_resolve_a_login_reference()
    {
        using var scratch = NewScratch();
        await StageAsync(scratch, """
            <Element Type="SqlLogin" Name="[app]" Disambiguator="3" />
            <Element Type="SqlUser" Name="[app]" Disambiguator="4" />
            <Element Type="SqlProcedure" Name="[dbo].[run_as_app]">
                <Relationship Name="User"><Entry><References Name="[app]" Disambiguator="3" /></Entry></Relationship>
            </Element>
            """);

        var error = await Assert.ThrowsAsync<SqlBranchException>(() => BacpacPackage.RewriteAsync(scratch, None));
        Assert.Equal(ErrorCodes.UnsupportedFeature, error.Code);
        Assert.Contains("references a removed login", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("IsMappedToWindowsLogin", """<Value><![CDATA[ [DOMAIN\app]]login] ]]></Value>""", "")]
    [InlineData("IsMappedToExternalLogin", "", """ Value="&quot;DOMAIN\app]login&quot;" """)]
    [InlineData("IsMappedToExternalLogin", "<Value>simple_login</Value>", "", "[simple_login]")]
    public async Task Login_identifier_script_resolves_non_sql_login_by_login_name(string property, string value, string attribute,
        string login = @"[DOMAIN\app]]login]")
    {
        using var scratch = NewScratch();
        await StageAsync(scratch, $"""
            <Element Type="SqlLogin" Name="{login}"><Property Name="{property}" Value="True" /></Element>
            <Element Type="SqlUser" Name="[enterprise]">
                <Property Name="LoginIdentifierScript"{attribute}>{value}</Property>
            </Element>
            """);

        var error = await Assert.ThrowsAsync<SqlBranchException>(() => BacpacPackage.RewriteAsync(scratch, None));
        Assert.Equal(ErrorCodes.UnsupportedFeature, error.Code);
        Assert.Contains("[enterprise]", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(scratch.RewrittenPackage));
    }

    [Fact]
    public async Task Reports_names_with_bracket_escapes_controls_and_length_limits()
    {
        using var scratch = NewScratch();
        await StageAsync(scratch, User("app]]&#xA;" + new string('x', 150), "LoginIdentifierScript"));
        var result = await BacpacPackage.RewriteAsync(scratch, None);
        Assert.Equal("app]?" + new string('x', 123), Assert.Single(result.Users));
    }

    private static BacpacScratch NewScratch() => new("l" + Guid.NewGuid().ToString("N"), "unit");

    private static async Task StageAsync(BacpacScratch scratch, string model, string? checksum = null)
    {
        using var tree = new TempTree();
        var path = tree.File("original.bacpac", "");
        Write(path, model, checksum: checksum);
        await scratch.CopyAsync(path, None);
        Assert.True((await BacpacPackage.ValidateAsync(scratch.Package, None)).NeedsRewrite);
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static XElement[] Elements(ZipArchive zip) =>
        XDocument.Parse(Read(zip, "model.xml")).Descendants(Ns + "Element").ToArray();

    internal static string User(string name, string property) =>
        $"<Element Type='SqlUser' Name='[{name}]'><Property Name='{property}' Value='fixture'/></Element>";

    internal static void Write(string path, string model = "", bool isBacpac = true, string modelPrefix = "", string? checksum = null)
    {
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'/>");
        var modelXml = $"{modelPrefix}<DataSchemaModel xmlns='{BacpacPackage.ModelNamespace}'><Model>{model}</Model></DataSchemaModel>";
        if (checksum == "compute")
        {
            checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelXml)));
        }
        var modelChecksum = checksum is null ? "" : $"<Checksum Uri='/model.xml'>{checksum}</Checksum>";
        Add(zip, "Origin.xml", $"<DacOrigin xmlns='{BacpacPackage.ModelNamespace}'><PackageProperties><ContainsExportedData>{(isBacpac ? "true" : "false")}</ContainsExportedData></PackageProperties><Checksums>{modelChecksum}<Checksum Uri='/other.xml'>retain this checksum</Checksum></Checksums></DacOrigin>");
        Add(zip, "model.xml", modelXml);
        Add(zip, "Data/dbo.Items/TableData-000-00000.BCP", "fixture bytes");
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.NoCompression).Open());
        writer.Write(content);
    }
}
