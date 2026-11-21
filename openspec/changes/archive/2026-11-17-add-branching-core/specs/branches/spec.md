# Spec Delta

## Purpose

Creates and manages branches: writable SQL Server databases cloned from an
image, isolated from the image and from each other, that can be reset or
removed at any time.

## ADDED Requirements

### Requirement: Create a branch
`sqlbranch branch create <name> --from <image>` SHALL create a writable database named `<name>` on the engine holding the image's data, and SHALL report the clone mode used and how to connect.

#### Scenario: Successful create
- **WHEN** the clone mode is `thin` or `copy`, image `shop` exists, and the user runs `sqlbranch branch create pr-142 --from shop`
- **THEN** the command exits with code 0
- **AND** the output shows the clone mode and how to get the connection string for database `pr-142`
- **AND** a query through the string printed by `sqlbranch branch conn pr-142` returns the image's rows

#### Scenario: Unknown image
- **WHEN** the user runs `sqlbranch branch create pr-142 --from missing`
- **THEN** the command exits with code 1, `error.code` is "image_not_found", and the message says image `missing` does not exist

### Requirement: Failed creates leave nothing behind
When `branch create` fails after it has started creating files or attaching the database, SqlBranch SHALL detach and drop any database it attached and delete the branch folder before exiting with code 1.

#### Scenario: Attach fails after the clone
- **WHEN** the clone mode is `thin` or `copy`, the files of branch `pr-142` are cloned, and attaching the database fails
- **THEN** the command exits with code 1
- **AND** no database `pr-142` is attached and no folder for `pr-142` remains in the pool
- **AND** `sqlbranch status` reports no mismatches

#### Scenario: Pool fills up during a copy
- **WHEN** the clone mode is `copy` and the pool runs out of space while the files of branch `dev1` are being copied
- **THEN** the command exits with code 1 and no folder for `dev1` remains in the pool

### Requirement: Thin branches use almost no extra space
In `thin` mode, creating a branch MUST NOT copy the image's data: pool used space SHALL grow by less than 16 MiB per branch, whatever the image size.

#### Scenario: Three branches from a 1 GiB image
- **WHEN** the clone mode is `thin`, an image of 1 GiB exists, and three branches are created from it
- **THEN** each create exits with code 0
- **AND** pool used space has grown by less than 48 MiB in total

### Requirement: Copy mode makes full copies
In `copy` mode, creating a branch SHALL make full copies of the image files and SHALL report the branch's clone mode as `copy`.

#### Scenario: Create in copy mode
- **WHEN** the clone mode is `copy` and the user creates branch `dev1` from image `shop`
- **THEN** the command exits with code 0 and reports clone mode `copy`
- **AND** the branch is readable and writable

### Requirement: Copy mode checks free space first
Before copying files, a create or reset that copies files SHALL check that free pool space is at least the image's total file size, and MUST fail with exit code 1 before creating any file when it is not.

#### Scenario: Not enough space in copy mode
- **WHEN** the clone mode is `copy`, image `shop` is 10 GiB, free pool space is 4 GiB, and the user runs `sqlbranch branch create dev1 --from shop`
- **THEN** the command exits with code 1, `error.code` is "pool_full", and the message names 10 GiB needed and 4 GiB available
- **AND** no folder for `dev1` exists in the pool

### Requirement: Full copy on request
`sqlbranch branch create <name> --from <image> --copy` SHALL make full copies of the image files even when the clone mode is `thin`, and SHALL report the branch's clone mode as `copy`.

#### Scenario: Benchmark branch on a thin pool
- **WHEN** the clone mode is `thin`, an image of 1 GiB exists, and the user runs `sqlbranch branch create bench1 --from shop --copy`
- **THEN** the command exits with code 0 and reports clone mode `copy`
- **AND** pool used space has grown by at least 900 MiB

### Requirement: Explicit copies bypass requireThin
`branch create --copy` SHALL be allowed when `requireThin` is on, because the user asked for a full copy.

