# Command output, error codes and exit codes

Every `sqlbranch` command prints readable text by default and one JSON
document with `--format json`. Scripts, CI jobs and AI agents should use JSON
and the exit code.

## Streams

- **Standard output** holds the result only: the text lines, or the JSON
  document.
- **Standard error** holds progress messages, warnings (`warning: ...`) and,
  in text format, the error (`error: ...`).

With `--format json`, standard output always parses as one JSON document,
even when the command fails or prints warnings.

## Global options

| Option | Meaning |
|---|---|
| `--format text\|json` | Output format. Default `text`. Case does not matter. |
| `--quiet`, `-q` | In text format, print only the essential result, such as the connection string alone (`branch conn`). |
| `--config <file>` | Use this file instead of the repository's `.sqlbranch.yml`. |
| `--version` | Print the version and exit 0. Always plain text; `--format` does not apply. |
| `--help`, `-h` | Print help and exit 0. Always plain text; `--format` does not apply. |

No command ever waits for input. A command that needs a decision fails and
names the flag that supplies it, for example `--force`.

## The JSON document

```json
{
  "schemaVersion": 1,
  "command": "branch create",
  "ok": true,
  "elapsedMs": 812,
  "branch": { "name": "pr-142", "image": "shop", "cloneMode": "thin", "server": "127.0.0.1,14330", "database": "pr-142", "user": "sa", "ephemeral": false, "expiresAt": null, "labels": {} }
}
```

| Field | Always present | Meaning |
|---|---|---|
| `schemaVersion` | yes | Version of this contract. It is `1`, and changes only when a field is removed or changes meaning. |
| `command` | yes | The command path, for example `branch create`, or `sqlbranch` when no command was recognised. |
| `ok` | yes | `true` on success. |
| `elapsedMs` | yes | Time the command took, in milliseconds. |
| `error` | on failure | An object with `code` (stable, from the table below) and `message` (for people; may change). |
| other fields | per command | The command's result, documented with each command. |

Fields added by the pool format 2 change (`poolFormat`, `version`, `imageVersion`, `imageRemoved` and the like, documented with each command) are additive under `schemaVersion` 1: nothing is removed and no meaning changes. On a format-1 pool they are `null` or `[]`.

On failure:

```json
{
  "schemaVersion": 1,
  "command": "branch create",
  "ok": false,
  "elapsedMs": 35,
  "error": { "code": "image_not_found", "message": "Image 'missing' does not exist." }
}
```

Error messages, warnings and any command line or log text shown in them
never contain the engine password: it is replaced by `***`. The only output
that contains the password is the result of `sqlbranch branch conn`, which
exists to print it (`connectionString` in JSON, the string itself in text and
with `--quiet`).

## Branch expiry and pruning in JSON

| Command | Result fields |
|---|---|
| `branch create` | The `branch` object includes `ephemeral` (boolean), `expiresAt` (UTC timestamp or null), and `labels` (object of string values). |
| `branch list` | Each item in `branches` includes the same three fields and `expired` (boolean). An expired branch can still have `state` equal to `online`. |
| `status` | For a running engine, `branches` counts all branches and `expiredBranches` lists the names past their expiry time. |
| `branch prune` | `removed` and `skipped` list names. `failed` lists objects with `name`, `path` and `message`. All three arrays are present even when empty. |

A prune that removes one expired branch and skips another branch held by a reset produces a document like this:

```json
{
  "schemaVersion": 1,
  "command": "branch prune",
  "ok": true,
  "elapsedMs": 250,
  "removed": ["run17"],
  "skipped": ["run18"],
  "failed": []
}
```

A held lock alone leaves the exit code at 0. If a removal fails, the document
includes the same result arrays alongside `ok: false` and
`error.code: "operation_failed"`. The command exits 1.

For `image build` and `image list`, each image reports `sourceType`, `sourceDatabase`,
`scripts` and `postScripts` (ordered objects with `name` and `sha256`), `ddlTriggers`
and `serviceBroker`, and `backupUrl`, the `https://` or `s3://` URL of a URL build
(null for a file or script build and for images built before 0.3; the URL never
holds a secret). A URL build has `backupFile` set to the last segment of the URL
and a null `backupSha256`. Script images have null `backupFile`, `backupUrl` and
`backupSha256`. A URL build can fail with `backup_unreachable`,
`credential_conflict` or `unsupported_source` (exit 1).
Older manifests default to source type `backup`, empty script and trigger lists,
and null for the unrecorded broker state.

