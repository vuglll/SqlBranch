# engine Specification

## Purpose
Sets up and runs the storage pool and the single SQL Server container that
SqlBranch owns, reports their health, detects whether the pool can clone
files without copying them, and removes everything it created on request.

## Requirements

### Requirement: Initialize pool and engine
`sqlbranch init` SHALL create the pool, start one SQL Server engine container that SqlBranch owns with the pool mounted, wait until the engine accepts connections, and report the engine version, the pool location and the clone mode.

#### Scenario: First init on a clean machine
- **WHEN** the user runs `sqlbranch init` with Docker running and no existing pool
- **THEN** the command exits with code 0
- **AND** the output shows the engine version, the pool location and a clone mode of `thin` or `copy`
- **AND** `sqlbranch status` exits with code 0 and reports the engine as running

#### Scenario: Docker is not available
- **WHEN** the user runs `sqlbranch init` and the `docker` command is missing or the daemon is not reachable
- **THEN** the command exits with code 3 and `error.code` is "docker_unavailable"
- **AND** the error names the missing prerequisite

### Requirement: Init states what it creates
`sqlbranch init` SHALL print each thing it creates on the container host: the pool file with its path and size, the mount point, and the engine container name.

#### Scenario: Managed pool on Linux
- **WHEN** `poolBackend` is `auto`, the pool directory is on ext4, and the user runs `sqlbranch init` with default settings
- **THEN** the command exits with code 0
- **AND** the output names the pool file `/var/lib/sqlbranch/pool.xfs` with size 60 GB, the mount point `/var/lib/sqlbranch/mnt`, and the container `sqlbranch-engine`

### Requirement: Init checks space for a managed pool
Before creating a managed pool file, under `poolBackend` `auto` or `loop-xfs`, `sqlbranch init` SHALL check that the container host has free space of at least `poolSize`, and MUST exit with code 3, `error.code` "host_disk_full", naming the space needed and available when it does not. It SHALL NOT fall back to copy mode for this reason.

#### Scenario: Host disk too small under loop-xfs
- **WHEN** `poolBackend` is `loop-xfs`, `poolSize` is 60 GB, and the container host has 20 GB free
- **THEN** `sqlbranch init` exits with code 3, `error.code` is "host_disk_full", and the message names 60 GB needed and 20 GB available
- **AND** no pool file exists afterwards

#### Scenario: Host disk too small under auto
- **WHEN** `poolBackend` is `auto`, the pool directory cannot share blocks, `poolSize` is 60 GB, and the container host has 20 GB free
- **THEN** `sqlbranch init` exits with code 3 and `error.code` is "host_disk_full"
- **AND** no pool file and no engine container exist afterwards

### Requirement: Init waits for the engine with a limit
`sqlbranch init` and `sqlbranch engine start` SHALL wait at most 120 seconds for the engine to accept connections, then exit with code 3, `error.code` "engine_not_ready", and the last lines of the engine log with secrets removed.

#### Scenario: Engine never becomes ready
- **WHEN** the engine container starts but does not accept connections within 120 seconds
- **THEN** the command exits with code 3 and `error.code` is "engine_not_ready"

### Requirement: A failed first init cleans up
When a first `sqlbranch init` fails, it SHALL remove the helper containers, the engine container, the mount, the pool file and the credentials it created in that run, and SHALL leave anything that existed before it started unchanged.

#### Scenario: Engine start fails after the pool is mounted
- **WHEN** the managed pool is created and mounted and starting the engine container fails
- **THEN** `sqlbranch init` exits with code 1 and `error.code` is "init_failed"
- **AND** no container labeled `io.sqlbranch.managed=true` exists, `/var/lib/sqlbranch/pool.xfs` does not exist, and nothing is mounted at `/var/lib/sqlbranch/mnt`

### Requirement: Pools carry a marker
`sqlbranch init` SHALL write a pool marker with a format version into every pool it creates, and SqlBranch SHALL treat a directory as a SqlBranch pool only when that marker is present.

#### Scenario: Marker after init
- **WHEN** `poolBackend` is `plain`, `pool` is the empty directory `/data/sb`, and the user runs `sqlbranch init`
- **THEN** the command exits with code 0 and a second `sqlbranch init` with the same settings also exits with code 0

### Requirement: Init refuses a foreign pool directory
When the pool is a directory used as it is (`plain`, or a reflink directory under `auto`), `sqlbranch init` MUST exit with code 2 and name the directory if it is not empty and has no pool marker, and MUST NOT change any file or its ownership.

#### Scenario: Pool set to a folder with other files
- **WHEN** `pool` is `/data/work`, which holds `notes.txt` and no pool marker, and the user runs `sqlbranch init`
- **THEN** the command exits with code 2 and names `/data/work`
- **AND** `notes.txt` and its owner are unchanged

#### Scenario: Folder that happens to hold an images folder
- **WHEN** `pool` is `/data/photos`, which holds only `images/photo.png`, and the user runs `sqlbranch init`
- **THEN** the command exits with code 2 and names `/data/photos`
- **AND** `images/photo.png` and its owner are unchanged

