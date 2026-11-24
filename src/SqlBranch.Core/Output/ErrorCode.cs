namespace SqlBranch.Core.Output;

/// <summary>A stable, machine-readable error code and the exit code it maps to.</summary>
public sealed record ErrorCode(string Code, ExitCode ExitCode)
{
    /// <inheritdoc />
    public override string ToString() => Code;
}

/// <summary>
/// Every error code SqlBranch can report. The list is documented in
/// docs/cli-output.md, and a test keeps the two in step.
/// </summary>
public static class ErrorCodes
{
    /// <summary>The command line could not be parsed or is incomplete.</summary>
    public static readonly ErrorCode UsageError = new("usage_error", ExitCode.UsageError);

    /// <summary>A configuration file, environment variable or setting is invalid.</summary>
    public static readonly ErrorCode ConfigError = new("config_error", ExitCode.UsageError);

    /// <summary>A name breaks the naming rules or is reserved.</summary>
    public static readonly ErrorCode InvalidName = new("invalid_name", ExitCode.UsageError);

    /// <summary>The Docker command is missing or its daemon is not reachable.</summary>
    public static readonly ErrorCode DockerUnavailable = new("docker_unavailable", ExitCode.EnvironmentNotReady);

    /// <summary>No SqlBranch engine or pool exists yet.</summary>
    public static readonly ErrorCode NotInitialized = new("not_initialized", ExitCode.EnvironmentNotReady);

    /// <summary>The engine did not accept connections in time.</summary>
    public static readonly ErrorCode EngineNotReady = new("engine_not_ready", ExitCode.EnvironmentNotReady);

    /// <summary>The engine container is not running.</summary>
    public static readonly ErrorCode EngineStopped = new("engine_stopped", ExitCode.EnvironmentNotReady);

    /// <summary>SqlPackage could not be found or its version could not be read.</summary>
    public static readonly ErrorCode SqlPackageMissing = new("sqlpackage_missing", ExitCode.EnvironmentNotReady);

    /// <summary>A <c>doctor</c> check failed.</summary>
    public static readonly ErrorCode CheckFailed = new("check_failed", ExitCode.EnvironmentNotReady);

    /// <summary>The managed pool is not mounted and could not be remounted.</summary>
    public static readonly ErrorCode PoolNotMounted = new("pool_not_mounted", ExitCode.EnvironmentNotReady);

    /// <summary>The managed pool could not be created or mounted and poolBackend forbids the fallback.</summary>
    public static readonly ErrorCode PoolUnavailable = new("pool_unavailable", ExitCode.EnvironmentNotReady);

    /// <summary>The container host has too little free space for the managed pool.</summary>
    public static readonly ErrorCode HostDiskFull = new("host_disk_full", ExitCode.EnvironmentNotReady);

    /// <summary>The clone mode is copy and requireThin is on.</summary>
    public static readonly ErrorCode ThinRequired = new("thin_required", ExitCode.EnvironmentNotReady);

    /// <summary>A container with SqlBranch's name exists that SqlBranch did not create.</summary>
    public static readonly ErrorCode ForeignContainer = new("foreign_container", ExitCode.EnvironmentNotReady);

    /// <summary>A first init failed and was undone.</summary>
    public static readonly ErrorCode InitFailed = new("init_failed", ExitCode.OperationFailed);

    /// <summary>The pool has too little free space for the operation.</summary>
    public static readonly ErrorCode PoolFull = new("pool_full", ExitCode.OperationFailed);

    public static readonly ErrorCode TempFull = new("temp_full", ExitCode.OperationFailed);
    public static readonly ErrorCode BacpacInvalid = new("bacpac_invalid", ExitCode.OperationFailed);

    /// <summary>The named image does not exist.</summary>
    public static readonly ErrorCode ImageNotFound = new("image_not_found", ExitCode.OperationFailed);

    /// <summary>An image with that name already exists.</summary>
    public static readonly ErrorCode ImageExists = new("image_exists", ExitCode.OperationFailed);

    /// <summary>The image still has branches and --force was not given.</summary>
    public static readonly ErrorCode ImageInUse = new("image_in_use", ExitCode.OperationFailed);

    /// <summary>The named branch does not exist.</summary>
    public static readonly ErrorCode BranchNotFound = new("branch_not_found", ExitCode.OperationFailed);

    /// <summary>A branch or database with that name already exists.</summary>
    public static readonly ErrorCode BranchExists = new("branch_exists", ExitCode.OperationFailed);

