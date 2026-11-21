# Implementation defaults for add-image-export-import

On 2026-11-19 the human asked for F3 to be implemented the same way as F1: an
unattended loop with the orchestrator's own defaults, documented here, and at
most two review-and-fix rounds per group. Nothing here changes the approved
specs. A task left unverified by a default stays unticked and moves to the
release gate when the change is archived.

## Limits of an unattended run

- No cloud accounts and no x86-64 host. Only local containers (the SqlBranch
  engine, F1's MinIO and Azurite fixtures) on the Mac.
- Branch `feat/add-image-export-import`; a pull request into `dev` at the end,
  not merged without the human.
- Loop per group: implementer, gate B (zero warnings, unit tests, the group's
  integration tests), code reviewer, at most two fix rounds. A blocker left
  after two rounds stops the run.
- If the 5-hour usage limit passes 90%, the run pauses, writes where it stopped
  here, and resumes after the reset.
- F2 runs in Codex in its own worktree at the same time; this run touches only
  this branch and its own pool.

## Defaults per task

| Task | Default | Why |
|---|---|---|
| 1.1 | Not run: it needs the x86-64 host and a 50 GB image. Gzip `Fastest` stays the format (the spec's default). Moves to the release gate with the Hetzner run. | No x86-64 host. |
| 1.2 | Run locally against F1's MinIO and Azurite fixtures in a scratch project, as written. | Needs no account. |
| 5.4 | Written so it skips without its variables; left unticked for the release gate. | Needs real accounts. |
| 6.4 | Not run; moves to the release gate with the Hetzner run. | Needs the x86-64 host. |

## Results

The run appends what it found and decided here, with dates.

### 2026-11-19: task group 1

- 1.1 not run (deferred to the release gate with the Hetzner run, per the table).
- 1.2 run locally against F1's pinned MinIO and Azurite images in a scratch
  project outside the repository; full results in
  `docs/research/2026-11-19-image-export-import-facts.md`.
  - MinIO honours `If-None-Match: *` on `CompleteMultipartUpload`: 412
    `PreconditionFailed` on an existing key and on a race; completing without
    the header replaces the object.
  - Azurite refuses the conditional `CommitBlockList` with 409
    `BlobAlreadyExists` (not 412); both codes must map to `destination_exists`.
  - Calls verified: `AWSSDK.S3` 4.1.2 (`BasicAWSCredentials`, path style, custom
    `HttpClientFactory`, multipart upload, `IfNoneMatch`, abort) and
    `Azure.Storage.Blobs` 12.30.1 (`AzureSasCredential`, `StageBlock`, conditional
    `CommitBlockList`). A 200 MiB stream round-tripped with an equal SHA-256 on both.
  - Tool package grows from 4.06 MiB to 6.55 MiB (+2.49 MiB, +61%).
  - Azure export SAS must include write permission; a SAS without it cannot stage blocks.

### 2026-11-19: task group 2

- Gzip `Fastest` is the archive format (spike 1.1 stays deferred). The reader detects gzip by `1f 8b`, refuses zstd
  (`28 b5 2f fd`) and anything else as `archive_invalid`.
- Streaming exec: `IContainerRuntime.ExecStreamAsync` returns an `IStreamingProcess` (a `Stream`, `WaitAsync` for the exit
  code and standard error, `KillAsync`). `IProcessRunner` gained `Start` for it. Disposing the process kills the `docker exec`
  client and waits for its exit; a cancelled token only makes the process end, so whoever disposes it is the one that waits.
  The buffered `ExecAsync` and its callers are unchanged.
- Finding: `System.Formats.Tar.TarWriter` refuses an unseekable entry stream when the archive stream (gzip) is unseekable. The
  writer therefore gives it a read-once stream that reports `CanSeek` and accepts only a seek to where it already is. A 9 GiB
  PAX entry writes and reads back without being buffered. `TarReader` reads a GNU base-256 size over 8 GiB, so the engine's
  GNU `tar` output needs no `--format` flag.
- Pool-directory check for `--to`: before any `docker` command it uses the `pool` setting (file, variable or flag); once the
  pool is found, it also uses the engine's own pool directory, so a destination in either is refused (exit 2). The second check
  runs after the first `docker` command, which the spec's "before any `docker` command" does not cover; it only adds a refusal.
- Until group 4, a valid `https://` or `s3://` `--to` passes the exit-2 checks and then exits 1 `operation_failed` ("not
  available yet") before any `docker` command or storage request.
- New error codes `destination_exists`, `archive_invalid` and `archive_corrupt` are in `ErrorCodes` and `docs/cli-output.md`
  (the doc table test requires both together). `storage_unreachable` comes with group 4.
- The export examples in `docs/images.md` are in a plain code block, not the `sh` block that the docs integration test runs
  (it needs SQL Server), so they carry no executed exit-code check; unit tests and the export integration tests cover them.
- Integration tests of this group use a labeled `SQLBRANCH_IT_IMAGE` container in place of the engine (export reads files only),
  so they run with `SQLBRANCH_IT_IMAGE=ubuntu:24.04`. The thin and copy clone modes do not apply to an export.

### 2026-11-19: task group 3

- Group 2 review minors fixed first (commit `fix(images): tidy export cancel...`): a cancel during export is rethrown as a
  cancellation (a killed client ends the stream early, which used to read as "damaged"); `RequireOutsidePool` resolves symbolic
  links (the real path of the destination's folder and of the pool, through a new `RealPathOf` service, so `/tmp` and
  `/private/tmp` compare equal); a `NotSupportedException` from the read-once stream workaround becomes `InvalidDataException`
  (so `operation_failed`); the "Press Ctrl+C again" hint is gone from the message printed after the export stopped; the reader
  disposes its `TarReader` when the head cannot be read.
- 3.0: the shared seal is `ImageSealer` (internal): it makes `/pool/images/<name>`, moves the files in mode 444, writes the
  manifest last and removes the image folder only when it made it. `EngineShell` holds the shell helper both use. The scripts and
  step names of `image build` are unchanged byte for byte.
- Killing `docker exec` does not stop `tar` in the container. The import starts `sh -c 'echo $$ > /tmp/sqlbranch-import-<id>.pid &&
  exec tar -C <build>/data -xf - --no-same-owner --no-same-permissions'`, so the recorded id is the tar's. Cleanup first kills the
  client and waits for it, then runs `ImageImporter.EnsureEndedScript` in the container: it signals that pid (TERM, then KILL after 5
  s) only while `/proc/<pid>/cmdline` still holds `/pool/build/<id>/data`, waits until it is gone, and deletes the pid file. The
  pid file is in the container's `/tmp`, not in the pool, because a full pool is exactly when the tar may be stuck. If the end cannot
  be confirmed, nothing is removed and the failure message names the build folder; `status` lists it. The script needs only
  `sh`, `tr`, `grep` and `sleep`, which both engine images and `ubuntu:24.04` have (`pkill` is not in `ubuntu:24.04`).
- The tar the client sends is a PAX tar written with the read-once stream workaround of group 2, one entry per manifest file,
  mode 0640. A tar written by .NET carries PAX extended headers; GNU tar reads them.
- Manifest checks (`ArchiveManifestChecks`) go slightly beyond the listed rules, all inside "plain name": an empty name and any
  control character (not only NUL) are refused, and an `engineVersion` that is not a version number is `archive_invalid`, because
  `check-version` needs its major part. A source with `https://` or `s3://` still exits 1 `operation_failed` ("not available
  yet") after the exit-2 checks, until group 4.
- Entry checks run before the entry's tar header is written: order, repeat, type (regular file only), exact name `data/<fileName>`,
  header size equal to the manifest's. The data stream of the entry goes through the same read-once, hashing stream as in export.
  The checksum list must follow the last data entry and nothing may follow it. A gzip footer cut off after a complete tar is not
  noticed, because every file is checked by its SHA-256 first; the tar reader stops at the end-of-archive marker.
- A tar exit that says "No space left" is `pool_full`, any other non-zero exit is `operation_failed`. A cancel always ends as a
  cancellation, whatever the killed client's streams threw.
- New integration test `ImageImportTarEndIntegrationTests`: the real command and end-check script in a labeled `ubuntu:24.04`
  container (no SQL Server needed); the full import round trip needs the engine and is in group 5.

### 2026-11-19, pause before group 4 (orchestrator)

Groups 1 to 3 are done and approved (group 3 after one fix round, 8c1f2c9).
Paused at 79% of the 5-hour usage limit, before group 4, which is the largest
group. Resumes after the reset with group 4. Carry into group 4 the two group 3
review minors: a CLI test pinning the cancel-with-leftovers message, and
`TryRemovePidFileAsync` catching every exception except a cancellation (drop the
unneeded CA1031 pragma). Group 4 also removes the "arrives with group 4" notes in
docs/images.md and the placeholder exit 1 for URL sources and destinations.

Also carried into group 4 (from the F2 spec review, 2026-11-19): `image import`
must keep manifest fields it does not know (for example F2's `bacpacFile`), so
an imported manifest equals the exported one as the spec says. Today
`ImageImporter` re-serialises a typed `ImageManifest` and drops them. Keep the
original JSON and change only `name`; add a test with an extra field.

### 2026-11-19: task group 4

- Carried items (first commit): a CLI test pins the cancelled-import message with leftovers (a write that throws a cancellation and an
  end-check that answers `SQLBRANCH_TAR=running`); `TryRemovePidFileAsync` catches every exception except a cancellation, with no
  pragma; unknown manifest fields are kept with `[JsonExtensionData]` on `ImageManifest` and `ImageFile`, so import and re-export write
  them back unchanged (tested through the writer, the reader and the import). This keeps every value of an unknown field; it does not
  keep the original text of the known fields, which this version writes in its own format.
- Storage code is in `SqlBranch.Core.Storage`: `IArchiveStore` and `IArchiveUpload` (the seam the CLI and tests use), `S3ArchiveStore`,
  `AzureArchiveStore`, `PartUploadStream` (cuts the archive into parts, one part in memory), `PartSizing`, `StorageErrors`.
  `ImageExporter.ExportToStoreAsync` shares the manifest lookup and the archive streaming with the file export.
- Safe set-up as written in D5 and in the spike: S3 client from `BasicAWSCredentials`, `ServiceURL`, `AuthenticationRegion`,
  `WHEN_REQUIRED` checksums, `DisableLogging`; Azure client from `AzureSasCredential`, logging, telemetry and tracing off. Neither
  client is ever built any other way (a test reads the sources). Retries: see the group 4 review minors in group 5 (two SDK retries
  since then; this line said "no retries" before).
- Part size: 64 MiB, raised to `ceil(imageBytes / 9000)` rounded up to whole MiB. `StorageOptions.PartSize` overrides it for tests only.
- Errors: every non-cancel SDK exception becomes `storage_unreachable` with the URL (it never has a query), `HTTP <status> (<code>)` or
  the first lines of the exception chain, and then a scrub: registered secrets, every query string in a URL, and values after `sig=`,
  `signature=` and similar. S3 `412` and Azure `409` or `412` on the conditional commit are `destination_exists`. A download that
  breaks in the middle is `storage_unreachable`. Secrets are also registered URL-encoded.
- `StorageSecrets` now also exposes `SasToken`, `AccessKeyId` and `SecretAccessKey`. CLI order for a URL: exit-2 checks, secrets (exit 2
  `config_error`), settings (so a secret in a config file exits 2), then for import one GET before any `docker` command, then the engine.
  `image import` now loads the settings before it reads the archive for files too (an invalid config exits 2 before `archive_invalid`).
- Tests use a fake storage service (`FakeStorageService`, an `HttpMessageHandler` that speaks enough S3 and Azure Blob, including
  `aws-chunked` bodies) behind the real SDK clients. The tests that set process variables run in one non-parallel collection.
- The unit test for 4.0 plants `AWS_*`, `AWS_PROFILE`, `AWS_ENDPOINT_URL`, `AZURE_*` and a `~/.aws` under a temporary `HOME` and checks
  every request, the console, the trace, the Azure event source and the AWS logging switch; a canary shows the capture does see Azure
  SDK logging when it is on. AWS logging has no canary.
- Package size, `dotnet pack -c Release` of `src/SqlBranch.Cli` on 2026-11-19: 6,943,742 bytes (6.62 MiB), against 4,260,378 bytes
  (4.06 MiB) before the SDKs: +2.56 MiB, +63%.
- Not done in this group, by design: MinIO and Azurite integration tests (group 5); `AWS_ENDPOINT_URL` handling beyond the fact that an
  explicit `ServiceURL` wins and the test sees no request leave for another host.


### 2026-11-19: group 4 review minors (first commit of group 5)

- Retries: the S3 client allows 2 SDK retries (`MaxErrorRetry = 2`) and the Azure client 2 (`Retry.MaxRetries = 2`, exponential from
  0.8 s). An UploadPart or StageBlock body is a seekable in-memory copy, Complete and CommitBlockList repeat the same part or block
  list, and abort is idempotent, so a re-send is safe, and `If-None-Match: *` stays on the complete. The retries cover every call of
  the client (existence check, GET, initiate) because neither SDK sets them per call; a retried initiate that lost its response can
  leave one unfinished multipart upload, which the documented lifecycle rule removes. A conditional complete whose first answer was
  lost after it succeeded reads as `destination_exists` on the retry. `StorageOptions.RetryDelay` (tests only) removes the Azure pause.
- S3 `409 ConditionalRequestConflict` on a conditional complete is `destination_exists`, like 412.
- A `TaskCanceledException` in the middle of a download is `storage_unreachable` unless the caller's token was cancelled.
- `ScriptFile` keeps unknown fields (`[JsonExtensionData]`), so `scripts` and `postScripts` entries survive import and re-export.
- `StorageSecrets` registers `Uri.UnescapeDataString` of the SAS `sig` as well.
- Finding from the AWS logging canary: the capture of AWS SDK logging never saw a line. `LogTo` must be `Console` alone (the SDK logs
  nothing when `SystemDiagnostics | Console` are combined) and the console must be redirected first. With the capture working, the
  SDK's own internal-configuration lookups log "There is no retry_mode set in the profile named '<AWS_PROFILE>'" at client
  construction: the SDK reads the non-credential settings (defaults mode, retry mode, max attempts, dualstack, FIPS, metadata
  endpoint) of the profile named by `AWS_PROFILE`, and the lines carry the profile name only. Setting those options explicitly on the
  config does not stop the lookups. It reads no credential, region or endpoint from the profile (the requests are signed with F1's
  variables and go to the explicit endpoint, and the tests assert it), and SqlBranch never enables SDK logging, so the name is never
  printed. The test filters exactly those two line kinds and checks every other line, the output and every request for markers.

### 2026-11-19: task group 5

- 5.1, 5.2 and 5.3 are ticked; 5.4 is written and skips without its variables, and stays unticked for the release gate (the 0.3
  gate in `docs/roadmap.md` now lists it, with 1.1 and 6.4). The tests share F1's `CloudSourceFixture` collection, so one engine, one
  MinIO and one Azurite serve all of them. `ImageArchiveScenarios` holds the cases; each runs in JSON and in text for the leak test.
- Clone modes: the default managed pool is `thin`, `SQLBRANCH_IT_POOL_BACKEND=plain` is `copy`. The tests check the mode the pool has
  (the import output and `branch create` must report the mode `status` reports) and are not skipped in either; no assertion in them is
  specific to thin.
- How the cases are made, where the spec text says otherwise or leaves a choice:
  - Cloud cases run the real command with `CliServices.StoreFactory` set to a factory whose handler connects to the container's
    published loopback port (the client cannot reach the bridge address) and trusts only the fixture authority by thumbprint. The
    cancelled upload, the object created during the upload and the multipart-upload checks call `ExportToStoreAsync` and an S3 client
    on the same handler directly.
  - Not enough space: an archive whose head lists a 32 TiB file (the import stops at `check-space`, so no data entry is needed), not a
    pool filled to the brim, because a plain pool is the client's disk. It checks `pool_full`, both sizes in the message, no build folder
    and no image.
  - Corrupted byte: the exported archive is rewritten entry by entry with one byte of the data file flipped and the checksums as
    exported (a flipped byte in the gzip stream would be a different failure). Truncated: the file cut to half.
  - Image removed during export: a hook on the process runner deletes a data file of a copy of the image right before the engine's
    `tar -cf` starts, which is what `image rm` does to a running export; the export exits 1 and no file or partial file is left.
  - Ctrl+C and the killed import use a generated archive (real writer, zero-filled 1 GiB data file, a few MB on disk) so the import
    is slow enough to interrupt after 64 MiB are written. Ctrl+C is a real SIGINT to the CLI harness process. A background process
    inherits an ignored SIGINT, which .NET then keeps ignoring, so the harness is started through `perl`, which resets it.
    Killed import: `status` exits 1 with one `build_folder`; the test removes the leftover folder and pid file itself.
  - MinIO honours `If-None-Match: *` through the product code too (research file, task 5.2); no change to `docs/images.md` beyond one sentence.
- 5.3 searches 322 recorded outputs and files (stdout and stderr of every run, the archives raw and unpacked, every manifest, `docker
  inspect` of the engine, 584 `docker` command lines the tool ran, `status`) for the key ID, both secrets, the SAS token and signature
  (also decoded) and the wrong ones: none appears, in both pool modes.
- The retry change of the review minors does not affect these tests: they run with the SDK default retry count against live servers.

### 2026-11-19: group 5 review fixes (round 1)

- Leak test now also covers: the docker command lines of the Ctrl+C and killed imports (the CLI harness logs them to a file named by
  `SQLBRANCH_HARNESS_COMMAND_LOG`, which the test reads), the exporters the cancelled-upload and object-created cases build (over a
  recording runtime), the exception messages of the refused and cancelled cases, and the objects stored in MinIO and Azurite (downloaded
  and searched like archives). Data blocks are scanned with a 1 KiB overlap. 630 outputs and 680 command lines are searched.
- Wrong secret and wrong SAS imports now target an object that exists and assert the reason: S3 `HTTP 403 (SignatureDoesNotMatch)`,
  Azurite `HTTP 403 (AuthorizationFailure)` (not `AuthenticationFailed`).
- Unit tests fail the S3 complete and the Azure commit once and check the retry still carries `If-None-Match: *`; `docs/images.md`
  describes the lost-response case. The Ctrl+C test is skipped with a reason when `perl` is not on the PATH. The killed-import cleanup
  removes only the build folder `status` named.
