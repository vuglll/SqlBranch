using System.Globalization;
using SqlBranch.Core.Engine;

namespace SqlBranch.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class CloudSourceGroup : ICollectionFixture<CloudSourceFixture>
{
    public const string Name = "CloudSources";
}

/// <summary>
/// One SqlBranch test engine plus the local storage servers of design D10: a MinIO the engine trusts, a second MinIO whose
/// certificate authority it does not know, and Azurite. Every container carries the fixture label and is removed here;
/// the engine and its pool are the world's own, as in every other integration test.
/// </summary>
public sealed class CloudSourceFixture : IAsyncLifetime, IDisposable
{
    public const string Bucket = "backups";
    public const string BlobContainer = "backups";
    private const string CaFolder = "/var/opt/mssql/security/ca-certificates";

    private TestPki? _trusted;
    private TestPki? _unknown;

    public ImageEngineFixture Engine { get; } = new();

    public EngineWorld World => Engine.World!;

    public string RunId { get; } = DockerHost.NewId();

    public MinioServer? Minio { get; private set; }

    public MinioServer? UntrustedMinio { get; private set; }

    public AzuriteServer? Azurite { get; private set; }

    public string S3KeyId { get; } = FixtureDocker.RandomText(20);

    public string S3Secret { get; } = FixtureDocker.RandomText(40);

    public string WrongS3Secret { get; } = FixtureDocker.RandomText(40);

    public string SasToken { get; private set; } = string.Empty;

    public string SasSignature { get; private set; } = string.Empty;

    /// <summary>A SAS token of the right shape whose signature is wrong.</summary>
    public string WrongSasToken { get; private set; } = string.Empty;

    /// <summary>A full backup of database Shop (100 rows in <c>dbo.Item</c>) on the client.</summary>
    public string ShopBackup { get; private set; } = string.Empty;

    /// <summary>A backup of a database whose restore takes a while, for the killed-build case.</summary>
    public string BigBackup { get; private set; } = string.Empty;

    public string CrmBackup { get; private set; } = string.Empty;

    public string S3Url(string path) => $"s3://{Minio!.Address}:9000/{Bucket}/{path}";

    public string S3CredentialName => $"s3://{Minio!.Address}:9000/{Bucket}";

    public string BlobUrl(string blob) => $"https://{Azurite!.Host}/{BlobContainer}/{blob}";

