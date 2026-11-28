# images Specification

## Purpose
Builds sealed, read-only database images from backup files so that branches
can be cloned from them, and lets users list and remove those images.

## Requirements

### Requirement: Build an image from a full backup
`sqlbranch image build <name> --from-bak <file>` SHALL restore a full database backup, seal the restored database files as a read-only image with the given name, and leave no restored database attached to the engine.

#### Scenario: Successful build
- **WHEN** the clone mode is `thin` or `copy` and the user runs `sqlbranch image build shop --from-bak shop.bak` with a valid full backup
- **THEN** the command exits with code 0
- **AND** `sqlbranch image list` shows `shop`
- **AND** no database from the build remains attached to the engine

### Requirement: Backup file is checked first
`image build` MUST exit with code 2 before copying anything when the `--from-bak` file does not exist or cannot be read, and with code 1 when the engine cannot read it as a backup.

#### Scenario: Missing backup file
- **WHEN** the user runs `sqlbranch image build shop --from-bak nope.bak` and `nope.bak` does not exist
- **THEN** the command exits with code 2 and names `nope.bak`
- **AND** the pool is unchanged

#### Scenario: File is not a backup
- **WHEN** the user runs `sqlbranch image build shop --from-bak notes.txt` and `notes.txt` is a text file
- **THEN** the command exits with code 1 and `error.code` is "not_a_backup"

### Requirement: Build checks space before staging
Before copying a backup into the engine, `image build` SHALL check that free pool space is at least the backup file size, and MUST exit with code 1 and `error.code` "pool_full" before copying when it is not.

#### Scenario: Not enough space to stage
- **WHEN** the clone mode is `thin` or `copy`, `shop.bak` is 20 GiB, and free pool space is 10 GiB
- **THEN** `sqlbranch image build shop --from-bak shop.bak` exits with code 1, `error.code` is "pool_full", and names 20 GiB needed and 10 GiB available
- **AND** the pool is unchanged

### Requirement: Build checks space before restoring
After reading the backup's file list and before restoring, `image build` SHALL check that free pool space is at least the restored files' total size, and MUST exit with code 1 and `error.code` "pool_full" when it is not, deleting the staged backup.

#### Scenario: Not enough space to restore
- **WHEN** the clone mode is `thin` or `copy`, `shop.bak` is 20 GiB, its restored files total 50 GiB, and free pool space is 40 GiB before staging
- **THEN** `sqlbranch image build shop --from-bak shop.bak` exits with code 1, `error.code` is "pool_full", and names 50 GiB needed and 20 GiB available
- **AND** the pool holds no copy of `shop.bak`

### Requirement: Staged backups are removed
After a build ends, successful or not, SqlBranch SHALL delete the copy of the backup it staged in the pool. A backup the user placed under the pool's import folder SHALL be kept.

#### Scenario: Staged copy after a build
- **WHEN** the clone mode is `thin` or `copy` and the user runs `sqlbranch image build shop --from-bak shop.bak` from a file outside the pool
- **THEN** the command exits with code 0
- **AND** the pool holds no copy of `shop.bak`

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

### Requirement: Recovery model of images
Every image build, from a backup or from scripts, SHALL switch the database to the SIMPLE recovery model before sealing unless `--keep-recovery-model` is given, and the manifest SHALL record both the original model and the model the image was sealed with. For a script image the original model is the one the database has after its scripts ran.

#### Scenario: Default build of a FULL-recovery database
- **WHEN** an image is built without flags from a backup of a database in FULL recovery
- **THEN** the build exits with code 0
- **AND** a branch from the image is in SIMPLE recovery
- **AND** the manifest shows original model FULL and sealed model SIMPLE

#### Scenario: Keeping the recovery model
- **WHEN** the same backup is built with `--keep-recovery-model`
- **THEN** the build exits with code 0
- **AND** a branch from the image is in FULL recovery

#### Scenario: Script that sets FULL recovery
- **WHEN** the clone mode is `thin` or `copy`, `001_schema.sql` runs `ALTER DATABASE CURRENT SET RECOVERY FULL`, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the build exits with code 0
- **AND** the manifest shows original model FULL and sealed model SIMPLE

### Requirement: Images carry a minimal log
Every image build, from a backup or from scripts, SHALL replace the database's transaction log with a new minimal log before sealing, so that creating a branch does not rewrite the source's log, and the manifest SHALL record the original log size and that the log was rebuilt.

#### Scenario: Source with a 256 MB log
- **WHEN** an image is built from a database whose log file is 256 MB
- **THEN** the build exits with code 0
- **AND** the image's log file is smaller than 8 MiB
- **AND** the manifest shows the original log size and marks the log as rebuilt

#### Scenario: Writes after a rebuilt log
- **WHEN** the clone mode is `thin` or `copy`, a branch is created from that image, and one million rows are inserted
- **THEN** the create exits with code 0, the insert succeeds, and DBCC CHECKDB on the branch reports no errors

#### Scenario: Script image log
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image build shopdev --from-scripts db` runs scripts that grow the log to 64 MB
- **THEN** the build exits with code 0, the image's log file is smaller than 8 MiB, and the manifest marks the log as rebuilt

### Requirement: Images are immutable
Image files MUST NOT change after the image is sealed, whatever happens in branches created from it.

#### Scenario: Writes to a branch do not change the image
- **WHEN** the clone mode is `thin` or `copy`, the SHA-256 of every file of image `shop` is recorded, a branch is created from it, and 10,000 rows are inserted into the branch
- **THEN** the create exits with code 0
- **AND** the SHA-256 of every file of image `shop` is unchanged

### Requirement: Image name rules
Image names MUST be 1 to 63 characters of lowercase letters, digits, hyphen and underscore, starting with a letter or digit, and MUST NOT start with `sqlbranch_`.

#### Scenario: Invalid name
- **WHEN** the user runs `sqlbranch image build "Shop DB" --from-bak shop.bak`
- **THEN** the command exits with code 2 and states the naming rule

#### Scenario: Reserved prefix in an image name
- **WHEN** the user runs `sqlbranch image build sqlbranch_x --from-bak shop.bak`
- **THEN** the command exits with code 2 and says the `sqlbranch_` prefix is reserved

### Requirement: Image names are checked before any action
`image build` and `image rm` MUST exit with code 2 on a name that breaks the image name rules before running any SQL or touching any file.

#### Scenario: Path in an image name
- **WHEN** the user runs `sqlbranch image rm ../branches/pr-142`
- **THEN** the command exits with code 2 and no file in the pool is changed

### Requirement: Image names are unique
A build or import with the name of an existing image MUST fail without changing the pool. On a pool that uses format 2, a name exists when the image catalog lists a current version of it, or when an image of that name written by an older client is present.

#### Scenario: Name already used
- **WHEN** image `shop` exists and the user runs `sqlbranch image build shop --from-bak other.bak`
- **THEN** the command exits with code 1 and says the image already exists
- **AND** the existing image is unchanged

#### Scenario: Name already used on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has version `shop@20261201T090000000Z`, and the user runs `sqlbranch image build shop --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "image_exists", and `sqlbranch image list --format json` shows shop with `version` "shop@20261201T090000000Z"

#### Scenario: Name used by an image an older client wrote
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, and image `crm` was written in the 0.2.2 layout after the upgrade (tested by writing its files directly)
- **THEN** `sqlbranch image build crm --from-bak crm.bak` exits with code 1, `error.code` is "image_exists", and crm's files are unchanged

### Requirement: Concurrent builds of one name
When two `image build` commands with the same name run at the same time, exactly one SHALL succeed and the other SHALL exit with code 1.

#### Scenario: Same image name at the same time
- **WHEN** two processes run `sqlbranch image build shop --from-bak shop.bak` at the same moment
- **THEN** one exits with code 0 and the other with code 1
- **AND** `sqlbranch status` exits with code 0

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

