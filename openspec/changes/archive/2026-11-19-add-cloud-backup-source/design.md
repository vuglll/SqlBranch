# Design

## Context

`ImageBuilder` (`src/SqlBranch.Core/Images/ImageBuilder.cs`) builds a backup
image in these steps: `check-name`, `check-backup-space`, `stage` (copy the
`.bak` into `/pool/build/<id>/` with `docker cp` and hash it with `sha256sum`),
`inspect` (`BackupInspector`: LABELONLY, HEADERONLY, FILELISTONLY from DISK),
`check-restore-space`, `restore` (`RESTORE DATABASE ... FROM DISK = @bak WITH
FILE = @file, MOVE ...`), then the shared seal steps. Every engine error,
`docker` command line and log excerpt passes through one `SecretRedactor`.
Build locks are engine application locks held by the build's session;
`status` uses them to tell a running build from a dead one.

The facts this design rests on, with sources, are in
`docs/research/2026-11-18-cloud-backup-source-facts.md`.

## Goals / Non-Goals

**Goals:**

- One new source kind inside the existing backup path: everything after the
  restore (post-scripts, checks, seal, manifest) is unchanged.
- A storage secret lives in the engine for one build only, and nowhere else.
- CI coverage without a cloud account for S3; opt-in coverage for real Azure
  and AWS.

**Non-Goals:**

- A storage SDK in the CLI. The engine does all storage I/O.
- Retries, resumable restores, progress beyond what the restore reports today.
- Any `status` change beyond listing a leftover build credential.

## Decisions

### D1. URLs go through `--from-bak`

A `--from-bak` value starting with `http://`, `https://` or `s3://` (ordinal,
ignoring case) is a URL; anything else is a path, as today. `http://` is
parsed only to be refused with exit 2.

Alternatives considered:

- A new `--from-url` flag. Clearer in help text, but it adds a third source to
  "Exactly one image source" and splits one concept (a native backup) in two.
- A separate `image build-from-cloud` command. Duplicates every build option.

### D2. The engine restores straight from the URL

`RESTORE LABELONLY | HEADERONLY | FILELISTONLY | DATABASE ... FROM URL = @url`.
The URL is a parameter. `WITH FILE` is omitted, because `RESTORE FROM URL` does
not support it (research file), which is why a URL build takes set 1 only. An
`s3://` build adds `RESTORE_OPTIONS = '{"s3": {"region":"<region>"}}'` to the
restore; the region is checked against `^[a-z0-9-]{1,32}$` before it is
formatted into the statement, so it cannot break out of the JSON literal.

URL step list: `check-name` (followed by the login and credential sweeps),
`check-source` (S3 needs engine major version 16 or later, else
`unsupported_source`; a SAS token over 256 characters on an engine below 16
exits 2 `config_error`, because the token is a setting the user must change),
`build-folder` (creates `/pool/build/<id>/`, which the file path does inside
`stage`), `credential`, `inspect`, `check-restore-space`, `restore`,
`drop-credential`, then the shared seal steps. `check-backup-space` and `stage`
do not run, so the pool needs room only for the restored files.

All URL and secret checks of D1 and D3 run in `ImageCommands.Build` before
`CommandContext.RequireAsync`, so bad input never reaches a `docker` command.
That includes the 128-character credential name limit (D4).

Alternatives considered:

- Download on the client, then the file path. This is what users do today; it
  doubles disk and time, which is the problem F1 solves.
- Download inside the engine container (`curl` or `azcopy`) into the build
  folder. Needs tools the image does not ship, keeps the pool cost of staging,
  and puts the secret into a process command line inside the container.
- Stream the blob through the client into `docker cp`. Still moves every byte
  through the client and needs a storage SDK.

**Unverified:** whether LABELONLY, HEADERONLY and FILELISTONLY need
`RESTORE_OPTIONS` for a bucket outside `us-east-1`, and whether they accept it.
Spike 1.1 runs all four statements against a bucket in `eu-west-1`.

### D3. Secrets come only from environment variables

`SQLBRANCH_AZURE_SAS_TOKEN`, `SQLBRANCH_S3_ACCESS_KEY_ID`,
`SQLBRANCH_S3_SECRET_ACCESS_KEY`. They are read only when the URL needs them,
checked before any `docker` command, and registered with the redactor at once.
One leading `?` of a SAS token is dropped. An S3 key or secret with `:` is
refused (the connector joins them with `:`). The S3 credential secret is
`<id>:<secret>`. The redactor gets every form a secret can take in text: the
S3 id, the secret and the joined string; the SAS token and its `sig` value on
its own; and each of these with `'` doubled, as they appear in the `CREATE
CREDENTIAL` statement.

