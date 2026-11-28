using SqlBranch.Core.Containers;

namespace SqlBranch.Core.Images;

/// <summary>Where an image and its build live in the pool, as the engine container sees them (design D3).</summary>
public static class ImagePaths
{
    public const string ImagesRoot = ContainerLabels.PoolMountPath + "/images";

    public const string BuildRoot = ContainerLabels.PoolMountPath + "/build";

    public const string ImportRoot = ContainerLabels.PoolMountPath + "/import";

    public static string Image(string name) => $"{ImagesRoot}/{name}";

    public static string Data(string name) => $"{Image(name)}/data";

    /// <summary>The data folder of the version whose files are in <paramref name="directory"/> of the image folder (<c>.</c> or <c>versions/&lt;id&gt;</c>).</summary>
    public static string Data(string name, string directory) => directory == "." ? Data(name) : $"{Image(name)}/{directory}/data";

    public static string Manifest(string name) => $"{Image(name)}/manifest.json";

    public static string Build(string id) => $"{BuildRoot}/{id}";

    /// <summary>Size of the space reserve a build keeps in its folder.</summary>
    public const int BuildReserveMiB = 64;

    /// <summary>
    /// The space reserve in a build folder. A seed script or a restore can fill the pool to the last block, and then
    /// SQL Server cannot grow master's log, so the cleanup cannot even drop the build database. The cleanup deletes this
    /// file first to get that room back.
    /// </summary>
    public static string BuildReserve(string buildDirectory) => $"{buildDirectory}/.reserve";

    /// <summary>
    /// Shell text that makes the reserve file. It never fails the step: a pool with no room for it fails the build at
    /// its own space check or write instead, and a reserve that could not be made is not left half written.
    /// </summary>
    internal static string MakeBuildReserve(string buildDirectory)
    {
        var file = Quoting.Shell(BuildReserve(buildDirectory));
        return $"{{ fallocate -l {BuildReserveMiB}M {file} 2>/dev/null || dd if=/dev/zero of={file} bs=1M count={BuildReserveMiB} 2>/dev/null || rm -f {file}; true; }}";
    }

    /// <summary>The name of the temporary database of a build.</summary>
    public static string BuildDatabase(string id) => Engine.StatusReporter.BuildPrefix + id;

    /// <summary>
    /// The engine's path for a backup the user keeps under the pool's import folder, or null when the file is somewhere else.
    /// Only a pool that is a directory on the client's own file system can be looked at this way.
    /// </summary>
    public static string? ImportPathOf(string hostPath, Pools.PoolLocation pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (pool.Kind == Pools.PoolKind.Volume)
        {
            return null;
        }

        var root = pool.Source.Replace('\\', '/').TrimEnd('/') + "/import/";
        var path = hostPath.Replace('\\', '/');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root, comparison) || path.Length == root.Length || path[root.Length..].Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return ImportRoot + "/" + path[root.Length..];
    }
}

/// <summary>Quoting for the two languages the build writes: T-SQL and the shell.</summary>
internal static class Quoting
{
    /// <summary>A T-SQL identifier in brackets, with <c>]</c> doubled.</summary>
    public static string Identifier(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>A T-SQL Unicode string literal, with the quote doubled.</summary>
    public static string Literal(string text) => "N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>A POSIX shell word in single quotes.</summary>
    public static string Shell(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