### Requirement: Unsupported database features are rejected
A build from a backup whose database uses FILESTREAM or a memory-optimized filegroup MUST fail with a message naming the feature.

#### Scenario: Memory-optimized filegroup
- **WHEN** the backup's file list contains a memory-optimized filegroup
- **THEN** the command exits with code 1 and names the memory-optimized filegroup as unsupported

### Requirement: Failed builds leave nothing behind
When a build fails at any step, from a backup or from scripts, SqlBranch SHALL remove the temporary database, the build's login and all files the build created, except files a script placed outside the build folder, which it SHALL leave in place and name.

#### Scenario: Restore fails halfway
- **WHEN** the clone mode is `thin` or `copy` and a build fails during restore because the pool runs out of space
- **THEN** the command exits with code 1
- **AND** `sqlbranch image list` does not show the image
- **AND** `sqlbranch status` exits with code 0 and reports no mismatches

#### Scenario: Pool fills up during a script build
- **WHEN** the clone mode is `copy` and the pool runs out of space while `002_seed.sql` of `sqlbranch image build shopdev --from-scripts db` inserts rows
- **THEN** the command exits with code 1 and names `002_seed.sql`
- **AND** `sqlbranch image list` does not show `shopdev`
- **AND** `sqlbranch status` exits with code 0 and reports no mismatches

### Requirement: List images
`sqlbranch image list` SHALL show each image's name, source, source database, total size, build time and the number of branches created from it. The source SHALL be the backup file name for a backup image and `scripts (<n> files)` for a script image.

#### Scenario: Two images, one in use
- **WHEN** images `shop` and `crm` exist and two branches were created from `shop`
- **THEN** the list shows `shop` with 2 branches and `crm` with 0 branches

#### Scenario: Script image in the list
- **WHEN** image `shop` was built from `shop.bak` and image `shopdev` from three scripts, and the user runs `sqlbranch image list`
- **THEN** the command exits with code 0
- **AND** the source column shows `shop.bak` for `shop` and `scripts (3 files)` for `shopdev`

### Requirement: Remove an image
`sqlbranch image rm <name>` SHALL remove the image's files and manifest, and MUST NOT delete or change any branch file. On a pool that uses format 2 it SHALL remove the name from the image catalog, and its JSON `image.versions` SHALL list the removed version IDs. On a pool that uses format 1, and for an image written in the format-1 layout that no command has adopted yet, `image.versions` SHALL be [].

#### Scenario: Unused image
- **WHEN** the clone mode is `thin` or `copy`, image `crm` has no branches, and the user runs `sqlbranch image rm crm`
- **THEN** the command exits with code 0 and `sqlbranch image list` no longer shows `crm`

#### Scenario: Unknown image for rm
- **WHEN** the user runs `sqlbranch image rm nope`
- **THEN** the command exits with code 1 and `error.code` is "image_not_found"

#### Scenario: Remove on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, crm has version `crm@20261201T090000000Z` and no branches, and the user runs `sqlbranch image rm crm --format json`
- **THEN** the command exits with code 0, `image.versions` is ["crm@20261201T090000000Z"], and `sqlbranch image list` no longer shows crm

#### Scenario: Remove on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, crm has no branches, and the user runs `sqlbranch image rm crm --format json`
- **THEN** the command exits with code 0 and `image.versions` is []

#### Scenario: Remove an image not adopted yet
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, image `crm` was written in the 0.2.2 layout after the upgrade (tested by writing its files directly) and has no branches, and the user runs `sqlbranch image rm crm --format json`
- **THEN** the command exits with code 0, `image.versions` is [], and `sqlbranch image list` no longer shows crm

### Requirement: A removed image stops being listed first
`image rm` SHALL make the image unlisted and unusable for new branches before deleting its files. If deleting the files fails, it SHALL exit with code 1 and name the leftover path, and `status` SHALL report it.

#### Scenario: File delete fails
- **WHEN** the user runs `sqlbranch image rm crm` and deleting one of its data files fails
- **THEN** the command exits with code 1 and names the leftover path
- **AND** `sqlbranch image list` does not show `crm`
- **AND** `sqlbranch branch create x --from crm` exits with code 1 and `error.code` is "image_not_found"
- **AND** `sqlbranch status` exits with code 1 and lists the leftover path

### Requirement: Removing an image in use needs force
When branches created from the image exist, `image rm` MUST fail with exit code 1 and list them unless `--force` is given.

#### Scenario: Image in use, no force
- **WHEN** branch `pr-142` was created from image `shop` and the user runs `sqlbranch image rm shop`
- **THEN** the command exits with code 1 and lists `pr-142`
- **AND** the image still exists

### Requirement: Branches outlive a forced removal
After `image rm --force`, branches created from the image SHALL keep working with their data, in both clone modes.

#### Scenario: Image in use, forced
- **WHEN** the clone mode is `thin` or `copy`, branch `pr-142` was created from image `shop`, and the user runs `sqlbranch image rm shop --force`
- **THEN** the command exits with code 0 and the image is gone
- **AND** a query against branch `pr-142` still succeeds

### Requirement: Build an image from SQL scripts
`sqlbranch image build <name> --from-scripts <dir>` SHALL create an empty database, run every file whose name ends in `.sql` directly in the folder in ordinal file-name order, and seal the result as an image. Subfolders and files with other extensions SHALL be ignored. Batches SHALL be split on lines that hold only `GO`. The image's source database name SHALL be the image name.

#### Scenario: Three scripts in order
- **WHEN** the clone mode is `thin` or `copy`, folder `db` holds `001_schema.sql`, `002_seed.sql`, `010_more.sql`, `readme.md` and subfolder `old/` with `999_drop.sql`, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 0 and its output lists the three `.sql` scripts in that order, each with its run time
- **AND** a branch created from `shopdev` contains the rows `002_seed.sql` inserted and nothing from `999_drop.sql`

#### Scenario: A script fails
- **WHEN** the clone mode is `thin` or `copy` and `002_seed.sql` raises error 547 while the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1 and `error.code` is "script_failed", and the message names `002_seed.sql`, the batch's first line number and the SQL error
- **AND** `sqlbranch image list` does not show `shopdev` and `sqlbranch status` reports no mismatches

### Requirement: Script folder is checked first
`image build --from-scripts` MUST exit with code 2 and `error.code` "usage_error", before running any SQL or touching any file, when the folder does not exist, cannot be read, or holds no `.sql` file directly inside it.

#### Scenario: Missing folder
- **WHEN** the user runs `sqlbranch image build shopdev --from-scripts nope` and `nope` does not exist
- **THEN** the command exits with code 2, names `nope` and the pool is unchanged

#### Scenario: Folder without scripts
- **WHEN** folder `docs` holds only `readme.md` and subfolder `sql/` with `001.sql`, and the user runs `sqlbranch image build shopdev --from-scripts docs`
- **THEN** the command exits with code 2 and says `docs` holds no `.sql` file

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

### Requirement: Post-build script files are checked first
`image build` MUST exit with code 2 and `error.code` "usage_error", before running any SQL or touching any file, when a `--post-script` file does not exist or cannot be read.

#### Scenario: Missing post-build script
- **WHEN** the user runs `sqlbranch image build shop --from-bak shop.bak --post-script nope.sql` and `nope.sql` does not exist
- **THEN** the command exits with code 2, names `nope.sql`, and the pool is unchanged

### Requirement: Scripts run with rights limited to the build database
Source and post-build scripts SHALL run under a login SqlBranch creates for that build and makes the build database's owner. Outside that database the login SHALL have only the server's `public` rights and guest access to `master`, `tempdb` and `msdb`. Scripts MUST NOT be able to drop, alter or change data in any other database.

