# branches Specification

## Purpose
Creates and manages branches: writable SQL Server databases cloned from an
image, isolated from the image and from each other, that can be reset or
removed at any time.

## Requirements

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
`branch rm`, `branch reset` and `branch prune` MUST act only on a branch that SqlBranch created, and MUST NOT drop or change any other database, including system databases, image build databases and databases created by users.

#### Scenario: Database a user created
- **WHEN** a user created database `scratch` through the engine connection and runs `sqlbranch branch rm scratch`
- **THEN** the command exits with code 1 and says `scratch` is not a branch
- **AND** database `scratch` still exists

#### Scenario: Prune with a user database present
- **WHEN** a user created database `scratch` through the engine connection, expired short-lived branch `t-old` exists, and the user runs `sqlbranch branch prune`
- **THEN** the command exits with code 0 and database `scratch` still exists

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
`sqlbranch branch list` SHALL show each branch's name, source image, clone mode, creation time, state, the space its own changed data uses, whether it is short-lived, its expiry time, whether it has expired, and its labels. The state SHALL be one of `online`, `offline` or `broken`. Expiry is a separate flag, not a state.

#### Scenario: Branch whose image was removed
- **WHEN** the clone mode is `thin` or `copy`, branch `pr-142` was created from image `shop`, and the image was removed with `--force`
- **THEN** `sqlbranch branch list` exits with code 0
- **AND** the list shows `pr-142` with image `shop` marked as removed and state `online`

#### Scenario: Ordinary and short-lived branches in JSON
- **WHEN** ordinary branch `dev1` and short-lived branch `tmp1` that expired at 2026-12-01T10:30:00Z exist, the time is 2026-12-01T11:00:00Z (tested with a fake clock), and the user runs `sqlbranch branch list --format json`
- **THEN** `dev1` has `ephemeral` false, `expiresAt` null, `expired` false and `labels` {}
- **AND** `tmp1` has `ephemeral` true, `expiresAt` "2026-12-01T10:30:00Z", `expired` true and state `online`

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

### Requirement: Short-lived branches
`sqlbranch branch create` SHALL accept `--ephemeral` with an optional `--ttl <duration>`, and SHALL record the branch as short-lived with an expiry time equal to the creation time plus the time limit. The default time limit SHALL be 1 hour. A duration is a whole number followed by `m`, `h` or `d`.

#### Scenario: Branch with a 30-minute limit
- **WHEN** the clone mode is `thin` or `copy` and the user runs `sqlbranch branch create tmp1 --from shop --ephemeral --ttl 30m` at 2026-12-01T10:00:00Z (tested with a fake clock)
- **THEN** the command exits with code 0
- **AND** `sqlbranch branch list --format json` shows `tmp1` with `ephemeral` true and `expiresAt` "2026-12-01T10:30:00Z"

#### Scenario: Default time limit
- **WHEN** the user runs `sqlbranch branch create tmp2 --from shop --ephemeral` at 2026-12-01T10:00:00Z (tested with a fake clock)
- **THEN** `sqlbranch branch list --format json` shows `tmp2` with `expiresAt` "2026-12-01T11:00:00Z"

### Requirement: Time limit values are checked
`branch create` MUST exit with code 2 and `error.code` "usage_error", before running any SQL or touching any file, when `--ttl` is not a positive whole number followed by `m`, `h` or `d`, when it is longer than 365d, or when `--ttl` is given without `--ephemeral`.

#### Scenario: Unknown unit
- **WHEN** the user runs `sqlbranch branch create tmp1 --from shop --ephemeral --ttl 30x`
- **THEN** the command exits with code 2, names `--ttl` and no branch `tmp1` exists

#### Scenario: Zero limit
- **WHEN** the user runs `sqlbranch branch create tmp1 --from shop --ephemeral --ttl 0`
- **THEN** the command exits with code 2 and names `--ttl`

#### Scenario: Negative limit
- **WHEN** the user runs `sqlbranch branch create tmp1 --from shop --ephemeral --ttl -5m`
- **THEN** the command exits with code 2 and names `--ttl`

#### Scenario: Limit above a year
- **WHEN** the user runs `sqlbranch branch create tmp1 --from shop --ephemeral --ttl 366d`
- **THEN** the command exits with code 2 and names `--ttl` and the 365d maximum

#### Scenario: Limit without ephemeral
- **WHEN** the user runs `sqlbranch branch create tmp1 --from shop --ttl 30m`
- **THEN** the command exits with code 2 and names `--ephemeral`

### Requirement: Labels on create
`sqlbranch branch create` SHALL accept `--label key=value`, which may be given several times, and SHALL store the labels with the branch. A later `--label` with the same key SHALL replace the earlier value.

#### Scenario: Two labels
- **WHEN** the user runs `sqlbranch branch create a --from shop --label run=17 --label team=web`
- **THEN** the command exits with code 0
- **AND** `sqlbranch branch list --format json` shows `a` with `labels` {"run": "17", "team": "web"}

### Requirement: Label values are checked
A label key MUST be 1 to 63 characters of lowercase letters, digits, `.`, `-` and `_`, starting with a letter or digit. A value MUST be at most 256 characters with no control characters. When setting labels with `branch create`, a key MUST NOT start with `sqlbranch.`, which is reserved for SqlBranch. A bad `--label` MUST exit with code 2 and `error.code` "usage_error" before any action.

#### Scenario: No equals sign
- **WHEN** the user runs `sqlbranch branch create a --from shop --label run17`
- **THEN** the command exits with code 2, names `--label` and no branch `a` exists

#### Scenario: Bad key
- **WHEN** the user runs `sqlbranch branch create a --from shop --label "Run Id=17"`
- **THEN** the command exits with code 2 and states the label key rule

