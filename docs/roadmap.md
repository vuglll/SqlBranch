# SqlBranch roadmap (open source)

SqlBranch gives every developer, pull request and AI agent its own writable,
full-size SQL Server database in seconds. It does for SQL Server what DBLab and
Neon do for Postgres.

"SqlBranch" is a working name. It is a quick swap if you prefer another.

This file covers the open-source project only. Any commercial plan is kept
separately and is not part of this repository.

## How it works, in four lines

1. A **pool** is storage that can clone files without copying them: an XFS
   file system with reflink. Where the machine has none, the tool creates
   one inside a file and mounts it itself.
2. An **engine** is one managed SQL Server container with the pool mounted.
3. An **image** is a sealed, read-only set of database files built once from a
   backup.
4. A **branch** is a writable database attached to the engine. Its files are
   copy-on-write clones of an image, so creating one takes seconds and almost
   no disk.

## Releases

| Release | Theme | OpenSpec change | Done when |
|---|---|---|---|
| 0.0 | Spike: prove the core | `spike-thin-clone` | Gate G1 passes and results are written down. **Passed on a Mac 2026-11-15; x86-64 run done 2026-11-19 (G1 and G5 pass, G2 measured); archived 2026-11-20** |
| 0.1 | Branching core | `add-branching-core` | A 50 GB image branches in under 10 s on Linux and on Docker Desktop for macOS. **Archived 2026-11-17; release held for its gate. Done on x86-64 Linux 2026-11-20: official timings, G2, the README quick start on clean Linux. the Linux XFS integration run (green after #22: 136 passed, 6 skipped, 0 failed). The Windows check is recorded as not verified (2026-11-20, no Windows machine); the README says so. Nothing open on this gate** |
| 0.2 | Tests and agents (launch) | `add-test-fixtures` | An integration test gets a fresh branch from one line of C#. **Archived 2026-11-18. The 16 GB fixture-concurrency measurement is done (2026-11-20, native Linux: 400 parallel branches). The native Linux demo (`tools/demo/demo.sh`, task R.1 of `add-test-fixtures`) and the 60-second video moved to later on 2026-11-20 and no longer block a release. Nothing open on this gate** |
| 0.3 | Cloud sources, sharing and CI | one change per feature, from `docs/feature-drafts.md` | A nightly job builds an image from a cloud backup and a teammate imports it. **Release gate for F1: build from a real Azure Blob and a real S3 URL (task 5.4 of `add-cloud-backup-source`), the folder-named SAS credential and the region option on the three read statements against real storage (task 1.1), and whether a SQL Managed Instance copy-only backup carries the `XTP` filegroup (task 1.3, needs a Managed Instance on the SQL Server 2022 update policy). Release gate for F3 (`add-image-export-import`): export and import through a real Azure Blob and a real S3 location (task 5.4, `SQLBRANCH_IT_AZURE_EXPORT_URL` and `SQLBRANCH_IT_S3_EXPORT_URL` with F1's secrets), and the maintainer's decision on the archive codec (task 1.1 ran 2026-11-20: zstd level 1 is 2.06 times faster than gzip on export at a smaller size, which meets design D2's rule for switching). Task 6.4, Apple Silicon to x86-64 and back, passed 2026-11-20** |
| 0.4 | Windows and more sources | one change per feature | Branching works on a Windows Dev Drive without Docker |
| later | Whatever users ask for | one change per feature | — Also the launch demo: `tools/demo/demo.sh` verified end to end on native Linux XFS, and the 60-second video |

**Launch at 0.2, not 0.1.** A 60-second video of a 50 GB clone plus the
one-line test fixture is the launch post. The video and its demo script
come later; the first package release does not wait for them. 0.1 alone has no easy way in for most
developers.

### 0.0 Spike (go or no-go)

The whole project rests on one unproven step: SQL Server running happily on
reflink-cloned data files. Nothing else is built until gate G1 passes.

| Gate | Question | Result (2026-11-15) |
|---|---|---|
| G1 | Does a 50 GB image branch in under 10 s, stay isolated, and pass DBCC CHECKDB after writes? | **Pass.** Mac: 0.46 to 0.57 s per branch. x86-64 Linux (official, 2026-11-19): 0.40 to 0.41 s per branch, 2.1 to 2.5 s per `sqlbranch branch create`, 20 branches added 0.03 percent, 7 of 7 checks |
| G2 | How much slower are first writes to cloned blocks? | **Measured on x86-64 Linux (2026-11-19).** Scattered updates on a 50 GB branch: about 5.4 times a full copy (4.5 with an 8 KiB copy-on-write extent); the cost stays after the blocks are unshared, because the files end up in millions of fragments. Inserts cost the same as on a copy. Pool was an XFS file on ext4, which may add to the cost |
| G3 | Can a Mac host a thin pool? | **Pass** on Colima and on Docker Desktop, with matching speed and correctness |
| G4 | Can a GitHub-hosted runner host a thin pool? | **Pass.** Pool in 0.4 s, branches in about 260 ms, 8 of 8 checks |
| G5 | Does the SQL Server error log stay free of I/O warnings on cloned files? | **Pass** on a Mac and on x86-64 Linux. None in any log |

Details and raw numbers are in `docs/research/spike-results.md`.

**What the spike changed in the plan**

- Images must rebuild the database's log when they are sealed. Without it
  every branch rewrote part of the source's log (68 MiB each on the test
  database) and 20 branches cost 15 percent of the image. With it, growth
  was zero.
- Docker Desktop for macOS can host a thin pool, so that moved from a later
  feature into 0.1. The tool creates and mounts the pool itself.
- The pool mount is lost when Docker or the machine restarts. The tool
  remounts it on start.
- Thin branches fragment under heavy scattered writes, and G2 on x86-64
  measured about 5 times the cost of a full copy for scattered updates. They
  stay positioned for development, tests and CI, `branch reset` gives a fresh
  clone, and `--copy` branches are the answer for write-heavy work.

**Spike done:** section 4 ran on a Hetzner CCX33 on 2026-11-19; results in
`docs/research/spike-results.md`.

### 0.1 Branching core

- `sqlbranch init`, `status`, `doctor`, `engine start|stop`, `teardown`
- `image build --from-bak`, `image list`, `image rm`
- `branch create`, `list`, `conn`, `reset`, `rm`
- Thin mode by default: the tool creates and mounts its own XFS pool where
  the machine has none. Copy mode is the fallback, always reported
- Text and JSON output, stable exit codes, `.sqlbranch.yml`
- A choice of development edition to match production, `--copy` branches
  for benchmarks, and `--keep-recovery-model` for images
- Thin mode is verified on Linux, Colima and Docker Desktop for macOS.
  Docker Desktop for Windows is not verified yet and falls back to copy mode
  if the pool cannot be mounted.
- Images get a rebuilt, minimal log so branches stay cheap

### 0.2 Tests and agents (launch)

- `SqlBranch.Testing` NuGet package: a fresh branch per test class or test
- Images built from SQL scripts, and post-build scripts for backup images
- Short-lived branches with a time limit, and `branch prune`
- Agent skill generated from command metadata

### 0.3 and later

Each feature is one small OpenSpec change. The queue is below; the drafts are
in `docs/feature-drafts.md`.

## Feature queue

"0.1 archived" is met as of 2026-11-17, and "0.2 archived" as of 2026-11-18.

| # | Change | Release | Needs first |
|---|---|---|---|
| F1 | `add-cloud-backup-source` | 0.3 | 0.1 archived. **Archived 2026-11-19**; its real-cloud checks are on the 0.3 release gate |
| F2 | `add-bacpac-source` | 0.3 | 0.2 archived. **Archived 2026-11-20**; Windows (SqlPackage and file permissions) is recorded as not verified; the 10/50 GB timings are on the 0.3 release gate |
| F3 | `add-image-export-import` | 0.3 | 0.1 archived; F1 for cloud URLs. **Archived 2026-11-19**; its x86-64 and real-cloud checks are on the 0.3 release gate |
| F4 | `add-image-refresh` | 0.3 | 0.1 archived |
| F5 | `add-github-action` | 0.3 | 0.2 archived (gate G4 passed) |
| F6 | `add-branch-snapshots` | 0.3 | 0.1 archived |
| F7 | `add-mcp-server` | 0.3 | 0.2 archived (agent skill) |
| F8 | `add-aspire-integration` | 0.4 | 0.2 archived |
| F9 | `add-windows-refs-backend` | 0.4 | 0.1 archived; a Windows spike |
| F10 | `add-live-source` | 0.4 | 0.1 archived |
| F11 | `add-isolated-branches` | 0.4 | 0.1 archived |
| F12 | `add-podman-runtime` | 0.5 | 0.1 archived |
| F13 | `add-ef-migrations-source` | 0.4 | 0.2 archived |

**Cloud databases come first in 0.3.** F1 covers anything that can put a
native backup in Azure Blob Storage or S3 (SQL Server on VMs, Azure SQL
Managed Instance, Amazon RDS). F2 covers Azure SQL Database, which has no
native backup. Until then, cloud teams can build images from SQL scripts
(0.2) or from a backup file they download themselves (0.1).

**The nightly recipe.** F1, F3 and F4 together give the flow most teams want:
a build machine rebuilds the image overnight from the latest cloud backup,
runs the masking scripts, and exports the result to shared storage. In the
morning each developer runs one `image import` and resets their branches.
The schedule itself is the team's own cron or CI job.

F1, F6, F8 and F12 touch different parts of the code. After 0.2 they can run
in parallel, one git worktree and one agent session each.

## How a feature ships (the agentic loop)

One feature, one OpenSpec change, one branch, one orchestrator session.
Which agent and model does each step is in `docs/agent-workflow.md`.

1. **Pick** the next row in the feature queue whose "Needs first" is met.
2. **Propose.** Start from the draft in `docs/feature-drafts.md`. Re-check
   every fact in it first, because drafts age. Run the propose workflow
   (`/openspec-propose`).
3. **Validate.** `openspec validate <change> --strict` must pass.
4. **Human gate.** Read the proposal and the specs before any code is
   written. This is the one place a person must look. Design and tasks follow
   from the specs.
5. **Apply** on a branch from `dev` (`/openspec-apply-change`). A task is ticked only
   when the build has no warnings and the tests it names pass.
6. **Verify.** Run the integration suite with Docker
   (`SQLBRANCH_IT=1 dotnet test`). On a thin pool, run the bench script too.
7. **Review and merge** the pull request into `dev`.
8. **Archive** the change so its deltas become the main specs.
9. **Release** by merging `dev` into `main`. Batch two to four features.

### Rules for agents

These are also in `openspec/config.yaml`, which OpenSpec feeds to every
workflow.

- Touch only the SqlBranch pool and containers that SqlBranch created. Never
  stop, remove or exec into any other container.
- Never run destructive SQL against a server that is not the SqlBranch
  engine.
- Integration tests need Docker and run only when `SQLBRANCH_IT=1`.
- Platform and pricing facts carry a source URL and the date checked.
- No secrets in the repository, in logs or in test output.
- Stop at the human gate. Do not start applying a change whose specs were not
  reviewed.
- One change per session. If a task uncovers work outside the change, write
  it into `docs/feature-drafts.md` and carry on.

## Open-core seams

The core stays fully usable on its own. Paid add-ons, if they ever exist, plug
in from outside through these seams and never fork the core:

- **Build hooks:** scripts that run after an image is restored and before it
  is sealed (0.2).
- **JSON contract:** every command has stable JSON output with a schema
  version, so other programs can drive the CLI.
- **Core library:** `SqlBranch.Core` exposes the same operations as the CLI.
- **Manifest labels:** images and branches carry free-form labels.

## What 0.1 leaves out on purpose

- Windows without Docker, and resizing a pool after it is created
- Databases with FILESTREAM or memory-optimized filegroups (rejected with a
  clear message until tested)
- Striped, differential and log backups
- Data masking beyond running your own scripts (0.2)
- Any server, web UI or multi-user access

## Risks

| Risk | What we do about it |
|---|---|
| The storage core does not hold up | The spike passed on a Mac; the x86-64 run is the remaining check |
| Thin branches slow down or bloat under heavy scattered writes | Copy-on-write hint set at `init`; `--copy` branches for write-heavy work; G2 measured on x86-64 (about 5 times a copy for scattered updates), stated in the README |
| The managed pool needs privileged containers | Falls back to copy mode with a clear reason |
| Microsoft only supports XFS and ext4 for data files | The thin pool is always XFS |
| SQL Server container images target x86-64 Linux hosts | `doctor` warns on other hosts; Apple Silicon runs under emulation |
| Developer edition is for non-production use only | The README and `init` say so; SqlBranch is a dev and test tool |
| Redgate cuts prices or Microsoft adds branching | Stay the simple, free, scriptable option; ship fast |
