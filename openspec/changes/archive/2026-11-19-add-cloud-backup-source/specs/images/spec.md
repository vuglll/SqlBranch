## ADDED Requirements

### Requirement: Build an image from a backup URL
`sqlbranch image build <name> --from-bak <url>` SHALL accept an `https://` Azure Blob Storage URL or an `s3://` URL, have the engine restore the full backup directly from that URL, and then seal the image as for a backup file. It MUST NOT download the backup to the client or copy it into the pool.

#### Scenario: Build from Azure Blob Storage
- **WHEN** the clone mode is `thin` or `copy`, `SQLBRANCH_AZURE_SAS_TOKEN` holds a read and list SAS token for container `backups`, and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak --format json`
- **THEN** the command exits with code 0, `cloneMode` is the pool's clone mode, and `sqlbranch image list` shows `shop`
- **AND** no file named `shop.bak` exists in the pool or in the client's working directory

#### Scenario: Build from S3-compatible storage
- **WHEN** the clone mode is `thin` or `copy`, a local S3-compatible server with a certificate the engine trusts holds `nightly/shop.bak` in bucket `backups`, `SQLBRANCH_S3_ACCESS_KEY_ID` and `SQLBRANCH_S3_SECRET_ACCESS_KEY` are set, and the user runs `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/nightly/shop.bak`
- **THEN** the command exits with code 0 and `sqlbranch image list` shows `shop`

#### Scenario: S3 region
- **WHEN** the clone mode is `copy` and the user runs `sqlbranch image build shop --from-bak s3://acme-db.s3.eu-west-1.amazonaws.com/shop.bak --s3-region eu-west-1` (tested with a fake connector)
- **THEN** the restore statement the engine runs names region `eu-west-1`

#### Scenario: Post-build scripts on a URL build
- **WHEN** the clone mode is `thin` or `copy` and the user runs `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak --post-script mask.sql` with valid S3 secrets
- **THEN** the command exits with code 0 and `mask.sql` ran before the image was sealed

#### Scenario: Striped backup at a URL
- **WHEN** the clone mode is `copy` and `https://acme.blob.core.windows.net/backups/shop_1.bak` is the first of two stripes of one backup
- **THEN** `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop_1.bak` exits with code 1, `error.code` is "unsupported_backup_type", and the message says only a backup in one file is supported

### Requirement: Backup URLs are checked first
A `--from-bak` value that starts with `http://`, `https://` or `s3://` (any case) SHALL be treated as a URL. `image build` MUST exit with code 2 and `error.code` "usage_error" before running any `docker` command or SQL when the URL uses `http://`, has a query string or fragment, has no host, has no container or bucket and file name, ends in `/`, is longer than 259 characters, or gives a credential name (as design D4 derives it from the URL) longer than 128 characters.

#### Scenario: Plain HTTP
- **WHEN** the user runs `sqlbranch image build shop --from-bak http://acme.blob.core.windows.net/backups/shop.bak`
- **THEN** the command exits with code 2, says only `https://` and `s3://` URLs are supported, and no `docker` command ran

#### Scenario: Pasted SAS URL
- **WHEN** the user runs `sqlbranch image build shop --from-bak "https://acme.blob.core.windows.net/backups/shop.bak?sv=2024-11-04&sig=AbC123secret"`
- **THEN** the command exits with code 2 and names `SQLBRANCH_AZURE_SAS_TOKEN`
- **AND** neither standard output nor standard error contains `AbC123secret`

#### Scenario: No file name
- **WHEN** the user runs `sqlbranch image build shop --from-bak s3://s3.us-east-1.amazonaws.com/backups/`
- **THEN** the command exits with code 2 and says the URL must name a backup file

#### Scenario: No container
- **WHEN** the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/shop.bak`
- **THEN** the command exits with code 2 and says the URL must name a container and a file

#### Scenario: URL too long
- **WHEN** the user runs `sqlbranch image build shop --from-bak` with an `s3://` URL of 260 characters
- **THEN** the command exits with code 2 and names the 259-character limit

#### Scenario: Credential name too long
- **WHEN** the user runs `sqlbranch image build shop --from-bak` with an `s3://` URL of 200 characters whose folder part is 130 characters
- **THEN** the command exits with code 2, names the 128-character credential name limit, and no `docker` command ran

