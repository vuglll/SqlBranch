namespace SqlBranch.Core.Tests.Configuration;

/// <summary>A throwaway directory tree for config file tests.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Directory.CreateTempSubdirectory("sqlbranch-config-").FullName;
    }

    public string Root { get; }

    public string Dir(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
