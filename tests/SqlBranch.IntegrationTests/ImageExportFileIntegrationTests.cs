using System.Security.Cryptography;
using System.Text;
using SqlBranch.Core;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Pools;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 2.3 against a real container and the real GNU <c>tar</c>: a labeled container of the test image stands in for the engine and
/// holds an image folder in the layout of design D3. No SQL Server is needed, because export reads files only.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ImageExportFileIntegrationTests
{
    [IntegrationFact]
    public async Task An_image_exports_to_a_file_whose_archive_holds_the_exact_bytes_and_checksums()
    {
        var runtime = DockerHost.Runtime();
        var name = $"sqlbranch-it-export-{DockerHost.NewId()}";
        var folder = Directory.CreateTempSubdirectory("sqlbranch-it-export-").FullName;
        var dest = Path.Combine(folder, "shop.sbimg");
        await runtime.RunAsync(new ContainerSpec
        {
            Name = name,
            Image = DockerHost.Image,
            Detach = true,
            Entrypoint = string.Empty,
            Command = ["sleep", "600"],
        }, CancellationToken.None);
        try
        {
            const long dataBytes = 20L << 20;
            const long logBytes = 5L << 20;
            var manifest = new ImageManifest(
                ImageManifest.CurrentFormat, "shop", "Shop", "16.0.4135.4",
                [new ImageFile("Shop", "shop.mdf", "data", dataBytes), new ImageFile("Shop_log", "shop_log.ldf", "log", logBytes)],
                "FULL", "SIMPLE", 268_435_456, LogRebuilt: true, new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero),
                "shop.bak", new string('a', 64), new Dictionary<string, string>(), "mcr.microsoft.com/mssql/server:2022-latest");
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest.ToJson()));
            var made = await runtime.ExecAsync(name,
                ["sh", "-c",
                 $"mkdir -p /pool/images/shop/data && cd /pool/images/shop/data && head -c {dataBytes} /dev/urandom > shop.mdf && head -c {logBytes} /dev/urandom > shop_log.ldf " +
                 $"&& chmod 444 * && printf %s {encoded} | base64 -d > /pool/images/shop/manifest.json && sha256sum shop.mdf shop_log.ldf | cut -d' ' -f1"],
                CancellationToken.None);
            Assert.True(made.Succeeded, made.StdErr);
            var hashes = made.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var pool = new FoundPool(new PoolLocation(PoolKind.Directory, "/not-used"), "plain", PoolSource.EngineLabels,
                await runtime.InspectAsync(name, CancellationToken.None));
            var result = await new ImageExporter(runtime, name).ExportAsync(pool, "shop", dest, force: false, CancellationToken.None);

            Assert.Equal(new ImageExportResult("shop", 2, dataBytes + logBytes, new FileInfo(dest).Length), result);
            Assert.Equal([dest], Directory.GetFileSystemEntries(folder));
            await using var reader = await ImageArchiveReader.OpenAsync(File.OpenRead(dest), CancellationToken.None);
            Assert.Equal(manifest.ToJson(), reader.Head.Manifest.ToJson());
            var seen = new List<string>();
            ArchiveChecksums? checksums = null;
            while (await reader.NextAsync(CancellationToken.None) is { } entry)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var copy = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await entry.Data!.ReadAsync(buffer)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                    if (entry.Name == ImageArchive.ChecksumsEntry)
                    {
                        copy.Write(buffer, 0, read);
                    }
                }

                seen.Add($"{entry.Name}:{Convert.ToHexStringLower(hash.GetHashAndReset())}");
                if (entry.Name == ImageArchive.ChecksumsEntry)
                {
                    checksums = ArchiveChecksums.Parse(copy.ToArray());
                }
            }

            Assert.Equal([$"data/shop.mdf:{hashes[0]}", $"data/shop_log.ldf:{hashes[1]}", seen[^1]], seen);
            Assert.NotNull(checksums);
            Assert.Equal([hashes[0], hashes[1]], checksums.Files.Select(f => f.Sha256));

            // Not replaced without --force; the image folder is untouched either way.
            var exists = await Assert.ThrowsAsync<SqlBranchException>(() =>
                new ImageExporter(runtime, name).ExportAsync(pool, "shop", dest, force: false, CancellationToken.None));
            Assert.Equal("destination_exists", exists.Code.Code);
            var after = await runtime.ExecAsync(name, ["sh", "-c", "cd /pool/images/shop/data && sha256sum shop.mdf shop_log.ldf | cut -d' ' -f1"], CancellationToken.None);
            Assert.Equal(hashes, after.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        finally
        {
            await runtime.RemoveAsync(name, CancellationToken.None);
            Directory.Delete(folder, recursive: true);
        }
    }

    [IntegrationFact]
    public async Task An_image_removed_while_it_is_read_never_yields_an_archive()
    {
        var runtime = DockerHost.Runtime();
        var name = $"sqlbranch-it-export-{DockerHost.NewId()}";
        var folder = Directory.CreateTempSubdirectory("sqlbranch-it-export-").FullName;
        var dest = Path.Combine(folder, "gone.sbimg");
        await runtime.RunAsync(new ContainerSpec
        {
            Name = name,
            Image = DockerHost.Image,
            Detach = true,
            Entrypoint = string.Empty,
            Command = ["sleep", "600"],
        }, CancellationToken.None);
        try
        {
            // The manifest lists a second file that is not on disk, as if image rm had deleted it: tar exits non-zero.
            var manifest = new ImageManifest(
                ImageManifest.CurrentFormat, "shop", "Shop", "16.0.4135.4",
                [new ImageFile("Shop", "shop.mdf", "data", 1 << 20), new ImageFile("Shop_log", "shop_log.ldf", "log", 1 << 20)],
                "FULL", "SIMPLE", 268_435_456, LogRebuilt: true, DateTimeOffset.UtcNow, "shop.bak", new string('a', 64),
                new Dictionary<string, string>(), "mcr.microsoft.com/mssql/server:2022-latest");
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest.ToJson()));
            var made = await runtime.ExecAsync(name,
                ["sh", "-c",
                 $"mkdir -p /pool/images/shop/data && head -c 1048576 /dev/urandom > /pool/images/shop/data/shop.mdf && printf %s {encoded} | base64 -d > /pool/images/shop/manifest.json"],
                CancellationToken.None);
            Assert.True(made.Succeeded, made.StdErr);
            var pool = new FoundPool(new PoolLocation(PoolKind.Directory, "/not-used"), "plain", PoolSource.EngineLabels,
                await runtime.InspectAsync(name, CancellationToken.None));

            var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
                new ImageExporter(runtime, name).ExportAsync(pool, "shop", dest, force: false, CancellationToken.None));

            Assert.Equal("operation_failed", ex.Code.Code);
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally
        {
            await runtime.RemoveAsync(name, CancellationToken.None);
            Directory.Delete(folder, recursive: true);
        }
    }
}
