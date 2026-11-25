using System.Diagnostics;
using System.Security.Cryptography;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Tests.Fakes;
using SqlBranch.Core.Tests.Images;

namespace SqlBranch.Core.Tests.Cli;

public sealed class PoolUpgradeTemporaryTests
{
    [UnixTheory]
    [InlineData("credentials:running:write", "/pool/engine/credentials.json.new")]
    [InlineData("catalog:shop:write", "/pool/images/shop/catalog.json.new")]
    [InlineData("id:shop:write", "/pool/images/.ids/shop.new")]
    [InlineData("branch:dev1:write", "/pool/branches/dev1/branch.json.new")]
    public void Each_of_the_four_temporary_files_is_named_by_status_and_only_that_file_is_deleted_on_rerun(string kill, string temporary)
    {
        var pool = new UpgradeWorld();
        pool.AddImage("shop");
        pool.AddBranch("dev1");
        pool.AfterStep = step => { if (step == kill) { throw new IOException("Simulated death with temporary file present"); } };
        Assert.NotEqual(0, pool.Run("pool", "upgrade", "--yes").Exit);
        Assert.Equal([temporary], pool.TemporaryFiles.Keys);
        pool.AfterStep = null;

        // Run the product's actual listing and deletion scripts through a local shell in the fake runtime.
        // Metadata writes remain modeled by UpgradeWorld; every other file here must survive byte-for-byte.
        var root = Directory.CreateTempSubdirectory("sqlbranch-upgrade-files-").FullName;
        string Local(string path) => path.Replace("/pool", root, StringComparison.Ordinal);
        void Write(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Local(path))!);
            File.WriteAllText(Local(path), value);
        }
        try
        {
            foreach (var path in new[]
            {
                "/pool/engine/credentials.json", "/pool/engine/credentials.json.new.backup",
                "/pool/images/shop/manifest.json", "/pool/images/shop/data/shop.mdf", "/pool/images/shop/data/shop.ldf",
                "/pool/images/shop/catalog.json", "/pool/images/shop/unrelated.new", "/pool/images/.ids/shop",
                "/pool/branches/dev1/branch.json", "/pool/branches/dev1/data/shop.mdf", "/pool/branches/dev1/other.new",
                "/pool/build/staging/catalog.json.new", "/pool/images/.removing-old/catalog.json.new", "/pool/readme.new",
            })
            {
                Write(path, "keep bytes of " + path);
            }
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            Write(temporary, pool.TemporaryFiles[temporary]);
            var previous = pool.World.Runtime.ExecHandler;
            pool.World.Runtime.ExecHandler = (container, args) =>
            {
                var script = args[^1];
                if (script == StatusReporter.ListingScript)
                {
                    var listing = Shell(Local(UpgradeTemporaryFiles.ListingScript)).Replace(root, "/pool", StringComparison.Ordinal);
                    return FakeContainerRuntime.Ok(listing + "SQLBRANCH_SPACE|100 50 50\n");
                }
                if (script.StartsWith(UpgradeTemporaryFiles.CleanupPrefix, StringComparison.Ordinal))
                {
                    Shell(Local(script));
                    Assert.False(File.Exists(Local(temporary)));
                    Assert.Equal(before.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
                    foreach (var (path, hash) in before)
                    {
                        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
                    }
                }
                return previous(container, args);
            };
            var status = pool.Run("status", "--format", "json");
            Assert.Equal(1, status.Exit);
            Assert.Contains(temporary, status.Out, StringComparison.Ordinal);
            Assert.Contains("sqlbranch pool upgrade --yes", status.Out, StringComparison.Ordinal);
            pool.World.Connector.HeldLocks.Add(EngineLocks.PoolUpgrade);
            var live = pool.Run("status", "--format", "json");
            Assert.Equal(0, live.Exit);
            Assert.Contains(temporary, live.Out, StringComparison.Ordinal);
            pool.World.Connector.HeldLocks.Clear();
            Assert.Equal(0, pool.Run("pool", "upgrade", "--yes").Exit);
            Assert.Equal([temporary], pool.DeletedFiles);
            Assert.False(File.Exists(Local(temporary)));
            foreach (var (path, hash) in before)
            {
                Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Shell(string script)
    {
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