### Requirement: Init is safe to repeat
Running `sqlbranch init` when a pool and engine already exist SHALL leave existing images and branches untouched, start the engine if it is stopped, and exit with code 0.

#### Scenario: Init run twice
- **WHEN** the clone mode is `thin` or `copy`, a pool holds one image and two branches, and the user runs `sqlbranch init` again
- **THEN** the command exits with code 0
- **AND** `sqlbranch image list` still shows one image and `sqlbranch branch list` still shows two branches

### Requirement: Engine listens on loopback by default
The engine's SQL port SHALL be published on 127.0.0.1 only, on port 14330 unless the `port` setting says otherwise.

#### Scenario: Default network exposure
- **WHEN** the user runs `sqlbranch init` with no `port` setting
- **THEN** the command exits with code 0 and a connection to 127.0.0.1 port 14330 succeeds
- **AND** `docker port sqlbranch-engine` lists only `127.0.0.1:14330`

### Requirement: Clone mode detection
SqlBranch SHALL determine the clone mode by attempting a block-sharing copy of a probe file inside the pool, and SHALL report `thin` when the attempt succeeds and `copy` when it fails.

#### Scenario: Pool on XFS with reflink
- **WHEN** the pool is on an XFS file system with reflink enabled and the user runs `sqlbranch status`
- **THEN** the command exits with code 0 and the reported clone mode is `thin`

#### Scenario: Pool on a file system without reflink
- **WHEN** `poolBackend` is `plain`, the pool is on ext4, and the user runs `sqlbranch status`
- **THEN** the command exits with code 0 and the reported clone mode is `copy`
- **AND** the output says that branch creation will copy every file in full

### Requirement: Managed thin pool
When the pool location cannot share blocks and `poolBackend` is `auto` or `loop-xfs`, `sqlbranch init` SHALL create an XFS pool of size `poolSize` inside a file, mount it for the engine, and report clone mode `thin`.

#### Scenario: Docker Desktop on macOS with no settings
- **WHEN** the user runs `sqlbranch init` on Docker Desktop for macOS with default settings
- **THEN** the command exits with code 0 and reports clone mode `thin`
- **AND** `sqlbranch status` reports the pool backend as `loop-xfs`

#### Scenario: Linux host whose pool directory is on ext4
- **WHEN** the pool directory is on ext4 and `poolBackend` is `auto`
- **THEN** `sqlbranch init` exits with code 0 and reports clone mode `thin` and pool backend `loop-xfs`

### Requirement: Helper containers are limited
The privileged helper containers that create, mount and delete a managed pool MUST carry the label `io.sqlbranch.managed=true`, MUST touch only paths under `/var/lib/sqlbranch/` on the container host, MUST mount or unmount nothing else, and SHALL be removed when their step ends, whether it succeeded or failed.

#### Scenario: No helpers left after init
- **WHEN** `sqlbranch init` creates a managed pool and exits with any code
- **THEN** `docker ps -a --filter label=io.sqlbranch.managed=true` lists no container other than `sqlbranch-engine`

### Requirement: Pool work containers are limited
A container that SqlBranch starts to work on pool files while the engine is not running MUST carry the label `io.sqlbranch.managed=true`, MUST mount only the pool, MUST NOT be privileged, and SHALL be removed when its step ends.

#### Scenario: No work container left after teardown
- **WHEN** `sqlbranch teardown --force` runs on a `plain` pool and exits with any code
- **THEN** `docker ps -a --filter label=io.sqlbranch.managed=true` lists no container

### Requirement: Fallback to copy mode under auto
If a managed pool cannot be created or mounted for a reason other than host disk space and `poolBackend` is `auto`, `sqlbranch init` SHALL fall back to a directory pool in `copy` mode, state the reason and exit with code 0.

#### Scenario: Privileged containers are not allowed
- **WHEN** the container runtime refuses privileged containers and `poolBackend` is `auto`
- **THEN** `sqlbranch init` exits with code 0, reports clone mode `copy`, and says the managed pool needs a privileged helper

### Requirement: A failed managed-pool attempt leaves nothing
Before falling back to copy mode, `sqlbranch init` SHALL remove the pool file, mount and helper containers that the managed-pool attempt created.

#### Scenario: Mount fails after the file is formatted
- **WHEN** `poolBackend` is `auto`, the pool file is formatted, and mounting it fails
- **THEN** `sqlbranch init` exits with code 0 and reports clone mode `copy`
- **AND** `/var/lib/sqlbranch/pool.xfs` does not exist and no helper container remains

### Requirement: Fallback pool location
The copy-mode pool used after a fallback SHALL be the `pool` directory on Linux, and a Docker volume named `sqlbranch-pool` on Docker Desktop.

#### Scenario: Fallback on Docker Desktop
- **WHEN** `sqlbranch init` falls back to copy mode on Docker Desktop for macOS
- **THEN** the command exits with code 0 and `sqlbranch status` reports the pool location as the Docker volume `sqlbranch-pool`

### Requirement: No fallback under loop-xfs
If a managed pool cannot be created or mounted and `poolBackend` is `loop-xfs`, `sqlbranch init` MUST fail with exit code 3 and name the cause.

