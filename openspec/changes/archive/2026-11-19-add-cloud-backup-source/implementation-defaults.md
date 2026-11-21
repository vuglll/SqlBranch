# Implementation defaults for add-cloud-backup-source

On 2026-11-18 the human asked for F1 to be implemented without waiting on them:
"do your own defaults and document it", with up to two review-and-fix rounds per
task group. This file records each default taken in their absence, so they can
check or reverse it later. Nothing here changes the approved specs. Where a
default leaves a task unverified, the task stays unticked and moves to the
release gate when the change is archived.

## Limits of an unattended run

- No cloud accounts are created or used: no Azure storage account, AWS bucket or
  Managed Instance. Only local containers (MinIO, Azurite, the SqlBranch engine)
  and anonymous requests to public endpoints.
- Work goes to branch `feat/add-cloud-backup-source`. A pull request into `dev`
  is opened at the end; it is not merged without the human.
- Loop per group: implementer, gate B (build with zero warnings, unit tests, the
  group's integration tests), code reviewer, at most two fix rounds. If a group
  still has a blocker after two rounds, the run stops there and reports.

## Defaults per task

| Task | Default | Why |
|---|---|---|
| 1.1 | Partly done locally. Certificate trust is checked by running `RESTORE HEADERONLY FROM URL` in a stock engine against the public S3 and Blob endpoints with a dummy credential: a TLS error means the certificate is not trusted, an authorization error means it is. Folder-named SAS credentials, the region option and the exact error texts for a wrong secret or a missing object need real accounts and stay open. | No cloud account may be used unattended. |
| 1.1, credential name | Use the documented container-level SAS credential name (design D4's fallback), not a folder-level one. | The fallback is the documented behaviour; the folder form is unverified. |
| 1.1, region option | Pass `RESTORE_OPTIONS` with the region on all four statements for S3, if MinIO accepts it there; otherwise only on RESTORE. Spike 1.2 decides against MinIO. | Closest local evidence. |
| 1.2 | Done locally as written. | Needs no account. |
| 1.3 | Not done. Record that the question stays open, add it to the 0.3 release gate in `docs/roadmap.md`, and say in `docs/images.md` that Managed Instance is not verified yet. | Needs a real Managed Instance. |
| 4.3 | Error texts for a wrong secret and a missing object come from MinIO where SQL Server reports them the same way; otherwise the mapping matches on the engine's message number. | Real-cloud texts are unavailable. |
| 4.6 | Follows the local certificate check in 1.1: build the CA copy only if a public endpoint is not trusted. | As the task says, with the local evidence. |
| 5.4 | Write the opt-in cloud tests so they skip without their variables. Leave the task unticked; it moves to the release gate. | Needs real accounts. |
| 5.5 | As written: Azurite tests if spike 1.2 shows Azurite works as a restore source, otherwise the documented fallback. | Needs no account. |

## Results

The run appends what it found and decided under this heading, with dates.

### 2026-11-19, group 1 (spikes)

Full data and commands: `docs/research/2026-11-18-cloud-backup-source-facts.md`,
section "Spike results, run 2026-11-19".

- 1.1 (partly done, left unticked): a stock 2022 engine with no file in
  `ca-certificates` trusts the certificates of AWS S3 (`s3.eu-west-1`,
  `s3.us-east-1`, virtual-host form) and of a real `*.blob.core.windows.net`
  account: both gave error 5 (access denied), not 12175 (untrusted certificate).
  **Task 4.6 (the CA copy) is not needed** on that evidence. Open and on the 0.3
  release gate: folder-named SAS credential and region option on real storage,
  real-cloud error texts.
- 1.2 (done): MinIO with a test CA works after the CA is placed and the engine
  restarted (12175 before). LABELONLY, HEADERONLY, FILELISTONLY and RESTORE all
  accept `RESTORE_OPTIONS` with the region, so per the 1.1 default it is passed
  on all four S3 statements. MinIO errors: wrong secret or key id is error 5,
  missing object or bucket is error 2, no matching credential is 86. A credential
  named after a folder is found for S3 on MinIO.
- Azurite works as a Blob restore source, but only in virtual-host style over
  HTTPS on 443 (test certificate for `<account>.blob.core.windows.net`, a hosts
  entry in the engine) and with a SAS that includes write permission, since the
  engine leases the blob (read-only SAS gives error 13). A folder-named
  credential is not found on Azurite (86). Task 5.5 can use Azurite with those
  conditions.
- Image finding for task 5.1: `minio/minio` no longer pulls from Docker Hub and
  the Quay image needs a login; `cgr.dev/chainguard/minio:latest` pulled
  anonymously and worked, so the fixture must pin an image that still pulls.
- Design check: D4 says restore needs read and list on the SAS; Azurite shows a
  write permission is needed for the lease. Settle on real Blob before 4.5 writes
  the SAS permission advice.
- 1.3 not done: stays open, added to the 0.3 release gate in `docs/roadmap.md`.
  The note in `docs/images.md` goes in with task 4.5, since that file has no URL
  content yet.

### 2026-11-19, group 2 (URL and secret input checks)

- Blob credential name: the container URL (`https://<host>/<container>`), the
  documented fallback of D4 per the 1.1 default. S3: the URL up to its last `/`.
- `backupFile` is the last path segment as typed (percent-encoding kept); the URL
  handed on is the typed URL with only the scheme lower-cased.
- Also refused with exit 2: user information (`user:pw@host`) in a URL, since it
  would put a secret into the URL and the credential name.
- After the checks pass, a URL build stops with exit 1 `operation_failed`
  ("not available yet") until groups 3 and 4 add the credential and restore steps.
- `--s3-region` is also refused with a file path or `--from-scripts`.
- Round 1 review: a storage secret given as a flag (`--sas-token`,
  `--s3-access-key-id`, `--s3-secret-access-key`, also `--flag=value`) now exits 2
  with a message that names the environment variable and echoes no value.
  The reviewer's request to reword the "URL up to its last `/`" text in the images
  spec was not applied: specs are fixed in this run. The wording differs from the
  Blob container-level name (D4 fallback); the spec author should align it.

### 2026-11-19, group 2 review (orchestrator)

- The code review of group 2 found a SAS token given as a flag echoed in the
  parse error; fixed in round 1 (8a465b6).
- Spec wording aligned by the orchestrator, as the unattended default: the
  credential name in "Backup URLs are checked first" is now "as design D4
  derives it from the URL" instead of "the URL up to its last `/`", because the
  Blob default is the container URL (D4 fallback). Behaviour is unchanged.

### 2026-11-19, group 3 and 4 (credential lifetime, restore from the URL)

Both groups changed `ImageBuilder`, so they were built together; the unit tests
use the fake connector and fake runtime only. Defaults taken:

- 4.6 ticked as "not needed" (spike 1.1 result). 4.5 stays unticked: its
  verification is the integration tests of group 5, which were not written yet.
- Error mapping (4.3) reads the OS error number out of the engine text
  (`Operating system error N`): 5 access denied, 2 not found, 12175 untrusted
  certificate (also any text with "certificate"), 86 no credential, 12007 and 12029
  network; 3201 and, for a storage account that does not resolve, 3078 give
  `backup_unreachable` in URL mode. Any other 3201 reads as "could not open".
- Credential name is the container URL for Blob and the URL up to its last `/`
  for S3 (design D4 fallback), as in group 2. The credential lock is
  `sqlbranch_cred_<first 16 hex of SHA-256 of the name>`; a release helper
  (`EngineLocks.ReleaseAsync`) was added because the sweep must give the lock back.
- Credential read-back uses `CONVERT(varchar(33), create_date, 126)` so the date
  in the marker compares as text, exactly.
- The sweep also runs in file and script builds (the spec scenarios use
  `image build crm --from-bak crm.bak`). It drops a marker's credential only on
  exact name, `credential_id` and date match, then deletes the marker and removes
  the folder with `rmdir` (so a folder that holds more stays, as before).
- `status` shows a leftover credential as kind `build_credential`, by name only,
  next to the existing `build_folder` entry of the same dead build.
- Cleanup after a `CREATE` that returned (or whose outcome is unknown, such as a
  transport failure) but whose read-back failed: cleanup reads the credential back
  once and drops it if it exists. An engine error from `CREATE` (for example "already
  exists") drops nothing. Not in the design text; it avoids leaving a secret behind
  without dropping one that is not ours. If even that read-back fails, the message
  says to drop the credential by hand.
- A failed drop is tried twice (the build step, then the failure cleanup); both
  are sent, and only if both fail the marker is kept.
- Ctrl+C: the CLI had no cancellation wiring. A URL build now links
  `Console.CancelKeyPress` to the build's token on the first press (a second press
  ends the process), so a waiting build ends at once and undoes what it made, and
  exits 1 with a "build was cancelled" message. File and script builds are unchanged. The waiting
  message goes to standard error through `CliApp.WriteProgress`.
- `backupFile` of a URL build is the last segment, URL-decoded with `Uri.UnescapeDataString` (design D6); review round 1 reversed the group 2 choice of keeping it as typed. `backupUrl` keeps the URL as typed.
- Parse errors: the token after any unrecognized `--option`, and the value of
  `--option=value`, is never echoed; plain misspelled commands still are.
- Not verified here: the Azure SAS permission advice (write may be needed for the
  blob lease) comes from Azurite only; Managed Instance is not verified. Both are
  in `docs/images.md`.

### 2026-11-19, group 5 (integration and cloud tests)

- Fixture (5.1): one `CloudSourceFixture` per test run (xUnit collection `CloudSources`) starts a SqlBranch test engine, a MinIO
  the engine trusts, a second MinIO whose CA the engine does not know, and Azurite. Containers carry the label
  `sqlbranch-it-fixture=<run id>`; removal reads the label back first. The per-run CAs are made in .NET (no openssl). The
  MinIO certificate names the bridge address, which is known only after the first start: start, copy the certificate in with
  `docker cp`, restart. Image: `cgr.dev/chainguard/minio` pinned by digest (`SQLBRANCH_IT_MINIO_IMAGE` replaces it); Azurite is
  pinned by digest too. Test keys are random per run and appear only in these containers' settings.
- Files copied into a container keep the owner and mode of the host: the folder had mode 700 and the key 600, so MinIO silently
  ignored them and ran plain HTTP. The fixture sets 755 and 644 before the copy.
- Azurite (5.5) follows the spike conditions: HTTPS on 443, a certificate for `<account>.blob.core.windows.net`, a hosts entry
  in the engine added after the engine restart (it would not survive another restart), an account SAS made by the fixture to
  create the container and upload, and a container SAS with `rwl` for the build. Cases: success, wrong signature, missing blob,
  credential made by hand.
- Killed-build case (5.2): a 160 MB backup (one row per page) is needed. With a small backup the restore ends in about a second,
  so the client was killed after the seal and the case could not fail the way it should. The test kills as soon as it sees the
  temporary database `RESTORING` and the credential, then records the build folder.
- Leak test (5.3): all six MinIO cases in JSON and text; 50 outputs and files searched for the key ID, the secret and the
  wrong secret (stdout and stderr, `docker inspect` of the engine, manifests, `credential.json` files). The recorder reads only
  `*.json` in build folders, since a build folder also holds the restored data files.
- 5.4 written as `CloudOptInIntegrationTests` (skipped without `SQLBRANCH_IT_AZURE_URL` with `SQLBRANCH_AZURE_SAS_TOKEN`, or
  `SQLBRANCH_IT_S3_URL`, `SQLBRANCH_IT_S3_REGION` and the S3 keys); it also checks the engine's certificate folder is empty.
  Left unticked: it needs a real account and moves to the 0.3 release gate.
- 5.6: the agent skill is generated from command metadata only, so the `image build` entry now says a URL is accepted and
  where its secret goes, and its example is the `s3://` form with `--s3-region`. `skills/sqlbranch/reference/image.md` was
  regenerated; the unit test that compares it passes.
- 4.5 ticked: `DocsIntegrationTests` pass on the default pool (the documented URL examples exit 2 as written).
- Results: new tests 12 passed and 2 skipped (the opt-in cloud tests) on the thin pool (`loop-xfs`, cloneMode thin) and on
  the plain pool (copy). No thin-only assertion is made, so none is reported as skipped for that reason.


### 2026-11-19, group 5 review (orchestrator)

Review of 504a108: CHANGES REQUIRED, test-only findings (opt-in tests could
echo a real secret on failure; the leak test misses most manifests; weak
certificate-message assertion; missing Integration trait; some outputs not
recorded). Fix round 1 starts after gate C, since gate C runs the product code
these fixes do not touch.

### 2026-11-19, gate C and hand-off (orchestrator)

Gate C passed (docs/research/2026-11-19-cloud-backup-source-integration-runs.md):
thin 116 passed, 3 skipped; copy 95 passed, 24 skipped; 0 failed; 848 + 10 unit
tests; strict validation 9 of 9. Open for the human: 1.1 (rest needs real Azure
and S3), 1.3 (needs a Managed Instance), 5.4 (opt-in cloud tests written, need a
manual run with real accounts). The pull request into dev is opened, not merged.
