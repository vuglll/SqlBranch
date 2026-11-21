# Proposal

## Why

Most teams keep their backups in Azure Blob Storage or S3, not on a laptop.
Today `image build --from-bak` takes only a local file, so a user downloads a
large backup first and SqlBranch then copies it again into the pool: twice the
disk and twice the wait. SQL Server can restore straight from a URL, so the
engine can read the backup where it already is. This is the first step of the
nightly recipe in `docs/roadmap.md` (F1 with F3 and F4).

## What Changes

- `sqlbranch image build <name> --from-bak <url>` accepts an Azure Blob Storage
  URL (`https://<account>.blob.core.windows.net/<container>/<path>.bak`) or an
  S3 URL (`s3://<endpoint>[:<port>]/<bucket>/<path>.bak` or
  `s3://<bucket>.<endpoint>/<path>.bak`). The engine restores from the URL.
  Nothing is downloaded to the client or staged in the pool.
- Storage secrets come only from environment variables:
  `SQLBRANCH_AZURE_SAS_TOKEN`, or `SQLBRANCH_S3_ACCESS_KEY_ID` and
  `SQLBRANCH_S3_SECRET_ACCESS_KEY`. A new `--s3-region` flag sets the S3 region
  (default `us-east-1`, as in SQL Server).
- The secret is given to the engine as a server credential for the one build
  and dropped right after the restore, before any post-build script runs, and
  on every failure. A credential left by a killed build or a failed drop is
  listed by `status` and removed by the next build, which drops only a
  credential whose id and creation time match what SqlBranch recorded.
- The engine trusts the public CAs of Azure Blob Storage and Amazon S3. If
  spike 1.1 shows a stock engine already does, this needs no code; otherwise
  `init` copies those roots into the engine's CA folder.
- Storage secrets never appear in output, manifests, logs, errors, `docker`
  command lines or container settings. A URL with a query string (a pasted SAS
  URL) is refused with exit 2.
- Bad input exits 2 before any `docker` command: `http://` URLs, a URL
  without a container or bucket and file, a missing secret, an S3 key with
  `:`, a bad region, `--backup-set` other than 1 with a URL, `--s3-region`
  without an S3 URL, a URL over 259 characters, a credential name over 128
  characters. A SAS token over 256 characters on an engine older than SQL
  Server 2022 also exits 2, after the engine version is read.
- The manifest of a URL build records `backupUrl` (no query string),
  `backupFile` (the last path segment) and a null `backupSha256`, because the
  backup is never read whole by SqlBranch.
- A version error on any backup build also says that a Managed Instance
  backup restores only to the SQL Server version of the instance's update
  policy, and that Always-up-to-date instances need the BACPAC route (F2).
- New error codes `backup_unreachable`, `credential_conflict` and
  `unsupported_source` (all exit 1). JSON gains `backupUrl`; `schemaVersion`
  stays 1.

Targets release 0.3 in `docs/roadmap.md`, feature queue row F1.

**Prerequisite.** F1 needs "0.1 archived". `add-branching-core` was archived on
2026-11-17, so it is met. The 0.1 release is still held for its gate (x86-64
timings and G2, the Linux XFS run, the clean-Linux quick start, the Windows
check); this change does not depend on any gate result, because it changes
only where the restore reads the backup from. No spike gate in
`docs/research/` applies to it.

**Platform facts** (checked 2026-11-18, recorded with sources in
`docs/research/2026-11-18-cloud-backup-source-facts.md`):

- SQL Server on Linux and in containers restores from Azure Blob Storage only
  from block blobs with a SAS credential
  (https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/sql-server-backup-to-url).
- The S3 connector needs SQL Server 2022 or later, HTTPS, and a CA the engine
  trusts; on Linux extra CAs go in `/var/opt/mssql/security/ca-certificates`
  before SQL Server starts
  (https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/sql-server-backup-to-url-s3-compatible-object-storage).
- `RESTORE ... FROM URL` does not support the `FILE` option (same two pages),
  so a URL build uses backup set 1 only.
