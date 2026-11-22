# Feature drafts

Each draft becomes one OpenSpec change. Start the proposal from the draft, but
re-check its facts first: drafts age. Order and prerequisites are in
`docs/roadmap.md`.

---

## F1 `add-cloud-backup-source` (0.3)

**Why.** Most teams keep backups in cloud storage, not on a laptop. Downloading
a large backup first doubles the disk and the wait.

**Behavior sketch.**
- `image build <name> --from-bak <url>` accepts an Azure Blob Storage URL
  (`https://...blob.core.windows.net/...`) or an S3 URL (`s3://...`). The
  engine restores straight from the URL. Nothing is downloaded to the client.
- Credentials come from environment variables or flags (a SAS token, or an S3
  access key and secret). They are given to the engine for the one restore,
  removed afterwards, and never written to the manifest, logs or config.
- This covers: SQL Server on VMs that back up to Blob or S3, copy-only backups
  taken on Azure SQL Managed Instance, and any service that can write a native
  backup to S3-compatible storage.
- For Managed Instance the build checks the backup's version first and, on a
  mismatch, says which engine image to use.

**Needs first.** 0.1 archived.

**Verify before proposing.**
- Restore from URL inside the Linux container, for both Blob and S3, including
  certificate requirements for S3 endpoints.
- Managed Instance: a copy-only backup restores to SQL Server 2022 only from
  an instance on the SQL Server 2022 update policy, and to SQL Server 2025
  only from one on the 2025 policy. Instances on "Always-up-to-date" cannot be
  restored to SQL Server at all; those users need the BACPAC route.
- Managed Instance blocks copy-only backups when the database is encrypted
  with a service-managed key.
- Amazon RDS and Google Cloud SQL: how their native backup exports land in
  storage, and whether Google's S3-compatible endpoint works with the engine.
- How to test this in CI without a cloud account (a local S3-compatible
  server is the likely answer).

---

## F2 `add-bacpac-source` (0.3)

**Why.** Azure SQL Database cannot produce a native backup. A BACPAC export
gives teams a portable package of database schema and data.

**Behavior sketch.**
- `image build <name> --from-bacpac <file-or-url>` imports the export into
  the engine, then seals the image like any other.
- Produce the package separately with the Azure portal or the user's
  own SqlPackage installation, then build with `--from-bacpac`.
- Output shows package size before import and time per phase.
- Post-build scripts (0.2) run as usual, so masking works the same way.

**Needs first.** 0.2 archived (the build pipeline with post-build scripts).

**Verify before proposing.**
- SqlPackage on Linux, macOS and inside the engine container: where it runs
  best and how it is installed.
- Import time and failure modes for 10 GB and 50 GB databases.
- Which Azure SQL Database features do not import into SQL Server, and how to
  report them clearly.
- How SqlPackage imports database users mapped to server logins.

---

## Follow-up draft `add-consistent-azure-sql-export` (unscheduled)

**Why.** Automate an Azure SQL Database export and optionally take a
transactionally consistent snapshot while writes continue.

**First open question.** How can a user-installed SqlPackage authenticate
to Azure SQL without exposing reusable credentials in argv? SqlPackage
170.5.96.0 rejects `/Profile` for Export, and its documented password and
access-token options put credentials in argv. Resolve this before
proposing direct export. [SqlPackage Export parameters](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-export)

**Behavior sketch.** A future `--from-azure-sql <connection-variable>`
would export the live database read-only, warn about concurrent writes,
then import and seal. An explicit `--consistent` would warn of charges,
create a same-server database copy, wait until
online, export it, and remove only the verified copy. A collision must
leave the existing database intact. Ambiguous creation, replacement,
cancellation or failed removal must preserve the source and any unverified
resource, identify possible ongoing charges and provide recovery guidance.

**Needs first.** F2 shipped. Prove management-plane create-only and
identity-safe deletion (or equivalent exclusivity), including a concurrent
replacement and a lost create response, in a disposable authorized account.
Determine required permissions and billing before scheduling this feature.

---

## F3 `add-image-export-import` (0.3)

**Why.** Building an image from a large backup takes a while. One person or
one build machine should do it once, and everyone else should just fetch the
result.

**Behavior sketch.**
- `image export <name> --to <file-or-url>` writes one compressed archive
  holding the sealed files and the manifest. The target is a local path, an
  Azure Blob Storage URL or an S3 URL.
- `image import <file-or-url> [--as <name>]` fetches the archive, checks its
  SHA-256 against the manifest, and adds the image to the pool.
- Import refuses an image built by a newer engine version than the local one
  and says which engine image to use.
- An imported image is a full copy on that machine, once. Branches from it
  are thin as usual.
- Export never includes branches, credentials or engine state.

**Needs first.** 0.1 archived. Cloud URLs reuse the credential handling from
`add-cloud-backup-source`.

**Verify before proposing.**
- Which compression gives the best time for database files (try zstd), and
  export and import times for 10 GB and 50 GB images.
- Whether the archive can be streamed straight to and from storage without a
  temporary copy.
