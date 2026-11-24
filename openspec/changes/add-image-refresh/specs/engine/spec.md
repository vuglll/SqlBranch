## ADDED Requirements

### Requirement: Status reports interrupted version work
On a pool that uses format 2, `sqlbranch status` SHALL also list, as mismatches with their kind and image name, the files of an unpublished version and of a removed or pruned version whose command is no longer running, and SHALL exit with code 1 when it lists any.

#### Scenario: Interrupted version operations
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, and an unpublished version of shop and the files of a removed crm version remain after the commands that made them were killed
- **THEN** `sqlbranch status` exits with code 1 and lists each leftover with its kind (unpublished version, removed version) and its image name

#### Scenario: Publication still running
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, a replacement of shop is waiting to publish in another process, and nothing else is out of place
- **THEN** `sqlbranch status` exits with code 0 and shows the build of shop as in progress

### Requirement: Commands that need pool format 2
On a pool that uses format 1, `image build --replace`, `image import --replace`, `image list --versions`, `image prune` and `branch reset --latest` MUST exit with code 3 and `error.code` "pool_upgrade_required", change nothing, and name `sqlbranch pool upgrade --yes` and that SqlBranch 0.2.x tools cannot use an upgraded pool. Input and configuration checks (exit 2), `not_initialized` and `engine_stopped` SHALL come first.

#### Scenario: Prune on a format-1 pool
- **WHEN** the pool uses format 1 and the user runs `sqlbranch image prune --keep 1`
- **THEN** the command exits with code 3, `error.code` is "pool_upgrade_required", the message names `sqlbranch pool upgrade --yes` and SqlBranch 0.2.x, and no file in the pool changes

#### Scenario: Bad count beats the format check
- **WHEN** the pool uses format 1 and the user runs `sqlbranch image prune --keep 0`
- **THEN** the command exits with code 2 and `error.code` is "usage_error"

#### Scenario: Stopped engine beats the format check
- **WHEN** the pool uses format 1, the engine was stopped, and the user runs `sqlbranch branch reset dev1 --latest`
- **THEN** the command exits with code 3 and `error.code` is "engine_stopped"

#### Scenario: Replacement on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1 with image shop, and the user runs `sqlbranch image build shop --from-bak next.bak --replace`
- **THEN** the command exits with code 3, `error.code` is "pool_upgrade_required", no restore runs, and `sqlbranch status --format json` still reports `poolFormat` 1
