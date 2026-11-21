using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>Everything a scenario printed or left on disk, kept for the secret leak test of task 5.3.</summary>
public sealed class OutputRecorder
{
    private readonly List<(string Label, string Text)> _items = [];

    public IReadOnlyList<(string Label, string Text)> Items => _items;

    public void Add(string label, string text) => _items.Add((label, text));
}

/// <summary>
/// The MinIO cases of task 5.2, written once and run in whatever clone mode the pool has. Each takes the output format, so the
/// leak test of 5.3 can run all of them in text and in JSON. Each removes the images and branches it made.
/// </summary>
public sealed class MinioScenarios(CloudSourceFixture fixture, ITestOutputHelper output, OutputRecorder recorder)
{
    private EngineWorld World => fixture.World;

    private BranchKit Kit => new(fixture.Engine, output);

    private static string Tag(bool json) => json ? "json" : "text";

    private static string[] Format(bool json) => json ? ["--format", "json"] : [];

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private (int Exit, string Out, string Err, TimeSpan Elapsed) Run(string label, bool json, params string[] args) =>
        Record(label, World.RunCli([.. args, .. Format(json)]));

    private (int Exit, string Out, string Err, TimeSpan Elapsed) Record(string label, (int Exit, string Out, string Err, TimeSpan Elapsed) run)
    {
        recorder.Add(label + " stdout", run.Out);
        recorder.Add(label + " stderr", run.Err);
        output.WriteLine($"{label} -> {run.Exit} ({run.Elapsed.TotalSeconds:F1} s)");
        return run;
    }

    private async Task<string> InPoolAsync(string script) => await Kit.InPoolAsync(script);