For `image build --from-bacpac`, `detachedUsers` lists the names of database users
converted to users without a login. `removedLogins` and `removedServerObjects`
count the server logins and other server objects removed from the package.
When no rewrite is needed, the list is empty and both counts are zero.
`sourceSizeBytes` and `image.bacpacSha256` describe the supplied package.
`phaseMilliseconds` includes `rewrite` when the rewrite phase ran.

For `image export`, `export` holds `image`, `to` (the destination as given, without any query string), `files`, `imageBytes`
(the files' total size) and `archiveBytes` (the archive's size). There is no `cloneMode`, because an export creates no pool
files. A failed export exits 1 with `image_not_found`, `destination_exists`, `storage_unreachable` (an `https://` or `s3://`
destination) or `operation_failed`; a missing or invalid storage secret exits 2 `config_error`.

For `image import`, the result holds `from` (the archive as given, without any query string), `cloneMode` and `image`, the object
of one `image list` entry with `branches` 0. A failed import exits 1 with `archive_invalid`, `archive_corrupt`, `version_mismatch`,
`image_exists`, `pool_full`, `storage_unreachable` (an `https://` or `s3://` source) or `operation_failed`; a source that cannot be read,
a bad `--as`, a misused `--s3-region` or a missing or invalid storage secret exits 2.
A newer build of the same major version is a warning on standard error and exit 0.

## Status and the pool format

`status` reports the pool format as `poolFormat` (an integer, in the JSON
document next to `images` and `branches`) and as a `Pool format` line in text. The new fields of this release are
additive under schemaVersion 1; on a format-1 pool they are null or `[]` (and `poolFormat` is 1), so that output stays
compatible with 0.2.2. Examples of a running engine, with the unchanged lines left out:

Format 1:

```text
Pool backend: loop-xfs
Pool format: 1
Clone mode: thin
Mismatches: none
```

```json
{ "schemaVersion": 1, "command": "status", "ok": true, "poolFormat": 1, "images": 2, "branches": 1,
  "inProgress": [], "mismatches": [] }
```

Format 2 (the exit code is 0):

```text
Pool format: 2
Mismatches: none
```

```json
{ "schemaVersion": 1, "command": "status", "ok": true, "poolFormat": 2, "images": 2, "branches": 1,
  "inProgress": [], "mismatches": [] }
```

Upgrade in progress. The upgrade lock is held, so `status` shows the upgrade, and its temporary credentials file, as
in progress and exits 0:

```text
Pool format: 1
In progress: pool upgrade is running
Mismatches: none
```

```json
{ "schemaVersion": 1, "command": "status", "ok": true, "poolFormat": 1,
  "inProgress": ["pool upgrade is running"], "mismatches": [] }
```

Upgrade not finished. The credentials say `running` and nobody holds the lock, or a temporary credentials file is left.
`status` exits 1 `mismatch` and names the rerun:

```text
Pool format: 2
Mismatches: 2
  pool upgrade: The pool upgrade did not finish. Commands that change images or branches are refused until it finishes. Run 'sqlbranch pool upgrade --yes' to finish it.
  credentials.json.new: The temporary file /pool/engine/credentials.json.new was left by a pool upgrade that did not finish. Run 'sqlbranch pool upgrade --yes' to remove it.
```

```json
{ "schemaVersion": 1, "command": "status", "ok": false, "poolFormat": 2,
  "mismatches": [
    { "kind": "pool_upgrade", "name": "pool upgrade", "message": "The pool upgrade did not finish. ..." },
    { "kind": "upgrade_leftover", "name": "credentials.json.new", "message": "The temporary file ... was left by a pool upgrade that did not finish. ..." }
  ],
  "error": { "code": "mismatch", "message": "status found 2 mismatches: pool upgrade (pool_upgrade); credentials.json.new (upgrade_leftover)" } }
```

On a format-2 pool, an image whose `catalog.json` cannot be parsed is a mismatch of kind `image_catalog`, named by the image,
and `status` exits 1 `mismatch`. The message names the repair, `sqlbranch image rm <name>`:

