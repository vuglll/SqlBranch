using SqlBranch.Core.LogicalSources;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json.Nodes;
using SqlBranch.Core;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Storage;
using CommandResult = SqlBranch.Core.Output.CommandResult;

namespace SqlBranch.Cli.Commands;

/// <summary><c>sqlbranch image build</c>, <c>list</c> and <c>rm</c>.</summary>
internal static partial class ImageCommands
{
    public static void Register(CliApp app, Func<CliServices> getServices, SecretRedactor redactor)
    {
        var image = new Command("image", "Build, list and remove images: sealed, read-only database files that branches are cloned from.");
        app.Root.Subcommands.Add(image);

        var name = new Argument<string>("name") { Description = "The image name: 1 to 63 lowercase letters, digits, hyphens and underscores." };
        var fromBak = new Option<string?>("--from-bak") { Description = "The full database backup to build the image from: a file, an https:// Azure Blob URL or an s3:// URL." };
        var s3Region = new Option<string?>("--s3-region") { Description = "The region of an s3:// URL. Default us-east-1." };
        var backupSet = new Option<int>("--backup-set") { Description = "Which backup set of the file to use.", DefaultValueFactory = _ => 1 };
        var keep = new Option<bool>("--keep-recovery-model") { Description = "Do not switch the image's database to the SIMPLE recovery model." };

        var build = new Command("build", "Build an image from a backup, SQL scripts or a BACPAC.");
        var fromBacpac = new Option<string?>("--from-bacpac") { Description = "Build from a local BACPAC or an Azure Blob or S3 URL." };
        var sqlPackage = new Option<string?>("--sqlpackage") { Description = "Path to SqlPackage for a BACPAC build. Defaults to searching PATH." };
        build.Options.Add(fromBacpac);
        build.Options.Add(sqlPackage);
        build.Arguments.Add(name);
        build.Options.Add(fromBak);
        build.Options.Add(s3Region);
        var fromScripts = new Option<string?>("--from-scripts") { Description = "Build from the SQL files in this folder." };
        var postScripts = new Option<string[]>("--post-script") { Description = "Run this SQL script before sealing. May be repeated.", Arity = ArgumentArity.OneOrMore };
        var timeout = new Option<string?>("--script-timeout") { Description = "Maximum time per script in minutes, hours or days." };
        build.Options.Add(fromScripts);
        build.Options.Add(postScripts);
        build.Options.Add(timeout);
        build.Options.Add(backupSet);
        build.Options.Add(keep);
        app.AddCommand(image, build, new CommandMetadata
        {
            Purpose = "Build a sealed image from a backup file, a backup URL (https:// Azure Blob or s3://) or SQL scripts.",
            WhenToUse = "Prepare a reusable starting database for branches. A URL needs its secret in SQLBRANCH_AZURE_SAS_TOKEN or SQLBRANCH_S3_ACCESS_KEY_ID and SQLBRANCH_S3_SECRET_ACCESS_KEY, never in an argument.",
            Example = "sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak --s3-region eu-west-1",
            Destroys = "nothing",
        }, parse =>
        {
            var services = getServices();
            var config = parse.GetValue(CliApp.ConfigOption)?.FullName;
            var imageName = parse.GetValue(name)!;
            var post = parse.GetValue(postScripts) ?? [];
            var scriptTimeout = parse.GetValue(timeout);
            var region = parse.GetValue(s3Region);
            if (parse.GetResult(fromBacpac) is not null)
            {
                var sources = parse.Tokens.Count(t => t.Type == TokenType.Option &&
                    (t.Value == fromBacpac.Name || t.Value == fromBak.Name || t.Value == fromScripts.Name));
                if (sources != 1 || parse.GetResult(fromBak) is not null || parse.GetResult(fromScripts) is not null)
                {
                    throw new SqlBranchException(ErrorCodes.UsageError, SourceChoices);
                }
                if (parse.GetResult(backupSet) is { Implicit: false })
                {
                    throw new SqlBranchException(ErrorCodes.UsageError, "--backup-set cannot be used with --from-bacpac.");
                }

                return BuildBacpac(services, redactor, config, imageName, parse.GetValue(fromBacpac),
                    parse.GetValue(sqlPackage), post, scriptTimeout, region, parse.GetValue(keep), app.WriteProgress);
            }
            if (parse.GetResult(sqlPackage) is not null)
            {
                throw new SqlBranchException(ErrorCodes.UsageError, "--sqlpackage requires --from-bacpac.");
            }

            return Build(services, redactor, config, imageName, parse.GetValue(fromBak), parse.GetValue(backupSet), parse.GetValue(keep),
                parse.GetValue(fromScripts), post, scriptTimeout, region, app.WriteProgress);
        },
            ["shop", "--from-bak", "shop.bak"]);

        var list = new Command("list", "List the images with their source database, size, build time and the number of branches created from each.");
        app.AddCommand(image, list, new CommandMetadata
        {
            Purpose = "List available images and their provenance.",
            WhenToUse = "Choose the baseline required by the project.",
            Example = "sqlbranch image list --format json",
            Destroys = "nothing",
        }, parse => List(getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName));

        var exportName = new Argument<string>("name") { Description = "The image to export." };
        var exportTo = new Option<string?>("--to") { Description = "Where to write the archive: a file, an https:// Azure Blob URL or an s3:// URL. A file ends in .sbimg by convention." };
        var exportRegion = new Option<string?>("--s3-region") { Description = "The region of an s3:// URL. Default us-east-1." };
        var exportForce = new Option<bool>("--force") { Description = "Replace the destination when it already exists. The old file is kept until the new archive is complete." };
        var export = new Command("export", "Write an image, its manifest and a SHA-256 for each file, as one compressed archive.");
        export.Arguments.Add(exportName);
        export.Options.Add(exportTo);
        export.Options.Add(exportRegion);
        export.Options.Add(exportForce);
        app.AddCommand(image, export, new CommandMetadata
        {
            Purpose = "Export a sealed image as one archive file that 'image import' can add to any SqlBranch pool.",
            WhenToUse = "Share or keep an image: move it to another machine, or save a nightly baseline. The image is not changed. An existing destination is refused unless --force is given. A URL (https:// Azure Blob or s3://) needs its secret in SQLBRANCH_AZURE_SAS_TOKEN or SQLBRANCH_S3_ACCESS_KEY_ID and SQLBRANCH_S3_SECRET_ACCESS_KEY, never in an argument.",
            Example = "sqlbranch image export shop --to shop.sbimg",
            Destroys = "nothing; with --force, the file it replaces, and only after the new archive is complete.",
        }, parse => Export(
            getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName,
            parse.GetValue(exportName)!, parse.GetValue(exportTo), parse.GetValue(exportForce), parse.GetValue(exportRegion), app.WriteProgress),
            ["shop", "--to", "shop.sbimg"]);

        var importSource = new Argument<string>("source") { Description = "The archive to import: a file, an https:// Azure Blob URL or an s3:// URL." };
        var importAs = new Option<string?>("--as") { Description = "The name for the imported image. Default: the name inside the archive." };
        var importRegion = new Option<string?>("--s3-region") { Description = "The region of an s3:// URL. Default us-east-1." };
        var import = new Command("import", "Add the image inside an archive written by 'image export' to the pool.");
        import.Arguments.Add(importSource);
        import.Options.Add(importAs);
        import.Options.Add(importRegion);
        app.AddCommand(image, import, new CommandMetadata
        {
            Purpose = "Import an image archive written by 'image export' as a new, sealed image.",
            WhenToUse = "Bring an image from another machine or a nightly baseline into this pool. The archive's engine must be as new as this engine (the same major version or older). An existing image is never replaced; use --as to pick another name. A URL (https:// Azure Blob or s3://) needs its secret in SQLBRANCH_AZURE_SAS_TOKEN or SQLBRANCH_S3_ACCESS_KEY_ID and SQLBRANCH_S3_SECRET_ACCESS_KEY, never in an argument.",
            Example = "sqlbranch image import shop.sbimg --as shop2",
            Destroys = "nothing; a failed import removes only what it made.",
        }, parse => Import(
            getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName,
            parse.GetValue(importSource)!, parse.GetValue(importAs), parse.GetValue(importRegion), app.WriteProgress),
            ["shop.sbimg"]);

        var rmName = new Argument<string>("name") { Description = "The image to remove." };
        var force = new Option<bool>("--force") { Description = "Remove the image even when branches were created from it. They keep working, but cannot be reset." };
        var rm = new Command("rm", "Remove an image. Branch files are never touched.");
        rm.Arguments.Add(rmName);
        rm.Options.Add(force);
        app.AddCommand(image, rm, new CommandMetadata
        {
            Purpose = "Remove a sealed image.",
            WhenToUse = "Retire an image after checking its dependent branches.",
            Example = "sqlbranch image rm shop",
            Destroys = "The named image. With --force, existing branches lose their reset source.",
        }, parse => Remove(
            getServices(), redactor, parse.GetValue(CliApp.ConfigOption)?.FullName, parse.GetValue(rmName)!, parse.GetValue(force)),
            ["shop"]);
    }

