## MODIFIED Requirements

### Requirement: Status reports mismatches
`sqlbranch status` SHALL list every branch folder with no attached database, every attached database with files in the branches area of the pool but no branch folder, the leftovers of a reset or image build that is no longer running, a `sqlbranch_build_<id>` login and a build's storage credential among them, every image that was only partly removed, an unfinished pool upgrade and a leftover temporary credentials file, and SHALL exit with code 1 when it lists any.

#### Scenario: Branch folder without a database
- **WHEN** a branch folder `pr-7` exists in the pool but no database `pr-7` is attached
- **THEN** `sqlbranch status` exits with code 1, lists `pr-7` as a mismatch and says the database is missing

#### Scenario: User database is not a mismatch
- **WHEN** a user created database `scratch` through the engine connection, its files are outside the pool's branches area, and nothing else is out of place
- **THEN** `sqlbranch status` exits with code 0 and does not list `scratch`

#### Scenario: Build interrupted
- **WHEN** the client running `sqlbranch image build shop` was killed during the restore
- **THEN** `sqlbranch status` exits with code 1 and lists the build's staging folder, and its temporary database when one is left

#### Scenario: Build still running
- **WHEN** `sqlbranch image build shop` is restoring in another process and nothing else is out of place
- **THEN** `sqlbranch status` exits with code 0 and shows the build as in progress

#### Scenario: Reset interrupted
- **WHEN** the client running `sqlbranch branch reset pr-142` was killed after the new files were prepared and before they were attached
- **THEN** `sqlbranch status` exits with code 1 and lists the leftover reset files of `pr-142`

#### Scenario: Login left by a killed build
- **WHEN** the client running `sqlbranch image build shopdev --from-scripts db` was killed while a script ran, its temporary database and staging folder were removed by hand, and login `sqlbranch_build_7f3a` is left
- **THEN** `sqlbranch status` exits with code 1 and lists login `sqlbranch_build_7f3a` as a build leftover
- **AND** after `sqlbranch image build crm --from-bak crm.bak` exits with code 0, `sqlbranch status` exits with code 0

#### Scenario: Credential left by a failed drop
- **WHEN** the clone mode is `copy`, a URL build from `s3://172.17.0.5:9000/backups/shop.bak` exited with code 1 because dropping its credential failed (tested with a fake connector), and nothing else is out of place
- **THEN** `sqlbranch status` exits with code 1 and lists credential `s3://172.17.0.5:9000/backups` as a build leftover without showing its secret

#### Scenario: Upgrade interrupted
- **WHEN** the client running `sqlbranch pool upgrade --yes` was killed after the engine credentials were rewritten and before the upgrade finished
- **THEN** `sqlbranch status` exits with code 1, says the pool upgrade did not finish, says that commands that change images or branches are refused until it finishes, and names `sqlbranch pool upgrade --yes`

#### Scenario: Temporary credentials file left
- **WHEN** the client running `sqlbranch pool upgrade --yes` was killed after it wrote the new credentials to a temporary file and before it replaced the old file (tested with a fake runtime)
- **THEN** `sqlbranch status` exits with code 1 and lists the temporary credentials file as an upgrade leftover without showing its content
- **AND** after `sqlbranch pool upgrade --yes` exits 0, `sqlbranch status` exits with code 0

## ADDED Requirements

### Requirement: Pool format
Every pool SHALL have a format: 1 for pools written by SqlBranch 0.2.2 and for pools that `sqlbranch init` creates in this release, 2 after `sqlbranch pool upgrade --yes`. `status` SHALL report it as `poolFormat` (integer) in JSON and as a "Pool format" line in text.

#### Scenario: Status of a new pool
- **WHEN** the user runs `sqlbranch init` and then `sqlbranch status --format json`
- **THEN** `status` exits with code 0 and `poolFormat` is 1

