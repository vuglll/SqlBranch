using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

namespace SqlBranch.Testing.Tests;

public sealed class PackageTests
{
    [Fact]
    public async Task Packed_library_contains_core_and_declares_only_runtime_dependencies()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SqlBranch.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var output = Directory.CreateTempSubdirectory("sqlbranch-package-");
        try
        {
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = directory.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "pack", "src/SqlBranch.Testing", "--no-restore", "--output", output.FullName })
            {
                info.ArgumentList.Add(arg);
            }
            using var pack = Process.Start(info)!;
            var stdout = pack.StandardOutput.ReadToEndAsync();
            var stderr = pack.StandardError.ReadToEndAsync();
            await pack.WaitForExitAsync();
            Assert.True(pack.ExitCode == 0, await stdout + await stderr);
            using var zip = ZipFile.OpenRead(Assert.Single(output.GetFiles("*.nupkg")).FullName);
            Assert.NotNull(zip.GetEntry("lib/net10.0/SqlBranch.Testing.dll"));
            Assert.NotNull(zip.GetEntry("lib/net10.0/SqlBranch.Core.dll"));
            Assert.NotNull(zip.GetEntry("lib/net10.0/SqlBranch.Core.xml"));
            using var nuspec = Assert.Single(zip.Entries, e => e.Name.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
            var deps = XDocument.Load(nuspec).Descendants().Where(e => e.Name.LocalName == "dependency");
            Assert.Equal(["AWSSDK.S3", "Azure.Storage.Blobs", "Microsoft.Data.SqlClient", "YamlDotNet", "ZstdSharp.Port"], deps.Select(e => (string?)e.Attribute("id")).Order(StringComparer.Ordinal));
        }
        finally { output.Delete(recursive: true); }
    }
}
