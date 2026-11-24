## MODIFIED Requirements

### Requirement: Status reports mismatches
`sqlbranch status` SHALL list every branch folder with no attached database, every attached database with files in the branches area of the pool but no branch folder, the leftovers of a reset, image build, unpublished version or version removal that is no longer running, a `sqlbranch_build_<id>` login and a build's storage credential among them, every image that was only partly removed, and an unfinished pool upgrade, and SHALL exit with code 1 when it lists any.

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

#### Scenario: Interrupted version operations
- **WHEN** the clone mode is `thin` or `copy`, and an unpublished version of shop, the files of a removed crm version and a reset workspace of `dev1` remain after the commands that made them were killed
- **THEN** `sqlbranch status` exits with code 1 and lists each leftover with its kind (unpublished version, removed version, reset) and its path

#### Scenario: Upgrade interrupted
- **WHEN** the client running `sqlbranch pool upgrade` was killed after it rewrote the credentials file and before it finished
- **THEN** `sqlbranch status` exits with code 1, says the pool upgrade did not finish, and names `sqlbranch pool upgrade`

## ADDED Requirements

### Requirement: Pool format
Every pool SHALL have a format: 1 for pools written by SqlBranch 0.2.2 and for pools that `sqlbranch init` creates in this release, 2 after `sqlbranch pool upgrade`. `status` SHALL report it as `poolFormat` (integer) in JSON and as a "Pool format" line in text.

#### Scenario: Status of a new pool
- **WHEN** the user runs `sqlbranch init` and then `sqlbranch status --format json`
- **THEN** `status` exits with code 0 and `poolFormat` is 1

#### Scenario: Status after the upgrade
- **WHEN** `sqlbranch pool upgrade` exited 0 and the user runs `sqlbranch status --format json`
- **THEN** `poolFormat` is 2

### Requirement: Upgrade the pool on request
`sqlbranch pool upgrade` SHALL move a format-1 pool to format 2. It is the only command that changes the format; no other command SHALL upgrade a pool, and nothing SHALL upgrade it without that command.

#### Scenario: Refresh does not upgrade
- **WHEN** the pool uses format 1 and the user runs `sqlbranch image build shop --from-bak next.bak --replace`
- **THEN** the pool still uses format 1 afterwards

#### Scenario: Upgrade a pool with images and branches
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1 with images shop and crm and branch `dev1` of shop, and the user runs `sqlbranch pool upgrade`
- **THEN** the command exits with code 0, `sqlbranch status --format json` reports `poolFormat` 2, and shop, crm and `dev1` keep their data

### Requirement: Commands that need pool format 2
On a pool that uses format 1, `image build --replace`, `image import --replace`, `image list --versions`, `image prune` and `branch reset --latest` MUST exit with code 3 and `error.code` "pool_upgrade_required", change nothing, and name `sqlbranch pool upgrade` and that SqlBranch 0.2.x tools cannot use an upgraded pool. Input checks with exit code 2 SHALL come first.

#### Scenario: Prune on a format-1 pool
- **WHEN** the pool uses format 1 and the user runs `sqlbranch image prune --keep 1`
- **THEN** the command exits with code 3, `error.code` is "pool_upgrade_required", the message names `sqlbranch pool upgrade` and SqlBranch 0.2.x, and no file in the pool changes

#### Scenario: Bad count beats the format check
- **WHEN** the pool uses format 1 and the user runs `sqlbranch image prune --keep 0`
- **THEN** the command exits with code 2 and `error.code` is "usage_error"

### Requirement: Other commands work on both formats
Every command and option that existed in SqlBranch 0.2.2 SHALL keep working on a pool that uses format 1, with 0.2.2 behavior, and on a pool that uses format 2.

#### Scenario: Create on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, and the user runs `sqlbranch branch create dev3 --from shop`
- **THEN** the command exits with code 0 and the pool still uses format 1

### Requirement: Upgrade gives each image its first version
`pool upgrade` SHALL make each existing image the first version of its name, with the ID `<name>@<upgrade start time>` in UTC to the millisecond. A rerun after an interruption SHALL reuse the same start time, so the IDs do not change.