#### Scenario: Script drops another database
- **WHEN** the clone mode is `thin` or `copy`, branch `pr-142` exists, `evil.sql` runs `DROP DATABASE [pr-142]`, and the user runs `sqlbranch image build shop --from-bak shop.bak --post-script evil.sql`
- **THEN** the command exits with code 1 and `error.code` is "script_failed"
- **AND** branch `pr-142` is online with its data unchanged
- **AND** `sqlbranch image list` does not show `shop` and `sqlbranch status` reports no mismatches

#### Scenario: Script switches to another database
- **WHEN** the clone mode is `thin` or `copy`, branch `pr-142` exists, `001_schema.sql` runs `USE [pr-142]`, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "script_failed" and the message names `001_schema.sql`

#### Scenario: Script creates a database
- **WHEN** `001_schema.sql` runs `CREATE DATABASE other` and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "script_failed", and no database `other` exists

### Requirement: Build login is removed
After a successful script run SqlBranch SHALL detach the build database while the build login owns it, drop the login, and after the attach that rebuilds the log make `sa` the owner. It MUST NOT change the owner while code of the build login can still run. After a failure it SHALL remove the build database first, then close the login's sessions and drop the login.

#### Scenario: Script creates a schema
- **WHEN** the clone mode is `thin` or `copy`, `001_schema.sql` runs `CREATE SCHEMA sales` and creates table `sales.Orders`, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 0 and no login created for the build exists on the engine
- **AND** in a branch from `shopdev`, schema `sales` is owned by `dbo` and the database owner is `sa`

#### Scenario: Owner code works in a branch
- **WHEN** the clone mode is `thin` or `copy`, `001_schema.sql` creates procedure `dbo.WhoAmI` `WITH EXECUTE AS OWNER`, `sqlbranch image build shopdev --from-scripts db` exits with code 0, and a branch `own1` is created from `shopdev`
- **THEN** `EXEC dbo.WhoAmI` in `own1` succeeds
- **AND** in `own1`, `SUSER_SNAME(owner_sid)` of the `dbo` user is `sa`

