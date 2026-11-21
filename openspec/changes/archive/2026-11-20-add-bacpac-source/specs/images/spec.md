## MODIFIED Requirements

### Requirement: Exactly one image source
When the BACPAC source option is absent, the following rule applies. An image build MUST be given exactly one of `--from-bak` and `--from-scripts`, and MUST exit with code 2 and `error.code` "usage_error" before any action otherwise.

#### Scenario: Both sources given
- **WHEN** the user runs `sqlbranch image build shop --from-bak shop.bak --from-scripts db`
- **THEN** the command exits with code 2 and says to choose one source

#### Scenario: No source given
- **WHEN** the user runs `sqlbranch image build shop`
- **THEN** the command exits with code 2 and names `--from-bak` and `--from-scripts`

#### Scenario: Mixed BACPAC and script sources
- **WHEN** the user gives both `--from-bacpac shop.bacpac` and `--from-scripts db`
- **THEN** the command exits 2 with `usage_error` before Docker and names the three source choices

### Requirement: Image manifest
Backup and script images SHALL record source type (`backup` or `scripts`), source database, engine version, database file logical names and sizes, original recovery model, build time, database DDL trigger names, Service Broker state and ordered post-script names and SHA-256 hashes. Backup images SHALL record filename, URL or null, and local SHA-256 or null for a URL. Script images SHALL record each source script’s name and SHA-256 in run order.

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

#### Scenario: Logical provenance
- **WHEN** the clone mode is `copy` and image `shop` is built from `shop.bacpac`
- **THEN** the build exits 0 and list JSON shows `sourceType` "bacpac", `sourceDatabase` "shop", `bacpacFile` "shop.bacpac", `bacpacUrl` null and `backupFile` null

### Requirement: Post-build scripts
`--post-script <file>`, which may be given several times, SHALL run each file against the database, in the order given, after backup restore, source script execution or BACPAC import, and before the image is sealed.

#### Scenario: Masking script on a backup image
- **WHEN** the clone mode is `thin` or `copy`, `shop.bak` holds 1,000 customers with real email addresses, `mask.sql` sets every email to `user<Id>@example.test`, and the user runs `sqlbranch image build shop --from-bak shop.bak --post-script mask.sql`
- **THEN** the command exits with code 0
- **AND** in a branch created from `shop`, all 1,000 emails end in `@example.test`

#### Scenario: Post-build script fails
- **WHEN** the clone mode is `thin` or `copy`, `trim.sql` raises an error, and the user runs `sqlbranch image build shop --from-bak shop.bak --post-script mask.sql --post-script trim.sql`
- **THEN** the command exits with code 1, `error.code` is "script_failed" and the message names `trim.sql`
- **AND** `sqlbranch image list` does not show `shop` and `sqlbranch status` exits with code 0 and reports no mismatches

#### Scenario: Logical post-scripts
- **WHEN** the clone mode is `copy` and a BACPAC is built with `--post-script mask.sql`
- **THEN** the command runs the script after import with the existing post-build restrictions before sealing

## ADDED Requirements

### Requirement: Logical source syntax and configuration preflight
A logical build MUST receive exactly one occurrence of one of the three sources. Invalid names, conflicting or repeated sources, blank values, explicit `--backup-set`, unreadable post-scripts, invalid region options, unknown flags and invalid path, URL or storage configuration MUST exit 2 before Docker, SQL, SqlPackage, storage requests or file creation. The exit-2 checks SHALL inspect no package contents.

#### Scenario: Source or flag conflict
- **WHEN** each of `--from-bak x.bak`, `--from-scripts db`, a second `--from-bacpac x`, `--backup-set 1`, `--s3-region eu-west-1`, and `--post-script missing.sql` is tried with `image build shop --from-bacpac shop.bacpac`
- **THEN** each exits 2 with `usage_error` and zero Docker, SQL, SqlPackage or network calls

#### Scenario: Invalid name
- **WHEN** the image name is `Shop DB` and its local source exists
- **THEN** the command exits 2 with `invalid_name` before Docker

### Requirement: BACPAC files and URLs are checked first
A local BACPAC path MUST name a readable regular file. Remote URLs SHALL follow the approved image archive URL and S3 region rules. Unsupported schemes, userinfo, empty or malformed URLs, query or fragment delimiters and invalid paths MUST exit 2 with `usage_error` before Docker. Diagnostics MUST omit URL credentials, queries and fragments.

