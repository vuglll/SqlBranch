using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

/// <summary>
/// A build from a backup URL on fakes (groups 3 and 4): the credential's life, the statements the engine gets, the
/// errors it can give back and the manifest. The fake pool keeps the build folders and the credential markers;
/// the fake engine keeps the server credentials. None of these tests reaches the real docker.
/// </summary>
public sealed partial class ImageBuildUrlTests
{
    private const string S3Url = "s3://172.17.0.5:9000/backups/nightly/shop.bak";
    private const string S3Name = "s3://172.17.0.5:9000/backups/nightly";
    private const string BlobUrl = "https://acme.blob.core.windows.net/backups/shop.bak";
    private const string BlobName = "https://acme.blob.core.windows.net/backups";
    private const string KeyId = "AKIDTESTKEY1";
    private const string S3Secret = "se'cretTestValue9";
    private const string Sas = "sv=2024-11-04&sp=rl&sig=Zq7TestSig";
    private const string DeadId = "dead00000001";

    private readonly CliWorld _world = new();

    /// <summary>Everything that happened, in order: <c>sh:</c> scripts, <c>sql:</c> statements and <c>step:</c> markers.</summary>
    private readonly List<string> _timeline = [];

    private readonly Dictionary<string, string> _manifests = [];
    private int _fileQueries;
    private int _freeChecks;
    private Func<string, Exception?>? _failSql;
    private int _backupMajor = 16;

    public ImageBuildUrlTests()
    {
        _world.WithEngine();
        _world.Files["/work/crm.bak"] = 1_000_000;
        _world.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = KeyId;
        _world.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = S3Secret;
        _world.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = Sas;

        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (script == StatusReporter.ListingScript || script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal))
            {
                return inner(container, command);
            }

            if (script == ImageCatalog.ListingScript)
            {
                return FakeContainerRuntime.Ok(string.Join('\n', _manifests.Select(m => $"SQLBRANCH_IMAGE|{m.Key}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Value))}")) + "\n");
            }

