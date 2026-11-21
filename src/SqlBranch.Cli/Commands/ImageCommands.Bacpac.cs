using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SqlBranch.Core;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Cli.Commands;

internal static partial class ImageCommands
{
    private static CommandResult BuildBacpac(CliServices services, SecretRedactor redactor, string? config, string name,
        string? source, string? sqlPackage, string[] postPaths, string? timeout, string? region,
        bool keepRecoveryModel, Action<string> notice)
    {
        NameRules.ValidateImage(name);
        if (string.IsNullOrWhiteSpace(source) || (sqlPackage is not null && string.IsNullOrWhiteSpace(sqlPackage)))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, "--from-bacpac and --sqlpackage require nonblank values when specified.");
        }
        if (timeout is not null && Duration.Parse(timeout) is null)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, "--script-timeout requires a positive duration up to 365d.");
        }

        var url = BacpacUrl(source, region);
        var secrets = url is null ? null : StorageSecrets.Read(url, services.Environment, redactor);
        var path = url is null ? RequireReadable(services, source, "--from-bacpac") : null;
        var scriptPaths = postPaths.Select(p => RequireReadable(services, p, "--post-script")).ToArray();
        var progress = new BacpacBuildProgress(services.Clock) { Notice = notice };

        using var cancel = new CancellationTokenSource();
        var presses = 0;
        void OnCancel(object? sender, ConsoleCancelEventArgs args)
        {
            if (Interlocked.Increment(ref presses) == 1)
            {
                args.Cancel = true;
                cancel.Cancel();
            }
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            var context = CommandContext.RequireAsync(services, redactor, config, cancel.Token).GetAwaiter().GetResult();
            ImageCatalog.RequireRunningEngine(context.Pool);
            var request = new ImageBuildRequest(name, "", 0, KeepRecoveryModel: keepRecoveryModel)
            {
                PostScripts = scriptPaths.Select(ImageScript.Read).ToArray(),
                ScriptTimeout = timeout is null ? null : Duration.Parse(timeout),
                Url = url,
                Secrets = secrets,
            };
            var builder = new BacpacBuilder(services.Runtime, services.Connector, redactor,
                services.SqlPackageProcess ?? new SystemProcessRunner(inheritEnvironment: false), services.EngineName)
            {
                ReadSource = url is null ? null : services.BacpacReadSourceOf(redactor),
                Clock = services.Clock,
            };
            var result = builder.BuildAsync(context.Pool, context.Settings.Port, request, path, sqlPackage,
                services.Environment, services.WorkingDirectory, cancel.Token, progress).GetAwaiter().GetResult();
            return Describe(result, progress);
        }
        catch (SqlBranchException ex)
        {
            return BacpacFailure(ex.Code, ex.Message, progress);
        }
        catch (OperationCanceledException)
        {
            return BacpacFailure(ErrorCodes.OperationFailed, "The BACPAC build was cancelled.", progress);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    private static CommandResult BacpacFailure(ErrorCode code, string message, BacpacBuildProgress progress)
    {
        var payload = new JsonObject();
        var text = new List<string>();
        AddBacpacProgress(progress, payload, text);
        return CommandResult.Failure("image build", code, message, payload, textLines: text);
    }

    private static void AddBacpacProgress(BacpacBuildProgress progress, JsonObject payload, List<string> text)
    {
        payload["sourceSizeBytes"] = progress.SourceSizeBytes;
        if (progress.SourceSizeBytes is { } size)
        {
            text.Add(FormattableString.Invariant($"BACPAC package size: {size} bytes"));
        }

        // A build that failed before reading the package does not know which users it would detach.
        if (progress.DetachmentKnown)
        {
            payload["detachedUsers"] = new JsonArray([.. progress.DetachedUsers.Select(name => (JsonNode?)JsonValue.Create(name))]);
            payload["removedLogins"] = progress.RemovedLogins;
            payload["removedServerObjects"] = progress.RemovedServerObjects;
            text.Add($"Detached database users: {(progress.DetachedUsers.Count == 0 ? "none" : string.Join(", ", progress.DetachedUsers))}");
            text.Add(FormattableString.Invariant($"Removed server logins: {progress.RemovedLogins}"));
            text.Add(FormattableString.Invariant($"Removed server objects: {progress.RemovedServerObjects}"));
        }

        var phases = new JsonObject();
        foreach (var (phase, milliseconds) in progress.PhaseMilliseconds)
        {
            phases[phase] = milliseconds;
            text.Add(FormattableString.Invariant($"{phase}: {milliseconds} ms"));
        }
        payload["phaseMilliseconds"] = phases;
    }

    private static BackupUrl? BacpacUrl(string source, string? region)
    {
        if (region is not null && !source.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlBranchException(ErrorCodes.UsageError, "--s3-region applies only to an s3:// source URL.");
        }
        var separator = source.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0 && Uri.CheckSchemeName(source[..separator]) &&
            !(OperatingSystem.IsWindows() && separator == 1 && char.IsAsciiLetter(source[0])))
        {
            if (!BackupUrl.IsUrl(source) || source.Any(char.IsWhiteSpace) || source.Contains('\\') ||
                !Uri.TryCreate(source, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString())
            {
                throw new SqlBranchException(ErrorCodes.UsageError, "--from-bacpac requires a local file, an Azure Blob HTTPS URL or an S3 URL.");
            }

            return BackupUrl.Parse(source, region);
        }
        return null;
    }

    private static string RequireReadable(CliServices services, string value, string option)
    {
        var message = $"{option} must name a readable regular file.";
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var full = Path.GetFullPath(value, services.WorkingDirectory);
                if ((services.FileProbe is not null || IsRegularFile(full)) &&
                    services.ProbeFile(full) is { Exists: true, Readable: true })
                {
                    return full;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, message);
        }
        throw new SqlBranchException(ErrorCodes.UsageError, message);
    }

    private static bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Device) == 0;
        }

        // On Unix File.Exists also accepts named pipes and devices. Check the file type
        // before opening it, since opening a pipe for reading can block indefinitely.
        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/bin/test" : "/usr/bin/test")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment.Clear();
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start);
        process?.WaitForExit();
        return process?.ExitCode == 0;
    }
}