#### Scenario: Explicit copy with thin required
- **WHEN** `requireThin` is on, the clone mode is `thin`, and the user runs `sqlbranch branch create bench1 --from shop --copy`
- **THEN** the command exits with code 0 and reports clone mode `copy`

#### Scenario: Explicit copy on a copy-mode pool with thin required
- **WHEN** `requireThin` is on, the clone mode is `copy`, and the user runs `sqlbranch branch create bench1 --from shop --copy`
- **THEN** the command exits with code 0 and reports clone mode `copy`

### Requirement: A copy branch stays a copy
Resetting a branch that was created with `--copy` SHALL recreate it as a full copy.

#### Scenario: Reset a benchmark branch
- **WHEN** the clone mode is `thin`, branch `bench1` was created with `--copy`, and the user runs `sqlbranch branch reset bench1`
- **THEN** the command exits with code 0
- **AND** `sqlbranch branch list` still shows `bench1` with clone mode `copy`

### Requirement: Branches are isolated
Changes made in one branch MUST NOT be visible in the image or in any other branch.

#### Scenario: Two branches from one image
- **WHEN** the clone mode is `thin` or `copy`, branches `a` and `b` are created from image `shop`, and a row is inserted into a table in `a`
- **THEN** both creates exit with code 0
- **AND** the same table in `b` does not contain that row
- **AND** a branch created from `shop` afterwards does not contain that row

### Requirement: Branch name rules
Branch names MUST be 1 to 63 characters of lowercase letters, digits, hyphen and underscore, starting with a letter or digit, MUST NOT be `master`, `model`, `msdb` or `tempdb`, and MUST NOT start with `sqlbranch_`.

#### Scenario: Reserved name
- **WHEN** the user runs `sqlbranch branch create master --from shop`
- **THEN** the command exits with code 2 and says the name is reserved

#### Scenario: Reserved prefix
- **WHEN** the user runs `sqlbranch branch create sqlbranch_build_x --from shop`
- **THEN** the command exits with code 2 and says the `sqlbranch_` prefix is reserved

### Requirement: Names are checked before any action
Every command that takes a branch name (`create`, `conn`, `reset`, `rm`) MUST apply the branch name rules and exit with code 2 before running any SQL or touching any file when the name breaks them.

#### Scenario: Path in a branch name
- **WHEN** the user runs `sqlbranch branch rm ../images/shop`
- **THEN** the command exits with code 2 and states the naming rule
- **AND** no database is dropped and no file in the pool is changed

### Requirement: Branch commands act only on branches
`branch rm` and `branch reset` MUST act only on a branch that SqlBranch created, and MUST NOT drop or change any other database, including system databases, image build databases and databases created by users.

#### Scenario: Database a user created
- **WHEN** a user created database `scratch` through the engine connection and runs `sqlbranch branch rm scratch`
- **THEN** the command exits with code 1 and says `scratch` is not a branch
- **AND** database `scratch` still exists

### Requirement: Duplicate branch names are refused
Creating a branch whose name is already in use MUST fail without changing the existing branch.

#### Scenario: Name already used
- **WHEN** branch `pr-142` exists and the user runs `sqlbranch branch create pr-142 --from shop`
- **THEN** the command exits with code 1 and says the branch already exists
- **AND** data previously written to `pr-142` is still present

#### Scenario: Name used by a user database
- **WHEN** a user created database `scratch` through the engine connection and runs `sqlbranch branch create scratch --from shop`
- **THEN** the command exits with code 1 and says a database named `scratch` already exists
- **AND** database `scratch` is unchanged

### Requirement: Concurrent creates are safe
When two `branch create` commands run at the same time, both SHALL succeed if their names differ, and exactly one SHALL succeed if their names are the same.

#### Scenario: Same name at the same time
- **WHEN** the clone mode is `thin` or `copy` and two processes run `sqlbranch branch create pr-9 --from shop` at the same moment
- **THEN** one exits with code 0 and the other with code 1
- **AND** `sqlbranch status` exits with code 0 and reports no mismatches

### Requirement: List branches
`sqlbranch branch list` SHALL show each branch's name, source image, clone mode, creation time, state and the space its own changed data uses. The state SHALL be one of `online`, `offline` or `broken`.

