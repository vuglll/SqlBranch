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

    /// <summary>The refusal when the image is not the one the command read before adopting it.</summary>
    private static SqlBranchException Changed(string name) =>
        new(ErrorCodes.CatalogBusy,
            $"The image '{name}' changed while it was being adopted, so this command cannot use the version it read. Nothing was changed. Run the command again.");

    /// <summary>
    /// Adopts <paramref name="legacy"/> (an image read with a null ID). The command holds the image's shared lock, which is given
    /// back before the exclusive lock is tried without waiting; when another operation holds the image, nothing is changed and
    /// the command exits 1 <c>catalog_busy</c>. The ID is max(command start time, last allocated + 1 ms) (D4, D7). Once the
    /// catalog is written the exclusive lock is given back and the shared lock taken again, so parallel creates from the image
    /// are not held up for the rest of the command. The image is read again under each lock: when it is no longer the one the
    /// command read (a different <c>builtAt</c> or directory), the command exits 1 <c>catalog_busy</c> and nothing is used.
    /// </summary>
    public static async Task<ImageVersionData> AdoptAsync(
        IContainerRuntime runtime, string engineName, IEngineSession session, string name, ImageVersionData legacy, DateTimeOffset commandStart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        await EngineLocks.ReleaseAsync(session, EngineLocks.Image(name), cancellationToken).ConfigureAwait(false);
        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Image(name), cancellationToken).ConfigureAwait(false))
        {
            throw Busy(name);
        }

        // Read again under the exclusive lock: another command may have adopted, replaced or removed the image since the first read.
        var current = await ImageCatalog.ReadVersionAsync(runtime, engineName, name, null, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.ImageNotFound, $"There is no image named '{name}'. 'sqlbranch image list' shows the images.");
        if (current.Directory != legacy.Directory || current.Manifest.BuiltAt != legacy.Manifest.BuiltAt)
        {
            throw Changed(name);
        }

        var id = current.Id;
        if (id is null)
        {
            id = await ImageVersions.PublishAsync(runtime, engineName, name, new FixedClock(commandStart), cancellationToken).ConfigureAwait(false);
        }

        // Back to the shared lock. The gap lets 'image rm' in, so the image is checked once more before the command uses it.
        await EngineLocks.ReleaseAsync(session, EngineLocks.Image(name), cancellationToken).ConfigureAwait(false);
        if (!await EngineLocks.TryTakeSharedAsync(session, EngineLocks.Image(name), cancellationToken).ConfigureAwait(false))
        {
            throw Busy(name);
        }

        var again = await ImageCatalog.ReadVersionAsync(runtime, engineName, name, null, cancellationToken).ConfigureAwait(false);
        if (again is null || again.Id != id || again.Directory != legacy.Directory || again.Manifest.BuiltAt != legacy.Manifest.BuiltAt)
        {
            throw Changed(name);
        }

        return again;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