### Requirement: S3 region option is checked
`--s3-region <region>` SHALL set the region for an `s3://` URL, with `us-east-1` as the default. `image build` MUST exit with code 2 and `error.code` "usage_error" before any action when `--s3-region` is given without an `s3://` URL or its value is not 1 to 32 characters of lowercase letters, digits and hyphens.

#### Scenario: Region without an S3 URL
- **WHEN** the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak --s3-region eu-west-1`
- **THEN** the command exits with code 2, names `--s3-region`, and no `docker` command ran

#### Scenario: Malformed region
- **WHEN** the user runs `sqlbranch image build shop --from-bak s3://s3.amazonaws.com/backups/shop.bak --s3-region "EU West"`
- **THEN** the command exits with code 2 and names `--s3-region`

### Requirement: S3 URLs need SQL Server 2022 or later
A build from an `s3://` URL on an engine older than SQL Server 2022 MUST exit with code 1 and `error.code` "unsupported_source" before creating any credential, naming the engine version and saying how to choose a SQL Server 2022 or later engine image.

#### Scenario: SQL Server 2019 engine
- **WHEN** the engine runs SQL Server 2019 (tested with a fake connector) and the user runs `sqlbranch image build shop --from-bak s3://s3.amazonaws.com/backups/shop.bak` with valid S3 secrets
- **THEN** the command exits with code 1, `error.code` is "unsupported_source", the message names SQL Server 2019 and `engineImage`, and no credential was created

### Requirement: Long SAS tokens need SQL Server 2022 or later
A build from an `https://` URL with a SAS token longer than 256 characters on an engine older than SQL Server 2022 MUST exit with code 2 and `error.code` "config_error" before creating any credential, naming `SQLBRANCH_AZURE_SAS_TOKEN`, the 256-character limit and the engine version, without showing the token.

#### Scenario: 300-character token on SQL Server 2019
- **WHEN** the engine runs SQL Server 2019 (tested with a fake connector), `SQLBRANCH_AZURE_SAS_TOKEN` has 300 characters, and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak`
- **THEN** the command exits with code 2, names `SQLBRANCH_AZURE_SAS_TOKEN` and 256, and no credential was created

### Requirement: Unreachable backup URL
When the engine cannot reach, authenticate to, verify the certificate of, or find the backup at the URL, `image build` MUST exit with code 1 and `error.code` "backup_unreachable" with the URL (without secrets) and the engine's reason, and leave the pool and the engine's credentials as they were before the build.

#### Scenario: Wrong S3 secret
- **WHEN** the clone mode is `thin` or `copy` and the user runs `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak` with a wrong `SQLBRANCH_S3_SECRET_ACCESS_KEY`
- **THEN** the command exits with code 1 and `error.code` is "backup_unreachable"
- **AND** `sqlbranch image list` does not show `shop`, `sqlbranch status` exits with code 0, and the engine has no credential named `s3://172.17.0.5:9000/backups`

#### Scenario: Missing blob
- **WHEN** the clone mode is `copy` and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/nope.bak` with a valid SAS token (tested with a fake connector that returns the engine's "cannot open backup device" error)
- **THEN** the command exits with code 1, `error.code` is "backup_unreachable", and the message names `https://acme.blob.core.windows.net/backups/nope.bak`

#### Scenario: Untrusted certificate
- **WHEN** the clone mode is `copy` and the local S3-compatible server uses a certificate the engine does not trust
- **THEN** `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak` exits with code 1, `error.code` is "backup_unreachable", and the message says the engine does not trust the endpoint's certificate

### Requirement: Build credential lasts only for the restore
A URL build SHALL give its storage secret to the engine as one server credential named after the URL's folder, and MUST drop that credential right after the restore, before any post-build script runs. If the build fails or is cancelled at any step, it MUST drop the credential before it exits. When a drop fails, the build SHALL keep its record of the credential so that `status` and the next build find it.

#### Scenario: Credential after a successful build
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/nightly/shop.bak` exits with code 0
- **THEN** `sys.credentials` on the engine has no row named `s3://172.17.0.5:9000/backups/nightly`