#### Scenario: Status after the upgrade
- **WHEN** `sqlbranch pool upgrade --yes` exited 0 and the user runs `sqlbranch status`
- **THEN** the text output has the line `Pool format: 2`, and `sqlbranch status --format json` reports `poolFormat` 2

### Requirement: Upgrade the pool on request
`sqlbranch pool upgrade --yes` SHALL move a format-1 pool to format 2. It is the only command that changes the format: no other command or option SHALL upgrade a pool.

#### Scenario: Upgrade a pool with images and branches
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1 with images shop and crm and branch `dev1` of shop holding one extra row, and the user runs `sqlbranch pool upgrade --yes`
- **THEN** the command exits with code 0, `sqlbranch status --format json` reports `poolFormat` 2, `sqlbranch image list` shows shop and crm, and `dev1` still holds its extra row

#### Scenario: Other commands do not upgrade
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, and the user runs `sqlbranch image build crm --from-bak crm.bak`, `sqlbranch branch create dev2 --from crm`, `sqlbranch branch reset dev2` and `sqlbranch image rm crm --force`
- **THEN** each exits with code 0 and `sqlbranch status --format json` still reports `poolFormat` 1

### Requirement: Upgrade input is checked before any action
`pool upgrade` MUST exit 2 with `error.code` "usage_error" and run no Docker command when `--yes` is missing, repeated or given a value, or when an operand or unknown option is given. When `--yes` is missing the message SHALL say the upgrade cannot be undone and name `--yes`.

#### Scenario: Upgrade input table
- **WHEN** the user runs `sqlbranch pool upgrade` with each of these arguments: (none), `--yes=true`, `--yes --yes`, `extra --yes`, `--force`, `--yes --force`
- **THEN** each exits with code 2, `error.code` is "usage_error", it names `--yes` or the unexpected argument, and no Docker command runs

#### Scenario: Missing confirmation
- **WHEN** the pool uses format 1 and the user runs `sqlbranch pool upgrade`
- **THEN** the command exits with code 2, the message says the upgrade cannot be undone and names `sqlbranch pool upgrade --yes`, and `sqlbranch status --format json` still reports `poolFormat` 1

### Requirement: Upgrade needs an initialized, running engine
After its input checks, `pool upgrade --yes` MUST exit with code 3 and change nothing when SqlBranch was never initialized (`error.code` "not_initialized", naming `sqlbranch init`) or the engine is stopped (`error.code` "engine_stopped", naming `sqlbranch engine start`).

#### Scenario: Upgrade before init
- **WHEN** no pool and no engine exist and the user runs `sqlbranch pool upgrade --yes`
- **THEN** the command exits with code 3, `error.code` is "not_initialized", and the message names `sqlbranch init`

#### Scenario: Upgrade with the engine stopped
- **WHEN** the engine was stopped with `sqlbranch engine stop` and the user runs `sqlbranch pool upgrade --yes`
- **THEN** the command exits with code 3, `error.code` is "engine_stopped", and `sqlbranch engine start` followed by `sqlbranch status --format json` reports `poolFormat` 1

### Requirement: Order of environment errors
When more than one environment error applies, a command SHALL report the first of: `not_initialized`, `engine_stopped`, `pool_upgrade_required`. Input and configuration errors with exit code 2 SHALL come before all three.

#### Scenario: Stopped engine during an unfinished upgrade
- **WHEN** a pool upgrade did not finish, the engine was then stopped, and the user runs `sqlbranch branch create dev5 --from crm`
- **THEN** the command exits with code 3 and `error.code` is "engine_stopped"

#### Scenario: Bad name during an unfinished upgrade
- **WHEN** a pool upgrade did not finish and the user runs `sqlbranch branch create Dev5 --from crm`
- **THEN** the command exits with code 2 and `error.code` is "invalid_name"

### Requirement: Upgrade gives each image its first version
`pool upgrade --yes` SHALL make each existing image the first version of its name, with the ID `<name>@<upgrade start time>` in UTC to the millisecond. A rerun after an interruption SHALL reuse the recorded start time, so IDs already given do not change.

