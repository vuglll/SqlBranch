# Design

## Context

An image lives in the pool as `/pool/images/<name>/data/<files>` (mode 444)
plus `/pool/images/<name>/manifest.json`, written last, so an image exists only
when its manifest does (`ImagePaths`, `ImageBuilder.SealAsync`). The manifest
(`ImageManifest`, format 1) already holds the name, engine version, engine
image and each file's logical name, file name, type and size. It holds no
per-file hash.

Builds work in `/pool/build/<id>/` under an engine application lock that
`status` uses to tell a running build from a dead one, and take a lock on the
image name so two builds of one name cannot both succeed. All file work on the
pool happens inside the engine container; the CLI runs `docker`.

F1 parses `https://` and `s3://` URLs, reads the storage secrets from three
environment variables before any `docker` command, and registers every form of
them with `SecretRedactor`. In F1 the engine does all storage I/O. That does
not work here: SQL Server can read and write backups at a URL, but not
arbitrary files.

Facts and sources: `docs/research/2026-11-19-image-export-import-facts.md`.

## Goals / Non-Goals

**Goals:**

- One archive file per image that can sit on a disk, a share, Azure Blob
  Storage or S3, and import into any SqlBranch pool whose engine is at least
  as new.
- No temporary copy of the archive on the client, and none in the pool beyond
  the image's own files.
- Every destructive step (replacing a file or object, deleting a partial file
  or upload, removing a failed import) is safe, fails with a clear code, and is
  cleaned up on every failure path.

**Non-Goals:**

- Parallel or resumable transfers, progress beyond one line per 10 seconds on
  standard error.
- Any change to `image build`, `branch` commands or the manifest format.

## Decisions

### D1. Two commands under `image`

`image export <name> --to <dest> [--force] [--s3-region <r>]` and
`image import <source> [--as <name>] [--s3-region <r>]`. The destination or
source is a URL when it starts with `http://`, `https://` or `s3://` (ordinal,
ignoring case), exactly as F1's D1; anything else is a local path. `http://`
is parsed only to be refused.

Bad input (name rules, missing `--to`, directory or missing parent folder,
unreadable source, URL shape, `--s3-region` misuse, missing or bad secrets)
is checked in `ImageCommands` before `CommandContext.RequireAsync`, so no
`docker` command and no storage request runs. The URL checks reuse F1's parser,
refactored to take the option name (`--from-bak`, `--to`, the import source)
and its length limits as parameters. `image build` keeps the 259-character URL
and 128-character credential name limits; export and import pass none, because
those are SQL Server limits and do not apply when the client does the I/O
(decided by the human). An export destination under a directory pool's root is
refused, so an archive can never land among pool files; a volume pool is not
reachable as a client path anyway.

Alternatives considered:

- `image push` / `image pull`. Suggests a registry, which SqlBranch does not
  have.
- Export to standard output (`--to -`). Handy for pipes, but makes
  "never overwrite" and cleanup the caller's job. Can be added later.
- `--from` on import. Import has exactly one required input, so a positional
  argument is shorter and matches `image rm <name>`.

### D2. Archive format: compressed tar, manifest first, checksums last

One compressed stream of a PAX tar (`System.Formats.Tar`) with these entries,
in this order:

1. `sqlbranch-archive.json`: `{ "archiveFormat": 1, "manifest": { ... } }`,
   the image manifest exactly as stored.
2. `data/<fileName>` for each file in manifest order, regular files only.
3. `sqlbranch-checksums.json`: the SHA-256 and size of each data file and the
   SHA-256 of entry 1.

The manifest comes first so that import can check the archive format, name,
engine version and space before any byte reaches the pool. The checksums come
last because export computes them while streaming; no second pass over 50 GB.

Compression: GZip `Fastest` by default, built in, no new dependency (decided
by the human). zstd through `ZstdSharp.Port` (pure managed, MIT; .NET 10 has no
built-in zstd) replaces it only if spike 1.1 shows it clearly better on the
50 GB image, at least 1.5 times faster at a similar or smaller size. The
compression is recorded by the stream's own magic bytes (gzip `1f 8b`, zstd
`28 b5 2f fd`); the reader detects it, refuses any other as `archive_invalid`,
and, if zstd is chosen, reads both, so a later switch never breaks old
archives. The suggested file extension is `.sbimg`; none is
required.

Alternatives considered:

- Zip. Its directory is at the end, so streaming reads from storage are
  awkward, and Zip64 is needed above 4 GiB.
- A native SQL Server backup (`BACKUP TO URL`, import as `image build
  --from-bak`). Reuses F1, but loses the manifest (post-scripts, original
  recovery model, triggers, Service Broker state), needs a temporary database,
  and rebuilds the image instead of copying it.
