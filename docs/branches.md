# Branches: create, list, conn, reset, remove and prune

A branch is a writable database attached to the engine. Its files are clones of
an image's files, so changes in a branch never reach the image or another
branch. Every command takes `--format json` and the global options in
`docs/cli-output.md`.

Branches are for development and test use only. Never use SqlBranch for
production data.

## The commands

The commands below are run as written by an integration test, on a pool with no
images, after `sqlbranch init`. `shop.bak` is a full backup of a database named
`Shop`; the test makes it and puts it in the working folder. The comment after
each line is the exit code the test expects.

```sh
sqlbranch image build shop --from-bak shop.bak          # exit 0
sqlbranch branch create run17 --from shop --ephemeral --ttl 30m --label run=17    # exit 0
sqlbranch branch list --label run=17 --format json      # exit 0
# After the 30-minute time limit:
sqlbranch status --format json                          # exit 0
sqlbranch branch prune --format json                    # exit 0
sqlbranch branch create pr-142 --from shop              # exit 0
sqlbranch branch create pr-142 --from shop              # exit 1
sqlbranch branch create nope --from missing             # exit 1
sqlbranch branch create master --from shop              # exit 2
sqlbranch branch create bench1 --from shop --copy       # exit 0
sqlbranch branch list                                   # exit 0
sqlbranch branch conn pr-142                            # exit 0
sqlbranch branch conn pr-142 --quiet                    # exit 0
sqlbranch branch conn nope                              # exit 1
sqlbranch branch reset pr-142                           # exit 0
sqlbranch branch reset nope                             # exit 1
sqlbranch branch rm ../images/shop                      # exit 2
sqlbranch branch rm bench1                              # exit 0
sqlbranch branch rm bench1                              # exit 1
sqlbranch image rm shop                                 # exit 1
sqlbranch image rm shop --force                         # exit 0
sqlbranch branch list                                   # exit 0
sqlbranch branch reset pr-142                           # exit 1
sqlbranch branch rm pr-142                              # exit 0
sqlbranch status                                        # exit 0
```

The second create of `pr-142` fails because the name is taken (`branch_exists`), and so
does the create from an image that does not exist (`image_not_found`). `master`
is a reserved name, so it exits 2. `branch conn` is the only command that
prints the engine password. `image rm shop` fails while branches use the image
(`image_in_use`); after `--force` the list marks `pr-142` as having a removed
image, and its reset fails because there is nothing to clone (`branch reset`
exits 1 and the branch is unchanged).

The benchmark setup, also run as written:

```sh
sqlbranch image build shop-full --from-bak shop.bak --keep-recovery-model   # exit 0
sqlbranch branch create bench1 --from shop-full --copy                      # exit 0
sqlbranch branch reset bench1                                               # exit 0
sqlbranch branch list                                                       # exit 0
sqlbranch branch rm bench1                                                  # exit 0
sqlbranch image rm shop-full                                                # exit 0
```

## branch create

```
sqlbranch branch create <name> [--from <image>] [--copy] [--ephemeral [--ttl <duration>]] [--label key=value]...
```

`--from <image>` names the image to clone. Without it, the `defaultImage`
setting (`.sqlbranch.yml` or `SQLBRANCH_DEFAULT_IMAGE`, see
`docs/configuration.md`) is used; with neither, the command exits 2 and says
to pass `--from`. `--from` wins over `defaultImage`.

`--copy` makes full copies of the image's files even when the pool can share
blocks, and reports the clone mode `copy`. It is for benchmarks: see "Benchmark
setup" below. A copy branch needs the image's total size free in the pool, so
the create checks it first and exits 1 `pool_full` before it makes any file,
whatever the pool's clone mode is. The same check runs when the pool is in
`copy` mode without `--copy`.

`requireThin` (see `docs/configuration.md`) turns a create on a `copy`-mode
pool into exit 3 `thin_required` before any file is made. A `--copy` branch is
exempt, in both clone modes, because you asked for a full copy.

`<name>` is the branch name and its database name. It follows the naming rules
of `docs/images.md` and may not be `master`, `model`, `msdb` or `tempdb`, and
may not start with `sqlbranch_`. A bad name exits 2 before the pool is looked
for, so it exits 2 even before `sqlbranch init`.

The command, in order:

