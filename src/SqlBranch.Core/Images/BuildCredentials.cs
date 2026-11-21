using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlBranch.Core.Engine;

namespace SqlBranch.Core.Images;

/// <summary>
/// <c>/pool/build/&lt;id&gt;/credential.json</c> (design D4): the name of the storage credential a URL build made and,
/// once it exists, the <c>credential_id</c> and <c>create_date</c> read back from <c>sys.credentials</c>. It never
/// holds the secret. A marker without an id is an intent marker: nothing proves the credential of that name is ours.
/// </summary>
/// <param name="Name">The credential's name.</param>
/// <param name="CredentialId">The engine's id of the credential, or null for an intent marker.</param>
/// <param name="CreateDate">The credential's creation time as ISO 8601 text, or null for an intent marker.</param>
public sealed record CredentialMarker(string Name, int? CredentialId = null, string? CreateDate = null)
{
    public const string FileName = "credential.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>True when the credential's id and date were read back.</summary>
    public bool IsFull => CredentialId is not null && CreateDate is not null;

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a marker from base64 of its JSON, or returns null when it is not one.</summary>
    public static CredentialMarker? FromBase64(string encoded)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<CredentialMarker>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), Options);
            return marker is { Name.Length: > 0 } ? marker : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>What the engine's <c>sys.credentials</c> and the pool's markers say about build credentials.</summary>
internal static class BuildCredentials
{
    /// <summary>The prefix of each marker line the listing prints.</summary>
    public const string Tag = "SQLBRANCH_CRED|";

    /// <summary>The shell that prints one tagged line per marker: build id and base64 of the file. Part of the status listing too.</summary>
    public const string ListMarkersLoop =
        "for d in /pool/build/*/; do [ -f \"${d}credential.json\" ] || continue; " +
        "echo \"SQLBRANCH_CRED|$(basename \"$d\")|$(base64 -w0 \"${d}credential.json\")\"; done";

    public const string FindSql =
        "SELECT credential_id, CONVERT(varchar(33), create_date, 126) AS create_date FROM sys.credentials WHERE name = @name";

    public const string NameParameter = "name";

    /// <summary>Parses the marker lines of <see cref="ListMarkersLoop"/>.</summary>
    public static IReadOnlyList<(string BuildId, CredentialMarker Marker)> ParseMarkers(string output)
    {
        var markers = new List<(string, CredentialMarker)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(Tag, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length >= 3 && parts[1].Length > 0 && CredentialMarker.FromBase64(parts[2]) is { } marker)
            {
                markers.Add((parts[1], marker));
            }
        }

        return markers;
    }

    /// <summary>The id and creation time of the credential with this name, or null when there is none.</summary>
    public static async Task<(int Id, string CreateDate)?> FindAsync(IEngineSession session, string name, CancellationToken cancellationToken)
    {
        var rows = await session.QueryRowsAsync(FindSql, new Dictionary<string, object?> { [NameParameter] = name }, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        return (Convert.ToInt32(rows[0]["credential_id"], CultureInfo.InvariantCulture),
            Convert.ToString(rows[0]["create_date"], CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <summary>True when the engine has a credential that is exactly the one the marker recorded.</summary>
    public static async Task<bool> StillExistsAsync(IEngineSession session, CredentialMarker marker, CancellationToken cancellationToken)
    {
        if (!marker.IsFull)
        {
            return false;
        }

        var found = await FindAsync(session, marker.Name, cancellationToken).ConfigureAwait(false);
        return found is { } row && row.Id == marker.CredentialId && string.Equals(row.CreateDate, marker.CreateDate, StringComparison.Ordinal);
    }

    public static string DropSql(string name) => "DROP CREDENTIAL " + Quoting.Identifier(name);

    /// <summary>
    /// The one statement that holds the secret: <c>CREATE CREDENTIAL</c> takes no variables. The name is a quoted identifier
    /// and every quote of the secret is doubled.
    /// </summary>
    public static string CreateSql(string name, string identity, string secret) =>
        $"CREATE CREDENTIAL {Quoting.Identifier(name)} WITH IDENTITY = '{identity}', SECRET = N'{secret.Replace("'", "''", StringComparison.Ordinal)}'";
}