#### Scenario: Branch whose image was removed
- **WHEN** the clone mode is `thin` or `copy`, branch `pr-142` was created from image `shop`, and the image was removed with `--force`
- **THEN** `sqlbranch branch list` exits with code 0
- **AND** the list shows `pr-142` with image `shop` marked as removed and state `online`

### Requirement: Connection information
`sqlbranch branch conn <name>` SHALL print a connection string, including the engine password, that works from the machine running the CLI, in text output and as `connectionString` in JSON output.

#### Scenario: Connect with the printed string
- **WHEN** branch `pr-142` exists and the user runs `sqlbranch branch conn pr-142`
- **THEN** the command exits with code 0
- **AND** a connection opened with the printed string is in database `pr-142`

#### Scenario: Unknown branch for conn
- **WHEN** the user runs `sqlbranch branch conn nope`
- **THEN** the command exits with code 1 and `error.code` is "branch_not_found"

### Requirement: Reset a branch
`sqlbranch branch reset <name>` SHALL close open connections to the branch and return it to the exact state of its image, keeping its name and connection string.

#### Scenario: Reset after changes
- **WHEN** the clone mode is `thin` or `copy`, a table is dropped in branch `pr-142`, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 0
- **AND** the table exists again with the image's rows
- **AND** the connection string from before the reset still works

### Requirement: Reset checks before it drops
`branch reset` SHALL confirm that the image exists, check free space when it copies files, and prepare the new files before it drops the old database. A failure in any of these steps MUST leave the branch as it was.

#### Scenario: Reset when the image is gone
- **WHEN** the clone mode is `thin` or `copy`, the branch's image was removed, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 1 and says the image no longer exists
- **AND** the branch is online with its data unchanged

#### Scenario: Reset of a copy branch without space
- **WHEN** branch `bench1` was created with `--copy` from a 10 GiB image, free pool space is 4 GiB, and the user runs `sqlbranch branch reset bench1`
- **THEN** the command exits with code 1 and `error.code` is "pool_full"
- **AND** `bench1` is online with its data unchanged

### Requirement: A reset that fails after the drop marks the branch broken
If attaching the new files fails after the old database was dropped, `branch reset` SHALL keep the branch's record, exit with code 1, and `branch list` SHALL show the branch as `broken`.

#### Scenario: Attach fails during reset
- **WHEN** the clone mode is `thin`, the old database of `pr-142` is dropped, and attaching the new files fails
- **THEN** the command exits with code 1
- **AND** `sqlbranch branch list` shows `pr-142` as `broken`

### Requirement: A broken branch can be reset again
Running `branch reset` on a `broken` branch SHALL retry the reset from its image.

#### Scenario: Retry after a failed reset
- **WHEN** the clone mode is `thin`, branch `pr-142` is `broken`, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 0 and `sqlbranch branch list` shows `pr-142` as `online`

### Requirement: Remove a branch
`sqlbranch branch rm <name>` SHALL close open connections, remove the database and delete the branch's files.

#### Scenario: Remove with an open connection
- **WHEN** the clone mode is `thin` or `copy`, a client holds an open connection to branch `pr-142`, and the user runs `sqlbranch branch rm pr-142`
- **THEN** the command exits with code 0
- **AND** `sqlbranch branch list` no longer shows `pr-142`
- **AND** the pool has no files for `pr-142`

#### Scenario: Unknown branch
- **WHEN** the user runs `sqlbranch branch rm nope`
- **THEN** the command exits with code 1 and `error.code` is "branch_not_found"

### Requirement: Remove reports a folder it could not delete
If the database is dropped but the branch folder cannot be deleted, `branch rm` SHALL exit with code 1 and name the folder, and `status` SHALL then report the folder as a mismatch.

#### Scenario: Folder delete fails
- **WHEN** the database of `pr-142` is dropped and deleting `/pool/branches/pr-142` fails
- **THEN** the command exits with code 1 and names `/pool/branches/pr-142`
- **AND** `sqlbranch status` exits with code 1 and lists `pr-142` as a folder with no database