#### Scenario: No build login left in the image
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image build shopdev --from-scripts db` exits with code 0
- **THEN** no login created for the build exists on the engine
- **AND** a branch from `shopdev` has no database user other than the ones its scripts created

### Requirement: Scripts must not leave database DDL triggers
After the scripts and before the build database is detached, SqlBranch SHALL compare the database-scoped DDL triggers with those present before the scripts ran, and SHALL compare them again after the re-attach, immediately before changing the owner to `sa`. A trigger a script created, changed or enabled MUST fail the build with exit code 1 and `error.code` "ddl_trigger", naming it, and leave nothing behind.

#### Scenario: Trigger on ownership change
- **WHEN** a script in either clone mode creates a database trigger named `ownership_audit` for `ALTER_AUTHORIZATION_DATABASE` with a body of `RETURN`
- **THEN** the build exits with code 1, reports `ddl_trigger` naming the handler and leaves no build database, build login or status mismatch

#### Scenario: Background code still running at the freeze
- **WHEN** the clone mode is `thin` or `copy`, `001_queue.sql` starts a Service Broker activation procedure, `EXECUTE AS OWNER`, that keeps creating and dropping trigger `t_late` for `ALTER_AUTHORIZATION_DATABASE` for 40 seconds, and the user runs `sqlbranch image build latetrig --from-scripts db`
- **THEN** the command exits with code 0, or with code 1 and `error.code` "ddl_trigger"
- **AND** `t_late` never fires, no branch of the image has a database DDL trigger, and no build login or build database is left

#### Scenario: Trigger on ALTER_DATABASE
- **WHEN** a script creates a database-scoped trigger for `ALTER_DATABASE` with a body of `RETURN`
- **THEN** the build exits with code 1 and reports `script_failed` if SQL Server rejects the event at database scope, or `ddl_trigger` if the trigger was created

#### Scenario: Post-build script changes a trigger from the backup
- **WHEN** `shop.bak` holds database trigger `audit_ddl` for table creation only, `evil.sql` runs `ALTER TRIGGER audit_ddl ON DATABASE ...`, and the user runs `sqlbranch image build shop --from-bak shop.bak --post-script evil.sql`
- **THEN** the command exits with code 1, `error.code` is "ddl_trigger" and the message names `audit_ddl`

### Requirement: Database DDL triggers from a backup are kept
Database-scoped DDL triggers that were in the restored backup and that no script changed SHALL stay in the image, and the manifest SHALL list their names. When post-scripts are requested, any enabled database trigger covering the ownership change, directly or through an event group, MUST fail the build before login creation, ownership transfer or script execution, with exit code 1 and `error.code` "ddl_trigger", naming the trigger.

#### Scenario: Backup with an audit trigger
- **WHEN** the clone mode is `thin` or `copy`, `shop.bak` holds an enabled database trigger `audit_ddl` for ownership changes, and the user runs `sqlbranch image build shop --from-bak shop.bak`
- **THEN** the command exits with code 0
- **AND** `sqlbranch image list --format json` shows `shop` with `ddlTriggers` ["audit_ddl"]

#### Scenario: Enabled ownership-change handler in a backup
- **WHEN** a thin or copy pool builds a backup with a post-script and an enabled audit trigger covering `ALTER_AUTHORIZATION_DATABASE` directly or through `DDL_DATABASE_LEVEL_EVENTS`
- **THEN** the build exits 1 with `ddl_trigger`, names the trigger and leaves no build login, database or folder
- **AND** neither the handler nor the post-script runs

#### Scenario: Disabled or unrelated restored trigger
- **WHEN** a backup with a disabled ownership-change handler and an enabled table-creation handler is built with a valid post-script
- **THEN** the build exits 0 and the script's changes appear in the image

### Requirement: Scripts must leave the database usable
After the scripts and before the build database is detached, it MUST be ONLINE, read-write and MULTI_USER. Otherwise the build MUST exit with code 1 and `error.code` "script_failed", naming the state, and leave nothing behind.

#### Scenario: Script makes the database read-only
- **WHEN** the clone mode is `thin` or `copy`, `010_more.sql` runs `ALTER DATABASE CURRENT SET READ_ONLY`, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "script_failed" and the message names `READ_ONLY`
- **AND** no build login and no build database exist, and `sqlbranch status` reports no mismatches

#### Scenario: Script takes the database offline
- **WHEN** the clone mode is `thin` or `copy`, `010_more.sql` runs `ALTER DATABASE CURRENT SET OFFLINE`, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "script_failed" and the message names `OFFLINE`
- **AND** no build login and no build database exist, the build folder is gone, and `sqlbranch status` reports no mismatches

#### Scenario: Script leaves single-user mode
- **WHEN** `mask.sql` runs `ALTER DATABASE CURRENT SET SINGLE_USER` and the user runs `sqlbranch image build shop --from-bak shop.bak --post-script mask.sql`
- **THEN** the command exits with code 1 and the message names `SINGLE_USER`

### Requirement: Scripts must keep the database inside the pool
After the scripts and before the build database is detached, every file of it MUST be inside the build folder and it MUST have no FILESTREAM or memory-optimized filegroup. Otherwise the build MUST exit with code 1 and `error.code` "unsupported_feature", naming the file or filegroup. A database with a file outside the build folder SHALL be detached, not dropped, and the error SHALL list those files.

#### Scenario: Script adds a file outside the pool
- **WHEN** the clone mode is `thin` or `copy`, `010_more.sql` runs `ALTER DATABASE CURRENT ADD FILE (NAME = extra, FILENAME = '/var/opt/mssql/data/extra.ndf')`, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "unsupported_feature", and the message names `/var/opt/mssql/data/extra.ndf` as left in place
- **AND** no build login and no build database exist, and `sqlbranch image list` does not show `shopdev`

#### Scenario: Script adds a memory-optimized filegroup
- **WHEN** `010_more.sql` adds a `MEMORY_OPTIMIZED_DATA` filegroup with a container inside the build folder, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "unsupported_feature", and the message names the memory-optimized filegroup
- **AND** no build login and no build database exist, and `sqlbranch status` reports no mismatches

### Requirement: Script-built images have Service Broker disabled
An image built from scripts or with post-build scripts SHALL be sealed with Service Broker disabled, and its manifest SHALL record `serviceBroker` "disabled". Any other image SHALL keep the restored database's state and record it as "enabled" or "disabled".

#### Scenario: Queue left by a script
- **WHEN** the clone mode is `thin` or `copy`, `001_queue.sql` creates a Service Broker queue and confirms its activation procedure recorded a test message in a local marker table, and `sqlbranch image build shopdev --from-scripts db` exits with code 0
- **THEN** `sqlbranch image list --format json` shows `shopdev` with `serviceBroker` "disabled"
- **AND** in a branch from `shopdev`, `is_broker_enabled` is 0 in `sys.databases`
- **AND** a new test message sent in that branch remains pending and the marker count stays at its pre-seal value

#### Scenario: Backup image without scripts
- **WHEN** `sqlbranch image build shop --from-bak shop.bak` runs without `--post-script` and exits with code 0
- **THEN** `sqlbranch image list --format json` shows `shop` with `serviceBroker` "enabled" if `is_broker_enabled` of the restored database was 1, and "disabled" otherwise

### Requirement: Script builds refuse databases open to guest
Before changing anything, a build that runs scripts MUST check every ONLINE, non-SINGLE_USER database with `database_id` above 4, and SHALL skip all others. When one grants `CONNECT` to `guest`, or cannot be read, the build MUST exit with code 1, `error.code` "guest_database", name it, say which of the two applies, and change nothing.

#### Scenario: Branch whose database allows guest
- **WHEN** branch `pr-142` was created from an image whose source database had `guest` enabled, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "guest_database" and the message names `pr-142`
- **AND** no SQL from `db` ran, `pr-142` is unchanged, and the pool is unchanged

#### Scenario: User database that allows guest
- **WHEN** a user created database `scratch` and ran `GRANT CONNECT TO guest` in it, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, names `scratch`, and `scratch` is unchanged

#### Scenario: Database being restored
- **WHEN** another `sqlbranch image build crm --from-bak crm.bak` is restoring, so its temporary database is RESTORING, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the guest check skips that database and the command exits with code 0

#### Scenario: Database held in single-user mode
- **WHEN** `sqlbranch branch rm pr-7` holds branch `pr-7` in SINGLE_USER mode and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the guest check skips `pr-7` and the command exits with code 0

#### Scenario: Database that cannot be read
- **WHEN** ONLINE, MULTI_USER database `locked` cannot be read by the check (tested with a fake connector) and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "guest_database", and the message names `locked` and says it could not be read

#### Scenario: Database name with a bracket
- **WHEN** a user database named `odd]name` grants `CONNECT` to `guest`, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "guest_database" and the message names `odd]name`

#### Scenario: Backup build without scripts
- **WHEN** the clone mode is `thin` or `copy`, branch `pr-142` allows `guest`, and the user runs `sqlbranch image build crm --from-bak crm.bak` without `--post-script`
- **THEN** the command exits with code 0

### Requirement: Leftover build logins are removed
Each `image build` SHALL first remove every login named `sqlbranch_build_<id>` whose build is not running: drop its leftover build database, close its sessions, then drop it. It MUST NOT touch a running build's login or any login with another name. If a removal fails, the build SHALL warn and continue.

#### Scenario: Login left by a killed build
- **WHEN** the client running a script build was killed, leaving login `sqlbranch_build_7f3a`, a user created login `app_user`, and the user runs `sqlbranch image build crm --from-bak crm.bak`
- **THEN** the command exits with code 0
- **AND** login `sqlbranch_build_7f3a` no longer exists and login `app_user` still exists

#### Scenario: Another build is running
- **WHEN** a script build in another process holds login `sqlbranch_build_9c1d` and the user runs `sqlbranch image build crm --from-bak crm.bak`
- **THEN** the command exits with code 0 and the other build also exits with code 0

#### Scenario: Leftover login cannot be dropped
- **WHEN** login `sqlbranch_build_7f3a` is left by a killed build and dropping it fails (tested with a fake connector), and the user runs `sqlbranch image build crm --from-bak crm.bak`
- **THEN** the command exits with code 0 and warns on standard error naming `sqlbranch_build_7f3a`
- **AND** `sqlbranch status` exits with code 1 and still lists `sqlbranch_build_7f3a`

### Requirement: Build login password stays secret
The password of a build's login MUST NOT appear in any command output, error message, warning, log text or file SqlBranch writes.

#### Scenario: Failing post-build script
- **WHEN** a post-build script fails during `sqlbranch image build shop --from-bak shop.bak --post-script trim.sql --format json` (tested with a fake connector that records the generated password)
- **THEN** neither standard output nor standard error contains that password

### Requirement: Script time limit
Scripts SHALL run with no time limit by default. `image build --script-timeout <duration>` SHALL limit each source or post-build script to that time, using the `--ttl` duration syntax with a maximum of 365d. A script that runs longer MUST fail the build with exit code 1 and `error.code` "script_failed" and leave nothing behind.

#### Scenario: Long script without a limit
- **WHEN** the clone mode is `thin` or `copy`, `002_seed.sql` runs for 5 minutes, and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 0

#### Scenario: Script over its limit
- **WHEN** the clone mode is `thin` or `copy`, `002_seed.sql` runs `WAITFOR DELAY '00:02:00'`, and the user runs `sqlbranch image build shopdev --from-scripts db --script-timeout 1m`
- **THEN** the command exits with code 1, `error.code` is "script_failed", and the message names `002_seed.sql` and the 1-minute limit
- **AND** `sqlbranch image list` does not show `shopdev` and `sqlbranch status` reports no mismatches

#### Scenario: Bad time limit
- **WHEN** the user runs `sqlbranch image build shopdev --from-scripts db --script-timeout 366d`, or the same with `--script-timeout 0`
- **THEN** the command exits with code 2 and names `--script-timeout`

### Requirement: GO with a count is refused
A line `GO <n>` in a source or post-build script MUST make the build exit with code 2 and `error.code` "usage_error" before any SQL runs, naming the script and the line number.

#### Scenario: Batch repeat count
- **WHEN** line 12 of `002_seed.sql` is `GO 5` and the user runs `sqlbranch image build shopdev --from-scripts db`
- **THEN** the command exits with code 2, names `002_seed.sql` and line 12, and the pool is unchanged

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

### Requirement: Export an image
`sqlbranch image export <name> --to <file>` SHALL write one compressed archive holding the image's manifest, its sealed database files and a SHA-256 for each file, and SHALL leave the image unchanged. Text and JSON output SHALL name the image, the destination, the archive size and the image size. The export MUST fail when the engine's file stream ends with an error or an entry's size differs from the manifest.

#### Scenario: Export to a local file
- **WHEN** the clone mode is `thin` or `copy`, image `shop` has one data file and one log file totalling 3 GiB, and the user runs `sqlbranch image export shop --to out/shop.sbimg --format json`
- **THEN** the command exits with code 0, `command` is "image export", and `export` has `image` "shop", `to` "out/shop.sbimg", `files` 2, `imageBytes` 3221225472 and `archiveBytes` equal to the size of `out/shop.sbimg`
- **AND** the SHA-256 of every file of image `shop` is unchanged

#### Scenario: File size differs from the manifest
- **WHEN** the clone mode is `copy` and the engine's stream for image `shop` delivers its data file 4096 bytes shorter than the manifest says (tested with a fake runtime)
- **THEN** `sqlbranch image export shop --to shop.sbimg` exits with code 1 and `shop.sbimg` does not exist

#### Scenario: Image does not exist
- **WHEN** the user runs `sqlbranch image export nope --to nope.sbimg`
- **THEN** the command exits with code 1, `error.code` is "image_not_found", and `nope.sbimg` does not exist

### Requirement: Archives hold one image and nothing else
An export archive SHALL be one compressed stream whose compression its first bytes identify, and SHALL hold exactly the image's manifest, the files the manifest lists, and their SHA-256 values. Import SHALL read every compression this version's export can write. It MUST NOT hold branch files, other images, credentials, logins, passwords, storage secrets, build folders or engine settings.

#### Scenario: Compression is recorded in the archive
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image export shop --to shop.sbimg` exits with code 0
- **THEN** the first bytes of `shop.sbimg` are the gzip magic bytes `1f 8b`, unless spike 1.1 chose zstd, in which case they are `28 b5 2f fd`

