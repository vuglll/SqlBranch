## MODIFIED Requirements

### Requirement: Reset a branch
`sqlbranch branch reset <name>` SHALL close open connections to the branch and return it to the exact state of its base, keeping its name and connection string. On a pool that uses format 2 the base is the image version the branch was created from or last reset to, never a newer one; on a pool that uses format 1 it is the image.

#### Scenario: Reset after changes
- **WHEN** the clone mode is `thin` or `copy`, a table is dropped in branch `pr-142`, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 0
- **AND** the table exists again with the image's rows
- **AND** the connection string from before the reset still works

#### Scenario: Reset on a format-2 pool keeps the base
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `dev1` was created from `shop@20261201T090000000Z` and has local edits, and the user runs `sqlbranch branch reset dev1 --format json`
- **THEN** the command exits with code 0, `dev1` holds the rows of `shop@20261201T090000000Z`, and `branch.imageVersion` is "shop@20261201T090000000Z"

#### Scenario: Plain reset after a refresh keeps its base
- **WHEN** the clone mode is `thin` or `copy`, `dev1` was created from `shop@20261201T020000000Z` and has local edits, and shop's current version is now `shop@20261202T020000000Z`
- **THEN** `sqlbranch branch reset dev1` exits 0, `dev1` holds the rows of `shop@20261201T020000000Z`, and `sqlbranch branch list` still shows it on that version with `outdated` true

### Requirement: Plain reset refuses a removed or unknown base
On a pool that uses format 2, when the version a branch was created from was removed, or its base is unknown, `branch reset <name>` MUST exit 1 with `error.code` "image_not_found" before closing any connection, leave the branch unchanged, say that the version it was created from no longer exists, and name `sqlbranch branch reset <name> --latest` as the way to move to the current version.

#### Scenario: Base removed and name reused
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `dev1` was created from `shop@20261201T090000000Z`, shop was removed with `--force`, `sqlbranch image build shop --from-bak shop.bak` then published `shop@20261203T020000000Z`, and `dev1` has local writes and an open connection
- **THEN** `sqlbranch branch reset dev1` exits with code 1, `error.code` is "image_not_found", the message names `sqlbranch branch reset dev1 --latest`, and `dev1` keeps its writes and its connection can commit a row

#### Scenario: Unknown base after the upgrade
- **WHEN** the clone mode is `thin` or `copy`, branch `old1` got an unknown base from `sqlbranch pool upgrade --yes`, and has local writes
- **THEN** `sqlbranch branch reset old1` exits with code 1, `error.code` is "image_not_found", the message names `sqlbranch branch reset old1 --latest`, and `old1` keeps its writes

## ADDED Requirements

### Requirement: List shows whether a branch is behind
On a pool that uses format 2, `branch list` SHALL show for each branch, online, offline, broken or expired, whether its base is behind the current version of its image. A base whose version was removed SHALL be shown as removed, also after the name was used again.

#### Scenario: Branch created before an upgrade, after the first refresh
- **WHEN** the clone mode is `thin` or `copy`, `dev1` was created from shop on a pool that uses format 1, `sqlbranch pool upgrade --yes` then gave shop the version `shop@20261201T090000000Z`, and a refresh published `shop@20261202T020000000Z`
- **THEN** `dev1` still reads its local edits, and `sqlbranch branch list --format json` shows `dev1` with `state` "online", `imageVersion` "shop@20261201T090000000Z" and `outdated` true
- **AND** a branch `dev2` created after the refresh shows `imageVersion` "shop@20261202T020000000Z" and `outdated` false

### Requirement: Behind marker output
Each `branch list` row SHALL add `outdated`: on a pool that uses format 2, true for a retained older base, false for the current version, null for a removed or unknown base; on a pool that uses format 1, false when the image exists and null when it was removed. Text output SHALL show the same.

#### Scenario: Removed base in JSON
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `dev1` was created from `shop@20261119T020000000Z`, shop was then removed with `--force`, and no reset has occurred
- **THEN** `sqlbranch branch list --format json` exits 0 and the `dev1` row has `imageVersion` "shop@20261119T020000000Z", `imageRemoved` true, `outdated` null and `state` "online"

#### Scenario: List on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, and `dev1` was created from shop
- **THEN** `sqlbranch branch list --format json` exits 0 and the `dev1` row has `imageVersion` null and `outdated` false