    /// <summary>The file is not a backup the engine can read.</summary>
    public static readonly ErrorCode NotABackup = new("not_a_backup", ExitCode.OperationFailed);

    /// <summary>The backup is not a full database backup.</summary>
    public static readonly ErrorCode UnsupportedBackupType = new("unsupported_backup_type", ExitCode.OperationFailed);

    public static readonly ErrorCode GuestDatabase = new("guest_database", ExitCode.OperationFailed);
    public static readonly ErrorCode ScriptFailed = new("script_failed", ExitCode.OperationFailed);
    public static readonly ErrorCode DdlTrigger = new("ddl_trigger", ExitCode.OperationFailed);

    /// <summary>The database uses a feature images do not support.</summary>
    public static readonly ErrorCode UnsupportedFeature = new("unsupported_feature", ExitCode.OperationFailed);

    /// <summary>The backup was made by a newer SQL Server version than the engine.</summary>
    public static readonly ErrorCode VersionMismatch = new("version_mismatch", ExitCode.OperationFailed);

    /// <summary>The engine could not reach, authenticate to or find the backup at its URL.</summary>
    public static readonly ErrorCode BackupUnreachable = new("backup_unreachable", ExitCode.OperationFailed);

    /// <summary>The engine already has a credential with the name a URL build needs, and it is not SqlBranch's.</summary>
    public static readonly ErrorCode CredentialConflict = new("credential_conflict", ExitCode.OperationFailed);

    /// <summary>The engine cannot read this kind of backup URL (S3 needs SQL Server 2022).</summary>
    public static readonly ErrorCode UnsupportedSource = new("unsupported_source", ExitCode.OperationFailed);

    /// <summary>An export target file or object already exists and --force was not given, or appeared during the export.</summary>
    public static readonly ErrorCode DestinationExists = new("destination_exists", ExitCode.OperationFailed);

    /// <summary>The source is not a SqlBranch image archive, or holds an entry or manifest import will not accept.</summary>
    public static readonly ErrorCode ArchiveInvalid = new("archive_invalid", ExitCode.OperationFailed);

    /// <summary>An archive's checksum or size differs, its checksums are missing, or its stream ended early.</summary>
    public static readonly ErrorCode ArchiveCorrupt = new("archive_corrupt", ExitCode.OperationFailed);

    /// <summary>The archive storage of an export or import could not be reached, refused the secret, has an untrusted certificate, or has no object at the URL.</summary>
    public static readonly ErrorCode StorageUnreachable = new("storage_unreachable", ExitCode.OperationFailed);

    /// <summary>status found state that does not match.</summary>
    public static readonly ErrorCode Mismatch = new("mismatch", ExitCode.OperationFailed);

    public static readonly ErrorCode SkillExists = new("skill_exists", ExitCode.OperationFailed);

    /// <summary>A command waited <c>publishWait</c> for running image or branch operations and gave up, or met one it must not wait for.</summary>
    public static readonly ErrorCode CatalogBusy = new("catalog_busy", ExitCode.OperationFailed);

    /// <summary>A pool upgrade did not finish, or the command needs pool format 2. The message names <c>sqlbranch pool upgrade --yes</c>.</summary>
    public static readonly ErrorCode PoolUpgradeRequired = new("pool_upgrade_required", ExitCode.EnvironmentNotReady);

    /// <summary>The operation failed for a reason with no more specific code.</summary>
    public static readonly ErrorCode OperationFailed = new("operation_failed", ExitCode.OperationFailed);

    /// <summary>All codes, in documentation order.</summary>
    public static IReadOnlyList<ErrorCode> All { get; } =
    [
        UsageError, ConfigError, InvalidName,
        DockerUnavailable, NotInitialized, CheckFailed, SqlPackageMissing, EngineNotReady, EngineStopped, PoolNotMounted, PoolUnavailable,
        HostDiskFull, ThinRequired, ForeignContainer,
        InitFailed, PoolFull, TempFull, BacpacInvalid, ImageNotFound, ImageExists, ImageInUse, BranchNotFound, BranchExists,
        NotABackup, UnsupportedBackupType, GuestDatabase, ScriptFailed, DdlTrigger, UnsupportedFeature, VersionMismatch, BackupUnreachable, CredentialConflict, UnsupportedSource, DestinationExists, ArchiveInvalid, ArchiveCorrupt, StorageUnreachable, Mismatch, SkillExists, CatalogBusy, PoolUpgradeRequired, OperationFailed,
    ];
}