#### Scenario: Reserved key prefix
- **WHEN** the user runs `sqlbranch branch create a --from shop --label sqlbranch.owner=testing`
- **THEN** the command exits with code 2 and says the `sqlbranch.` prefix is reserved

### Requirement: Filter the list by label
`sqlbranch branch list` SHALL accept `--label key=value`, which may be given several times, and SHALL then show only branches that carry every given label with exactly that value. Keys starting with `sqlbranch.` SHALL be allowed in the filter. A `--label` that breaks the key or value rules MUST exit with code 2.

#### Scenario: Filter by label
- **WHEN** branches `a` with label `run=17` and `b` with label `run=18` exist and the user runs `sqlbranch branch list --label run=17`
- **THEN** the command exits with code 0 and only `a` is listed

#### Scenario: Filter on the fixture's owner label
- **WHEN** fixture branch `t-1a2b3c4d`, created by the testing library, and ordinary branch `dev1` exist, and the user runs `sqlbranch branch list --label sqlbranch.owner=testing`
- **THEN** the command exits with code 0 and only `t-1a2b3c4d` is listed

#### Scenario: No match
- **WHEN** no branch carries label `run=99` and the user runs `sqlbranch branch list --label run=99 --format json`
- **THEN** the command exits with code 0 and `branches` is an empty list

### Requirement: Expired branches keep working until pruned
An expired short-lived branch SHALL stay usable until it is pruned or removed, and `sqlbranch branch list` SHALL show it with `expired` true.

#### Scenario: Expired but not pruned
- **WHEN** the clone mode is `thin` or `copy`, branch `tmp1` expired at 10:30 UTC, the time is 10:31 UTC (tested with a fake clock) and no prune has run
- **THEN** a query against `tmp1` still succeeds
- **AND** `sqlbranch branch list --format json` shows `tmp1` with state `online` and `expired` true

### Requirement: Reset keeps the expiry
`sqlbranch branch reset` SHALL NOT change whether a branch is short-lived, its expiry time or its labels.

#### Scenario: Reset of a short-lived branch
- **WHEN** the clone mode is `thin` or `copy`, branch `tmp1` was created with `--ephemeral --ttl 30m` at 10:00 UTC, and the user runs `sqlbranch branch reset tmp1` at 10:20 UTC (tested with a fake clock)
- **THEN** the command exits with code 0
- **AND** `sqlbranch branch list --format json` still shows `tmp1` with `ephemeral` true and `expiresAt` at 10:30 UTC

### Requirement: Prune expired branches
`sqlbranch branch prune` SHALL remove every branch whose record has `ephemeral` true and an expiry time in the past, whatever its state, in the same way as `branch rm`: it closes open connections, removes the database and deletes the branch's files. It SHALL report the names removed.

#### Scenario: Mixed branches
- **WHEN** the clone mode is `thin` or `copy`, expired short-lived branch `t-old`, unexpired short-lived branch `t-new` and ordinary branch `dev1` exist, and the user runs `sqlbranch branch prune`
- **THEN** the command exits with code 0 and reports `t-old` as removed
- **AND** `sqlbranch branch list` shows `t-new` and `dev1` and not `t-old`

#### Scenario: Expired branch with an open connection
- **WHEN** the clone mode is `thin` or `copy`, a client holds an open connection to expired short-lived branch `t-old`, and the user runs `sqlbranch branch prune`
- **THEN** the command exits with code 0 and the pool has no files for `t-old`

#### Scenario: Expired branch that is broken
- **WHEN** the clone mode is `thin`, expired short-lived branch `t-old` is `broken` after a failed reset, and the user runs `sqlbranch branch prune`
- **THEN** the command exits with code 0, reports `t-old` as removed and `sqlbranch status` reports no mismatches

### Requirement: Prune touches nothing else
`branch prune` MUST NOT remove or change ordinary branches, unexpired short-lived branches, images, image build databases or user databases. It MUST skip a branch whose create, reset or removal is running at that moment, and SHALL list it as skipped.

#### Scenario: User database and image left alone
- **WHEN** image `shop`, user database `scratch` and no expired branch exist and the user runs `sqlbranch branch prune`
- **THEN** the command exits with code 0
- **AND** `sqlbranch image list` still shows `shop` and database `scratch` still exists

#### Scenario: Branch being reset
- **WHEN** the clone mode is `thin` or `copy`, expired short-lived branch `t-old` is being reset by another process, and the user runs `sqlbranch branch prune --format json`
- **THEN** the command exits with code 0, `removed` does not contain `t-old` and `skipped` contains `t-old`
- **AND** the reset in the other process exits with code 0

### Requirement: Prune continues after a failure
When one removal fails, `branch prune` SHALL go on with the other expired branches, and SHALL then exit with code 1 and name each leftover, as `branch rm` does for a folder it could not delete.

#### Scenario: One folder cannot be deleted
- **WHEN** the clone mode is `thin` or `copy`, expired short-lived branches `t-a` and `t-b` exist, and deleting `/pool/branches/t-a` fails
- **THEN** the command exits with code 1 and names `/pool/branches/t-a`
- **AND** `t-b` is removed
- **AND** `sqlbranch status` exits with code 1 and lists `t-a` as a folder with no database

### Requirement: Prune output
With nothing expired, `branch prune` SHALL exit with code 0 and report that nothing was removed. Its JSON document SHALL contain `removed` (names), `skipped` (names) and `failed` (objects with `name`, `path` and `message`), each an empty list when there is nothing to report.

#### Scenario: Nothing to prune
- **WHEN** no expired short-lived branch exists and the user runs `sqlbranch branch prune --format json`
- **THEN** the command exits with code 0 and `removed`, `skipped` and `failed` are empty lists
