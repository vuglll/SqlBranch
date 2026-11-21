# Design

## Context

See `proposal.md` for motivation. The storage approach was proven by hand in
the spike; numbers are in `docs/research/spike-results.md`. In short, on
2026-11-15:

- A 51 GiB image branched in about half a second on a Mac (Colima and Docker
  Desktop, SQL Server under Rosetta) and in about 260 ms on a GitHub-hosted
  x86-64 runner. Isolation, image immutability, CHECKDB and restart passed.
- Branches are only cheap if the image's log is rebuilt at seal time
  (decision D4).
- A thin pool can be created inside Docker Desktop's VM and on a hosted
  runner (decision D2a).
- A dry run suggests heavy scattered writes cost extra space and fragment a
  thin branch (decision D14). The x86-64 run for gate G2 is still open.

Constraints that shape the design:

- Microsoft supports XFS and ext4 for SQL Server data files on Linux, and
  container images only on x86-64 Linux hosts
  (https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-setup,
  checked 2026-11-15). XFS can clone files with reflink
  (https://docs.oracle.com/en/operating-systems/oracle-linux/8/xfs/xfs-CreatinganXFSFileSystemWiththeReflinkFeature.html,
  checked 2026-11-15); ext4 cannot.
- Users run the CLI from Linux, macOS and Windows. On the last two, Docker
  runs inside a Linux VM whose disk the CLI cannot reach directly.
- This is a single-user, single-host tool in 0.1.

## Goals / Non-Goals

**Goals:**

- One code path for all client operating systems.
- Branch creation that does not depend on database size in thin mode.
- No state that can drift: what is on disk and in the engine is the truth.
- Seams that let later features (tests, CI, other backends) plug in without
  rewrites.

**Non-Goals:**

- Production use, high availability, multi-user access.
- Being a general container manager. SqlBranch owns exactly one container.
- Hiding the clone mode. Copy mode is slower and the tool says so.

## Decisions

### D1. One shared engine; a branch is an attached database

A branch is a database attached to the one engine instance with
`CREATE DATABASE ... FOR ATTACH`. The branch name is the database name.

Why: attaching takes about a second and costs almost no memory, so dozens of
branches fit on a laptop. Alternative considered: one container per branch.
It isolates server-level state but costs seconds of start-up and gigabytes of
memory each. It is drafted as `add-isolated-branches` for later.

### D2. Clone files inside the engine container with `cp --reflink`

All pool file work runs through `docker exec` in the engine container:
`cp --reflink=always` for thin mode, plain `cp` for copy mode. The CLI never
touches pool files itself.

Why: the same code works whether the pool is a host directory (Linux) or a
Docker volume inside a VM (macOS, Windows), and the CLI needs no native file
system code. Alternatives considered:

- FICLONE from the CLI process: needs direct access to the pool, which Docker
  Desktop does not give.
- ZFS or LVM thin snapshots (what DBLab uses): ZFS is not a supported file
  system for SQL Server; LVM needs root and block devices.
- SQL Server database snapshots: read-only.
- `DBCC CLONEDATABASE`: schema and statistics only, no data.
- VHDX differencing disks (what SQL Clone and dbaclone use): Windows only.

### D2a. The managed pool: an XFS file that SqlBranch mounts itself

Most machines do not keep Docker's storage on a reflink file system. So
unless the pool directory already supports reflink, `init` creates a sparse
file of `poolSize`, formats it as XFS with reflink, and loop-mounts it where
the engine can bind-mount it. This is the `loop-xfs` backend. It was proven
in the spike on Docker Desktop for macOS, on Colima and on a GitHub-hosted
runner.

How, as found in the spike:

- A privileged helper container formats the file. Docker Desktop's VM has no
  `mkfs.xfs`, so the tool cannot rely on the host having it.
- A privileged helper with the host's process namespace runs
  `nsenter -t 1 -m` to mount the file in the host's own mount namespace, so
  ordinary containers can bind-mount the result.