1. Takes the lock `sqlbranch:branch:<name>` in the engine, so two creates of one
   name cannot both go on. The loser exits 1 `branch_exists`.
2. Holds `sqlbranch:image:<image>` in shared mode. Creates from one image run
   together; `image rm` and `image build` of that image take the lock
   exclusively and wait (they exit 1) while a create is running.
3. Checks that the image exists (exit 1 `image_not_found`) and that the name is
   free: no branch of that name, no database of that name on the engine (a
   user's database is never used or changed), and no folder of that name in the
   pool. Each exits 1 `branch_exists`.
4. Probes the clone mode of the pool.
5. Creates `/pool/branches/<name>/` and clones each image file into
   `/pool/branches/<name>/data/`: `cp --reflink=always` when the mode is `thin`,
   plain `cp` when it is `copy`. The files are made read-write for the engine
   (mode 640). `/pool/branches` is created for the engine's user when the pool
   has none, as a copy-mode pool does not.
6. Writes `branch.json` and attaches the database with `CREATE DATABASE ...
   FOR ATTACH`.

Use `--ephemeral` to make a branch eligible for pruning after one hour.
Override the time limit with `--ttl` and a positive whole number followed by
`m`, `h` or `d`, up to 365 days. Reset preserves the original expiry and labels.

Any branch can have labels. Repeat `--label key=value` to set several. If a key
appears twice, create stores its last value. Keys start with a lowercase ASCII
letter or digit and may contain up to 63 lowercase ASCII letters, digits, dots,
hyphens or underscores. Values can be empty and allow up to 256 characters
without control characters. The `sqlbranch.` key prefix is reserved for the tool.

### Output

Text output shows the clone mode, the server, the database and the user, and
says to run `sqlbranch branch conn <name>` for the connection string. The
password is never in the output. In JSON:

```json
{
  "branch": {
    "name": "pr-142",
    "image": "shop",
    "cloneMode": "thin",
    "server": "127.0.0.1,14330",
    "database": "pr-142",
    "user": "sa",
    "ephemeral": false,
    "expiresAt": null,
    "labels": {}
  }
}
```

`server` is the data source to put in a connection string. The password is the
engine's `sa` password, which `sqlbranch` stores in the pool's credentials file.

### branch.json

`/pool/branches/<name>/branch.json` is written by the create.

| Field | Meaning |
|---|---|
| `image` | The image the files were cloned from. `image list` and `image rm` count branches by it |
| `cloneMode` | `thin` or `copy`; a later reset recreates the branch the same way |
| `explicitCopy` | `true` only when the branch was created with `--copy`; left out otherwise (and in records written before the field existed). Only such a branch is exempt from `requireThin` at reset |
| `imageVersion`, `baseKnown` | Format-2 pools only (see "The branch base on a format-2 pool"). Absent in records written on a format-1 pool; `imageVersion` is null with `baseKnown` true for a base removed before the upgrade |
| `createdAt` | When the branch was created, UTC |
| `labels` | Key/value pairs set at creation, or an empty object |
| `ephemeral` | True for a short-lived branch. Defaults to false for older records |
| `expiresAt` | UTC expiry for a short-lived branch. Null for ordinary branches |

### A failed create

A create that fails after it made the folder or started the attach drops a
database it attached (only one whose files are in the branch's own folder) and
deletes the branch folder, then exits 1. If the cleanup itself fails, the error
says what is left, and `sqlbranch status` lists it. A folder that was already
in the pool when the create started is never deleted by it.

A client that is killed in the middle leaves the folder. `branch.json` is
written before the attach, so a branch that never got its database has a record
and no database, and `sqlbranch status` reports it as a folder with no database.

## branch list

```
sqlbranch branch list [--label key=value]...
```

Branches are ordered by name. Each `--label` filter requires an exact key and
value match. Multiple filters must all match. Filters accept reserved keys such
as `sqlbranch.owner`. With no matches the command succeeds with an empty list.
Example text output:

```
NAME    IMAGE           CLONE MODE  CREATED               STATE   OWN DATA  EPHEMERAL  EXPIRES AT  EXPIRED  LABELS
bench1  shop-full       copy        2026-11-17 09:12 UTC  online  1.1 GiB   no         -           no
pr-142  shop (removed)  thin        2026-11-17 09:10 UTC  online  3.4 MiB   no         -           no      run=17
```

In JSON, `branches` is an array of objects with `name`, `image`,
`imageRemoved`, `cloneMode`, `createdAt` (UTC), `state`, `ownBytes`, `ephemeral`, `expiresAt`, `expired` and `labels`. An empty
pool gives an empty array and exit 0.

The state comes from the engine and the pool:

| State | Meaning |
|---|---|
| `online` | The branch's database is attached from its folder and the engine reports it ONLINE. |
| `offline` | The same database, reported OFFLINE. |
| `broken` | The folder has a `branch.json` but no database (a create that was killed, a reset whose attach failed, a database dropped by hand), or the record says it is broken or cannot be read, or the engine reports a state other than ONLINE or OFFLINE. `branch reset` repairs it when its image exists; `branch rm` removes it. |

A branch that is in the middle of a `branch create` or `branch reset` shows as `broken` in `branch list` until the command finishes: its folder and record exist before its database is attached. `branch rm` and `branch reset` also work on a branch whose database is `offline` or otherwise not online. `branch conn` still prints the string for such a branch and warns on standard error.

An image that was removed with `image rm --force` is shown as `shop (removed)`
(`imageRemoved` is `true`). The branch keeps working, and cannot be reset.

**Own data** is the space the branch's own changed data uses. For a `copy`
branch it is everything the branch allocated, because a full copy owns all of
its files. For a `thin` branch it is the length of the file extents that no
other file shares, which is what the branch wrote since it was created (plus
anything the image no longer shares after `image rm --force`). It counts
allocated space, including the space the database file grew into
(autogrowth), not only the pages the database wrote, so a branch can show
more than its tables hold. It is a way to see which branches have drifted far
from their image, not an accounting of rows. A size that cannot be read is
shown as `-` (null in JSON) with a warning.

## branch prune

Once a short-lived branch passes its expiry time, it remains usable. An online
branch still shows `state: online` and now shows `expired: true`. Run
`sqlbranch branch prune` to remove expired branches, including broken ones.
Prune closes active connections and rolls back their transactions as part of
removal. It skips branches locked by another branch operation. Ordinary
branches and short-lived branches within their time limit stay in the pool.

Prune reports what it removed and skipped. If a removal fails, it continues
with the other branches and exits 1 with the paths and errors. Running prune
with nothing expired succeeds.

## branch conn

```
sqlbranch branch conn <name> [--quiet]
```

Prints a connection string for Microsoft.Data.SqlClient that works from the
machine running the CLI, for example:

```
Data Source=127.0.0.1,14330;Initial Catalog=pr-142;User ID=sa;Password=...;Trust Server Certificate=True
```

**This is the only command whose output contains the engine password.** Create,
list, reset, status and every error leave it out (errors show `***`). Do not
paste the output into a log or a chat. With `--quiet` (or `-q`) the text output
is the string alone, one line, for use in a script:
`export DB="$(sqlbranch branch conn pr-142 --quiet)"`. In JSON the string is
`connectionString`, next to a `branch` object with `name`, `server`,
`database` and `user`.

A name that is not a branch exits 1 `branch_not_found`; a bad name exits 2.

## branch reset

```
sqlbranch branch reset <name>
```

Closes the connections to the branch and returns it to the exact state of its
image. The name and the connection string stay the same, so a client only has
to reconnect. The order is chosen so that a failure before the drop changes
nothing:

1. Takes the branch's lock and checks that `branch.json` exists (a database
   that is not a branch is never touched; exit 1 `branch_not_found`).
2. Checks that the image named in `branch.json` still exists. If not, exit 1
   says the image no longer exists and the branch is unchanged.
3. Checks `requireThin` (a `thin` branch on a `copy` pool, exit 3) and, when the
   new files will be full copies, that the pool has room for them: during the
   reset the branch holds two copies, so a `copy` branch needs the image's size
   free (exit 1 `pool_full`, branch unchanged).
4. Clones the image into `/pool/branches/<name>/next/`.
5. Puts the database in single-user mode with rollback and drops it, which
   deletes its files. A database that is not online (offline, recovery pending,
   suspect) cannot be altered, so it is dropped directly.
6. Writes `"state": "broken"` into `branch.json`, moves `next/` into place and
   attaches the database. On success the state is cleared.

A branch made with `--copy` stays a full copy after a reset, in either clone
mode, and the mode is shown in the output and by `branch list`. A branch that is
a copy only because its pool is in `copy` mode is not exempt from `requireThin`
at reset.

If the attach in step 6 fails, the old database is already gone. The command
exits 1, keeps the branch's record, and `branch list` shows the branch as
`broken`. Run `sqlbranch branch reset <name>` again: it retries from the
image. The same retry repairs a branch that a killed reset left without a
database. A branch whose image was removed with `image rm --force` cannot be
repaired by a reset; remove it and create a new one from another image.

## The branch base on a format-2 pool

On a format-2 pool `branch create` records the ID of the image's current version in `branch.json` (`imageVersion`, with
`baseKnown` true), and a successful reset records the version it attached. `branch.json` is written to `branch.json.new` and
renamed. `branch list` shows the recorded ID as `imageVersion`; `imageRemoved` is true when the name's catalog no longer lists
it, also after the name was built again. Format 1 records and shows no version (null).

