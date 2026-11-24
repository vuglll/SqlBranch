# Images: build, list, remove, export and import

An image is a sealed, read-only set of database files with a manifest.
Branches are cloned from it. An image can start from a full backup or a folder of SQL scripts. The engine reads backups directly. Every command takes
`--format json` and the global options in `docs/cli-output.md`.

Images are for development and test use only. Never use SqlBranch for
production data.

## The commands

The commands below are run as written by an integration test, on a pool with no
images, after `sqlbranch init`. `shop.bak` is a full backup of a database
named `Shop` with one data file and one log file; the test makes it and puts it
in the working folder. The comment after each line is the exit code the test
expects.

```sh
sqlbranch image build shop --from-bak shop.bak                          # exit 0
sqlbranch image build shop --from-bak shop.bak                          # exit 1
sqlbranch image build crm --from-bak shop.bak --keep-recovery-model     # exit 0
sqlbranch image build crm2 --from-bak missing.bak                       # exit 2
sqlbranch image list                                                    # exit 0
sqlbranch image rm crm                                                  # exit 0
sqlbranch image rm crm                                                  # exit 1
sqlbranch image rm ../branches/pr-142                                   # exit 2
sqlbranch image rm shop --force                                         # exit 0
sqlbranch image list                                                    # exit 0
sqlbranch status                                                        # exit 0
```

The second build fails because the name `shop` is taken (`image_exists`). The
second `rm crm` fails with `image_not_found`. The path-like name is refused
before any file is touched.

## Building an image

```
sqlbranch image build <name> --from-bak <file> [--backup-set <n>] [--keep-recovery-model]
```

`<name>` is 1 to 63 characters: lowercase letters, digits, hyphen and
underscore, starting with a letter or digit, and not starting with
`sqlbranch_`. A bad name exits 2 before any SQL runs or any file is touched.
A name that is already an image exits 1 `image_exists` and changes nothing.
Two builds of one name at the same moment give one success and one exit 1.

A backup build proceeds as follows:

1. Checks the file on the client. A missing or unreadable file exits 2.
2. Checks that free pool space is at least the backup's size, then copies the
   backup into `/pool/build/<id>/` inside the engine container. A backup you
   keep under the pool's import folder (`<pool>/import/`) is used where it is
   and is never deleted. Not enough space exits 1 `pool_full` before copying.
3. Reads the backup's header and file list in the engine and checks the limits
   below. A file the engine cannot read as a backup exits 1 `not_a_backup`.
4. Checks that free pool space is at least the restored files' total size.
5. Restores the backup as a temporary database named `sqlbranch_build_<id>`.
6. Records the recovery model and switches the database to SIMPLE, unless you
   gave `--keep-recovery-model`.
7. Runs a checkpoint, detaches, and rebuilds the log: the old log is deleted
   and the data files are attached with a new, minimal log, then detached
   again. Branches then do not rewrite the source's log (the spike measured
   about 68 MiB per branch for a 256 MB log).
8. Moves the files to `/pool/images/<name>/data/`, makes them read-only and
   writes `manifest.json` last. An image exists once its manifest does.
9. Deletes `/pool/build/<id>/` and the staged backup, whether the build
   succeeded or failed.

The command reports the clone mode of the pool, `thin` or `copy`. When a build
fails, SqlBranch removes the temporary database and its build folder. It reports
and preserves any database files a script placed outside that folder. A client
that is killed in the middle leaves them, and `sqlbranch status` lists them.

### Building and masking with SQL scripts

Choose one source. With `--from-scripts db`, SqlBranch creates an empty database
and reads the SQL files directly in `db` in ordinal filename order. For example,
`001_schema.sql` creates tables and `002_seed.sql` populates them. Subdirectories
and other extensions are ignored. A line containing only `GO` starts a new batch.
A repeat count such as `GO 5` exits 2 with the filename and line before any SQL runs.

For either source, repeat `--post-script` to run additional files in the order
given. For the sample Shop backup, save this as `mask.sql` to replace the
sample customer names with predictable test addresses before sealing:

```sql
UPDATE dbo.Item SET Name = CONCAT('user', Id, '@example.test');
```

```sh
sqlbranch image build shopdev --from-scripts db                          # exit 0
sqlbranch image build masked --from-bak shop.bak --post-script mask.sql --script-timeout 2m   # exit 0
sqlbranch image rm shopdev                                               # exit 0
sqlbranch image rm masked                                                # exit 0
```

Each script has an unlimited runtime by default. Set `--script-timeout`
to a positive whole number of minutes, hours or days, up to 365 days. On
failure the command names the script and batch line. Successful text output
reports elapsed time for each file.

Scripts run as a temporary login that owns the build database. They cannot
create databases or use another ordinary user database. Before a scripted
build, SqlBranch checks online user databases for guest access. A grant or
an unreadable permission catalog blocks the build. The check skips databases
in SINGLE_USER and databases outside ONLINE, including concurrent restores.
It is a point-in-time check: a database brought online afterwards was not
checked.