Alternatives considered:

- Flags (`--sas-token`). Visible in shell history and to other users in the
  process list.
- Config file keys. Config files are committed to repositories; the existing
  rule refuses `password` keys for the same reason.
- Accept a pasted SAS URL and split off the query. Convenient, but the secret
  then sits in the flag value (process list, shell history, CI logs that echo
  commands). Refused with exit 2 and a message that names the variable; the
  message never echoes the query string.
- A `--sas-token-file` flag. Safer than a flag value, but one more input path
  to check; can be added later without breaking anything.

### D4. One short-lived server credential per build

SQL Server finds a SAS or S3 credential by name, so the name must be a prefix
of the URL. The build uses the URL up to its last `/` (for
`s3://h:9000/backups/nightly/shop.bak` that is `s3://h:9000/backups/nightly`).
For S3 the documented examples name credentials after folders. For Blob the
documented name is the container URL; whether a deeper folder name is found
is unverified, and spike 1.1 decides. If it is not, Blob builds use
`https://<host>/<container>`; the spec scenarios use files directly in the
container, so they hold either way.

A credential name longer than 128 characters (the engine's limit) exits 2
before any action.

**The marker.** `/pool/build/<id>/credential.json` holds `name`, and after the
credential exists also `credentialId` and `createDate` read back from
`sys.credentials`. It never holds the secret. A marker without `credentialId`
is an *intent marker*: the build may have died before or during `CREATE`, so
nothing proves the credential with that name is SqlBranch's.

**The lock.** Application lock `sqlbranch_cred_<first 16 hex of SHA-256 of the
name>`, session-owned. `EngineLocks` only tries (`@LockTimeout = 0`) and engine
commands time out after 120 s, so the build waits in a client loop: try the
lock, and if it is held, print "Waiting for another build that uses <name>"
on standard error once and try again every 2 seconds, with no overall limit.
The build's cancellation token ends the loop at once; a cancelled wait has
created nothing, so cleanup removes only the build folder.

Order inside the build, all under the build's engine session:

1. **Sweep** (after `check-name`, next to the login sweep): for each
   `/pool/build/<id>/credential.json` whose build lock is free, *try* the
   credential lock without waiting; if it is held, skip that marker (a running
   build uses the name). Otherwise: an intent marker is deleted and nothing is
   dropped; a full marker leads to `DROP CREDENTIAL` only when a row in
   `sys.credentials` has that name, that `credential_id` and that
   `create_date`, and the marker is deleted right after a successful drop (or
   when no matching row exists). Then release the credential lock. A failed
   drop keeps the marker and warns. Credentials without a marker are never
   touched.
2. **Build folder** (`build-folder` step).
3. **Lock**: the waiting loop above.
4. **Re-sweep this name**: under the lock, apply step 1's rules to every
   marker with this name (other dead builds' markers).
5. **Conflict check**: if `sys.credentials` still has the name, exit 1
   `credential_conflict`.
6. **Intent marker**: write `credential.json` with `name` only.
7. **Create**: `CREATE CREDENTIAL <QUOTENAME(name)> WITH IDENTITY = 'SHARED
   ACCESS SIGNATURE' | 'S3 Access Key', SECRET = N'<secret, quotes doubled>'`.
   `CREATE CREDENTIAL` takes no variables, so this is the one statement whose
   text holds the secret. It is sent over the loopback connection, never
   written to a file, never logged by SqlBranch, and any error text about it is
   redacted (all forms of D3).
8. **Full marker**: read `credential_id` and `create_date` back and rewrite
   the marker with them.
9. **Restore** (`inspect`, `check-restore-space`, `restore`).
10. **Drop**: `DROP CREDENTIAL`, delete the marker, release the lock. This runs
    before `post-scripts`, so no script can use the secret.
11. **Failure cleanup**: after a failure or cancel at any step after 7, the
    cleanup first drops the credential, matching id and date as in step 1.
    A failed drop is added to the leftovers message (exit 1) and the marker is
    kept.

**Keeping the marker.** Both places that remove build folders keep a
`credential.json` whose credential was not dropped: `CleanupAsync` in
`ImageBuilder.cs` and the leftover-login sweep's `rm -rf` in
`ImageBuilder.Scripts.cs`. Each deletes everything else in the folder; a folder
that holds only the marker stays until the credential is dropped. `status`
reads full markers of dead builds whose credential still exists (same id and
date) and lists them as a build leftover with the name only; that is the
engine "Status reports mismatches" delta.

Safety and failure rules per step:

| Step | Safety rule | On failure |
|---|---|---|
| Sweep and re-sweep | Skip a marker whose credential lock is held; drop only on an exact name, id and date match; intent markers drop nothing | Warn, keep the marker, continue |
| Create | Only under the lock, only when the name is absent, intent marker first | Exit 1 (`operation_failed`); no credential, the intent marker goes with the folder |
| Restore reads | Secret only in the engine credential | Exit 1 (`backup_unreachable` or the existing codes); credential dropped |
| Drop after restore | Only the credential this build created (id and date) | Exit 1, image not sealed, no script run; marker kept, `status` lists it, next build drops it |
| Cleanup drop | Same | Named in the leftovers message; marker kept |
| Folder removal | Never delete a marker whose credential still exists | The existing leftover reporting |

Alternatives considered:

- `RESTORE ... WITH CREDENTIAL = 'sqlbranch_build_<id>'`. A private name would
  make leftovers trivial to find, but for Blob `WITH CREDENTIAL` works only
  with storage-key credentials, which Linux does not support.
- Keep one credential per container for reuse. Leaves a secret in the engine
  between builds, and a stale token would make later builds fail confusingly.
- Wait on the lock inside the engine (`@LockTimeout = -1`). Hits the 120 s
  command timeout and cannot be cancelled from the client cleanly.
- Drop by name only, as the first version of this design did. A stale marker
  could then drop a credential a user or a running build made later.
- Container-level names always. Two builds from different folders of one
  container would then contend for one lock for no reason; kept as the Blob
  fallback.
- Record created credentials in a table in `master`. Writes SqlBranch state
  into the engine's system database; the pool marker sits next to the other
  build leftovers instead.

### D5. Error mapping

`BackupInspector.MapError` gains a URL mode. Engine errors 3201 ("cannot open
backup device") and 3013 that follow it, with an OS or HTTP reason, map to
`backup_unreachable`; the message is the URL (no query string, which D1
already refused) plus the engine's reason, redacted. A certificate failure is
recognised by the engine's text and gets the hint about trusted certificates.
Striping (3132 or `FamilyCount` > 1) and version errors (3169 and the 3241
case) keep their codes. The exact reason texts for S3 and Blob failures are
captured in spikes 1.1 and 1.2 and pinned in unit tests.

Alternatives considered: one generic `operation_failed`. Scripts could not tell
a bad secret from a bad backup.

### D6. Manifest records the URL, not a hash

`backupUrl` is the URL as given (it cannot hold a query string). `backupFile`
is its last path segment, URL-decoded. `backupSha256` is null: SqlBranch never
reads the backup whole.

Alternatives considered:

- Hash inside the engine by reading the blob again. Doubles the transfer of a
  large backup.
- Record the storage ETag or Last-Modified. Needs a storage API call from the
  client (SDK, network path from the client, secret on the client). Belongs to
  F4 `add-image-refresh`, which needs change detection anyway.

### D7. Managed Instance hint is static text

The version-mismatch message gets one more sentence for every backup build.
Managed Instance backups are not detected.

Alternatives considered: detect a Managed Instance backup from HEADERONLY
fields (server name pattern). Unverified without a real backup, and the hint
is useful for a downloaded Managed Instance backup too.

### D8. Region as a flag with SQL Server's default

`--s3-region`, default `us-east-1`.

Alternatives considered:

- Parse the region from AWS host names (`s3.<region>.amazonaws.com`). Saves a
  flag for AWS, but host formats vary (dual-stack, FIPS, access points); can be
  added later as a default.
- An environment variable or config setting. The region is not a secret and
  belongs to the URL, so it travels with the command.

### D9. Public CA trust, only if needed

Spike 1.1 checks whether a stock engine trusts AWS's and Azure's certificates.
If it does, the engine requirement "Engine trusts public storage certificates"
holds with no code and is proven by the opt-in cloud tests. If it does not,
`init` (first creation and recreation of the engine container) copies the
container's system CA bundle, split into one file per certificate, into
`/var/opt/mssql/security/ca-certificates` before SQL Server starts (task 4.6).
The folder holds at most 50 certificates, so only the roots that sign Azure
Blob Storage and Amazon S3 endpoints are copied; the list is pinned in a unit
test and in the research file.

Alternatives considered: a user-supplied CA flag (out of scope, kept for
private S3 endpoints later); mounting the host's CA folder (differs per host
OS and Docker Desktop).

### D10. Testing without a cloud account

- **Unit tests** on the fake runtime and fake connector: URL parsing, every
  exit-2 case with "no `docker` command ran", statement order (sweep, lock,
  conflict, marker, create, reads, restore, drop, seal), cleanup after a
  failure at each step, redaction of all three secrets in every error.
- **Integration tests** (`SQLBRANCH_IT=1`): the test fixture starts its own
  MinIO container with a test CA and server certificate whose SAN holds the
  container's bridge IP, uploads a backup with a test-only access key, copies
  the CA into the SqlBranch engine's `/var/opt/mssql/security/ca-certificates`
  and restarts the engine with `sqlbranch engine stop` and `start`. The fixture
  removes only the MinIO container it created (by its own label). Test access
  keys are generated per run and never printed. Passing them to the MinIO
  container's settings is the one test-only exception to "Where storage
  secrets may appear". Keys are long random strings,
  so redaction cannot hit parts of URLs or names.
- **Opt-in cloud tests**, skipped unless set: `SQLBRANCH_IT_AZURE_URL` with
  `SQLBRANCH_AZURE_SAS_TOKEN`, and `SQLBRANCH_IT_S3_URL` with the S3 variables
  and `SQLBRANCH_IT_S3_REGION`. Run by hand before the 0.3 release; results go
  into the research file.

Alternatives considered:

- Azurite for Blob. Unverified that SQL Server accepts it as a URL source
  (path-style host, HTTPS with a test CA); spike 1.2. If it works, it joins the
  integration suite.
- LocalStack for S3. Heavier, and MinIO is on Microsoft's provider list.
- Real clouds only. Needs secrets in CI, which the project avoids.

## JSON output changes

`schemaVersion` stays 1. Additive only.

- `image build` and `image list`: each image gains `backupUrl` (string or
  null). For a URL build `backupFile` is the last path segment and
  `backupSha256` is null. Older manifests read `backupUrl` as null. Older CLIs
  ignore the unknown field (System.Text.Json default).
- Text output of `image list`: the source column shows the URL for URL images.
- New `error.code` values, all exit 1: `backup_unreachable`,
  `credential_conflict`, `unsupported_source`. Bad URLs and `--backup-set` or
  `--s3-region` misuse use the existing `usage_error` (exit 2); missing or bad
  secrets use `config_error` (exit 2).
- `image build` JSON gains no secret-bearing field. `cloneMode` is reported as
  for any build.

## Risks / Trade-offs

- [The stock engine may not trust AWS's public CA; the Linux page says extra
  CAs go in a folder read at start-up] → Spike 1.1 with a real bucket before
  any code; if it fails, D9's CA step is built (decided by the human).
- [Managed Instance backups may always carry an `XTP` memory-optimized
  filegroup, which SqlBranch rejects] → Spike 1.3. If so, F1 ships for SQL
  Server on VMs and RDS only, and Managed Instance goes in a later change
  (decided by the human). The docs say so plainly.
- [A killed build leaves a secret in the engine's `master` until the next
  build or `teardown`] → The credential is encrypted by the engine's service
  master key, the engine listens on loopback, `status` lists it, and the next
  build's sweep drops it.
- [Waiting on a shared credential name can last as long as another restore] →
  The message names the other build's credential; Ctrl+C ends the wait.
- [The credential statement text holds the secret] → One statement, loopback
  only, redacted in every error; nothing SqlBranch writes contains it.
- [Large backups are striped (Blob blocks limit about 195 GB, RDS up to 10
  files)] → Clear `unsupported_backup_type` message; striped support is a later
  change.
- [DBCC CHECKDB can report error 8992 on Managed Instance databases after an
  index drop] → Documented in `docs/images.md`; not caused by SqlBranch.
- [Restore throughput depends on the network between engine and storage] →
  Out of SqlBranch's control; documented.

## Migration Plan

No migration. Manifests gain one nullable field. Rolling back to an older CLI
keeps working images; URL images just show no URL there.