#### Scenario: First version IDs
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1 with images shop and crm, and `sqlbranch pool upgrade --format json` starts at 2026-12-01T09:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0 and `images` is [{"name":"crm","version":"crm@20261201T090000000Z"},{"name":"shop","version":"shop@20261201T090000000Z"}]

### Requirement: Upgrade records the base of existing branches
`pool upgrade` SHALL record each branch's first version as its base when the image exists and the branch was created at or after the image was last built or imported. A branch whose image is gone SHALL get a removed base. Any other branch SHALL get an unknown base, and the command SHALL name it.

#### Scenario: Branch older than its image
- **WHEN** the pool uses format 1, shop was last built at 2026-11-20T10:00:00Z, branch `old1` of shop was created at 2026-11-18T10:00:00Z and branch `dev1` at 2026-11-21T10:00:00Z, and the user runs `sqlbranch pool upgrade --format json`
- **THEN** the command exits with code 0, `branches.pinned` is ["dev1"] and `branches.unknownBase` is ["old1"]
- **AND** `sqlbranch branch list --format json` shows `old1` with `imageVersion` null and `outdated` null

### Requirement: Upgrade output
`pool upgrade` SHALL report the old and new format, each image with its first version ID, and the branches with a pinned, unknown or removed base. Text output SHALL also print, on standard error, a warning that the change cannot be undone and that SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x packages can no longer use the pool.

#### Scenario: Upgrade JSON
- **WHEN** the pool uses format 1 with image shop and branch `dev1`, upgraded at 2026-12-01T09:00:00.000Z (tested with a fake clock), and the user runs `sqlbranch pool upgrade --format json`
- **THEN** the command exits with code 0, `schemaVersion` is 1, `pool` is {"format":2,"previousFormat":1}, `images` is [{"name":"shop","version":"shop@20261201T090000000Z"}], and `branches` is {"pinned":["dev1"],"unknownBase":[],"removedBase":[]}

#### Scenario: Upgrade text
- **WHEN** the pool uses format 1 and the user runs `sqlbranch pool upgrade`
- **THEN** standard output names format 2 and each image's version, and standard error has a `warning:` line that says the upgrade cannot be undone and names SqlBranch 0.2.x and SqlBranch.Testing 0.2.x

### Requirement: Upgrade cannot be undone
No command SHALL move a pool from format 2 back to format 1. Running `pool upgrade` on a format-2 pool SHALL change nothing and exit 0.

#### Scenario: Upgrade twice
- **WHEN** the pool uses format 2 and the user runs `sqlbranch pool upgrade --format json`
- **THEN** the command exits with code 0, `pool` is {"format":2,"previousFormat":2}, `images` is [] and the pool is unchanged
- **AND** the text output says the pool already uses format 2

### Requirement: Upgrade waits for running operations
`pool upgrade` SHALL first rewrite the credentials file, so SqlBranch 0.2.x commands started after that fail, and then wait for running builds, imports, removals, creates, resets and branch removals, from any SqlBranch version, for at most `publishWait`. When the wait runs out it MUST exit 1 with `error.code` "catalog_busy" and name the busy image or branch.

#### Scenario: Branch create still running
- **WHEN** the pool uses format 1, `publishWait` is `10m`, and a `sqlbranch branch create dev4 --from shop` started before the upgrade keeps running for 11 minutes (tested with a fake clock)
- **THEN** `sqlbranch pool upgrade` exits with code 1 at the 10-minute mark, `error.code` is "catalog_busy", and the message names `dev4`
- **AND** `sqlbranch status` reports that the pool upgrade did not finish

### Requirement: An interrupted upgrade resumes
After a pool upgrade stops before finishing, commands that change images or branches MUST exit 3 with `error.code` "pool_upgrade_required" and say the upgrade did not finish. `image list`, `branch list` and `status` SHALL keep working. Running `pool upgrade` again SHALL finish the work, keep IDs already given, and exit 0.