#### Scenario: Archive content with branches present
- **WHEN** the clone mode is `thin` or `copy`, branches `pr-1` and `pr-2` exist from image `shop`, and `sqlbranch image export shop --to shop.sbimg` exits with code 0
- **THEN** the archive's entries are the manifest, the two files named in the manifest and the checksum list, in that order
- **AND** no entry contains the engine password or the name `pr-1`

### Requirement: Export to cloud storage
`--to` SHALL accept an `https://` Azure Blob Storage URL or an `s3://` URL in the forms `image build --from-bak` accepts, and write the archive as one blob or object at that URL without a temporary copy on the client. `--s3-region` SHALL set the region of an `s3://` URL, `us-east-1` by default.

#### Scenario: Export to S3-compatible storage
- **WHEN** the clone mode is `thin` or `copy`, a local S3-compatible server with a certificate the client trusts has bucket `images`, the S3 secrets are set, and `shop` is exported to `s3://172.17.0.5:9000/images/nightly/shop.sbimg`
- **THEN** the export exits with code 0 and object `nightly/shop.sbimg` in bucket `images` has the size reported as `archiveBytes`

#### Scenario: Export to Azure Blob Storage
- **WHEN** the clone mode is `copy`, `SQLBRANCH_AZURE_SAS_TOKEN` grants read, create and write on container `images`, and the user runs `sqlbranch image export shop --to https://acme.blob.core.windows.net/images/shop.sbimg` (tested with a fake storage service)
- **THEN** the command exits with code 0 and the blob is committed once, after its last block was uploaded

### Requirement: Export input is checked first
`image export` MUST exit with code 2 before running any `docker` command when the name breaks the image name rules (`error.code` "invalid_name"), `--to` is missing, `--to` is an existing directory, its parent folder does not exist or it lies under a pool directory, or `--s3-region` is misused as for `image build` (`error.code` "usage_error").

#### Scenario: Invalid image name on export
- **WHEN** the user runs `sqlbranch image export "Shop DB" --to shop.sbimg`
- **THEN** the command exits with code 2, states the naming rule, and no `docker` command ran

#### Scenario: No destination
- **WHEN** the user runs `sqlbranch image export shop`
- **THEN** the command exits with code 2, names `--to`, and no `docker` command ran

#### Scenario: Destination is a directory
- **WHEN** folder `out` exists and the user runs `sqlbranch image export shop --to out`
- **THEN** the command exits with code 2, names `out`, and no `docker` command ran

#### Scenario: Destination folder missing
- **WHEN** folder `missing` does not exist and the user runs `sqlbranch image export shop --to missing/shop.sbimg`
- **THEN** the command exits with code 2, names `missing`, and no `docker` command ran

#### Scenario: Destination inside the pool
- **WHEN** the pool is the directory `/srv/sqlbranch-pool` and the user runs `sqlbranch image export shop --to /srv/sqlbranch-pool/import/shop.sbimg`
- **THEN** the command exits with code 2, says the destination must be outside the pool, and no `docker` command ran

#### Scenario: Region without an S3 destination
- **WHEN** the user runs `sqlbranch image export shop --to shop.sbimg --s3-region eu-west-1`
- **THEN** the command exits with code 2, names `--s3-region`, and no `docker` command ran

#### Scenario: Malformed region on export
- **WHEN** the user runs `sqlbranch image export shop --to s3://s3.amazonaws.com/images/shop.sbimg --s3-region "EU West"`
- **THEN** the command exits with code 2, names `--s3-region`, and no `docker` command ran

### Requirement: Archive URLs are checked first
A `--to` value of `image export` or a source of `image import` that starts with `http://`, `https://` or `s3://` (any case) SHALL be a URL. The command MUST exit with code 2 and `error.code` "usage_error" before any `docker` command or storage request when the URL uses `http://`, has a query string or fragment, has no host, has no container or bucket and file name, or ends in `/`, and MUST NOT echo a query string.

#### Scenario: Plain HTTP destination
- **WHEN** the user runs `sqlbranch image export shop --to http://acme.blob.core.windows.net/images/shop.sbimg`
- **THEN** the command exits with code 2, says only `https://` and `s3://` URLs are supported, and no `docker` command ran

#### Scenario: Pasted SAS URL as import source
- **WHEN** the user runs `sqlbranch image import "https://acme.blob.core.windows.net/images/shop.sbimg?sv=2024-11-04&sig=AbC123secret"`
- **THEN** the command exits with code 2 and names `SQLBRANCH_AZURE_SAS_TOKEN`
- **AND** neither standard output nor standard error contains `AbC123secret`

#### Scenario: URL with a fragment
- **WHEN** the user runs `sqlbranch image import s3://s3.amazonaws.com/images/shop.sbimg#v2`
- **THEN** the command exits with code 2 and no `docker` command ran

#### Scenario: URL without a host
- **WHEN** the user runs `sqlbranch image import s3:///images/shop.sbimg`
- **THEN** the command exits with code 2 and no `docker` command ran

#### Scenario: URL without a container
- **WHEN** the user runs `sqlbranch image export shop --to https://acme.blob.core.windows.net/shop.sbimg`
- **THEN** the command exits with code 2 and says the URL must name a container and a file

#### Scenario: URL ending in a slash
- **WHEN** the user runs `sqlbranch image export shop --to s3://s3.us-east-1.amazonaws.com/images/`
- **THEN** the command exits with code 2 and says the URL must name a file

### Requirement: Export does not overwrite without force
When the `--to` file or object already exists, `image export` MUST exit with code 1 and `error.code` "destination_exists", naming it and `--force`, before reading any image file, and leave it unchanged. With `--force` it SHALL replace it only after the new archive is complete. When a storage service that honours `If-None-Match: *` gets the object during the upload, the export MUST fail the same way without replacing it.

#### Scenario: Existing file without force
- **WHEN** the clone mode is `thin` or `copy`, `shop.sbimg` exists with SHA-256 `h1`, and the user runs `sqlbranch image export shop --to shop.sbimg`
- **THEN** the command exits with code 1, `error.code` is "destination_exists", the message names `--force`, and `shop.sbimg` still has SHA-256 `h1`

#### Scenario: Existing file with force
- **WHEN** the clone mode is `thin` or `copy`, `shop.sbimg` exists, and the user runs `sqlbranch image export shop --to shop.sbimg --force`
- **THEN** the command exits with code 0 and `sqlbranch image import shop.sbimg --as shop2` exits with code 0

#### Scenario: Object created during the upload
- **WHEN** the clone mode is `copy`, `s3://172.17.0.5:9000/images/shop.sbimg` does not exist when `sqlbranch image export shop --to s3://172.17.0.5:9000/images/shop.sbimg` starts, and another client writes that object before the export completes (tested with a fake storage service that honours `If-None-Match: *`)
- **THEN** the command exits with code 1, `error.code` is "destination_exists", and the other client's object is unchanged

### Requirement: Failed exports leave the destination as it was
When an export fails or is cancelled at any step, SqlBranch SHALL exit with a non-zero code, delete its partial local file, abort its S3 multipart upload and never commit a partial Azure blob, so that the destination is as it was before. A file or object that existed before MUST keep its content.

