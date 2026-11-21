using System.Xml.Linq;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.LogicalSources;

internal sealed record BacpacDetachment(IReadOnlyList<string> Users, int RemovedLogins, int RemovedServerObjects)
{
    public static BacpacDetachment Empty { get; } = new([], 0, 0);
}

internal static class BacpacModelRewriter
{
    private static readonly XNamespace Ns = BacpacPackage.ModelNamespace;
    private static readonly HashSet<string> BuiltInUsers = new(StringComparer.Ordinal)
        { "dbo", "guest", "sys", "INFORMATION_SCHEMA" };

    // DacFx uses both parts to refer to elements that can have the same SQL name.
    private readonly record struct ModelId(string? Name, string? Disambiguator)
    {
        public static ModelId Of(XElement element) => new(Attr(element, "Name"), Attr(element, "Disambiguator"));
    }

    public static bool IsMappedUser(XElement user)
    {
        var name = UserName(user);
        if (BuiltInUsers.Contains(name))
        {
            return false;
        }

        if (Property(user, "AuthenticationType") is "3" or "4" || Property(user, "UserType") is "1" or "2")
        {
            throw Unsupported(user, "uses Windows or external-provider authentication");
        }

        return Relationships(user, "Login").Any() || FindProperty(user, "LoginIdentifierScript") is not null;
    }

    public static BacpacDetachment Detach(XElement model, CancellationToken token)
    {
        var elements = model.Elements(Ns + "Element").ToArray();
        var types = elements.ToLookup(ModelId.Of, e => Attr(e, "Type"));
        var logins = elements.Where(e => Attr(e, "Type") == "SqlLogin").ToArray();
        var loginIds = logins.Select(ModelId.Of).ToHashSet();
        var nonSqlLogins = logins.Where(e => IsTrue(Property(e, "IsMappedToWindowsLogin")) ||
            IsTrue(Property(e, "IsMappedToExternalLogin"))).Select(ModelId.Of).ToHashSet();
        var nonSqlLoginNames = nonSqlLogins.Select(id => id.Name).OfType<string>()
            .Select(IdentifierName).ToHashSet(StringComparer.Ordinal);
        var detached = new List<string>();
        foreach (var element in elements)
        {
            token.ThrowIfCancellationRequested();
            if (Attr(element, "Type") != "SqlUser" || !IsMappedUser(element))
            {
                continue;
            }

            var mapping = Relationships(element, "Login").ToArray();
            var script = FindProperty(element, "LoginIdentifierScript");
            var scriptIdentifier = Attr(script, "Value") ?? script?.Element(Ns + "Value")?.Value;
            if (mapping.SelectMany(References).Any(r => RefersToLogin(r, nonSqlLogins)) ||
                (mapping.Length == 0 && scriptIdentifier is not null &&
                    nonSqlLoginNames.Contains(IdentifierName(scriptIdentifier.Trim()))))
            {
                throw Unsupported(element, "uses Windows or external-provider authentication");
            }

            mapping.Remove();
            element.Elements(Ns + "Property").Where(p => Attr(p, "Name") is
                "AuthenticationType" or "LoginIdentifierScript" or "IsWithoutLogin").Remove();
            element.AddFirst(new XElement(Ns + "Property", new XAttribute("Name", "IsWithoutLogin"),
                new XAttribute("Value", "True")));
            detached.Add(Safe(UserName(element)));
        }

        logins.Remove();
        var removedServerObjects = 0;
        foreach (var element in model.Elements(Ns + "Element").ToArray())
        {
            token.ThrowIfCancellationRequested();
            var type = Attr(element, "Type");
            var membership = type == "SqlServerRoleMembership" || (type == "SqlRoleMembership" &&
                Relationships(element, "Role").SelectMany(References).Any(r => HasType(r, types, IsServerRole)));
            var permission = type == "SqlServerPermissionStatement" || (type == "SqlPermissionStatement" &&
                IsServerPermission(element, types));
            var principal = membership ? "Member" : permission ? "Grantee" : null;
            if (principal is not null && Relationships(element, principal).SelectMany(References)
                .Any(r => RefersToLogin(r, loginIds)))
            {
                element.Remove();
                removedServerObjects++;
                continue;
            }

            if (References(element).Any(r => RefersToLogin(r, loginIds) &&
                !IsDatabasePrincipalReference(r, types)))
            {
                throw Unsupported(element, "references a removed login");
            }
        }

        detached.Sort(StringComparer.Ordinal);
        return new BacpacDetachment(detached.AsReadOnly(), logins.Length, removedServerObjects);
    }

