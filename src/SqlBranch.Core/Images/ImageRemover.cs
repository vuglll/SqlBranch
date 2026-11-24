using System.Globalization;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Images;

/// <summary>A removed image.</summary>
/// <param name="Name">The image's name.</param>
/// <param name="Branches">The branches that were created from it and keep their own files.</param>
public sealed record ImageRemoval(string Name, IReadOnlyList<string> Branches)
{
    /// <summary>The version IDs removed with the name: from the catalog on a format-2 pool, empty on format 1 and for an image not adopted yet.</summary>
    public IReadOnlyList<string> Versions { get; init; } = [];
}

/// <summary>
/// <c>image rm</c> as design D5 describes it: the image folder is renamed to <c>.removing-&lt;name&gt;</c>
/// first, one atomic step that makes it unlisted and unusable, and only then deleted. The command never
/// touches <c>/pool/branches</c>, so a branch keeps its own files whatever happens to its image.
/// </summary>
public sealed class ImageRemover
{
    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly string _engineName;

    public ImageRemover(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        string engineName = ContainerLabels.EngineName)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _engineName = engineName;
    }

    /// <summary>The folder an image is renamed to before its files are deleted.</summary>
    public static string RemovingPath(string name) => $"{ImagePaths.ImagesRoot}/.removing-{name}";

    public async Task<ImageRemoval> RemoveAsync(FoundPool pool, int fallbackPort, string name, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);

        // Before any SQL runs and before any file is touched.
        NameRules.ValidateImage(name);
        var engine = ImageCatalog.RequireRunningEngine(pool);

        var password = await EngineCredentials.ReadAsync(_runtime, engine.Image, pool.Location, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The engine credentials are missing from {pool.Location}. Run 'sqlbranch teardown --force' and 'sqlbranch init' to start again.");
        _redactor.Register(password);

        await using var session = await _connector.ConnectAsync(EngineEndpoint.Loopback(EngineStarter.PortOf(engine, fallbackPort)), password, cancellationToken).ConfigureAwait(false);

        // The same lock a build of this name holds, so a removal and a build of one name cannot overlap.
        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Image(name), cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Another command is working on the image '{name}': a build or a removal is running. Wait for it to finish.");
        }

        var listing = await new ImageCatalog(_runtime, _engineName).ListAsync(cancellationToken, pool.Location).ConfigureAwait(false);
        var entry = listing.Images.FirstOrDefault(i => i.Name == name)
            ?? throw new SqlBranchException(ErrorCodes.ImageNotFound, $"There is no image named '{name}'. 'sqlbranch image list' shows the images.");

        if (entry.Branches.Count > 0 && !force)
        {
            throw new SqlBranchException(ErrorCodes.ImageInUse,
                $"The image '{name}' is used by {entry.Branches.Count.ToString(CultureInfo.InvariantCulture)} branch" +
                $"{(entry.Branches.Count == 1 ? string.Empty : "es")}: {string.Join(", ", entry.Branches)}. " +
                "Remove them first, or run the command again with --force. Branches keep working after a forced removal, but they can no longer be reset.");
        }

        // 1. One atomic step: from here the image is not listed and cannot be used for a new branch.
        var source = ImagePaths.Image(name);
        var target = RemovingPath(name);
        var renamed = await ExecAsync(
            $"if [ -e {Quoting.Shell(target)} ]; then echo SQLBRANCH_LEFTOVER=yes; else mv -T {Quoting.Shell(source)} {Quoting.Shell(target)} && echo SQLBRANCH_RENAMED=yes; fi",
            cancellationToken).ConfigureAwait(false);
        if (renamed.StdOut.Contains("SQLBRANCH_LEFTOVER=yes", StringComparison.Ordinal))
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The pool still holds {target}, left by an earlier removal of an image named '{name}' that did not finish. " +
                "'sqlbranch status' reports it. Delete that folder, then run the command again.");
        }

        if (!renamed.Succeeded || !renamed.StdOut.Contains("SQLBRANCH_RENAMED=yes", StringComparison.Ordinal))
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The image '{name}' could not be taken out of use ({Detail(renamed)}). Nothing was deleted.");
        }

        // 2. Delete the files. The image is already gone as far as every other command is concerned.
        var deleted = await ExecAsync($"rm -rf {Quoting.Shell(target)}", cancellationToken).ConfigureAwait(false);
        if (!deleted.Succeeded)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The image '{name}' is removed from use, but deleting its files failed ({Detail(deleted)}). {target} is left behind. " +
                "'sqlbranch status' reports it. Delete that folder to finish.");
        }

        return new ImageRemoval(name, entry.Branches) { Versions = entry.Versions };
    }

    private Task<ContainerResult> ExecAsync(string script, CancellationToken cancellationToken) =>
        _runtime.ExecAsync(_engineName, ["sh", "-c", script], cancellationToken);

    private static string Detail(ContainerResult result)
    {
        var line = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return $"exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}{(line is null ? string.Empty : ": " + line)}";
    }
}