- Whether an image built under emulation on Apple Silicon imports cleanly on
  x86-64 and the reverse.

---

## F4 `add-image-refresh` (0.3)

**Why.** Teams want this morning's data, not last month's. The simple version
is a nightly job: rebuild the image while nobody is working, and have fresh
data ready at 9 am.

**Behavior sketch.**
- `image build <name> ... --replace` builds a new version of an existing
  image from backup or scripts and switches the name only on success. A failed
  build leaves the current version in place.
- Each version is kept as `<name>@<timestamp>`. Existing branches keep
  working on the version they came from. New branches use the newest version.
  Plain CLI and fixture resets restore the branch’s pinned version.
- `image list --versions` shows each version’s size and branch count.
  `image prune --keep <n>` keeps the newest n per image plus every pinned version.
- `image import --replace` publishes a verified archive as a new version.
- `branch list` marks branches that sit on an older version, and
  `branch reset <name> --latest` moves one to the newest.
- Scheduling is left to the tool the team already has: cron, launchd, Task
  Scheduler or a CI schedule. The docs give a copy-paste example for each,
  plus the full nightly recipe: build from a cloud backup, run masking
  scripts, export to shared storage.

**Needs first.** 0.1 archived. The nightly recipe also uses
`add-cloud-backup-source` and `add-image-export-import`.

**Spec note.** One existing requirement changes for backup and scripts builds: building with the name
of an existing image fails today. With `--replace` it is allowed. Propose it
as a MODIFIED requirement in `images`.

**Verify before proposing.**
- Disk needed while two versions exist, and a clear message when there is not
  enough.
- That a build running overnight does not slow or block branches in use.

---

## Follow-up draft `add-bacpac-replacement` (unscheduled)

**Why.** Teams that refresh from BACPAC need to keep existing branch bases.

**Behavior sketch.** Extend F2’s name reservation rule to accept an occupied
name only for an explicit BACPAC replacement. Publish a fully imported and
sealed version using F4’s rules and preserve the previous version on failure.

**Needs first.** F2 archived and F4 version publication available.

**Verify before proposing.** Exercise failed import, overlapping builds and
a branch pinned to the prior version across a successful BACPAC refresh.

---

## F5 `add-github-action` (0.3)

**Why.** Pull requests are where a fresh, realistic database pays off most.

**Behavior sketch.**
- A composite action installs the CLI, prepares a pool and starts the engine.
- Inputs: image source (scripts directory or a cached backup), branch name
  (default `pr-<number>`), `require-thin`.
- Outputs: connection string, clone mode.
- A post step removes the branch.

**Needs first.** 0.2 archived. Gate G4 passed on 2026-11-15: a hosted
`ubuntu-latest` runner made a thin pool in 0.4 s and created branches in
about 260 ms (see `docs/research/spike-results.md`). The working probe is in
`spike/g4-probe/`.

**Verify before proposing.** The runner had 88 GiB free and keeps nothing
between jobs, so the image must be built or imported in each job: decide
between building from scripts, importing an exported image, and caching.
Re-check that `sudo mount -o loop` still works on hosted runners.

---

## F6 `add-branch-snapshots` (0.3)

**Why.** "Save this state" and "branch from my branch" make branches feel like
git.

**Behavior sketch.**
- `branch snapshot <branch> --as <image>` seals the branch's current state as
  a new image. The branch is briefly detached, cloned, and re-attached.
- `branch create <name> --from-branch <branch>` is a snapshot plus a create.
- Image manifests record their parent.

**Needs first.** 0.1 archived.

**Verify before proposing.** Detach and re-attach time on a large database
with open connections.

---

## F7 `add-mcp-server` (0.3)

**Why.** Agents should get a sandbox database through a tool call, with
guardrails, instead of shell access.

**Behavior sketch.**
- `sqlbranch mcp` serves tools over stdio: list images, create, reset and
  remove a branch, get a connection string.
- Agent-created branches are always short-lived and labeled.
- No tool can touch the engine, images, or branches the agent did not create.

**Needs first.** 0.2 archived (labels, time limits, agent skill).

**Verify before proposing.** The current MCP SDK for .NET and its transport
options.

---

## F8 `add-aspire-integration` (0.4)

**Why.** .NET Aspire is where many .NET teams now wire up local dependencies.

**Behavior sketch.**
- `builder.AddSqlBranch("db", image: "shop")` gives the app host a branch and
  hands the connection string to projects that reference it.
- The branch is removed when the app host stops, unless marked persistent.

**Needs first.** 0.2 archived.

**Verify before proposing.** The current Aspire resource and lifecycle APIs.

---

## F9 `add-windows-refs-backend` (0.4)

**Why.** Teams with SQL Server installed on Windows should not need Docker.

**Behavior sketch.**
- The pool is a folder on a ReFS volume or Dev Drive. Clones use block
  cloning.
- Branches attach to a local SQL Server instance named in the config.
- `doctor` checks the volume type and the instance's file permissions.