    private static bool IsServerPermission(XElement permission, ILookup<ModelId, string?> types)
    {
        // DacFx includes the permission class in the first identifier part.
        // A server-wide grant can have an empty SecuredObject entry.
        if (Attr(permission, "Name") is { } name && name.StartsWith('[') && name.IndexOf(']') is var end && end > 0)
        {
            var identifier = name.AsSpan(1, end - 1);
            var separator = identifier.LastIndexOf('.');
            if (separator >= 0)
            {
                return identifier[(separator + 1)..] is "Server" or "Login" or "ServerRole" or "Endpoint" or "AvailabilityGroup";
            }
        }

        return Relationships(permission, "SecuredObject").SelectMany(References).Any(r =>
            HasType(r, types, t => IsServerRole(t) || t is "SqlServer" or "SqlServerOptions" or
                "SqlEndpoint" or "SqlAvailabilityGroup") ||
            (HasType(r, types, t => t == "SqlLogin") && !IsDatabasePrincipalReference(r, types)));
    }

    private static bool IsServerRole(string? type) => type is "SqlUserDefinedServerRole" or "SqlBuiltInServerRole" or "SqlServerRole";

    private static bool HasType(XElement reference, ILookup<ModelId, string?> types, Func<string?, bool> matches) =>
        reference.Name == Ns + "Annotation" ? matches(Property(reference, "TargetTypeStorage"))
        : Attr(reference, "ExternalSource") is null && types[ModelId.Of(reference)].Any(matches);

    // Untyped references can share an identity with a removed login and a retained database principal.
    private static bool IsDatabasePrincipalReference(XElement reference, ILookup<ModelId, string?> types) =>
        reference.Name == Ns + "References" && HasType(reference, types, t => t is "SqlUser" or "SqlRole");

    private static bool RefersToLogin(XElement reference, HashSet<ModelId> ids) =>
        Attr(reference, "ExternalSource") is null && ids.Contains(ModelId.Of(reference)) &&
        (reference.Name == Ns + "References" || Property(reference, "TargetTypeStorage") == "SqlLogin");

    private static IEnumerable<XElement> References(XElement element) =>
        element.Descendants().Where(e => e.Name == Ns + "References" ||
            (e.Name == Ns + "Annotation" && Attr(e, "Type") == "PersistedResolvableAnnotation"));

    private static IEnumerable<XElement> Relationships(XElement element, string name) =>
        element.Elements(Ns + "Relationship").Where(e => Attr(e, "Name") == name);

    private static XElement? FindProperty(XElement element, string name) =>
        element.Elements(Ns + "Property").FirstOrDefault(e => Attr(e, "Name") == name);

    private static string? Property(XElement element, string name) => Attr(FindProperty(element, name), "Value");
    private static string? Attr(XElement? element, string name) => (string?)element?.Attribute(name);
    private static bool IsTrue(string? value) => value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string UserName(XElement user)
    {
        var name = Attr(user, "Name") ?? throw new SqlBranchException(ErrorCodes.BacpacInvalid, "The input is not a valid BACPAC package.");
        return IdentifierName(name);
    }

    private static string IdentifierName(string name)
    {
        if (name.Length < 2)
        {
            return name;
        }

        return (name[0], name[^1]) switch
        {
            ('[', ']') => name[1..^1].Replace("]]", "]", StringComparison.Ordinal),
            ('"', '"') => name[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal),
            _ => name,
        };
    }

    private static string Safe(string value) => string.Concat(value.Take(128).Select(c => char.IsControl(c) ? '?' : c));

    private static SqlBranchException Unsupported(XElement element, string reason) =>
        new(ErrorCodes.UnsupportedFeature,
            $"BACPAC element {Safe(Attr(element, "Type") ?? "<unknown type>")} {Safe(Attr(element, "Name") ?? "<unnamed>")} {reason}.");
}