#### Scenario: Malformed local and remote inputs
- **WHEN** each of a missing path, directory, unreadable file, `http://host/c/x`, `ftp://host/c/x`, `s3:///c/x`, `s3://host/c/`, `https://host/x`, `https://user:secret@host/c/x`, `https://host/c/x?sig=secret` and `s3://host/c/x#secret` is passed to `--from-bacpac`
- **THEN** each exits 2 with `usage_error` before Docker or storage requests, and messages omit `secret`

### Requirement: Check the local engine and reserve the name first
After exit-2 checks the build MUST check engine and pool readiness, reserve the image name under the build locks and refuse an existing image before reading package contents, downloading an object or invoking SqlPackage. Name contention and an existing sealed image MUST exit 1 with `image_exists`, preserving the winner and all existing images.

#### Scenario: Existing image or concurrent build
- **WHEN** the clone mode is `thin` or `copy` and `shop` is already sealed or a live build holds the lock for `shop`
- **THEN** a logical build of `shop` exits 1 with `image_exists` with zero download requests or SqlPackage invocations, and the first image or build stays unchanged

### Requirement: Build from a logical package
A build SHALL accept a local BACPAC, an Azure Blob HTTPS URL or an S3 URL. On success it SHALL report clone mode and seal imported schema and data for branching. An inaccessible object or untrusted storage certificate MUST fail with exit 1 and `storage_unreachable` after readiness checks. The build MUST preserve the source.

#### Scenario: Sources produce branchable images
- **WHEN** the clone mode is `thin` or `copy` and a BACPAC with 17 rows in `dbo.Items` is built from a file, `https://acme.blob.core.windows.net/db/shop.bacpac`, or `s3://s3.amazonaws.com/db/shop.bacpac` using separate names
- **THEN** each exits 0, reports the pool's mode, preserves the source bytes, and a branch of each image returns 17 rows

#### Scenario: Storage request fails
- **WHEN** the clone mode is `copy` and storage returns 403 or fails certificate verification
- **THEN** the build exits 1 with `storage_unreachable`, names the query-free URL and publishes no image

### Requirement: Invalid BACPAC content
Once readiness and name checks pass, a text file, DACPAC, SqlBranch image archive, damaged or unrecognizable BACPAC MUST fail with exit 1 and `bacpac_invalid` for both local and downloaded input. The command MUST preserve user inputs, clean up its private files and publish no image.

#### Scenario: Invalid content after acquisition
- **WHEN** the clone mode is `thin` or `copy` and a readable local input or a successfully downloaded object contains a text file, a DACPAC, an F3 archive or a truncated BACPAC
- **THEN** the build exits 1 with `bacpac_invalid`, removes its private temporary files and leaves the source unchanged

### Requirement: Check import space separately from client scratch
Before creating the import database the build SHALL require at least the local or downloaded BACPAC's compressed byte length free in the engine pool. Insufficient pool space at this check or during import MUST exit 1 with `pool_full`. Exhausting client temporary storage during download or import MUST exit 1 with `temp_full`. Each failure SHALL identify the storage location and phase, clean up this build's files and publish no image.

#### Scenario: Lower bound before import
- **WHEN** the clone mode is `thin` or `copy`, the package is 5242880 bytes and the engine pool has 4194304 bytes free
- **THEN** the build exits 1 with `pool_full`, names 5242880 needed and 4194304 available, and creates no import database

#### Scenario: Growth exhausts the pool or scratch
- **WHEN** the clone mode is `copy`, an import passes the compressed-size check, then imported data fills the engine pool or client scratch independently runs out of space
- **THEN** the build exits 1 with `pool_full` for the former or `temp_full` for the latter, identifies import, and publishes no image

### Requirement: Refuse login-mapped package users
A logical build MUST refuse user-defined database users mapped to server logins, name the affected users and exit 1 with `bacpac_login_users` before deploying them or changing any server login. This check SHALL apply to local and downloaded packages. Built-in database principals and users without a server login SHALL not trigger this refusal.

#### Scenario: Two users depend on server logins
- **WHEN** the clone mode is `thin` or `copy` and the package defines `report_reader` and `sales_app` mapped to server logins
- **THEN** the build exits 1 with `bacpac_login_users`, names both users, publishes no image and leaves the engine's logins unchanged