    private async Task AssertNothingLeftAsync()
    {
        Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/build"));
        Assert.Equal(string.Empty, await InPoolAsync("find /pool -name '*.bak'"));
        Assert.Equal(0, await fixture.CountCredentialsAsync("name LIKE N's3://%'"));
        Assert.Equal(0, Convert.ToInt32(await fixture.Engine.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]%'"), CultureInfo.InvariantCulture));
    }

    private async Task RemoveImageAsync(string image)
    {
        // Every manifest is read before it goes, so the leak test searches it.
        var manifest = await InPoolAsync($"cat /pool/images/{image}/manifest.json 2>/dev/null");
        if (manifest.Length > 0)
        {
            recorder.Add("manifest " + image, manifest);
        }

        var removed = World.RunCli("image", "rm", image, "--force");
        output.WriteLine($"cleanup: image rm {image} -> {removed.Exit}");
        await InPoolAsync($"rm -rf /pool/images/{image}");
    }

    private static (string Name, string Code, string Message) Failure(bool json, (int Exit, string Out, string Err, TimeSpan Elapsed) run)
    {
        if (!json)
        {
            return (string.Empty, string.Empty, run.Out + run.Err);
        }

        var error = Parse(run.Out).GetProperty("error");
        return (string.Empty, error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    /// <summary>A URL build: the image is listed, no backup file is in the pool, no credential is left, and the manifest says where it came from.</summary>
    public async Task BuildFromUrlAsync(bool json)
    {
        var image = "s3shop-" + Tag(json);
        var url = fixture.S3Url("nightly/shop.bak");
        try
        {
            var build = Run($"image build {image}", json, "image", "build", image, "--from-bak", url);
            Assert.True(build.Exit == 0, build.Out + build.Err);
            var mode = Parse(Record("status", World.RunCli("status", "--format", "json")).Out).GetProperty("pool").GetProperty("cloneMode").GetString()!;
            Assert.Contains(mode, build.Out, StringComparison.Ordinal);

            var list = Run("image list", true, "image", "list");
            var listed = Parse(list.Out).GetProperty("images").EnumerateArray().Single(i => i.GetProperty("name").GetString() == image);
            Assert.Equal(url, listed.GetProperty("backupUrl").GetString());
            Assert.Equal("shop.bak", listed.GetProperty("backupFile").GetString());
            Assert.Equal(JsonValueKind.Null, listed.GetProperty("backupSha256").ValueKind);
            Assert.Contains(url, Run("image list text", false, "image", "list").Out, StringComparison.Ordinal);

            var manifestText = await InPoolAsync($"cat /pool/images/{image}/manifest.json");
            recorder.Add("manifest " + image, manifestText);
            var manifest = Parse(manifestText);
            Assert.Equal("backup", manifest.GetProperty("sourceType").GetString());
            Assert.Equal(url, manifest.GetProperty("backupUrl").GetString());
            Assert.Equal("shop.bak", manifest.GetProperty("backupFile").GetString());
            Assert.Equal(JsonValueKind.Null, manifest.GetProperty("backupSha256").ValueKind);
            Assert.Equal("Shop", manifest.GetProperty("sourceDatabase").GetString());

            await AssertNothingLeftAsync();
            Assert.Equal(0, Record("status", World.RunCli("status")).Exit);
        }
        finally
        {
            await RemoveImageAsync(image);
        }
    }

    /// <summary>A post-build script that copies <c>sys.credentials</c> into a table sees no credential of the build.</summary>
    public async Task PostScriptSeesNoCredentialAsync(bool json)
    {
        var image = "s3probe-" + Tag(json);
        var branch = "probe-" + Tag(json);
        var probe = Path.Combine(fixture.Engine.ClientFolder, "probe.sql");
        await File.WriteAllTextAsync(probe, "SELECT name INTO dbo.seen FROM sys.credentials;");
        try
        {
            var build = Run($"image build {image}", json, "image", "build", image, "--from-bak", fixture.S3Url("shop.bak"), "--post-script", probe);
            Assert.True(build.Exit == 0, build.Out + build.Err);

            await Kit.CreateAsync(branch, image);
            await using var connection = await Kit.ConnectAsync(branch);
            Assert.Equal(0, await BranchKit.CountAsync(connection, "SELECT COUNT(*) FROM dbo.seen WHERE name LIKE N'%' + N'" + fixture.Minio!.Address + "%'"));
            await AssertNothingLeftAsync();
        }
        finally
        {
            await Kit.DropBranchAsync(branch);
            await RemoveImageAsync(image);
        }
    }

    /// <summary>A wrong secret: backup_unreachable, no image, no credential, and status is clean.</summary>
    public async Task WrongSecretAsync(bool json)
    {
        var image = "s3wrong-" + Tag(json);
        var url = fixture.S3Url("shop.bak");
        var run = Record("image build with a wrong secret", fixture.RunCliWith(
            new Dictionary<string, string> { ["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = fixture.WrongS3Secret },
            ["image", "build", image, "--from-bak", url, .. Format(json)]));

        Assert.Equal(1, run.Exit);
        var failure = Failure(json, run);
        if (json)
        {
            Assert.Equal("backup_unreachable", failure.Code);
        }
        else
        {
            // Text output prints "error: <message>" and never the code.
            Assert.Contains("error: The engine could not read the backup", run.Out + run.Err, StringComparison.Ordinal);
        }

        Assert.Contains(url, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(image, Parse(Record("image list", World.RunCli("image", "list", "--format", "json")).Out).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
        Assert.Equal(0, Record("status", World.RunCli("status")).Exit);
        await AssertNothingLeftAsync();
    }

    /// <summary>A second MinIO signed by an authority the engine does not know: backup_unreachable that says why.</summary>
    public async Task UntrustedCertificateAsync(bool json)
    {
        var image = "s3untrusted-" + Tag(json);
        var url = $"s3://{fixture.UntrustedMinio!.Address}:9000/{CloudSourceFixture.Bucket}/shop.bak";
        var run = Run($"image build {image} from the untrusted server", json, "image", "build", image, "--from-bak", url);

        Assert.Equal(1, run.Exit);
        var failure = Failure(json, run);
        if (json)
        {
            Assert.Equal("backup_unreachable", failure.Code);
        }

        Assert.Contains("does not trust the endpoint's certificate", failure.Message, StringComparison.Ordinal);
        if (json)
        {
            Assert.Contains(url, failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, Record("status", World.RunCli("status")).Exit);
        await AssertNothingLeftAsync();
        Assert.Equal(0, await fixture.CountCredentialsAsync($"name LIKE N's3://{fixture.UntrustedMinio.Address}%'"));
    }

    /// <summary>Two builds from one folder at the same moment: one waits for the other's credential, and both succeed.</summary>
    public async Task ParallelBuildsAsync(bool json)
    {
        var a = "s3par-a-" + Tag(json);
        var b = "s3par-b-" + Tag(json);
        try
        {
            using var first = World.StartCli(["image", "build", a, "--from-bak", fixture.S3Url("shop.bak"), .. Format(json)]);
            using var second = World.StartCli(["image", "build", b, "--from-bak", fixture.S3Url("crm.bak"), .. Format(json)]);
            var one = await first.WaitAsync(TimeSpan.FromMinutes(5));
            var two = await second.WaitAsync(TimeSpan.FromMinutes(5));
            recorder.Add("parallel build a stdout", one.Out);
            recorder.Add("parallel build a stderr", one.Err);
            recorder.Add("parallel build b stdout", two.Out);
            recorder.Add("parallel build b stderr", two.Err);
            output.WriteLine($"parallel: {one.Exit} and {two.Exit}");

            Assert.True(one.Exit == 0, one.Out + one.Err);
            Assert.True(two.Exit == 0, two.Out + two.Err);
            var names = Parse(Record("image list", World.RunCli("image", "list", "--format", "json")).Out).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
            Assert.Contains(a, names);
            Assert.Contains(b, names);
            await AssertNothingLeftAsync();
            Assert.Equal(0, Record("status", World.RunCli("status")).Exit);
        }
        finally
        {
            await RemoveImageAsync(a);
            await RemoveImageAsync(b);
        }
    }

    private async Task<string> BuildDatabasesAsync() =>
        Convert.ToString(await fixture.Engine.ScalarAsync("SELECT TOP (1) CONVERT(nvarchar(200), name) COLLATE DATABASE_DEFAULT + N':' + CONVERT(nvarchar(60), state_desc) COLLATE DATABASE_DEFAULT FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]%'"), CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// A build killed during the restore leaves its credential. Status lists it, and the next build (from a file) drops it and
    /// leaves the credential <c>app_cred</c> that was made by hand.
    /// </summary>
    public async Task KilledBuildCredentialIsRemovedByTheNextBuildAsync(bool json)
    {
        var killed = "s3killed-" + Tag(json);
        var next = "crm-" + Tag(json);
        var name = fixture.S3CredentialName;
        await fixture.Engine.SqlAsync("IF NOT EXISTS (SELECT 1 FROM sys.credentials WHERE name = N'app_cred') CREATE CREDENTIAL [app_cred] WITH IDENTITY = N'it-app-user', SECRET = N'it-app-secret'");
        try
        {
            using var build = World.StartCli(["image", "build", killed, "--from-bak", fixture.S3Url("big.bak"), .. Format(json)]);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                Assert.False(build.HasExited, "The build ended before the restore started.");
                Assert.True(clock.Elapsed < TimeSpan.FromMinutes(4), "The restore never started.");
                await Task.Delay(100);
                if ((await BuildDatabasesAsync()).EndsWith(":RESTORING", StringComparison.Ordinal) &&
                    await fixture.CountCredentialsAsync($"name = N'{name}'") == 1)
                {
                    break;
                }
            }

            build.Kill();
            recorder.Add("build folder of the killed build", await InPoolAsync("for f in /pool/build/*/*.json; do echo \"== $f\"; cat \"$f\"; done 2>/dev/null; ls -A /pool/build/*"));

            (int Exit, string Out, string Err, TimeSpan Elapsed) status;
            clock.Restart();
            do
            {
                Assert.True(clock.Elapsed < TimeSpan.FromMinutes(6), "status never reported the dead build.");
                await Task.Delay(1000);
                status = World.RunCli("status", "--format", "json");
                if ((int)clock.Elapsed.TotalSeconds % 30 == 0)
                {
                    output.WriteLine($"{clock.Elapsed.TotalSeconds:F0} s after the kill: status {status.Exit}, database {await BuildDatabasesAsync()}, {status.Out.ReplaceLineEndings(" ")}");
                }
            }
            while (status.Exit == 0);

            Record("status after the kill", status);
            Assert.Equal(1, status.Exit);
            var leftovers = Parse(status.Out).GetProperty("mismatches").EnumerateArray().ToList();
            var credential = Assert.Single(leftovers, m => m.GetProperty("kind").GetString() == "build_credential");
            Assert.Equal(name, credential.GetProperty("name").GetString());
            recorder.Add("build folder of the dead build", await InPoolAsync("for f in /pool/build/*/*.json; do echo \"== $f\"; cat \"$f\"; done 2>/dev/null; ls -A /pool/build/*"));
            Assert.Equal(1, await fixture.CountCredentialsAsync($"name = N'{name}'"));

            var again = Run($"image build {next}", json, "image", "build", next, "--from-bak", fixture.CrmBackup);
            Assert.True(again.Exit == 0, again.Out + again.Err);
            Assert.Equal(0, await fixture.CountCredentialsAsync($"name = N'{name}'"));
            Assert.Equal(1, await fixture.CountCredentialsAsync("name = N'app_cred'"));
        }
        finally
        {
            await RemoveImageAsync(next);
            await RemoveImageAsync(killed);
            await fixture.Engine.SqlAsync(
                "IF EXISTS (SELECT 1 FROM sys.credentials WHERE name = N'app_cred') DROP CREDENTIAL [app_cred]",
                $"IF EXISTS (SELECT 1 FROM sys.credentials WHERE name = N'{name}') DROP CREDENTIAL [{name}]",
                "DECLARE @n sysname, @sql nvarchar(400); DECLARE c CURSOR FOR SELECT name FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]%'; OPEN c; FETCH NEXT FROM c INTO @n; " +
                "WHILE @@FETCH_STATUS = 0 BEGIN SET @sql = N'DROP DATABASE ' + QUOTENAME(@n); EXEC (@sql); FETCH NEXT FROM c INTO @n; END; CLOSE c; DEALLOCATE c;");
            await InPoolAsync("rm -rf /pool/build/*");
        }
    }
}