| Recorded base | `imageVersion` | `imageRemoved` | Reset |
|---|---|---|---|
| a version ID | the ID | true when the catalog does not list it | restores that version |
| none, image removed before the upgrade | null | true | refused |
| unknown | null | false while the name exists | refused |
| no base fields (written before the upgrade) | null | by name, as in 0.2.2 | restores the current version and records it |

A reset restores the recorded base, so a branch keeps the data it was made from even when the name now holds a newer
version. For a removed or unknown base `branch reset` exits 1 with `image_not_found` before it closes any connection, leaves
the branch as it was, and tells you to remove it with `sqlbranch branch rm <name>` and create a new one with
`sqlbranch branch create`. A broken branch that is retried gets the same checks.

## branch rm

```
sqlbranch branch rm <name>
```

Puts the database in single-user mode with rollback (open connections are
closed, and their open transactions are rolled back), drops it and deletes the
branch folder. A branch with a `branch.json` and no database, which is what a
killed create leaves, loses its folder too. Names are checked first (a path
such as `../images/shop` exits 2 and touches nothing), then `branch.json` must
exist, so the command never drops a system, build or user-made database. A name
that is not a branch exits 1 `branch_not_found`.

If the database is dropped but the folder cannot be deleted, the command exits
1 and names the folder, and `sqlbranch status` exits 1 and lists it as a
folder with no database. Delete the folder, or run `branch rm` again.