For a backup with post-scripts, an enabled database trigger covering an
ownership change (including an event-group registration) fails the build
before ownership transfer. Backup-only builds preserve these triggers.
Scripts may not create, change or enable database DDL triggers. The database
must finish ONLINE, read-write and MULTI_USER. FILESTREAM and memory-optimized
filegroups are unsupported. Files outside the build folder are reported and
left in place. The engine disables Service Broker before sealing a scripted
build; enabling it later can reactivate queues from that image. After the log
rebuild the engine resets ownership to `sa`. An interrupted build can leave
a login. The next build attempts cleanup and warns if a leftover remains.

### Limits

- **Full backups only.** A differential or log backup exits 1
  `unsupported_backup_type`. When a file holds several backup sets, the build
  uses set 1; `--backup-set <n>` chooses another, which must be a full backup.
- **One file per backup.** A backup striped across several files exits 1
  `unsupported_backup_type`. Make the backup to a single file.
- **No FILESTREAM and no memory-optimized filegroups.** A backup with either
  exits 1 `unsupported_feature` and names the filegroup.
- **The engine must be at least as new as the backup.** A backup made by a
  newer SQL Server than the engine exits 1 `version_mismatch` and names both
  versions. Set `engineImage` (or `SQLBRANCH_ENGINE_IMAGE`) to a newer image,
  run `sqlbranch teardown --force` and then `sqlbranch init`, because the
  engine image is fixed when the engine is created.
- **Space.** The pool needs room for the staged backup and then for the
  restored files; `pool_full` names the bytes needed and available. A URL
  build stages nothing, so it needs room for the restored files only.
  Every build also keeps a 64 MiB reserve file in its build folder while it
  runs. If a script or a restore fills the pool, the cleanup deletes that file
  first, so SQL Server has room to drop the temporary database.

### The recovery model

By default the image is sealed in the SIMPLE recovery model, so a branch's log
stays small. `--keep-recovery-model` seals it with the model the source had,
for performance tests that need production-like log behavior. The manifest
records both.

### Building from a backup URL

> **Preview.** Azure Blob Storage and Amazon S3 support has been tested against local MinIO and Azurite, not yet against real Azure or AWS accounts. It may change before release 0.3.


```
sqlbranch image build <name> --from-bak <url> [--s3-region <region>]
```

`--from-bak` takes a file path, or a URL when the value starts with `https://`
or `s3://` (any case). The URL is checked before any `docker` command or SQL
runs:

| Form | Example |
|---|---|
| Azure Blob Storage | `https://acme.blob.core.windows.net/backups/shop.bak` |
| S3-compatible, path style | `s3://172.17.0.5:9000/backups/nightly/shop.bak` |
| S3, virtual-host style | `s3://acme-db.s3.eu-west-1.amazonaws.com/shop.bak` |

`--s3-region` sets the region of an `s3://` URL (1 to 32 lowercase letters,
digits and hyphens; default `us-east-1`). It is refused with any other source.

The secret never goes into the URL, a flag or a config file. Set
`SQLBRANCH_AZURE_SAS_TOKEN` for an `https://` URL, or
`SQLBRANCH_S3_ACCESS_KEY_ID` and `SQLBRANCH_S3_SECRET_ACCESS_KEY` for an `s3://`
URL. A URL with a query string or fragment (a pasted SAS URL), `http://`, no
host, user information (`user:password@host`), no container or bucket and file name, a trailing `/`, more than 259
characters, or a credential name (the URL up to its last `/`, the container URL
for Blob) of more than 128 characters exits 2 `usage_error`. A URL build always
uses backup set 1, and `--backup-set` with another number exits 2. A missing or
empty secret, or an S3 key ID or secret containing `:`, exits 2 `config_error`
naming the variable and never its value. These examples are run as written by
the same integration test:

```sh
sqlbranch image build shop --from-bak http://acme.blob.core.windows.net/backups/shop.bak                   # exit 2
sqlbranch image build shop --from-bak s3://s3.us-east-1.amazonaws.com/backups/                              # exit 2
sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/shop.bak                           # exit 2
sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak --s3-region eu-west-1   # exit 2
sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak --backup-set 2   # exit 2
```

#### What a URL build does

The engine reads the backup straight from the URL with `RESTORE LABELONLY`,
`HEADERONLY`, `FILELISTONLY` and `RESTORE DATABASE ... FROM URL`; the URL is a
statement parameter. Nothing is downloaded to the client, copied into the pool
(no `docker cp`, no staged `.bak`) or hashed, so the manifest has `backupSha256`
`null` and a `backupUrl`. For an `s3://` URL every one of the four statements
carries `RESTORE_OPTIONS` with the region (`us-east-1` unless `--s3-region` says
otherwise). The steps are `check-name`, `check-source`, `build-folder`,
`credential`, `inspect`, `check-restore-space`, `restore`, `drop-credential`, then
the same seal steps as any backup. Post-build scripts run after the credential
is gone.