    public string BlobCredentialName => $"https://{Azurite!.Host}/{BlobContainer}";

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnableVariable) != "1")
        {
            return;
        }

        try
        {
            await StartAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task StartAsync()
    {
        await Engine.InitializeAsync();
        Assert.True(Engine.Init.Exit == 0, Engine.Init.Out + Engine.Init.Err);

        _trusted = new TestPki("sqlbranch-it-trusted-ca-" + RunId);
        _unknown = new TestPki("sqlbranch-it-unknown-ca-" + RunId);
        Minio = await MinioServer.StartAsync("trusted", RunId, _trusted, S3KeyId, S3Secret);
        UntrustedMinio = await MinioServer.StartAsync("unknown", RunId, _unknown, S3KeyId, S3Secret);
        Azurite = await AzuriteServer.StartAsync(RunId, _trusted);

        await TrustCaAsync(_trusted.CaPem);
        // The engine learns the Blob host name only here: a restart of the engine container would drop the entry.
        await EngineWorld.DockerAsync("exec", "-u", "0", World.EngineName, "sh", "-c", $"echo '{Azurite.Address} {Azurite.Host}' >> /etc/hosts");

        (SasToken, SasSignature) = Azurite.ContainerSas(BlobContainer);
        WrongSasToken = SasToken.Replace(Uri.EscapeDataString(SasSignature), Uri.EscapeDataString(Convert.ToBase64String(new byte[32])), StringComparison.Ordinal);
        World.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = S3KeyId;
        World.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = S3Secret;
        World.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = SasToken;

        ShopBackup = await Engine.MakeBackupAsync("Shop");
        CrmBackup = await Engine.MakeBackupAsync("Crm", rows: 10);
        BigBackup = await MakeBigBackupAsync();

        await Minio.PutAsync($"/{Bucket}", []);
        await Minio.PutAsync($"/{Bucket}/shop.bak", await File.ReadAllBytesAsync(ShopBackup));
        await Minio.PutAsync($"/{Bucket}/nightly/shop.bak", await File.ReadAllBytesAsync(ShopBackup));
        await Minio.PutAsync($"/{Bucket}/crm.bak", await File.ReadAllBytesAsync(CrmBackup));
        await Minio.PutAsync($"/{Bucket}/big.bak", await File.ReadAllBytesAsync(BigBackup));
        await Azurite.CreateContainerAsync(BlobContainer);
        await Azurite.PutBlobAsync(BlobContainer, "shop.bak", await File.ReadAllBytesAsync(ShopBackup));
    }

    /// <summary>
    /// Task 5.1: puts the run's CA where the engine reads trusted authorities and restarts the engine, which reads the folder
    /// only when SQL Server starts.
    /// </summary>
    private async Task TrustCaAsync(string caPem)
    {
        var file = Path.Combine(Path.GetTempPath(), $"sqlbranch-it-ca-{RunId}.crt");
        await File.WriteAllTextAsync(file, caPem);
        try
        {
            var make = await EngineWorld.DockerAsync("exec", "-u", "0", World.EngineName, "mkdir", "-p", CaFolder);
            Assert.True(make.ExitCode == 0, make.StdErr);
            var copy = await EngineWorld.DockerAsync("cp", file, $"{World.EngineName}:{CaFolder}/sqlbranch-it-ca.crt");
            Assert.True(copy.ExitCode == 0, copy.StdErr);
        }
        finally
        {
            File.Delete(file);
        }

        var stop = World.RunCli("engine", "stop");
        Assert.True(stop.Exit == 0, stop.Out + stop.Err);
        var start = World.RunCli("engine", "start");
        Assert.True(start.Exit == 0, start.Out + start.Err);
        Assert.True(await World.CanLoginAsync(World.Password), "The engine does not take logins after the restart.");
    }

    /// <summary>A database with a data file big enough that its restore takes a while, backed up and dropped.</summary>
    private async Task<string> MakeBigBackupAsync()
    {
        await Engine.SqlAsync(
            "CREATE DATABASE [Big]",
            "ALTER DATABASE [Big] MODIFY FILE (NAME = N'Big', SIZE = 700MB)",
            // One row fills a page, so the backup holds about 160 MB that the engine reads over the network: the restore lasts long enough to kill the client in it.
            "EXEC [Big].sys.sp_executesql N'CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, Pad char(8000) NOT NULL); INSERT dbo.T SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), ''x'' FROM sys.all_objects a CROSS JOIN sys.all_objects b'",
            "BACKUP DATABASE [Big] TO DISK = N'/tmp/big.bak' WITH INIT");
        var bak = await Engine.TakeFromEngineAsync("big.bak");
        await Engine.SqlAsync("DROP DATABASE [Big]");
        return bak;
    }

    /// <summary>Runs the tool with some environment variables replaced for this one run, then puts them back.</summary>
    public (int Exit, string Out, string Err, TimeSpan Elapsed) RunCliWith(IReadOnlyDictionary<string, string> replace, params string[] args)
    {
        var saved = replace.Keys.ToDictionary(k => k, k => World.Environment.GetValueOrDefault(k));
        try
        {
            foreach (var (key, value) in replace)
            {
                World.Environment[key] = value;
            }

            return World.RunCli(args);
        }
        finally
        {
            foreach (var (key, value) in saved)
            {
                if (value is null)
                {
                    World.Environment.Remove(key);
                }
                else
                {
                    World.Environment[key] = value;
                }
            }
        }
    }

    public async Task<int> CountCredentialsAsync(string where) =>
        Convert.ToInt32(await Engine.ScalarAsync($"SELECT COUNT(*) FROM sys.credentials WHERE {where}"), CultureInfo.InvariantCulture);

    public async Task DisposeAsync()
    {
        // Only the containers this fixture made, found by name and checked by label inside RemoveOwnAsync.
        foreach (var server in new IAsyncDisposable?[] { Azurite, UntrustedMinio, Minio })
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }
        }

        await Engine.DisposeAsync();
    }

    public void Dispose()
    {
        _trusted?.Dispose();
        _unknown?.Dispose();
    }
}
