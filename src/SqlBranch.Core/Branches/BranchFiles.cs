using System.Globalization;
using System.Text;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;

namespace SqlBranch.Core.Branches;

/// <summary>The cloning of an image's files, shared by <c>branch create</c> and <c>branch reset</c>.</summary>
internal static class BranchFiles
{
    /// <summary>
    /// One <c>cp</c> and one <c>chmod 640</c> for each file of the image, into <paramref name="targetDirectory"/>: <c>cp --reflink=always</c>
    /// in thin mode, a plain <c>cp</c> in copy mode. The image's files are read-only and so would a clone be, so the branch's own files
    /// are made writable for the engine.
    /// </summary>
    public static string CloneCommands(ImageManifest manifest, string image, CloneMode mode, string targetDirectory, string imageDirectory = ".")
    {
        var script = new StringBuilder();
        var cp = mode == CloneMode.Thin ? "cp --reflink=always --" : "cp --";
        foreach (var file in manifest.Files)
        {
            RequireSafeFileName(file.FileName, image);
            var from = Quoting.Shell($"{ImagePaths.Data(image, imageDirectory)}/{file.FileName}");
            var to = Quoting.Shell($"{targetDirectory}/{file.FileName}");
            script.Append(CultureInfo.InvariantCulture, $"{cp} {from} {to}; chmod 640 {to}; ");
        }

        return script.ToString();
    }

    /// <summary>The <c>FILENAME</c> list of <c>CREATE DATABASE ... FOR ATTACH</c> for the files in <paramref name="directory"/>.</summary>
    public static string AttachFiles(ImageManifest manifest, string directory) =>
        string.Join(", ", manifest.Files.Select(f => $"(FILENAME = {Quoting.Literal($"{directory}/{f.FileName}")})"));

    public static void RequireSafeFileName(string fileName, string image)
    {
        if (fileName.Length == 0 || fileName.Contains('/', StringComparison.Ordinal) || fileName.StartsWith('.'))
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"The manifest of the image '{image}' names a file that is not a plain file name, so no branch can be made from it.");
        }
    }
}
