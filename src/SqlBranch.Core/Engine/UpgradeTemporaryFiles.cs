using SqlBranch.Core.Images;

namespace SqlBranch.Core.Engine;

/// <summary>Only the four fixed temporary-file locations owned by a pool upgrade (D11).</summary>
internal static class UpgradeTemporaryFiles
{
    /// <summary>The temporary credentials file. Only the upgrade writes it, so status reports it on any pool.</summary>
    internal const string Credentials = "/pool/engine/credentials.json.new";

    // No recursive search or broad *.new cleanup: unrelated files, staging and dot-folders are left alone.
    internal const string ListingScript =
        "for f in /pool/engine/credentials.json.new /pool/images/*/catalog.json.new /pool/images/.ids/*.new /pool/branches/*/branch.json.new; do " +
        "[ -f \"$f\" ] || continue; echo \"SQLBRANCH_UPGRADETEMP|$f\"; done; ";

    /// <summary>The start of every cleanup script, so a fake runtime can recognize it.</summary>
    internal const string CleanupPrefix = "set -e; rm -f -- ";

    /// <summary>
    /// Whether a listed file belongs to a dead upgrade. The other three names are also written by ordinary 0.3 writers
    /// (a create, a reset, a build), so they count only while the credentials say the upgrade is running (D11).
    /// </summary>
    internal static bool IsUpgradeFile(string path, bool upgradeUnfinished) => upgradeUnfinished || path == Credentials;

    /// <summary>Removes exactly the temporary files of the locked images and branches, and the credentials file.</summary>
    internal static string CleanupScript(IEnumerable<string> images, IEnumerable<string> branches)
    {
        var paths = new List<string> { Credentials };
        foreach (var image in images)
        {
            paths.Add($"/pool/images/{image}/catalog.json.new");
            paths.Add($"/pool/images/.ids/{image}.new");
        }
        paths.AddRange(branches.Select(b => $"/pool/branches/{b}/branch.json.new"));
        return CleanupPrefix + string.Join(' ', paths.Select(Quoting.Shell)) + "; true";
    }
}
