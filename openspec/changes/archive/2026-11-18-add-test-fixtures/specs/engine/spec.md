# Spec Delta

## MODIFIED Requirements

### Requirement: Status report
`sqlbranch status` SHALL report whether the engine is running, the engine version and edition, the pool location and backend, the clone mode, the number of images and branches, the number and names of expired short-lived branches, and the pool's total, used and free space. Expired branches SHALL NOT count as mismatches.

#### Scenario: Status with the engine stopped
- **WHEN** the engine container is stopped and the user runs `sqlbranch status`
- **THEN** the command exits with code 3
- **AND** the output reports the engine as stopped and still shows the pool location

#### Scenario: Status with an expired branch
- **WHEN** the clone mode is `thin` or `copy`, short-lived branch `t-old` has expired, nothing else is out of place, and the user runs `sqlbranch status --format json` (tested with a fake clock)
- **THEN** the command exits with code 0
- **AND** `branches` is still the total count and `expiredBranches` is ["t-old"], and the text output names `sqlbranch branch prune`

### Requirement: Commands before init
`status`, `engine start`, `engine stop`, `teardown` and every image and branch command, `branch prune` included, MUST exit with code 3, `error.code` "not_initialized", and a message naming `sqlbranch init` when no SqlBranch engine container, managed pool file, `sqlbranch-pool` volume or marked pool directory exists. `skill export` needs no engine and SHALL work before init.

#### Scenario: Engine start on a new machine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch engine start`
- **THEN** the command exits with code 3 and names `sqlbranch init`

#### Scenario: Branch create on a new machine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch branch create dev1 --from shop`
- **THEN** the command exits with code 3 and `error.code` is "not_initialized"

#### Scenario: Skill export on a new machine
- **WHEN** no pool, no engine and no Docker exist and the user runs `sqlbranch skill export --dir out/skill`
- **THEN** the command exits with code 0 and `out/skill/SKILL.md` exists

### Requirement: Status reports mismatches
`sqlbranch status` SHALL list every branch folder with no attached database, every attached database with files in the branches area of the pool but no branch folder, the leftovers of a reset or image build that is no longer running, a `sqlbranch_build_<id>` login among them, and every image that was only partly removed, and SHALL exit with code 1 when it lists any.

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