**Needs first.** 0.1 archived, and a short Windows spike equal to gate G1.

**Verify before proposing.** That copies by the service account are block
clones; the 8176-clones-per-block limit and what happens when it is reached.

---

## F10 `add-live-source` (0.4)

**Why.** Asking for a backup file is a hurdle. Many users want "branch from
that server".

**Behavior sketch.**
- `image build <name> --from-server <connection> --database <db>` takes a
  copy-only backup and builds the image from it.
- The connection needs backup permission only. The source is never modified.

**Needs first.** 0.1 archived.

**Verify before proposing.** How to move the backup when the source server
and the engine do not share storage.

---

## F11 `add-isolated-branches` (0.4)

**Why.** Some tests change server-level state (logins, jobs, settings) and
need a whole instance.

**Behavior sketch.**
- `branch create <name> --from <image> --isolated` starts a dedicated
  container with its own port. The files are still clones.
- `status` lists isolated branches with their memory use.

**Needs first.** 0.1 archived.

**Verify before proposing.** Start-up time and memory per extra instance.

---

## F12 `add-podman-runtime` (0.5)

**Why.** Some companies do not allow Docker.

**Behavior sketch.**
- `runtime: podman` in the config. Same commands, same output.

**Needs first.** 0.1 archived (the container runtime boundary).

**Verify before proposing.** Rootless Podman and the engine's file ownership.

---

## F13 `add-ef-migrations-source` (0.4)

**Why.** Many .NET teams keep their schema as Entity Framework Core migrations.
Today they must run `dotnet ef migrations script` and build from the script
folder; asked for on r/programiranje (2026-11-22) as the way they set up test
databases with Testcontainers.

**Behavior sketch.**
- `sqlbranch image build app --from-ef-migrations src/App.Data` builds an image
  from an empty database by applying the project's migrations, then runs any
  `--post-script` files (for example a seed), and seals it like a script build.
- Options for the startup project and the DbContext when the project has more
  than one, matching `dotnet ef`.
- The manifest records the project, the last migration applied and the EF Core
  version, so `image list` shows which schema an image has.

**Needs first.** 0.2 archived.

**Verify before proposing.** Whether to call the `dotnet ef` tool (a dependency
the user installs, like SqlPackage) or to load the migrations assembly
directly; how `--idempotent` scripts behave with the script build's batch and
restriction rules; and EF Core versions to support.

---

## Follow-up from add-branching-core: a killed build can leave an image folder with no manifest

**Why.** The build's shell steps run through `docker exec` and finish even when
the client is killed. A kill during the seal step can leave
`/pool/images/<name>/` without `manifest.json`. `status` lists only folders
that hold a manifest, so it exits 0, while a later `image build <name>` fails
because the name check sees the folder.

**Behavior sketch.**
- `status` reports an image folder with no manifest and no running build lock
  as a mismatch, and the name check treats it the same way.
- The same sweep covers `/pool/images/.removing-*`.

**Needs first.** 0.1 archived.

**Verify before proposing.** Kill a build at each step after `seal`.

## Follow-up from add-branching-core: the staged backup counts against the pool

**Narrowed 2026-11-19.** Builds from a backup URL (`add-cloud-backup-source`)
restore straight from the URL and stage nothing, so this applies only to builds
from a local `.bak` file.

**Why.** `image build` copies the `.bak` into the build folder in the pool
before the restore, so the pool must hold the backup and the restored files at
once. In the 9.5 bench (StackOverflow2013, 50.78 GiB of files from a 14.8 GB
compressed `.bak`), the default 60 GB pool failed with "needs 51.03 GiB and
40.76 GiB is available", and the bench needed an 80 GB pool. The failure also
comes late: the restore-space check runs only after the backup is copied and
hashed, because only the engine reads the file list. A user with a 50 GB
database and default settings waits for a 15 GB copy and then gets exit 1.

**Behavior sketch.**
- Stage the backup outside the pool: copy it into the engine container's own
  filesystem (for example `/var/opt/mssql/staging/`), restore from there, and
  delete it in the same cleanup that removes the build folder today. The pool
  then needs room only for the restored files. A backup the user keeps in
  `<pool>/import/` is used in place, as now.
- The backup-space check moves to the engine's filesystem instead of the pool.
- The error for too little pool space names the size the image needs and the
  `poolSize` that would fit it, so the user can tear down and re-init (pool
  resizing is a later change).
- `docs/images.md` and the README say how much pool an image takes: about the
  sum of its data files, which is often several times the compressed `.bak`.

**Needs first.** 0.1 archived.

**Verify before proposing.**
- Where the engine container's writable layer lives on Linux and on Docker
  Desktop, and whether it has the room: on Docker Desktop it is the VM disk,
  on Linux the Docker root, which may be a small root partition. If it may
  be too small, keep staging in the pool as the fallback and say so.
- That a killed build cannot leave the staged copy behind for good: a staged
  file in the container layer survives until the container is recreated, so
  `status` or the next build must clean it.
- Re-run the 9.5 bench with the default 60 GB pool.