#### Scenario: Post-build script cannot use the credential
- **WHEN** the clone mode is `copy` and `probe.sql` selects the rows of `sys.credentials` into table `seen` during `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak --post-script probe.sql`
- **THEN** the build exits with code 0 and table `seen` in a branch of `shop` has no row naming `s3://172.17.0.5:9000/backups`

#### Scenario: Restore fails halfway
- **WHEN** the clone mode is `copy` and the restore from `https://acme.blob.core.windows.net/backups/shop.bak` fails because the pool runs out of space
- **THEN** the command exits with code 1 and `sys.credentials` has no row named `https://acme.blob.core.windows.net/backups`

#### Scenario: Credential drop fails
- **WHEN** the restore succeeded and dropping the credential fails (tested with a fake connector)
- **THEN** the command exits with code 1, no post-build script ran, `sqlbranch image list` does not show the image, and the message names the credential and says the next `image build` removes it
- **AND** `sqlbranch status` exits with code 1 and lists the credential as a build leftover
- **AND** after the next `sqlbranch image build crm --from-bak crm.bak` exits with code 0, the credential no longer exists and `sqlbranch status` exits with code 0

### Requirement: Existing credentials are not replaced
A URL build MUST NOT alter or drop a credential it did not create. When the engine still has a credential with the name the build needs after the build removed a leftover of that name (see "Leftover build credentials are removed"), the build MUST exit with code 1 and `error.code` "credential_conflict", naming the credential, before any restore.

#### Scenario: Credential created by hand
- **WHEN** the clone mode is `copy`, a user created credential `https://acme.blob.core.windows.net/backups` on the engine, and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak` with a SAS token
- **THEN** the command exits with code 1, `error.code` is "credential_conflict", the message names the credential, and the credential's secret is unchanged

### Requirement: Builds sharing a credential name
When two URL builds need a credential with the same name at the same time, the second SHALL wait until the first has dropped it, say on standard error that it is waiting, and then go on. Each build SHALL use its own secret.

#### Scenario: Two images from one container
- **WHEN** the clone mode is `thin` or `copy` and two processes run `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak` and `sqlbranch image build crm --from-bak s3://172.17.0.5:9000/backups/crm.bak` at the same moment with valid S3 secrets
- **THEN** both exit with code 0 and `sqlbranch image list` shows both images

#### Scenario: Waiting build is cancelled
- **WHEN** the clone mode is `copy`, a build holds credential `s3://172.17.0.5:9000/backups`, a second `sqlbranch image build crm --from-bak s3://172.17.0.5:9000/backups/crm.bak` is waiting for it, and the second process receives Ctrl+C after 5 seconds
- **THEN** the second command exits within 5 seconds of the cancel with a non-zero code, leaves no image `crm`, no build folder and no credential of its own, and the first build exits with code 0

### Requirement: Leftover build credentials are removed
After the name check, each `image build` SHALL drop every credential that a SqlBranch build recorded after creating it, when that build is not running and the credential's id and creation time both match the record. It MUST NOT touch any other credential. A record made before the credential existed SHALL be discarded without dropping anything. If a drop fails, the build SHALL warn naming the credential and continue.

#### Scenario: Credential left by a killed build
- **WHEN** the client running `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak` was killed during the restore, leaving credential `s3://172.17.0.5:9000/backups`, a user created credential `app_cred`, and the user runs `sqlbranch image build crm --from-bak crm.bak`
- **THEN** the command exits with code 0
- **AND** credential `s3://172.17.0.5:9000/backups` no longer exists and credential `app_cred` still exists

#### Scenario: Another URL build is running
- **WHEN** a URL build in another process holds credential `s3://172.17.0.5:9000/backups` and the user runs `sqlbranch image build crm --from-bak crm.bak`
- **THEN** both builds exit with code 0

#### Scenario: Stale record and a credential made by hand
- **WHEN** a finished build left a record of credential `s3://172.17.0.5:9000/backups`, that credential was later dropped, a user then created a credential with the same name by hand, and the user runs `sqlbranch image build crm --from-bak crm.bak`
- **THEN** the command exits with code 0 and the hand-made credential still exists with its secret unchanged