#### Scenario: Managed pool required but not possible
- **WHEN** the container runtime refuses privileged containers and `poolBackend` is `loop-xfs`
- **THEN** `sqlbranch init` exits with code 3 and names the cause

### Requirement: Pool is remounted after a restart
After the container host restarts, `sqlbranch engine start` and `sqlbranch init` SHALL remount a managed pool before starting the engine, and every branch SHALL come back online with its data.

#### Scenario: Docker Desktop restarted
- **WHEN** the clone mode is `thin`, a row is inserted into branch `pr-142`, Docker Desktop is restarted, and the user runs `sqlbranch engine start`
- **THEN** the command exits with code 0 and `sqlbranch branch list` shows `pr-142` as online
- **AND** the inserted row is still present

#### Scenario: Remount fails
- **WHEN** the managed pool is not mounted and remounting it fails
- **THEN** `sqlbranch engine start` exits with code 3, `error.code` is "pool_not_mounted", and the engine is not started

### Requirement: Thin mode can be required
When `requireThin` is on and the clone mode is `copy`, `sqlbranch branch create` without `--copy` and `sqlbranch branch reset` of a branch not created with `--copy` MUST fail with exit code 3 and `error.code` "thin_required" before creating any file.

#### Scenario: Branch creation refused in copy mode
- **WHEN** `requireThin` is on, the clone mode is `copy`, and the user runs `sqlbranch branch create pr-142 --from shop`
- **THEN** the command exits with code 3 and `error.code` is "thin_required"
- **AND** no branch named `pr-142` exists afterwards

#### Scenario: Reset refused in copy mode
- **WHEN** `requireThin` is on, the clone mode is `copy`, branch `pr-142` was created without `--copy`, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 3 and `pr-142` is online with its data unchanged

### Requirement: Image builds ignore requireThin
`sqlbranch image build` SHALL run whether or not `requireThin` is on, because a build writes full files in either clone mode.

#### Scenario: Image build allowed in copy mode
- **WHEN** `requireThin` is on, the clone mode is `copy`, and the user runs `sqlbranch image build shop --from-bak shop.bak` with a valid full backup
- **THEN** the command exits with code 0

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

### Requirement: Engine stop is safe to repeat
`sqlbranch engine stop` on an engine that is already stopped SHALL exit with code 0 and change nothing.

#### Scenario: Stop twice
- **WHEN** the engine is stopped and the user runs `sqlbranch engine stop`
- **THEN** the command exits with code 0

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

### Requirement: Status reports mismatches
`sqlbranch status` SHALL list every branch folder with no attached database, every attached database with files in the branches area of the pool but no branch folder, the leftovers of a reset or image build that is no longer running, a `sqlbranch_build_<id>` login and a build's storage credential among them, every image that was only partly removed, a dead, unfinished pool upgrade and its leftover temporary credentials file, and SHALL exit with code 1 when it lists any.

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

### Requirement: Engine start and stop keep branches
`sqlbranch engine stop` followed by `sqlbranch engine start` SHALL bring every branch back online with the data it had before the stop.

#### Scenario: Restart with data in a branch
- **WHEN** the clone mode is `thin` or `copy`, a row is inserted into branch `pr-142`, and the engine is stopped and then started
- **THEN** both commands exit with code 0 and `sqlbranch branch list` shows `pr-142` as online
- **AND** the inserted row is still present

### Requirement: Teardown removes everything SqlBranch created
`sqlbranch teardown --force` SHALL remove every database on the engine, the engine container, every image and branch, staged builds, import backups, the credentials and the pool marker, a managed pool's mount and file or the `sqlbranch-pool` volume, and SHALL list the user databases and import backups it removed.

#### Scenario: Teardown of a managed pool
- **WHEN** the clone mode is `thin`, a managed pool holds one image, one branch and a user database `scratch`, and the user runs `sqlbranch teardown --force`
- **THEN** the command exits with code 0 and its output lists `scratch`
- **AND** no container labeled `io.sqlbranch.managed=true` exists and `/var/lib/sqlbranch/pool.xfs` does not exist

#### Scenario: Teardown with the engine stopped
- **WHEN** the engine is stopped, a user database `scratch` exists, and the user runs `sqlbranch teardown --force`
- **THEN** the command exits with code 0, its output lists `scratch`, and no container labeled `io.sqlbranch.managed=true` exists

#### Scenario: Teardown after a host restart
- **WHEN** the container host restarted, so the managed pool is not mounted, and the user runs `sqlbranch teardown --force`
- **THEN** the command exits with code 0 and `/var/lib/sqlbranch/pool.xfs` does not exist

### Requirement: Teardown needs force
`sqlbranch teardown` without `--force` MUST exit with code 2, name `--force`, and remove nothing.

#### Scenario: Teardown without force
- **WHEN** the user runs `sqlbranch teardown`
- **THEN** the command exits with code 2, names `--force`, and nothing is removed

### Requirement: Teardown keeps foreign files
When the pool is a directory used as it is, `sqlbranch teardown --force` SHALL delete only what SqlBranch created in it and MUST NOT delete the directory itself or any other file in it.