#### Scenario: First version IDs
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1 with images shop and crm, and `sqlbranch pool upgrade --yes --format json` starts at 2026-12-01T09:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0 and `images` is [{"name":"crm","version":"crm@20261201T090000000Z"},{"name":"shop","version":"shop@20261201T090000000Z"}]

### Requirement: Upgrade records the base of existing branches
`pool upgrade --yes` SHALL record each branch's base as its image's first version when the image exists and the branch was created at or after the image was last built or imported on this pool. A branch whose image is gone SHALL get a removed base. Any other branch SHALL get an unknown base, and the command SHALL name it.

#### Scenario: Branch older than its image
- **WHEN** the pool uses format 1, shop was last built at 2026-11-20T10:00:00Z, branch `old1` of shop was created at 2026-11-18T10:00:00Z and branch `dev1` at 2026-11-21T10:00:00Z, and the user runs `sqlbranch pool upgrade --yes --format json` at 2026-12-01T09:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0, `branches.pinned` is ["dev1"] and `branches.unknownBase` is ["old1"]
- **AND** `sqlbranch branch list --format json` shows `old1` with `imageVersion` null and `dev1` with `imageVersion` "shop@20261201T090000000Z"

#### Scenario: Branch of a removed image
- **WHEN** the pool uses format 1, branch `pr-9` was created from crm, crm was removed with `--force`, and the user runs `sqlbranch pool upgrade --yes --format json`
- **THEN** the command exits with code 0 and `branches.removedBase` is ["pr-9"]

### Requirement: Upgrade output
`pool upgrade --yes` SHALL report the old and new format, each image with its first version ID, and the branches with a pinned, unknown or removed base. In text and in JSON format it SHALL print on standard error a `warning:` line saying the change cannot be undone and that SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x packages can no longer use the pool.

#### Scenario: Upgrade JSON
- **WHEN** the pool uses format 1 with image shop and branch `dev1`, upgraded at 2026-12-01T09:00:00.000Z (tested with a fake clock), and the user runs `sqlbranch pool upgrade --yes --format json`
- **THEN** the command exits with code 0, standard output is one JSON document with `schemaVersion` 1, `pool` {"format":2,"previousFormat":1}, `images` [{"name":"shop","version":"shop@20261201T090000000Z"}] and `branches` {"pinned":["dev1"],"unknownBase":[],"removedBase":[]}
- **AND** standard error has a `warning:` line that says the upgrade cannot be undone and names SqlBranch 0.2.x and SqlBranch.Testing 0.2.x

#### Scenario: Upgrade text
- **WHEN** the pool uses format 1 and the user runs `sqlbranch pool upgrade --yes`
- **THEN** standard output names format 2 and each image's version, and standard error has the same `warning:` line

### Requirement: Upgrade cannot be undone
No command SHALL move a pool from format 2 back to format 1. Running `pool upgrade --yes` on a finished format-2 pool SHALL change nothing and exit 0.

#### Scenario: Upgrade twice
- **WHEN** the pool uses format 2 and the user runs `sqlbranch pool upgrade --yes --format json`
- **THEN** the command exits with code 0, `pool` is {"format":2,"previousFormat":2}, `images` is [] and no file in the pool changes
- **AND** the text output of the same command says the pool already uses format 2

### Requirement: Upgrade waits for running operations
`pool upgrade --yes` SHALL first rewrite the engine credentials, so SqlBranch 0.2.x commands started after that fail, then wait for running builds, imports, removals, creates, resets and branch removals of any SqlBranch version, for at most `publishWait`. When the wait runs out it MUST exit 1 with `error.code` "catalog_busy" and name the busy image or branch.