- Docker Desktop only accepts bind mounts of VM paths under `/var/lib`, so
  the managed pool lives at `/var/lib/sqlbranch/` on the container host.
- The mount does not survive a restart of Docker Desktop, Colima or the
  machine. The file does. `init` and `engine start` check for the mount and
  redo it before starting the engine.

Limits on the helpers: each carries the `io.sqlbranch.managed=true` label,
mounts only `/var/lib/sqlbranch/` from the host, runs a fixed command
(format, mount, unmount or delete of `/var/lib/sqlbranch/pool.xfs`), and is
started with `--rm` and also removed in a `finally` step. The pool file
`/var/lib/sqlbranch/pool.xfs` and the mount point `/var/lib/sqlbranch/mnt`
are constants, never built from user input.

Pool work without the engine: D2 runs file work in the engine. When the
engine is not running (teardown, a failed init), a labeled, unprivileged
one-off container that mounts only the pool does it instead, with the
same fixed commands.

Finding the pool: later commands look for, in order, the engine
container's labels (`io.sqlbranch.backend`, `io.sqlbranch.pool`), then
`/var/lib/sqlbranch/pool.xfs` through a helper, then the `sqlbranch-pool`
volume, then the configured `pool` directory with a marker. Finding any of
them means SqlBranch is initialized, so a half-done teardown or an
unmounted pool after a restart is still recognised. `port` and `pool` are
fixed at first `init` like the edition (exit 2 on change).

`init` prints the pool file, its size and the mount point before creating
them. A first `init` that fails records each thing it created in this run
and undoes them in reverse order: helper containers, engine container,
mount, pool file, credentials. Before creating the pool file it checks
host free space against `poolSize` from a helper (`df` on `/var/lib`). Too
little space is exit 3 `host_disk_full` under both `auto` and `loop-xfs`:
falling back to copy mode would need even more space, so it would only
move the failure to the first branch.

Fallback under `auto`: before switching to copy mode, `init` removes
whatever the managed-pool attempt created (helpers, mount, pool file). The
copy-mode pool is the `pool` directory on Linux and a Docker volume named
`sqlbranch-pool` on Docker Desktop, where host directories are not on the
VM's disk. Alternative considered for Docker Desktop: a bind-mounted macOS
folder. Rejected because file sharing through the VM is slow for database
files.

Backends: `auto` (default) uses the directory as it is when the probe says it
can share blocks, otherwise `loop-xfs`, otherwise falls back to copy mode
with the reason. `loop-xfs` and `plain` force one or the other.

Trade-offs: it needs permission to run privileged containers, which some
company machines forbid (then copy mode is the fallback); the pool has a
fixed size chosen at creation; and loop devices add a layer whose
performance on real workloads is part of gate G2. Docker Desktop for Windows
is not verified. Alternatives considered: asking users to switch to Colima
or to reformat a disk (too much friction), and a Docker volume plugin (more
moving parts for the same result).

### D3. Pool layout is the state

```
/pool/engine/                 system databases and credentials
/pool/images/<name>/manifest.json
/pool/images/<name>/data/*    sealed files, read-only
/pool/branches/<name>/branch.json
/pool/branches/<name>/data/*  cloned files, attached to the engine
/pool/import/                 backups placed here are used without a copy
/pool/build/<id>/             staging for one image build, deleted at the end
/pool/.probe/                 clone-mode probe files
/pool/.sqlbranch-pool         pool marker: {"format": 1, "created": ...}
```

The marker is the only thing that makes a directory count as a SqlBranch
pool. `init` refuses a non-empty directory without it, and `teardown` and
ownership changes act only where it exists. Folder names alone are not
trusted, because a user folder may well contain `images/`.