#### Scenario: Plain pool with an extra file
- **WHEN** `poolBackend` is `plain`, the clone mode is `copy`, the pool directory also holds `readme.txt`, and the user runs `sqlbranch teardown --force`
- **THEN** the command exits with code 0
- **AND** the pool directory and `readme.txt` still exist

### Requirement: Teardown can be finished
If a step of `sqlbranch teardown --force` fails, the command SHALL exit with code 1 and name what is left, and running `sqlbranch teardown --force` again SHALL remove the rest.

#### Scenario: Unmount fails
- **WHEN** the engine is removed and unmounting the managed pool fails because the loop device is busy
- **THEN** the command exits with code 1 and names `/var/lib/sqlbranch/mnt` and `/var/lib/sqlbranch/pool.xfs`
- **AND** a second `sqlbranch teardown --force` after the device is free exits with code 0 and the file no longer exists

### Requirement: Environment check
`sqlbranch doctor` SHALL check that Docker is reachable, the engine image is available, the pool is writable, the engine port is free or held by the engine, and free pool space, and SHALL report each check as pass, warn or fail. Free space under 10 GiB SHALL warn and under 1 GiB SHALL fail.

#### Scenario: All checks pass
- **WHEN** every check passes
- **THEN** `sqlbranch doctor` exits with code 0 and lists each check as `pass`

#### Scenario: A check fails
- **WHEN** the engine port is held by another process
- **THEN** `sqlbranch doctor` exits with code 3 and marks the port check as `fail` with the port number

### Requirement: Warnings do not fail doctor
`sqlbranch doctor` SHALL exit with code 0 when no check fails, even if some checks warn.

#### Scenario: Only warnings
- **WHEN** free pool space is 6 GiB and every other check passes
- **THEN** `sqlbranch doctor` exits with code 0 and marks the space check as `warn`

### Requirement: Warn on unsupported host
`sqlbranch doctor` and `sqlbranch init` SHALL warn when the Docker host is not an x86-64 Linux host and when the clone mode is `copy`, and SHALL continue.

#### Scenario: Arm64 host
- **WHEN** the Docker host architecture is arm64 and every other check passes
- **THEN** `sqlbranch doctor` exits with code 0, marks the host check as `warn` and says the engine runs under emulation

### Requirement: Only the owned container is touched
SqlBranch MUST NOT start, stop, remove or run commands in any container that it did not create.

#### Scenario: A foreign container uses the engine's name
- **WHEN** a container named `sqlbranch-engine` exists that SqlBranch did not create and the user runs `sqlbranch init`
- **THEN** the command exits with code 3 and names the conflicting container
- **AND** that container is left running and unchanged

### Requirement: Non-production notice
`sqlbranch init` SHALL state the edition the engine runs and that it is for development and test use only.

#### Scenario: Notice at init
- **WHEN** the user runs `sqlbranch init`
- **THEN** the command exits with code 0
- **AND** the text output names the engine edition and contains a line saying the engine is for development and test use only

### Requirement: Engine trusts public storage certificates
The engine SHALL trust the public certificate authorities needed to reach Azure Blob Storage and Amazon S3 over HTTPS, so that a URL build from either service needs no certificate setup by the user.

#### Scenario: Build from Amazon S3 on a new engine
- **WHEN** the clone mode is `thin` or `copy`, the engine was created by `sqlbranch init` with default settings, and the user runs `sqlbranch image build shop --from-bak s3://acme-db.s3.eu-west-1.amazonaws.com/shop.bak --s3-region eu-west-1` with valid S3 secrets (opt-in cloud test)
- **THEN** the command exits with code 0