#### Scenario: Branch create still running
- **WHEN** the pool uses format 1, `publishWait` is `10m`, and a `sqlbranch branch create dev4 --from shop` started before the upgrade keeps running for 11 minutes (tested with a fake clock)
- **THEN** `sqlbranch pool upgrade --yes` exits with code 1 at the 10-minute mark, `error.code` is "catalog_busy", and the message names `dev4` and says to run `sqlbranch pool upgrade --yes` again
- **AND** `sqlbranch status` exits with code 1 and reports that the pool upgrade did not finish

### Requirement: An interrupted upgrade blocks writes until it is rerun
While a pool upgrade has not finished, every command that changes images or branches MUST exit 3 with `error.code` "pool_upgrade_required", say the upgrade did not finish and name `sqlbranch pool upgrade --yes`. `image list`, `image export`, `branch list`, `branch conn` and `status` SHALL keep working.

#### Scenario: Create during an unfinished upgrade
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch pool upgrade --yes` was killed after it recorded shop's version and before crm's, and the user runs `sqlbranch branch create dev5 --from crm`
- **THEN** the command exits with code 3, `error.code` is "pool_upgrade_required", the message names `sqlbranch pool upgrade --yes`, and no branch `dev5` exists
- **AND** `sqlbranch image list` exits with code 0 and shows shop and crm

### Requirement: An interrupted upgrade resumes
Running `pool upgrade --yes` again after an interruption, a timeout or a full pool SHALL finish the work, keep the IDs and bases already recorded, remove any temporary credentials file the earlier run left, and exit 0.

#### Scenario: Killed in the middle
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch pool upgrade --yes` started at 2026-12-01T09:00:00.000Z was killed after it recorded shop's version and before crm's (tested with a fake clock and a fake runtime)
- **THEN** `sqlbranch pool upgrade --yes` exits 0, shop keeps `shop@20261201T090000000Z`, crm gets `crm@20261201T090000000Z`, and `sqlbranch status` exits 0
- **AND** `sqlbranch branch create dev5 --from crm` then exits 0

### Requirement: Upgrade stops cleanly when the pool is full
When the pool runs out of space during the upgrade, `pool upgrade --yes` MUST exit 1 with `error.code` "pool_full", name the available bytes, leave every image and branch with its data, and leave the upgrade unfinished so that `status` reports it and a rerun can finish it.

#### Scenario: Pool full while writing catalogs
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1 with images shop and crm, and the pool runs out of space while the upgrade records crm's version (tested with a fake runtime)
- **THEN** the command exits with code 1 and `error.code` is "pool_full", and `sqlbranch status` exits with code 1 and reports that the pool upgrade did not finish
- **AND** after space is freed, `sqlbranch pool upgrade --yes` exits with code 0 and `sqlbranch status --format json` reports `poolFormat` 2

### Requirement: The credentials file stays private during the upgrade
The rewritten engine credentials MUST stay readable and writable only by the engine's user. The engine password MUST NOT appear in any container argument, environment variable, log, or the output of `pool upgrade`, and at every moment the credentials file MUST hold either the complete old or the complete new content.

#### Scenario: Password sentinel during upgrade
- **WHEN** the engine was initialized with `SQLBRANCH_SA_PASSWORD` set to `Sentinel-Pw-12345!` and the user runs `sqlbranch pool upgrade --yes` with a recording container runtime
- **THEN** the command exits with code 0, the credentials file is readable and writable only by the engine's user, and no recorded argument, environment value, log line or output stream contains `Sentinel-Pw-12345!`

#### Scenario: Killed while rewriting the credentials
- **WHEN** the client running `sqlbranch pool upgrade --yes` is killed at each step of the credentials rewrite in turn (tested with a fake runtime)
- **THEN** after each kill `sqlbranch image list` exits with code 0, and running `sqlbranch pool upgrade --yes` again exits 0 and `sqlbranch branch conn dev1` still connects

### Requirement: Older clients stop on an upgraded pool
On a pool that uses format 2, SqlBranch 0.2.2 commands that read the engine credentials (`init`, `engine start`, `status`, every `image build`, `image import`, `image rm`, and every `branch` command) MUST exit 1 with `error.code` "operation_failed" before changing any file, and SqlBranch.Testing 0.2.x create and reset MUST throw.

