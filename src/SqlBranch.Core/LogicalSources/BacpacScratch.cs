using System.Security.AccessControl;
using System.Security.Principal;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.LogicalSources;

internal sealed class BacpacScratch : IDisposable
{
    private const string Marker = ".sqlbranch-bacpac";
    private const string Prefix = "sqlbranch-bacpac-";
    private readonly string _identity;
    private readonly string _directoryIdentity;
    private readonly FileStream _lock;
    private readonly DirectoryInfo _directory;
    private string? _packageIdentity;
    private string? _rewrittenIdentity;
    private string? _spoolIdentity;

    public BacpacScratch(string id, string engineId)
    {
        _identity = $"{engineId}|{Guid.NewGuid():N}";
        _directory = CreatePrivateDirectory($"{Prefix}{id}-");
        string? directoryIdentity = null;
        FileStream? handle = null;
        var marker = Path.Combine(PathName, Marker);
        try
        {
            directoryIdentity = BacpacScratchIdentity.Read(PathName);
            _directoryIdentity = directoryIdentity;
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            handle = new FileStream(marker, options);
            handle.Write(System.Text.Encoding.ASCII.GetBytes(_identity));
            handle.Flush();
            _lock = handle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or SqlBranchException)
        {
            try
            {
                handle?.Dispose();
                if (directoryIdentity is null || !MatchesDirectory(directoryIdentity))
                {
                    throw new IOException();
                }
                if (handle is not null)
                {
                    File.Delete(marker);
                }
                _directory.Delete();
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                throw new SqlBranchException($"Could not remove incomplete BACPAC client scratch {Identifier}.");
            }
            throw;
        }
    }

    public string PathName => _directory.FullName;
    public string Identifier => _directory.Name;
    public string Package => Path.Combine(PathName, "source.bacpac");
    public string RewrittenPackage => Path.Combine(PathName, "rewritten.bacpac");
    private string Spool => Path.Combine(PathName, "rewritten-xml.tmp");

    private static DirectoryInfo CreatePrivateDirectory(string prefix)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Directory.CreateTempSubdirectory(prefix);
        }

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new SqlBranchException("Could not identify the owner of BACPAC scratch.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}"));
        directory.Create(security);
        return directory;
    }

    public async Task CopyAsync(string source, CancellationToken token)
    {
        try
        {
            await using var input = File.OpenRead(source);
            await using var output = CreatePackage();
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
        catch (IOException ex) when (IsFull(ex))
        {
            throw new SqlBranchException(ErrorCodes.TempFull, "Client scratch ran out of space while staging the BACPAC for import.");
        }
    }

    internal FileStream CreatePackage() => CreateOwnedFile(Package, ref _packageIdentity);

    internal FileStream CreateRewrittenPackage() => CreateOwnedFile(RewrittenPackage, ref _rewrittenIdentity);

    internal FileStream CreateRewriteSpool() => CreateOwnedFile(Spool, ref _spoolIdentity, FileAccess.ReadWrite);

    private static FileStream CreateOwnedFile(string path, ref string? identity, FileAccess access = FileAccess.Write)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = access, Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        var stream = new FileStream(path, options);
        try
        {
            identity = BacpacScratchIdentity.Read(stream);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        var marker = new FileInfo(Path.Combine(PathName, Marker));
        if (!MatchesDirectory(_directoryIdentity) ||
            !marker.Exists || (marker.Attributes & FileAttributes.ReparsePoint) != 0 ||
            marker.Length != _identity.Length || File.ReadAllText(marker.FullName) != _identity ||
            !MatchesFile(Package, _packageIdentity) || !MatchesFile(RewrittenPackage, _rewrittenIdentity) ||
            !MatchesFile(Spool, _spoolIdentity, allowMissing: true))
        {
            throw new SqlBranchException($"Could not verify ownership of client scratch {Identifier}.");
        }
        _directory.Delete(recursive: true);
    }

    private bool MatchesDirectory(string identity)
    {
        _directory.Refresh();
        return _directory.Exists && (_directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
            BacpacScratchIdentity.Read(PathName) == identity;
    }

    private static bool MatchesFile(string path, string? identity, bool allowMissing = false)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                identity is not null && BacpacScratchIdentity.Read(path) == identity;
        }
        catch (FileNotFoundException)
        {
            return allowMissing || identity is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> FindAbandoned(string? engineId)
    {
        if (string.IsNullOrEmpty(engineId))
        {
            return [];
        }

        var names = new List<string>();
        try
        {
            foreach (var directory in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories(Prefix + "l*"))
            {
                var name = directory.Name;
                var tail = name.AsSpan(Prefix.Length);
                var separator = tail.IndexOf('-');
                if (separator < 0 || !BacpacOperation.IsLogicalId(tail[..separator].ToString()) ||
                    !SafeSuffix(tail[(separator + 1)..]) || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var marker = new FileInfo(Path.Combine(directory.FullName, Marker));
                try
                {
                    if (!marker.Exists || (marker.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        marker.Length != engineId.Length + 33)
                    {
                        continue;
                    }
                    using var stream = new FileStream(marker.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
                    using var reader = new StreamReader(stream);
                    if (reader.ReadToEnd().StartsWith(engineId + "|", StringComparison.Ordinal))
                    {
                        names.Add(name);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A live build has the marker open exclusively. Another user's scratch is private.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Local scratch may be inaccessible, so engine recovery records still report the operation id.
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static bool SafeSuffix(ReadOnlySpan<char> suffix)
    {
        if (suffix.IsEmpty || suffix.Length > 32)
        {
            return false;
        }
        foreach (var character in suffix)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool IsFull(IOException ex) =>
        (ex.HResult & 0xffff) is 28 or 39 or 112 ||
        ex.Message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase);
}