#### Scenario: Killed in the middle
- **WHEN** the clone mode is `thin` or `copy`, `sqlbranch pool upgrade` started at 2026-12-01T09:00:00.000Z was killed after it recorded shop's version and before crm's, and the user runs `sqlbranch branch create dev5 --from crm`
- **THEN** the create exits with code 3 and `error.code` is "pool_upgrade_required"
- **AND** `sqlbranch pool upgrade` then exits 0, shop keeps `shop@20261201T090000000Z`, crm gets `crm@20261201T090000000Z`, and `sqlbranch status` exits 0

### Requirement: The credentials file stays private during the upgrade
The rewritten credentials file MUST stay readable and writable by the engine's user only (mode 600, owner 10001). The engine password MUST NOT appear in any container argument, environment variable, log, or the output of `pool upgrade`, and at every moment the file MUST hold either the complete old or the complete new content.

#### Scenario: Password sentinel during upgrade
- **WHEN** the engine was initialized with `SQLBRANCH_SA_PASSWORD` set to `Sentinel-Pw-12345!` and the user runs `sqlbranch pool upgrade` with a recording container runtime
- **THEN** the command exits with code 0, `/pool/engine/credentials.json` has mode 600 and owner 10001, and no recorded argument, environment value, log line or output stream contains `Sentinel-Pw-12345!`

#### Scenario: Killed while rewriting the file
- **WHEN** the client running `sqlbranch pool upgrade` is killed at each step of the credentials rewrite in turn (tested with a fake runtime)
- **THEN** after each kill `sqlbranch image list` exits with code 0, and running `sqlbranch pool upgrade` again exits 0 and leaves `/pool/engine/credentials.json` with mode 600

### Requirement: Older clients stop on an upgraded pool
On a pool that uses format 2, SqlBranch 0.2.2 commands that read the engine credentials (`init`, `engine start`, `status`, every `image build`, `image import`, `image rm`, and every `branch` command) MUST exit 1 with `error.code` "operation_failed" before changing any file, and SqlBranch.Testing 0.2.x create and reset MUST throw.

#### Scenario: Older client attempts forced removal
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has two versions with branch `dev1`, and the released 0.2.2 tool runs `sqlbranch image rm shop --force`
- **THEN** it exits with code 1, `error.code` is "operation_failed", the message is "The engine credentials file in <pool> is not valid.", and both versions and `dev1` are unchanged

#### Scenario: Older test package
- **WHEN** the pool uses format 2 and a test using SqlBranch.Testing 0.2.x calls create for image shop
- **THEN** create throws with the message "The engine credentials file in <pool> is not valid.", and the 0.2.x availability check returns false

### Requirement: Newer pool formats are refused
When the pool uses a format above 2, every command that reads the engine credentials MUST exit 1 with `error.code` "operation_failed" before changing any file, and the message SHALL name the pool's format and tell the user to update the `sqlbranch` tool and the SqlBranch.Testing package.

#### Scenario: Pool from a newer release
- **WHEN** the credentials file says format 3 and the user runs `sqlbranch branch list`
- **THEN** the command exits with code 1, `error.code` is "operation_failed", and the message names format 3 and says to update `sqlbranch` and SqlBranch.Testing

### Requirement: Upgrade needs an initialized, running engine
`pool upgrade` MUST exit with code 3 and change nothing when SqlBranch was never initialized (`error.code` "not_initialized", naming `sqlbranch init`) or the engine is stopped (`error.code` "engine_stopped", naming `sqlbranch engine start`).

#### Scenario: Upgrade before init
- **WHEN** no pool and no engine exist and the user runs `sqlbranch pool upgrade`
- **THEN** the command exits with code 3, `error.code` is "not_initialized", and the message names `sqlbranch init`

#### Scenario: Upgrade with the engine stopped
- **WHEN** the engine was stopped with `sqlbranch engine stop` and the user runs `sqlbranch pool upgrade`
- **THEN** the command exits with code 3 and `error.code` is "engine_stopped"
