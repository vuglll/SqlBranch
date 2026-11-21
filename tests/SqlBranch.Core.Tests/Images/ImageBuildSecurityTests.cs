using System.Text.RegularExpressions;
using SqlBranch.Core.Images;
using SqlBranch.Core.Tests.Configuration;

namespace SqlBranch.Core.Tests.Images;

public sealed partial class ImageBuildTests
{
    [Fact]
    public void Old_manifests_default_to_backup_with_empty_script_lists()
    {
        var old = ImageManifest.FromJson("""{"format":1,"name":"shop","backupFile":"shop.bak","files":[]}""");
        Assert.Equal("backup", old.SourceType);
        Assert.Empty(old.Scripts);
        Assert.Empty(old.PostScripts);
        Assert.Empty(old.DdlTriggers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Guest_hit_or_unreadable_catalog_fails_before_changing_anything(bool readable)
    {
        using var files = new TempTree();
        var post = files.File("mask.sql", "SELECT 1");
        _world.Connector.QueryHandler = sql => sql.Contains("database_id > 4", StringComparison.Ordinal) ? [["odd]name"]] : [];
        _world.Connector.ScalarHandler = sql => sql.Contains("[odd]]name].sys.database_permissions", StringComparison.Ordinal)
            ? readable ? 1 : throw new InvalidOperationException("access failed")
            : null;
        var run = Build("--post-script", post);
        Assert.Equal(1, run.Exit);
        Assert.Equal("guest_database", ErrorCode(run.Json));
        Assert.Contains(readable ? "grants CONNECT" : "could not be read", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.Contains("odd]name", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.False(Ran("mkdir"));
        Assert.False(Ran("CREATE LOGIN"));
    }

    [Fact]
    public void Backup_ownership_handler_rejects_post_scripts_before_login_or_ownership_transfer()
    {
        using var files = new TempTree();
        var post = files.File("mask.sql", "SELECT 42");
        _world.Connector.QueryHandler = sql => sql.Contains("sys.trigger_events", StringComparison.Ordinal)
            ? [["ownership_audit", false, true, 17, DateTime.UnixEpoch]] : [];

        var run = Build("--post-script", post);
        Assert.Equal(1, run.Exit);
        Assert.Equal("ddl_trigger", ErrorCode(run.Json));
        Assert.Contains("ownership_audit", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain(_world.Connector.Statements, statement =>
            statement.StartsWith("CREATE LOGIN", StringComparison.Ordinal) ||
            statement.StartsWith("ALTER AUTHORIZATION", StringComparison.Ordinal) ||
            statement.Contains("SELECT 42", StringComparison.Ordinal));
    }

    [Fact]
    public void Backup_post_script_stages_freeze_before_validation_and_drop_login_after_detach()
    {
        using var files = new TempTree();
        var post = files.File("mask.sql", "SELECT 42;");
        _world.Connector.QueryHandler = sql => sql.Contains("user_access_desc FROM", StringComparison.Ordinal) ? [["ONLINE", false, "MULTI_USER"]] : [];
        var run = Build("--post-script", post);
        Assert.Equal(0, run.Exit);
        var sql = _world.Connector.Statements;
        var stages = new[] { "CREATE LOGIN", "ALTER AUTHORIZATION ON DATABASE", "SELECT 42", "ALTER LOGIN",
            "SET DISABLE_BROKER", "sys.filegroups", "SET RECOVERY SIMPLE", "CHECKPOINT", "sp_detach_db", "DROP LOGIN",
            "FOR ATTACH_REBUILD_LOG", "ALTER AUTHORIZATION ON DATABASE" };
        var previous = -1;
        foreach (var stage in stages)
        {
            var next = sql.FindIndex(previous + 1, statement => statement.Contains(stage, StringComparison.Ordinal));
            Assert.True(next > previous, stage);
            previous = next;
        }
        var checkpoint = sql.FindIndex(s => s.Contains("CHECKPOINT", StringComparison.Ordinal));
        Assert.Contains("SET RECOVERY SIMPLE", sql[checkpoint - 1], StringComparison.Ordinal);
        Assert.Contains("sp_detach_db", sql[checkpoint + 1], StringComparison.Ordinal);
        Assert.Contains("TO [sa]", sql[previous], StringComparison.Ordinal);
        Assert.Equal(2, sql.Count(statement => statement.StartsWith("ALTER AUTHORIZATION", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_trigger_that_appears_after_the_freeze_is_caught_before_the_owner_changes_to_sa()
    {
        using var files = new TempTree();
        var post = files.File("mask.sql", "SELECT 42;");
        _world.Connector.QueryHandler = sql =>
        {
            if (sql.Contains("user_access_desc FROM", StringComparison.Ordinal))
            {
                return [["ONLINE", false, "MULTI_USER"]];
            }

            // Background code plants t_late after the freeze compared the triggers; only the re-attached database has it.
            var attached = _world.Connector.Statements.Exists(s => s.Contains("FOR ATTACH_REBUILD_LOG", StringComparison.Ordinal));
            return sql.Contains("sys.trigger_events", StringComparison.Ordinal) && attached
                ? [["t_late", false, true, 99, DateTime.UnixEpoch]] : [];
        };

        var run = Build("--post-script", post);
        Assert.Equal(1, run.Exit);
        Assert.Equal("ddl_trigger", ErrorCode(run.Json));
        Assert.Contains("t_late", ErrorMessage(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain(_world.Connector.Statements, statement =>
            statement.StartsWith("ALTER AUTHORIZATION", StringComparison.Ordinal) && statement.Contains("TO [sa]", StringComparison.Ordinal));
        var sql = _world.Connector.Statements;
        var attach = sql.FindIndex(statement => statement.Contains("FOR ATTACH_REBUILD_LOG", StringComparison.Ordinal));
        var dropLogin = sql.FindIndex(statement => statement.StartsWith("DROP LOGIN", StringComparison.Ordinal));
        Assert.InRange(dropLogin, 0, attach - 1);
        Assert.True(sql.FindIndex(attach + 1, statement => statement.Contains("DROP DATABASE", StringComparison.Ordinal)) > attach,
            "the re-attached database is dropped by the cleanup");
    }

    [Fact]
    public void Script_failure_redacts_the_generated_password()
    {
        using var files = new TempTree();
        var post = files.File("mask.sql", "SELECT 42;");
        string? password = null;
        _world.Connector.ExecuteHandler = sql =>
        {
            if (sql.StartsWith("CREATE LOGIN", StringComparison.Ordinal))
            {
                password = Regex.Match(sql, "PASSWORD=N'([^']+)'").Groups[1].Value;
            }
        };
        _world.Connector.ParameterizedExecuteHandler = (sql, _) =>
        {
            if (sql.Contains("SELECT 42", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"SQL error including {password}");
            }
        };
        var run = Build("--post-script", post);
        Assert.Equal("script_failed", ErrorCode(run.Json));
        Assert.False(string.IsNullOrEmpty(password));
        Assert.DoesNotContain(password, run.Json.ToString() + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Leftover_login_scan_skips_held_locks_and_unrelated_names_and_warns_on_failed_cleanup()
    {
        _world.Connector.QueryHandler = sql => sql.Contains("sys.sql_logins", StringComparison.Ordinal)
            ? [["app_user"], ["sqlbranch_build_abcd"], ["sqlbranch_build_ffff"]] : [];
        _world.Connector.HeldLocks.Add("sqlbranch:build:abcd");
        _world.Connector.ScalarHandler = sql => sql.Contains("owner_sid", StringComparison.Ordinal) ? 0 : null;
        _world.Connector.ExecuteHandler = sql =>
        {
            if (sql.Contains("DROP LOGIN [sqlbranch_build_ffff]", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("drop refused");
            }
        };
        _masterFiles = (folder, db, _) => db.EndsWith("ffff", StringComparison.Ordinal) ? [] :
            [MasterFile("Shop", folder + "/Shop.mdf", "ROWS", 4096), MasterFile("Shop_log", folder + "/Shop_log.ldf", "LOG", 4096)];
        var run = Build();
        Assert.Equal(0, run.Exit);
        Assert.Contains("drop refused", run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(_world.Connector.Statements, sql => sql.Contains("[sqlbranch_build_abcd]", StringComparison.Ordinal) || sql.Contains("[app_user]", StringComparison.Ordinal));
        var status = _world.Run("status", "--format", "json");
        Assert.Equal(1, status.Exit);
        Assert.Contains("sqlbranch_build_ffff", status.Out, StringComparison.Ordinal);
    }
}