- Uncompressed tar. Database files have much free space; archives would be
  several times larger.
- Brotli (built in). Slow at useful levels; measured as a third option in
  spike 1.1 only if gzip is the fallback.

**Unverified:** times and sizes for 10 GB and 50 GB images (spike 1.1), and
that `System.Formats.Tar` handles entries over 8 GiB in PAX format (unit test
in 2.1).

### D3. Bytes move through `docker exec ... tar`

Export runs `docker exec sqlbranch-engine tar -C /pool/images/<name>/data
-cf - -- <file>...` with the file names from the manifest and reads its
standard output. Import runs `docker exec -i sqlbranch-engine tar -C
/pool/build/<id>/data -xf - --no-same-owner --no-same-permissions` as the
container's default user (`mssql`) and writes to its standard input a tar the
client builds itself: only the manifest's files, plain names, regular files,
fixed mode 0640. The client never forwards an archive entry to the container
as is, so a link, device or `../` entry in a crafted archive never reaches
`tar` in the container; it is refused as `archive_invalid` (D7).

Both engine images ship GNU tar (research file). `docker exec` carries no
secret: storage I/O happens in the client (D5).

Today `IProcessRunner` and `ExecAsync` collect standard output and error as
strings. A new streaming exec on the container runtime (and on the fake
runtime for tests) exposes the process's standard input or output as a
`Stream`, collects only standard error, and on cancellation kills the
`docker exec` client and waits for it to exit (task 2.0).

Export fails (`operation_failed`, exit 1) when the engine's `tar` exits non-zero
or when any entry's size differs from the manifest; the D6 or D5 cleanup then
runs. An image removed during export therefore never yields an archive.

Alternatives considered:

- `docker cp -` in both directions. Docker's documentation says it cannot copy
  mounts created inside the container, which may cover the loop-xfs pool; the
  page itself suggests `tar` through `docker exec`.
- Writing to a temporary file on the client first. Doubles disk use and time
  on the machine the draft wants to keep light.

### D4. SHA-256 per file, checked while streaming

Export hashes each data file as its bytes pass through the client. Import
hashes each file as it passes to the container, then compares hashes and sizes
with `sqlbranch-checksums.json`, and also checks each file's size in the
container with `stat` before sealing. Any difference, a missing checksum entry,
or a stream that ends early is `archive_corrupt`, naming the file.

Alternatives considered:

- One SHA-256 of the whole archive in a sidecar file. Two objects to keep
  together, and the check can only happen after the whole download.
- `sha256sum` inside the container after writing. Catches disk errors too, but
  reads every byte a second time; the size check covers truncation.
- Adding per-file hashes to the image manifest at build time. Changes the
  build and older images would still lack them.

### D5. Client-side storage with the official SDKs

`Azure.Storage.Blobs` for `https://` URLs (SAS via `AzureSasCredential`;
`StageBlock` then one `CommitBlockList`) and `AWSSDK.S3` for `s3://` URLs
(explicit `BasicAWSCredentials`, region from `--s3-region`, endpoint
`https://<host>[:port]`, path style when the bucket is in the path;
`CreateMultipartUpload`, `UploadPart`, `CompleteMultipartUpload`). The SDKs
are configured so they read no other credential source (no AWS profile,
environment or instance metadata, no Azure default credential) and log
nothing.

Upload streams in parts: the client buffers one part (64 MiB, raised to
`ceil(imageBytes / 9000)` for images over about 560 GiB so the 10,000-part S3
limit is never reached) and uploads parts one at a time. Memory stays near two
parts. Download is one GET stream into the decompressor.

Existence and overwrite: without `--force`, export checks for the object
before reading any image file (`destination_exists`), and also commits with
`If-None-Match: *` (S3 `CompleteMultipartUpload`, Azure `Put Block List`), so
an object created during the upload is never replaced on a service that honours
that header. A service that ignores it (possibly some S3-compatible servers;
MinIO is checked in task 5.2) is protected only by the up-front check, and
`docs/images.md` says so.

Azure block IDs are a random 16-byte prefix per export plus a zero-padded
6-digit block index, base64-encoded, so all IDs of one blob have the same
length (Azure requires that) and two exports to the same blob never commit
each other's blocks.

Secrets and SDK defaults (decided by the human): the S3 client gets a
`BasicAWSCredentials` and an explicit region in its config, and is never built
through the default credential chain, so `~/.aws`, `AWS_*` variables and
instance metadata are never read. The Azure client gets only an
`AzureSasCredential`; `DefaultAzureCredential` is not referenced. SDK logging
stays off (AWS logging set to none, no Azure event-source listener), and SDK
exceptions pass through `SecretRedactor` before any text is shown. Task 4.0
adds both and a test. With `--force` neither
check runs; the old object stays intact until the commit replaces it at once.

