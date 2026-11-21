using SqlBranch.Core.Containers;

namespace SqlBranch.Core.Pools;

/// <summary>What a pool is.</summary>
public enum PoolKind
{
    /// <summary>A directory on the container host, used as it is.</summary>
    Directory,

    /// <summary>The Docker volume <c>sqlbranch-pool</c>.</summary>
    Volume,

    /// <summary>The mount of the XFS pool file that SqlBranch created.</summary>
    ManagedMount,
}

/// <summary>Whether the pool can share blocks between files.</summary>
public enum CloneMode
{
    /// <summary>A clone shares blocks and costs almost no space.</summary>
    Thin,

    /// <summary>A clone copies every byte.</summary>
    Copy,
}

/// <summary>Where a pool is, in the terms a container needs to mount it.</summary>
public sealed record PoolLocation(PoolKind Kind, string Source)
{
    /// <summary>The mount that puts this pool at <c>/pool</c>.</summary>
    public MountSpec ToMount(bool readOnly = false) =>
        new(Kind == PoolKind.Volume ? MountKind.Volume : MountKind.Bind, Source, ContainerLabels.PoolMountPath, readOnly);

    /// <summary>For messages: the directory, the mount point or the volume name.</summary>
    public override string ToString() => Kind == PoolKind.Volume ? $"Docker volume {Source}" : Source;
}

/// <summary>The cloning mode as printed in output and stored in files.</summary>
public static class CloneModeNames
{
    public static string Of(CloneMode mode) => mode == CloneMode.Thin ? "thin" : "copy";
}