            _timeline.Add("sh: " + script);
            return Pool(script);
        };
        _world.Runtime.OnCopy = (host, container) => _timeline.Add($"cp: {host} -> {container}");

        var connector = _world.Connector;
        connector.ExecuteHandler = sql =>
        {
            _timeline.Add("sql: " + sql);
            if (_failSql?.Invoke(sql) is { } failure)
            {
                throw failure;
            }
        };
        connector.ParameterizedExecuteHandler = (sql, _) =>
        {
            _timeline.Add("sql: " + sql);
            if (_failSql?.Invoke(sql) is { } failure)
            {
                throw failure;
            }
        };
        connector.ParameterizedScalarHandler = (sql, _) => sql.Contains("recovery_model_desc", StringComparison.Ordinal) ? "FULL" : 1;
        connector.RowsHandler = Rows;
    }

    // ---- the fake pool ----

    [GeneratedRegex(@"printf %s '(?<b>[^']+)' \| base64 -d > '/pool/build/(?<id>[0-9a-f]+)/credential\.json'")]
    private static partial Regex MarkerWrite();

    [GeneratedRegex(@"printf %s '(?<b>[^']+)' \| base64 -d > '/pool/images/(?<n>[^/']+)/manifest\.json'")]
    private static partial Regex ManifestWrite();

    [GeneratedRegex(@"mkdir '/pool/build/(?<id>[0-9a-f]+)'")]
    private static partial Regex MakeFolder();

    [GeneratedRegex(@"^rm -f '/pool/build/(?<id>[0-9a-f]+)/credential\.json'; rmdir")]
    private static partial Regex MarkerRemove();

    [GeneratedRegex(@"^rm -rf '/pool/build/(?<id>[0-9a-f]+)'")]
    private static partial Regex FolderRemove();

    private ContainerResult Pool(string script)
    {
        var host = _world.Host;
        if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
        {
            _freeChecks++;
            return FakeContainerRuntime.Ok("SQLBRANCH_FREE=" + (100L << 30));
        }

        if (script.Contains("SQLBRANCH_EXISTS=", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok("SQLBRANCH_EXISTS=no");
        }

        if (script.Contains("SQLBRANCH_SHA256=", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok("SQLBRANCH_SHA256=" + new string('a', 64));
        }

        if (script.Contains("SQLBRANCH_CRED_DONE", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok(string.Join('\n', host.CredentialMarkers.Select(m =>
                $"SQLBRANCH_CRED|{m.Key}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Value))}")) + "\nSQLBRANCH_CRED_DONE\n");
        }

        if (MarkerWrite().Match(script) is { Success: true } write)
        {
            host.CredentialMarkers[write.Groups["id"].Value] = Encoding.UTF8.GetString(Convert.FromBase64String(write.Groups["b"].Value));
        }
        else if (ManifestWrite().Match(script) is { Success: true } manifest)
        {
            _manifests[manifest.Groups["n"].Value] = Encoding.UTF8.GetString(Convert.FromBase64String(manifest.Groups["b"].Value));
        }
        else if (MarkerRemove().Match(script) is { Success: true } remove)
        {
            host.CredentialMarkers.Remove(remove.Groups["id"].Value);
            if (!host.CredentialMarkers.ContainsKey(remove.Groups["id"].Value))
            {
                host.BuildFolders.Remove(remove.Groups["id"].Value);
            }
        }
        else if (FolderRemove().Match(script) is { Success: true } folder)
        {
            host.CredentialMarkers.Remove(folder.Groups["id"].Value);
            host.BuildFolders.Remove(folder.Groups["id"].Value);
        }
        else if (MakeFolder().Match(script) is { Success: true } made)
        {
            host.BuildFolders.Add(made.Groups["id"].Value);
        }

        return FakeContainerRuntime.Ok();
    }

    // ---- the fake engine's answers to RESTORE ----

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        _timeline.Add("sql: " + sql);
        if (_failSql?.Invoke(sql) is { } failure)
        {
            throw failure;
        }

        if (sql.Contains("LABELONLY", StringComparison.Ordinal))
        {
            return [Row(("FamilyCount", 1))];
        }

        if (sql.Contains("HEADERONLY", StringComparison.Ordinal))
        {
            return [Row(("Position", 1), ("BackupType", 1), ("BackupTypeDescription", "Database"), ("DatabaseName", "Shop"),
                ("SoftwareVersionMajor", _backupMajor), ("SoftwareVersionMinor", 0), ("SoftwareVersionBuild", 4000), ("RecoveryModel", "FULL"))];
        }

        if (sql.Contains("FILELISTONLY", StringComparison.Ordinal))
        {
            return
            [
                Row(("LogicalName", "Shop"), ("PhysicalName", @"C:\Data\Shop.mdf"), ("Type", "D"), ("FileGroupName", "PRIMARY"), ("Size", 8L * 1024 * 1024)),
                Row(("LogicalName", "Shop_log"), ("PhysicalName", @"C:\Data\Shop_log.ldf"), ("Type", "L"), ("FileGroupName", string.Empty), ("Size", 256L * 1024 * 1024)),
            ];
        }

        var database = (string)parameters["n"]!;
        var folder = "/pool/build/" + database["sqlbranch_build_".Length..];
        _fileQueries++;
        return
        [
            Row(("name", "Shop"), ("physical_name", folder + "/Shop.mdf"), ("type_desc", "ROWS"), ("bytes", 8L * 1024 * 1024)),
            Row(("name", "Shop_log"), ("physical_name", folder + "/Shop_log.ldf"), ("type_desc", "LOG"), ("bytes", _fileQueries == 1 ? 256L * 1024 * 1024 : 524_288L)),
        ];
    }

    // ---- helpers ----

    private (int Exit, JsonElement Json, string Out, string Err) Cli(params string[] args)
    {
        var run = _world.Run(args);
        return (run.Exit, string.IsNullOrWhiteSpace(run.Out) ? default : JsonDocument.Parse(run.Out).RootElement, run.Out, run.Err);
    }

    private (int Exit, JsonElement Json, string Out, string Err) BuildCli(string url, params string[] extra) =>
        Cli(["image", "build", "shop", "--from-bak", url, .. extra, "--format", "json"]);

    private static string Code(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString()!;

    private static string Message(JsonElement json) => json.GetProperty("error").GetProperty("message").GetString()!;

    private ImageBuilder Builder(
        Func<string, CancellationToken, Task>? afterStep = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? progress = null,
        string id = "aaaaaaaaaaaa") =>
        new(_world.Runtime, _world.Connector, _world.Redactor, time: null, newId: () => id)
        {
            AfterStep = (step, token) =>
            {
                _timeline.Add("step: " + step);
                return afterStep?.Invoke(step, token) ?? Task.CompletedTask;
            },
            Delay = delay,
            Progress = progress,
        };

    private async Task<ImageBuildResult> BuildDirectAsync(string url, ImageBuilder? builder = null, string? region = null, IReadOnlyList<ImageScript>? post = null, CancellationToken token = default)
    {
        var parsed = BackupUrl.Parse(url, region);
        var secrets = StorageSecrets.Read(parsed, _world.Environment, _world.Redactor);
        var pool = await new PoolFinder(_world.Runtime, ManagedPaths.Default, _ => true).RequireAsync(
            _world.Services().LoadSettings(null), "/home/u/.sqlbranch/pool", CancellationToken.None);
        var request = new ImageBuildRequest("shop", parsed.Url, 0) { Url = parsed, Secrets = secrets, PostScripts = post ?? [] };
        return await (builder ?? Builder()).BuildAsync(pool, 14330, request, token);
    }

    private int IndexOf(string marker, int from = 0) => _timeline.FindIndex(from, l => l.Contains(marker, StringComparison.Ordinal));

    private int Count(string marker) => _timeline.Count(l => l.Contains(marker, StringComparison.Ordinal));

    private static string MarkerJson(string name, FakeCredential? credential) =>
        (credential is null ? new CredentialMarker(name) : new CredentialMarker(name, credential.Id, credential.CreateDate)).ToJson();

    private static string[] CreatesSecret(IEnumerable<string> statements) =>
        [.. statements.Where(s => s.StartsWith("CREATE CREDENTIAL", StringComparison.Ordinal))];

    // ---- 3.1: the credential steps ----

    [Fact]
    public void A_url_build_takes_the_lock_records_creates_reads_restores_and_drops_in_the_order_of_the_design()
    {
        var run = BuildCli(S3Url);

        Assert.Equal(0, run.Exit);
        var intent = IndexOf("credential.json'");
        var create = IndexOf("sql: CREATE CREDENTIAL");
        var full = IndexOf("credential.json'", create);
        var label = IndexOf("LABELONLY");
        var restore = IndexOf("RESTORE DATABASE");
        var drop = IndexOf("sql: DROP CREDENTIAL");
        Assert.True(IndexOf("mkdir '/pool/build/") < intent);
        Assert.True(intent < create && create < full && full < label);
        Assert.True(label < IndexOf("HEADERONLY") && IndexOf("HEADERONLY") < IndexOf("FILELISTONLY") && IndexOf("FILELISTONLY") < restore);
        Assert.True(restore < drop);
        Assert.True(drop < IndexOf("DETACH") || drop < IndexOf("sp_detach_db"));
        Assert.Contains(EngineLocks.Credential(S3Name), _world.Connector.TakenLocks);
        Assert.Contains(EngineLocks.Credential(S3Name), _world.Connector.ReleasedLocks);
        Assert.Empty(_world.Connector.Credentials);
        Assert.Empty(_world.Host.CredentialMarkers);
        Assert.Empty(_world.Runtime.Copies);
    }

    [Fact]
    public async Task The_credential_is_dropped_before_the_post_build_script_login_exists()
    {
        _world.Connector.QueryHandler = sql => sql.Contains("user_access_desc FROM", StringComparison.Ordinal) ? [["ONLINE", false, "MULTI_USER"]] : [];
        var script = new ImageScript(new ScriptFile("mask.sql", new string('a', 64)), [new ScriptBatch(1, "SELECT 1;")]);

        await BuildDirectAsync(S3Url, post: [script]);

        Assert.True(IndexOf("sql: DROP CREDENTIAL") >= 0);
        Assert.True(IndexOf("sql: DROP CREDENTIAL") < IndexOf("CREATE LOGIN"));
        Assert.True(IndexOf("sql: DROP CREDENTIAL") < IndexOf("step: post-scripts"));
    }

    [Fact]
    public void A_name_with_a_closing_bracket_is_quoted_and_the_credential_is_dropped_again()
    {
        var run = BuildCli("s3://172.17.0.5:9000/ba]ckups/shop.bak");

        Assert.Equal(0, run.Exit);
        Assert.Contains(_timeline, l => l.StartsWith("sql: CREATE CREDENTIAL [s3://172.17.0.5:9000/ba]]ckups] WITH IDENTITY = 'S3 Access Key'", StringComparison.Ordinal));
        Assert.Contains(_timeline, l => l == "sql: DROP CREDENTIAL [s3://172.17.0.5:9000/ba]]ckups]");
        Assert.Empty(_world.Connector.Credentials);
    }

    [Fact]
    public void A_secret_with_a_quote_is_doubled_in_the_statement_and_never_reaches_the_output()
    {
        var run = BuildCli(S3Url);

        Assert.Equal(0, run.Exit);
        var create = Assert.Single(_world.Connector.Statements, s => s.StartsWith("CREATE CREDENTIAL", StringComparison.Ordinal));
        Assert.Contains($"SECRET = N'{KeyId}:se''cretTestValue9'", create, StringComparison.Ordinal);
        Assert.DoesNotContain(S3Secret, run.Out + run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyId, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_held_lock_prints_one_message_then_the_build_goes_on()
    {
        var messages = new List<string>();
        var waits = new List<TimeSpan>();
        _world.Connector.HeldLocks.Add(EngineLocks.Credential(S3Name));
        var builder = Builder(
            delay: (time, _) =>
            {
                waits.Add(time);
                if (waits.Count == 3)
                {
                    _world.Connector.HeldLocks.Remove(EngineLocks.Credential(S3Name));
                }

                return Task.CompletedTask;
            },
            progress: messages.Add);

        var result = await BuildDirectAsync(S3Url, builder);

        Assert.NotNull(result.Manifest);
        Assert.Equal($"Waiting for another build that uses {S3Name}", Assert.Single(messages));
        Assert.Equal(3, waits.Count);
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(2), w));
        Assert.Empty(_world.Connector.Credentials);
    }

    [Fact]
    public void The_waiting_message_goes_to_standard_error_through_the_cli()
    {
        _world.Connector.HeldLocks.Add(EngineLocks.Credential(S3Name));
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            _world.Connector.HeldLocks.Remove(EngineLocks.Credential(S3Name));
        });

        var run = BuildCli(S3Url);

        Assert.Equal(0, run.Exit);
        Assert.Equal(1, Regex.Count(run.Err, "Waiting for another build"));
        Assert.Contains(S3Name, run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("Waiting", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wait_cancelled_after_5_seconds_creates_nothing_and_removes_the_folder()
    {
        using var cancel = new CancellationTokenSource();
        var waited = TimeSpan.Zero;
        _world.Connector.HeldLocks.Add(EngineLocks.Credential(S3Name));
        var builder = Builder(delay: (time, _) =>
        {
            waited += time;
            if (waited >= TimeSpan.FromSeconds(5))
            {
                cancel.Cancel();
            }

            return Task.CompletedTask;
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildDirectAsync(S3Url, builder, token: cancel.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(_timeline, l => l.Contains("CREATE CREDENTIAL", StringComparison.Ordinal));
        Assert.DoesNotContain(_timeline, l => l.Contains("base64 -d > '/pool/build/", StringComparison.Ordinal));
        Assert.Contains(_timeline, l => l.StartsWith("sh: rm -rf '/pool/build/aaaaaaaaaaaa'", StringComparison.Ordinal));
        Assert.Empty(_world.Host.BuildFolders);
        Assert.Empty(_world.Connector.Credentials);
    }

    [Fact]
    public async Task A_resweep_under_the_lock_clears_a_dead_builds_matching_credential_before_the_conflict_check()
    {
        var dead = _world.Connector.AddCredential(S3Name, "old-secret");
        _world.Host.CredentialMarkers[DeadId] = MarkerJson(S3Name, dead);
        _world.Host.BuildFolders.Add(DeadId);

        // A running build holds the name while the first sweep looks, then finishes: only the re-sweep can clear the marker.
        _world.Connector.HeldLocks.Add(EngineLocks.Credential(S3Name));
        var builder = Builder(delay: (_, _) =>
        {
            _world.Connector.HeldLocks.Remove(EngineLocks.Credential(S3Name));
            return Task.CompletedTask;
        });

        await BuildDirectAsync(S3Url, builder);

        var dropOfDead = IndexOf("sql: DROP CREDENTIAL");
        var create = IndexOf("sql: CREATE CREDENTIAL");
        Assert.True(dropOfDead >= 0 && dropOfDead < create);
        Assert.Equal(2, Count("sql: DROP CREDENTIAL"));
        Assert.DoesNotContain(DeadId, _world.Host.CredentialMarkers.Keys);
        Assert.Empty(_world.Connector.Credentials);
    }

    [Fact]
    public void A_hand_made_credential_with_the_name_is_a_conflict_and_is_never_created_dropped_or_changed()
    {
        var handMade = _world.Connector.AddCredential(BlobName, "hands-off");

        var run = BuildCli(BlobUrl);

        Assert.Equal(1, run.Exit);
        Assert.Equal("credential_conflict", Code(run.Json));
        Assert.Contains(BlobName, Message(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain(_timeline, l => l.Contains("CREATE CREDENTIAL", StringComparison.Ordinal) || l.Contains("DROP CREDENTIAL", StringComparison.Ordinal));
        Assert.Equal(handMade, Assert.Single(_world.Connector.Credentials));
        Assert.Empty(_world.Host.CredentialMarkers);
        Assert.Empty(_world.Host.BuildFolders);
        Assert.DoesNotContain(Sas, run.Out + run.Err, StringComparison.Ordinal);
    }

    // ---- the read-back rule ----

    [Fact]
    public async Task A_create_that_worked_and_a_failed_read_back_is_read_again_and_dropped_once()
    {
        _world.Connector.ReadBackFaults = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => BuildDirectAsync(S3Url));

        Assert.Equal(1, Count("sql: DROP CREDENTIAL"));
        Assert.Empty(_world.Connector.Credentials);
        Assert.Empty(_world.Host.CredentialMarkers);
        Assert.Empty(_world.Host.BuildFolders);
    }

    [Fact]
    public async Task A_create_the_engine_refused_never_drops_a_credential_of_that_name_made_in_between()
    {
        FakeCredential? other = null;
        _failSql = sql =>
        {
            if (!sql.StartsWith("CREATE CREDENTIAL", StringComparison.Ordinal))
            {
                return null;
            }

            other = _world.Connector.AddCredential(S3Name, "someone-else");
            return new EngineSqlException("A credential with the name already exists.", [15530]);
        };

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => BuildDirectAsync(S3Url));

        Assert.Contains("failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, Count("sql: DROP CREDENTIAL"));
        Assert.Equal(other, Assert.Single(_world.Connector.Credentials));
        Assert.Empty(_world.Host.CredentialMarkers);
    }

    [Fact]
    public async Task When_the_read_back_cannot_be_done_at_all_the_message_says_to_drop_the_credential_by_hand()
    {
        _world.Connector.ReadBackFaults = 2;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => BuildDirectAsync(S3Url));

        Assert.Contains($"DROP CREDENTIAL [{S3Name}]", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("next 'sqlbranch image build' removes it", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(S3Secret, ex.Message, StringComparison.Ordinal);
    }

    // ---- the real mapping of a SqlClient error ----

    /// <summary>A real <see cref="Microsoft.Data.SqlClient.SqlException"/>, built the way SqlClient builds one.</summary>
    private static Microsoft.Data.SqlClient.SqlException RealSqlException(int number, byte severity, string message)
    {
        const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var sqlClient = typeof(Microsoft.Data.SqlClient.SqlException).Assembly;
        var errorType = typeof(Microsoft.Data.SqlClient.SqlError);
        var ctor = errorType.GetConstructors(All).OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => p.Name switch
        {
            "infoNumber" => (object)number,
            "errorClass" => severity,
            "errorMessage" => message,
            _ => p.ParameterType == typeof(string) ? "server" : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        }).ToArray();
        var error = ctor.Invoke(args);
        var collection = (Microsoft.Data.SqlClient.SqlErrorCollection)Activator.CreateInstance(typeof(Microsoft.Data.SqlClient.SqlErrorCollection), All, null, null, null)!;
        collection.GetType().GetMethod("Add", All)!.Invoke(collection, [error]);
        var create = typeof(Microsoft.Data.SqlClient.SqlException).GetMethods(All).First(m => m.Name == "CreateException" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(Microsoft.Data.SqlClient.SqlErrorCollection));
        Assert.NotNull(sqlClient);
        return (Microsoft.Data.SqlClient.SqlException)create.Invoke(null, [collection, "16.0"])!;
    }

    [Theory]
    [InlineData(15530, 16, false)]
    [InlineData(2714, 16, false)]
    [InlineData(-2, 11, true)]
    [InlineData(0, 11, true)]
    [InlineData(233, 20, true)]
    [InlineData(10054, 20, true)]
    [InlineData(5000, 20, true)]
    public void The_connector_mapping_keeps_the_number_and_class_and_tells_a_refusal_from_an_unknown_outcome(int number, int severity, bool unknown)
    {
        var mapped = EngineSqlException.From(RealSqlException(number, (byte)severity, "boom"));

        Assert.Equal([number], mapped.Numbers);
        Assert.Equal(severity, mapped.Severity);
        Assert.Equal(unknown, mapped.OutcomeUnknown);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(10054)]
    public async Task A_create_with_an_unknown_outcome_is_read_back_and_its_own_credential_is_dropped(int number)
    {
        // The statement ran on the server, then the connection broke: the credential is this build's.
        _failSql = sql =>
        {
            if (!sql.StartsWith("CREATE CREDENTIAL", StringComparison.Ordinal))
            {
                return null;
            }

            _world.Connector.AddCredential(S3Name, "this-build");
            return EngineSqlException.From(RealSqlException(number, 20, "The connection was lost."));
        };

        await Assert.ThrowsAsync<SqlBranchException>(() => BuildDirectAsync(S3Url));

        Assert.Equal(1, Count("sql: DROP CREDENTIAL"));
        Assert.Empty(_world.Connector.Credentials);
    }

    [Fact]
    public async Task A_create_refused_with_what_the_real_mapping_gives_drops_nothing()
    {
        _failSql = sql =>
        {
            if (!sql.StartsWith("CREATE CREDENTIAL", StringComparison.Ordinal))
            {
                return null;
            }

            _world.Connector.AddCredential(S3Name, "someone-else");
            return EngineSqlException.From(RealSqlException(15530, 16, "A credential with that name already exists."));
        };

        await Assert.ThrowsAsync<SqlBranchException>(() => BuildDirectAsync(S3Url));

        Assert.Equal(0, Count("sql: DROP CREDENTIAL"));
        Assert.Single(_world.Connector.Credentials);
    }

    // ---- 3.2: cleanup ----

    public static TheoryData<string> UrlSteps() => new(ImageBuilder.StepNames(ImageSource.Url));

    [Theory]
    [MemberData(nameof(UrlSteps))]
    public async Task A_failure_after_any_url_step_drops_the_credential_once_after_the_create_and_leaves_nothing(string step)
    {
        var failing = Builder(afterStep: (name, _) => name == step ? throw new InvalidOperationException("injected") : Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildDirectAsync(BlobUrl, failing));

        Assert.Equal("injected", ex.Message);
        var steps = ImageBuilder.StepNames(ImageSource.Url).ToList();
        bool Reached(string name) => steps.IndexOf(step) >= steps.IndexOf(name);
        Assert.Equal(Reached("credential"), Count("sql: CREATE CREDENTIAL") == 1);
        Assert.Equal(Reached("credential") ? 1 : 0, Count("sql: DROP CREDENTIAL"));
        if (Reached("credential"))
        {
            Assert.True(IndexOf("sql: CREATE CREDENTIAL") < IndexOf("sql: DROP CREDENTIAL"));
        }

        Assert.Empty(_world.Connector.Credentials);
        Assert.Empty(_world.Host.CredentialMarkers);
        Assert.Empty(_world.Host.BuildFolders);
        Assert.DoesNotContain(Sas, string.Join('\n', _timeline.Where(l => !l.StartsWith("sql: CREATE CREDENTIAL", StringComparison.Ordinal))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_drop_exits_1_before_any_script_and_leaves_a_folder_with_only_the_marker()
    {
        _world.Connector.QueryHandler = sql => sql.Contains("user_access_desc FROM", StringComparison.Ordinal) ? [["ONLINE", false, "MULTI_USER"]] : [];
        _world.Connector.FailDropCredential.Add(BlobName);
        var script = new ImageScript(new ScriptFile("mask.sql", new string('a', 64)), [new ScriptBatch(1, "SELECT 1;")]);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => BuildDirectAsync(BlobUrl, post: [script]));

        Assert.Equal("operation_failed", ex.Code.Code);
        Assert.Contains(BlobName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("next 'sqlbranch image build'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sas, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_timeline, l => l.Contains("CREATE LOGIN", StringComparison.Ordinal));
        Assert.DoesNotContain(_timeline, l => l.Contains("chmod 444", StringComparison.Ordinal));
        Assert.Empty(_manifests);
        Assert.Equal(2, Count("sql: DROP CREDENTIAL")); // the build's try and the cleanup's try
        Assert.DoesNotContain(_timeline, l => l.StartsWith("sh: rm -rf '/pool/build/aaaaaaaaaaaa'", StringComparison.Ordinal));
        Assert.Contains(_timeline, l => l.Contains("-mindepth 1 ! -name credential.json -delete", StringComparison.Ordinal));
        Assert.Contains("aaaaaaaaaaaa", _world.Host.CredentialMarkers.Keys);
        Assert.Single(_world.Connector.Credentials);
    }

    [Fact]
    public void A_failed_drop_is_a_status_leftover_and_the_next_build_removes_it_and_the_folder()
    {
        _world.Connector.FailDropCredential.Add(BlobName);
        var failed = BuildCli(BlobUrl);

        Assert.Equal(1, failed.Exit);
        Assert.Contains(BlobName, Message(failed.Json), StringComparison.Ordinal);
        Assert.DoesNotContain(Sas, failed.Out + failed.Err, StringComparison.Ordinal);

        var status = Cli("status", "--format", "json");
        Assert.Equal(1, status.Exit);
        var mismatches = status.Json.GetProperty("mismatches").EnumerateArray().ToList();
        var leftover = Assert.Single(mismatches, m => m.GetProperty("kind").GetString() == "build_credential");
        Assert.Equal(BlobName, leftover.GetProperty("name").GetString());
        Assert.DoesNotContain(Sas, status.Out + status.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("hand-made", status.Out, StringComparison.Ordinal);

        _world.Connector.FailDropCredential.Clear();
        var next = Cli("image", "build", "crm", "--from-bak", "crm.bak", "--format", "json");

        Assert.Equal(0, next.Exit);
        Assert.Empty(_world.Connector.Credentials);
        Assert.Empty(_world.Host.CredentialMarkers);
        Assert.Empty(_world.Host.BuildFolders);
        Assert.Equal(0, Cli("status", "--format", "json").Exit);
    }

    // ---- 3.3: the sweep ----

    private (FakeCredential Dead, FakeCredential App) DeadBuildWithCredential()
    {
        var dead = _world.Connector.AddCredential(S3Name, "dead-secret");
        var app = _world.Connector.AddCredential("app_cred", "app-secret");
        _world.Host.CredentialMarkers[DeadId] = MarkerJson(S3Name, dead);
        _world.Host.BuildFolders.Add(DeadId);
        return (dead, app);
    }

    [Fact]
    public void The_sweep_drops_a_killed_builds_credential_and_never_touches_another()
    {
        var (_, app) = DeadBuildWithCredential();

        var run = Cli("image", "build", "crm", "--from-bak", "crm.bak", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Equal(app, Assert.Single(_world.Connector.Credentials));
        Assert.Equal(["DROP CREDENTIAL [" + S3Name + "]"], _world.Connector.Statements.Where(s => s.Contains("CREDENTIAL", StringComparison.Ordinal)));
        Assert.DoesNotContain(_world.Connector.Parameters, p => p.ContainsValue("app_cred"));
        Assert.Empty(_world.Host.CredentialMarkers);
        Assert.Empty(_world.Host.BuildFolders);
    }

    [Fact]
    public void A_stale_marker_and_a_hand_made_credential_of_the_same_name_leave_the_credential_alone()
    {
        var stale = new FakeCredential(S3Name, 1, "2020-01-01T00:00:00.000", "gone");
        _world.Host.CredentialMarkers[DeadId] = MarkerJson(S3Name, stale);
        var handMade = _world.Connector.AddCredential(S3Name, "mine");

        var run = Cli("image", "build", "crm", "--from-bak", "crm.bak", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Equal(handMade, Assert.Single(_world.Connector.Credentials));
        Assert.DoesNotContain(_timeline, l => l.Contains("DROP CREDENTIAL", StringComparison.Ordinal));
        Assert.Empty(_world.Host.CredentialMarkers);
    }

    [Fact]
    public void A_stale_marker_while_a_running_build_holds_the_name_leaves_both_alone()
    {
        var (dead, _) = DeadBuildWithCredential();
        _world.Connector.HeldLocks.Add(EngineLocks.Credential(S3Name));

        var run = Cli("image", "build", "crm", "--from-bak", "crm.bak", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Contains(dead, _world.Connector.Credentials);
        Assert.Contains(DeadId, _world.Host.CredentialMarkers.Keys);
        Assert.DoesNotContain(_timeline, l => l.Contains("DROP CREDENTIAL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_marker_of_a_build_that_still_runs_is_left_alone()
    {
        var (dead, _) = DeadBuildWithCredential();
        _world.Connector.HeldLocks.Add(EngineLocks.Build(DeadId));

        var run = Cli("image", "build", "crm", "--from-bak", "crm.bak", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Contains(dead, _world.Connector.Credentials);
        Assert.Contains(DeadId, _world.Host.CredentialMarkers.Keys);
    }

    [Fact]
    public void An_intent_marker_is_deleted_without_dropping_the_hand_made_credential()
    {
        _world.Host.CredentialMarkers[DeadId] = MarkerJson(S3Name, null);
        _world.Host.BuildFolders.Add(DeadId);
        var handMade = _world.Connector.AddCredential(S3Name, "mine");

        var run = Cli("image", "build", "crm", "--from-bak", "crm.bak", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Equal(handMade, Assert.Single(_world.Connector.Credentials));
        Assert.DoesNotContain(_timeline, l => l.Contains("DROP CREDENTIAL", StringComparison.Ordinal));
        Assert.Empty(_world.Host.CredentialMarkers);
    }

    [Fact]
    public void A_failing_sweep_drop_warns_exits_0_and_keeps_the_marker()
    {
        var (dead, _) = DeadBuildWithCredential();
        _world.Connector.FailDropCredential.Add(S3Name);

        var run = Cli("image", "build", "crm", "--from-bak", "crm.bak", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Contains("warning:", run.Err, StringComparison.Ordinal);
        Assert.Contains(S3Name, run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("dead-secret", run.Out + run.Err, StringComparison.Ordinal);
        Assert.Contains(dead, _world.Connector.Credentials);
        Assert.Contains(DeadId, _world.Host.CredentialMarkers.Keys);
    }

    // ---- 4.1: check-source ----

    [Fact]
    public void The_url_step_list_follows_the_design()
    {
        Assert.Equal(
            ["check-name", "check-source", "build-folder", "credential", "inspect", "check-restore-space", "restore", "drop-credential",
                "post-scripts", "check-database", "recovery-model", "checkpoint", "detach", "rebuild-log", "seal", "manifest"],
            ImageBuilder.StepNames(ImageSource.Url));
        Assert.DoesNotContain("stage", ImageBuilder.StepNames(ImageSource.Url));
        Assert.DoesNotContain("check-backup-space", ImageBuilder.StepNames(ImageSource.Url));
    }

    [Fact]
    public void An_s3_url_on_sql_server_2019_exits_1_unsupported_source_before_any_credential()
    {
        _world.Connector.Version = "15.0.4400.1|Developer Edition (64-bit)";

        var run = BuildCli(S3Url);

        Assert.Equal(1, run.Exit);
        Assert.Equal("unsupported_source", Code(run.Json));
        Assert.Contains("2019", Message(run.Json), StringComparison.Ordinal);
        Assert.Contains("engineImage", Message(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain(_timeline, l => l.Contains("CREDENTIAL", StringComparison.Ordinal));
        Assert.Empty(_world.Host.BuildFolders);
    }

    [Fact]
    public void A_300_character_sas_token_on_sql_server_2019_exits_2_without_showing_the_token()
    {
        _world.Connector.Version = "15.0.4400.1|Developer Edition (64-bit)";
        var token = "sv=2024-11-04&sig=" + new string('Q', 282);
        Assert.Equal(300, token.Length);
        _world.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = token;

        var run = BuildCli(BlobUrl);

        Assert.Equal(2, run.Exit);
        Assert.Equal("config_error", Code(run.Json));
        Assert.Contains("SQLBRANCH_AZURE_SAS_TOKEN", Message(run.Json), StringComparison.Ordinal);
        Assert.Contains("256", Message(run.Json), StringComparison.Ordinal);
        Assert.Contains("2019", Message(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('Q', 20), run.Out + run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(_timeline, l => l.Contains("CREDENTIAL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_sas_token_is_fine_on_sql_server_2022()
    {
        _world.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = "sv=2024-11-04&sig=" + new string('Q', 282);

        Assert.Equal(0, BuildCli(BlobUrl).Exit);
    }

    // ---- 4.2: the statements ----

    [Fact]
    public void The_statements_carry_the_url_as_a_parameter_and_s3_names_the_region_on_all_four()
    {
        var run = BuildCli(S3Url, "--s3-region", "eu-west-1");

        Assert.Equal(0, run.Exit);
        var reads = _world.Connector.Statements.Where(s => s.StartsWith("RESTORE", StringComparison.Ordinal)).Select(s => (Sql: s, Parameters: (Dictionary<string, object?>?)null)).ToList();
        Assert.Equal(["LABELONLY", "HEADERONLY", "FILELISTONLY", "DATABASE"], reads.Select(r => r.Sql.Split(' ')[1]));
        Assert.Equal(4, _world.Connector.Parameters.Count(p => p.TryGetValue("bak", out var value) && Equals(value, S3Url)));
        Assert.DoesNotContain(_world.Connector.Parameters, p => p.ContainsKey("file"));
        foreach (var read in reads)
        {
            Assert.Contains("FROM URL = @bak", read.Sql, StringComparison.Ordinal);
            Assert.Contains("RESTORE_OPTIONS = '{\"s3\": {\"region\":\"eu-west-1\"}}'", read.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("FILE = @file", read.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain(S3Url, read.Sql, StringComparison.Ordinal);
        }

        Assert.Equal(1, _freeChecks); // only the restored files; no staged copy to make room for
        Assert.DoesNotContain(_timeline, l => l.Contains("sha256sum", StringComparison.Ordinal));
        Assert.Empty(_world.Runtime.Copies);
        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("cp ", StringComparison.Ordinal));
    }

    [Fact]
    public void S3_uses_us_east_1_by_default_and_blob_has_no_region_option()
    {
        Assert.Equal(0, BuildCli(S3Url).Exit);
        Assert.Contains("\"region\":\"us-east-1\"", _world.Connector.Statements.Single(s => s.StartsWith("RESTORE DATABASE", StringComparison.Ordinal)), StringComparison.Ordinal);

        _world.Connector.Statements.Clear();
        _world.Host.BuildFolders.Clear();
        Assert.Equal(0, BuildCli(BlobUrl).Exit);
        Assert.All(_world.Connector.Statements.Where(s => s.StartsWith("RESTORE", StringComparison.Ordinal)),
            s => Assert.DoesNotContain("RESTORE_OPTIONS", s, StringComparison.Ordinal));
        Assert.Contains(_world.Connector.Statements, s => s.Contains("IDENTITY = 'SHARED ACCESS SIGNATURE'", StringComparison.Ordinal));
    }

    // ---- 4.3: errors ----

    private static EngineSqlException Engine(string message, params int[] numbers) => new(message, numbers);

    private static SqlBranchException Map(EngineSqlException error, string url = S3Url, BackupStage stage = BackupStage.Label) =>
        BackupInspector.MapError(error, url, stage, "16.0.4325.4", BackupUrl.Parse(url));

    [Theory]
    [InlineData(5, "Access is denied.", "access was denied")]
    [InlineData(2, "The system cannot find the file specified.", "not found")]
    [InlineData(86, "The specified network password is not correct.", "no storage credential")]
    [InlineData(12175, "", "does not trust the endpoint's certificate")]
    [InlineData(12007, "", "connect")]
    [InlineData(12029, "", "connect")]
    public void Recorded_engine_texts_map_to_backup_unreachable_with_the_url_and_the_reason(int osError, string text, string reason)
    {
        var error = Engine($"Cannot open backup device '{S3Url}'. Operating system error {osError}({text}).", 3201, 3013);

        var mapped = Map(error);

        Assert.Equal("backup_unreachable", mapped.Code.Code);
        Assert.Contains(S3Url, mapped.Message, StringComparison.Ordinal);
        Assert.Contains(reason, mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_untrusted_certificate_gets_the_hint_about_the_certificate_folder()
    {
        var mapped = Map(Engine("Operating system error 12175().", 3201));

        Assert.Contains("/var/opt/mssql/security/ca-certificates", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_storage_account_that_does_not_resolve_is_unreachable_too()
    {
        var mapped = Map(Engine($"The file name '{BlobUrl}' is invalid as a backup device name for the specified device type.", 3078), BlobUrl);

        Assert.Equal("backup_unreachable", mapped.Code.Code);
        Assert.Contains(BlobUrl, mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Striping_and_version_errors_keep_their_codes_for_a_url()
    {
        Assert.Equal("unsupported_backup_type", Map(Engine("striped", 3132)).Code.Code);

        var version = Map(Engine("The backup was made on a server running version 17.00.1000. Restore on version 16.00.4325.", 3169), BlobUrl, BackupStage.Restore);
        Assert.Equal("version_mismatch", version.Code.Code);
        Assert.Contains("update policy", version.Message, StringComparison.Ordinal);
        Assert.Contains("Always-up-to-date", version.Message, StringComparison.Ordinal);

        var header = Map(Engine("cannot process", 3241), BlobUrl, BackupStage.Header);
        Assert.Equal("version_mismatch", header.Code.Code);
        Assert.Contains("Always-up-to-date", header.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_backup_at_a_url_names_the_managed_instance_policy_through_the_cli()
    {
        _backupMajor = 17;

        var run = BuildCli(BlobUrl);

        Assert.Equal(1, run.Exit);
        Assert.Equal("version_mismatch", Code(run.Json));
        Assert.Contains("update policy", Message(run.Json), StringComparison.Ordinal);
        Assert.Contains("Always-up-to-date", Message(run.Json), StringComparison.Ordinal);
        Assert.Empty(_world.Connector.Credentials);
    }

    [Fact]
    public void A_wrong_secret_exits_1_backup_unreachable_and_leaves_no_credential_and_a_clean_status()
    {
        _failSql = sql => sql.Contains("LABELONLY", StringComparison.Ordinal)
            ? Engine($"Cannot open backup device '{S3Url}'. Operating system error 5(Access is denied.).", 3201, 3013)
            : null;

        var run = BuildCli(S3Url);

        Assert.Equal(1, run.Exit);
        Assert.Equal("backup_unreachable", Code(run.Json));
        Assert.Contains(S3Url, Message(run.Json), StringComparison.Ordinal);
        Assert.Empty(_world.Connector.Credentials);
        Assert.Empty(_manifests);
        _failSql = null;
        Assert.Equal(0, Cli("status", "--format", "json").Exit);
    }

    [Fact]
    public void A_missing_blob_names_the_url()
    {
        const string Missing = "https://acme.blob.core.windows.net/backups/nope.bak";
        _failSql = sql => sql.Contains("LABELONLY", StringComparison.Ordinal)
            ? Engine($"Cannot open backup device '{Missing}'. Operating system error 2(The system cannot find the file specified.).", 3201, 3013)
            : null;

        var run = BuildCli(Missing);

        Assert.Equal(1, run.Exit);
        Assert.Equal("backup_unreachable", Code(run.Json));
        Assert.Contains(Missing, Message(run.Json), StringComparison.Ordinal);
    }

    [Fact]
    public void An_engine_text_that_echoes_the_secret_is_shown_with_stars()
    {
        _failSql = sql => sql.Contains("HEADERONLY", StringComparison.Ordinal)
            ? Engine($"Cannot open backup device '{S3Url}'. Operating system error 5(Access is denied for {KeyId}:{S3Secret} and {Sas}).", 3201)
            : null;

        var run = BuildCli(S3Url);

        Assert.Equal(1, run.Exit);
        Assert.Contains("***", Message(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain(S3Secret, run.Out + run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyId, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_text_that_echoes_the_secret_is_redacted_by_the_builder_itself()
    {
        _failSql = sql => sql.Contains("RESTORE DATABASE", StringComparison.Ordinal)
            ? Engine($"Cannot open backup device '{S3Url}'. Operating system error 5(denied {KeyId}:{S3Secret}).", 3201, 3013)
            : null;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => BuildDirectAsync(S3Url));

        Assert.Equal("backup_unreachable", ex.Code.Code);
        Assert.DoesNotContain(S3Secret, ex.Message, StringComparison.Ordinal);
        Assert.Contains("***", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_world.Connector.Credentials);
    }

    [Fact]
    public void A_failed_create_exits_1_with_the_secret_masked_and_leaves_nothing()
    {
        _world.Connector.FailCreateCredential = new InvalidOperationException($"bad secret {KeyId}:{S3Secret}");

        var run = BuildCli(S3Url);

        Assert.Equal(1, run.Exit);
        Assert.Contains("***", Message(run.Json), StringComparison.Ordinal);
        Assert.DoesNotContain(S3Secret, run.Out + run.Err, StringComparison.Ordinal);
        Assert.Empty(_world.Connector.Credentials);
        Assert.Empty(_world.Host.CredentialMarkers);
        Assert.Empty(_world.Host.BuildFolders);
    }

    // ---- 4.4: the manifest ----

    [Fact]
    public void A_url_build_records_the_url_and_file_name_and_no_hash_and_no_secret()
    {
        var run = BuildCli(S3Url);

        Assert.Equal(0, run.Exit);
        var image = run.Json.GetProperty("image");
        Assert.Equal(S3Url, image.GetProperty("backupUrl").GetString());
        Assert.Equal("shop.bak", image.GetProperty("backupFile").GetString());
        Assert.Equal(JsonValueKind.Null, image.GetProperty("backupSha256").ValueKind);
        Assert.Equal("backup", image.GetProperty("sourceType").GetString());

        var json = _manifests["shop"];
        var manifest = ImageManifest.FromJson(json);
        Assert.Equal(S3Url, manifest.BackupUrl);
        Assert.Null(manifest.BackupSha256);
        Assert.DoesNotContain(KeyId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(S3Secret, json, StringComparison.Ordinal);
        Assert.Equal(json, ImageManifest.FromJson(json).ToJson());
    }

    [Fact]
    public void A_manifest_written_before_urls_reads_backupUrl_as_null()
    {
        var run = BuildCli(BlobUrl);
        Assert.Equal(0, run.Exit);
        var old = JsonDocument.Parse(_manifests["shop"]).RootElement.EnumerateObject()
            .Where(p => p.Name != "backupUrl").ToDictionary(p => p.Name, p => p.Value);

        var manifest = ImageManifest.FromJson(JsonSerializer.Serialize(old));

        Assert.Null(manifest.BackupUrl);
    }

    [Fact]
    public void Image_list_shows_the_url_in_the_source_column_and_in_json()
    {
        Assert.Equal(0, BuildCli(S3Url).Exit);

        var text = _world.Run("image", "list");
        var json = Cli("image", "list", "--format", "json");

        Assert.Equal(0, text.Exit);
        Assert.Contains(S3Url, text.Out, StringComparison.Ordinal);
        Assert.Equal(S3Url, json.Json.GetProperty("images")[0].GetProperty("backupUrl").GetString());
    }

    [Fact]
    public void A_file_build_reports_backupUrl_null()
    {
        var run = Cli("image", "build", "crm", "--from-bak", "crm.bak", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("image").GetProperty("backupUrl").ValueKind);
        Assert.Equal("crm.bak", run.Json.GetProperty("image").GetProperty("backupFile").GetString());
    }
}
