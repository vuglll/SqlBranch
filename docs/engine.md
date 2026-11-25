# The engine: init, status, doctor, engine start and stop, teardown

SqlBranch owns one SQL Server container, the engine, and one pool of storage
that the engine mounts at `/pool`. These commands create, inspect and remove
them. Every command takes `--format json` and the global options in
`docs/cli-output.md`; the settings are in `docs/configuration.md`.

The engine is for development and test use only. Never use it for production.

## The commands

The commands below are run as written by an integration test, on an empty
pool, in this order. The comment after each line is the exit code the test
expects.

```sh
sqlbranch doctor               # exit 0
sqlbranch init                 # exit 0
sqlbranch init                 # exit 0
sqlbranch status               # exit 0
sqlbranch engine stop          # exit 0
sqlbranch engine stop          # exit 0
sqlbranch status               # exit 3
sqlbranch engine start         # exit 0
sqlbranch teardown             # exit 2
sqlbranch teardown --force     # exit 0
sqlbranch teardown --force     # exit 3
```

### `sqlbranch init`

Creates the pool and the engine, then reports them. It is safe to repeat: on
an existing engine it creates nothing, starts the engine if it is stopped, and
reports the same facts.

What a first `init` creates, and prints before it does:

1. **The pool.** By `poolBackend`:
   - `plain` uses the `pool` directory as it is. It must be empty or already a
     SqlBranch pool, or `init` exits 2 and changes nothing. `init` writes the
     marker `.sqlbranch-pool` in its root and makes the engine's user the owner.
   - `loop-xfs` creates the sparse file `/var/lib/sqlbranch/pool.xfs` of
     `poolSize` on the container host, formats it as XFS with reflink and
     mounts it at `/var/lib/sqlbranch/mnt`. Helpers that do this are privileged,
     labeled `io.sqlbranch.managed=true` and removed after each step. Host space
     is checked first (exit 3 `host_disk_full`).
   - `auto` (the default) uses the directory when the clone probe says it can
     share blocks, otherwise creates the managed pool.
2. **The fallback.** Under `auto`, when the managed pool cannot be created or
   mounted (for example the runtime refuses privileged containers), `init`
   removes what that attempt created and uses copy mode: the `pool` directory
   on Linux, the Docker volume `sqlbranch-pool` on Docker Desktop. It says why,
   and exits 0. Copy mode copies every file when a branch is created. `loop-xfs`
   never falls back: it exits 3 `pool_unavailable`.
3. **The engine folder** `engine/` and the credentials file
   `engine/credentials.json`, readable by the engine's user only. The
   password is random unless `SQLBRANCH_SA_PASSWORD` gives one. It never
   appears in output.
4. **The `branches` folder**, in thin mode, with an 8 KiB copy-on-write extent
   hint. By default XFS unshares 128 KiB around each written 8 KiB page; the
   hint roughly halves the space a heavily written branch uses. If the pool
   cannot take the hint, `init` still succeeds and prints a warning.
