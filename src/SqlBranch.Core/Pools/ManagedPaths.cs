using System.Text.RegularExpressions;

namespace SqlBranch.Core.Pools;

/// <summary>
/// The fixed paths of the managed pool on the container host. They are
/// constants unless a test gives another root under the same parent, and they
/// are never built from user input.
/// </summary>
public sealed partial class ManagedPaths
{
    /// <summary>The only place on the container host SqlBranch's helpers may touch.</summary>
    public const string DefaultRoot = "/var/lib/sqlbranch";

    [GeneratedRegex(@"^/var/lib/sqlbranch(/[A-Za-z0-9][A-Za-z0-9._-]*)*$")]
    private static partial Regex AllowedRoot();

    /// <summary>The default paths.</summary>
    public static ManagedPaths Default { get; } = new();

    public ManagedPaths(string root = DefaultRoot)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!AllowedRoot().IsMatch(root))
        {
            throw new ArgumentException($"The managed pool root must be {DefaultRoot} or a folder under it, not '{root}'.", nameof(root));
        }

        Root = root;
    }

    /// <summary>The folder the helpers mount.</summary>
    public string Root { get; }

    /// <summary>The XFS pool file.</summary>
    public string PoolFile => Root + "/pool.xfs";

    /// <summary>Where the pool file is mounted.</summary>
    public string MountPoint => Root + "/mnt";

    /// <summary>The pool as a container mounts it.</summary>
    public PoolLocation Location => new(PoolKind.ManagedMount, MountPoint);
}
