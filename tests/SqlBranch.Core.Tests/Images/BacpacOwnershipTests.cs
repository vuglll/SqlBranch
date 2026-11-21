using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

public sealed class BacpacOwnershipTests
{
    [UnixFact]
    public async Task Require_stops_the_shell_when_an_early_check_fails()
    {
        var script = BacpacOwnership.Require("[ 1 = 2 ] && true") + "; echo REACHED";
        var result = await new SystemProcessRunner().RunAsync("/bin/sh", ["-c", script], null, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task AnyOf_accepts_the_first_alternative_without_running_the_second()
    {
        var script = BacpacOwnership.AnyOf("true && true", "[ -e /nonexistent-sqlbranch ] && stat /nonexistent-sqlbranch") + " && echo OK";
        var result = await new SystemProcessRunner().RunAsync("/bin/sh", ["-c", script], null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("OK", result.StdOut.Trim());
        Assert.Empty(result.StdErr);
    }

    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rollback_preserves_replaced_staging_and_unexpected_contents(bool replace)
    {
        using var tree = new Configuration.TempTree();
        var runner = new SystemProcessRunner();
        var token = CancellationToken.None;
        var canonical = await runner.RunAsync("/bin/sh", ["-c", "cd -P -- \"$1\" && pwd -P", "sh", tree.Root], null, token);
        Assert.Equal(0, canonical.ExitCode);
        var root = canonical.StdOut.Trim();
        var op = new BacpacOperation("l" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(root, op.Id));
        var image = Directory.CreateDirectory(Path.Combine(directory.FullName, "image"));
        op.DirectoryIdentity = BacpacScratchIdentity.Read(directory.FullName);
        op.ImageIdentity = BacpacScratchIdentity.Read(image.FullName);

        if (replace)
        {
            Directory.Move(image.FullName, image.FullName + "-original");
            Directory.CreateDirectory(image.FullName);
        }
        var sentinel = Path.Combine(image.FullName, "sentinel");
        await File.WriteAllTextAsync(sentinel, "keep");
        var shell = "realpath() { (cd -P -- \"$2\" && pwd -P); }; ";
        if (OperatingSystem.IsMacOS())
        {
            shell += "stat() { case \"$2\" in %h) /usr/bin/stat -f %l \"$3\";; *) /usr/bin/stat -f \"$2\" \"$3\";; esac; }; ";
        }
        shell += BacpacOwnership.RemoveStagedImageScript(op).Replace(ImagePaths.BuildRoot, root, StringComparison.Ordinal);

        var refused = await runner.RunAsync("/bin/sh", ["-c", shell], null, token);
        Assert.Equal(1, refused.ExitCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));

        File.Delete(sentinel);
        if (replace)
        {
            Directory.Delete(image.FullName);
            Directory.Move(image.FullName + "-original", image.FullName);
        }
        Directory.CreateDirectory(Path.Combine(image.FullName, "data"));
        await File.WriteAllTextAsync(Path.Combine(image.FullName, "data", "data.mdf"), "owned");
        var removed = await runner.RunAsync("/bin/sh", ["-c", shell], null, token);
        Assert.True(removed.ExitCode == 0, removed.StdErr);
        Assert.False(Directory.Exists(image.FullName));
    }

    [UnixTheory]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    [InlineData(false, "symlink")]
    [InlineData(true, "hard link")]
    [InlineData(false, "rmdir failure")]
    public async Task Cleanup_and_sweep_remove_only_a_regular_single_link_temporary_record(bool sweep, string kind)
    {
        using var tree = new Configuration.TempTree();
        var token = CancellationToken.None;
        var runner = new SystemProcessRunner();
        var canonical = await runner.RunAsync("/bin/sh", ["-c", "cd -P -- \"$1\" && pwd -P", "sh", tree.Root], null, token);
        Assert.Equal(0, canonical.ExitCode);
        var root = canonical.StdOut.Trim();
        var op = new BacpacOperation("l" + Guid.NewGuid().ToString("N")) { SealState = BacpacSealState.HandedOff };
        var directory = Directory.CreateDirectory(Path.Combine(root, op.Id)).FullName;
        op.DirectoryIdentity = BacpacScratchIdentity.Read(directory);
        var world = new CliWorld();
        var engine = new ContainerInfo(ContainerLabels.EngineName, CliWorld.EngineImage, true,
            new Dictionary<string, string> { [ContainerLabels.Managed] = "true" }, [])
        { Id = "engine-id", Hostname = "engine-host" };
        world.Runtime.Containers[engine.Id] = engine;
        world.Connector.SaPassword = CliWorld.StoredPassword;
        world.Connector.ScalarHandler = sql => sql.Contains("MachineName", StringComparison.Ordinal) ? engine.Hostname : 0;
        var record = Path.Combine(directory, "bacpac.owner");
        var originalRecord = $"{engine.Id}|{op.Id}|0|-|{op.DirectoryIdentity}|handed-off";
        await File.WriteAllTextAsync(record, originalRecord);
        var temporary = record + ".new";
        var sentinel = tree.File("sentinel", "keep");
        if (kind == "symlink")
        {
            File.CreateSymbolicLink(temporary, sentinel);
        }
        else if (kind == "hard link")
        {
            var link = await runner.RunAsync("/bin/ln", [sentinel, temporary], null, token);
            Assert.Equal(0, link.ExitCode);
        }
        else
        {
            await File.WriteAllTextAsync(temporary, "interrupted write");
        }

        var shell = "realpath() { (cd -P -- \"$2\" && pwd -P); }; ";
        if (OperatingSystem.IsMacOS())
        {
            shell += "stat() { case \"$2\" in %h) /usr/bin/stat -f %l \"$3\";; *) /usr/bin/stat -f \"$2\" \"$3\";; esac; }; ";
        }
        var failRmdir = kind == "rmdir failure";
        world.Runtime.ExecHandler = (_, args) => args[2].StartsWith("mountpoint", StringComparison.Ordinal)
            ? FakeContainerRuntime.Ok()
            : RunShell(runner, shell + (failRmdir ? "rmdir() { return 1; }; " : "") +
                args[2].Replace(ImagePaths.BuildRoot, root, StringComparison.Ordinal));
        await using var session = await world.Connector.ConnectAsync(EngineEndpoint.Loopback(14330), CliWorld.StoredPassword, token);
        var owner = new BacpacOwnership(world.Runtime, engine, session);
        var keep = kind != "file";
        if (sweep)
        {
            await owner.SweepAsync(token);
        }
        else
        {
            Assert.Equal(!keep, await owner.TryCleanupAsync(op));
        }

        Assert.Equal(keep, Directory.Exists(directory));
        Assert.Equal(keep, File.Exists(record));
        Assert.Equal(kind is "symlink" or "hard link", File.Exists(temporary));
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        if (failRmdir)
        {
            Assert.Equal(originalRecord, await File.ReadAllTextAsync(record));
            failRmdir = false;
            await owner.SweepAsync(token);
            Assert.False(Directory.Exists(directory));
        }
    }

    private static ContainerResult RunShell(SystemProcessRunner runner, string script)
    {
        var result = runner.RunAsync("/bin/sh", ["-c", script], null, CancellationToken.None).GetAwaiter().GetResult();
        return new ContainerResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    public async Task Cleanup_recovers_failed_record_writes_and_wraps_mutation_guards(int failedWrite, bool loginExists)
    {
        const string identity = "5:91";
        var world = new CliWorld();
        var engine = new ContainerInfo(ContainerLabels.EngineName, CliWorld.EngineImage, true,
            new Dictionary<string, string> { [ContainerLabels.Managed] = "true" }, [])
        { Id = "engine-id", Hostname = "engine-host" };
        world.Runtime.Containers[engine.Id] = engine;
        world.Connector.SaPassword = CliWorld.StoredPassword;
        var token = CancellationToken.None;
        await using var sql = await world.Connector.ConnectAsync(EngineEndpoint.Loopback(14330), CliWorld.StoredPassword, token);
        var owner = new BacpacOwnership(world.Runtime, engine, sql);
        var op = new BacpacOperation("l" + Guid.NewGuid().ToString("N"));
        var guid = Guid.NewGuid();
        var folder = Quoting.Shell(op.Directory);
        var record = Quoting.Shell(op.Record);
        var root = op.Directory[..op.Directory.LastIndexOf('/')];
        var rootGuard = $"[ \"$(realpath -e {root})\" = {root} ]";
        var ownedGuard = $"{rootGuard} && [ \"$(realpath -e {folder})\" = {folder} ] && [ -d {folder} ] && " +
            $"[ \"$(stat -c '%d:%i' {folder})\" = '{identity}' ]";
        var fileGuard = $"[ -f {record} ] && [ ! -L {record} ] && [ \"$(stat -c %h {record})\" = 1 ]";
        var emptyGuard = $"unexpected=$(find {folder} -mindepth 1 -maxdepth 1 ! -name bacpac.owner -print -quit) && [ \"$unexpected\" = '' ]";
        var scripts = new List<string>();
        var writes = 0;
        string? persisted = null;
        string CurrentRecord() => $"{engine.Id}|{op.Id}|{(op.LoginAttempted ? 1 : 0)}|{op.DatabaseGuid?.ToString("D") ?? "-"}|{identity}";
        world.Runtime.ExecHandler = (_, args) =>
        {
            var script = args[2];
            scripts.Add(script);
            if (script.Contains("mv -T --", StringComparison.Ordinal))
            {
                if (++writes == failedWrite)
                {
                    return FakeContainerRuntime.Fail();
                }
                const string writePrefix = "printf %s '";
                var start = script.IndexOf(writePrefix, StringComparison.Ordinal) + writePrefix.Length;
                persisted = script[start..script.IndexOf('\'', start)];
                return FakeContainerRuntime.Ok();
            }
            if (script.Contains("head -c 512", StringComparison.Ordinal) && !script.Contains("saved=$(", StringComparison.Ordinal))
            {
                return persisted is null ? FakeContainerRuntime.Fail() : FakeContainerRuntime.Ok(persisted);
            }
            if (script.EndsWith($"; rmdir -- {folder}; printf removed", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok(persisted is null ? "removed" : "");
            }
            return FakeContainerRuntime.Ok(script.EndsWith($"stat -c '%d:%i' {folder}", StringComparison.Ordinal) ? identity : "");
        };
        world.Connector.ScalarHandler = statement => statement switch
        {
            var s when s.Contains("MachineName", StringComparison.Ordinal) => engine.Hostname,
            var s when s.Contains("AND SUSER_ID", StringComparison.Ordinal) => 0,
            var s when s.Contains("database_guid", StringComparison.Ordinal) => guid,
            var s when s.Contains("sys.sql_logins", StringComparison.Ordinal) => loginExists ? 1 : 0,
            _ => 1,
        };
        world.Connector.QueryHandler = _ => [[op.Data], [op.Log]];
        world.Connector.ExecuteHandler = statement =>
        {
            if (statement.Contains("DROP DATABASE", StringComparison.Ordinal) || statement.Contains("DROP LOGIN", StringComparison.Ordinal))
            {
                Assert.Equal(CurrentRecord(), persisted);
            }
        };

        async Task BuildAsync()
        {
            await owner.CreateAsync(op, token);
            await owner.CreateLoginAsync(op, "test-build-password", token);
        }
        if (failedWrite == 0)
        {
            await BuildAsync();
        }
        else
        {
            await Assert.ThrowsAsync<SqlBranchException>(BuildAsync);
        }
        Assert.Equal(failedWrite == 0 ? 3 : failedWrite, writes);
        Assert.Equal(failedWrite switch
        {
            0 => CurrentRecord(),
            1 => null,
            2 => $"{engine.Id}|{op.Id}|0|-|{identity}",
            _ => $"{engine.Id}|{op.Id}|0|{guid:D}|{identity}",
        }, persisted);
        if (failedWrite == 2 && loginExists)
        {
            // Also exercise a matching login identified by its SID when both record fields lag.
            op.LoginAttempted = true;
        }
        Assert.True(await owner.TryCleanupAsync(op));

        const string poolGuard = "mountpoint -q /pool && [ \"$(realpath -e /pool)\" = /pool ]";
        Assert.Contains($"set -e; {{ {poolGuard}; }} || exit 1; mkdir -p {root}; {{ {rootGuard}; }} || exit 1; mkdir -m 700 {folder}", scripts);
        var writeScripts = scripts.Where(s => s.Contains("mv -T --", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(writeScripts);
        Assert.All(writeScripts, s => Assert.StartsWith($"set -e; {{ {ownedGuard}; }} || exit 1; umask 077;", s, StringComparison.Ordinal));
        var drops = world.Connector.Statements.Where(s => s.Contains("DROP DATABASE", StringComparison.Ordinal) ||
            s.Contains("DROP LOGIN", StringComparison.Ordinal)).ToArray();
        if (failedWrite == 1)
        {
            Assert.Contains(scripts, script => script.StartsWith($"set -e; {{ {ownedGuard}; }} || exit 1; " +
                $"if {BacpacOwnership.AnyOf($"[ -e {record} ]", $"[ -L {record} ]")}; then exit 0; fi; ", StringComparison.Ordinal) &&
                script.EndsWith($"; rmdir -- {folder}; printf removed", StringComparison.Ordinal));
            Assert.Empty(drops);
            Assert.DoesNotContain(world.Connector.Statements, s => s.StartsWith("CREATE", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(scripts, script => script.StartsWith($"set -e; {{ {ownedGuard}; }} || exit 1; {{ {fileGuard}; }} || exit 1; " +
                $"{{ {emptyGuard}; }} || exit 1; ", StringComparison.Ordinal) &&
                script.Contains($"rm -- {record}; if rmdir -- {folder};", StringComparison.Ordinal));
            Assert.Equal(loginExists ? 2 : 1, drops.Length);
            Assert.EndsWith($"DROP DATABASE {Quoting.Identifier(op.Database)};", drops[0], StringComparison.Ordinal);
            if (loginExists)
            {
                Assert.Contains($"DROP LOGIN {Quoting.Identifier(op.Database)};", drops[1], StringComparison.Ordinal);
                var disable = Assert.Single(world.Connector.Statements, s => s.Contains("ALTER LOGIN", StringComparison.Ordinal));
                Assert.Contains("SELECT TOP (1) @id=session_id, @t=login_time", disable, StringComparison.Ordinal);
                Assert.Contains($"WHERE session_id=@id AND original_security_id={op.LoginSid} AND login_time=@t) BEGIN SET @sql=N'KILL '",
                    disable, StringComparison.Ordinal);
            }
            if (failedWrite != 0)
            {
                Assert.Equal(CurrentRecord(), persisted);
                Assert.Equal(failedWrite + 1, writes);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sweep_after_a_kill_uses_the_persisted_seal_handoff(bool loginExists)
    {
        var world = new CliWorld();
        var engine = new ContainerInfo(ContainerLabels.EngineName, CliWorld.EngineImage, true,
            new Dictionary<string, string> { [ContainerLabels.Managed] = "true" }, [])
        { Id = "engine-id", Hostname = "engine-host" };
        world.Runtime.Containers[engine.Id] = engine;
        world.Connector.SaPassword = CliWorld.StoredPassword;
        world.Connector.TrackSessionLocks = true;
        var op = new BacpacOperation("l" + Guid.NewGuid().ToString("N"))
        {
            SealState = BacpacSealState.Sealing, LoginAttempted = true, DatabaseGuid = Guid.NewGuid(), DirectoryIdentity = "5:91",
        };
        var record = $"{engine.Id}|{op.Id}|1|{op.DatabaseGuid:D}|{op.DirectoryIdentity}";
        string? persisted = record + "|seal";
        var scripts = new List<string>();
        world.Runtime.ExecHandler = (_, args) =>
        {
            var script = args[2];
            scripts.Add(script);
            if (script.Contains("basename", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok(persisted is null ? "" : op.Id + "\n");
            }
            if (script.Contains("head -c 512", StringComparison.Ordinal) && !script.Contains("saved=$(", StringComparison.Ordinal))
            {
                return persisted is null ? FakeContainerRuntime.Fail() : FakeContainerRuntime.Ok(persisted);
            }
            if (script.Contains("mv -T --", StringComparison.Ordinal))
            {
                const string prefix = "printf %s '";
                var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
                persisted = script[start..script.IndexOf('\'', start)];
            }
            if (script.EndsWith("stat -c '%d:%i' " + Quoting.Shell(op.StagedImage), StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("5:92");
            }
            if (script.Contains("rmdir -- " + Quoting.Shell(op.Directory), StringComparison.Ordinal))
            {
                persisted = null;
            }
            return FakeContainerRuntime.Ok();
        };
        world.Connector.ScalarHandler = statement => statement switch
        {
            var s when s.Contains("MachineName", StringComparison.Ordinal) => engine.Hostname,
            var s when s.Contains("sys.dm_exec_sessions", StringComparison.Ordinal) => 0,
            var s when s.Contains("sys.sql_logins", StringComparison.Ordinal) => loginExists ? 1 : 0,
            var s when s.Contains("DB_ID", StringComparison.Ordinal) && op.SealState == BacpacSealState.Sealing => 0,
            _ => throw new InvalidOperationException("Database lookup after ownership handoff"),
        };
        var token = CancellationToken.None;
        var endpoint = EngineEndpoint.Loopback(14330);
        await using var sweepSession = await world.Connector.ConnectAsync(endpoint, CliWorld.StoredPassword, token);
        var sweeper = new BacpacOwnership(world.Runtime, engine, sweepSession);
        await using (var buildSession = await world.Connector.ConnectAsync(endpoint, CliWorld.StoredPassword, token))
        {
            Assert.True(await EngineLocks.TryTakeAsync(buildSession, EngineLocks.Build(op.Id), token));
            await new BacpacOwnership(world.Runtime, engine, buildSession).StageImageAsync(op,
                [new SealFile(op.Data, "data.mdf"), new SealFile(op.RebuiltLog, Path.GetFileName(op.RebuiltLog))], token);
            Assert.Equal(record + "|handed-off|5:92", persisted);
            // A stray '$' before an interpolated guard turns `[ ... ]` into `$[ ... ]`, which the shell runs as a
            // command that always fails; the fake runtime would accept it, so check the script text itself.
            Assert.DoesNotContain(scripts, s => s.Contains("$[", StringComparison.Ordinal));
            Assert.Contains(scripts, s => s.StartsWith("[ \"$(realpath -e", StringComparison.Ordinal)
                && s.Contains("unexpected=$(find", StringComparison.Ordinal));
            await sweeper.SweepAsync(token);
            Assert.NotNull(persisted);
        }

        // The killed process has lost its session and its in-memory operation state.
        await sweeper.SweepAsync(token);

        Assert.Null(persisted);
        Assert.DoesNotContain(scripts, s => s.Contains("rm -f -- " + Quoting.Shell(op.Data), StringComparison.Ordinal));
        Assert.DoesNotContain(world.Connector.Statements, s => s.Contains("DROP DATABASE", StringComparison.Ordinal));
        Assert.Equal(loginExists ? 1 : 0, world.Connector.Statements.Count(s => s.Contains("DROP LOGIN", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("foreign login")]
    [InlineData("foreign database")]
    [InlineData("outside file")]
    [InlineData("replaced directory")]
    [InlineData("drop failure")]
    [InlineData("owns both")]
    public async Task Cleanup_requires_database_login_and_path_ownership(string caseName)
    {
        var world = new CliWorld();
        var engine = new ContainerInfo(ContainerLabels.EngineName, CliWorld.EngineImage, true,
            new Dictionary<string, string> { [ContainerLabels.Managed] = "true" }, [])
        { Id = "engine-id", Hostname = "engine-host" };
        world.Runtime.Containers[engine.Id] = engine;
        var op = new BacpacOperation("l" + Guid.NewGuid().ToString("N"))
        { LoginAttempted = true, DatabaseGuid = Guid.NewGuid(), DirectoryIdentity = "5:91" };
        world.Connector.SaPassword = CliWorld.StoredPassword;
        await using var sql = await world.Connector.ConnectAsync(Core.Engine.EngineEndpoint.Loopback(14330), CliWorld.StoredPassword, CancellationToken.None);
        world.Connector.Statements.Clear();
        world.Runtime.ExecHandler = (_, args) =>
        {
            var script = args[2];
            if (script.Contains("head -c 512", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok($"{engine.Id}|{op.Id}|1|{op.DatabaseGuid:D}|{op.DirectoryIdentity}");
            }
            return caseName == "replaced directory" && script.Contains("stat -c '%d:%i'", StringComparison.Ordinal)
                ? FakeContainerRuntime.Fail() : FakeContainerRuntime.Ok();
        };
        world.Connector.ScalarHandler = statement =>
        {
            if (statement.Contains("MachineName", StringComparison.Ordinal))
            {
                return engine.Hostname;
            }
            if (statement.Contains("sys.sql_logins", StringComparison.Ordinal))
            {
                return caseName == "foreign login" ? 2 : 1;
            }
            if (statement.Contains("database_guid", StringComparison.Ordinal))
            {
                return caseName == "foreign database" ? Guid.NewGuid() : op.DatabaseGuid;
            }
            return 1;
        };
        world.Connector.QueryHandler = _ => [[op.Data], [caseName == "outside file" ? "/pool/foreign.ldf" : op.Log]];
        if (caseName == "drop failure")
        {
            world.Connector.FailDrop.Add(op.Database);
        }

        var owner = new BacpacOwnership(world.Runtime, engine, sql);
        var cleaned = await owner.TryCleanupAsync(op);

        Assert.Equal(caseName == "owns both", cleaned);
        Assert.Equal(caseName is "owns both" or "drop failure", world.Connector.Statements.Any(s => s.Contains("DROP DATABASE", StringComparison.Ordinal)));
        Assert.Equal(caseName == "owns both", world.Connector.Statements.Any(s => s.Contains("DROP LOGIN", StringComparison.Ordinal)));
        if (caseName == "foreign login")
        {
            Assert.DoesNotContain(world.Connector.Statements, s => s.Contains("ALTER LOGIN", StringComparison.Ordinal));
        }
    }
}