#### Scenario: Engine stream breaks halfway to a file
- **WHEN** the clone mode is `copy` and reading the image files from the engine fails after half of the bytes (tested with a fake runtime) during `sqlbranch image export shop --to shop.sbimg`
- **THEN** the command exits with code 1, `shop.sbimg` does not exist, and the folder holds no partial file

#### Scenario: Failure while replacing a file
- **WHEN** the clone mode is `copy`, `shop.sbimg` exists with SHA-256 `h1`, and `sqlbranch image export shop --to shop.sbimg --force` fails halfway (tested with a fake runtime)
- **THEN** the command exits with code 1 and `shop.sbimg` still has SHA-256 `h1`

#### Scenario: Cancelled S3 export
- **WHEN** the clone mode is `thin` or `copy` and the process receives Ctrl+C while `sqlbranch image export shop --to s3://172.17.0.5:9000/images/shop.sbimg` uploads parts
- **THEN** the command exits with a non-zero code, the bucket has no object `shop.sbimg` and no unfinished multipart upload for that key

#### Scenario: Image removed during export
- **WHEN** the clone mode is `copy` and the engine's `tar` exits with a non-zero code because `sqlbranch image rm shop` deleted a file while `sqlbranch image export shop --to shop.sbimg` ran (tested with a fake runtime)
- **THEN** the export exits with code 1 and `shop.sbimg` does not exist

### Requirement: Import an image
`sqlbranch image import <file>` SHALL add the archive's image to the pool under the manifest's name, or under `--as <name>`, as a full copy, and SHALL report the clone mode. The imported manifest SHALL equal the exported one except for the name. Branches from it SHALL be created like branches from a built image.

#### Scenario: Import under a new name
- **WHEN** the clone mode is `thin`, `shop.sbimg` was exported from image `shop`, and the user runs `sqlbranch image import shop.sbimg --as shop2 --format json`
- **THEN** the command exits with code 0, `command` is "image import", `cloneMode` is "thin", and `image.name` is "shop2"
- **AND** `sqlbranch image list --format json` shows `shop2` with the same `sourceDatabase`, files, sizes, `engineVersion` and `builtAt` as `shop`, and the SHA-256 of each file of `shop2` equals that of the same file of `shop`
- **AND** `sqlbranch branch create pr-9 --from shop2` exits with code 0 and reports clone mode `thin`

#### Scenario: Import in copy mode
- **WHEN** the clone mode is `copy` and the user runs `sqlbranch image import shop.sbimg` on a pool with no image `shop`
- **THEN** the command exits with code 0, the output states clone mode `copy`, and `sqlbranch image list` shows `shop`

#### Scenario: Import from S3-compatible storage
- **WHEN** the clone mode is `thin` or `copy`, `s3://172.17.0.5:9000/images/nightly/shop.sbimg` was written by `image export`, the server's certificate is trusted by the client, and the S3 secrets are set
- **THEN** importing it with `--as shop3` exits with code 0 and `sqlbranch image list` shows `shop3`, with no copy of the archive in the client's working directory or temporary folder

### Requirement: Import input is checked first
`image import` MUST exit with code 2 before running any `docker` command when the local source does not exist, cannot be read or is a directory, `--as` breaks the image name rules (`error.code` "invalid_name"), or `--s3-region` is misused as for `image build` (`error.code` "usage_error").

#### Scenario: Missing archive file
- **WHEN** `nope.sbimg` does not exist and the user runs `sqlbranch image import nope.sbimg`
- **THEN** the command exits with code 2, names `nope.sbimg`, and no `docker` command ran

#### Scenario: Unreadable archive file
- **WHEN** `locked.sbimg` exists but the user may not read it, and the user runs `sqlbranch image import locked.sbimg`
- **THEN** the command exits with code 2, names `locked.sbimg`, and no `docker` command ran

#### Scenario: Archive path is a directory
- **WHEN** folder `out` exists and the user runs `sqlbranch image import out`
- **THEN** the command exits with code 2, names `out`, and no `docker` command ran

#### Scenario: Invalid name in --as
- **WHEN** the user runs `sqlbranch image import shop.sbimg --as ../branches/x`
- **THEN** the command exits with code 2, states the naming rule, and no `docker` command ran

#### Scenario: Region without an S3 source
- **WHEN** the user runs `sqlbranch image import shop.sbimg --s3-region eu-west-1`
- **THEN** the command exits with code 2, names `--s3-region`, and no `docker` command ran

### Requirement: Import refuses archives it cannot read
After the exit-2 checks and before any `docker` command, so also before the `not_initialized` check, `image import` SHALL read the archive's first entry and MUST exit with code 1 and `error.code` "archive_invalid" when the source is not a SqlBranch image archive, uses an unknown compression or a newer archive format, or its manifest fails the checks of "Archive manifests are checked".

#### Scenario: Not an archive
- **WHEN** `notes.txt` is a text file and the user runs `sqlbranch image import notes.txt`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

#### Scenario: Valid archive on a machine without an engine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch image import shop.sbimg` with a valid archive
- **THEN** the command exits with code 3 and `error.code` is "not_initialized"

#### Scenario: Invalid archive on a machine without an engine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch image import notes.txt`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

#### Scenario: Newer archive format
- **WHEN** `future.sbimg` declares archive format 2 and the user runs `sqlbranch image import future.sbimg`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", the message says a newer SqlBranch wrote it, and no `docker` command ran

### Requirement: Archive manifests are checked
The archive's manifest is untrusted. `image import` MUST treat it as invalid when its image name breaks the name rules and no valid `--as` is given, `files` is empty, a `fileName` contains `/`, `\`, `..` or NUL, starts with `.`, or repeats ignoring case, a `type` is not `data` or `log`, a `sizeBytes` is negative, or the sizes' total overflows a 64-bit integer.

#### Scenario: Traversal in a manifest file name
- **WHEN** the manifest in `evil.sbimg` lists a file named `../branches/pr-1/x.mdf` and the user runs `sqlbranch image import evil.sbimg`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

#### Scenario: Negative size in a manifest
- **WHEN** the manifest in `neg.sbimg` lists a file with `sizeBytes` -1 and the user runs `sqlbranch image import neg.sbimg`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

#### Scenario: Unknown file type in a manifest
- **WHEN** the manifest in `odd.sbimg` lists a file with `type` "filestream" and the user runs `sqlbranch image import odd.sbimg`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

### Requirement: Import accepts only the listed files
`image import` SHALL write into the pool only regular files that the archive's manifest lists, under their plain file names. An archive entry that is not listed, appears twice, is a link or device, has a name holding a path separator or `..`, or whose header size differs from the manifest's size, MUST make the import exit with code 1 and `error.code` "archive_invalid" before that entry's bytes are written, and remove what it wrote.

#### Scenario: Path traversal entry
- **WHEN** the clone mode is `copy` and `evil.sbimg` holds an entry named `../branches/pr-1/x.mdf` after a valid manifest
- **THEN** `sqlbranch image import evil.sbimg` exits with code 1, `error.code` is "archive_invalid", no file outside the import's own build folder was written, and `sqlbranch status` exits with code 0

#### Scenario: Entry size differs from the manifest
- **WHEN** the clone mode is `copy` and the tar header of `shop.mdf` in `big.sbimg` says 10 GiB while the manifest says 1 GiB
- **THEN** `sqlbranch image import big.sbimg --as shop2` exits with code 1, `error.code` is "archive_invalid", and no byte of `shop.mdf` was written to the pool

#### Scenario: Symbolic link entry
- **WHEN** the clone mode is `copy` and `link.sbimg` holds a symbolic link entry named `shop.mdf`
- **THEN** `sqlbranch image import link.sbimg` exits with code 1, `error.code` is "archive_invalid", and `sqlbranch image list` does not show its image

### Requirement: Import refuses images from a newer SQL Server
When the archive's engine major version is newer than the local engine's, `image import` MUST exit with code 1 and `error.code` "version_mismatch" before writing any file, naming both versions and the manifest's engine image. When only the build number is newer within the same major version, it SHALL warn on standard error naming both versions and continue.

