# Spec Delta

## Purpose

Builds sealed, read-only database images from backup files so that branches
can be cloned from them, and lets users list and remove those images.

## ADDED Requirements

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
Each image SHALL record the source database name, the engine version that built it, each database file with its logical name and size, the original recovery model, the build time, and the backup file name and SHA-256.

#### Scenario: Manifest after a build
- **WHEN** an image is built from a backup of database `Shop` that has one data file and one log file
- **THEN** the build exits with code 0
- **AND** `sqlbranch image list --format json` shows for that image the source database `Shop`, two files with logical names and sizes, and a build time

### Requirement: Recovery model of images
An image build SHALL switch the database to the SIMPLE recovery model before sealing unless `--keep-recovery-model` is given, and the manifest SHALL record both the original model and the model the image was sealed with.

#### Scenario: Default build of a FULL-recovery database
- **WHEN** an image is built without flags from a backup of a database in FULL recovery
- **THEN** the build exits with code 0
- **AND** a branch from the image is in SIMPLE recovery
- **AND** the manifest shows original model FULL and sealed model SIMPLE

#### Scenario: Keeping the recovery model
- **WHEN** the same backup is built with `--keep-recovery-model`
- **THEN** the build exits with code 0
- **AND** a branch from the image is in FULL recovery

### Requirement: Images carry a minimal log
An image build SHALL replace the database's transaction log with a new minimal log before sealing, so that creating a branch does not rewrite the source's log, and the manifest SHALL record the original log size and that the log was rebuilt.

#### Scenario: Source with a 256 MB log
- **WHEN** an image is built from a database whose log file is 256 MB
- **THEN** the build exits with code 0
- **AND** the image's log file is smaller than 8 MiB
- **AND** the manifest shows the original log size and marks the log as rebuilt

#### Scenario: Writes after a rebuilt log
- **WHEN** the clone mode is `thin` or `copy`, a branch is created from that image, and one million rows are inserted
- **THEN** the create exits with code 0, the insert succeeds, and DBCC CHECKDB on the branch reports no errors

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
A build with the name of an existing image MUST fail without changing the pool.

#### Scenario: Name already used
- **WHEN** image `shop` exists and the user runs `sqlbranch image build shop --from-bak other.bak`
- **THEN** the command exits with code 1 and says the image already exists
- **AND** the existing image is unchanged

### Requirement: Concurrent builds of one name
When two `image build` commands with the same name run at the same time, exactly one SHALL succeed and the other SHALL exit with code 1.

#### Scenario: Same image name at the same time
- **WHEN** two processes run `sqlbranch image build shop --from-bak shop.bak` at the same moment
- **THEN** one exits with code 0 and the other with code 1
- **AND** `sqlbranch status` exits with code 0

### Requirement: Backup set selection
When a backup file holds several backup sets, the build SHALL use set 1 unless `--backup-set <n>` names another, and SHALL accept only a full database backup.

#### Scenario: Differential backup chosen
- **WHEN** the chosen backup set is a differential backup
- **THEN** the command exits with code 1, `error.code` is "unsupported_backup_type", and the message says only full database backups are supported

### Requirement: Backup from a newer SQL Server version
A build from a backup made by a newer SQL Server version than the engine MUST fail with a message naming both versions.

#### Scenario: Version mismatch
- **WHEN** the engine is SQL Server 2022 and the backup was made by SQL Server 2025
- **THEN** the command exits with code 1
- **AND** the message names both versions and says how to choose a newer engine image

### Requirement: Unsupported database features are rejected
A build from a backup whose database uses FILESTREAM or a memory-optimized filegroup MUST fail with a message naming the feature.

#### Scenario: Memory-optimized filegroup
- **WHEN** the backup's file list contains a memory-optimized filegroup
- **THEN** the command exits with code 1 and names the memory-optimized filegroup as unsupported

### Requirement: Failed builds leave nothing behind
When a build fails at any step, SqlBranch SHALL remove the temporary database and all files the build created.

#### Scenario: Restore fails halfway
- **WHEN** the clone mode is `thin` or `copy` and a build fails during restore because the pool runs out of space
- **THEN** the command exits with code 1
- **AND** `sqlbranch image list` does not show the image
- **AND** `sqlbranch status` exits with code 0 and reports no mismatches

### Requirement: List images
`sqlbranch image list` SHALL show each image's name, source database, total size, build time and the number of branches created from it.

#### Scenario: Two images, one in use
- **WHEN** images `shop` and `crm` exist and two branches were created from `shop`
- **THEN** the list shows `shop` with 2 branches and `crm` with 0 branches

### Requirement: Remove an image
`sqlbranch image rm <name>` SHALL remove the image's files and manifest, and MUST NOT delete or change any branch file.

#### Scenario: Unused image
- **WHEN** the clone mode is `thin` or `copy`, image `crm` has no branches, and the user runs `sqlbranch image rm crm`
- **THEN** the command exits with code 0 and `sqlbranch image list` no longer shows `crm`

#### Scenario: Unknown image for rm
- **WHEN** the user runs `sqlbranch image rm nope`
- **THEN** the command exits with code 1 and `error.code` is "image_not_found"

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