#### Scenario: Build from Azure Blob Storage on a new engine
- **WHEN** the clone mode is `thin` or `copy`, the engine was created by `sqlbranch init` with default settings, and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak` with a valid SAS token (opt-in cloud test)
- **THEN** the command exits with code 0

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

### Requirement: A running upgrade is shown as in progress
While `pool upgrade --yes` is still running, `sqlbranch status` SHALL show the upgrade, and its temporary files, as in progress, not as mismatches, and SHALL not exit 1 because of them. `status` SHALL only test whether the upgrade lock is held and MUST NOT take it.

#### Scenario: Upgrade still running
- **WHEN** `sqlbranch pool upgrade --yes` is waiting for a reset of `dev1` in another process and nothing else is out of place
- **THEN** `sqlbranch status` exits with code 0, shows the pool upgrade as in progress, and lists no mismatch
- **AND** with a recording connector, `status` sent no request to take the lock `sqlbranch:pool-upgrade`, and the upgrade then exits 0 after the reset ends

#### Scenario: Temporary credentials file of a running upgrade
- **WHEN** `sqlbranch pool upgrade --yes` is paused after it wrote the new credentials to a temporary file and before it replaced the old file (tested with a barrier in a fake runtime), and nothing else is out of place
- **THEN** `sqlbranch status` exits with code 0, shows the pool upgrade as in progress, and does not list the temporary file as a leftover

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
`pool upgrade` MUST exit 2 with `error.code` "usage_error" and run no Docker command when `--yes` is missing, repeated or given a value, or when an operand or unknown option is given. When `--yes` is missing the message SHALL say the upgrade cannot be undone and name `--yes`. The global options `--format`, `--config` and `--quiet` SHALL be accepted.

#### Scenario: Upgrade input table
- **WHEN** the user runs `sqlbranch pool upgrade` with each of these arguments: (none), `--yes=true`, `--yes true`, `--yes --yes`, `extra --yes`, `--force`, `--yes --force`
- **THEN** each exits with code 2, `error.code` is "usage_error", it names `--yes` or the unexpected argument, and no Docker command runs

#### Scenario: Missing confirmation
- **WHEN** the pool uses format 1 and the user runs `sqlbranch pool upgrade`
- **THEN** the command exits with code 2, the message says the upgrade cannot be undone and names `sqlbranch pool upgrade --yes`, and `sqlbranch status --format json` still reports `poolFormat` 1

#### Scenario: Global options are accepted
- **WHEN** the pool uses format 2 and the user runs `sqlbranch pool upgrade --yes --quiet --config ci.sqlbranch.yml --format json`
- **THEN** the command exits with code 0 and `pool` is {"format":2,"previousFormat":2}

### Requirement: Upgrade needs an initialized, running engine
After its input checks, `pool upgrade --yes` MUST exit with code 3 and change nothing when SqlBranch was never initialized (`error.code` "not_initialized", naming `sqlbranch init`) or the engine is stopped (`error.code` "engine_stopped", naming `sqlbranch engine start`).

#### Scenario: Upgrade before init
- **WHEN** no pool and no engine exist and the user runs `sqlbranch pool upgrade --yes`
- **THEN** the command exits with code 3, `error.code` is "not_initialized", and the message names `sqlbranch init`

#### Scenario: Upgrade with the engine stopped
- **WHEN** the engine was stopped with `sqlbranch engine stop` and the user runs `sqlbranch pool upgrade --yes`
- **THEN** the command exits with code 3, `error.code` is "engine_stopped", and `sqlbranch engine start` followed by `sqlbranch status --format json` reports `poolFormat` 1

### Requirement: Order of environment errors
When more than one environment error applies, a command SHALL report the first of: `docker_unavailable`, `not_initialized`, `engine_stopped`, `pool_upgrade_required`. Input and configuration errors with exit code 2 SHALL come before all four.

#### Scenario: Stopped engine during an unfinished upgrade
- **WHEN** a pool upgrade did not finish, the engine was then stopped, and the user runs `sqlbranch branch create dev5 --from crm`
- **THEN** the command exits with code 3 and `error.code` is "engine_stopped"

#### Scenario: Docker stopped during an unfinished upgrade
- **WHEN** a pool upgrade did not finish, Docker was then stopped, and the user runs `sqlbranch branch create dev5 --from crm`
- **THEN** the command exits with code 3 and `error.code` is "docker_unavailable"

#### Scenario: Bad name during an unfinished upgrade
- **WHEN** a pool upgrade did not finish and the user runs `sqlbranch branch create Dev5 --from crm`
- **THEN** the command exits with code 2 and `error.code` is "invalid_name"

### Requirement: Upgrade gives each image its first version
`pool upgrade --yes` SHALL make each existing image the first version of its name, with the ID `<name>@<upgrade start time>` in UTC to the millisecond. A rerun after an interruption SHALL reuse the recorded start time, so IDs already given do not change.

#### Scenario: First version IDs
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1 with images shop and crm, and `sqlbranch pool upgrade --yes --format json` starts at 2026-12-01T09:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0 and `images` is [{"name":"crm","version":"crm@20261201T090000000Z"},{"name":"shop","version":"shop@20261201T090000000Z"}]

### Requirement: Upgrade records the base of existing branches
`pool upgrade --yes` SHALL record each branch's base as its image's first version when the image exists and the branch was created at or after the build time recorded in the image's manifest (for an imported image, the time its archive was built). A branch whose image is gone SHALL get a removed base. Any other branch SHALL get an unknown base, and the command SHALL name it.

#### Scenario: Branch older than its image
- **WHEN** the pool uses format 1, shop was last built at 2026-11-20T10:00:00Z, branch `old1` of shop was created at 2026-11-18T10:00:00Z and branch `dev1` at 2026-11-21T10:00:00Z, and the user runs `sqlbranch pool upgrade --yes --format json` at 2026-12-01T09:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0, `branches.pinned` is ["dev1"] and `branches.unknownBase` is ["old1"]
- **AND** `sqlbranch branch list --format json` shows `old1` with `imageVersion` null and `dev1` with `imageVersion` "shop@20261201T090000000Z"

#### Scenario: Unreadable branch record
- **WHEN** the pool uses format 1, the `branch.json` of branch `bad1` cannot be parsed, and the user runs `sqlbranch pool upgrade --yes`
- **THEN** the command exits with code 0, the record of `bad1` is unchanged, and standard error has one warning naming `bad1` and `sqlbranch branch rm bad1` (design D14)

#### Scenario: Branch of a removed image
- **WHEN** the pool uses format 1, branch `pr-9` was created from crm, crm was removed with `--force`, and the user runs `sqlbranch pool upgrade --yes --format json`
- **THEN** the command exits with code 0 and `branches.removedBase` is ["pr-9"]

#### Scenario: Image replaced by an import of an older archive
- **WHEN** the pool uses format 1, crm was built at 2026-11-10T10:00:00Z, branch `pr-3` was created from it at 2026-11-12T10:00:00Z, crm was removed with `sqlbranch image rm crm --force` at 2026-11-14T10:00:00Z, `sqlbranch image import crm.sbimg --as crm` ran at 2026-11-15T10:00:00Z with an archive built at 2026-11-11T10:00:00Z, and the user runs `sqlbranch pool upgrade --yes --format json` at 2026-12-01T09:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0, `branches.pinned` is ["pr-3"] and `branches.removedBase` is []
- **AND** `sqlbranch branch list --format json` shows `pr-3` with `imageVersion` "crm@20261201T090000000Z"

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
`pool upgrade --yes` SHALL first rewrite the engine credentials, so SqlBranch 0.2.x commands started after that fail, then wait, for at most `publishWait`, for running operations of any SqlBranch version on existing image names and existing branches. When the wait runs out it MUST exit 1 with `error.code` "catalog_busy" and name every image and branch that is still busy.

#### Scenario: Branch create still running
- **WHEN** the pool uses format 1, `publishWait` is `10m`, and a `sqlbranch branch create dev4 --from shop` started before the upgrade, has made the branch folder of `dev4`, and keeps running for 11 minutes (tested with a fake clock)
- **THEN** `sqlbranch pool upgrade --yes` exits with code 1 at the 10-minute mark, `error.code` is "catalog_busy", and the message names both `shop` and `dev4` and says to run `sqlbranch pool upgrade --yes` again
- **AND** `sqlbranch status` exits with code 1 and reports that the pool upgrade did not finish

### Requirement: Upgrade does not wait for new names
`pool upgrade --yes` SHALL NOT wait for builds and imports of image names that have no image directory, nor for a `branch create` from such an image. A `branch create` from an existing image SHALL be waited for through that image's lock. The upgrade SHALL process only the images and branches it waited for; what the others write SHALL be listed after the upgrade and adopted by the first command that uses it.

#### Scenario: Build of a new name during the upgrade
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, `sqlbranch image build crm2 --from-bak crm.bak` is restoring when `sqlbranch pool upgrade --yes --format json` starts, and the build publishes while the upgrade waits for a reset of `dev1` (tested with barriers and a fake clock)
- **THEN** the build and the upgrade both exit 0, the upgrade's JSON `images` does not list crm2, and `sqlbranch image list --format json` shows crm2 with `version` null
- **AND** `sqlbranch branch create d2 --from crm2 --format json` at 2026-12-01T09:05:00.000Z exits 0 with `branch.imageVersion` "crm2@20261201T090500000Z", and `sqlbranch image list --format json` then shows crm2 with that `version`

### Requirement: One upgrade at a time
`pool upgrade --yes` SHALL take a pool-wide upgrade lock before it changes anything. When another `pool upgrade` holds it, the command MUST exit 1 with `error.code` "catalog_busy", say that an upgrade is running, and change nothing.

#### Scenario: Second upgrade while one runs
- **WHEN** `sqlbranch pool upgrade --yes` is waiting for a reset of `dev1` in another process and the user runs `sqlbranch pool upgrade --yes --format json`
- **THEN** the second command exits with code 1, `error.code` is "catalog_busy", the message says a pool upgrade is running, and no file in the pool changes
- **AND** the first upgrade exits 0 after the reset ends

### Requirement: The upgrade changes only pool metadata
`pool upgrade --yes` SHALL change only the engine credentials file, the image catalogs, the per-name version-ID records and the branch records. It MUST NOT change, move or delete image data files or manifests, branch data files, attached databases or logins, and MUST NOT stop, restart or reconfigure the engine container.

#### Scenario: Data untouched by the upgrade
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1 with images shop and crm and branches `dev1` (with an open connection) and `old1`, and the user runs `sqlbranch pool upgrade --yes` and then `sqlbranch pool upgrade --yes` again
- **THEN** both exit 0, and after each the SHA-256 and modification time of every image data file, every `manifest.json` and every branch data file other than `dev1`'s are unchanged, and `dev1`'s data files (open, so the engine writes them in the background) keep their names and sizes
- **AND** the engine container's start time from `docker inspect` is unchanged, the server logins are unchanged, and `dev1`'s open connection can commit a row

### Requirement: An interrupted upgrade blocks writes until it is rerun
While a pool upgrade has not finished, every command that changes images or branches and starts, or reaches its check after locking, MUST exit 3 with `error.code` "pool_upgrade_required", say the upgrade did not finish and name `sqlbranch pool upgrade --yes`. `image list`, `image export`, `branch list`, `branch conn`, `status` and `teardown --force` SHALL keep working.

#### Scenario: Create during an unfinished upgrade
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch pool upgrade --yes` was killed after it recorded crm's version and before shop's (images are processed in name order), and the user runs `sqlbranch branch create dev5 --from crm`
- **THEN** the command exits with code 3, `error.code` is "pool_upgrade_required", the message names `sqlbranch pool upgrade --yes`, and no branch `dev5` exists
- **AND** `sqlbranch image list` exits with code 0 and shows shop and crm