    private static CommandResult List(CliServices services, SecretRedactor redactor, string? configFile)
    {
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        ImageCatalog.RequireRunningEngine(context.Pool);
        var listing = new ImageCatalog(services.Runtime, services.EngineName).ListAsync(CancellationToken.None).GetAwaiter().GetResult();
        return DescribeList(listing);
    }

    private static CommandResult Remove(CliServices services, SecretRedactor redactor, string? configFile, string name, bool force)
    {
        NameRules.ValidateImage(name);
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        var removal = new ImageRemover(services.Runtime, services.Connector, redactor, services.EngineName)
            .RemoveAsync(context.Pool, context.Settings.Port, name, force, CancellationToken.None).GetAwaiter().GetResult();

        var text = new List<string> { $"Image {removal.Name} removed." };
        if (removal.Branches.Count > 0)
        {
            text.Add($"Branches created from it keep working: {string.Join(", ", removal.Branches)}. They can no longer be reset.");
        }

        var payload = new JsonObject
        {
            ["image"] = new JsonObject
            {
                ["name"] = removal.Name,
                ["removed"] = true,
                ["forced"] = force,
                ["branches"] = new JsonArray([.. removal.Branches.Select(b => (JsonNode?)b)]),
            },
        };
        return CommandResult.Success("image rm", payload, text, [removal.Name]);
    }

