using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;

namespace SqlBranch.Core.Branches;

/// <summary>
/// Checks that <c>branch create</c> and <c>branch reset</c> share and run before they make a file (design D5, D6).
/// </summary>
public static class BranchGuards
{
    /// <summary>
    /// <c>requireThin</c> turns a copy-mode pool into exit 3 <c>thin_required</c> for a branch that is not a
    /// <c>--copy</c> branch. A branch the user asked to be a full copy is exempt in both clone modes, because the
    /// copy is what was asked for. <paramref name="explicitCopy"/> is true for <c>create --copy</c> and for a reset of a branch
    /// whose record says it was created with it.
    /// </summary>
    public static void RequireThin(bool requireThin, CloneMode poolMode, bool explicitCopy)
    {
        if (requireThin && poolMode == CloneMode.Copy && !explicitCopy)
        {
            throw new SqlBranchException(ErrorCodes.ThinRequired,
                "requireThin is on and the pool cannot share blocks, so its clone mode is copy and branches would be full copies. " +
                "Move the pool to a file system that can clone files, or turn requireThin off. 'sqlbranch branch create --copy' asks for a copy on purpose and is allowed.");
        }
    }

    /// <summary>
    /// A copy needs room for a full copy of the image's files: exit 1 <c>pool_full</c> naming the space needed and the
    /// space available, before any file is made. A thin clone needs no check, because it shares the image's blocks.
    /// </summary>
    public static Task RequireSpaceForCopyAsync(
        IContainerRuntime runtime, string engineName, CloneMode effectiveMode, long imageBytes, string what, CancellationToken cancellationToken) =>
        effectiveMode == CloneMode.Copy
            ? PoolFreeSpace.RequireAsync(runtime, engineName, imageBytes, what, cancellationToken)
            : Task.CompletedTask;
}