### Requirement: An interrupted upgrade resumes
Running `pool upgrade --yes` again after an interruption, a timeout or a full pool SHALL finish the work, keep the IDs and bases already recorded, remove any temporary credentials file the earlier run left, and exit 0.

#### Scenario: Killed in the middle
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch pool upgrade --yes` started at 2026-12-01T09:00:00.000Z was killed after it recorded crm's version and before shop's (tested with a fake clock and a fake runtime)
- **THEN** `sqlbranch pool upgrade --yes` exits 0, crm keeps `crm@20261201T090000000Z`, shop gets `shop@20261201T090000000Z`, and `sqlbranch status` exits 0
- **AND** `sqlbranch branch create dev5 --from crm` then exits 0

### Requirement: Upgrade stops cleanly when the pool is full
When the pool runs out of space after the new engine credentials replaced the old ones, `pool upgrade --yes` MUST exit 1 with `error.code` "pool_full", name the available bytes, leave every image and branch with its data, and leave the upgrade unfinished so that `status` reports it and a rerun can finish it.

#### Scenario: Pool full while writing catalogs
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1 with images shop and crm, and the pool runs out of space while the upgrade records crm's version (tested with a fake runtime)
- **THEN** the command exits with code 1 and `error.code` is "pool_full", and `sqlbranch status` exits with code 1 and reports that the pool upgrade did not finish
- **AND** after space is freed, `sqlbranch pool upgrade --yes` exits with code 0 and `sqlbranch status --format json` reports `poolFormat` 2

### Requirement: Pool full while writing the new credentials
When the pool runs out of space before the new engine credentials replace the old ones, `pool upgrade --yes` MUST exit 1 with `error.code` "pool_full", leave the credentials file unchanged, remove the temporary file it wrote, and leave the pool at format 1.

#### Scenario: Pool full before the fence
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, and writing the temporary credentials file fails because the pool is full (tested with a fake runtime)
- **THEN** the command exits with code 1 and `error.code` is "pool_full", the credentials file has the same SHA-256 as before, no temporary credentials file is left, `sqlbranch status --format json` exits 0 with `poolFormat` 1, and `sqlbranch branch create dev5 --from crm` exits 0

### Requirement: The credentials file stays private during the upgrade
The rewritten engine credentials MUST stay readable and writable only by the engine's user. The engine password MUST NOT appear in any container argument, environment variable, log, or the output of `pool upgrade`, and at every moment the credentials file MUST hold either the complete old or the complete new content.

#### Scenario: Password sentinel during upgrade
- **WHEN** the engine was initialized with `SQLBRANCH_SA_PASSWORD` set to `Sentinel-Pw-12345!` and the user runs `sqlbranch pool upgrade --yes` with a recording container runtime
- **THEN** the command exits with code 0, the credentials file is readable and writable only by the engine's user, and no recorded argument, environment value, log line or output stream contains `Sentinel-Pw-12345!`

#### Scenario: Killed while rewriting the credentials
- **WHEN** the client running `sqlbranch pool upgrade --yes` is killed at each step of the credentials rewrite in turn (tested with a fake runtime)
- **THEN** after each kill `sqlbranch image list` exits with code 0, and running `sqlbranch pool upgrade --yes` again exits 0 and `sqlbranch branch conn dev1` still connects

### Requirement: A failed image lock is checked against the pool format
When a `branch create` or `branch reset` cannot take an image lock, it SHALL read the pool format again before it reports the failure. During an unfinished or running pool upgrade it MUST exit 3 with `error.code` "pool_upgrade_required" and name `sqlbranch pool upgrade --yes` instead of saying that the image is being built or removed. Otherwise it SHALL report the lock failure as before.

#### Scenario: Create meets an image lock held by a running upgrade
- **WHEN** the clone mode is `thin` or `copy`, `sqlbranch pool upgrade --yes` holds shop's image lock while it waits for a reset of `dev1` in another process, and the user runs `sqlbranch branch create dev7 --from shop`
- **THEN** the create exits with code 3, `error.code` is "pool_upgrade_required", the message says a pool upgrade is running and names `sqlbranch pool upgrade --yes`, the message does not say the image is being built or removed, and no branch `dev7` exists

#### Scenario: Reset meets an image lock taken by an upgrade that started after its first read
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, `sqlbranch branch reset dev1` (a branch of shop) has read format 1, and `sqlbranch pool upgrade --yes` then rewrites the credentials and takes shop's image lock before the reset tries it (tested with a fake runtime)
- **THEN** the command exits with code 3, `error.code` is "pool_upgrade_required", the message names `sqlbranch pool upgrade --yes`, and `dev1` is online with its data unchanged

#### Scenario: Create meets an image lock on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, `sqlbranch image rm shop` holds shop's image lock in another process (tested with a fake runtime), and the user runs `sqlbranch branch create dev7 --from shop`
- **THEN** the command exits with code 1 with the same error code and message as SqlBranch 0.2.2, and `sqlbranch status --format json` reports `poolFormat` 1

### Requirement: Teardown works on every pool format
`sqlbranch teardown --force` SHALL work on a pool that uses format 1, on a pool that uses format 2, and during an unfinished pool upgrade, and SHALL remove everything the existing teardown rule names, including the temporary files and version-ID records the upgrade wrote.

#### Scenario: Teardown during an unfinished upgrade
- **WHEN** the clone mode is `thin`, `sqlbranch pool upgrade --yes` was killed after it rewrote the credentials, and the user runs `sqlbranch teardown --force`
- **THEN** the command exits with code 0, the engine container and the pool are gone, and `sqlbranch init` followed by `sqlbranch status --format json` reports `poolFormat` 1

#### Scenario: Teardown of a format-2 pool
- **WHEN** the clone mode is `copy`, the pool uses format 2 with image shop and branch `dev1`, and the user runs `sqlbranch teardown --force`
- **THEN** the command exits with code 0 and the engine container, shop, `dev1` and the pool are gone

### Requirement: Older clients stop on an upgraded pool
On a pool that uses format 2 with the engine running, SqlBranch 0.2.2 commands that read the engine credentials (`init`, `engine start`, `status`, every `image build`, `image import`, `image rm`, and every `branch` command) MUST exit 1 with `error.code` "operation_failed" before changing any file, and SqlBranch.Testing 0.2.x create and reset MUST throw. With the engine stopped they report `engine_stopped` as before, except `init` and `engine start` (design D10).

#### Scenario: Older client starts a stopped engine
- **WHEN** the pool uses format 2, the engine is stopped, and the released 0.2.2 tool runs `sqlbranch engine start`
- **THEN** it exits with code 1, `error.code` is "operation_failed", and the message is "The engine credentials file in <pool> is not valid."
- **AND** the engine stays stopped and no pool file changes

#### Scenario: Older client attempts forced removal
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has branch `dev1`, and the released 0.2.2 tool runs `sqlbranch image rm shop --force`
- **THEN** it exits with code 1, `error.code` is "operation_failed", the message is "The engine credentials file in <pool> is not valid.", and shop and `dev1` are unchanged

#### Scenario: Older test package
- **WHEN** the pool uses format 2 and a test using SqlBranch.Testing 0.2.2 calls create for image shop
- **THEN** create throws with the message "The engine credentials file in <pool> is not valid.", and the 0.2.2 availability check returns false

#### Scenario: Older client with the engine stopped
- **WHEN** the pool uses format 2, the engine was stopped with `sqlbranch engine stop`, and the released 0.2.2 tool runs `sqlbranch branch list`
- **THEN** it exits with code 3, `error.code` is "engine_stopped", and no file in the pool changes

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
On a pool that uses format 2, an image or branch written in the format-1 layout after the upgrade SHALL be listed. The first command that changes images or branches with it SHALL give the image a first version ID by the version-ID rule and the branch a base by the upgrade's rule. `image list`, `image export`, `branch list`, `branch conn` and `status` SHALL NOT adopt.

#### Scenario: Image written by 0.2.2 after the upgrade
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, image `crm` was written in the 0.2.2 layout after the upgrade finished (tested by writing its files directly), and the user runs `sqlbranch branch create dev6 --from crm --format json` at 2026-12-02T08:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0 and `branch.imageVersion` is "crm@20261202T080000000Z"
- **AND** `sqlbranch image list` shows crm once
- **AND** `sqlbranch image list --format json` run before the create showed crm with `version` null and left crm's files unchanged

#### Scenario: Adopted name that was removed and reused
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, crm was published as `crm@20261201T020000000Z` and removed with `sqlbranch image rm crm`, image `crm` was then written in the 0.2.2 layout (tested by writing its files directly), and the user runs `sqlbranch branch create dev6 --from crm --format json` while the fake clock reads 2026-11-30T02:00:00.000Z
- **THEN** the command exits with code 0 and `branch.imageVersion` is "crm@20261201T020000001Z"

#### Scenario: Branch written by 0.2.2 after the upgrade
- **WHEN** the clone mode is `thin` or `copy`, the pool was upgraded at 2026-12-01T09:00:00.000Z giving shop `shop@20261201T090000000Z`, and branch `dev8` of shop was written in the 0.2.2 layout at 2026-12-01T10:00:00Z (tested by writing its record directly)
- **THEN** `sqlbranch branch reset dev8 --format json` exits with code 0 and `branch.imageVersion` is "shop@20261201T090000000Z"

### Requirement: Adoption does not wait
A command that must adopt an image written in the format-1 layout SHALL try the image's lock exclusively without waiting. When another operation holds it, the command MUST exit 1 with `error.code` "catalog_busy", name the image, and change nothing.

#### Scenario: Image busy during adoption
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, image `crm` was written in the 0.2.2 layout after the upgrade, `sqlbranch image rm crm` holds crm's lock in another process (tested with a fake runtime), and the user runs `sqlbranch branch create dev7 --from crm`
- **THEN** the command exits with code 1, `error.code` is "catalog_busy", the message names crm, and no branch `dev7` exists