    /// <summary>The option that names an export's destination.</summary>
    private const string ToOption = "--to";

    private static CommandResult Export(CliServices services, SecretRedactor redactor, string? configFile, string name, string? to, bool force,
        string? s3Region, Action<string>? progress)
    {
        // Every exit-2 check runs before the pool is looked for, so no docker command and no storage request runs on bad input.
        NameRules.ValidateImage(name);
        if (string.IsNullOrWhiteSpace(to))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, "Specify where to write the archive with --to: a file, an https:// Azure Blob URL or an s3:// URL.");
        }

        var url = BackupUrl.IsUrl(to) ? BackupUrl.Parse(to, s3Region, ToOption, maxUrlLength: null, maxCredentialNameLength: null) : null;
        BackupUrl.CheckRegionOption(s3Region, url, ToOption);
        if (url is not null)
        {
            // The secrets are read and checked here, so a missing one exits 2 before any docker command or storage request.
            var secrets = StorageSecrets.Read(url, services.Environment, redactor);
            _ = services.LoadSettings(configFile);
            return ExportToUrl(services, redactor, configFile, name, url, secrets, force, progress);
        }

        var full = Path.GetFullPath(to, services.WorkingDirectory);
        if (services.LoadSettings(configFile).Pool is { Length: > 0 } poolSetting)
        {
            RequireOutsidePool(services, to, full, Path.GetFullPath(poolSetting, services.WorkingDirectory), poolSetting);
        }

        if (services.DirectoryExistsCheck(full))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"'{to}' is a folder; --to must name the archive file to write.");
        }

        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || !services.DirectoryExistsCheck(parent))
        {
            var shown = Path.GetDirectoryName(to);
            throw new SqlBranchException(ErrorCodes.UsageError, $"The folder '{(string.IsNullOrEmpty(shown) ? "." : shown)}' for the archive does not exist. Create it first.");
        }

        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        if (context.Pool.Location.Kind == PoolKind.Directory)
        {
            // The engine's own labels may name a pool directory the settings do not.
            RequireOutsidePool(services, to, full, Path.GetFullPath(context.Pool.Location.Source, services.WorkingDirectory), context.Pool.Location.Source);
        }

        using var cancel = new CancellationTokenSource();
        var presses = 0;
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            // The first Ctrl+C stops the export and lets it delete its partial file; a second one ends the process.
            if (Interlocked.Increment(ref presses) == 1)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            var exported = new ImageExporter(services.Runtime, services.EngineName) { Progress = progress }
                .ExportAsync(context.Pool, name, full, force, cancel.Token).GetAwaiter().GetResult();
            return DescribeExport(exported, to);
        }
        catch (OperationCanceledException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, "The export was cancelled. The partial file was deleted; the destination is as it was.");
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    private static CommandResult ExportToUrl(CliServices services, SecretRedactor redactor, string? configFile, string name, BackupUrl url,
        StorageSecrets secrets, bool force, Action<string>? progress)
    {
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        using var cancel = new CancellationTokenSource();
        var presses = 0;
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            // The first Ctrl+C stops the export and lets it abort its upload; a second one ends the process.
            if (Interlocked.Increment(ref presses) == 1)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        }

        Console.CancelKeyPress += OnCancel;
        var store = services.StoreOf(url, secrets, redactor);
        try
        {
            var exported = new ImageExporter(services.Runtime, services.EngineName) { Progress = progress }
                .ExportToStoreAsync(context.Pool, name, store, force, cancel.Token).GetAwaiter().GetResult();
            return DescribeExport(exported, url.Url);
        }
        catch (OperationCanceledException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, "The export was cancelled. The upload was aborted and nothing was committed; the destination is as it was.");
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            (store as IDisposable)?.Dispose();
        }
    }

    internal static CommandResult DescribeExport(ImageExportResult result, string to)
    {
        var text = new List<string>
        {
            $"Image {result.Image} is exported to {to}.",
            $"Files: {result.Files.ToString(CultureInfo.InvariantCulture)}, {new ByteSize(Math.Max(result.ImageBytes, 1)).ToBinary()}",
            $"Archive: {new ByteSize(Math.Max(result.ArchiveBytes, 1)).ToBinary()}",
        };
        var payload = new JsonObject
        {
            ["export"] = new JsonObject
            {
                ["image"] = result.Image,
                ["to"] = to,
                ["files"] = result.Files,
                ["imageBytes"] = result.ImageBytes,
                ["archiveBytes"] = result.ArchiveBytes,
            },
        };
        return CommandResult.Success("image export", payload, text, [to]);
    }

    /// <summary>The words that name an import's source in messages about its URL.</summary>
    private const string ImportSource = "the archive source";

    private static CommandResult Import(CliServices services, SecretRedactor redactor, string? configFile, string source, string? asName,
        string? s3Region, Action<string>? progress)
    {
        // Every exit-2 check runs before the archive is read and the pool is looked for, so no docker command runs on bad input.
        if (asName is not null)
        {
            NameRules.ValidateImage(asName);
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, "Specify the archive to import: a file, an https:// Azure Blob URL or an s3:// URL.");
        }

        var url = BackupUrl.IsUrl(source) ? BackupUrl.Parse(source, s3Region, ImportSource, maxUrlLength: null, maxCredentialNameLength: null) : null;
        BackupUrl.CheckRegionOption(s3Region, url, ImportSource);
        StorageSecrets? secrets = url is null ? null : StorageSecrets.Read(url, services.Environment, redactor);

        // Settings are read now, so a secret in a config file or a bad setting exits 2 before the archive or the storage is touched.
        _ = services.LoadSettings(configFile);

        string? filePath = null;
        if (url is null)
        {
            filePath = Path.GetFullPath(source, services.WorkingDirectory);
            if (services.DirectoryExistsCheck(filePath))
            {
                throw new SqlBranchException(ErrorCodes.UsageError, $"'{source}' is a folder; the archive to import must be a file.");
            }

            var probe = services.ProbeFile(filePath);
            if (!probe.Exists || !probe.Readable)
            {
                throw new SqlBranchException(ErrorCodes.UsageError, probe.Exists
                    ? $"The archive {source} cannot be read. Check its permissions."
                    : $"The archive {source} does not exist.");
            }
        }

        using var cancel = new CancellationTokenSource();
        var presses = 0;
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            // The first Ctrl+C stops the import and lets it undo what it made; a second one ends the process.
            if (Interlocked.Increment(ref presses) == 1)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        }

        Console.CancelKeyPress += OnCancel;
        IArchiveStore? store = null;
        try
        {
            Stream input;
            if (url is null)
            {
                input = OpenFile(filePath!, source);
            }
            else
            {
                // One storage GET, before any docker command: a wrong secret or a missing object is storage_unreachable and changes nothing.
                store = services.StoreOf(url, secrets!, redactor);
                input = store.OpenReadAsync(cancel.Token).GetAwaiter().GetResult();
            }

            using (input)
            {
                // Read-head (design D7): the archive's first entry and its manifest are checked before any docker command,
                // and before the engine is looked for, so an invalid archive is exit 1 even on a machine without an engine.
                var reader = ImageArchiveReader.OpenAsync(input, cancel.Token).GetAwaiter().GetResult();
                try
                {
                    var target = ArchiveManifestChecks.Check(reader.Head.Manifest, asName);
                    var context = CommandContext.RequireAsync(services, redactor, configFile, cancel.Token).GetAwaiter().GetResult();
                    var imported = new ImageImporter(services.Runtime, services.Connector, redactor, services.EngineName) { Progress = progress }
                        .ImportAsync(context.Pool, context.Settings.Port, reader, target, cancel.Token).GetAwaiter().GetResult();
                    return DescribeImport(imported, url?.Url ?? source);
                }
                finally
                {
                    reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
        catch (ImportCancelledException cancelled) when (cancelled.Leftovers.Count > 0)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The import was cancelled, but its cleanup could not remove {string.Join(" and ", cancelled.Leftovers)}. 'sqlbranch status' lists what is left.");
        }
        catch (OperationCanceledException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, "The import was cancelled. What it created was removed.");
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            (store as IDisposable)?.Dispose();
        }
    }

    private static FileStream OpenFile(string path, string given)
    {
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 1 << 16,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"The archive {given} cannot be read: {ex.Message}");
        }
    }

    internal static CommandResult DescribeImport(ImageImportResult result, string from)
    {
        var manifest = result.Manifest;
        var mode = CloneModeNames.Of(result.Mode);
        var text = new List<string>
        {
            $"Image {manifest.Name} is imported from {from}.",
            $"Source database: {manifest.SourceDatabase}",
            $"Files: {manifest.Files.Count.ToString(CultureInfo.InvariantCulture)}, {new ByteSize(Math.Max(manifest.TotalBytes, 1)).ToBinary()}",
            $"Clone mode: {mode}",
        };
        if (result.Mode == CloneMode.Copy)
        {
            text.Add("Branch creation will copy every file in full.");
        }

        var image = ImageJson(manifest);
        image["branches"] = 0;
        image["branchNames"] = new JsonArray();
        var payload = new JsonObject { ["from"] = from, ["cloneMode"] = mode, ["image"] = image };
        return CommandResult.Success("image import", payload, text, [manifest.Name], result.Warnings);
    }

    /// <summary>Exit 2 <c>usage_error</c> when the archive would be written inside a pool directory, among pool files.</summary>
    private static void RequireOutsidePool(CliServices services, string given, string fullDestination, string fullPool, string poolText)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Inside(string destination, string pool) =>
            destination.StartsWith(pool.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);

        // Symbolic links are resolved on the folder, which exists, and on the pool, so /tmp and /private/tmp are one place.
        var parent = Path.GetDirectoryName(fullDestination) ?? fullDestination;
        var realDestination = Path.Combine(services.RealPathOf(parent), Path.GetFileName(fullDestination));
        var realPool = services.RealPathOf(fullPool);
        if (Inside(fullDestination, fullPool) || Inside(realDestination, realPool))
        {
            throw new SqlBranchException(ErrorCodes.UsageError,
                $"The destination '{given}' is inside the pool {poolText}. It must be outside the pool, so an archive never lands among pool files.");
        }
    }

    internal static CommandResult DescribeList(ImageListing listing)
    {
        var text = new List<string>();
        if (listing.Images.Count == 0)
        {
            text.Add("No images. Build one with 'sqlbranch image build <name> --from-bak <file>'.");
        }
        else
        {
            var rows = listing.Images.Select(i => new[]
            {
                i.Name,
                i.Manifest.SourceType == "scripts" ? $"scripts ({i.Manifest.Scripts.Count.ToString(CultureInfo.InvariantCulture)} files)" : i.Manifest.BacpacFile ?? i.Manifest.BackupUrl ?? i.Manifest.BackupFile ?? "-",
                i.Manifest.SourceDatabase,
                new ByteSize(Math.Max(i.Manifest.TotalBytes, 1)).ToBinary(),
                i.Manifest.BuiltAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                i.Branches.Count.ToString(CultureInfo.InvariantCulture),
            }).ToList();
            string[] header = ["NAME", "SOURCE", "SOURCE DATABASE", "SIZE", "BUILT", "BRANCHES"];
            var widths = header.Select((h, c) => Math.Max(h.Length, rows.Max(r => r[c].Length))).ToArray();
            string Line(string[] cells) => string.Join("  ", cells.Select((cell, c) => cell.PadRight(widths[c]))).TrimEnd();
            text.Add(Line(header));
            text.AddRange(rows.Select(Line));
        }

        var payload = new JsonObject
        {
            ["images"] = new JsonArray([.. listing.Images.Select(i =>
            {
                var node = ImageJson(i.Manifest);
                node["branches"] = i.Branches.Count;
                node["branchNames"] = new JsonArray([.. i.Branches.Select(b => (JsonNode?)b)]);
                return (JsonNode?)node;
            })]),
        };
        return CommandResult.Success("image list", payload, text, [.. listing.Images.Select(i => i.Name)], listing.Warnings);
    }

    private const string SourceChoices = "Specify exactly one of --from-bak, --from-scripts and --from-bacpac.";

    private static CommandResult Build(CliServices services, SecretRedactor redactor, string? configFile, string name, string? fromBak, int backupSet, bool keepRecoveryModel,
        string? fromScripts, string[] postPaths, string? timeout, string? s3Region = null, Action<string>? progress = null)
    {
        // Before the pool is looked for, so a bad name exits 2 even before init, and before any SQL runs or file is touched.
        NameRules.ValidateImage(name);
        if ((fromBak is null) == (fromScripts is null))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, SourceChoices);
        }
        if (backupSet < 1)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, "--backup-set must be 1 or higher.");
        }
        // A URL and its secrets are checked before the pool is looked for, so no docker command or SQL runs on bad input.
        var url = BackupUrl.IsUrl(fromBak) ? BackupUrl.Parse(fromBak!, s3Region, BackupUrl.BuildOption, BackupUrl.MaxUrlLength, BackupUrl.MaxCredentialNameLength) : null;
        BackupUrl.CheckRegionOption(s3Region, url);
        if (url is not null && backupSet != 1)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, "--backup-set cannot be used with a URL; a URL build always uses backup set 1.");
        }
        var secrets = url is null ? null : StorageSecrets.Read(url, services.Environment, redactor);
        TimeSpan? limit = timeout is null ? null : Duration.Parse(timeout) ??
            throw new SqlBranchException(ErrorCodes.UsageError, "--script-timeout requires a positive duration up to 365d.");
        string Full(string path) => Path.GetFullPath(path, services.WorkingDirectory);
        var scripts = fromScripts is null ? [] : ImageScript.ReadFolder(Full(fromScripts));
        var post = postPaths.Select(p => ImageScript.Read(Full(p))).ToArray();
        var context = CommandContext.RequireAsync(services, redactor, configFile, CancellationToken.None).GetAwaiter().GetResult();
        if (url is not null)
        {
            // The engine reads the backup itself: nothing is staged, so there is no file to look at on the client.
            var urlRequest = new ImageBuildRequest(name, url.Url, 0, null, 1, keepRecoveryModel)
            { SourceScripts = scripts, PostScripts = post, ScriptTimeout = limit, Url = url, Secrets = secrets };
            using var cancel = new CancellationTokenSource();
            var presses = 0;
            void OnCancel(object? sender, ConsoleCancelEventArgs e)
            {
                // The first Ctrl+C ends a wait for a credential at once and lets the build undo what it made; a second one ends the process.
                if (Interlocked.Increment(ref presses) == 1)
                {
                    e.Cancel = true;
                    cancel.Cancel();
                }
            }

            Console.CancelKeyPress += OnCancel;
            try
            {
                var built = new ImageBuilder(services.Runtime, services.Connector, redactor, services.EngineName) { Progress = progress }
                    .BuildAsync(context.Pool, context.Settings.Port, urlRequest, cancel.Token).GetAwaiter().GetResult();
                return Describe(built);
            }
            catch (OperationCanceledException)
            {
                throw new SqlBranchException(ErrorCodes.OperationFailed, "The build was cancelled. What it created was removed; press Ctrl+C again to stop at once.");
            }
            finally
            {
                Console.CancelKeyPress -= OnCancel;
            }
        }

        var path = fromBak is null ? string.Empty : Full(fromBak);
        long bytes = 0;
        if (fromBak is not null)
        {
            var file = services.ProbeFile(path);
            if (!file.Exists || !file.Readable)
            {
                throw new SqlBranchException(ErrorCodes.UsageError, file.Exists
                    ? $"The backup file {fromBak} cannot be read. Check its permissions."
                    : $"The backup file {fromBak} does not exist.");
            }
            bytes = file.Length;
        }
        var request = new ImageBuildRequest(name, path, bytes, fromBak is null ? null : ImagePaths.ImportPathOf(path, context.Pool.Location), backupSet, keepRecoveryModel)
        { SourceScripts = scripts, PostScripts = post, ScriptTimeout = limit };
        var result = new ImageBuilder(services.Runtime, services.Connector, redactor, services.EngineName)
            .BuildAsync(context.Pool, context.Settings.Port, request, CancellationToken.None).GetAwaiter().GetResult();
        return Describe(result);
    }

    internal static CommandResult Describe(ImageBuildResult result, BacpacBuildProgress? progress = null)
    {
        var manifest = result.Manifest;
        var mode = CloneModeNames.Of(result.Mode);
        var total = new ByteSize(Math.Max(manifest.TotalBytes, 1)).ToBinary();

        var text = new List<string>
        {
            manifest.SourceType == "scripts" ? $"Image {manifest.Name} is built from scripts." : $"Image {manifest.Name} is built from {manifest.BacpacFile ?? manifest.BackupUrl ?? manifest.BackupFile}.",
            $"Source database: {manifest.SourceDatabase}",
            $"Files: {manifest.Files.Count.ToString(CultureInfo.InvariantCulture)}, {total}",
            manifest.OriginalRecoveryModel == manifest.SealedRecoveryModel
                ? $"Recovery model: {manifest.SealedRecoveryModel}"
                : $"Recovery model: {manifest.OriginalRecoveryModel}, sealed as {manifest.SealedRecoveryModel}",
            $"Clone mode: {mode}",
        };
        if (result.Mode == CloneMode.Copy)
        {
            text.Add("Branch creation will copy every file in full.");
        }

        text.AddRange(result.ScriptTimes.Select(t => FormattableString.Invariant($"{t.Name}: {t.Duration.TotalMilliseconds:F0} ms")));
        var image = ImageJson(manifest);
        image["cloneMode"] = mode;
        var payload = new JsonObject { ["image"] = image };
        if (progress is not null)
        {
            AddBacpacProgress(progress, payload, text);
        }
        return CommandResult.Success("image build", payload, text, [manifest.Name], result.Warnings);
    }

    private static JsonArray ScriptJson(IReadOnlyList<ScriptFile> files) =>
        new([.. files.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["sha256"] = f.Sha256 })]);

    /// <summary>The manifest's facts as the JSON of <c>image build</c> and <c>image list</c>.</summary>
    private static JsonObject ImageJson(ImageManifest manifest) => new()
    {
        ["name"] = manifest.Name,
        ["sourceDatabase"] = manifest.SourceDatabase,
        ["engineVersion"] = manifest.EngineVersion,
        ["engineImage"] = manifest.EngineImage,
        ["sizeBytes"] = manifest.TotalBytes,
        ["files"] = new JsonArray([.. manifest.Files.Select(f => (JsonNode?)new JsonObject
        {
            ["logicalName"] = f.LogicalName,
            ["fileName"] = f.FileName,
            ["type"] = f.Type,
            ["sizeBytes"] = f.SizeBytes,
        })]),
        ["originalRecoveryModel"] = manifest.OriginalRecoveryModel,
        ["sealedRecoveryModel"] = manifest.SealedRecoveryModel,
        ["originalLogSizeBytes"] = manifest.OriginalLogSizeBytes,
        ["logRebuilt"] = manifest.LogRebuilt,
        ["builtAt"] = manifest.BuiltAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["backupFile"] = manifest.BackupFile,
        ["backupUrl"] = manifest.BackupUrl,
        ["backupSha256"] = manifest.BackupSha256,
        ["sourceType"] = manifest.SourceType,
        ["bacpacFile"] = manifest.BacpacFile,
        ["bacpacSha256"] = manifest.BacpacSha256,
        ["bacpacUrl"] = manifest.BacpacUrl,
        ["scripts"] = ScriptJson(manifest.Scripts),
        ["postScripts"] = ScriptJson(manifest.PostScripts),
        ["serviceBroker"] = manifest.ServiceBroker,
        ["ddlTriggers"] = new JsonArray([.. manifest.DdlTriggers.Select(t => (JsonNode?)t)]),
    };
}