Cleanup: on any failure or cancellation, S3 aborts the multipart upload. Azure
has no call to drop uncommitted blocks of a blob that was never committed;
they are never visible as a blob and Azure removes them after a week. The docs
say so. Every storage error becomes `storage_unreachable` with the URL without
its query and the status or reason, after `SecretRedactor`.

Permissions documented in `docs/images.md`: export needs SAS `r` and `c`/`w`
(S3 `GetObject`, `PutObject`, `AbortMultipartUpload`); import needs SAS `r`
(S3 `GetObject`).

Alternatives considered:

- Hand-written REST calls. Easy for Azure with a SAS, but S3 needs SigV4
  signing and multipart handling; more code to get right than a dependency.
- A helper container with `rclone` or `azcopy`. The secret would sit in its
  settings or command line, which "Where storage secrets may appear" forbids,
  and it means pulling another image.
- Shelling out to the `az` or `aws` CLIs. Not installed everywhere, and
  secrets would pass through their environment and logs.
- Export via the engine's `BACKUP TO URL`. See D2.

**Unverified:** the exact SDK calls for conditional commit, explicit
credentials and path-style S3 (spike 1.2), and whether MinIO honours
`If-None-Match: *` on `CompleteMultipartUpload` (5.2; the up-front check still
covers the common case).

### D6. Local files: write aside, then move

Export writes to `<dest>.sqlbranch-partial-<id>` in the destination folder,
flushes it to disk, then moves it onto `<dest>`: without `--force` with a move
that fails when `<dest>` exists (`destination_exists`, partial deleted), with
`--force` with a replacing move. On every failure and on Ctrl+C the partial
file is deleted. A killed process leaves the partial file; its name says what
it is, and `docs/images.md` says it can be deleted.

Import from a local file only reads it.

Alternatives considered: writing `<dest>` directly (a failure destroys the old
file and leaves a broken one); a temporary folder elsewhere (the final move
could cross file systems and stop being atomic).

### D7. Import steps

`check-input` and `read-head` run before any `docker` command: the source is
opened (file or one storage GET), decompressed, and the first entry read and
checked (`archiveFormat` 1, a manifest format this CLI reads, a valid name or
`--as`). The stream stays open. Then, with the engine:

