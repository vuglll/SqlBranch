using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Tests.Images;

/// <summary>The checks on recorded results of <c>RESTORE LABELONLY</c>, <c>HEADERONLY</c> and <c>FILELISTONLY</c>.</summary>
public sealed class BackupInspectorTests
{
    private const string Engine2022 = "16.0.4325.4";

    private static Dictionary<string, object?> Header(int position, int type, string description, int major = 16, string database = "Shop") =>
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Position"] = (short)position,
            ["BackupType"] = (short)type,
            ["BackupTypeDescription"] = description,
            ["DatabaseName"] = database,
            ["SoftwareVersionMajor"] = major,
            ["SoftwareVersionMinor"] = 0,
            ["SoftwareVersionBuild"] = 4000,
            ["RecoveryModel"] = "FULL",
        };

    private static Dictionary<string, object?> FileRow(string logical, string physical, string type, string group, long size) =>
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["LogicalName"] = logical,
            ["PhysicalName"] = physical,
            ["Type"] = type,
            ["FileGroupName"] = group,
            ["Size"] = size,
        };

    private static readonly IReadOnlyDictionary<string, object?> Labels = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["FamilyCount"] = 1 };

    private static readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> TwoFiles =
    [
        FileRow("Shop", @"C:\Data\Shop.mdf", "D", "PRIMARY", 8_388_608),
        FileRow("Shop_log", @"C:\Data\Shop_log.ldf", "L", string.Empty, 268_435_456),
    ];

    private sealed class Script(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> headers,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> files,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? labels = null,
        EngineSqlException? failWith = null,
        string? failOn = null) : IEngineSession
    {
        public List<(string Sql, Dictionary<string, object?> Parameters)> Queries { get; } = [];

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            Queries.Add((sql, new Dictionary<string, object?>(parameters)));
            if (failWith is not null && (failOn is null || sql.Contains(failOn, StringComparison.Ordinal)))
            {
                throw failWith;
            }

            return Task.FromResult(sql.Contains("LABELONLY", StringComparison.Ordinal) ? labels ?? [Labels]
                : sql.Contains("HEADERONLY", StringComparison.Ordinal) ? headers
                : files);
        }

        public Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<object?> ScalarAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Task<BackupInspection> Inspect(Script session, int set = 1, string engine = Engine2022) =>
        BackupInspector.InspectAsync(session, "/pool/build/x/backup.bak", set, engine, CancellationToken.None);

    [Fact]
    public async Task A_full_backup_of_an_older_or_equal_version_is_accepted_and_its_files_are_read()
    {
        var session = new Script([Header(1, 1, "Database", major: 15)], TwoFiles);

        var inspection = await Inspect(session);

        Assert.Equal("Shop", inspection.Set.DatabaseName);
        Assert.Equal(2, inspection.Files.Count);
        Assert.Equal(8_388_608 + 268_435_456, inspection.TotalBytes);
        Assert.Equal(268_435_456, inspection.LogBytes);
        Assert.Equal("/pool/build/x/backup.bak", session.Queries[0].Parameters["bak"]);
        Assert.Equal(1, session.Queries[^1].Parameters["file"]);
    }

    [Fact]
    public async Task The_path_goes_in_as_a_parameter_never_into_the_statement()
    {
        var session = new Script([Header(1, 1, "Database")], TwoFiles);

        await BackupInspector.InspectAsync(session, "/pool/import/o'brien.bak", 1, Engine2022, CancellationToken.None);

        Assert.All(session.Queries, q =>
        {
            Assert.DoesNotContain("brien", q.Sql, StringComparison.Ordinal);
            Assert.Equal("/pool/import/o'brien.bak", q.Parameters["bak"]);
        });
    }

    [Fact]
    public async Task The_chosen_backup_set_is_used_for_the_file_list()
    {
        var session = new Script([Header(1, 5, "Database Differential"), Header(2, 1, "Database"), Header(3, 2, "Transaction Log")], TwoFiles);

        var inspection = await Inspect(session, set: 2);

        Assert.Equal(2, inspection.Set.Position);
        Assert.Equal(3, inspection.SetCount);
        Assert.Equal(2, session.Queries[^1].Parameters["file"]);
    }

    [Fact]
    public async Task Set_1_is_the_default_and_a_differential_set_is_refused_with_the_type_code()
    {
        var session = new Script([Header(1, 5, "Database Differential")], TwoFiles);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.UnsupportedBackupType, ex.Code);
        Assert.Contains("Only full database backups are supported", ex.Message, StringComparison.Ordinal);
        Assert.Contains("database differential", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Queries, q => q.Sql.Contains("FILELISTONLY", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2, "Transaction Log")]
    [InlineData(4, "File or filegroup")]
    [InlineData(6, "File or filegroup differential")]
    [InlineData(7, "Partial")]
    public async Task A_log_file_or_partial_backup_is_refused(int type, string description)
    {
        var session = new Script([Header(1, type, description)], TwoFiles);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.UnsupportedBackupType, ex.Code);
    }

    [Fact]
    public async Task A_set_number_the_file_does_not_have_is_refused_naming_the_count()
    {
        var session = new Script([Header(1, 1, "Database")], TwoFiles);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session, set: 3));

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.Contains("holds 1 backup set", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--backup-set 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backup_from_sql_server_2025_on_a_2022_engine_names_both_versions_and_how_to_choose_a_newer_image()
    {
        var session = new Script([Header(1, 1, "Database", major: 17)], TwoFiles);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.VersionMismatch, ex.Code);
        Assert.Contains("SQL Server 2025 (17.0.4000)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SQL Server 2022 (16.0.4325)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("engineImage", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SQL Server 2025 or later", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_memory_optimized_or_filestream_container_is_refused_naming_the_feature()
    {
        var files = new List<IReadOnlyDictionary<string, object?>>(TwoFiles)
        {
            FileRow("Shop_mod", "/data/Shop_mod", "S", "ShopInMemory", 0),
        };
        var session = new Script([Header(1, 1, "Database")], files);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.UnsupportedFeature, ex.Code);
        Assert.Contains("memory-optimized filegroup", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FILESTREAM", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ShopInMemory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backup_striped_over_several_files_is_refused()
    {
        var session = new Script([Header(1, 1, "Database")], TwoFiles, labels: [new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["FamilyCount"] = 3 }]);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.UnsupportedBackupType, ex.Code);
        Assert.Contains("striped", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_engines_error_about_missing_stripes_is_also_refused_as_striped()
    {
        var session = new Script([], [], failWith: new EngineSqlException("The media set has 2 media families but only 1 are provided.", [3132, 3013]));

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.UnsupportedBackupType, ex.Code);
        Assert.Contains("striped", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3241)]
    [InlineData(3242)]
    [InlineData(3243)]
    [InlineData(3254)]
    public async Task A_file_the_engine_cannot_read_as_a_backup_is_not_a_backup(int number)
    {
        var session = new Script([], [], failWith: new EngineSqlException("The media family on device is incorrectly formed.", [number]));

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.NotABackup, ex.Code);
        Assert.Equal(ExitCode.OperationFailed, ex.Code.ExitCode);
        Assert.Contains("backup.bak", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_engine_says_about_a_text_file_is_not_a_backup()
    {
        // As recorded from SQL Server 2022 for a plain text file: the volume is empty, then the generic termination.
        var session = new Script([], [], failWith: new EngineSqlException(
            "The volume on device '/pool/build/x/backup.bak' is empty.\nRESTORE LABELONLY is terminating abnormally.", [3254, 3013]));

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.NotABackup, ex.Code);
    }

    [Fact]
    public async Task A_lone_3013_is_only_the_generic_terminating_error_and_keeps_the_engines_message()
    {
        var session = new Script([], [], failWith: new EngineSqlException("RESTORE HEADERONLY is terminating abnormally.", [3013]));

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.Contains("terminating abnormally", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_cannot_be_opened_is_a_plain_failure_with_the_operating_systems_message_not_a_bad_backup()
    {
        var session = new Script([], [], failWith: new EngineSqlException(
            "Cannot open backup device '/pool/build/x/backup.bak'. Operating system error 13(Permission denied).", [3201, 3013]));

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.Contains("Permission denied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_3241_from_headeronly_after_the_label_was_read_is_a_version_mismatch_naming_the_engine()
    {
        var session = new Script([], [], failWith: new EngineSqlException("The media family on device is incorrectly formed.", [3241, 3013]), failOn: "HEADERONLY");

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.VersionMismatch, ex.Code);
        Assert.Contains("2022 (16.0.4325)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LABELONLY")]
    [InlineData("FILELISTONLY")]
    public async Task A_3241_from_any_other_statement_is_still_not_a_backup(string statement)
    {
        var session = new Script([Header(1, 1, "Database")], TwoFiles, failWith: new EngineSqlException("The media family on device is incorrectly formed.", [3241, 3013]), failOn: statement);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.NotABackup, ex.Code);
    }

    [Fact]
    public void A_3169_from_restore_is_a_version_mismatch_naming_both_versions()
    {
        var error = new EngineSqlException(
            "The database was backed up on a server running version 17.00.1000. That version is incompatible with this server, which is running version 16.00.4325.",
            [3169, 3013]);

        var mapped = BackupInspector.MapError(error, "/pool/build/x/backup.bak", BackupStage.Restore, Engine2022);

        Assert.Equal(ErrorCodes.VersionMismatch, mapped.Code);
        Assert.Contains("2025 (17.0.1000)", mapped.Message, StringComparison.Ordinal);
        Assert.Contains("2022 (16.0.4325)", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_3169_without_versions_in_the_message_names_the_engine_version_the_caller_knows()
    {
        var mapped = BackupInspector.MapError(new EngineSqlException("incompatible", [3169]), "/pool/build/x/backup.bak", BackupStage.Restore, Engine2022);

        Assert.Equal(ErrorCodes.VersionMismatch, mapped.Code);
        Assert.Contains("2022 (16.0.4325)", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_restore_errors_keep_the_restore_failed_wording()
    {
        var mapped = BackupInspector.MapError(new EngineSqlException("There is not enough space on the disk.", [3202, 3013]), "/x/backup.bak", BackupStage.Restore, Engine2022);

        Assert.Equal(ErrorCodes.OperationFailed, mapped.Code);
        Assert.StartsWith("The restore failed: ", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Any_other_engine_error_keeps_its_message_and_is_a_plain_failure()
    {
        var session = new Script([], [], failWith: new EngineSqlException("Operating system error 5(Access is denied).", [3201]));

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Inspect(session));

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.Contains("Access is denied", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, "2008")]
    [InlineData(13, "2016")]
    [InlineData(15, "2019")]
    [InlineData(16, "2022")]
    [InlineData(17, "2025")]
    [InlineData(18, "18")]
    public void Version_numbers_have_names(int major, string year)
    {
        Assert.Equal(year, SqlServerVersions.Year(major));
    }
}
