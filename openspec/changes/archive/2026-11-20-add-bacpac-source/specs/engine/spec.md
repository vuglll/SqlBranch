## MODIFIED Requirements

### Requirement: Commands before init
`status`, `engine start`, `engine stop`, `teardown` and every image and branch command, `branch prune` included, MUST exit with code 3, `error.code` "not_initialized", and a message naming `sqlbranch init` when no SqlBranch engine container, managed pool file, `sqlbranch-pool` volume or marked pool directory exists. `skill export` SHALL work before init without an engine. `image import` SHALL check its archive head first (exit 1 `archive_invalid`). BACPAC builds check only exit-2 inputs first.

#### Scenario: Engine start on a new machine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch engine start`
- **THEN** the command exits with code 3 and names `sqlbranch init`

#### Scenario: Branch create on a new machine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch branch create dev1 --from shop`
- **THEN** the command exits with code 3 and `error.code` is "not_initialized"

#### Scenario: Skill export on a new machine
- **WHEN** no pool, no engine and no Docker exist and the user runs `sqlbranch skill export --dir out/skill`
- **THEN** the command exits with code 0 and `out/skill/SKILL.md` exists

#### Scenario: Import of an invalid archive before init
- **WHEN** no pool and no engine exist and the user runs `sqlbranch image import notes.txt` on a text file
- **THEN** the command exits with code 1 and `error.code` is "archive_invalid", and no `docker` command ran

#### Scenario: Readable invalid package on a new machine
- **WHEN** no engine or pool exists and `image build shop --from-bacpac notes.txt` names a readable text file
- **THEN** the command exits 3 with `not_initialized`, names `sqlbranch init` and never examines the package contents

#### Scenario: Uninitialized BACPAC URL
- **WHEN** no engine or pool exists and a well-formed BACPAC URL is supplied with valid configuration
- **THEN** the command exits 3 with `not_initialized`, names `sqlbranch init`, and makes zero storage requests and SqlPackage invocations

#### Scenario: Missing path before init
- **WHEN** no engine or pool exists and `image build shop --from-bacpac missing.bacpac` names a missing file
- **THEN** the command exits 2 with `usage_error` and makes zero Docker calls

## ADDED Requirements

### Requirement: Logical checks before initialization
Only CLI syntax, source/option combinations, image name, local input and post-script path readability, URL/region syntax, and storage credential configuration SHALL precede `not_initialized`. Tool discovery, package contents, image lookup, locks, temp creation, and downloads MUST wait for the initialization check.

#### Scenario: Invalid URL before init
- **WHEN** no engine or pool exists and the source URL contains a SAS query
- **THEN** the build exits 2 with `usage_error` before Docker or any network request and omits the query

### Requirement: BACPAC import stays within the owned build
The import SHALL target only a fresh, empty database created by this build on the verified SqlBranch engine. Imported objects MUST NOT obtain server-level rights, change another database or choose paths outside the build folder. Before detach, drop or deletion the build MUST verify ownership and scope. On collision or failed verification it MUST exit 1 with `operation_failed` and leave foreign resources unchanged.

#### Scenario: Occupied database name or unexpected file
- **WHEN** the clone mode is `thin` or `copy` and the intended temporary database already exists, or a catalog file points outside this build folder
- **THEN** the command exits 1 with `operation_failed`, publishes no image, and neither drops the existing database nor removes or replaces the outside file

### Requirement: Build-owned SqlPackage login
After reserving the image name, each BACPAC build SHALL create a uniquely named login in SqlBranch's reserved build namespace with a random one-time password and access limited to its import database. SqlBranch MUST drop it on success or failure, and sweep it after a killed build only when ownership and inactivity are verified. A failed cleanup MUST exit 1 with `operation_failed`, withhold publication and report the login name without credentials.

#### Scenario: Import login is restricted and never reused
- **WHEN** a BACPAC build imports into its fresh build database
- **THEN** SqlPackage authenticates with that build's login, which cannot access another user database or create databases, and SqlBranch drops it after success or handled failure

#### Scenario: Sweep an abandoned import login
- **WHEN** one build was killed after creating its import login and a live build owns another
- **THEN** SqlBranch sweeps only the verified abandoned login and leaves the live build and any user logins intact

### Requirement: Local BACPAC cleanup and recovery
Success, failure and cancellation SHALL remove only this operation's owned staging files, temporary database and login, preserving user inputs and other builds. If cleanup fails the command MUST exit 1 with `operation_failed`, withhold the image and name safe leftover identifiers. After interruption `status` MUST distinguish live builds from abandoned logical builds and report local leftovers without contacting Azure or displaying credentials.

#### Scenario: Interrupted import with a live neighbor
- **WHEN** the clone mode is `copy`, build A was killed during import and build B still holds its build lock
- **THEN** `status` exits 1 with `mismatch`, identifies A's local leftovers, identifies B as running, and makes no Azure requests or changes to B

#### Scenario: Local cleanup fails
- **WHEN** the clone mode is `thin` or `copy` and deletion of a private temporary BACPAC or dropping the owned import database fails
- **THEN** the command exits 1 with `operation_failed`, names the leftover without secrets, preserves `shop.bacpac` and publishes no image
- **AND** `status` exits 1 with `mismatch` and identifies the abandoned build