### Requirement: Import compatibility and rollback
When an unsupported model object prevents deployment, the build MUST exit 1 with `unsupported_feature`, identify the import phase and safe feature or object name when known, and publish no image. Other SqlPackage deployment failures MUST exit 1 with `operation_failed` unless a more specific error above applies. The build MUST preserve existing images and input files.

#### Scenario: Incompatible model
- **WHEN** the clone mode is `thin` or `copy` and SqlPackage rejects an Azure-only external data source `SalesShard` on the local target
- **THEN** the build exits 1 with `unsupported_feature`, names import and the source when identifiable, and leaves no image or temporary database after successful cleanup

### Requirement: Logical images use the existing sealing pipeline
Logical builds SHALL apply post-scripts in order after import with the existing build login, guest, trigger, containment and database-state checks. An enabled trigger covering ownership change MUST fail before post-script login creation or ownership transfer. Sealing SHALL preserve trigger inventory and original recovery metadata, honor the recovery option, minimize the log and publish an immutable image only after required cleanup succeeds.

#### Scenario: Masking and recovery
- **WHEN** the clone mode is `thin` or `copy` and a package imported in FULL recovery is built with two post-scripts that first seed and then mask 17 email addresses
- **THEN** a branch contains 17 masked addresses, is in SIMPLE recovery by default or FULL with `--keep-recovery-model`, and the image records both scripts in order and a rebuilt log

#### Scenario: Ownership event trigger
- **WHEN** the clone mode is `thin` or `copy` and the imported database has enabled trigger `on_owner` covering ownership transfer and one post-script is requested
- **THEN** the build exits 1 with `ddl_trigger` naming `on_owner` before post-script login creation, ownership change or script execution, and publishes no image

### Requirement: Logical image provenance
The legacy source-type list SHALL extend to `bacpac`. BACPAC images SHALL retain common sealing and post-script metadata with native backup fields null. The manifest SHALL record the package basename, SHA-256, query-free URL if remote and image name as source database. Image list SHALL show the package basename.

#### Scenario: Local and remote manifests
- **WHEN** the clone mode is `copy` and file `db/shop.bacpac` and an S3 object with the same basename and bytes are built under different names
- **THEN** list JSON gives both `sourceType` "bacpac", `bacpacFile` "shop.bacpac" and the same `bacpacSha256`, with `bacpacUrl` null for the file and the query-free URL for S3
- **AND** list text identifies each source as `shop.bacpac`

### Requirement: Logical provenance survives archive format 1
A sealed logical image SHALL use archiveFormat 1 with additive manifest fields. Readers MUST accept older manifests without the new fields, tolerate unknown additive fields, and preserve logical source metadata and unknown additive manifest fields through an F3 export/import/export round trip. No package or connection secret SHALL accompany the image.

#### Scenario: New fields on an archive round trip
- **WHEN** the clone mode is `thin` or `copy` and an archiveFormat 1 image with `sourceType` "bacpac", `bacpacFile` "sales.bacpac" and unknown manifest field `futureTag` "v" is imported as `sales2` and exported again
- **THEN** the new archive has archiveFormat 1, the renamed image retains all three field values, and the archive contains sealed image files and manifest only

### Requirement: Build size and phase times
Before import the build SHALL report package bytes and warn that large operations can take hours. It SHALL report elapsed milliseconds per attempted phase, including the phase that failed. Text and versioned JSON SHALL identify size and phases. In JSON mode live notices MUST use standard error so standard output holds one result.

#### Scenario: Timings with a fake clock
- **WHEN** the clone mode is `copy`, a 2048-byte package takes 120 ms to import and 15 ms to seal, and JSON output is requested
- **THEN** standard error reports 2048 bytes and the hours warning before import, and the single JSON result includes `schemaVersion`, `sourceSizeBytes` 2048 and `phaseMilliseconds` with `import` 120 and `seal` 15

### Requirement: Protect temporary packages
A build SHALL store downloaded BACPACs in owner-only client scratch and remove only files owned by that operation on success, failure and Ctrl+C. Deletion MUST preserve source files, remote objects and other builds. If safe removal fails the build MUST exit 1 with `operation_failed`, withhold publication and identify the leftover without credentials.

#### Scenario: Cancellation during download
- **WHEN** the clone mode is `thin` or `copy` and Ctrl+C interrupts writing the private download for build A while build B is importing its own file
- **THEN** A exits 1 with `operation_failed`, its partial download is removed, A publishes no image, B's file and the remote object remain unchanged, and no other user had access to A's file
