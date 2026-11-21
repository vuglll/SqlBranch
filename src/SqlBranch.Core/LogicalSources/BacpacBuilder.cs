using System.ComponentModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.LogicalSources;

/// <summary>A verified import target, valid only for the duration of the callback.</summary>
public sealed record BacpacImportTarget(string Database, string BuildDirectory);

public interface IBacpacImportConsumer
{
    Task ConsumeAsync(BacpacImportTarget target, CancellationToken cancellationToken);
}

/// <summary>Imports a private copy under the image and operation locks on the owned engine.</summary>
public sealed partial class BacpacBuilder(
    IContainerRuntime runtime, IEngineConnector connector, SecretRedactor redactor, IProcessRunner process,
    string engineName = ContainerLabels.EngineName)
{
    public IBacpacImportConsumer? Consumer { get; init; }

    public IBacpacReadSource? ReadSource { get; init; }

    public TimeProvider? Clock { get; init; }
    internal Func<string, CancellationToken, Task>? AfterSealStep { get; init; }

    internal Func<Stream, Stream>? WrapRewriteOutput { get; init; }

    internal Func<string> NewId { get; init; } = () => "l" + Guid.NewGuid().ToString("N");

    public Task ImportAsync(FoundPool pool, int fallbackPort, string image, string? localPath, string? sqlPackage,
        IReadOnlyDictionary<string, string> environment, string workingDirectory, CancellationToken token) =>
        ImportCoreAsync(pool, fallbackPort, image, (scratch, ct) =>
            scratch.CopyAsync(localPath ?? throw new SqlBranchException(ErrorCodes.UnsupportedSource, "A local BACPAC path is required."), ct),
            sqlPackage, environment, workingDirectory, token);

    public Task ImportRemoteAsync(FoundPool pool, int fallbackPort, string image, BackupUrl url, StorageSecrets secrets,
        string? sqlPackage, IReadOnlyDictionary<string, string> environment, string workingDirectory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(secrets);
        // BackupUrl also has a public record constructor. Enforce the F1 URL contract
        // at this entry point so even a direct caller cannot supply an authenticated URL.
        var source = BackupUrl.Parse(url.Url, url.S3Region);
        return ImportCoreAsync(pool, fallbackPort, image,
            (scratch, ct) => BacpacDownload.StageAsync(ReadSource, source, secrets, scratch, ct),
            sqlPackage, environment, workingDirectory, token);
    }

    public async Task<ImageBuildResult> BuildAsync(FoundPool pool, int fallbackPort, ImageBuildRequest request,
        string? localPath, string? sqlPackage, IReadOnlyDictionary<string, string> environment, string workingDirectory,
        CancellationToken token, BacpacBuildProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress ??= new BacpacBuildProgress(Clock);
        if (request.Url is { } url)
        {
            var source = BackupUrl.Parse(url.Url, url.S3Region);
            var secrets = request.Secrets ?? throw new ArgumentException("Storage credentials are required.", nameof(request));
            return (await ImportCoreAsync(pool, fallbackPort, request.Name,
                (scratch, ct) => DownloadAsync(source, secrets, scratch, progress, ct),
                sqlPackage, environment, workingDirectory, token, request, source.BackupFile, source.Url, progress).ConfigureAwait(false))!;
        }

        ArgumentNullException.ThrowIfNull(localPath);
        return (await ImportCoreAsync(pool, fallbackPort, request.Name, (scratch, ct) => scratch.CopyAsync(localPath, ct),
            sqlPackage, environment, workingDirectory, token, request, Path.GetFileName(localPath), progress: progress).ConfigureAwait(false))!;
    }

    private async Task DownloadAsync(BackupUrl source, StorageSecrets secrets, BacpacScratch scratch, BacpacBuildProgress progress,
        CancellationToken token)
    {
        using var phase = progress.Measure("download");
        await BacpacDownload.StageAsync(ReadSource, source, secrets, scratch, token, reportSize: progress.ReportSize).ConfigureAwait(false);
    }

    private async Task<ImageBuildResult?> ImportCoreAsync(FoundPool pool, int fallbackPort, string image,
        Func<BacpacScratch, CancellationToken, Task> stage, string? sqlPackage,
        IReadOnlyDictionary<string, string> environment, string workingDirectory, CancellationToken token,
        ImageBuildRequest? request = null, string? file = null, string? url = null, BacpacBuildProgress? progress = null)
    {
        progress ??= new BacpacBuildProgress(Clock);
        NameRules.ValidateImage(image);
        var engine = pool.Engine ?? throw new SqlBranchException(ErrorCodes.NotInitialized,
            "The engine is missing. Run 'sqlbranch init'.");
        if (!engine.Running)
        {
            throw new SqlBranchException(ErrorCodes.EngineStopped, "The engine is stopped. Run 'sqlbranch engine start'.");
        }

        var port = EngineStarter.PortOf(engine, fallbackPort);
        if (!engine.IsManaged || engine.Name != engineName || string.IsNullOrEmpty(engine.Id) ||
            string.IsNullOrEmpty(engine.Hostname) || !engine.PublishedPorts.Contains($"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}"))
        {
            throw new SqlBranchException("Could not verify the SqlBranch import engine and its loopback port.");
        }

        var password = await EngineCredentials.ReadAsync(runtime, engine.Image, pool.Location, token).ConfigureAwait(false)
            ?? throw new SqlBranchException("The engine credentials are missing.");
        redactor.Register(password);
        var endpoint = EngineEndpoint.Loopback(port);
        await using var session = await connector.ConnectAsync(endpoint, password, token).ConfigureAwait(false);
        var ownership = new BacpacOwnership(runtime, engine, session);
        await ownership.VerifyEngineAsync(token).ConfigureAwait(false);

        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Image(image), token).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.ImageExists, $"Another build of image '{image}' is running.");
        }
        var imagePath = Quoting.Shell(ImagePaths.Image(image));
        var occupied = BacpacOwnership.AnyOf($"[ -e {imagePath} ]", $"[ -L {imagePath} ]");
        var exists = await ownership.ShellAsync($"if {occupied}; then echo exists; fi", token).ConfigureAwait(false);
        if (exists.Length > 0)
        {
            throw new SqlBranchException(ErrorCodes.ImageExists, $"Image '{image}' already exists.");
        }

        var installation = await new SqlPackageTool(process).FindAsync(sqlPackage, environment, workingDirectory, token).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.SqlPackageMissing, SqlPackageTool.InstallGuidance);
        var operation = new BacpacOperation(NewId());
        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.Build(operation.Id), token).ConfigureAwait(false))
        {
            throw new SqlBranchException($"BACPAC build {operation.Id} is in use.");
        }

        BacpacScratch? scratch = null;
        ImageManifest? manifest = null;
        ProcessOutput? importResult = null;
        Exception? failure = null;
        var phase = "stage the package";
        try
        {
            scratch = new BacpacScratch(operation.Id, engine.Id);
            await stage(scratch, token).ConfigureAwait(false);
            if (progress.SourceSizeBytes is null)
            {
                progress.ReportSize(new FileInfo(scratch.Package).Length);
            }
            phase = "validate the package";
            var (bytes, needsRewrite) = await BacpacPackage.ValidateAsync(scratch.Package, token).ConfigureAwait(false);
            BacpacProvenance? provenance = null;
            if (request is not null)
            {
                await using var package = File.OpenRead(scratch.Package);
                provenance = new BacpacProvenance(file!, Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(package, token).ConfigureAwait(false)), url);
            }
            phase = "check pool capacity";
            await RequireSpaceAsync(engine.Id, bytes, token).ConfigureAwait(false);

            var importPath = scratch.Package;
            if (needsRewrite)
            {
                using var rewrite = progress.Measure("rewrite");
                phase = "rewrite the package";
                progress.ReportRewrite(bytes);
                var detachment = await BacpacPackage.RewriteAsync(scratch, token, WrapRewriteOutput).ConfigureAwait(false);
                importPath = scratch.RewrittenPackage;
                phase = "validate the rewritten package";
                var rewritten = await BacpacPackage.ValidateAsync(importPath, token).ConfigureAwait(false);
                if (rewritten.NeedsRewrite)
                {
                    throw new SqlBranchException(ErrorCodes.OperationFailed, "The rewritten BACPAC still requires login detachment.");
                }
                progress.ReportDetachment(detachment);
            }

            phase = "check guest access and sweep abandoned imports";
            await ImageBuilder.CheckGuestAsync(session, token).ConfigureAwait(false);
            await ownership.SweepAsync(token).ConfigureAwait(false);
            phase = "check pool capacity";
            await RequireSpaceAsync(engine.Id, bytes, token).ConfigureAwait(false);
            using (progress.Measure("import"))
            {
                phase = "create the import target";
                await ownership.CreateAsync(operation, token).ConfigureAwait(false);

                phase = "create the restricted login";
                var oneTimePassword = PasswordGenerator.Generate();
                redactor.Register(oneTimePassword);
                await ownership.CreateLoginAsync(operation, oneTimePassword, token).ConfigureAwait(false);
                phase = "verify the target before import";
                await ownership.VerifyTargetAsync(operation, token).ConfigureAwait(false);
                phase = "run SqlPackage";
                importResult = await RunImportAsync(installation.Path, endpoint, operation.Database, oneTimePassword, importPath, scratch.PathName, environment, token).ConfigureAwait(false);
                if (importResult.ExitCode != 0)
                {
                    throw ImportFailure(importResult, redactor);
                }
                phase = "verify the target after import";
                await ownership.VerifyTargetAsync(operation, token).ConfigureAwait(false);
                phase = "disable the restricted login";
                await ownership.DisableLoginAsync(operation, token).ConfigureAwait(false);
            }

            phase = "remove client scratch";
            using (progress.Measure("cleanup"))
            {
                scratch.Dispose();
                scratch = null;
            }
            if (Consumer is { } consumer)
            {
                phase = "consume the imported database";
                await consumer.ConsumeAsync(new BacpacImportTarget(operation.Database, operation.Directory), token).ConfigureAwait(false);
            }
            if (request is not null)
            {
                phase = "mask and seal the imported database";
                var imageBuilder = new ImageBuilder(runtime, connector, redactor, engine.Id, Clock) { AfterStep = AfterSealStep };
                manifest = await imageBuilder.SealBacpacAsync(session, endpoint, engine.Image, ownership, operation, request,
                    provenance!, progress, token).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // All failures run cleanup. Tool, SQL and filesystem diagnostics stay private.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        var leftovers = new List<string>();
        using (progress.Measure("cleanup"))
        {
            if (scratch is not null)
            {
                try
                {
                    scratch.Dispose();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqlBranchException)
                {
                    leftovers.Add($"client scratch {scratch.Identifier}");
                }
            }
        }

        ImageBuildResult? result = null;
        var verifiedPublication = true;
        if (failure is null && leftovers.Count == 0 && manifest is not null)
        {
            bool cleaned;
            using (progress.Measure("cleanup"))
            {
                cleaned = await ownership.TryCleanupAsync(operation, keepStagedImage: true).ConfigureAwait(false);
            }

            if (!cleaned)
            {
                failure = new SqlBranchException("Could not clean up the BACPAC import before publication.");
            }
            else
            {
                try
                {
                    using var sealing = progress.Measure("seal");
                    await ownership.VerifyEngineAsync(token).ConfigureAwait(false);
                    var mode = await CloneProbe.ProbeInEngineAsync(runtime, token, engine.Id, uniqueId: operation.Id).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    // Publish the complete directory while the operation record still owns staging.
                    verifiedPublication = await ownership.PublishImageAsync(operation, manifest).ConfigureAwait(false);
                    result = new ImageBuildResult(manifest, mode);
                }
#pragma warning disable CA1031 // Leave a failed publication eligible for ownership-checked cleanup.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    phase = "publish the image";
                    failure = ex;
                }
            }
        }

        using (progress.Measure("cleanup"))
        {
            if (operation.DirectoryCreated &&
                (!verifiedPublication || !await ownership.TryCleanupAsync(operation).ConfigureAwait(false)))
            {
                leftovers.Add(result is null ? $"database or login {operation.Database} and build folder {operation.Directory}"
                    : $"build folder {operation.Directory}");
            }
        }
        var leftoverNote = leftovers.Count == 0 ? null
            : $"BACPAC cleanup could not verify or remove {string.Join(" and ", leftovers)}. Run 'sqlbranch status'.";
        if (failure is SqlBranchException known)
        {
            throw WithLeftovers(known);
        }
        if (failure is OperationCanceledException)
        {
            if (leftoverNote is not null)
            {
                throw WithLeftovers(new SqlBranchException("The BACPAC build was cancelled."));
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (failure is IOException io && BacpacScratch.IsFull(io))
        {
            throw WithLeftovers(new SqlBranchException(ErrorCodes.TempFull, $"Client scratch ran out of space while attempting to {phase}."));
        }
        if (failure is EngineSqlException sql && sql.Numbers.Any(n => n is 1101 or 1105 or 9002 or 112))
        {
            throw WithLeftovers(new SqlBranchException(ErrorCodes.PoolFull, "The engine pool ran out of space during BACPAC import or sealing."));
        }
        if (failure is not null)
        {
            throw WithLeftovers(DescribeFailure(redactor, phase, failure.GetType().Name, importResult, (failure as EngineSqlException)?.Numbers));
        }
        if (leftoverNote is not null)
        {
            if (result is not null)
            {
                var warning = verifiedPublication ? leftoverNote
                    : $"BACPAC image '{image}' was published, but its destination identity could not be verified. " +
                      $"Build folder {operation.Directory} was left for cleanup. Run 'sqlbranch status'.";
                return result with { Warnings = [warning] };
            }
            throw new SqlBranchException(leftoverNote);
        }
        return result;

        SqlBranchException WithLeftovers(SqlBranchException error) =>
            new(leftoverNote is null ? error.Code : ErrorCodes.OperationFailed,
                redactor.Redact(leftoverNote is null ? error.Message : $"{error.Message} {leftoverNote}"));
    }

    private async Task RequireSpaceAsync(string engine, long bytes, CancellationToken token)
    {
        var free = await PoolFreeSpace.FreeBytesAsync(runtime, engine, token).ConfigureAwait(false);
        if (free < bytes)
        {
            throw new SqlBranchException(ErrorCodes.PoolFull,
                FormattableString.Invariant($"BACPAC import needs {bytes} bytes in the engine pool and {free} bytes are available."));
        }
    }

    private async Task<ProcessOutput> RunImportAsync(string executable, EngineEndpoint endpoint, string database, string password,
        string importPath, string directory, IReadOnlyDictionary<string, string> environment, CancellationToken token)
    {
        var child = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = directory, ["USERPROFILE"] = directory,
            ["TMPDIR"] = directory, ["TMP"] = directory, ["TEMP"] = directory,
            ["DOTNET_CLI_HOME"] = directory,
        };
        foreach (var (key, value) in environment)
        {
            if (key.Equals("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("DOTNET_ROOT_", StringComparison.OrdinalIgnoreCase))
            {
                child[key] = value;
            }
        }
        try
        {
            return await process.RunAsync(executable,
                ["/Action:Import", $"/SourceFile:{importPath}",
                 FormattableString.Invariant($"/TargetServerName:{endpoint.Host},{endpoint.Port}"),
                 $"/TargetDatabaseName:{database}", $"/TargetUser:{database}", $"/TargetPassword:{password}",
                 "/TargetTrustServerCertificate:True", "/Diagnostics:False"],
                child, token).ConfigureAwait(false);
        }
        catch (IOException ex) when (BacpacScratch.IsFull(ex))
        {
            throw new SqlBranchException(ErrorCodes.TempFull, "Client scratch ran out of space during BACPAC import.");
        }
        catch (Win32Exception)
        {
            throw new SqlBranchException(ErrorCodes.SqlPackageMissing, SqlPackageTool.InstallGuidance);
        }

    }

    internal static SqlBranchException ImportFailure(ProcessOutput result, SecretRedactor redactor)
    {
        var diagnostic = result.StdErr + "\n" + result.StdOut;
        if (diagnostic.Contains("Msg 1101,", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("Msg 1105,", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("Msg 9002,", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("operating system error 112", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("operating system error 28", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlBranchException(ErrorCodes.PoolFull, "The engine pool ran out of space during BACPAC import.");
        }
        if (diagnostic.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("There is not enough space on the disk", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlBranchException(ErrorCodes.TempFull, "Client scratch ran out of space during BACPAC import.");
        }
        if (diagnostic.Contains("SHARD_MAP_MANAGER", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlBranchException(ErrorCodes.UnsupportedFeature, "BACPAC import does not support the SHARD_MAP_MANAGER external data source type.");
        }
        if (diagnostic.Contains("is not supported on this platform", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("is not supported in this version of SQL Server", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlBranchException(ErrorCodes.UnsupportedFeature, "BACPAC import encountered a feature unsupported by this engine.");
        }
        var numbers = SqlErrorNumber().Matches(diagnostic)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().ToArray();
        return DescribeFailure(redactor, "run SqlPackage", nameof(SqlBranchException), result,
            numbers.Length > 0 ? numbers : null);
    }

    private static SqlBranchException DescribeFailure(SecretRedactor redactor, string phase, string exceptionType,
        ProcessOutput? result, IReadOnlyList<int>? sqlNumbers = null)
    {
        var exitCode = result?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        var codes = result is null ? [] : DiagnosticCode().Matches(result.StdOut + "\n" + result.StdErr)
            .Select(match => match.Value).Distinct(StringComparer.Ordinal);
        var message = $"BACPAC import failed. Phase: {phase}. Exception type: {exceptionType}. " +
            $"SqlPackage exit code: {exitCode}. SqlPackage diagnostic codes: [{string.Join(", ", codes)}].";
        if (sqlNumbers is not null)
        {
            message += $" SQL Server error numbers: [{string.Join(", ", sqlNumbers.Select(n => n.ToString(CultureInfo.InvariantCulture)))}].";
        }
        return new SqlBranchException(redactor.Redact(message));
    }

    [GeneratedRegex(@"\bSQL[0-9]{5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticCode();

    [GeneratedRegex(@"\bMsg ([0-9]{1,6}), Level [0-9]{1,2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SqlErrorNumber();
}