There is no separate state database. `status` reads the pool and
`sys.databases` and reports anything that does not match: a branch folder
with no database, a branch database with no folder (a database counts as
a branch database only when its files are under `/pool/branches/`; other
user databases are never mismatches), any `/pool/build/`
folder or `sqlbranch_build_*` database whose build is not running (its
application lock is free), any `/pool/branches/<name>/next/` whose reset is
not running, and any `/pool/images/.removing-<name>/` folder. A running
build is shown as in progress and does not count. It exits 1 when it
reports anything. Branch and image names may not start with `sqlbranch_`,
so a branch can never look like build debris.

Alternative considered: a state file or SQLite database. Rejected because it
can drift from reality after a crash.

### D4. How an image is built

1. Check the file on the client (exists, readable; exit 2 otherwise) and
   check free pool space for the backup file (exit 1 `pool_full`
   otherwise). Then copy the backup into `/pool/build/<id>/` (`docker cp`),
   unless it is already under `/pool/import/`. The client does not parse
   the backup format; only the engine reads it.
2. `RESTORE HEADERONLY` and `FILELISTONLY` to check the backup type, version
   and files. Reject differential and log backups, backup sets striped across
   files, FILESTREAM and memory-optimized filegroups. Then check free pool
   space for the restored files' total size (exit 1 `pool_full`, staging
   deleted). Alternative considered: parse the backup header on the client
   to check both sizes before staging. Rejected as a second, fragile reader
   of Microsoft's backup format.
3. Restore under a temporary database name with files in a staging folder.
4. Record the recovery model, then set SIMPLE so branch logs stay small.
   `--keep-recovery-model` skips the switch, for performance tests that
   need production-like log behavior.
5. `CHECKPOINT`, detach.
6. Rebuild the log: delete the log file, attach the data files with
   `FOR ATTACH_REBUILD_LOG`, detach again. The spike showed why this is
   required: attaching a branch rewrites part of the source's log, about
   68 MiB per branch for a 256 MB log, which made 20 branches cost 15
   percent of a 9 GiB image. `DBCC SHRINKFILE` could not shrink that log
   below two large virtual log files. With a rebuilt 0.5 MB log, growth was
   0 MiB per branch and all checks still passed. Rebuilding is safe here
   because the detach before it was clean, and a sealed image has no use
   for the old log chain.
7. Move the files to `/pool/images/<name>/data/`, make them read-only, write
   the manifest (source database name, engine version, files with logical
   names and sizes, recovery model, original log size, build time, backup
   file name and SHA-256).
8. Delete `/pool/build/<id>/`, including the staged backup, whether the
   build succeeded or failed; on failure also drop the temporary database
   `sqlbranch_build_<id>`. A killed client leaves both, and `status`
   reports them.

Alternative considered: keep images as backups and restore per branch. That
is exactly the slow path this tool replaces.

### D5. How a branch is created, reset and removed

- Create: make the folder, clone each image file, attach, write
  `branch.json` (image, clone mode, created time). `--copy` forces plain
  `cp` on a thin pool. It exists for benchmarks: a thin branch pays a
  one-time cost on the first write to each shared block (spike gate G2),
  which would distort write-heavy measurements. The mode is stored, so a
  reset recreates the branch the same way.
- Create failure: anything after the folder is made runs under a
  try/cleanup that drops an attached database and deletes the folder.
- Reset, ordered so a failure before the drop changes nothing:
  1. check the image named in `branch.json` exists;
  2. in copy mode, check free space for a full copy;
  3. clone the image files into `/pool/branches/<name>/next/`;
  4. single-user with rollback, drop the old database (which deletes its
     files);
  5. move `next/` into place and attach.
  A failure in step 5 leaves `branch.json` with `state: broken`, and
  `reset` can be run again. Thin clones in step 3 cost almost nothing; a
  copy branch needs room for a second copy for a moment, which step 2
  checks.
- Remove: single-user with rollback, drop, delete the folder. If the
  folder delete fails, exit 1 and name it; `status` shows it as a mismatch.