```text
Mismatches: 1
  crm: The catalog of the image 'crm' is not valid. Run 'sqlbranch image rm crm' to remove it.
```

```json
{ "schemaVersion": 1, "command": "status", "ok": false, "poolFormat": 2,
  "mismatches": [
    { "kind": "image_catalog", "name": "crm", "message": "The catalog of the image 'crm' is not valid. Run 'sqlbranch image rm crm' to remove it." }
  ],
  "error": { "code": "mismatch", "message": "status found 1 mismatch: crm (image_catalog)" } }
```

## Image list and image rm on both formats

`image list` adds `catalogReadable`: true on format 2, false for an unreadable catalog, and null on format 1. An unreadable catalog has `version` null and is marked "catalog unreadable" in text.

`image list` adds `version` to each image: the ID of the name's current version on a format-2 pool, null on a format-1
pool and for an image an older client wrote after the upgrade and no command has adopted yet. `image rm` adds
`image.versions`, the IDs removed with the name; it is `[]` on a format-1 pool and for an image with no catalog.

```json
{ "command": "image list", "ok": true,
  "images": [ { "name": "shop", "version": null, "catalogReadable": null, "branches": 1, "branchNames": ["dev1"], "...": "..." } ] }
```

```json
{ "command": "image list", "ok": true,
  "images": [ { "name": "shop", "version": "shop@20261201T090000000Z", "catalogReadable": true, "branches": 1, "branchNames": ["dev1"], "...": "..." } ] }
```

The first document is a format-1 pool and the second a format-2 pool. In text the format-2 table has a `VERSION`
column after `BRANCHES`; the format-1 table is the 0.2.2 table.

```json
{ "command": "image rm", "ok": true,
  "image": { "name": "crm", "removed": true, "forced": false, "branches": [], "versions": [] } }
```

```json
{ "command": "image rm", "ok": true,
  "image": { "name": "crm", "removed": true, "forced": false, "branches": [], "versions": ["crm@20261201T090000000Z"] } }
```

Again the first is a format-1 pool and the second a format-2 pool.

## Build, import and branch version fields

`image build` and `image import` add `image.version`: the version ID on a format-2 pool, null on a format-1 pool.

```json
{ "command": "image build", "ok": true,
  "image": { "name": "shop", "version": null, "cloneMode": "thin", "...": "..." } }
```

```json
{ "command": "image import", "ok": true, "from": "shop.sbimg", "cloneMode": "thin",
  "image": { "name": "shop3", "version": "shop3@20261201T090000000Z", "branches": 0, "...": "..." } }
```

The first document is a format-1 pool and the second a format-2 pool; `image build` on a format-2 pool has the same shape
with an ID, for example `"version": "shop@20261201T020000000Z"`.

`branch create` and `branch reset` add `branch.imageVersion`, and each `branch list` row adds `imageVersion`: the base version
ID on a format-2 pool, null on a format-1 pool, for an unknown base and for a base removed before the upgrade. On a format-2
pool `imageRemoved` is true when the base version is no longer in the name's catalog, also after the name was used again.

```json
{ "command": "branch create", "ok": true,
  "branch": { "name": "dev2", "image": "shop", "imageVersion": "shop@20261201T090000000Z", "cloneMode": "thin", "...": "..." } }
```

```json
{ "command": "branch reset", "ok": true,
  "branch": { "name": "dev2", "image": "shop", "imageVersion": "shop@20261201T090000000Z", "cloneMode": "thin", "...": "..." } }
```

```json
{ "command": "branch list", "ok": true,
  "branches": [ { "name": "dev1", "image": "shop", "imageVersion": "shop@20261119T020000000Z", "imageRemoved": true, "state": "online", "...": "..." },
                { "name": "dev0", "image": "shop", "imageVersion": null, "imageRemoved": false, "state": "online", "...": "..." } ] }
```

In text, an `Image version:` line follows the first line of `branch create` and `branch reset`, and the `IMAGE` column of
`branch list` shows the version ID instead of the name, followed by `(removed)` when it was removed. A format-1 pool prints
the 0.2.2 text.

## Pool upgrade

