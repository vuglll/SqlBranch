## MODIFIED Requirements

### Requirement: Reset a branch
`sqlbranch branch reset <name>` SHALL close open connections to the branch and return it to the exact state of its base, keeping its name and connection string. On a pool that uses format 2 the base is the image version the branch was created from; on a pool that uses format 1 it is the image.

#### Scenario: Reset after changes
- **WHEN** the clone mode is `thin` or `copy`, a table is dropped in branch `pr-142`, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 0
- **AND** the table exists again with the image's rows
- **AND** the connection string from before the reset still works

#### Scenario: Reset on a format-2 pool keeps the base
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `dev1` was created from `shop@20261201T090000000Z` and has local edits, and the user runs `sqlbranch branch reset dev1 --format json`
- **THEN** the command exits with code 0, `dev1` holds the rows of `shop@20261201T090000000Z`, and `branch.imageVersion` is "shop@20261201T090000000Z"

### Requirement: Reset checks before it drops
`branch reset` SHALL confirm that its base exists (on a pool that uses format 1, the image; on a pool that uses format 2, the recorded base version), check free space when it copies files, and prepare the new files before it drops the old database. A failure in any of these steps MUST leave the branch as it was.

#### Scenario: Reset when the image is gone
- **WHEN** the clone mode is `thin` or `copy`, the branch's image was removed, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 1 and says the image no longer exists
- **AND** the branch is online with its data unchanged

#### Scenario: Reset of a copy branch without space
- **WHEN** branch `bench1` was created with `--copy` from a 10 GiB image, free pool space is 4 GiB, and the user runs `sqlbranch branch reset bench1`
- **THEN** the command exits with code 1 and `error.code` is "pool_full"
- **AND** `bench1` is online with its data unchanged

#### Scenario: Reset when the base version is gone
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `pr-142` was created from `shop@20261201T090000000Z`, shop was removed with `--force` and built again as `shop@20261203T020000000Z`, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 1 and `error.code` is "image_not_found"
- **AND** the branch is online with its data unchanged

### Requirement: A broken branch can be reset again
Running `branch reset` on a `broken` branch SHALL retry the reset to the same base, with the same removed-base and unknown-base checks as for an online branch.

#### Scenario: Retry after a failed reset
- **WHEN** the clone mode is `thin`, branch `pr-142` is `broken`, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 0 and `sqlbranch branch list` shows `pr-142` as `online`

## ADDED Requirements

### Requirement: Track the branch base
On a pool that uses format 2, `branch create` SHALL record the ID of the image's current version as the branch's base. The base stays recorded, whatever the branch's state, until a reset changes it.

#### Scenario: Create records the current version
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop's current version is `shop@20261201T090000000Z`, and the user runs `sqlbranch branch create dev2 --from shop --format json`
- **THEN** the command exits with code 0 and `branch.imageVersion` is "shop@20261201T090000000Z"

### Requirement: Plain reset refuses a removed or unknown base
On a pool that uses format 2, when the version a branch was created from was removed, or its base is unknown, `branch reset <name>` MUST exit 1 with `error.code` "image_not_found" before closing any connection, leave the branch unchanged, say that the version it was created from no longer exists, and name `sqlbranch branch rm` and `sqlbranch branch create` as the fix.

#### Scenario: Base removed and name reused
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `dev1` was created from `shop@20261201T090000000Z`, shop was removed with `--force`, `sqlbranch image build shop --from-bak shop.bak` then published `shop@20261203T020000000Z`, and `dev1` has local writes and an open connection
- **THEN** `sqlbranch branch reset dev1` exits with code 1, `error.code` is "image_not_found", and `dev1` keeps its writes and its connection can commit a row

#### Scenario: Unknown base after the upgrade
- **WHEN** the clone mode is `thin` or `copy`, branch `old1` got an unknown base from `sqlbranch pool upgrade --yes`, and has local writes
- **THEN** `sqlbranch branch reset old1` exits with code 1, `error.code` is "image_not_found", the message names `old1`, `sqlbranch branch rm` and `sqlbranch branch create`, and `old1` keeps its writes

### Requirement: Branch version output
`branch create` and `branch reset` SHALL add `imageVersion` to their JSON `branch` object, and each `branch list` row SHALL add `imageVersion`: the base version ID on a pool that uses format 2; null for an unknown base, a base removed before the upgrade, or a pool that uses format 1. On format 2, `imageRemoved` SHALL be true when the base version no longer exists, also after the name was used again. Text output SHALL show the same.

#### Scenario: Removed base in JSON
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `dev1` was created from `shop@20261119T020000000Z`, shop was then removed with `--force`, and no reset has occurred
- **THEN** `sqlbranch branch list --format json` exits 0 and the `dev1` row has `imageVersion` "shop@20261119T020000000Z", `imageRemoved` true and `state` "online"

#### Scenario: Removed base after the name was reused
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `dev1` was created from `shop@20261201T090000000Z`, shop was removed with `--force`, and `sqlbranch image build shop --from-bak shop.bak` then published `shop@20261203T020000000Z`
- **THEN** `sqlbranch branch list --format json` exits 0 and the `dev1` row has `imageVersion` "shop@20261201T090000000Z" and `imageRemoved` true

#### Scenario: Image removed before the upgrade and name reused
- **WHEN** the clone mode is `thin` or `copy`, branch `pr-9` was created from crm on a format-1 pool, crm was removed with `--force`, `sqlbranch pool upgrade --yes` reported `pr-9` in `branches.removedBase`, and `sqlbranch image build crm --from-bak crm.bak` then published a new version
- **THEN** `sqlbranch branch list --format json` shows `pr-9` with `imageVersion` null and `imageRemoved` true, and `sqlbranch branch reset pr-9` exits with code 1 and `error.code` "image_not_found"

#### Scenario: List on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, and `dev1` was created from shop
- **THEN** `sqlbranch branch list --format json` exits 0 and the `dev1` row has `imageVersion` null and `imageRemoved` false
- **AND** `sqlbranch branch create dev3 --from shop --format json` reports `branch.imageVersion` null