- An `s3://` URL needs SQL Server 2022 or later. On an older engine the build
  exits 1 `unsupported_source`, names the version and `engineImage`, and
  creates nothing.
- A SAS token of more than 256 characters on an engine below SQL Server 2022
  exits 2 `config_error` naming `SQLBRANCH_AZURE_SAS_TOKEN`.
- The engine cannot reach, authenticate to, verify the certificate of, or find
  the backup: exit 1 `backup_unreachable` with the URL (never a secret) and the
  engine's reason. Access denied (operating system error 5), not found (2), no
  matching credential (86), an untrusted certificate (12175, with a hint to put
  the certificate authority's file in `/var/opt/mssql/security/ca-certificates`
  and restart the engine) and network errors (12007, 12029) are told apart.
  The public certificates of AWS S3 and Azure Blob Storage are trusted by a
  stock engine (checked 2026-11-19); a private or test endpoint needs its
  certificate authority placed in the engine.
- A striped backup (several files) exits 1 `unsupported_backup_type`.
- RDS: make the backup without `@kms_master_key_arn` and with
  `@number_of_files = 1`; an encrypted or striped RDS backup cannot be read.
- Azure SQL Managed Instance: restore works only to the SQL Server version of
  the instance's update policy, and a backup of an Always-up-to-date instance
  cannot be restored to SQL Server; the version-mismatch message says so.
  A Managed Instance backup uses service-managed TDE and can carry a
  memory-optimized (`XTP`) filegroup, which images refuse
  (`unsupported_feature`). **Managed Instance is not verified yet:** no backup
  from a real instance has been tried (spike 1.3, on the release gate). After a
  restore, `DBCC CHECKDB` can report error 8992 on a database from Managed
  Instance after an index was dropped; SqlBranch does not cause it.
- Azure Blob Storage: a SAS token that lets the engine read the blob may also
  need write permission. The engine takes a lease on the blob, and Azurite
  (the local emulator) refused a read-only SAS with operating system error 13.
  Real Blob Storage is not verified yet; the documented permissions for a
  restore are read and list.
- The SAS credential is named after the container (`https://<host>/<container>`),
  an S3 credential after the URL up to its last `/`.

#### The storage credential

SQL Server finds a SAS or S3 secret through a server credential named after
the URL's folder. A URL build makes one for itself, with these rules:

- It lives only for the restore: it is dropped right after `restore`, before
  any post-build script, and also when the build fails or is cancelled.
- A record of it, `/pool/build/<id>/credential.json`, holds the name and the
  credential's id and creation time, never the secret.
- If the engine already has a credential with that name that SqlBranch did not
  create, the build exits 1 `credential_conflict` naming it and changes
  nothing. Drop it with `DROP CREDENTIAL` if nothing needs it.
- Two builds that need the same name run one after the other: the second says
  `Waiting for another build that uses <name>` on standard error, once, and goes
  on when the first has dropped its credential. Ctrl+C ends the wait and removes
  what the second build made.
- A killed build, or a failed drop (the build exits 1, names the credential and
  the image is not sealed), leaves the credential and its record. `sqlbranch
  status` lists it as a build leftover, by name, and exits 1. The next
  `sqlbranch image build` drops it (only when the name, id and creation time all
  match the record), deletes the record and the folder, and warns if a drop
  fails. A credential made by hand is never dropped.

## The manifest

`/pool/images/<name>/manifest.json` is written by the build, owned by the
engine's user and read-only. `sqlbranch image list --format json` shows the
same facts, plus the number of branches.