`sqlbranch pool upgrade --yes` moves a format-1 pool to format 2. `pool` is the new format and `previousFormat` the one
before (2 when the pool was already upgraded, then nothing else changes and `images` is `[]`); `images` lists each image the
upgrade processed with its first version ID, by name; `branches` names the branches by the base they got in three lists: `pinned`, `unknownBase` and `removedBase`. A completed rerun reports empty lists.

```json
{ "schemaVersion": 1, "command": "pool upgrade", "ok": true,
  "pool": { "format": 2, "previousFormat": 1 },
  "images": [ { "name": "crm", "version": "crm@20261201T090000000Z" },
              { "name": "shop", "version": "shop@20261201T090000000Z" } ],
  "branches": { "pinned": ["dev1"], "unknownBase": ["old1"], "removedBase": ["pr-9"] } }
```

```json
{ "schemaVersion": 1, "command": "pool upgrade", "ok": true,
  "pool": { "format": 2, "previousFormat": 2 }, "images": [],
  "branches": { "pinned": [], "unknownBase": [], "removedBase": [] } }
```

The first document is a format-1 pool being upgraded at 2026-12-01T09:00:00.000Z and the second a rerun on a pool that is
already format 2. The text output of the first run:

```
The pool now uses format 2 (it used format 1).
Image crm: version crm@20261201T090000000Z
Image shop: version shop@20261201T090000000Z
Branches pinned to the first version: dev1
Branches with an unknown base (they cannot be reset): old1
Branches whose image was already removed (they cannot be reset): pr-9
```

and of the rerun: `The pool already uses format 2. Nothing was changed.` In text and in JSON format, an upgrade that is going to
write prints one line on standard error before its first write:

```
warning: The pool upgrade cannot be undone. SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x packages can no longer use the pool after it.
```

While locks are busy, progress goes to standard error, for example:

```text
Waiting for image 'shop', branch 'dev4' before upgrading the pool.
```

The wait is bounded by `publishWait`; a timeout names every still-busy image and branch and asks for `sqlbranch pool upgrade --yes` again.

The command exits 2 `usage_error` before any Docker command when `--yes` is missing, repeated or given a value, or when an
operand or another option is given; 3 `not_initialized` or `engine_stopped` when there is no running engine; 1
`catalog_busy` when another upgrade is running or an image or branch is still in use; 1 `pool_full` when the pool has no
room (before the first write the pool stays format 1, afterwards `sqlbranch pool upgrade --yes` finishes it).

## Doctor output

`sqlbranch doctor --format json` returns a `checks` array. Each entry contains
`id`, `title`, `status` and `detail`. Text output shows the same status in
brackets before the check title.

SqlPackage is needed only for `--from-bacpac`. When no `--sqlpackage` path is
given and doctor finds no usable SqlPackage on PATH, the `sqlpackage` check has
status `optional`. Its detail says it is missing and shows
`dotnet tool install -g microsoft.sqlpackage`. The optional check leaves the
exit code unaffected. A usable executable reports `pass` with its path and
version. An explicit path that is missing or cannot report a version reports
`fail`, names the path and exits 3 with `sqlpackage_missing`.

## Skill export

`sqlbranch skill export --dir <path> --format json` returns `written`, an array of
paths relative to the destination, including `SKILL.md` and the reference files.
The command works before initialization. An unwritable destination or parent
produces `usage_error` (exit 2). A staging or installation failure produces
`operation_failed` (exit 1).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | The operation was attempted and failed. |
| 2 | Usage or configuration error: fix the command line or the settings. |
| 3 | The environment is not ready: Docker, the engine or the pool. |

## Error codes

Match on `error.code`, not on the message. The exit code is fixed by the
error code.

When an explicit `--sqlpackage` path fails its check and Docker also fails,
`sqlbranch doctor` reports `sqlpackage_missing` and includes both failed checks.
Without an explicit path, Docker failure reports `docker_unavailable`.

