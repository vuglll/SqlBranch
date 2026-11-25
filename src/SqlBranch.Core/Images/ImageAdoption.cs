using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

/// <summary>
/// Adoption of an image an older client wrote in the format-1 layout after the upgrade (design D3a, D7): a manifest and no
/// catalog. Only commands that write with the image adopt it (<c>branch create</c>, <c>branch reset</c>); read-only commands never
/// do. Adoption takes the image lock exclusively without waiting, then gives the image its first version ID by the version-ID rule.
/// </summary>
internal static class ImageAdoption
{
    private const string Tag = "SQLBRANCH_ADOPT=";

    /// <summary>The error of a command that must adopt an image whose lock another operation holds.</summary>
    public static SqlBranchException Busy(string name) =>
        new(ErrorCodes.CatalogBusy,
            $"The image '{name}' was written by an older sqlbranch and has to be adopted first, but another command is working on it. " +
            "Nothing was changed. Wait for that command to finish, then run this command again.");

    /// <summary>True when the image folder holds a manifest and no catalog: an image no command has adopted yet.</summary>
    public static async Task<bool> IsUnadoptedAsync(IContainerRuntime runtime, string engineName, string name, CancellationToken cancellationToken)
    {
        var catalog = Quoting.Shell(ImageCatalogFile.Path(name));
        var manifest = Quoting.Shell(ImagePaths.Manifest(name));
        var output = await EngineShell.RunAsync(runtime, engineName,
            $"if [ -f {catalog} ]; then echo {Tag}catalog; elif [ -f {manifest} ]; then echo {Tag}unadopted; else echo {Tag}none; fi",
            "checking the image", cancellationToken).ConfigureAwait(false);
        return output.Contains(Tag + "unadopted", StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal for a command that could not take the image's lock on a format-2 pool: <c>catalog_busy</c> when the image is one
    /// it would have had to adopt, otherwise null and the caller reports the lock as it always did.
    /// </summary>
    public static async Task<SqlBranchException?> BusyIfUnadoptedAsync(IContainerRuntime runtime, string engineName, string name, CancellationToken cancellationToken) =>
        await IsUnadoptedAsync(runtime, engineName, name, cancellationToken).ConfigureAwait(false) ? Busy(name) : null;

    /// <summary>
    /// Adopts <paramref name="legacy"/> (an image read with a null ID). The command holds the image's shared lock, which is given
    /// back before the exclusive lock is tried without waiting; when another operation holds the image, nothing is changed and
    /// the command exits 1 <c>catalog_busy</c>. The ID is max(clock, last allocated + 1 ms), as for a build (D4, D7). The exclusive
    /// lock is kept for the rest of the command.
    /// </summary>
    public static async Task<ImageVersionData> AdoptAsync(
        IContainerRuntime runtime, string engineName, IEngineSession session, string name, ImageVersionData legacy, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        await EngineLocks.ReleaseAsync(session, EngineLocks.Image(name), cancellationToken).ConfigureAwait(false);
        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Image(name), cancellationToken).ConfigureAwait(false))
        {
            throw Busy(name);
        }

        // Read again under the exclusive lock: another command may have adopted or removed the image since the first read.
        var current = await ImageCatalog.ReadVersionAsync(runtime, engineName, name, null, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.ImageNotFound, $"There is no image named '{name}'. 'sqlbranch image list' shows the images.");
        if (current.Id is not null)
        {
            return current;
        }

        var id = await ImageVersions.PublishAsync(runtime, engineName, name, clock, cancellationToken).ConfigureAwait(false);
        return current with { Id = id };
    }
}