## Benchmark setup

A thin branch shares blocks with its image and pays a one-time cost on the
first write to each shared block. After scattered updates its files can be in
many pieces. That is right for development, tests and CI, where a branch lives
minutes to days and is reset often. It distorts a write-heavy measurement, so
set a benchmark up like this:

1. **Use a copy branch.** `sqlbranch branch create bench1 --from shop-full --copy`
   makes full copies of the image's files, so the branch's files are in a few
   pieces and nothing is shared with the image. It needs the image's size free
   in the pool (and twice that while you reset it). `branch reset bench1`
   returns it to a clean copy between runs and keeps it a copy. On a pool that is
   already in `copy` mode every branch is a full copy.
2. **Keep the engine quiet.** The engine, and so every branch on it, shares one
   container's memory and CPU. Remove other branches, stop anything else that
   queries the engine (`sqlbranch branch list` shows what exists), and run on
   the hardware you want numbers for: x86-64 that resembles production, not an
   emulated engine on a Mac. A container does not have the resource limits of a
   managed service, so for Azure SQL Database trust only relative results.
3. **Match the edition.** Set `engineEdition` to `standard-developer` or
   `enterprise-developer` (SQL Server 2025 and later) to match production;
   plain `developer` has the Enterprise feature set. The edition is fixed when
   the engine is first created (`docs/configuration.md`).
4. **Match the recovery model.** An image is sealed in the SIMPLE recovery
   model by default, which keeps a branch's log small. Build the image with
   `sqlbranch image build shop-full --from-bak shop.bak --keep-recovery-model`
   when production runs in FULL recovery and log behavior matters
   (`docs/images.md`).

The commands of the second block at the top of this page do exactly this.

## DBCC CHECKDB on a branch

`DBCC CHECKDB` works on a branch like on any database. On a pool folder that the
container host serves from the client's file system, which is how a Docker
Desktop directory pool behaves, SQL Server cannot make the sparse snapshot that
CHECKDB reads by default (operating system error 31); run it `WITH TABLOCK`,
which checks the live database under locks. A managed pool has no such limit.