#### Scenario: SQL Server 2025 image on a 2022 engine
- **WHEN** the clone mode is `copy`, the local engine runs 16.0.4135.4, and `shop.sbimg` was exported from an image with engine version 17.0.1000.7 and engine image `mcr.microsoft.com/mssql/server:2025-latest`
- **THEN** `sqlbranch image import shop.sbimg` exits with code 1, `error.code` is "version_mismatch", the message names 17.0.1000.7, 16.0.4135.4 and `mcr.microsoft.com/mssql/server:2025-latest`, and the pool is unchanged

#### Scenario: Newer build of the same major version
- **WHEN** the clone mode is `copy`, the local engine runs 16.0.4100.1, and `shop.sbimg` was exported with engine version 16.0.4135.4
- **THEN** `sqlbranch image import shop.sbimg` exits with code 0 and standard error names both versions

### Requirement: Import checks every file's SHA-256
`image import` SHALL compute the SHA-256 and size of every file it writes and compare them with the archive's checksum list and manifest. On any difference, a missing checksum list or a truncated archive, it MUST exit with code 1 and `error.code` "archive_corrupt", naming the file, and leave no image.

#### Scenario: Corrupted byte
- **WHEN** the clone mode is `thin` or `copy` and one byte of the data file inside `shop.sbimg` was changed after export
- **THEN** `sqlbranch image import shop.sbimg --as shop2` exits with code 1, `error.code` is "archive_corrupt", and the message names the data file
- **AND** `sqlbranch image list` does not show `shop2` and `sqlbranch status` exits with code 0

#### Scenario: Truncated archive
- **WHEN** the clone mode is `copy` and `shop.sbimg` was cut to half its size
- **THEN** `sqlbranch image import shop.sbimg --as shop2` exits with code 1, `error.code` is "archive_corrupt", and `sqlbranch image list` does not show `shop2`

### Requirement: Import checks space first
Before writing any file, `image import` SHALL check that free pool space is at least the total size of the files the manifest lists, and MUST exit with code 1 and `error.code` "pool_full" naming both sizes when it is not.

#### Scenario: Not enough space to import
- **WHEN** the clone mode is `thin` or `copy`, the archive's image files total 50 GiB, and free pool space is 40 GiB
- **THEN** `sqlbranch image import shop.sbimg` exits with code 1, `error.code` is "pool_full", the message names 50 GiB needed and 40 GiB available, and the pool is unchanged

### Requirement: Imported image names are unique
`image import` MUST NOT replace an existing image. When the target name is taken, it SHALL exit with code 1 and `error.code` "image_exists", naming `--as`, before writing any file. An import and a build or another import of the same name at the same time SHALL leave exactly one of them succeeding.

#### Scenario: Name already used on import
- **WHEN** the clone mode is `thin` or `copy`, image `shop` exists, and the user runs `sqlbranch image import shop.sbimg`
- **THEN** the command exits with code 1, `error.code` is "image_exists", the message names `--as`, and image `shop` is unchanged

#### Scenario: Import and build of one name at once
- **WHEN** the clone mode is `copy` and `sqlbranch image import shop.sbimg --as crm` and `sqlbranch image build crm --from-bak crm.bak` run at the same moment
- **THEN** one exits with code 0 and the other with code 1, and `sqlbranch status` exits with code 0

### Requirement: Failed imports leave nothing behind
When an import fails or is cancelled after it started writing, SqlBranch SHALL wait for its file stream into the engine to end, then remove its build folder, and the image folder only if this import created it. It MUST NOT remove an image folder it did not create, and the image MUST NOT be listed. When the client is killed, `status` SHALL list the import's build folder as a build leftover.

#### Scenario: Pool fills up during import
- **WHEN** the clone mode is `copy` and the pool runs out of space while `sqlbranch image import shop.sbimg --as shop2` writes the data file
- **THEN** the command exits with code 1, `sqlbranch image list` does not show `shop2`, and `sqlbranch status` exits with code 0

#### Scenario: Seal fails after the image folder was created
- **WHEN** the clone mode is `copy` and moving the files, setting their mode, or writing the manifest of `shop2` fails during `sqlbranch image import shop.sbimg --as shop2` (tested with a fake runtime, each step in turn)
- **THEN** the command exits with code 1, `/pool/images/shop2` does not exist, and `sqlbranch image import shop.sbimg --as shop2` then exits with code 0

#### Scenario: Cancelled import
- **WHEN** the clone mode is `thin` or `copy` and the process receives Ctrl+C while `sqlbranch image import shop.sbimg --as shop2` writes files
- **THEN** the command exits with a non-zero code, `sqlbranch image list` does not show `shop2`, and `sqlbranch status` exits with code 0

#### Scenario: Killed import
- **WHEN** the clone mode is `copy` and the client running `sqlbranch image import shop.sbimg --as shop2` was killed while writing files
- **THEN** `sqlbranch status` exits with code 1 and lists the import's build folder, and `sqlbranch image list` does not show `shop2`

### Requirement: Unreachable archive storage
When storage cannot be reached, refuses the secret, presents a certificate the client does not trust, or has no object at the import URL, `image export` and `image import` MUST exit with code 1 and `error.code` "storage_unreachable", naming the URL and the reason, and follow the cleanup rules of export and import.

#### Scenario: Wrong S3 secret on import
- **WHEN** the clone mode is `thin` or `copy` and the user runs `sqlbranch image import s3://172.17.0.5:9000/images/shop.sbimg` with a wrong `SQLBRANCH_S3_SECRET_ACCESS_KEY`
- **THEN** the command exits with code 1, `error.code` is "storage_unreachable", no `docker` command ran, and the pool is unchanged

#### Scenario: Missing blob on import
- **WHEN** the user runs `sqlbranch image import https://acme.blob.core.windows.net/images/nope.sbimg` with a valid SAS token (tested with a fake storage service that returns 404)
- **THEN** the command exits with code 1, `error.code` is "storage_unreachable", and the message names `https://acme.blob.core.windows.net/images/nope.sbimg`

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

### Requirement: Detach login-mapped package users
A logical build SHALL import user-defined users mapped to server logins as users without a login, keeping their database permissions, role memberships and ownership. It SHALL import a validated scratch copy without the package's logins or server objects referencing them, MUST NOT create or change any login named in the package or already on the engine, and MUST leave the supplied file unchanged.

#### Scenario: Two users depend on server logins
- **WHEN** the clone mode is `thin` or `copy`, the package maps `report_reader` and `sales_app` to logins of the same names, `report_reader` owns schema `reports` and `sales_app` has SELECT on `dbo.Orders`, and the engine already has a login named `sales_app`
- **THEN** the build exits 0, both users exist in a branch as users without a login with the ownership and grant intact, the existing `sales_app` login keeps its SID, state and memberships, and the supplied package file is unchanged

#### Scenario: Users linked as in an Azure SQL export
- **WHEN** the clone mode is `thin` or `copy` and each user references its `SqlLogin` element, one with a `Disambiguator`, through a `Login` relationship instead of a `LoginIdentifierScript` property
- **THEN** the users are detached the same way and the build exits 0

#### Scenario: A retained object references a removed login
- **WHEN** the clone mode is `thin` or `copy` and a retained model element other than a server role membership or server permission references a package login
- **THEN** the build exits 1 with `unsupported_feature` before import, names the element type and name, and publishes no image

#### Scenario: Damaged package with mapped users
- **WHEN** the clone mode is `thin` or `copy` and a package with login-mapped users has a model checksum that does not match `model.xml`
- **THEN** the build exits 1 with `bacpac_invalid` before any rewrite or import