### Requirement: Explicit reset to latest
On a pool that uses format 2, `branch reset <name> --latest` SHALL return the branch to the image's current version and record that version as its new base, keeping its name, connection details, copy policy, labels and expiry. It SHALL also work when the old base was removed or is unknown and the name now has a current version.

#### Scenario: Name reused after forced removal
- **WHEN** the clone mode is `thin` or `copy`, `dev1` lost its base `shop@20261201T020000000Z` to a forced removal, `sqlbranch image import shop.sbimg` then published `shop@20261203T020000000Z`, and `dev1` has local writes
- **THEN** `sqlbranch branch reset dev1 --latest` exits with code 0, `dev1` holds the archive's rows, and `sqlbranch branch list` shows `dev1` on `shop@20261203T020000000Z` with `outdated` false

#### Scenario: Latest keeps the copy policy and labels
- **WHEN** the clone mode is `thin`, `bench1` was created with `--copy --label team=red`, and the user runs `sqlbranch branch reset bench1 --latest`
- **THEN** the command exits with code 0 and `sqlbranch branch list --format json` shows `bench1` as a full copy with label `team` "red" and its old expiry time

### Requirement: Latest needs a current version
When the image name has no current version, `branch reset <name> --latest` MUST exit 1 with `error.code` "image_not_found" before closing any connection, and leave the branch online with its data and no staged reset files.

#### Scenario: Import fails after forced removal
- **WHEN** the clone mode is `thin` or `copy`, shop was removed with `--force`, a new `sqlbranch image import shop.sbimg` failed, and `dev1` has local edits
- **THEN** `sqlbranch branch reset dev1 --latest` exits with code 1, `error.code` is "image_not_found", `dev1` is online with its edits, and `sqlbranch status` lists no reset leftovers

### Requirement: Reset input is checked before any action
`branch reset` MUST exit 2 before any Docker command when the branch name is missing, invalid or reserved, or when `--latest` is repeated or given a value.

#### Scenario: Invalid branch name
- **WHEN** the user runs `sqlbranch branch reset master --latest`
- **THEN** the command exits with code 2, `error.code` is "invalid_name", it names the reserved name `master`, and no Docker command runs

#### Scenario: Missing branch name
- **WHEN** the user runs `sqlbranch branch reset --latest`
- **THEN** the command exits with code 2, `error.code` is "usage_error", and no Docker command runs

#### Scenario: Value on latest
- **WHEN** the user runs `sqlbranch branch reset dev1 --latest=yes`
- **THEN** the command exits with code 2, `error.code` is "usage_error", it names `--latest`, and no Docker command runs

#### Scenario: Repeated latest
- **WHEN** the user runs `sqlbranch branch reset dev1 --latest --latest`
- **THEN** the command exits with code 2, `error.code` is "usage_error", and no Docker command runs

### Requirement: A reset failing before the drop changes nothing
When a reset fails or is cancelled before the old database is dropped, it MUST exit 1, leave the branch online on its old base with its data, and remove the files it prepared.

#### Scenario: Cannot stage latest
- **WHEN** the clone mode is `thin` or `copy` and a fault interrupts preparing the files for `sqlbranch branch reset dev1 --latest` (tested with a fake runtime)
- **THEN** the command exits with code 1, `dev1` is online on its old base with its edits, and `sqlbranch status` lists no reset leftovers

### Requirement: A reset failing after the drop keeps the old base
When a reset fails after the old database was dropped, it MUST exit 1, mark the branch `broken`, keep its old base recorded, and keep that base from being pruned until a reset succeeds. Only a successful attach SHALL record a new base. Files it could not remove SHALL be listed by `status`.

#### Scenario: Attach fails after drop
- **WHEN** the clone mode is `thin` or `copy`, `dev1` was created from `shop@20261201T020000000Z`, the current version is `shop@20261202T020000000Z`, and attaching fails during `sqlbranch branch reset dev1 --latest` (tested with a fault injected into the attach step)
- **THEN** the command exits with code 1, removes its partial attachment and prepared files, and `sqlbranch branch list` shows `dev1` as `broken` on `shop@20261201T020000000Z`
- **AND** `sqlbranch image prune --keep 1` exits 0 and keeps `shop@20261201T020000000Z`
- **AND** once the injected fault is removed, `sqlbranch branch reset dev1 --latest` exits 0, and `sqlbranch image list --versions` shows 0 branches on `shop@20261201T020000000Z`
