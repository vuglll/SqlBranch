using System.ComponentModel;
using SqlBranch.Core.Containers;

namespace SqlBranch.Core.LogicalSources;

public sealed record SqlPackageInstallation(string Path, Version Version);

/// <summary>Locates a user-installed executable and verifies its version without credentials.</summary>
public sealed class SqlPackageTool(IProcessRunner runner)
{
    public const string InstallGuidance =
        "Install SqlPackage with 'dotnet tool install -g microsoft.sqlpackage', then put it on PATH or specify --sqlpackage <path>.";

    private static readonly StringComparison EnvironmentNameComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether a variable is needed by a SqlPackage version probe or its .NET tool shim.</summary>
    public static bool IsProbeEnvironmentVariable(string name) =>
        name.Equals("PATH", EnvironmentNameComparison) || name.Equals("HOME", EnvironmentNameComparison) ||
        name.Equals("TMPDIR", EnvironmentNameComparison) || name.Equals("DOTNET_ROOT", EnvironmentNameComparison) ||
        name.StartsWith("DOTNET_ROOT_", EnvironmentNameComparison);

    public async Task<SqlPackageInstallation?> FindAsync(
        string? explicitPath, IReadOnlyDictionary<string, string> environment, string workingDirectory, CancellationToken cancellationToken)
    {
        var probeEnvironment = new Dictionary<string, string>(StringComparer.FromComparison(EnvironmentNameComparison));
        foreach (var (name, value) in environment)
        {
            if (IsProbeEnvironmentVariable(name))
            {
                probeEnvironment[name] = value;
            }
        }

        var executable = OperatingSystem.IsWindows() ? "sqlpackage.exe" : "sqlpackage";
        var paths = explicitPath is null
            ? probeEnvironment.GetValueOrDefault("PATH", "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => directory.Trim('"'))
                .Where(directory => directory.Length > 0)
                .Select(directory => Path.Combine(directory, executable))
            : [explicitPath];

        foreach (var candidate in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var path = Path.IsPathFullyQualified(candidate) ? Path.GetFullPath(candidate) : Path.GetFullPath(candidate, workingDirectory);
                var result = await runner.RunAsync(path, ["/Version"], probeEnvironment, timeout.Token).ConfigureAwait(false);
                if (result.ExitCode == 0 && Version.TryParse(result.StdOut.Trim(), out var version))
                {
                    return new SqlPackageInstallation(path, version);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or ArgumentException or NotSupportedException)
            {
                // An absent or unusable PATH entry does not prevent trying the next one.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A timed-out candidate does not consume the next candidate's deadline.
            }
        }

        return null;
    }
}
