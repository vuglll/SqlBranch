using System.Globalization;
using System.Text;
using SqlBranch.Core.Containers;

namespace SqlBranch.Core.Images;

/// <summary>A file to seal into an image: where it is now, and its name in the image's <c>data</c> folder.</summary>
/// <param name="Source">The file's path in the engine container.</param>
/// <param name="FileName">The file's plain name in the image.</param>
internal sealed record SealFile(string Source, string FileName);

/// <summary>
/// The seal that <c>image build</c> and <c>image import</c> share (design D7): make <c>/pool/images/&lt;name&gt;</c>, move the files into
/// its <c>data</c> folder read-only, then write the manifest last, so an image exists only once it has one. It remembers whether it made
/// the image folder, and only then may a failed run remove it; a folder that already existed is never this run's to remove.
/// </summary>
internal sealed class ImageSealer
{
    private readonly IContainerRuntime _runtime;
    private readonly string _engineName;
    private readonly string _name;

    public ImageSealer(IContainerRuntime runtime, string engineName, string name)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _engineName = engineName;
        _name = name;
    }

    /// <summary>This run made <c>/pool/images/&lt;name&gt;</c>, so only this run removes it on failure.</summary>
    public bool ImageDirectoryCreated { get; private set; }

    /// <summary>Makes the image folder and moves the files into it, mode 444.</summary>
    public async Task SealAsync(IReadOnlyList<SealFile> files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);

        // The image folder gets its own step, and the flag is set only after it succeeded: a folder that already
        // existed is not this run's, so a failed mkdir must never lead to a cleanup that deletes it.
        var root = Quoting.Shell(ImagePaths.ImagesRoot);
        await EngineShell.RunAsync(_runtime, _engineName,
            $"mkdir -p {root} && mkdir {Quoting.Shell(ImagePaths.Image(_name))}",
            "creating the image folder", cancellationToken).ConfigureAwait(false);
        ImageDirectoryCreated = true;

        await EngineShell.RunAsync(_runtime, _engineName, MoveFilesScript(ImagePaths.Image(_name), files),
            "moving the files into the image", cancellationToken).ConfigureAwait(false);
    }

    internal static string MoveFilesScript(string directory, IReadOnlyList<SealFile> files)
    {
        var data = directory + "/data";
        var script = new StringBuilder($"set -e; mkdir {Quoting.Shell(data)}; ");
        foreach (var file in files)
        {
            var target = $"{data}/{file.FileName}";
            script.Append(CultureInfo.InvariantCulture, $"mv {Quoting.Shell(file.Source)} {Quoting.Shell(target)}; chmod 444 {Quoting.Shell(target)}; ");
        }

        return script.ToString();
    }

    /// <summary>Writes <c>manifest.json</c>, the last thing an image needs to exist.</summary>
    public Task WriteManifestAsync(ImageManifest manifest, CancellationToken cancellationToken) =>
        EngineShell.RunAsync(_runtime, _engineName, ManifestScript(manifest, ImagePaths.Manifest(manifest.Name)),
            "writing the manifest", cancellationToken);

    internal static string ManifestScript(ImageManifest manifest, string path)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest.ToJson()));
        var target = Quoting.Shell(path);
        return $"printf %s {Quoting.Shell(encoded)} | base64 -d > {target} && chmod 444 {target}";
    }

    /// <summary>Removes the image folder when this run made it. Returns what could not be removed, with the reason; never throws.</summary>
    public Task<IEnumerable<string>> RemoveCreatedAsync() =>
        ImageDirectoryCreated ? EngineShell.TryRemoveAsync(_runtime, _engineName, ImagePaths.Image(_name)) : Task.FromResult<IEnumerable<string>>([]);
}

/// <summary>Shell scripts in the engine container, as the build and the import run them.</summary>
internal static class EngineShell
{
    /// <summary>Runs a <c>sh</c> script in the engine and returns its standard output, or throws exit 1 naming <paramref name="what"/>.</summary>
    public static async Task<string> RunAsync(IContainerRuntime runtime, string engineName, string script, string what, CancellationToken cancellationToken)
    {
        var result = await runtime.ExecAsync(engineName, ["sh", "-c", script], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var detail = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            throw new SqlBranchException($"The engine could not do this step ({what}), exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}{(detail is null ? string.Empty : ": " + detail)}.");
        }

        return result.StdOut;
    }

    /// <summary>Removes a path inside the pool, never throwing; the result lists what could not be removed.</summary>
    public static async Task<IEnumerable<string>> TryRemoveAsync(IContainerRuntime runtime, string engineName, string path, bool keepMarker = false)
    {
        try
        {
            var script = keepMarker
                ? $"find {Quoting.Shell(path)} -mindepth 1 ! -name {CredentialMarker.FileName} -delete"
                : $"rm -rf {Quoting.Shell(path)}";
            await RunAsync(runtime, engineName, script, $"removing {path}", CancellationToken.None).ConfigureAwait(false);
            return [];
        }
#pragma warning disable CA1031 // Reported by the caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return [$"{path} ({ex.Message})"];
        }
    }
}