| Field | Meaning |
|---|---|
| `format` | Manifest format, `1` |
| `name` | The image name |
| `sourceDatabase` | The database's name in the backup |
| `engineVersion` | The SQL Server version of the engine that built the image |
| `engineImage` | The exact engine image reference that built it, for example `mcr.microsoft.com/mssql/server:2022-latest`; empty in a manifest written before it was recorded |
| `files` | Each database file: `logicalName`, `fileName` (in the image's `data` folder), `type` (`data` or `log`) and `sizeBytes` |
| `originalRecoveryModel` | The recovery model the backup's database had |
| `sealedRecoveryModel` | The model the image was sealed with: SIMPLE unless `--keep-recovery-model` |
| `originalLogSizeBytes` | The size of the source's log file or files |
| `logRebuilt` | `true`: the image has a new minimal log, well under 8 MiB |
| `builtAt` | The build time, UTC |
| `backupFile` | The backup file's name, or the last segment of the URL |
| `ddlTriggers` | Database-scoped DDL trigger names from the restored backup, including disabled triggers. Older manifests default to an empty list. |
| `backupUrl` | The `https://` or `s3://` URL a URL build restored from, or `null`; older manifests read as `null` |
| `backupSha256` | The SHA-256 of the backup, computed in the engine; `null` for a URL build |
| `labels` | Free-form labels, empty for now |

## Listing images

```
sqlbranch image list
```

Shows each image's name, source database, total size, build time and the number
of branches created from it. On a format-2 pool the table adds a `VERSION`
column and each JSON entry has `version`, the ID of the name's current version
(null on a format-1 pool, and for an image an older client wrote after the
upgrade and no command has adopted yet). The count is the number of
`/pool/branches/*/branch.json` files that name the image. In JSON, each image
has the fields above and `branches` (the count) and `branchNames`. A pool with
no images lists none and exits 0. A manifest that cannot be read is reported as
a warning and that image is not listed.

## The image catalog (pool format 2)

On a format-2 pool each image folder has `catalog.json`, the per-name catalog:

```json
{"format":1,"name":"shop","current":"shop@20261201T090000000Z",
 "versions":[{"id":"shop@20261201T090000000Z","dir":"."}]}
```

`current` is the ID of the current version, or null when the name has none, and
`dir` is the folder of that version's files relative to the image folder. The
files at the top of the image folder (`manifest.json`, `data/`) are the first
version, `dir` ".". A catalog is replaced by writing a temporary file in the same
directory and renaming it over the old one.

A name exists when its catalog has a non-null `current`, or when the folder has
a `manifest.json` and no catalog, which is an image an older client wrote after
the upgrade. `image list` (`version` per row), `image export`, the name check of
`image build` and `image import` (exit 1 `image_exists`) and the image lookup of
`branch create` read the catalog on a format-2 pool. An image with no catalog is
listed with `version` null, exported from its top-level files and found by its
manifest. These read-only commands never write a catalog. A catalog that cannot
be read is a warning and the image is not listed. On a format-1 pool every one of
these commands keeps the 0.2.2 behavior and ignores catalogs.

## Removing an image

```
sqlbranch image rm <name> [--force]
```

The name is checked first (exit 2). An unknown image exits 1
`image_not_found`. Then:

1. The image folder is renamed to `/pool/images/.removing-<name>/`. That one
   step makes the image unlisted and unusable for new branches.
2. The renamed folder is deleted.

If deleting fails, the command exits 1 and names the leftover folder, the image
stays unlisted, and `sqlbranch status` exits 1 and lists the folder until you
delete it. `image rm` never deletes or changes a branch file.

When branches were created from the image, `image rm` exits 1 `image_in_use`
and lists them. `--force` removes the image anyway. Branches keep working with
their data in both clone modes, because the clone blocks are reference counted
and a copy branch has its own files. They can no longer be reset, because the
image they would be cloned from is gone, and `branch reset` says why.

On a format-2 pool the catalog goes with the folder, and the JSON result lists
the version IDs it held in `image.versions` (an empty list on a format-1 pool
and for an image with no catalog).

`image rm` takes the same lock as a build of that name, so the two cannot run
at once. If one is running, the other exits 1.

## Exporting an image

```
sqlbranch image export <name> --to <file|url> [--force] [--s3-region <region>]
```

`image export` writes one compressed archive that holds the image's manifest,
its database files and a SHA-256 for each file, and leaves the image as it was.
Use it to move an image to another machine or to keep a baseline. The examples
below are checked by unit tests and by the export integration tests, not by the
docs test above.

```
sqlbranch image export shop --to shop.sbimg           # exit 0
sqlbranch image export shop --to shop.sbimg           # exit 1  destination_exists
sqlbranch image export shop --to shop.sbimg --force   # exit 0
sqlbranch image export nope --to nope.sbimg           # exit 1  image_not_found
sqlbranch image export shop --to missing/shop.sbimg   # exit 2  the folder does not exist
sqlbranch image export shop                           # exit 2  --to is missing
```

`.sbimg` is the suggested extension; none is required. `--to` takes a file, an
`https://` Azure Blob Storage URL or an `s3://` URL (see [Cloud storage](#cloud-storage-for-archives)).
`--s3-region` sets the region of an `s3://` URL, `us-east-1` by default.

### The archive

Exports compress a PAX tar with zstd at level 1. The first four bytes are
`28 b5 2f fd`. Older gzip archives start with `1f 8b` and still import.

To inspect a new archive by hand, decompress it and list the tar entries.

```sh
zstd -d shop.sbimg -o shop.tar
tar -tf shop.tar
```

The tar holds exactly these entries in this order.

| Entry | Holds |
|---|---|
| `sqlbranch-archive.json` | `archiveFormat` (`1`) and `manifest`, the image's manifest as stored |
| `data/<fileName>` | One database file, for each file of the manifest, in manifest order |
| `sqlbranch-checksums.json` | `manifestSha256` (of the first entry) and, for each data file, `fileName`, `sizeBytes` and `sha256` |

The manifest comes first so that an import can check the format, the name and
the engine version before it writes a byte. The checksums come last because the
export computes them while the files stream past, so no file is read twice. The
archive never holds branch files, other images, credentials, logins, passwords,
storage secrets, build folders or engine settings. It does hold the database's
data: treat an archive like the backup it replaces, and never use one for
production data.

### What an export does

The image's files are read from the engine container with `tar` (through
`docker exec`), and pass through the client once, hashed and compressed, without
a second copy of the image on the client. An export takes no lock and changes
nothing in the pool.

1. The name, `--to` and the destination are checked (exit 2, below). The image
   must exist (`image_not_found`).
2. Without `--force`, a destination that exists exits 1 `destination_exists`,
   naming `--force`, before any image file is read. The file is not changed.
3. The archive is written to `<destination>.sqlbranch-partial-<id>` in the same
   folder and flushed to disk.
4. Only when the engine's `tar` exited 0 and every file had the size the
   manifest names, the partial file is moved onto the destination. Without
   `--force`, a file that someone else created during the export is not
   replaced and the export exits 1 `destination_exists`. With `--force`, the old
   file stays intact until this move replaces it in one step.

If anything fails, or you press Ctrl+C once, the partial file is deleted and the
destination is as it was; an older file there keeps its content. A failure
exits 1: the stream from the engine broke or ended early, an entry's size
differs from the manifest, or `tar` exited non-zero, for example because
`image rm` removed the image during the export. If the process is killed, it can
leave a `*.sqlbranch-partial-*` file next to the destination; its name says what
it is and it can be deleted.

An export to a URL runs the same steps without a partial file: the archive goes
up in parts straight from the client, and the object appears at the URL only
when the last part is committed (see [Cloud storage](#cloud-storage-for-archives)).

### Checks before anything runs

These exit 2 before any `docker` command: a name that breaks the image name
rules (`invalid_name`), a missing `--to`, a `--to` that is an existing folder,
a destination folder that does not exist, a destination inside the pool when the
pool is a directory (an archive must never land among pool files), and
`--s3-region` with a destination that is not an `s3://` URL or is malformed
(`usage_error`). An `https://` or `s3://` destination must not use `http://`,
have a query string or fragment, lack a host, lack a container or bucket and a
file name, or end in `/`; its query string is never echoed, so a pasted SAS URL
leaks nothing. Export puts no limit on the URL's length: the 259 and 128
character limits of `image build --from-bak` are SQL Server's. For a URL, the
secret it needs is read and checked here too: a missing or empty variable, or
an S3 key ID or secret with a `:`, exits 2 `config_error` naming the variable.

### Output

`--format json` returns `export` with `image`, `to` (as given), `files`,
`imageBytes` (the files' total size) and `archiveBytes` (the archive's size).
There is no `cloneMode`, because an export creates no pool files. Text output is
one result line followed by the file count and both sizes. While data moves, one
progress line is written to standard error about every 10 seconds.

### Cloud storage for archives

> **Preview.** Azure Blob Storage and Amazon S3 support has been tested against local MinIO and Azurite, not yet against real Azure or AWS accounts. It may change before release 0.3.


An export can write, and an import can read, one object in Azure Blob Storage or
S3-compatible storage. The URL forms are those of `image build --from-bak`:

| Form | Example |
|---|---|
| Azure Blob Storage | `https://acme.blob.core.windows.net/images/shop.sbimg` |
| S3-compatible, path style | `s3://172.17.0.5:9000/images/nightly/shop.sbimg` |
| S3, virtual-host style | `s3://acme-images.s3.eu-west-1.amazonaws.com/nightly/shop.sbimg` |

The client does the storage I/O itself, with the vendors' own libraries, over
HTTPS. SqlBranch never writes an archive to the engine's disk first and keeps no
temporary copy on the client: the archive is cut into parts of 64 MiB (larger for
an image over about 560 GiB, so that S3's limit of 10,000 parts is never reached)
and each part is uploaded before the next is read, so memory use stays near one
part.

The secrets are the ones `image build` uses, read only from the environment:
`SQLBRANCH_AZURE_SAS_TOKEN` for an `https://` URL (one leading `?` is dropped), and
`SQLBRANCH_S3_ACCESS_KEY_ID` with `SQLBRANCH_S3_SECRET_ACCESS_KEY` for an `s3://`
URL. They are read before any `docker` command or storage request. The client is
given only these, so it never reads an AWS profile, `~/.aws`, `AWS_*` variables,
instance metadata, an Azure managed identity or any other default credential, and
the vendors' logging and telemetry are off. Every error text passes through the
secret redactor, and a URL in a message has no query string.

Permissions the secrets need:

| | Azure Blob Storage (SAS) | S3 |
|---|---|---|
| Export | `r` and `w` (add `c` to allow creating the blob) on the blob or its container | `s3:GetObject` (for the existence check), `s3:PutObject`, `s3:AbortMultipartUpload` |
| Import | `r` | `s3:GetObject` |

A SAS with `c` but without `w` cannot stage the blocks and is refused with HTTP
403, so an export SAS needs `w`.

Overwrite protection works as for files. Without `--force`, an existing object
exits 1 `destination_exists` before any image file is read, and the final commit is
sent with `If-None-Match: *`, so an object that another client created during the
upload is not replaced: Azure answers with 412 or, as the Azurite emulator does,
409 `BlobAlreadyExists`, and S3 with 412; both are `destination_exists`. A
server that ignores that header (some S3-compatible servers) is protected only by
the up-front check; MinIO honours it, and the integration suite checks that against
a MinIO container on every run. With `--force` neither check runs, and the old object stays
intact until the commit replaces it at once.

The storage clients send a failed part, block, commit or abort again, twice at
most. If the service completed a conditional commit but its answer was lost, the
second attempt meets the finished object and the export reports
`destination_exists` although its own archive was committed; check the object
before you run the export again.

Failures leave the destination as it was:

- **S3**: a failed export, or Ctrl+C once, aborts the multipart upload, so no
  object appears and no part stays. If the process is killed, the unfinished
  multipart upload stays in the bucket and is billed. Add a lifecycle rule that
  aborts incomplete multipart uploads after a few days (S3's
  `AbortIncompleteMultipartUpload` rule).
- **Azure Blob Storage**: blocks are staged first and become a blob only with the
  one final commit, so a failed export commits nothing. The blocks that were
  already staged stay uncommitted: they are not visible as a blob and Azure
  removes them after a week. There is no call to drop them sooner. The block IDs
  hold a random prefix per export, so two exports to one blob never commit each
  other's blocks.

Storage that cannot be reached, refuses the secret (HTTP 401 or 403), presents a
certificate the client does not trust, or has no object at the import URL exits 1
`storage_unreachable`, naming the URL (without its query) and the reason, such as
`HTTP 404 (BlobNotFound)`. The client has no option for a private certificate
authority: a server with a self-signed certificate needs its certificate trusted
by the operating system.

A nightly baseline, with the secrets in the CI system's secret store:

```
sqlbranch image build shop --from-bak s3://acme-db.s3.eu-west-1.amazonaws.com/backups/shop.bak --s3-region eu-west-1
sqlbranch image export shop --to s3://acme-db.s3.eu-west-1.amazonaws.com/images/shop.sbimg --s3-region eu-west-1 --force
# on a developer machine
sqlbranch image import s3://acme-db.s3.eu-west-1.amazonaws.com/images/shop.sbimg --s3-region eu-west-1 --as shop
```

The examples below show the exit-2 checks, which no request or `docker` command
precedes. They are checked by unit tests, not by the docs test above.

```
sqlbranch image export shop --to https://acme.blob.core.windows.net/images/shop.sbimg      # exit 2  config_error, SQLBRANCH_AZURE_SAS_TOKEN is not set
sqlbranch image import s3://s3.amazonaws.com/images/shop.sbimg                             # exit 2  config_error, SQLBRANCH_S3_ACCESS_KEY_ID is not set
sqlbranch image import s3://s3.amazonaws.com/images/shop.sbimg --s3-secret-access-key abc  # exit 2  secrets are never flags
sqlbranch image export shop --to s3://s3.amazonaws.com/images/ --s3-region eu-west-1       # exit 2  usage_error, the URL ends in /
```

## Importing an image

```
sqlbranch image import <file|url> [--as <name>] [--s3-region <region>]
```

`image import` adds the image inside an archive written by `image export` to the
pool, as a full copy, under the name in the archive or under `--as`. It reports
the clone mode, like `image build`. The imported manifest equals the exported
one except for the name, and branches are created from it as from a built image.
The examples below are checked by unit tests and the import integration tests,
not by the docs test above.

```
sqlbranch image import shop.sbimg --as shop2   # exit 0
sqlbranch image import shop.sbimg              # exit 1  image_exists (an image named shop exists)
sqlbranch image import notes.txt               # exit 1  archive_invalid
sqlbranch image import nope.sbimg              # exit 2  the file does not exist
sqlbranch image import shop.sbimg --as "A B"   # exit 2  invalid_name
```

`<file|url>` is a file, an `https://` Azure Blob Storage URL or an `s3://` URL
(see [Cloud storage](#cloud-storage-for-archives)). An archive in storage is read
with one GET request and streamed through the client into the engine: there is no
copy of it on the client.

### What an import does

An import never trusts the archive. Two things happen before SqlBranch runs a
single `docker` command, and so also on a machine where `sqlbranch init` has not
run: the archive's first entry is read, and its manifest is checked. An archive
that is not a SqlBranch image archive, uses a compression or archive format
this version cannot read (a newer SqlBranch wrote it), or whose manifest breaks
a rule below, exits 1 `archive_invalid`. Only a valid archive goes on to look for
the engine, and a machine without one then exits 3 `not_initialized`.

The manifest is refused when its image name breaks the name rules and `--as`
gives no valid one; `files` is empty; a file name holds `/`, `\`, `..`, a NUL or
another control character, starts with `.`, is empty, or repeats another ignoring
case (`SHOP.MDF` next to `shop.mdf`); a file `type` is not `data` or `log`; a
`sizeBytes` is negative; the sizes add up to more than a 64-bit integer holds; or
the engine version is not a version number.

With the engine, an import runs these steps, and a failure at any of them is
undone as described below. The import holds the lock of the image name, so an
import and a build or another import of one name cannot both succeed, and the
lock of its own build folder, so `status` sees a running import as a running
build.

1. `check-name`: an existing image is never replaced. A name that is taken exits
   1 `image_exists` and names `--as`.
2. `check-version`: the manifest's engine version is compared with the engine's.
   SQL Server cannot attach files from a newer version, so a newer *major*
   version exits 1 `version_mismatch`, naming both versions and the engine image
   that built the image. A newer build within the same major version only prints
   a warning on standard error and goes on: cumulative updates keep the file
   format in practice. If a branch from such an image cannot attach,
   `branch create` reports the engine's error.
3. `check-space`: free pool space must be at least the total size of the files in
   the manifest, or the import exits 1 `pool_full`, naming both sizes.
4. `build-folder`: `/pool/build/<id>/data`, the folder `status` lists if the
   client dies.
5. `write`: the client reads the archive and builds its own tar, with only the
   manifest's files, as plain names, regular files, mode 0640, and sends it to
   `tar -xf -` in the engine container. An archive entry is never forwarded as it
   is. Before any byte of an entry is sent, its place in the order, its type
   (regular files only, no links or devices), its name (exactly `data/` and a
   file name of the manifest, no separator, no `..`) and its header size (equal to
   the manifest's size) are checked, and a repeated or unlisted entry is refused.
   Any of these exits 1 `archive_invalid`.
6. `verify`: the SHA-256 and size of every file, computed as it was sent, must
   equal the archive's `sqlbranch-checksums.json` and the manifest, the checksum
   list must be the archive's last entry and match its first, and the engine's own
   `stat` of each file must give the manifest's size. A difference, a missing
   checksum list, or an archive that ends early exits 1 `archive_corrupt`,
   naming the file.
7. `seal` and `manifest`: the same seal as `image build`. The files move into
   `/pool/images/<name>/data` and become read-only, and the manifest, with the new
   name, is written last, so an image exists only when it is complete.

### A failed or cancelled import

When an import fails, or you press Ctrl+C once, SqlBranch first waits for the data
stream into the engine to end: it stops the `docker exec` client, and then makes
sure the `tar` inside the container has ended too, because stopping the client
does not stop it (the import's `tar` records its process id in
`/tmp/sqlbranch-import-<id>.pid` inside the container for this). Only then does it
remove its build folder, and the image folder only if this import created it. An
image folder that was already there, for instance one that a build made between
the name check and the seal, is never removed. If SqlBranch cannot confirm that
the `tar` has ended, it removes nothing and says so, and `sqlbranch status` lists
the build folder.

If the client is killed, nothing can clean up: `sqlbranch status` exits 1 and
lists the import's build folder as a build leftover, as it does for a dead build.
An import has no temporary database. While an import runs, `status` counts it as
a running build.

### Checks before anything runs

These exit 2 before the archive is read and before any `docker` command: an
`--as` that breaks the image name rules (`invalid_name`), an archive that does
not exist, cannot be read, or is a folder, and `--s3-region` with a source that
is not an `s3://` URL or is malformed (`usage_error`). An `https://` or `s3://`
source follows the rules of an export destination above, and its query string is
never echoed. A missing or empty secret that its URL needs exits 2 `config_error`.
For a URL source the archive's first entry is read with one storage request before
any `docker` command, so an object that is missing, or a secret the storage
refuses, exits 1 `storage_unreachable` with the pool unchanged.

### Output

`--format json` returns `from` (the archive as given), `cloneMode`, and `image`,
the object of one `image list` entry, with `branches` 0. Text output is one result
line, the source database, the files and their size, and `Clone mode`. In copy mode
it adds that branch creation copies every file in full. A warning about a newer
build of the same major version goes to standard error. Every file the import wrote
passed through the client once, so no copy of the archive is made on the client.


## Build from a BACPAC

> **Preview.** BACPAC builds need your own SqlPackage. URL sources (Azure Blob, S3) have been tested against MinIO and Azurite only, not real cloud accounts, and Windows is not verified yet. It may change before release 0.3.


In the Azure portal, open the SQL database, select **Export**, and choose
the Storage account and blob container for the BACPAC. You can also run
your own SqlPackage Export action with `/Action:Export` and
`/TargetFile:shop.bacpac`. A transactionally consistent export needs a
write-free source for the duration of the export or a transactionally
consistent copy. [Azure portal export](https://learn.microsoft.com/en-us/azure/azure-sql/database/database-export) · [SqlPackage Export](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-export)

```text
sqlbranch image build shop --from-bacpac ./shop.bacpac
sqlbranch image build shop --from-bacpac ./shop.bacpac --sqlpackage /path/to/sqlpackage
sqlbranch image build shop --from-bacpac https://acme.blob.core.windows.net/db/shop.bacpac
sqlbranch image build shop --from-bacpac s3://s3.amazonaws.com/db/shop.bacpac --s3-region us-east-1
```

Choose one source. `--backup-set` applies to native backups. For Azure Blob, put the SAS in
`SQLBRANCH_AZURE_SAS_TOKEN` and use a URL without a query or fragment. For
S3, set `SQLBRANCH_S3_ACCESS_KEY_ID` and `SQLBRANCH_S3_SECRET_ACCESS_KEY`.
Use `--s3-region` only with S3. SqlPackage is resolved from the explicit
path or PATH. Install it with `dotnet tool install -g microsoft.sqlpackage`.

Invalid flags, names, paths, unreadable files and storage settings fail
before Docker. A readable corrupt package on a new installation gives
`not_initialized`. After initialization, the build checks and locks the
image name before running SqlPackage or reading the package. Invalid
packages give `bacpac_invalid`. `pool_full`
identifies insufficient engine pool space, while `temp_full` identifies
insufficient client space.

SqlPackage imports schema and data into a private database on the SqlBranch
engine. Objects the local SQL Server cannot deploy can fail with
`unsupported_feature` during import. Use a package produced from compatible
objects.

SqlBranch imports login-mapped database users as users without a login,
preserving their database permissions, role memberships, and ownership.
The import skips the package's server logins, and signing in as the
detached users is unsupported. The build result names the detached users
and counts the removed logins and other server objects. In JSON, these
fields are `detachedUsers`, `removedLogins`, and `removedServerObjects`.

Repeat `--post-script` to apply SQL files in the order given after import.
They run as the temporary database owner with a fresh password. An enabled
DDL trigger covering database ownership changes causes `ddl_trigger` before
the scripts run. Disable it at the export source and export a fresh BACPAC, or
omit the post-scripts. The same database state, file, trigger, and guest
restrictions as for other scripted builds apply. Keep the BACPAC build's
data and log file layout intact.

The build records the imported database's recovery model and uses SIMPLE by
default. Use `--keep-recovery-model` to carry the original model into branches.
The engine checkpoints, detaches, and rebuilds a minimal transaction log in
either case. SqlBranch seals the files read-only, removes temporary resources,
and then publishes `manifest.json`. The image records the package basename,
SHA-256 and, for remote packages, the query-free URL. Those fields travel with
the image in archive format 1.

The build reports compressed package bytes before import and warns that
large packages can take hours. The compressed size is the pool capacity
lower bound. Imported data, indexes, and the log can need more space.
Text and JSON results include elapsed milliseconds for attempted download,
rewrite, import, post-script, seal, and cleanup phases, including failed phases.
Live notices go to stderr in JSON mode.

The client download uses authenticated HTTPS with certificate verification
and refuses redirects. It checks the object size with HEAD, then streams at
most that many bytes to disk. An interrupted transfer or a body whose length
differs from HEAD gives `storage_unreachable`.

Client scratch is an owner-only `sqlbranch-bacpac-<build-id>-*` directory
under the client's system temp directory (`Path.GetTempPath()`). On Unix
the directory is mode 0700 and the staged package and ownership marker
are mode 0600. On Windows the directory has an owner-only inheritable
ACL. Provide room for the compressed package and SqlPackage working
files. To detach users or remove package logins, SqlBranch writes a scratch
copy that needs roughly one package size of additional client space.
On success, failure, and the first Ctrl+C, SqlBranch closes the
download and stops and reaps any running SqlPackage process before
checking the scratch identity and deleting the directory. The local
source or storage object is preserved. If cleanup cannot verify or
remove the directory, SqlBranch reports the scratch identifier and
withholds publication.

After forced termination, run `sqlbranch status` and look in the system
temp directory on the original client for the matching scratch prefix.
After confirming that the client and its SqlPackage process have exited,
remove the identified scratch directory. The next eligible BACPAC build
checks abandoned operations' locks, catalog identities and pool paths
before removing their engine resources. Sealing stages the image in
`/pool/build/<id>/image` and publishes it with one `mv` into the images
directory, with `manifest.json` already inside. Use the build ID from
`sqlbranch status` to locate `/pool/build/<id>` and its `bacpac.owner`
record in the owned engine pool. Leave the folder and record for the next
eligible BACPAC build to sweep. If status still reports leftovers after
the sweep, keep the affected resources for inspection.

### Remote integration gate

Run `BacpacProviderIntegrationTests` with
`SQLBRANCH_IT=1`. Set
`SQLBRANCH_IT_SQLPACKAGE` and point `SQLBRANCH_IT_BACPAC_MINIO_URL` and
`SQLBRANCH_IT_BACPAC_AZURITE_URL` at isolated local HTTPS fixtures trusted
by the test client. Seed each URL with a BACPAC containing 17 rows in
`dbo.Items`, and seed the same URL plus `.invalid` with a text object.
Supply the fixture credentials through the storage environment variables.
Run in thin and copy modes, then exercise an untrusted certificate, a
cross-authority redirect, and Ctrl+C during a slow GET. Verify that the
redirect target receives no credentials and that failed builds leave no
publication marker or scratch. On Windows, inspect the scratch DACL and
its inheritance on the staged file before release.
