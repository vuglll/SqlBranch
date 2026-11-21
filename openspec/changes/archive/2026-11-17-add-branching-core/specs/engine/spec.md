# Spec Delta

## Purpose

Sets up and runs the storage pool and the single SQL Server container that
SqlBranch owns, reports their health, detects whether the pool can clone
files without copying them, and removes everything it created on request.

## ADDED Requirements

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
`status`, `engine start`, `engine stop`, `teardown` and every image and branch command MUST exit with code 3, `error.code` "not_initialized", and a message naming `sqlbranch init` when no SqlBranch engine container, managed pool file, `sqlbranch-pool` volume or marked pool directory exists.

#### Scenario: Engine start on a new machine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch engine start`
- **THEN** the command exits with code 3 and names `sqlbranch init`

#### Scenario: Branch create on a new machine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch branch create dev1 --from shop`
- **THEN** the command exits with code 3 and `error.code` is "not_initialized"

### Requirement: Engine stop is safe to repeat
`sqlbranch engine stop` on an engine that is already stopped SHALL exit with code 0 and change nothing.

#### Scenario: Stop twice
- **WHEN** the engine is stopped and the user runs `sqlbranch engine stop`
- **THEN** the command exits with code 0

### Requirement: Status report
`sqlbranch status` SHALL report whether the engine is running, the engine version and edition, the pool location and backend, the clone mode, the number of images and branches, and the pool's total, used and free space.

#### Scenario: Status with the engine stopped
- **WHEN** the engine container is stopped and the user runs `sqlbranch status`
- **THEN** the command exits with code 3
- **AND** the output reports the engine as stopped and still shows the pool location

### Requirement: Status reports mismatches
`sqlbranch status` SHALL list every branch folder with no attached database, every attached database with files in the branches area of the pool but no branch folder, the leftovers of a reset or image build that is no longer running, and every image that was only partly removed, and SHALL exit with code 1 when it lists any.

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
