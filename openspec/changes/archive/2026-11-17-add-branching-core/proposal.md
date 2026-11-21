# Proposal

## Why

Postgres teams can give every developer and pull request a full-size, writable
copy of a database in seconds with open-source tools. SQL Server teams cannot:
the options are commercial, Windows-only or both (sources in
`docs/research/2026-11-15-market-and-storage.md`, checked 2026-11-15). Tests
run against empty databases, and developers share one dev database. AI agents
that write SQL need a safe copy to work in, which makes the gap wider.

## What Changes

- Add the `sqlbranch` command-line tool with a managed SQL Server engine
  container and a storage pool.
- Build sealed, read-only images from a full database backup file.
- Create, list, reset and remove branches: writable databases whose files are
  clones of an image.
- Use thin clones (shared blocks) where the pool supports them and full
  copies where it does not, and always report which.
- Create a thin pool automatically (an XFS file the tool mounts itself)
  where the machine's storage cannot share blocks, including Docker Desktop
  for macOS.
- Remove everything SqlBranch created with one command (`teardown`).
- Give every command text and JSON output and stable exit codes.
- Read settings from flags, environment variables and `.sqlbranch.yml`.
- Let teams pick a development edition that matches production (Standard
  Developer or Enterprise Developer on SQL Server 2025), and create
  full-copy branches on request for benchmark runs.

Targets release 0.1 in `docs/roadmap.md`.

**Needs first: the spike, as a conditional go.** On 2026-11-15 the spike
`spike-thin-clone` passed G1, G3 and G5 on a Mac and G4 on a GitHub-hosted
runner (see `docs/research/spike-results.md`). The Mac run decides correctness; its G1
timings ran under Rosetta and are not official. Still open: spike tasks
4.1-4.5 (official G1 timings, G2 first-write cost and G5 on x86-64 Linux)
and 7.1-7.2 (gate table and decision). This change starts on a conditional
go:

- Work may be applied, merged into `dev` and archived before those tasks
  finish. None of this change's tasks needs the x86-64 VM.
- Release 0.1 (the merge of `dev` into `main`) is gated on spike sections
  4 and 7 and on a 50 GB bench of this tool on x86-64 Linux, which is the
  official G1 record (see the release gate in `tasks.md`).
- If the x86-64 run misses G1 (a 50 GB branch over 10 s) or G5 (I/O warnings
  on pool files), release 0.1 is held and the roadmap is revised.
- If G2 shows a first-write slowdown, 0.1 still ships thin by default. The
  docs state the measured cost and point write-heavy work to `--copy`
  branches (design D14).

Out of scope: Windows without Docker, resizing a pool, the testing package,
agent skill, masking, snapshots of branches, any server or UI, differential,
log and striped backups, databases with FILESTREAM or memory-optimized
filegroups, and cleanup of debris after a crash (`doctor --fix`, later).
Docker Desktop for Windows ships as not verified unless task 8.3 proves it.

## Capabilities

### New Capabilities

- `engine`: the pool and the managed SQL Server container: setup, start and stop, teardown, health, environment checks and clone-mode detection.
- `images`: sealed, read-only database images built from backup files, with manifests, listing and removal.
- `branches`: writable databases cloned from images: create, list, connect, reset and remove, with isolation guarantees.
- `cli-output`: output formats, the JSON contract, exit codes and non-interactive behavior shared by every command.
- `configuration`: where settings come from, their precedence, validation and how secrets are kept out of files and logs.

### Modified Capabilities

None. This is the first change.

## Impact

- New repository layout: `src/SqlBranch.Core`, `src/SqlBranch.Cli`,
  `tests/SqlBranch.Core.Tests`, `tests/SqlBranch.IntegrationTests`,
  `tools/bench`.
- Runtime needs: Docker. Thin mode works out of the box through a managed
  XFS pool, which needs permission to run a privileged helper container.
  Without that permission the tool runs in copy mode. On first `init` the
  managed pool is a sparse file of `poolSize` (60 GB by default) under
  `/var/lib/sqlbranch/` on the container host; `init` prints what it
  creates.
- Dependencies: Microsoft.Data.SqlClient, a command-line parser, a YAML
  reader. The container runtime is driven through the `docker` command.
- Published as a dotnet tool on NuGet under Apache-2.0.
- Uses the SQL Server Developer edition container image, which is for
  non-production use
  (https://www.microsoft.com/en-us/sql-server/blog/2024/02/15/how-sql-developers-can-maximize-savings,
  checked 2026-11-15). The tool says so at `init`.