- Guard: `rm` and `reset` first check the name rules, then that
  `/pool/branches/<name>/branch.json` exists. `create` refuses a name used
  by any database on the engine, not only by a branch. They never act on a database
  without it, so system, build and user-made databases are out of reach.

Alternative considered for reset: drop first, then re-clone (simpler, one
fewer folder). Rejected because a failed clone would lose the branch.

Image removal: rename `/pool/images/<name>/` to
`/pool/images/.removing-<name>/` first (one atomic step that makes it
unlisted and unusable), then delete it. A failed delete exits 1 naming the
path, and `status` reports the leftover folder. Alternative considered:
delete files and then the manifest. Rejected because a half-deleted image
would still be listed and could be branched from.

Because reflink blocks are reference counted, removing an image never breaks
existing branches. Those branches can no longer be reset, and `reset` says
why.

### D6. Clone mode is probed, never assumed

At `init` and on every `status` and `doctor`, the tool writes a 1 MiB file to
`/pool/.probe/` and tries `cp --reflink=always`. Success means thin; failure
means copy. The result is shown in output and stored in each `branch.json`.
`requireThin` turns copy mode into exit code 3 for `branch create` and
`branch reset` of thin branches. `--copy` branches and `image build` are
exempt: one is an explicit request for a copy, and the other always writes
full files in either mode.

Alternative considered: detect the file system type. Rejected: the type does
not prove reflink is enabled, and the probe is cheap and exact.

### D7. Locking with the engine itself

Commands that change images or branches take `sp_getapplock` in `master` for
the name they change (so two builds or creates of one name cannot both
proceed), and one shared lock name for pool-wide operations. `status` tests
these locks to tell a running build or reset from a dead one.

Why: every client already has a connection to the engine, so this works from
any operating system with no lock files. Alternative considered: `flock` on a
pool file, which needs another `docker exec` and does not release cleanly if
the client dies.

### D8. Engine container

- Image: `mcr.microsoft.com/mssql/server:2022-latest` by default,
  configurable. The exact tag in use is recorded in manifests.
- Name: `sqlbranch-engine`. Labeled `io.sqlbranch.managed=true`. SqlBranch
  refuses to act on a container without that label.
- `/var/opt/mssql` lives under `/pool/engine/`, so recreating the container
  keeps attached branches.
- Port: published on `127.0.0.1:14330` by default.
- Pool location: see D2a. With the `plain` backend it is a host directory
  (default `~/.sqlbranch/pool` on Linux). With `loop-xfs` it is the managed
  mount under `/var/lib/sqlbranch/` on the container host. `init` fixes
  ownership for the engine's user with a one-off container, but only on an
  empty directory or one that already has the SqlBranch layout; any other
  non-empty directory is refused with exit 2 before anything changes.
- Startup wait: up to 120 s for a successful login, then exit 3
  `engine_not_ready` with the last 20 lines of the engine log, redacted.
- Edition: set by `engineEdition`, passed to the container as `MSSQL_PID` at
  first start. `developer` (default) works on every version and has the
  Enterprise feature set. `standard-developer` and `enterprise-developer`
  exist from SQL Server 2025
  (https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2025,
  https://learn.microsoft.com/sql/linux/sql-server-linux-editions-and-components-2025,
  both checked 2026-11-15) and let a team test with the limits of the
  edition it runs in production. Production editions are refused: SqlBranch
  is a dev and test tool and stays out of license keys. The exact
  `MSSQL_PID` value for Standard Developer must be confirmed against
  Microsoft's edition table when implementing (Enterprise Developer is
  listed as `EnterpriseDeveloper`). The edition is fixed when the engine is
  created; `init` prints it with the non-production notice.
- The engine image is fixed at first `init`, like the edition: a later
  `init` with a different `engineImage` exits 2. Moving branches to a new
  major version is a later feature, so no attach across versions happens
  in 0.1.
