using SqlBranch.Core;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;

namespace SqlBranch.IntegrationTests;

public sealed partial class ImageBuildIntegrationTests
{
    [IntegrationFact]
    public async Task Backup_ownership_trigger_guard_checks_effective_events_and_backup_only_build_keeps_triggers()
    {
        const string db = "TriggerSource";
        var token = CancellationToken.None;
        await fixture.SqlAsync("CREATE DATABASE [TriggerSource]");
        try
        {
            // Harmless registrations, including a group with the ownership event as a descendant.
            foreach (var (name, events) in new[] { ("direct_audit", "ALTER_AUTHORIZATION_DATABASE"),
                ("group_audit", "DDL_DATABASE_LEVEL_EVENTS"), ("table_audit", "CREATE_TABLE") })
            {
                await fixture.SqlAsync($"EXEC [TriggerSource].sys.sp_executesql N'CREATE TRIGGER [{name}] ON DATABASE FOR {events} AS RETURN;'");
            }

            await using var session = await new SqlClientEngineConnector().ConnectAsync(World.Endpoint, World.Password, token);
            var triggers = await DatabaseTriggerCatalog.ReadAsync(session, db, token);
            var error = Assert.Throws<SqlBranchException>(() => DatabaseTriggerCatalog.RequirePostScriptsAllowed(triggers));
            Assert.Equal(ErrorCodes.DdlTrigger, error.Code);
            Assert.Contains("direct_audit", error.Message, StringComparison.Ordinal);
            Assert.Contains("group_audit", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("table_audit", error.Message, StringComparison.Ordinal);

            await fixture.SqlAsync("BACKUP DATABASE [TriggerSource] TO DISK = N'/tmp/trigger-source.bak' WITH INIT");
            var path = Path.Combine(fixture.ClientFolder, "trigger-source.bak");
            var copy = await EngineWorld.DockerAsync("cp", $"{World.EngineName}:/tmp/trigger-source.bak", path);
            Assert.True(copy.ExitCode == 0, copy.StdErr);
            var post = Path.Combine(fixture.ClientFolder, "mask.sql");
            await File.WriteAllTextAsync(post, "THROW 50000, 'post-script ran unexpectedly', 1;");
            var rejected = World.RunCli("image", "build", "audit-source", "--from-bak", path, "--post-script", post, "--format", "json");
            Assert.Equal(1, rejected.Exit);
            Assert.Equal("ddl_trigger", BranchKit.ErrorCode(rejected.Out));
            Assert.Contains("group_audit", BranchKit.ErrorMessage(rejected.Out), StringComparison.Ordinal);
            Assert.Equal(0, World.RunCli("status").Exit);
            var built = World.RunCli("image", "build", "audit-source", "--from-bak", path, "--format", "json");
            Assert.True(built.Exit == 0, built.Out + built.Err);
            var listed = World.RunCli("image", "list", "--format", "json");
            Assert.Equal(0, listed.Exit);
            var image = BranchKit.Parse(listed.Out).GetProperty("images").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "audit-source");
            Assert.Equal(["direct_audit", "group_audit", "table_audit"], image.GetProperty("ddlTriggers").EnumerateArray().Select(t => t.GetString()));

            await fixture.SqlAsync("USE [TriggerSource]; DISABLE TRIGGER direct_audit ON DATABASE; DISABLE TRIGGER group_audit ON DATABASE;");
            DatabaseTriggerCatalog.RequirePostScriptsAllowed(await DatabaseTriggerCatalog.ReadAsync(session, db, token));
            await fixture.SqlAsync("BACKUP DATABASE [TriggerSource] TO DISK = N'/tmp/trigger-source.bak' WITH INIT");
            copy = await EngineWorld.DockerAsync("cp", $"{World.EngineName}:/tmp/trigger-source.bak", path);
            Assert.True(copy.ExitCode == 0, copy.StdErr);
            await File.WriteAllTextAsync(post, "SELECT 1;");
            var allowed = World.RunCli("image", "build", "audit-disabled", "--from-bak", path, "--post-script", post, "--format", "json");
            Assert.True(allowed.Exit == 0, allowed.Err + allowed.Out);
            await File.WriteAllTextAsync(post, "ALTER TRIGGER table_audit ON DATABASE FOR CREATE_TABLE AS PRINT N'new audit message';");
            var changed = World.RunCli("image", "build", "audit-changed", "--from-bak", path, "--post-script", post, "--format", "json");
            Assert.Equal("ddl_trigger", BranchKit.ErrorCode(changed.Out));
            Assert.Contains("table_audit", BranchKit.ErrorMessage(changed.Out), StringComparison.Ordinal);
            Assert.Equal(0, World.RunCli("status").Exit);
        }
        finally
        {
            await fixture.SqlAsync("DROP DATABASE [TriggerSource]");
            await World.Runtime.ExecAsync(World.EngineName, ["rm", "-f", "/tmp/trigger-source.bak"], token);
            World.RunCli("image", "rm", "audit-source");
            World.RunCli("image", "rm", "audit-disabled");
        }
    }
}