#### Scenario: Package without login-mapped users
- **WHEN** the clone mode is `thin` or `copy` and the package has no `SqlLogin` and only contained users and users without a login
- **THEN** the build makes no additional copy and reports no detached users and no `rewrite` phase

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
Before import the build SHALL report the supplied package's bytes and warn that large operations can take hours. It SHALL report elapsed milliseconds per attempted phase, including a failed phase and `rewrite` when it ran. Text and versioned JSON SHALL identify size, phases, detached user names, `removedLogins` and `removedServerObjects`. In JSON mode live notices MUST use standard error so standard output holds one result.

#### Scenario: Timings with a fake clock
- **WHEN** the clone mode is `copy`, a 2048-byte package takes 120 ms to import and 15 ms to seal, and JSON output is requested
- **THEN** standard error reports 2048 bytes and the hours warning before import, and the single JSON result includes `schemaVersion`, `sourceSizeBytes` 2048 and `phaseMilliseconds` with `import` 120 and `seal` 15

#### Scenario: Detached users in JSON
- **WHEN** the clone mode is `copy`, the package maps `report_reader` and `sales_app` to two logins and one server role membership has `sales_app` as member, and JSON output is requested
- **THEN** the single JSON result includes `detachedUsers` with both names, `removedLogins` 2, `removedServerObjects` 1 and a `rewrite` phase time, and `sourceSizeBytes` and `bacpacSha256` describe the supplied package

#### Scenario: Detached users in text
- **WHEN** the clone mode is `copy`, the same package is built with text output
- **THEN** the result names both detached users and both counts

### Requirement: Protect temporary packages
A build SHALL store downloaded BACPACs in owner-only client scratch and remove only files owned by that operation on success, failure and Ctrl+C. Deletion MUST preserve source files, remote objects and other builds. If safe removal fails the build MUST exit 1 with `operation_failed`, withhold publication and identify the leftover without credentials.

#### Scenario: Cancellation during download
- **WHEN** the clone mode is `thin` or `copy` and Ctrl+C interrupts writing the private download for build A while build B is importing its own file
- **THEN** A exits 1 with `operation_failed`, its partial download is removed, A publishes no image, B's file and the remote object remain unchanged, and no other user had access to A's file

### Requirement: Builds and imports publish versions on a format-2 pool
On a pool that uses format 2, every `image build` source (backup file, backup URL, scripts, BACPAC) and `image import` SHALL publish the new image as the first version of its name, with a version ID, and only after the image is sealed. A failed build or import MUST NOT add a version.

#### Scenario: Script build on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, and `sqlbranch image build shopdev --from-scripts db` publishes at 2026-12-02T02:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0, `sqlbranch image list --format json` shows shopdev with `version` "shopdev@20261202T020000000Z", and `sqlbranch branch create d1 --from shopdev` exits 0

#### Scenario: BACPAC build and import on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, and the user runs `sqlbranch image build sales --from-bacpac sales.bacpac` and `sqlbranch image import shop.sbimg --as shop3`
- **THEN** both exit with code 0 and `sqlbranch image list --format json` shows sales and shop3, each with a non-null `version`

#### Scenario: Failed build on a format-2 pool
- **WHEN** the clone mode is `copy`, the pool uses format 2, and `sqlbranch image build shopdev --from-scripts db` fails in `002_seed.sql`
- **THEN** the command exits with code 1, `sqlbranch image list` does not show shopdev, and `sqlbranch status` exits with code 0

### Requirement: Image version IDs
On a pool that uses format 2, every version SHALL have an ID of the form `<name>@<yyyyMMdd>T<HHmmssfff>Z`, the UTC publication time to the millisecond. IDs of one name MUST be unique and increase with publication order, also when the clock goes back or the name is removed and used again.

#### Scenario: Name removed and rebuilt in one millisecond
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, crm was published as `crm@20261201T020000000Z`, and `sqlbranch image rm crm` and `sqlbranch image build crm --from-bak crm.bak` both run while the fake clock reads 2026-12-01T02:00:00.000Z
- **THEN** the build exits 0 and `sqlbranch image list --format json` shows crm with `version` "crm@20261201T020000001Z"

#### Scenario: Clock moved back
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, crm was published as `crm@20261201T020000000Z` and then removed, and the next build of crm publishes while the fake clock reads 2026-11-30T02:00:00.000Z
- **THEN** the new version is `crm@20261201T020000001Z`

### Requirement: Version IDs are not image names
A version ID MUST NOT be accepted where an image name is expected. Such input MUST exit 2 with `error.code` "invalid_name" before any Docker command.

#### Scenario: Version used as image name
- **WHEN** the user runs `sqlbranch image rm shop@20261119T020000000Z`
- **THEN** the command exits with code 2, `error.code` is "invalid_name", and no Docker command runs

### Requirement: Build JSON reports the version
With `--format json`, a successful `image build` or `image import` SHALL add `image.version` to its existing fields, which keep their types and meanings: the version ID (string) on a pool that uses format 2, and null on a pool that uses format 1. `image list` rows SHALL add `version` the same way, null also for an image written in the format-1 layout that no command has adopted yet.

#### Scenario: Version in build JSON
- **WHEN** the clone mode is `copy`, the pool uses format 2, and `sqlbranch image build shop --from-scripts db --format json` publishes at 2026-12-01T02:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0, `schemaVersion` is 1, `image.version` is "shop@20261201T020000000Z" and `image.cloneMode` is "copy"

#### Scenario: Version in build JSON on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, and the user runs `sqlbranch image build shop --from-scripts db --format json`
- **THEN** the command exits with code 0 and `image.version` is null

### Requirement: Unreadable image catalog
On a pool that uses format 2, an image whose `catalog.json` cannot be parsed SHALL still be listed, with `version` null and `catalogReadable` false, and `status` SHALL report it as a mismatch. `image rm` SHALL remove it and keep the name's high-water mark. Every other command that uses the name MUST exit 1 with `error.code` "operation_failed", name `sqlbranch image rm <name>`, and change nothing.

#### Scenario: Catalog flag on readable catalogs
- **WHEN** the pool uses format 2 with readable catalogs, or uses format 1, and the user runs `sqlbranch image list --format json`
- **THEN** every row has `catalogReadable` true on format 2 and null on format 1

#### Scenario: List an image with an unreadable catalog
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, and the `catalog.json` of image crm holds invalid JSON (tested by writing it directly)
- **THEN** `sqlbranch image list --format json` exits with code 0 and shows crm with `version` null and `catalogReadable` false, the text output marks crm "catalog unreadable", and `sqlbranch status` exits with code 1 and names `sqlbranch image rm crm`

#### Scenario: Remove an image with an unreadable catalog
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, crm has an unreadable catalog and no branches, and the user runs `sqlbranch image rm crm --format json`
- **THEN** the command exits with code 0, `image.versions` is [], `sqlbranch image list` no longer shows crm, and a later build of crm gets a version ID above crm's high-water mark

#### Scenario: Use an image with an unreadable catalog
- **WHEN** the pool uses format 2, crm has an unreadable catalog, and the user runs `sqlbranch branch create d1 --from crm` or `sqlbranch image export crm --to crm.sbimg`
- **THEN** each command exits with code 1, `error.code` is "operation_failed", the message names `sqlbranch image rm crm`, and no file in the pool changes

### Requirement: Unreadable credentials on a format-2 pool
When the engine credentials file cannot be parsed and the pool holds format-2 content (an `images/.ids` directory or any image `catalog.json`), `image list` and `image export` MUST exit 1 with `error.code` "operation_failed" and "The engine credentials file in <pool> is not valid." instead of showing a format-1 view. Without format-2 content they SHALL behave as in 0.2.2.

#### Scenario: Image list with unreadable credentials on a format-2 pool
- **WHEN** the pool was upgraded to format 2 and its `credentials.json` then holds invalid JSON (tested by writing it directly)
- **THEN** `sqlbranch image list` and `sqlbranch image export shop --to shop.sbimg` exit with code 1, `error.code` is "operation_failed", and neither prints any image row
