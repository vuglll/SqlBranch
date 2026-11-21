using System.Globalization;
using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// One engine for the whole class, started once, plus the helpers that make a database and a backup of it
/// during a test. Everything is made on the test engine only; the folder for backups on the client is the
/// test's own and is removed at the end.
/// </summary>
public class ImageEngineFixture : IAsyncLifetime
{
    public EngineWorld? World { get; private set; }

    public string ClientFolder { get; } = Path.Combine(Path.GetTempPath(), "sqlbranch-it-bak-" + DockerHost.NewId());

    public (int Exit, string Out, string Err, TimeSpan Elapsed) Init { get; private set; }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnableVariable) != "1")
        {
            return;
        }

        Directory.CreateDirectory(ClientFolder);
        World = NewWorld();
        Init = World.RunCli("init");
        await Task.CompletedTask;
    }

    /// <summary>The engine of this fixture. The default is the managed pool (XFS with reflink), so clone mode thin; 4 GB leaves room for a 1 GiB image.</summary>
    protected virtual EngineWorld NewWorld() => new("4GB");

    public virtual async Task DisposeAsync()
    {
        if (World is not null)
        {
            await World.DisposeAsync();
        }

        if (Directory.Exists(ClientFolder))
        {
            Directory.Delete(ClientFolder, recursive: true);
        }
    }

    /// <summary>Runs statements on the test engine as <c>sa</c> with no time limit on each, for work that takes as long as the data is big.</summary>
    public async Task SqlLongAsync(params string[] statements)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        await using var session = await new SqlClientEngineConnector().ConnectAsync(World!.Endpoint, World.Password, cts.Token);
        foreach (var statement in statements)
        {
            await session.ExecuteAsync(statement, new Dictionary<string, object?>(), cts.Token);
        }
    }

    /// <summary>Runs statements on the test engine as <c>sa</c>.</summary>
    public async Task SqlAsync(params string[] statements)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var session = await new SqlClientEngineConnector().ConnectAsync(World!.Endpoint, World.Password, cts.Token);
        foreach (var statement in statements)
        {
            await session.ExecuteAsync(statement, cts.Token);
        }
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var session = await new SqlClientEngineConnector().ConnectAsync(World!.Endpoint, World.Password, cts.Token);
        return await session.ScalarAsync(sql, cts.Token);
    }

    /// <summary>Copies a file the engine wrote in <c>/tmp</c> to the client's folder and removes it in the engine.</summary>
    public async Task<string> TakeFromEngineAsync(string fileName)
    {
        var host = Path.Combine(ClientFolder, fileName);
        var copy = await EngineWorld.DockerAsync("cp", $"{World!.EngineName}:/tmp/{fileName}", host);
        Assert.True(copy.ExitCode == 0, copy.StdErr);
        await World.Runtime.ExecAsync(World.EngineName, ["rm", "-f", $"/tmp/{fileName}"], CancellationToken.None);
        return host;
    }

    /// <summary>
    /// Like <see cref="MakeBackupAsync"/>, for a database of about <paramref name="rows"/> data pages: each row is 7000 bytes of one
    /// character, so a row fills a page, and the compressed backup stays small. Inserts in batches under the SIMPLE recovery model
    /// so the log stays small. The table is <c>dbo.Item (Id, Pad)</c>.
    /// </summary>
    public async Task<string> MakeLargeBackupAsync(string database, int rows)
    {
        var quoted = "[" + database + "]";
        await SqlLongAsync(
            $"CREATE DATABASE {quoted}",
            $"ALTER DATABASE {quoted} SET RECOVERY SIMPLE",
            $"EXEC {quoted}.sys.sp_executesql N'CREATE TABLE dbo.Item (Id int NOT NULL PRIMARY KEY, Pad varchar(7000) NOT NULL);'",
            $"EXEC {quoted}.sys.sp_executesql N'DECLARE @i int = 0; WHILE @i < {rows.ToString(CultureInfo.InvariantCulture)} BEGIN INSERT dbo.Item (Id, Pad) SELECT TOP (5000) @i + ROW_NUMBER() OVER (ORDER BY (SELECT 1)), REPLICATE(''x'', 7000) FROM sys.all_objects a CROSS JOIN sys.all_objects b; SET @i += 5000; END'",
            $"BACKUP DATABASE {quoted} TO DISK = N'/tmp/{database}.bak' WITH INIT, COMPRESSION");

        var host = Path.Combine(ClientFolder, database + ".bak");
        var copy = await EngineWorld.DockerAsync("cp", $"{World!.EngineName}:/tmp/{database}.bak", host);
        Assert.True(copy.ExitCode == 0, copy.StdErr);
        await World.Runtime.ExecAsync(World.EngineName, ["rm", "-f", $"/tmp/{database}.bak"], CancellationToken.None);
        await SqlLongAsync($"DROP DATABASE {quoted}");
        return host;
    }

    /// <summary>
    /// Creates database <paramref name="database"/> with a table of rows, backs it up inside the engine, copies
    /// the backup to the client's folder and drops the database again. Returns the path on the client.
    /// </summary>
    public async Task<string> MakeBackupAsync(string database, string recovery = "FULL", int rows = 100)
    {
        var quoted = "[" + database + "]";
        await SqlAsync(
            $"CREATE DATABASE {quoted}",
            $"ALTER DATABASE {quoted} SET RECOVERY {recovery}",
            $"EXEC {quoted}.sys.sp_executesql N'CREATE TABLE dbo.Item (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL); " +
            $"INSERT dbo.Item (Id, Name) SELECT TOP ({rows.ToString(CultureInfo.InvariantCulture)}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), N''item'' FROM sys.all_objects'",
            $"BACKUP DATABASE {quoted} TO DISK = N'/tmp/{database}.bak' WITH INIT");

        var host = Path.Combine(ClientFolder, database + ".bak");
        var copy = await EngineWorld.DockerAsync("cp", $"{World!.EngineName}:/tmp/{database}.bak", host);
        Assert.True(copy.ExitCode == 0, copy.StdErr);
        await World.Runtime.ExecAsync(World.EngineName, ["rm", "-f", $"/tmp/{database}.bak"], CancellationToken.None);
        await SqlAsync($"DROP DATABASE {quoted}");
        return host;
    }
}

