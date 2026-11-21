# Spec Delta

## ADDED Requirements

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

## MODIFIED Requirements

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

### Requirement: Branch commands act only on branches
`branch rm`, `branch reset` and `branch prune` MUST act only on a branch that SqlBranch created, and MUST NOT drop or change any other database, including system databases, image build databases and databases created by users.

#### Scenario: Database a user created
- **WHEN** a user created database `scratch` through the engine connection and runs `sqlbranch branch rm scratch`
- **THEN** the command exits with code 1 and says `scratch` is not a branch
- **AND** database `scratch` still exists

#### Scenario: Prune with a user database present
- **WHEN** a user created database `scratch` through the engine connection, expired short-lived branch `t-old` exists, and the user runs `sqlbranch branch prune`
- **THEN** the command exits with code 0 and database `scratch` still exists