#### Scenario: Older client attempts forced removal
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has branch `dev1`, and the released 0.2.2 tool runs `sqlbranch image rm shop --force`
- **THEN** it exits with code 1, `error.code` is "operation_failed", the message is "The engine credentials file in <pool> is not valid.", and shop and `dev1` are unchanged

#### Scenario: Older test package
- **WHEN** the pool uses format 2 and a test using SqlBranch.Testing 0.2.2 calls create for image shop
- **THEN** create throws with the message "The engine credentials file in <pool> is not valid.", and the 0.2.2 availability check returns false

### Requirement: Newer pool formats are refused
When the pool uses a format above 2, every command that reads the engine credentials MUST exit 1 with `error.code` "operation_failed" before changing any file, and the message SHALL name the pool's format and tell the user to update the `sqlbranch` tool and the SqlBranch.Testing package.

#### Scenario: Pool from a newer release
- **WHEN** the pool says it uses format 3 and the user runs `sqlbranch branch list`
- **THEN** the command exits with code 1, `error.code` is "operation_failed", and the message names format 3 and says to update `sqlbranch` and SqlBranch.Testing

### Requirement: Commands re-read the pool format after locking
A command that changes images or branches SHALL read the pool format again after it takes its locks and act on that value. If an upgrade started in between, it MUST exit 3 with `error.code` "pool_upgrade_required" and change nothing.

#### Scenario: Upgrade starts while a create waits for its lock
- **WHEN** the pool uses format 1, `sqlbranch branch create dev7 --from shop` has read format 1, and `sqlbranch pool upgrade --yes` rewrites the credentials before the create takes its lock (tested with a fake runtime)
- **THEN** the create exits with code 3, `error.code` is "pool_upgrade_required", no branch `dev7` exists, and the upgrade then exits 0

### Requirement: Commands from 0.2.2 work on both formats
Every command and option that existed in SqlBranch 0.2.2 SHALL keep working on a pool that uses format 1, with 0.2.2 behavior, and on a pool that uses format 2, where it reads and writes the image catalog.

#### Scenario: Create on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, and the user runs `sqlbranch branch create dev3 --from shop`
- **THEN** the command exits with code 0 and `sqlbranch status --format json` still reports `poolFormat` 1

#### Scenario: List and export on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool was upgraded with images shop and crm, crm was then removed, and the user runs `sqlbranch image list` and `sqlbranch image export shop --to shop.sbimg`
- **THEN** both exit with code 0, the list shows shop and not crm, and `sqlbranch image import shop.sbimg --as shop2` on a format-1 pool exits 0 with the same rows

### Requirement: Items written by an older client after the upgrade are adopted
On a pool that uses format 2, an image or branch that a SqlBranch 0.2.2 command wrote after the upgrade SHALL be listed, and the first command that uses it SHALL give the image a first version ID with its own start time and give the branch a base by the upgrade's rule.

#### Scenario: Image written by 0.2.2 after the upgrade
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, image `crm` was written in the 0.2.2 layout after the upgrade finished (tested by writing its files directly), and the user runs `sqlbranch branch create dev6 --from crm --format json` at 2026-12-02T08:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0 and `branch.imageVersion` is "crm@20261202T080000000Z"
- **AND** `sqlbranch image list` shows crm once

#### Scenario: Branch written by 0.2.2 after the upgrade
- **WHEN** the clone mode is `thin` or `copy`, the pool was upgraded at 2026-12-01T09:00:00.000Z giving shop `shop@20261201T090000000Z`, and branch `dev8` of shop was written in the 0.2.2 layout at 2026-12-01T10:00:00Z (tested by writing its record directly)
- **THEN** `sqlbranch branch reset dev8 --format json` exits with code 0 and `branch.imageVersion` is "shop@20261201T090000000Z"