[Trait("Category", "Integration")]
public sealed partial class ImageBuildIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    private EngineWorld World => fixture.World!;

    private async Task<string> InPoolAsync(string script)
    {
        var result = await World.Runtime.ExecAsync(World.EngineName, ["sh", "-c", script], CancellationToken.None);
        return result.StdOut.Trim();
    }

    /// <summary>Everything in the build and images folders, as a snapshot a test compares before and after.</summary>
    private Task<string> PoolSnapshotAsync() => InPoolAsync("find /pool/build /pool/images -mindepth 1 2>/dev/null | sort");

    [IntegrationFact]
    public void The_test_engine_started()
    {
        Assert.True(fixture.Init.Exit == 0, fixture.Init.Out + fixture.Init.Err);
    }

    [IntegrationFact]
    public async Task A_text_file_is_not_a_backup_and_a_missing_file_is_a_usage_error_and_the_pool_is_unchanged()
    {
        var before = await PoolSnapshotAsync();
        var notes = Path.Combine(fixture.ClientFolder, "notes.txt");
        await File.WriteAllTextAsync(notes, "this is not a backup\n");

        var text = World.RunCli("image", "build", "shop", "--from-bak", notes, "--format", "json");
        var missing = World.RunCli("image", "build", "shop", "--from-bak", Path.Combine(fixture.ClientFolder, "nope.bak"), "--format", "json");
        output.WriteLine(text.Out);
        output.WriteLine(missing.Out);

        Assert.Equal(1, text.Exit);
        Assert.Equal("not_a_backup", JsonDocument.Parse(text.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, missing.Exit);
        var message = JsonDocument.Parse(missing.Out).RootElement.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("nope.bak", message, StringComparison.Ordinal);

        // Nothing is left in the pool and no temporary database is attached.
        Assert.Equal(before, await PoolSnapshotAsync());
        Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]%'"), CultureInfo.InvariantCulture));
    }

    [IntegrationFact]
    public async Task An_invalid_name_exits_2_before_anything_is_touched()
    {
        var before = await PoolSnapshotAsync();
        var bak = await fixture.MakeBackupAsync("NameCheck");

        var run = World.RunCli("image", "build", "../branches/pr-142", "--from-bak", bak, "--format", "json");

        Assert.Equal(2, run.Exit);
        Assert.Equal("invalid_name", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before, await PoolSnapshotAsync());
    }

    [IntegrationFact]
    public async Task A_backup_made_during_the_test_builds_an_image_with_a_manifest_sealed_files_and_no_staged_copy()
    {
        var bak = await fixture.MakeBackupAsync("Shop");

        var run = World.RunCli("image", "build", "shop", "--from-bak", bak, "--format", "json");
        output.WriteLine(run.Out + run.Err);
        output.WriteLine($"image build: {run.Elapsed.TotalSeconds:F1} s");

        Assert.True(run.Exit == 0, run.Out + run.Err);
        var root = JsonDocument.Parse(run.Out).RootElement;
        var image = root.GetProperty("image");
        // The mode is whatever the pool reports, not an assumption: a pool that cannot share blocks says copy.
        var status = World.RunCli("status", "--format", "json");
        var reported = JsonDocument.Parse(status.Out).RootElement.GetProperty("pool").GetProperty("cloneMode").GetString();
        Assert.True(reported is "thin" or "copy", reported);
        Assert.Equal(reported, image.GetProperty("cloneMode").GetString());
        Assert.Equal("Shop", image.GetProperty("sourceDatabase").GetString());

        // The manifest and the files are in the pool.
        var manifest = JsonDocument.Parse(await InPoolAsync("cat /pool/images/shop/manifest.json")).RootElement;
        Assert.Equal("Shop", manifest.GetProperty("sourceDatabase").GetString());
        Assert.Equal(2, manifest.GetProperty("files").GetArrayLength());
        Assert.Equal("shop.bak", manifest.GetProperty("backupFile").GetString(), ignoreCase: true);
        Assert.Equal(64, manifest.GetProperty("backupSha256").GetString()!.Length);
        Assert.Equal("FULL", manifest.GetProperty("originalRecoveryModel").GetString());
        Assert.Equal("SIMPLE", manifest.GetProperty("sealedRecoveryModel").GetString());
        Assert.True(manifest.GetProperty("logRebuilt").GetBoolean());
        Assert.StartsWith("16.", manifest.GetProperty("engineVersion").GetString(), StringComparison.Ordinal);
        Assert.True(manifest.TryGetProperty("labels", out _));
        Assert.Equal(EngineWorld.EngineImage, manifest.GetProperty("engineImage").GetString());
        Assert.Equal(EngineWorld.EngineImage, image.GetProperty("engineImage").GetString());

        // Sealed: read-only, owned by the engine's user.
        var stat = await InPoolAsync("stat -c '%a %u %n' /pool/images/shop/data/*");
        output.WriteLine(stat);
        Assert.All(stat.Split('\n'), line => Assert.StartsWith("444 10001 ", line, StringComparison.Ordinal));
        var rebuiltLog = manifest.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("type").GetString() == "log");
        Assert.True(rebuiltLog.GetProperty("sizeBytes").GetInt64() < 8L * 1024 * 1024);

        // No staged copy, no build folder, no temporary database.
        Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/build"));
        Assert.Equal(string.Empty, await InPoolAsync("find /pool -name '*.bak' | head"));
        Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]%'"), CultureInfo.InvariantCulture));

        // The text form says the clone mode too, and status finds nothing out of place.
        Assert.Equal(0, World.RunCli("status").Exit);
    }

    [IntegrationFact]
    public async Task A_differential_backup_set_is_refused_and_the_full_set_of_the_same_file_builds()
    {
        await fixture.SqlAsync(
            "CREATE DATABASE [Diff]",
            "BACKUP DATABASE [Diff] TO DISK = N'/tmp/diff.bak' WITH INIT",
            "EXEC [Diff].sys.sp_executesql N'CREATE TABLE dbo.T (Id int)'",
            "BACKUP DATABASE [Diff] TO DISK = N'/tmp/diff.bak' WITH DIFFERENTIAL, NOINIT",
            "DROP DATABASE [Diff]");
        var bak = await fixture.TakeFromEngineAsync("diff.bak");

        var refused = World.RunCli("image", "build", "diffimg", "--from-bak", bak, "--backup-set", "2", "--format", "json");
        var built = World.RunCli("image", "build", "diffimg", "--from-bak", bak, "--format", "json");
        output.WriteLine(refused.Out);

        Assert.Equal(1, refused.Exit);
        var error = JsonDocument.Parse(refused.Out).RootElement.GetProperty("error");
        Assert.Equal("unsupported_backup_type", error.GetProperty("code").GetString());
        Assert.Contains("Only full database backups are supported", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(built.Exit == 0, built.Out + built.Err);
        Assert.Equal("Diff", JsonDocument.Parse(built.Out).RootElement.GetProperty("image").GetProperty("sourceDatabase").GetString());
        Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/build"));
    }

    [IntegrationFact]
    public async Task One_file_of_a_striped_backup_is_refused_as_an_unsupported_backup_type()
    {
        await fixture.SqlAsync(
            "CREATE DATABASE [Stripe]",
            "BACKUP DATABASE [Stripe] TO DISK = N'/tmp/stripe1.bak', DISK = N'/tmp/stripe2.bak' WITH INIT",
            "DROP DATABASE [Stripe]");
        var first = await fixture.TakeFromEngineAsync("stripe1.bak");
        await fixture.TakeFromEngineAsync("stripe2.bak");

        var run = World.RunCli("image", "build", "striped", "--from-bak", first, "--format", "json");
        output.WriteLine(run.Out);

        Assert.Equal(1, run.Exit);
        Assert.Equal("unsupported_backup_type", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/build"));
        Assert.False((await InPoolAsync("ls /pool/images")).Contains("striped", StringComparison.Ordinal));
    }

    // ---- 5.6: one build per name ----

    /// <summary>The names, sizes, modes and SHA-256 of every file under an image, as one text a test compares.</summary>
    private Task<string> ImageFingerprintAsync(string name) =>
        InPoolAsync($"cd /pool/images/{name} && find . -type f | sort | while read f; do stat -c '%a %s %n' \"$f\"; sha256sum \"$f\" | cut -d' ' -f1; done");

    private static string ErrorCodeOf(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    [IntegrationFact]
    public async Task Two_simultaneous_builds_of_one_name_give_exit_codes_0_and_1_and_status_is_clean_afterwards()
    {
        var bak = await fixture.MakeBackupAsync("Race");

        using var first = World.StartCli("image", "build", "race", "--from-bak", bak, "--format", "json");
        using var second = World.StartCli("image", "build", "race", "--from-bak", bak, "--format", "json");
        var a = await first.WaitAsync(TimeSpan.FromMinutes(5));
        var b = await second.WaitAsync(TimeSpan.FromMinutes(5));
        output.WriteLine(a.Out + a.Err);
        output.WriteLine(b.Out + b.Err);

        Assert.Equal([0, 1], new[] { a.Exit, b.Exit }.Order().ToArray());
        var loser = a.Exit == 1 ? a : b;
        Assert.Equal("image_exists", ErrorCodeOf(loser.Out));
        Assert.Equal("race", JsonDocument.Parse((a.Exit == 0 ? a : b).Out).RootElement.GetProperty("image").GetProperty("name").GetString());
        Assert.Equal(0, World.RunCli("status").Exit);
        Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/build"));
        Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]%'"), CultureInfo.InvariantCulture));
    }

    [IntegrationFact]
    public async Task A_rebuild_of_an_existing_name_exits_1_and_leaves_the_image_exactly_as_it_was()
    {
        var bak = await fixture.MakeBackupAsync("Keep", rows: 100);
        var other = await fixture.MakeBackupAsync("Other", rows: 50);
        var built = World.RunCli("image", "build", "keeper", "--from-bak", bak, "--format", "json");
        Assert.True(built.Exit == 0, built.Out + built.Err);
        var manifest = await InPoolAsync("cat /pool/images/keeper/manifest.json");
        var fingerprint = await ImageFingerprintAsync("keeper");
        var snapshot = await PoolSnapshotAsync();

        var again = World.RunCli("image", "build", "keeper", "--from-bak", other, "--format", "json");
        output.WriteLine(again.Out);

        Assert.Equal(1, again.Exit);
        Assert.Equal("image_exists", ErrorCodeOf(again.Out));
        Assert.Contains("already exists", JsonDocument.Parse(again.Out).RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(manifest, await InPoolAsync("cat /pool/images/keeper/manifest.json"));
        Assert.Equal(fingerprint, await ImageFingerprintAsync("keeper"));
        Assert.Equal("Keep", JsonDocument.Parse(manifest).RootElement.GetProperty("sourceDatabase").GetString());
        Assert.Equal(snapshot, await PoolSnapshotAsync());
        Assert.Equal(0, World.RunCli("status").Exit);
    }

    // ---- 5.7: the recovery model of the sealed image ----

    /// <summary>
    /// The recovery model the files of an image carry, read by attaching a throwaway copy of them under the test
    /// pool and dropping it again. The image itself is never attached.
    /// </summary>
    private async Task<string> SealedRecoveryModelAsync(string image, string probe)
    {
        var manifest = JsonDocument.Parse(await InPoolAsync($"cat /pool/images/{image}/manifest.json")).RootElement;
        var files = manifest.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("fileName").GetString()!).ToList();
        var folder = $"/pool/it-attach-{probe}";
        var list = string.Join(", ", files.Select(f => $"(FILENAME = N'{folder}/{f}')"));
        try
        {
            await InPoolAsync($"mkdir {folder} && cp /pool/images/{image}/data/* {folder}/ && chmod 644 {folder}/*");
            await fixture.SqlAsync($"CREATE DATABASE [{probe}] ON {list} FOR ATTACH");
            return Convert.ToString(await fixture.ScalarAsync($"SELECT recovery_model_desc FROM sys.databases WHERE name = N'{probe}'"), CultureInfo.InvariantCulture)!;
        }
        finally
        {
            await fixture.SqlAsync($"IF DB_ID(N'{probe}') IS NOT NULL BEGIN ALTER DATABASE [{probe}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{probe}]; END");
            await InPoolAsync($"rm -rf {folder}");
        }
    }

    [IntegrationFact]
    public async Task A_full_recovery_database_is_sealed_as_simple_by_default_and_as_full_with_keep_recovery_model()
    {
        var bak = await fixture.MakeBackupAsync("Recov", recovery: "FULL");

        var simple = World.RunCli("image", "build", "recov-simple", "--from-bak", bak, "--format", "json");
        var full = World.RunCli("image", "build", "recov-full", "--from-bak", bak, "--keep-recovery-model", "--format", "json");
        output.WriteLine(simple.Out);
        output.WriteLine(full.Out);

        Assert.True(simple.Exit == 0, simple.Out + simple.Err);
        Assert.True(full.Exit == 0, full.Out + full.Err);

        // What the command reports, what the manifest records, and what the sealed files carry.
        var reported = JsonDocument.Parse(simple.Out).RootElement.GetProperty("image");
        Assert.Equal("FULL", reported.GetProperty("originalRecoveryModel").GetString());
        Assert.Equal("SIMPLE", reported.GetProperty("sealedRecoveryModel").GetString());
        var simpleManifest = JsonDocument.Parse(await InPoolAsync("cat /pool/images/recov-simple/manifest.json")).RootElement;
        Assert.Equal("FULL", simpleManifest.GetProperty("originalRecoveryModel").GetString());
        Assert.Equal("SIMPLE", simpleManifest.GetProperty("sealedRecoveryModel").GetString());
        var fullManifest = JsonDocument.Parse(await InPoolAsync("cat /pool/images/recov-full/manifest.json")).RootElement;
        Assert.Equal("FULL", fullManifest.GetProperty("originalRecoveryModel").GetString());
        Assert.Equal("FULL", fullManifest.GetProperty("sealedRecoveryModel").GetString());

        Assert.Equal("SIMPLE", await SealedRecoveryModelAsync("recov-simple", "it_probe_simple"));
        Assert.Equal("FULL", await SealedRecoveryModelAsync("recov-full", "it_probe_full"));
        Assert.Equal(0, World.RunCli("status").Exit);
    }

    // ---- 5.8: a build that fails is undone ----

    private async Task<(Exception Failure, string Seen)> BuildFailingAfterAsync(string name, string bak, string step)
    {
        var pool = await World.FindPoolAsync();
        var seen = string.Empty;
        var builder = new ImageBuilder(World.Runtime, new SqlClientEngineConnector(), World.Redactor, World.EngineName)
        {
            AfterStep = async (done, _) =>
            {
                if (done == "stage")
                {
                    seen = await InPoolAsync("find /pool/build -type f | sort");
                }

                if (done == step)
                {
                    throw new InvalidOperationException("injected after " + step);
                }
            },
        };
        var request = new ImageBuildRequest(name, bak, new FileInfo(bak).Length);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(pool, World.Port, request, CancellationToken.None));
        return (failure, seen);
    }

    [IntegrationFact]
    public async Task A_failure_after_the_restore_leaves_no_image_no_staging_folder_and_no_temporary_database()
    {
        var bak = await fixture.MakeBackupAsync("Doomed");
        var before = await PoolSnapshotAsync();

        var (failure, seen) = await BuildFailingAfterAsync("doomed", bak, "restore");
        output.WriteLine(seen);

        Assert.Equal("injected after restore", failure.Message);
        Assert.EndsWith("/backup.bak", seen, StringComparison.Ordinal);   // the copy existed while the build ran
        Assert.Equal(before, await PoolSnapshotAsync());
        Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/build"));
        Assert.DoesNotContain("doomed", await InPoolAsync("ls /pool/images"), StringComparison.Ordinal);
        Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]%'"), CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, await InPoolAsync("find /pool -name '*.bak'"));
        Assert.Equal(0, World.RunCli("status").Exit);

        // The name is free again.
        var retry = World.RunCli("image", "build", "doomed", "--from-bak", bak, "--format", "json");
        Assert.True(retry.Exit == 0, retry.Out + retry.Err);
    }

    [IntegrationFact]
    public async Task A_failure_after_the_seal_removes_the_image_folder_the_build_made()
    {
        var bak = await fixture.MakeBackupAsync("DoomedLate");
        var before = await PoolSnapshotAsync();

        await BuildFailingAfterAsync("doomed-late", bak, "seal");

        Assert.Equal(before, await PoolSnapshotAsync());
        Assert.Equal(0, World.RunCli("status").Exit);
    }

    // ---- 5.10: a backup in the import folder is used where it is ----

    [IntegrationFact]
    public async Task A_backup_under_the_import_folder_is_not_copied_and_is_kept_after_the_build()
    {
        var bak = await fixture.MakeBackupAsync("Imported");
        await InPoolAsync("mkdir -p /pool/import");
        await World.Runtime.CopyToAsync(World.EngineName, bak, "/pool/import/imported.bak", SqlBranch.Core.Pools.DirectoryPool.EngineOwner, CancellationToken.None);
        var hash = await InPoolAsync("sha256sum /pool/import/imported.bak | cut -d' ' -f1");
        Assert.Equal(64, hash.Length);

        var pool = await World.FindPoolAsync();
        var duringBuild = string.Empty;
        var builder = new ImageBuilder(World.Runtime, new SqlClientEngineConnector(), World.Redactor, World.EngineName)
        {
            AfterStep = async (done, _) =>
            {
                if (done == "restore")
                {
                    duringBuild = await InPoolAsync("find /pool -name '*.bak' | sort; echo; ls -A /pool/build/*/");
                }
            },
        };
        var request = new ImageBuildRequest("imported", bak, new FileInfo(bak).Length, EngineBackupPath: "/pool/import/imported.bak");

        var result = await builder.BuildAsync(pool, World.Port, request, CancellationToken.None);
        output.WriteLine(duringBuild);

        // While the build ran, the only .bak in the pool was the import one, and the build folder held no copy of it.
        Assert.Equal("/pool/import/imported.bak", duringBuild.Split("\n\n")[0]);
        Assert.DoesNotContain("backup.bak", duringBuild, StringComparison.Ordinal);
        Assert.Equal("/pool/import/imported.bak", await InPoolAsync("find /pool -name '*.bak'"));
        Assert.Equal(hash, await InPoolAsync("sha256sum /pool/import/imported.bak | cut -d' ' -f1"));
        Assert.Equal(hash, result.Manifest.BackupSha256);
        Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/build"));
        Assert.True((await InPoolAsync("cat /pool/images/imported/manifest.json")).Contains("Imported", StringComparison.Ordinal));
        Assert.Equal(0, World.RunCli("status").Exit);

        // The tool keeps the user's backup; the test removes its own, so other tests that look for stray copies stay exact.
        await InPoolAsync("rm -f /pool/import/imported.bak");
    }
}
