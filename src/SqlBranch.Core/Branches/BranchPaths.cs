using SqlBranch.Core.Containers;

namespace SqlBranch.Core.Branches;

/// <summary>Where a branch lives in the pool, as the engine container sees it (design D3).</summary>
public static class BranchPaths
{
    public const string BranchesRoot = ContainerLabels.PoolMountPath + "/branches";

    public static string Branch(string name) => $"{BranchesRoot}/{name}";

    public static string Data(string name) => $"{Branch(name)}/data";

    /// <summary>Where a reset prepares the new files before it drops the old database (design D5).</summary>
    public static string Next(string name) => $"{Branch(name)}/next";

    public static string Json(string name) => $"{Branch(name)}/branch.json";
}