`check-name` (the build's name lock and existing-image check, `image_exists`),
`check-version` (D8), `check-space` (total manifest bytes against free pool
space, `pool_full`), `build-folder` (`/pool/build/<id>/data` under the build
lock), `write` (D3, D4: every next entry must be the next listed file,
a regular file whose tar header size equals the manifest size, checked before
any of its bytes are sent; anything else is `archive_invalid`), `verify` (D4),
`seal` (the shared seal, made reusable in task 3.0: create
`/pool/images/<name>`, set `ImageDirectoryCreated`, move into `data`,
`chmod 444`, write the manifest last with `name` replaced).

`read-head` treats the archive's manifest as untrusted and refuses it as
`archive_invalid` when `files` is empty, a `fileName` is not a plain name
(`/`, `\`, `..` or NUL, a leading `.`, or a repeat ignoring case), a `type` is
not `data` or `log`, a `sizeBytes` is negative, or the total overflows. Only
then are the names used in any path or shell word.

Order against `not_initialized`: exit-2 checks, then `read-head`, then
`CommandContext.RequireAsync` (exit 3). An invalid archive is reported on a
machine without an engine with exit 1 and no `docker` command; a valid one
reaches the exit-3 check. This mirrors how exit-2 checks already run before
it.

Cleanup on any failure or cancellation after `build-folder`: kill the
`docker exec` client, wait for it to exit (so `tar` in the container stops
writing), remove `/pool/build/<id>`, and remove `/pool/images/<name>` only when
`ImageDirectoryCreated` is set, as the build's cleanup does. An existing image
folder is never removed: `check-name` refuses an existing name, and a failed
`mkdir` never sets the flag. Because import uses
the build folder and lock, a killed import is a build leftover to `status`
with no `status` change, and a running import counts as a running build.

Alternatives considered: extracting straight into `/pool/images/<name>`
(a half-written image could be seen before its checks); a separate import
folder and lock (a second set of leftovers for `status` to learn).

### D8. Version rule: major version decides

The manifest's `engineVersion` major part is compared with the local engine's
`SERVERPROPERTY('ProductVersion')`. A newer major is refused as
`version_mismatch` naming both versions and the manifest's `engineImage`
(SQL Server cannot attach files from a newer version). A newer build within
the same major prints a warning and continues: cumulative updates within a
major version keep the on-disk version in practice, and refusing them would
break the nightly flow whenever the build machine updates first. If a branch
from such an image cannot attach, `branch create` reports the engine's error
as today.

Alternatives considered:

- Exact version match. Safe but blocks most real teams.
- Compare the database's internal version. Not in the manifest; would need a
  manifest change and a rebuild of every image.
- A trial attach during import. Costs a clone and an attach on every import
  for a rare case.

### D9. Testing

- **Unit tests** on the fake runtime and a fake storage service (in-memory
  objects, injectable failures per request, records requests): every exit-2
  case with "no `docker` command and no storage request", archive round trip
  and order, crafted archives (traversal, link, duplicate, extra entry, wrong
  hash, truncated, newer format, not an archive), overwrite rules, cleanup on a
  failure at each step and on cancellation, redaction of every secret form in
  every error.
- **Integration tests** (`SQLBRANCH_IT=1`, clone modes `thin` and `copy`):
  file export and import round trip with branch creation, SHA-256 equality,
  corruption, pool full, cancel, kill. S3 against F1's MinIO fixture, driving
  the Core export and import services with an HTTP handler that trusts the
  fixture's CA; the CLI gets no CA option (out of scope).
- **Opt-in cloud tests**: `SQLBRANCH_IT_AZURE_EXPORT_URL` and
  `SQLBRANCH_IT_S3_EXPORT_URL` with F1's secret variables, run by hand on the
  0.3 release gate.
- **Cross-architecture check** on the release gate: export on an Apple Silicon
  Mac (engine under emulation), import on the x86-64 run host, and the
  reverse.

Alternatives considered: Azurite for Blob (needs a CA trusted by the client
and a path-style account; fake storage covers the logic, the opt-in test the
real service).

### D10. Error codes

| Code | Exit | When |
|---|---|---|
| `destination_exists` | 1 | export target exists and no `--force`, or appeared during upload |
| `archive_invalid` | 1 | not an archive, newer format, bad name without `--as`, unlisted or unsafe entry |
| `archive_corrupt` | 1 | SHA-256 or size differs, checksums missing, stream ends early |
| `storage_unreachable` | 1 | network, TLS, auth or missing object at the URL |
| `version_mismatch` | 1 | existing code, newer major version |
| `image_exists`, `image_not_found`, `pool_full` | 1 | existing codes |
| `invalid_name`, `usage_error`, `config_error` | 2 | existing codes |

## JSON output

`schemaVersion` stays 1. Additive only.

- `image export`: `{ "command": "image export", "export": { "image": "shop",
  "to": "<path or URL without query>", "files": 2, "imageBytes": n,
  "archiveBytes": n } }`. No `cloneMode`, because it creates no pool files.
- `image import`: `{ "command": "image import", "from": "<path or URL>",
  "cloneMode": "thin", "image": { ...same object as one `image list` entry } }`.
- New `error.code` values as in D10.
- Text output: one result line each, plus a `clone mode: <mode>` line for
  import as `image build` prints.

## Risks / Trade-offs

- [Compression is slower than the network or disk on large images] → Spike 1.1
  measures it; gzip stays unless zstd is clearly better, and the reader
  detects the compression, so a later switch keeps old archives readable.
- [A newer build within one major version cannot attach on an older engine]
  → Warning at import, the engine's error at `branch create` (rule decided by
  the human).
- [Uncommitted Azure blocks stay up to a week after a failed export] →
  Invisible as a blob, removed by Azure; documented.
- [A killed export leaves `*.sqlbranch-partial-*` locally or an unfinished S3
  upload] → Documented, with the S3 lifecycle rule AWS recommends.
- [Archives are trusted like backups; a crafted archive could carry a hostile
  database] → The entry checks stop file system escapes; the database content
  is the user's responsibility, as with `--from-bak`. Documented.
- [Two new SDKs grow the tool package] → Accepted for correct S3 signing and
  multipart; measured in task 1.2 and noted in the PR.
- [Images built under emulation on Apple Silicon are an unsupported engine
  setup per Microsoft] → The archive holds the same x86-64 SQL Server files
  either way; checked on the release gate (D9).

## Migration Plan

No migration. Manifests and pool layout are unchanged. An older CLI ignores
archives and lists imported images like built ones. Rollback is removing the
two commands.

Note (2026-11-19, orchestrator): the engine requirement "Commands before init"
is MODIFIED by this change so that `image import` reads and validates the
archive head before the `not_initialized` check. An untrusted archive is
refused before SqlBranch touches Docker or the engine.