- Managed Instance copy-only backups restore to SQL Server 2022 only from the
  SQL Server 2022 update policy and to SQL Server 2025 only from the 2025
  policy; Always-up-to-date backups cannot be restored to SQL Server; databases
  with service-managed TDE keys cannot be restored to SQL Server
  (https://learn.microsoft.com/en-us/azure/azure-sql/managed-instance/update-policy,
  https://learn.microsoft.com/en-us/azure/azure-sql/managed-instance/restore-database-to-sql-server).
- RDS `rds_backup_database` writes a native backup to S3, optionally split
  into up to 10 files or encrypted client-side with KMS
  (https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/SQLServer.Procedural.Importing.Native.Using.html).

**Not verified, and what settles it** (details in design.md):

- Whether a stock engine container trusts AWS S3's public CA. Spike task 1.1
  with a real bucket. If it does not, task 4.6 adds the CA step at engine
  creation (human decision, 2026-11-18); the engine requirement holds either
  way.
- Whether a SAS credential named after a folder below the container is found.
  Spike task 1.1; the design falls back to the documented container-level
  name.
- Whether Azurite over HTTPS works as a restore source in CI. Spike task 1.2;
  without it, Blob is covered by unit tests and an opt-in test against a real
  storage account.
- Whether Managed Instance backups always carry the `XTP` memory-optimized
  filegroup, which SqlBranch rejects today. Spike task 1.3 needs a real
  instance. If they do, F1 ships for SQL Server on VMs and RDS only and
  Managed Instance goes in a later change (human decision, 2026-11-18).
- Whether Managed Instance refuses copy-only backups of service-managed-TDE
  databases (the draft's claim). Not needed by the design: such a backup fails
  the restore either way.
- Google Cloud SQL exports and Google Cloud Storage's S3 endpoint. Left out.

**Out of scope:**

- Managed Instance, if spike 1.3 finds `XTP` in every backup (see above).
- Striped backups (human decision, 2026-11-18) (several URLs, RDS `@number_of_files` > 1, large Managed
  Instance backups over about 195 GB per blob). They fail with
  `unsupported_backup_type`, as striped file backups do today.
- Storage-account-key (page blob) credentials, managed identity, Microsoft
  Entra ID, AWS session tokens, instance profiles and other credential sources.
- Custom CA certificates for private S3 endpoints (unless spike 1.1 forces a
  CA step for AWS itself).
- Google Cloud SQL and Google Cloud Storage.
- Secrets given as flags, in config files, in a token file, or in a pasted
  SAS URL (human decision, 2026-11-18).
- Hashing the remote backup, and change detection (F4 `add-image-refresh`).
- `image export`/`import` to cloud URLs (F3).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `images`: adds building from a backup URL, URL checks, the long-SAS-token check, the build credential's lifetime and leftover removal, and URL errors; changes "Image manifest", "Backup set selection" and "Backup from a newer SQL Server version".
- `engine`: adds trust in the public storage CAs; changes "Status reports mismatches" to list a leftover build credential.
- `configuration`: adds the storage secret sources and where storage secrets may appear; changes "Failed commands are redacted".

## Impact

- `SqlBranch.Core/Images`: a URL source path in `ImageBuilder`, URL parsing,
  the credential lifetime, error mapping in `BackupInspector`, manifest field.
- `SqlBranch.Core/Engine`: `status` reads credential markers; `init` may copy
  CA roots (only if spike 1.1 requires it).
- `SqlBranch.Core/Secrets`: storage secrets registered with the redactor.
- `SqlBranch.Cli`: `--from-bak` accepts URLs, new `--s3-region`.
- Tests: unit tests on the fake runtime and connector; integration tests with
  a local MinIO container over HTTPS; opt-in tests against real Azure and AWS.
- Docs: `docs/images.md`, `docs/configuration.md`, `docs/cli-output.md`, the
  generated agent skill.
- No new NuGet dependency: the engine does all storage I/O.