#### Scenario: Stale record and a running build with the same name
- **WHEN** a finished build left a record of credential `s3://172.17.0.5:9000/backups`, a URL build in another process is restoring with a new credential of that name, and the user runs `sqlbranch image build crm --from-bak crm.bak`
- **THEN** both builds exit with code 0

#### Scenario: Killed before the credential existed
- **WHEN** a URL build was killed after recording its intent and before creating credential `s3://172.17.0.5:9000/backups`, a user created that credential by hand, and the user runs `sqlbranch image build crm --from-bak crm.bak`
- **THEN** the command exits with code 0, the hand-made credential still exists, and the record is gone

## MODIFIED Requirements

### Requirement: Image manifest
Each image SHALL record its source type (`backup` or `scripts`), source database name, engine version, each database file with logical name and size, original recovery model, build time, database DDL trigger names, Service Broker state, and post-build script names and SHA-256 in run order. A backup image SHALL record the backup file name, the backup URL or null, and the SHA-256 of a local backup file or null for a URL; a script image each source script's name and SHA-256 in run order.

#### Scenario: Manifest after a build
- **WHEN** an image is built from a backup of database `Shop` that has one data file and one log file
- **THEN** the build exits with code 0
- **AND** `sqlbranch image list --format json` shows for that image the source database `Shop`, two files with logical names and sizes, and a build time

#### Scenario: Manifest of a script image
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image build shopdev --from-scripts db` runs `001_schema.sql`, `002_seed.sql` and `010_more.sql` and exits with code 0
- **THEN** `sqlbranch image list --format json` shows `shopdev` with `sourceType` "scripts", `sourceDatabase` "shopdev", three `scripts` entries with `name` and `sha256` in that order, and `backupFile` and `backupSha256` null

#### Scenario: Manifest with post-build scripts
- **WHEN** `sqlbranch image build shop --from-bak shop.bak --post-script mask.sql` exits with code 0
- **THEN** `sqlbranch image list --format json` shows `shop` with `sourceType` "backup", `backupFile` "shop.bak", its `backupSha256`, `backupUrl` null, and one `postScripts` entry with `name` "mask.sql" and its `sha256`

#### Scenario: Manifest of a URL build
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/nightly/shop.bak` exits with code 0
- **THEN** `sqlbranch image list --format json` shows `shop` with `sourceType` "backup", `backupFile` "shop.bak", `backupUrl` "s3://172.17.0.5:9000/backups/nightly/shop.bak" and `backupSha256` null
- **AND** the image's `manifest.json` contains neither the S3 access key ID nor the secret

### Requirement: Backup set selection
When a backup file holds several backup sets, the build SHALL use set 1 unless `--backup-set <n>` names another, and SHALL accept only a full database backup. A build from a URL SHALL always use set 1 and MUST exit with code 2 and `error.code` "usage_error" before any action when `--backup-set` names another set.

#### Scenario: Differential backup chosen
- **WHEN** the chosen backup set is a differential backup
- **THEN** the command exits with code 1, `error.code` is "unsupported_backup_type", and the message says only full database backups are supported

#### Scenario: Backup set with a URL
- **WHEN** the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak --backup-set 2`
- **THEN** the command exits with code 2, names `--backup-set`, and no `docker` command ran

### Requirement: Backup from a newer SQL Server version
A build from a backup made by a newer SQL Server version than the engine MUST fail with a message naming both versions. The message SHALL also say that a backup from Azure SQL Managed Instance restores only to the SQL Server version of the instance's update policy, and that backups from an Always-up-to-date instance cannot be restored to SQL Server.

#### Scenario: Version mismatch
- **WHEN** the engine is SQL Server 2022 and the backup was made by SQL Server 2025
- **THEN** the command exits with code 1
- **AND** the message names both versions and says how to choose a newer engine image

#### Scenario: Managed Instance hint
- **WHEN** the engine is SQL Server 2022 and the engine refuses the backup at `https://acme.blob.core.windows.net/backups/mi.bak` as made by a newer version (tested with a fake connector returning error 3169)
- **THEN** the command exits with code 1, `error.code` is "version_mismatch", and the message names the Managed Instance update policy and Always-up-to-date
