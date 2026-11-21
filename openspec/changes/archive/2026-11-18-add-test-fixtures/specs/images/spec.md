# Spec Delta

## ADDED Requirements

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
An image build MUST be given exactly one of `--from-bak` and `--from-scripts`, and MUST exit with code 2 and `error.code` "usage_error" before any action otherwise.

#### Scenario: Both sources given
- **WHEN** the user runs `sqlbranch image build shop --from-bak shop.bak --from-scripts db`
- **THEN** the command exits with code 2 and says to choose one source

#### Scenario: No source given
- **WHEN** the user runs `sqlbranch image build shop`
- **THEN** the command exits with code 2 and names `--from-bak` and `--from-scripts`

### Requirement: Post-build scripts
`--post-script <file>`, which may be given several times, SHALL run each file against the database, in the order given, after the backup is restored or the source scripts have run and before the image is sealed. It applies to both sources.

#### Scenario: Masking script on a backup image
- **WHEN** the clone mode is `thin` or `copy`, `shop.bak` holds 1,000 customers with real email addresses, `mask.sql` sets every email to `user<Id>@example.test`, and the user runs `sqlbranch image build shop --from-bak shop.bak --post-script mask.sql`
- **THEN** the command exits with code 0
- **AND** in a branch created from `shop`, all 1,000 emails end in `@example.test`

#### Scenario: Post-build script fails
- **WHEN** the clone mode is `thin` or `copy`, `trim.sql` raises an error, and the user runs `sqlbranch image build shop --from-bak shop.bak --post-script mask.sql --post-script trim.sql`
- **THEN** the command exits with code 1, `error.code` is "script_failed" and the message names `trim.sql`
- **AND** `sqlbranch image list` does not show `shop` and `sqlbranch status` exits with code 0 and reports no mismatches

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

## MODIFIED Requirements

### Requirement: Image manifest
Each image SHALL record its source type (`backup` or `scripts`), source database name, engine version, each database file with logical name and size, original recovery model, build time, database DDL trigger names, Service Broker state, and post-build script names and SHA-256 in run order. A backup image SHALL also record the backup file name and SHA-256; a script image each source script's name and SHA-256 in run order.

#### Scenario: Manifest after a build
- **WHEN** an image is built from a backup of database `Shop` that has one data file and one log file
- **THEN** the build exits with code 0
- **AND** `sqlbranch image list --format json` shows for that image the source database `Shop`, two files with logical names and sizes, and a build time

#### Scenario: Manifest of a script image
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image build shopdev --from-scripts db` runs `001_schema.sql`, `002_seed.sql` and `010_more.sql` and exits with code 0
- **THEN** `sqlbranch image list --format json` shows `shopdev` with `sourceType` "scripts", `sourceDatabase` "shopdev", three `scripts` entries with `name` and `sha256` in that order, and `backupFile` and `backupSha256` null

#### Scenario: Manifest with post-build scripts
- **WHEN** `sqlbranch image build shop --from-bak shop.bak --post-script mask.sql` exits with code 0
- **THEN** `sqlbranch image list --format json` shows `shop` with `sourceType` "backup", `backupFile` "shop.bak", its `backupSha256`, and one `postScripts` entry with `name` "mask.sql" and its `sha256`

### Requirement: List images
`sqlbranch image list` SHALL show each image's name, source, source database, total size, build time and the number of branches created from it. The source SHALL be the backup file name for a backup image and `scripts (<n> files)` for a script image.

#### Scenario: Two images, one in use
- **WHEN** images `shop` and `crm` exist and two branches were created from `shop`
- **THEN** the list shows `shop` with 2 branches and `crm` with 0 branches

#### Scenario: Script image in the list
- **WHEN** image `shop` was built from `shop.bak` and image `shopdev` from three scripts, and the user runs `sqlbranch image list`
- **THEN** the command exits with code 0
- **AND** the source column shows `shop.bak` for `shop` and `scripts (3 files)` for `shopdev`

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
