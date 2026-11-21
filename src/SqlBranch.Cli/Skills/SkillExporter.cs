using SqlBranch.Core;
using SqlBranch.Core.Output;

namespace SqlBranch.Cli.Skills;

internal static class SkillExporter
{
    public static IReadOnlyList<string> Export(string workingDirectory, string directory, bool force,
        IReadOnlyDictionary<string, string> files, Action<string, string>? write = null, Action<string, string, string?>? install = null)
    {
        string target;
        try
        {
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory, workingDirectory));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"Invalid skill directory '{directory}': {ex.Message}");
        }
        var parent = Path.GetDirectoryName(target) ??
            throw new SqlBranchException(ErrorCodes.UsageError, $"Choose a skill directory below '{target}'.");
        // Serialize exports, including stale-directory cleanup, across CLI processes.
        using var mutex = new Mutex(false, "SqlBranch.SkillExport");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* This thread now owns the abandoned mutex. */ }
        try
        {
            return ExportLocked(target, parent, force, files, write ?? File.WriteAllText, install ?? InstallFile);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static string[] ExportLocked(string target, string parent, bool force,
        IReadOnlyDictionary<string, string> files, Action<string, string> write, Action<string, string, string?> install)
    {
        var createdParents = new Stack<string>();
        var prefix = Path.GetFileName(target) + ".sqlbranch-tmp-";
        var temp = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N")[..8]);
        var written = files.Keys.ToArray();
        try
        {
            try
            {
                EnsureDirectory(parent, createdParents);
                Probe(parent);
                if (File.Exists(target) || IsLink(target))
                {
                    throw new IOException($"'{target}' must be a directory.");
                }
                if (Directory.Exists(target))
                {
                    Probe(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SqlBranchException(ErrorCodes.UsageError, $"Cannot export to '{target}': {ex.Message}");
            }

            foreach (var sibling in Directory.EnumerateDirectories(parent))
            {
                var name = Path.GetFileName(sibling);
                if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length == prefix.Length + 8 &&
                    name[prefix.Length..].All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') && !IsLink(sibling))
                {
                    Directory.Delete(sibling, recursive: true);
                }
            }

            foreach (var file in written)
            {
                var path = Path.Combine(target, file);
                if (IsLink(Path.GetDirectoryName(path)!) || IsLink(path) || Directory.Exists(path))
                {
                    throw new SqlBranchException(ErrorCodes.UsageError, $"Cannot install generated file '{path}' over a directory or symbolic link.");
                }
                if (!force && File.Exists(path))
                {
                    throw new SqlBranchException(ErrorCodes.SkillExists, $"'{path}' already exists. Use --force to replace generated files.");
                }
            }

            Directory.CreateDirectory(temp);
            try
            {
                foreach (var (file, content) in files)
                {
                    var path = Path.Combine(temp, file);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        write(path, content);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException($"Could not write '{file}': {ex.Message}", ex);
                    }
                }
                if (Directory.Exists(target))
                {
                    Merge(temp, target, written, force, install);
                }
                else
                {
                    Directory.Move(temp, target);
                }
            }
            catch
            {
                // A second I/O failure during rollback may leave original files in backup.
                // Preserve those for recovery outside the stale staging namespace.
                var backup = Path.Combine(temp, "backup");
                if (Directory.Exists(backup) && Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Any())
                {
                    var recovery = target + ".sqlbranch-recovery-" + Guid.NewGuid().ToString("N");
                    Directory.Move(temp, recovery);
                    throw new IOException($"Rollback could not restore all files in '{target}'. Original files are in '{recovery}'.");
                }
                Directory.Delete(temp, recursive: true);
                throw;
            }
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
            return written;
        }
        catch
        {
            foreach (var path in createdParents)
            {
                if (!Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path);
                }
            }
            throw;
        }
    }

    private static void Merge(string temp, string target, string[] files, bool force, Action<string, string, string?> install)
    {
        var installed = new Stack<(string Destination, string? Backup)>();
        var directories = new Stack<string>();
        try
        {
            foreach (var file in files)
            {
                var dest = Path.Combine(target, file);
                EnsureDirectory(Path.GetDirectoryName(dest)!, directories);
                string? backup = null;
                if (force && File.Exists(dest))
                {
                    backup = Path.Combine(temp, "backup", file);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                }
                install(Path.Combine(temp, file), dest, backup);
                installed.Push((dest, backup));
            }
        }
        catch
        {
            foreach (var (dest, backup) in installed)
            {
                if (backup is null)
                {
                    File.Delete(dest);
                }
                else
                {
                    File.Move(backup, dest, overwrite: true);
                }
            }
            foreach (var dir in directories)
            {
                Directory.Delete(dir);
            }
            throw;
        }
    }

    internal static void InstallFile(string source, string destination, string? backup)
    {
        if (backup is null)
        {
            File.Move(source, destination);
        }
        else
        {
            File.Replace(source, destination, backup);
        }
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static void EnsureDirectory(string path, Stack<string> created)
    {
        if (Directory.Exists(path))
        {
            return;
        }
        EnsureDirectory(Path.GetDirectoryName(path)!, created);
        Directory.CreateDirectory(path);
        created.Push(path);
    }

    private static void Probe(string directory)
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory) &
            (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
        {
            throw new UnauthorizedAccessException($"Directory '{directory}' is read-only.");
        }
        var probe = Path.Combine(directory, ".sqlbranch-probe-" + Guid.NewGuid().ToString("N"));
        using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }
}