- Version check for those editions: from the tag when it starts with a
  year (`2019-`, `2022-`, `2025-`); otherwise `init` starts the engine,
  reads `SERVERPROPERTY('ProductMajorVersion')`, and on a version below
  2025 undoes everything that run created, like a failed first `init`
  (D2a), but exits 2 because the cause is configuration.

### D9. Secrets

`init` generates a 32-character `sa` password unless
`SQLBRANCH_SA_PASSWORD` is set; a given password is checked against SQL
Server's policy before anything is created. The password is fixed at first
`init`: a later `init` with a different `SQLBRANCH_SA_PASSWORD` exits 2.
Rotation is a later feature. The password is stored in
`/pool/engine/credentials.json` with owner-only permissions and is never
read from `.sqlbranch.yml`.

Where it may appear: the text and JSON output of `branch conn` (its
purpose) and the credentials file. Nowhere else, which means:

- JSON results for `branch create` return `server`, `database` and `user`,
  not a connection string. Scripts and agents call `branch conn --quiet`.
  This keeps the password out of CI logs by default.
- Container settings: the engine is created with a one-time bootstrap
  password in `MSSQL_SA_PASSWORD`, generated at random for each first
  `init` and never stored. Right after first start, `init`
  connects and runs `ALTER LOGIN sa WITH PASSWORD` to the real one, so
  `docker inspect` shows only a dead value. **Unverified:** that the
  container does not re-apply `MSSQL_SA_PASSWORD` on later starts; task 4.9
  tests it by restarting the engine and logging in with the real password.
- Process lists: SQL goes through Microsoft.Data.SqlClient, never `sqlcmd
  -P` in a `docker exec`, so the password is not on any command line.
- Errors: every message, `docker` command line and engine log excerpt
  passes through one redactor that replaces the password with `***`.

Alternatives considered: printing the full string from `branch create`
(convenient, but leaks into CI logs); Docker secrets (need Swarm).

### D10. Container runtime boundary

`SqlBranch.Core` talks to an `IContainerRuntime` interface (run, exec, copy,
inspect, stop, remove). The only implementation in 0.1 runs the `docker`
command. Unit tests use a fake.

Why not a Docker client library: one less dependency, and Podman can be added
later behind the same interface.

### D11. Output and exit codes

Every command builds a result object. Text and JSON renderers print it. JSON
has `schemaVersion: 1`, `command`, `ok`, a payload, and on failure an `error`
with a stable `code` and a message. Exit codes: 0, 1 operation failed,
2 usage or config error, 3 environment not ready. Nothing ever prompts.

### D12. Testing

- Unit tests: `SqlBranch.Core.Tests` with the fake runtime and a fake SQL
  session. Fast, no Docker.
- Integration tests: `SqlBranch.IntegrationTests`, tagged, run only when
  `SQLBRANCH_IT=1`. They create a small database and back it up at test time
  instead of committing a binary backup.
- Thin-mode assertions run only when the probe reports thin; otherwise they
  are reported as skipped, not passed.
- `tools/bench/clone-bench.sh` times branch creation against a large image.

### D13. Seams for later work

- Image building is a pipeline of steps, so a later change can add sources
  (scripts, live server) and hooks before sealing.
- Manifests and `branch.json` carry a free-form `labels` map, unused in 0.1.
- `SqlBranch.Core` exposes every operation the CLI has, so the testing
  package and other front ends call the library, not the CLI.

### D14. Copy-on-write hint, and what thin branches are for

By default XFS unshares 128 KiB around each written 8 KiB page. In the
spike's dry run that made 372,000 scattered row updates grow a branch by
7.1 GB on an 8.7 GiB image; an 8 KiB copy-on-write extent hint on the
branches directory roughly halved it. `init` sets that hint on
`/pool/branches`.

The same dry run showed the cost that remains: after one scattered update
pass the branch's files had about 531,000 extents (a full copy had 6), and a
second pass on the same branch ran 2 to 3 times slower. That was a single
run under emulation on a loopback disk, so it is a warning, not a
measurement. Until gate G2 is run on x86-64:

