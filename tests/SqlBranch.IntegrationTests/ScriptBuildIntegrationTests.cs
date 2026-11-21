using SqlBranch.Core.Images;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ScriptBuildIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    private EngineWorld World => fixture.World!;

    [IntegrationFact]
    public async Task Broker_delivers_a_marker_before_sealing_and_holds_new_messages_in_a_branch()
    {
        const string send = """
            DECLARE @dialog uniqueidentifier;
            BEGIN DIALOG CONVERSATION @dialog FROM SERVICE MarkerService TO SERVICE N'MarkerService'
                ON CONTRACT [DEFAULT] WITH ENCRYPTION = OFF;
            SEND ON CONVERSATION @dialog MESSAGE TYPE [DEFAULT] (N'marker');
            """;
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "broker")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "001_queue.sql"), $$"""
            CREATE TABLE dbo.Markers (body nvarchar(32));
            CREATE QUEUE dbo.MarkerQueue;
            CREATE SERVICE MarkerService ON QUEUE dbo.MarkerQueue ([DEFAULT]);
            GO
            CREATE PROCEDURE dbo.RecordMarker AS
            BEGIN
                DECLARE @body varbinary(max);
                BEGIN TRANSACTION;
                RECEIVE TOP (1) @body = message_body FROM dbo.MarkerQueue;
                IF @@ROWCOUNT = 1 INSERT dbo.Markers VALUES (CONVERT(nvarchar(32), @body));
                COMMIT;
            END;
            GO
            ALTER QUEUE dbo.MarkerQueue WITH ACTIVATION
                (STATUS = ON, PROCEDURE_NAME = dbo.RecordMarker, MAX_QUEUE_READERS = 1, EXECUTE AS SELF);
            {{send}}
            DECLARE @deadline datetime2 = DATEADD(second, 30, SYSUTCDATETIME());
            WHILE NOT EXISTS (SELECT 1 FROM dbo.Markers WHERE body = N'marker')
            BEGIN
                IF SYSUTCDATETIME() >= @deadline THROW 50001, 'Activation did not record the control message', 1;
                WAITFOR DELAY '00:00:00.200';
            END;
            """);
        var built = World.RunCli("image", "build", "broker", "--from-scripts", folder, "--format", "json");
        Assert.True(built.Exit == 0, built.Out + built.Err);
        var listed = World.RunCli("image", "list", "--format", "json");
        Assert.Equal(0, listed.Exit);
        var image = BranchKit.Parse(listed.Out).GetProperty("images").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "broker");
        Assert.Equal("disabled", image.GetProperty("serviceBroker").GetString());

        var kit = new BranchKit(fixture, output);
        var branch = await kit.CreateAsync("broker-check", "broker");
        Assert.Equal(World.IsPlain ? "copy" : "thin", branch.GetProperty("cloneMode").GetString());
        try
        {
            await using var db = await kit.ConnectAsync("broker-check");
            Assert.Equal(0, await BranchKit.CountAsync(db, "SELECT is_broker_enabled FROM sys.databases WHERE database_id=DB_ID()"));
            const string markers = "SELECT COUNT(*) FROM dbo.Markers WHERE body=N'marker'";
            const string pending = "SELECT COUNT(*) FROM sys.transmission_queue WHERE to_service_name=N'MarkerService' AND CONVERT(nvarchar(32), message_body)=N'marker'";
            Assert.Equal(1, await BranchKit.CountAsync(db, markers));
            Assert.Equal(0, await BranchKit.CountAsync(db, pending));
            await BranchKit.ExecuteAsync(db, send);
            Assert.Equal(1, await BranchKit.CountAsync(db, pending));
            await BranchKit.ExecuteAsync(db, "WAITFOR DELAY '00:00:02'");
            Assert.Equal(1, await BranchKit.CountAsync(db, pending));
            Assert.Equal(1, await BranchKit.CountAsync(db, markers));
        }
        finally
        {
            await kit.DropBranchAsync("broker-check");
        }
        kit.AssertStatusIsClean();
    }

    [IntegrationFact]
    public async Task Background_code_running_at_the_freeze_cannot_get_a_trigger_to_fire_as_sa()
    {
        // An activation procedure runs as the database owner (the build login) and, for 40 seconds, keeps creating
        // and dropping trigger t_late for ALTER_AUTHORIZATION_DATABASE. It is certainly running when the build
        // freezes the database. If t_late ever fired, it would be on the change of owner to sa, and its only effect
        // is a line with a unique token in the engine's error log (RAISERROR WITH LOG needs sa).
        var token = "sqlbranch-t-late-" + Guid.NewGuid().ToString("N")[..12];
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "late-trigger")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "001_queue.sql"), $$"""
            CREATE TABLE dbo.Markers (body nvarchar(64));
            CREATE QUEUE dbo.PlantQueue;
            CREATE SERVICE PlantService ON QUEUE dbo.PlantQueue ([DEFAULT]);
            GO
            CREATE PROCEDURE dbo.Plant AS
            BEGIN
                DECLARE @body varbinary(max);
                RECEIVE TOP (1) @body = message_body FROM dbo.PlantQueue;
                IF @@ROWCOUNT = 0 RETURN;
                INSERT dbo.Markers VALUES (N'started');
                DECLARE @until datetime2 = DATEADD(second, 40, SYSUTCDATETIME());
                WHILE SYSUTCDATETIME() < @until
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE name = N't_late' AND parent_class = 0)
                        EXEC (N'CREATE TRIGGER t_late ON DATABASE FOR ALTER_AUTHORIZATION_DATABASE AS RAISERROR(N''{{token}}'', 10, 1) WITH LOG;');
                    WAITFOR DELAY '00:00:00.100';
                    IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = N't_late' AND parent_class = 0)
                        DROP TRIGGER t_late ON DATABASE;
                    WAITFOR DELAY '00:00:00.100';
                END;
            END;
            GO
            ALTER QUEUE dbo.PlantQueue WITH ACTIVATION
                (STATUS = ON, PROCEDURE_NAME = dbo.Plant, MAX_QUEUE_READERS = 1, EXECUTE AS OWNER);
            DECLARE @dialog uniqueidentifier;
            BEGIN DIALOG CONVERSATION @dialog FROM SERVICE PlantService TO SERVICE N'PlantService'
                ON CONTRACT [DEFAULT] WITH ENCRYPTION = OFF;
            SEND ON CONVERSATION @dialog MESSAGE TYPE [DEFAULT] (N'go');
            DECLARE @deadline datetime2 = DATEADD(second, 30, SYSUTCDATETIME());
            WHILE NOT EXISTS (SELECT 1 FROM dbo.Markers WHERE body = N'started')
            BEGIN
                IF SYSUTCDATETIME() >= @deadline THROW 50001, 'Activation did not start', 1;
                WAITFOR DELAY '00:00:00.200';
            END;
            """);

        var built = World.RunCli("image", "build", "latetrig", "--from-scripts", folder, "--format", "json");
        output.WriteLine(built.Out + built.Err);
        var outcome = built.Exit == 0 ? "exit 0 (the freeze stopped the background code)" : $"exit {built.Exit} {BranchKit.ErrorCode(built.Out)}";
        output.WriteLine($"Outcome: {outcome}");
        Assert.True(built.Exit == 0 || BranchKit.ErrorCode(built.Out) == "ddl_trigger", built.Out + built.Err);

        var kit = new BranchKit(fixture, output);
        await using (var master = await kit.ConnectAsync("master"))
        {
            var fired = await BranchKit.CountAsync(master, $"""
                CREATE TABLE #log (LogDate datetime, ProcessInfo nvarchar(64), Text nvarchar(max));
                INSERT #log EXEC master.dbo.xp_readerrorlog 0, 1, N'{token}';
                SELECT COUNT(*) FROM #log;
                """);
            Assert.Equal(0, fired);
            Assert.Equal(0, await BranchKit.CountAsync(master, "SELECT COUNT(*) FROM sys.server_principals WHERE name LIKE N'sqlbranch[_]build[_]%'"));
            Assert.Equal(0, await BranchKit.CountAsync(master, "SELECT COUNT(*) FROM sys.databases WHERE name LIKE N'sqlbranch[_]build[_]%'"));
        }

        if (built.Exit == 0)
        {
            await kit.CreateAsync("latetrig-check", "latetrig");
            try
            {
                await using var db = await kit.ConnectAsync("latetrig-check");
                Assert.Equal(0, await BranchKit.CountAsync(db, "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0"));
            }
            finally
            {
                await kit.DropBranchAsync("latetrig-check");
            }
            Assert.Equal(0, World.RunCli("image", "rm", "latetrig", "--format", "json").Exit);
        }
        kit.AssertStatusIsClean();
    }

    [IntegrationFact]
    public async Task Script_state_and_file_placement_are_checked_before_sealing()
    {
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "invalid-state")).FullName;
        var file = Path.Combine(folder, "change.sql");
        foreach (var (statement, code, detail) in new[]
        {
            ("ALTER DATABASE CURRENT SET READ_ONLY;", "script_failed", "READ_ONLY"),
            ("ALTER DATABASE CURRENT SET SINGLE_USER;", "script_failed", "SINGLE_USER"),
            ("ALTER DATABASE CURRENT SET OFFLINE;", "script_failed", "OFFLINE"),
            ("ALTER DATABASE CURRENT ADD FILEGROUP mem CONTAINS MEMORY_OPTIMIZED_DATA;", "unsupported_feature", "mem"),
            ("ALTER DATABASE CURRENT ADD FILE (NAME=extra, FILENAME='/var/opt/mssql/data/extra.ndf');", "unsupported_feature", "/var/opt/mssql/data/extra.ndf"),
            ("CREATE TRIGGER database_audit ON DATABASE FOR ALTER_DATABASE AS RETURN;", "script_failed", "change.sql, batch at line 1"),
            ("CREATE TRIGGER ownership_audit ON DATABASE FOR ALTER_AUTHORIZATION_DATABASE AS RETURN;", "ddl_trigger", "ownership_audit"),
            ("CREATE TRIGGER audit_created ON DATABASE FOR CREATE_TABLE AS RETURN;", "ddl_trigger", "audit_created"),
        })
        {
            await File.WriteAllTextAsync(file, statement);
            var run = World.RunCli("image", "build", "invalid-state", "--from-scripts", folder, "--format", "json");
            Assert.True(run.Exit == 1, run.Out);
            Assert.Equal(code, BranchKit.ErrorCode(run.Out));
            Assert.Contains(detail, BranchKit.ErrorMessage(run.Out), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, World.RunCli("status").Exit);
            if (detail.EndsWith(".ndf", StringComparison.Ordinal))
            {
                Assert.Equal(0, (await World.Runtime.ExecAsync(World.EngineName, ["test", "-f", detail], CancellationToken.None)).ExitCode);
            }
        }
    }

    [IntegrationFact]
    public async Task Build_login_cannot_access_an_existing_branch_or_create_databases()
    {
        var kit = new BranchKit(fixture, output);
        await kit.EnsureImageAsync("rights-base");
        await kit.CreateAsync("pr-142", "rights-base");
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "rights")).FullName;
        var file = Path.Combine(folder, "rights.sql");
        foreach (var denied in new[] { "USE [pr-142];", "DROP DATABASE [pr-142];", "CREATE DATABASE other;" })
        {
            await File.WriteAllTextAsync(file, denied);
            var run = World.RunCli("image", "build", "rights", "--from-scripts", folder, "--format", "json");
            Assert.True(run.Exit == 1, run.Out);
            Assert.Equal("script_failed", BranchKit.ErrorCode(run.Out));
            await using var branch = await kit.ConnectAsync("pr-142");
            Assert.Equal(100, await BranchKit.CountAsync(branch, "SELECT COUNT(*) FROM dbo.Item"));
            Assert.Equal(0, await BranchKit.CountAsync(branch, "SELECT COUNT(*) FROM sys.databases WHERE name=N'other'"));
            kit.AssertStatusIsClean();
        }
        await kit.DropBranchAsync("pr-142");
    }

    [IntegrationFact]
    public async Task Guest_check_names_the_database_and_leaves_the_pool_untouched()
    {
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "guest")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "schema.sql"), "SELECT 1;");
        await fixture.SqlAsync("CREATE DATABASE [odd]]name]", "EXEC [odd]]name].sys.sp_executesql N'GRANT CONNECT TO guest;'");
        try
        {
            var blocked = World.RunCli("image", "build", "checkguest", "--from-scripts", folder, "--format", "json");
            Assert.Equal("guest_database", BranchKit.ErrorCode(blocked.Out));
            Assert.Contains("odd]name", BranchKit.ErrorMessage(blocked.Out), StringComparison.Ordinal);
            Assert.Equal(0, World.RunCli("status").Exit);
            var bak = await fixture.MakeBackupAsync("GuestCheck");
            var build = World.RunCli("image", "build", "guestbackup", "--from-bak", bak);
            Assert.True(build.Exit == 0, build.Err);
        }
        finally
        {
            await fixture.SqlAsync("DROP DATABASE [odd]]name]");
        }
    }

    [IntegrationFact]
    public async Task Guest_branch_blocks_scripts_but_single_user_and_restoring_databases_are_skipped()
    {
        var kit = new BranchKit(fixture, output);
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "guest-states")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "seed.sql"), "SELECT 1;");
        await fixture.SqlAsync("CREATE DATABASE GuestSource", "EXEC GuestSource.sys.sp_executesql N'GRANT CONNECT TO guest'",
            "BACKUP DATABASE GuestSource TO DISK=N'/tmp/guest-states.bak' WITH INIT");
        try
        {
            var bak = Path.Combine(fixture.ClientFolder, "guest-states.bak");
            var copy = await EngineWorld.DockerAsync("cp", $"{World.EngineName}:/tmp/guest-states.bak", bak);
            Assert.True(copy.ExitCode == 0, copy.StdErr);
            var image = World.RunCli("image", "build", "guests", "--from-bak", bak, "--format", "json");
            Assert.True(image.Exit == 0, image.Out + image.Err);
            await fixture.SqlAsync("DROP DATABASE GuestSource",
                "RESTORE DATABASE GuestSource FROM DISK=N'/tmp/guest-states.bak' WITH NORECOVERY");
            await kit.CreateAsync("guest-branch", "guests");
            var blocked = World.RunCli("image", "build", "skip-states", "--from-scripts", folder, "--format", "json");
            Assert.Equal("guest_database", BranchKit.ErrorCode(blocked.Out));
            Assert.Contains("guest-branch", BranchKit.ErrorMessage(blocked.Out), StringComparison.Ordinal);
            await fixture.SqlAsync("ALTER DATABASE [guest-branch] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            await using (var holder = await kit.ConnectAsync("guest-branch"))
            {
                var allowed = World.RunCli("image", "build", "skip-states", "--from-scripts", folder);
                Assert.True(allowed.Exit == 0, allowed.Err);
            }
            await fixture.SqlAsync("RESTORE DATABASE GuestSource WITH RECOVERY");
            var restoredBroker = Convert.ToBoolean(await fixture.ScalarAsync("SELECT is_broker_enabled FROM sys.databases WHERE name=N'GuestSource'"), System.Globalization.CultureInfo.InvariantCulture);
            output.WriteLine("Restored backup broker enabled: " + restoredBroker);
            Assert.Equal(restoredBroker ? "enabled" : "disabled", BranchKit.Parse(image.Out).GetProperty("image").GetProperty("serviceBroker").GetString());
        }
        finally
        {
            await kit.DropBranchAsync("guest-branch");
            await fixture.SqlAsync("DROP DATABASE IF EXISTS GuestSource");
            await World.Runtime.ExecAsync(World.EngineName, ["rm", "-f", "/tmp/guest-states.bak"], CancellationToken.None);
        }
    }

    [IntegrationFact]
    public async Task Killed_script_build_login_is_reported_and_removed_by_next_build()
    {
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "interrupted")).FullName;
        var file = Path.Combine(folder, "seed.sql");
        await File.WriteAllTextAsync(file, "WAITFOR DELAY '00:05:00';");
        using var build = World.StartCli("image", "build", "interrupted", "--from-scripts", folder);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE original_login_name LIKE 'sqlbranch[_]build[_]%'"), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            Assert.False(build.HasExited);
            Assert.True(wait.Elapsed < TimeSpan.FromMinutes(2));
            await Task.Delay(200);
        }
        build.Kill();
        wait.Restart();
        while (true)
        {
            var status = World.RunCli("status", "--format", "json");
            if (status.Exit == 1 && BranchKit.Parse(status.Out).GetProperty("mismatches").EnumerateArray().Any(m => m.GetProperty("kind").GetString() == "build_login"))
            {
                break;
            }
            Assert.True(wait.Elapsed < TimeSpan.FromMinutes(2), status.Out);
            await Task.Delay(200);
        }
        await File.WriteAllTextAsync(file, "SELECT 1;");
        var next = World.RunCli("image", "build", "afterkill", "--from-scripts", folder);
        Assert.True(next.Exit == 0, next.Err);
        Assert.Equal(0, World.RunCli("status").Exit);
    }

    [IntegrationFact]
    public async Task Script_time_limit_is_optional_and_cancels_the_entire_file()
    {
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "timer")).FullName;
        var script = Path.Combine(folder, "002_seed.sql");
        await File.WriteAllTextAsync(script, "WAITFOR DELAY '00:05:00';");
        var longRun = World.RunCli("image", "build", "longrun", "--from-scripts", folder);
        Assert.True(longRun.Exit == 0, longRun.Err);
        await File.WriteAllTextAsync(script, "WAITFOR DELAY '00:02:00';");
        var timed = World.RunCli("image", "build", "timed", "--from-scripts", folder, "--script-timeout", "1m", "--format", "json");
        Assert.Equal(1, timed.Exit);
        Assert.Equal("script_failed", BranchKit.ErrorCode(timed.Out));
        Assert.Contains("002_seed.sql", BranchKit.ErrorMessage(timed.Out), StringComparison.Ordinal);
        Assert.Equal(0, World.RunCli("status").Exit);
    }

    [IntegrationFact]
    public async Task Script_recovery_and_log_growth_are_reset_before_sealing()
    {
        var folder = Directory.CreateDirectory(Path.Combine(fixture.ClientFolder, "recovery")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "model.sql"), "ALTER DATABASE CURRENT SET RECOVERY FULL; ALTER DATABASE CURRENT MODIFY FILE (NAME=N'log', SIZE=64MB);");
        var run = World.RunCli("image", "build", "logsize", "--from-scripts", folder, "--format", "json");
        Assert.True(run.Exit == 0, run.Out + run.Err);
        var image = BranchKit.Parse(run.Out).GetProperty("image");
        Assert.Equal("FULL", image.GetProperty("originalRecoveryModel").GetString());
        Assert.Equal("SIMPLE", image.GetProperty("sealedRecoveryModel").GetString());
        Assert.True(image.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("type").GetString()=="log").GetProperty("sizeBytes").GetInt64() < 8L*1024*1024);
    }

    [IntegrationFact]
    public async Task Ordered_scripts_create_a_sealed_masked_image_and_failures_clean_up()
    {
        var folder = Path.Combine(fixture.ClientFolder, "scripts");
        Directory.CreateDirectory(Path.Combine(folder, "old"));
        await File.WriteAllTextAsync(Path.Combine(folder, "010_more.sql"), "INSERT sales.Item VALUES (2); CREATE QUEUE sales.Events;\nGO\nCREATE PROCEDURE sales.ReadItems WITH EXECUTE AS OWNER AS SELECT COUNT(*) FROM sales.Item;");
        await File.WriteAllTextAsync(Path.Combine(folder, "001_schema.sql"), "CREATE SCHEMA sales;\nGO\nCREATE TABLE sales.Item(Id int);");
        await File.WriteAllTextAsync(Path.Combine(folder, "002_seed.sql"), "INSERT sales.Item VALUES (1);");
        await File.WriteAllTextAsync(Path.Combine(folder, "old", "999_drop.sql"), "DROP TABLE sales.Item;");
        await File.WriteAllTextAsync(Path.Combine(folder, "readme.md"), "ignore");
        var built = World.RunCli("image", "build", "shopdev", "--from-scripts", folder, "--format", "json");
        Assert.True(built.Exit == 0, built.Out + built.Err);
        var image = BranchKit.Parse(built.Out).GetProperty("image");
        Assert.Equal("shopdev", image.GetProperty("sourceDatabase").GetString());
        Assert.Equal("scripts", image.GetProperty("sourceType").GetString());
        Assert.Null(image.GetProperty("backupFile").GetString());
        Assert.Null(image.GetProperty("backupSha256").GetString());
        Assert.Equal(["001_schema.sql","002_seed.sql","010_more.sql"], image.GetProperty("scripts").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        var disk = await World.Runtime.ExecAsync(World.EngineName, ["cat", "/pool/images/shopdev/manifest.json"], CancellationToken.None);
        Assert.Equal(0, disk.ExitCode);
        var manifest = ImageManifest.FromJson(disk.StdOut);
        Assert.Equal("disabled", manifest.ServiceBroker);
        Assert.Empty(manifest.PostScripts);
        Assert.Empty(manifest.DdlTriggers);
        foreach (var script in manifest.Scripts)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(folder, script.Name));
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), script.Sha256);
        }
        var kit = new BranchKit(fixture, output);
        await kit.CreateAsync("seeded", "shopdev");
        await using (var conn = await kit.ConnectAsync("seeded"))
        {
            Assert.Equal(2, await BranchKit.CountAsync(conn, "EXEC sales.ReadItems"));
            Assert.Equal(1, await BranchKit.CountAsync(conn, "SELECT COUNT(*) FROM sys.schemas WHERE name=N'sales' AND principal_id=DATABASE_PRINCIPAL_ID('dbo')"));
            Assert.Equal(1, await BranchKit.CountAsync(conn, "SELECT COUNT(*) FROM sys.databases WHERE database_id=DB_ID() AND SUSER_SNAME(owner_sid)='sa' AND is_broker_enabled=0"));
        }

        var mask = Path.Combine(fixture.ClientFolder, "mask.sql");
        await File.WriteAllTextAsync(mask, "UPDATE dbo.Item SET Name=CONCAT('user', Id, '@example.test');");
        var bak = await fixture.MakeBackupAsync("Customer", rows: 1000);
        var masked = World.RunCli("image", "build", "masked", "--from-bak", bak, "--post-script", mask, "--format", "json");
        Assert.True(masked.Exit == 0, masked.Out + masked.Err);
        await kit.CreateAsync("customers", "masked");
        await using (var conn = await kit.ConnectAsync("customers"))
        {
            Assert.Equal(1000, await BranchKit.CountAsync(conn, "SELECT COUNT(*) FROM dbo.Item WHERE Name=CONCAT('user', Id, '@example.test')"));
        }

        await File.WriteAllTextAsync(mask, "THROW 50000, 'deliberate failure', 1;");
        var failed = World.RunCli("image", "build", "failed", "--from-bak", bak, "--post-script", mask, "--format", "json");
        Assert.Equal(1, failed.Exit);
        Assert.Equal("script_failed", BranchKit.ErrorCode(failed.Out));
        Assert.Contains("mask.sql", BranchKit.ErrorMessage(failed.Out), StringComparison.Ordinal);
        kit.AssertStatusIsClean();
        Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.server_principals WHERE name LIKE 'sqlbranch[_]build[_]%'"), System.Globalization.CultureInfo.InvariantCulture));
    }
}
