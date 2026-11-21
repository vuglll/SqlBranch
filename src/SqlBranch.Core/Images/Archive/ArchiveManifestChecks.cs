using System.Globalization;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images.Archive;

/// <summary>
/// The checks of "Archive manifests are checked" (design D7). An archive's manifest is untrusted: it names files that become paths in
/// the pool and words in shell scripts, so it is checked as soon as the first entry is read, before any <c>docker</c> command.
/// </summary>
public static class ArchiveManifestChecks
{
    /// <summary>
    /// Returns the name the image will get (<paramref name="asName"/>, already known to be valid, or the manifest's own name) or throws
    /// exit 1 <c>archive_invalid</c> naming the first thing wrong.
    /// </summary>
    public static string Check(ImageManifest manifest, string? asName)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var target = asName ?? manifest.Name;
        if (asName is null && NameRules.Problem(manifest.Name, forBranch: false) is { } nameProblem)
        {
            throw ArchiveHeadJson.Invalid($"its image name '{Printable(manifest.Name)}' cannot be used. {nameProblem} Give the image a valid name with --as.");
        }

        if (manifest.Files is null || manifest.Files.Count == 0)
        {
            throw ArchiveHeadJson.Invalid("its manifest lists no files.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || file.FileName is null)
            {
                throw ArchiveHeadJson.Invalid("its manifest lists a file without a name.");
            }

            if (PlainNameProblem(file.FileName) is { } problem)
            {
                throw ArchiveHeadJson.Invalid($"its manifest lists the file name '{Printable(file.FileName)}', which {problem}.");
            }

            if (!seen.Add(file.FileName))
            {
                throw ArchiveHeadJson.Invalid($"its manifest lists the file name '{Printable(file.FileName)}' more than once (names are compared ignoring case).");
            }

            if (file.Type is not ("data" or "log"))
            {
                throw ArchiveHeadJson.Invalid($"its manifest lists '{Printable(file.FileName)}' with the type '{Printable(file.Type)}'; only data and log are known.");
            }

            if (file.SizeBytes < 0)
            {
                throw ArchiveHeadJson.Invalid($"its manifest lists '{Printable(file.FileName)}' with a negative size.");
            }

            try
            {
                total = checked(total + file.SizeBytes);
            }
            catch (OverflowException)
            {
                throw ArchiveHeadJson.Invalid("the file sizes in its manifest add up to more than a 64-bit integer holds.");
            }
        }

        if (!Version.TryParse(manifest.EngineVersion, out _))
        {
            throw ArchiveHeadJson.Invalid($"its manifest has the engine version '{Printable(manifest.EngineVersion)}', which is not a version number.");
        }

        return target;
    }

    /// <summary>Why a file name is not a plain name (a name, not a path), or null when it is.</summary>
    private static string? PlainNameProblem(string name)
    {
        if (name.Length == 0)
        {
            return "is empty";
        }

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return "holds a path separator";
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return "holds '..'";
        }

        if (name.StartsWith('.'))
        {
            return "starts with a dot";
        }

        if (name.Any(char.IsControl))
        {
            return "holds a control character";
        }

        return null;
    }

    /// <summary>A text for a message: control characters become a visible mark, and a very long text is cut.</summary>
    internal static string Printable(string? text)
    {
        var value = text ?? string.Empty;
        if (value.Length > 80)
        {
            value = value[..80] + "...";
        }

        return string.Concat(value.Select(c => char.IsControl(c) ? '?' : c));
    }

    internal static string Count(long number) => number.ToString(CultureInfo.InvariantCulture);
}