- Thin branches are positioned for development, tests and CI, where a
  branch lives minutes to days and is reset often. A reset gives a fresh,
  unfragmented clone.
- Sustained write-heavy work and benchmarks use `branch create --copy`.
- `branch list` shows each branch's own changed-data size, so a branch that
  has drifted far from its image is visible.

If G2 confirms the slowdown, a later change can add a warning threshold or a
command that rewrites a branch as a full copy.

### D15. Teardown

`teardown --force` exits 2 without `--force` and 3 `not_initialized` when
none of the things in "Finding the pool" (D2a) exists. Otherwise it runs
these steps, each skipping what is already gone:

1. Make the pool reachable: remount a managed pool that is not mounted
   (as `engine start` does).
2. Start the engine if it is stopped, then list and drop every database
   other than the system databases, printing the user databases. If the
   engine cannot start, it lists database files under `/pool/engine/data/`
   that are not system files instead, and goes on.
3. Remove the engine container.
4. In a pool work container (D2a): delete `engine/`, `images/`,
   `branches/`, `import/` (listing the backups), `build/`, `.probe/` and,
   last, the marker.
5. For `loop-xfs`, unmount `/var/lib/sqlbranch/mnt` and delete
   `/var/lib/sqlbranch/pool.xfs` through a helper. For the Docker Desktop
   fallback, remove the `sqlbranch-pool` volume.

On a directory pool (`plain`, or a reflink directory under `auto`) step 4
deletes only the listed items, never the directory or other files, and
step 5 does nothing.

A failure exits 1 naming what is left. Running it again finishes, because
the pool file, the volume or the engine container still marks SqlBranch as
initialized even after the marker is gone.

Alternative considered: documenting manual `docker rm` and `umount` steps.
Rejected because the managed pool lives in a VM users cannot easily reach.
Keeping user databases and import backups was also considered; rejected
because they live inside the pool file, which teardown deletes. Listing
user databases from files only, without starting the engine, was also
considered; rejected because file names do not reliably give database
names, so it is only the fallback.

## Risks / Trade-offs

- [SQL Server on cloned files misbehaves in a case the spike did not cover] →
  Integration tests run CHECKDB on written branches; the bench script is run
  before each release.
- [Benchmark numbers from a shared engine or a thin branch mislead] → The
  docs describe a benchmark setup: `--copy` branch, nothing else running
  on the engine, hardware and edition close to production.
- [Thin branches slow down under heavy scattered writes] → See D14: hint
  set at `init`, `--copy` for write-heavy work, G2 on x86-64 still to run.
- [The managed pool needs privileged containers] → `auto` falls back to
  copy mode with a clear reason; `doctor` reports it.
- [The managed pool is unverified on Docker Desktop for Windows] → Same
  fallback; verify before claiming support (task 8.3).
- [A managed pool fills up] → `status` and `doctor` show pool space; the
  size is fixed at creation in 0.1, resizing is a later change.
- [Attach fails across engine versions] → The engine image is fixed at
  first `init` (D8), so 0.1 never attaches across versions. Images record
  the engine tag for a later upgrade feature.
- [Database features that keep data outside the listed files] → Rejected at
  build time in 0.1 rather than half-supported.
- [A crash mid-operation leaves debris] → Layout is the state; `status`
  reports mismatches and leftover builds, and `doctor --fix` (later) can
  clean them.
- [The x86-64 spike run misses a gate] → This change starts on a
  conditional go (see the proposal); release 0.1 waits for spike sections
  4 and 7.
- [Local `sa` password on a developer machine] → Loopback-only port by
  default, owner-only credentials file.

## Open Questions

- Should text output use color and tables from a console library, or stay
  plain? Does not affect specs.
- Is Native AOT worth it once the dependency list is stable? Revisit after
  0.2.