5. **The engine container** `sqlbranch-engine`, labeled, publishing only
   `127.0.0.1:14330` (or `port`). `init` waits up to 120 seconds for a login
   (exit 3 `engine_not_ready` with the log's last lines) and records the
   backend, pool, edition and port in container labels.

The edition (`engineEdition`) is passed to the engine as `MSSQL_PID`:
`Developer`, `StandardDeveloper` or `EnterpriseDeveloper` (the last two need
SQL Server 2025; checked against Microsoft's edition table on 2026-11-16, see
`docs/research/2026-11-16-mssql-pid-values.md`). For an image tag that does
not start with a year, `init` cannot tell the version in advance. If the
engine turns out to be older than 2025, `init` removes everything that run
created and exits 2.

If any step of a first `init` fails, `init` undoes what that run created, in
reverse order, and exits 1 `init_failed` (or keeps the cause's exit code 2 or
3). The engine image, edition, port, pool and password are fixed at the first
`init`; a different value later exits 2 and names the current one.

The output ends with the edition and the sentence that the engine is for
development and test use only. The JSON has `repeated`, `engine`, `pool`
(`location`, `backend`, `cloneMode`), `created` and `notice`.

### `sqlbranch status`

Reports whether the engine is running, its version and edition, the pool
location and backend, the clone mode, the number of images and branches, and
the pool's total, used and free bytes. It also lists anything that does not
match: a branch folder with no database, a branch database with no folder,
leftovers of builds, resets and image removals that are no longer running. It
exits 1 `mismatch` when it lists any. A stopped engine exits 3
`engine_stopped` and still prints the pool.

For a running engine, `branches` includes expired branches. The JSON field
`expiredBranches` lists their names and is empty when none have expired.
Text output names the branches and suggests `sqlbranch branch prune`.
Expiry alone leaves the exit code at 0.

An abandoned script login appears as a `build_login` mismatch after its build
lock is released. A subsequent image build attempts to remove it and reports
a warning if cleanup fails or the login owns another database.

A storage credential that a URL build made and could not drop (see
[images.md](images.md#building-from-a-backup-url)) appears as a
`build_credential` mismatch, by name only, never with its secret. The next
`sqlbranch image build` drops it, deletes its record and the build folder, and
`status` exits 0 again.

Status also reports the pool format (see
[The engine credentials file](#the-engine-credentials-file)): a `Pool format`
line in text and `poolFormat` (an integer) in JSON, 1 for a pool that `init`
made or SqlBranch 0.2.2 wrote and 2 for a pool in the newer format. A pool
upgrade that did not finish appears as a `pool_upgrade` mismatch, and a
temporary credentials file left by one as an `upgrade_leftover` mismatch, by
name only, never with its content; both name `sqlbranch pool upgrade --yes`.
`status` tests the upgrade lock with `APPLOCK_TEST` and never takes it. While
the lock is held, the upgrade and its temporary file are shown as `In progress:
pool upgrade is running` and `status` exits 0. Examples of the four states are
in [cli-output.md](cli-output.md#status-and-the-pool-format).

### `sqlbranch doctor`

Doctor reports the detected SqlPackage path and version in text and JSON.
SqlPackage is needed only for `--from-bacpac`. If doctor finds no usable
executable on PATH, it reports SqlPackage as not installed with status
`optional` and leaves the exit code unaffected. To use BACPAC builds, install
SqlPackage with `dotnet tool install -g microsoft.sqlpackage` or use the
[platform downloads](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-download).
Put the executable on PATH or use `doctor --sqlpackage <path>` to check a
specific installation. If that path is missing or cannot report a version,
doctor names the path and exits 3 with `sqlpackage_missing`.
Verified on macOS and Linux (container), SqlPackage 170.5.96.0.
Windows verification is a release gate for 0.3.
[SqlPackage verification record](research/2026-11-19-bacpac-source-facts.md)

Checks that Docker is reachable, the engine image is on the host, the pool is
writable, the engine port is free or held by the engine, and free pool space.
These checks report `pass`, `warn` or `fail`. Free space under 10 GiB warns and
under 1 GiB fails. It also warns on a host that is not x86-64 and on copy
mode. Doctor exits 0 when nothing fails. A failed check exits 3 with
`docker_unavailable`, `sqlpackage_missing` or `check_failed`. Doctor works
before `init` and creates nothing.

### `sqlbranch engine start` and `sqlbranch engine stop`

`engine start` remounts a managed pool that went away with a restart of
Docker or the machine (exit 3 `pool_not_mounted` if that fails), starts the
engine and waits for a login. `engine stop` stops it and does nothing when it
is already stopped. Both exit 3 `not_initialized` when nothing is found.

### `sqlbranch teardown --force`

Removes everything SqlBranch created. Without `--force` it exits 2, names
`--force` and removes nothing. It exits 3 `not_initialized` when no engine,
managed pool file, `sqlbranch-pool` volume or marked pool directory exists.
Otherwise it runs these steps, each skipping what is already gone:

1. Remounts a managed pool that is not mounted.
2. Starts the engine if it is stopped, lists and drops every database that is
   not a system database, and prints their names. If the engine cannot start,
   it lists the database files under `engine/data` that are not system files.
3. Removes the engine container.
4. In a work container that mounts only the pool, deletes `engine/`,
   `images/`, `branches/`, `import/` (printing the backups in it), `build/`,
   `.probe/` and, last, the marker.
5. For `loop-xfs`, unmounts `/var/lib/sqlbranch/mnt` (and removes the empty
   folder) and deletes `/var/lib/sqlbranch/pool.xfs`. For the Docker Desktop
   fallback, removes the `sqlbranch-pool` volume.

On a directory pool, step 4 deletes only the items listed and step 5 does
nothing, so the directory and every other file in it, such as `readme.txt`,
stay. Teardown deletes user databases and import backups, because they live
inside the pool.

If a step fails, `teardown` exits 1 `operation_failed`, names what is left (for
example `/var/lib/sqlbranch/mnt and /var/lib/sqlbranch/pool.xfs` after a failed
unmount) and the next `sqlbranch teardown --force` finishes the job, because
the engine container, the pool file or the volume still mark SqlBranch as
initialized. JSON: `removed` (`databases`, `databaseFiles`, `importBackups`,
`engineContainer`, `poolFolders`, `poolFile`, `volume`) and `left`.

### `sqlbranch pool upgrade --yes`

```
sqlbranch pool upgrade --yes
```

Moves a format-1 pool (what `init` creates and SqlBranch 0.2.2 reads) to format 2, which gives every image a first
version ID and is the base of image refresh. It is the only command that changes the pool format, and it cannot be
undone: SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x packages can no longer use the pool afterwards. Stop them, and
any test run that uses the pool, first. `--yes` is required, takes no value and may be given once; without it, or with an
operand or another option, the command exits 2 `usage_error` before it runs any Docker command. The global options
`--format`, `--config` and `--quiet` work as on every command. It needs a running engine (exit 3 `not_initialized` or
`engine_stopped`).

What it does, in order: takes a pool-wide upgrade lock (a second upgrade exits 1 `catalog_busy`), writes the new credentials
file with the upgrade marked `running`, locks every existing image and branch, writes each image's `catalog.json` and
version-ID record with the first ID `<name>@<start time>` (UTC, to the millisecond), and marks the upgrade `done`. It writes
only the credentials file, the catalogs, the per-name ID records under `images/.ids/` and, in a later step, branch records;
it never changes image or branch data files, manifests, databases, logins or the engine container. A rerun after a kill or
a full pool continues from what is recorded and gives the same IDs. On a pool that is already format 2 it changes nothing
and exits 0.

## How later commands find the pool

In order: the engine container's labels, `/var/lib/sqlbranch/pool.xfs`, the
`sqlbranch-pool` volume, then the configured `pool` directory if it holds the
marker. Finding any of them means SqlBranch is initialized, so an unmounted
pool after a restart or a half-done teardown is still recognised. A volume
named `sqlbranch-pool` that does not carry the `io.sqlbranch.managed=true`
label is the user's own: it is not a pool, and `init`, `doctor` and
`teardown` never write to it or delete from it.

## The engine credentials file

`/pool/engine/credentials.json` holds the engine's `sa` password. It is owned
by the engine's user (10001) with mode 600 and is written through a short-lived
work container, with the password on standard input only, never in an argument,
an environment variable or a log. There are two formats:

```json
{"user":"sa","password":"..."}
```

Format 1 is what `init` writes and what SqlBranch 0.2.2 reads.

```json
{"format":2,"engine":{"user":"sa","password":"..."},
 "upgrade":{"startedAt":"2026-12-01T09:00:00.000Z","state":"running"},
 "note":"Written by SqlBranch 0.3 (pool format 2). ..."}
```

Format 2 nests the login under `engine`, records the upgrade (`state` is
`running` until the upgrade finishes, then `done`) and carries a note for
people who open the file. It has no top-level `password`, so a 0.2.x tool that
reads it stops with "The engine credentials file in <pool> is not valid." before
it takes a lock. That is the fence of the pool upgrade. This release reads both
formats. A file that says a format above 2 is refused with `operation_failed`;
the message names the format and tells the user to update the `sqlbranch` tool
and the SqlBranch.Testing package.

A format-2 file is written by a new path, not in place: the work container runs
as root (the engine image's own user cannot change ownership), writes
`credentials.json.new`, sets owner 10001:0 and mode 600, flushes it, renames it
over `credentials.json` and flushes the directory. If a step before the rename
fails, for example because the pool is full, the run deletes its own temporary
file and leaves the old file as it was, so the pool keeps its format; a full pool
is `pool_full`. A run that is killed before the rename leaves
`credentials.json.new`, which `status` lists as an upgrade leftover (see
[status](#sqlbranch-status)); after the rename none is left.

## Pool layout

```
/pool/engine/                 system databases and credentials
/pool/images/<name>/          sealed images (format 2 adds catalog.json)
/pool/branches/<name>/        branches
/pool/import/                 backups used without a copy
/pool/build/<id>/             staging for one image build
/pool/.probe/                 clone-mode probe files
/pool/.sqlbranch-pool         pool marker
```

An upgrade binds a branch to the image's first version when its `createdAt` is
at or after the manifest's `builtAt`; it does not use the manifest's filesystem
modification time. An imported image keeps the archive producer's `builtAt`,
not the local import time. If an earlier image was force-removed and replaced
by an import built before the branch was created, the upgrade pins that branch
to the imported version. This is also what a 0.2.2 reset would have restored.
A branch older than the current image gets an unknown base; a branch whose
image is gone gets a removed base. Neither can be reset to the current image.

An upgrade that times out or is interrupted blocks commands that change images or
branches with exit 3, `pool_upgrade_required`, until you rerun
`sqlbranch pool upgrade --yes`. The rerun resumes from the recorded start time,
keeps the version IDs already assigned, and removes only the upgrade's fixed
temporary files: `engine/credentials.json.new`, `images/<name>/catalog.json.new`,
`images/.ids/<name>.new`, and `branches/<name>/branch.json.new`. If the pool fills,
free space and rerun the same command.

### Stop 0.2.x tools first, and what adoption is

Before you run `sqlbranch pool upgrade --yes`, stop every SqlBranch 0.2.x CLI command and every test run that uses
SqlBranch.Testing 0.2.x on the pool. SqlBranch cannot see running 0.2.x processes. A 0.2.2 command that read the old
credentials file just before the upgrade wrote it can still take its locks afterwards, and a build, import or
`branch create` of a name the upgrade did not wait for (a new image name, or a create from one) finishes in the old
layout. The upgrade waits for commands that already hold the lock of an existing image or branch, for at most
`publishWait`; it does not wait for those.

What such a command leaves is still listed. An image written in the 0.2.2 layout after the upgrade (a `manifest.json` and
no `catalog.json`) shows in `image list` with `version` null, `image export` exports its files and `image rm` removes it
with `image.versions` `[]`. A branch record without a base shows its `imageVersion` as null. Neither is changed by a read:
`image list`, `image export`, `branch list`, `branch conn` and `status` never adopt.

The first command that writes with the item adopts it. `branch create` and `branch reset` give an image its first version
ID by the usual rule (the command's start time, or one millisecond after the last ID the name had, so a name that was
removed and reused still gets a larger ID than any it had) and write the catalog before the branch records its base.
A branch record without base fields is bound at reset by the same rule as in the upgrade: pinned to the image's first
version when its `createdAt` is at or after the manifest's `builtAt`, otherwise with an unknown base (it cannot be reset;
`sqlbranch branch rm` and `branch create` are the way out); a branch whose image is gone has a removed base. A build or
import of a name that such an image uses exits 1 `image_exists` and changes nothing.

Adoption takes the image's lock exclusively and does not wait. When another command holds it, the command exits 1
`catalog_busy`, names the image and changes nothing; run it again when the other command is done. A 0.3 build or import
reads the pool format once more just before it publishes: on a finished format 2 it publishes through the catalog, and on
format 1 or during an upgrade that has not finished it writes the 0.2.2 layout, which a later command adopts as above.

`status` tests the upgrade lock without taking it. While the lock is held it shows
“pool upgrade is running” and temporary files as in progress (exit 0). After the
upgrade process dies, it reports the unfinished upgrade and each temporary file
by name as mismatches (exit 1), with `sqlbranch pool upgrade --yes` as the fix.
Image list and export, branch list and conn, and status remain available.
If the upgrade reports an unreadable catalog, `sqlbranch image rm <name>` is
allowed to remove that image during the unfinished upgrade; use `--force` if it
has branches. Then rerun the upgrade. `teardown --force` also works during an
unfinished upgrade and on format 2, removing the temporary files and `.ids`
along with the rest of SqlBranch's pool contents.