| `error.code` | Exit | When |
|---|---|---|
| `usage_error` | 2 | The command line could not be parsed, an option is unknown, or no command was given. |
| `config_error` | 2 | A config file, environment variable or setting is invalid. |
| `invalid_name` | 2 | An image or branch name breaks the naming rules or is reserved. |
| `docker_unavailable` | 3 | The `docker` command is missing or its daemon is not reachable. |
| `not_initialized` | 3 | No SqlBranch engine or pool exists yet. Run `sqlbranch init`. |
| `check_failed` | 3 | A `sqlbranch doctor` check failed. The report names it. |
| `sqlpackage_missing` | 3 | A `--from-bacpac` build could not find a usable SqlPackage, or `doctor` could not read the version from an explicit `--sqlpackage` path. The report includes installation guidance. |
| `engine_not_ready` | 3 | The engine did not accept connections within 120 seconds. |
| `engine_stopped` | 3 | The engine container is not running. Run `sqlbranch engine start`. |
| `pool_not_mounted` | 3 | The managed pool is not mounted and could not be remounted. |
| `pool_unavailable` | 3 | The managed pool could not be created or mounted and `poolBackend` is `loop-xfs`, so there is no fallback to copy mode. The message names the cause. |
| `host_disk_full` | 3 | The container host has too little free space for the managed pool. |
| `thin_required` | 3 | `requireThin` is on and the clone mode is `copy`. |
| `foreign_container` | 3 | A container named `sqlbranch-engine` exists that SqlBranch did not create. |
| `init_failed` | 1 | A first `init` failed; what it created was removed. |
| `pool_full` | 1 | The pool has too little free space; the message names what is needed and available. |
| `temp_full` | 1 | Client scratch ran out of space. |
| `bacpac_invalid` | 1 | The readable file is not a valid BACPAC. |
| `image_not_found` | 1 | The named image does not exist. |
| `skill_exists` | 1 | A generated file already exists at the destination. Use `--force` to refresh it. |
| `image_exists` | 1 | An image with that name already exists. |
| `image_in_use` | 1 | The image still has branches and `--force` was not given. |
| `branch_not_found` | 1 | The named branch does not exist. |
| `branch_exists` | 1 | A branch or database with that name already exists. |
| `not_a_backup` | 1 | The engine cannot read the file as a backup. |
| `unsupported_backup_type` | 1 | The backup is not a full database backup. |
| `guest_database` | 1 | A database allows guest access or its guest permissions could not be read. |
| `script_failed` | 1 | A script failed, timed out, or left the database unusable. |
| `ddl_trigger` | 1 | An enabled ownership-change trigger prevents post-build scripts from running. The message names the trigger. |
| `unsupported_feature` | 1 | The database uses FILESTREAM or a memory-optimized filegroup. |
| `version_mismatch` | 1 | The backup, or the image in an `image import` archive, was made by a newer SQL Server version than the engine. |
| `backup_unreachable` | 1 | The engine could not reach, authenticate to, verify the certificate of, or find the backup at its URL. The message has the URL (never a secret) and the engine's reason. |
| `credential_conflict` | 1 | The engine already has a credential with the name a URL build needs and SqlBranch did not create it. Nothing was changed. |
| `unsupported_source` | 1 | The engine cannot read this kind of backup URL: an `s3://` URL needs SQL Server 2022 or later. The message names the engine version and `engineImage`. |
| `destination_exists` | 1 | `image export` found its `--to` file already there (or created by someone else during the export) and `--force` was not given. The message names the destination and `--force`. Nothing was changed. |
| `archive_invalid` | 1 | The file is not a SqlBranch image archive, uses a compression or archive format this version cannot read, or holds an entry or manifest `image import` will not accept (a link, an unlisted, repeated or out-of-order entry, a header size that differs from the manifest's, or a manifest with an unsafe file name). It is also what a machine without an engine reports for such an archive, instead of `not_initialized`. |
| `archive_corrupt` | 1 | An archive's SHA-256 or size differs from its checksum list, the checksum list is missing, or the archive ends early. The message names the file. |
| `storage_unreachable` | 1 | `image export`, `image import` or a BACPAC build could not reach the storage at its `https://` or `s3://` URL, was refused its secret, did not trust its certificate, found no object, or could not finish reading it. The message has the URL without its query and the reason (never a secret). |
| `mismatch` | 1 | `status` found state that does not match, such as a branch folder with no database. |
| `catalog_busy` | 1 | A command waited `publishWait` for running image or branch operations and gave up, or met an operation it must not wait for. The message names what is busy. |
| `pool_upgrade_required` | 3 | A pool upgrade did not finish, or the command needs pool format 2. The message names `sqlbranch pool upgrade --yes`. |
| `operation_failed` | 1 | The operation failed for a reason with no more specific code. |
