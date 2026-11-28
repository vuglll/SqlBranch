using System.Globalization;
using System.Text;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

/// <summary>
/// Version IDs of a format-2 pool (design D4): <c>&lt;name&gt;@&lt;UTC yyyyMMddTHHmmssfffZ&gt;</c>. The writer holds the image lock of the
/// name, allocates max(clock, last allocated + 1 ms) and stores it in <c>/pool/images/.ids/&lt;name&gt;</c>, one file per name, by temp file
/// and rename. The file stays when the name is removed, so a reused name keeps increasing.
/// </summary>
internal static class ImageVersions
{
    private const string Stamp = "yyyyMMdd'T'HHmmssfff'Z'";
    private const string LastIdTag = "SQLBRANCH_LASTID=";

    /// <summary>The folder of the per-name ID files. It starts with a dot, so no image glob lists it.</summary>
    public const string IdsDirectory = ImagePaths.ImagesRoot + "/.ids";

    public static string IdFile(string name) => $"{IdsDirectory}/{name}";

    public static string IdTemporaryFile(string name) => $"{IdFile(name)}.new";

    public static string Format(string name, DateTimeOffset time) =>
        $"{name}@{time.UtcDateTime.ToString(Stamp, CultureInfo.InvariantCulture)}";

    /// <summary>The time inside an ID of <paramref name="name"/>, or null when the text is not such an ID.</summary>
    public static DateTimeOffset? TimeOf(string name, string id)
    {
        var prefix = name + "@";
        if (!id.StartsWith(prefix, StringComparison.Ordinal) ||
            !DateTime.TryParseExact(id[prefix.Length..], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
        {
            return null;
        }

        return new DateTimeOffset(time, TimeSpan.Zero);
    }

    /// <summary>The next ID: the clock cut to the millisecond, or one millisecond after the last ID when that is not later.</summary>
    public static string Next(string name, DateTimeOffset now, DateTimeOffset? last)
    {
        var cut = ToMillisecond(now);
        return Format(name, last is { } l && cut <= l ? l.AddMilliseconds(1) : cut);
    }

    /// <summary>The time in UTC, cut to the millisecond the IDs carry.</summary>
    public static DateTimeOffset ToMillisecond(DateTimeOffset time)
    {
        var utc = time.UtcDateTime;
        return new DateTimeOffset(utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond)), TimeSpan.Zero);
    }

    /// <summary>Writes the high-water mark of <paramref name="name"/> by temp file and rename (design D4, D11). Run it after <c>set -e</c>.</summary>
    public static string HighWaterScript(string name, string id)
    {
        var file = Quoting.Shell(IdFile(name));
        var temporary = Quoting.Shell(IdTemporaryFile(name));
        return $"mkdir -p {Quoting.Shell(IdsDirectory)}; printf %s {Quoting.Shell(id)} > {temporary}; sync {temporary}; mv -f {temporary} {file}; sync {Quoting.Shell(IdsDirectory)} || true";
    }

    /// <summary>
    /// Allocates the next ID of <paramref name="name"/> and records it as the name's high-water mark. Call it under the image lock of
    /// the name. Two names use two files and never wait for each other.
    /// </summary>
    public static async Task<string> AllocateAsync(IContainerRuntime runtime, string engineName, string name, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var file = Quoting.Shell(IdFile(name));
        var output = await EngineShell.RunAsync(runtime, engineName,
            $"if [ -f {file} ]; then echo {LastIdTag}$(cat {file}); else echo {LastIdTag}none; fi", "reading the version record", cancellationToken).ConfigureAwait(false);
        var value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith(LastIdTag, StringComparison.Ordinal))?[LastIdTag.Length..];
        DateTimeOffset? last = null;
        if (value is not null and not "none")
        {
            last = TimeOf(name, value) ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The version record of the image '{name}' in {IdFile(name)} is not valid, so no version can be published.");
        }

        var id = Next(name, clock.GetUtcNow(), last);
        await EngineShell.RunAsync(runtime, engineName,
            "set -e; " + HighWaterScript(name, id),
            "recording the version", cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>The catalog of a name with one version, in the legacy files at the top of the image folder (design D3).</summary>
    public static ImageCatalogFile FirstCatalog(string name, string id) => new(name, id, [new ImageVersionRef(id, ".")]);

    /// <summary>Writes <c>catalog.json</c> into <paramref name="directory"/> by temp file and rename.</summary>
    public static string CatalogScript(string directory, ImageCatalogFile catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(catalog.ToJson()));
        var temporary = Quoting.Shell($"{directory}/{ImageCatalogFile.TemporaryName}");
        var target = Quoting.Shell($"{directory}/{ImageCatalogFile.FileName}");
        return $"printf %s {Quoting.Shell(encoded)} | base64 -d > {temporary} && sync {temporary} && mv -f {temporary} {target} && {{ sync {Quoting.Shell(directory)} || true; }}";
    }

    /// <summary>Allocates the ID of a first version and writes its catalog into the image folder, after the manifest.</summary>
    public static async Task<string> PublishAsync(IContainerRuntime runtime, string engineName, string name, TimeProvider clock, CancellationToken cancellationToken)
    {
        var id = await AllocateAsync(runtime, engineName, name, clock, cancellationToken).ConfigureAwait(false);
        await EngineShell.RunAsync(runtime, engineName, CatalogScript(ImagePaths.Image(name), FirstCatalog(name, id)), "writing the image catalog", cancellationToken).ConfigureAwait(false);
        return id;
    }
}
