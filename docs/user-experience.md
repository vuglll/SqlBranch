# User experience and use cases

What a developer needs, what they type, and what the tool is good for. This is
the target experience; release numbers say when each part arrives.

## What you need

| | Mac | Windows | Linux |
|---|---|---|---|
| Container runtime | Docker Desktop or Colima | Docker Desktop | Docker |
| Extra setting | Rosetta on (Apple Silicon) | none | none |
| Clone speed in 0.1 | Fast on both (verified) | Fast expected, not verified yet | Fast |
| .NET SDK | yes | yes | yes |
| Memory for the engine | at least 2 GB | at least 2 GB | at least 2 GB |

- **Fast clones need no setup.** `init` creates its own XFS pool inside a
  file and mounts it. In the spike this worked on Docker Desktop for macOS,
  on Colima and on a GitHub-hosted runner: a 51 GiB database branched in
  about half a second.
- **It needs permission to run a privileged helper container** to create
  that pool. Where a company machine forbids it, the tool falls back to full
  copies and says so.
- **Apple Silicon runs SQL Server under emulation.** It works, but Microsoft
  does not test or support that setup. Good for correctness, not for timings.
- **Docker Desktop for Windows is not verified yet.** If the pool cannot be
  mounted there, branches are full copies.
- **Windows without Docker** arrives with the Dev Drive backend in 0.4.

## One-time setup

```
dotnet tool install -g SqlBranch
sqlbranch init
```

`init` creates the pool, starts the engine and says which clone mode you got.
After a restart of Docker or the machine, `sqlbranch engine start` remounts
the pool and brings the branches back.

## Build an image (once per database)

```
sqlbranch image build shop --from-bak ~/Downloads/shop.bak
```

| Source | Flag | Release |
|---|---|---|
| Backup file on disk | `--from-bak <file>` | 0.1 |
| Folder of SQL scripts | `--from-scripts <dir>` | 0.2 |
| Masking or trimming before sealing | `--post-script <file>` | 0.2 |
| Backup in Azure Blob Storage or S3 | `--from-bak <url>` | 0.3 |
| Azure SQL Database | `--from-bacpac <file>` or `--from-azure-sql <connection>` | 0.3 |
| A teammate's or build machine's image | `image import <file-or-url>` | 0.3 |

The first build of a large database is slow: the backup is copied in and
restored once. Everything after that is fast.

## Daily use

```
sqlbranch branch create dev1 --from shop
sqlbranch branch conn dev1      # prints a connection string
sqlbranch branch reset dev1     # back to the image state
sqlbranch branch rm dev1
```

Paste the connection string into local settings once. It stays the same
after a reset.

## Use cases

### A database per developer

Everyone gets their own full-size copy. Break it, reset it, nobody else
notices.

### A database per test (0.2)

```csharp
await using var db = await SqlBranchDatabase.CreateAsync("shop");
// use db.ConnectionString; the branch is removed on dispose
```

Every test or scenario starts from the identical image. Tests can change
anything, run in parallel without seeing each other, and reset between
cases.

### Regression suites

The best fit of all. Every run starts from the same data, so a failure means
the code changed, not the data. No special setup.

### Performance tests

Strong for before-and-after comparisons: same image, same hardware, old
build against new build. For numbers you can trust:

- Create the branch with `--copy`. A thin branch pays a cost on the first
  write to each shared block, and in a dry run it fragmented and slowed down
  under heavy scattered updates. That distorts write-heavy runs.
- Keep the engine quiet. Other branches share its memory and CPU.
- Match production's edition: set `engineEdition` to `standard-developer` or
  `enterprise-developer` (SQL Server 2025 and later). Plain `developer` has
  the Enterprise feature set.
- Build the image with `--keep-recovery-model` if production runs in FULL
  recovery and log behavior matters.
- Run on x86-64 hardware that resembles production. Not on a Mac.
- If production is Azure SQL Database, trust relative results only. A
  container does not have its resource limits.

### Fresh data on a schedule (0.3)

A build machine rebuilds the image from the latest cloud backup, runs the
masking scripts and exports the result to shared storage. Developers run one
`image import` and reset their branches. Nightly, weekly or monthly: the
schedule is the team's own cron or CI job.

```
sqlbranch image build shop --from-bak https://.../shop.bak --post-script mask.sql --replace
sqlbranch image export shop --to https://.../images/shop.sqlbranch
```

### A sandbox for AI agents (0.2 skill, 0.3 MCP server)

An agent gets its own short-lived branch, works in it, and removes it. It
cannot touch images or anyone else's branches.

## Licensing in one paragraph (not legal advice)

The engine only ever runs a development edition of SQL Server, which is free
for development, test and demonstration use. Microsoft's guidance allows
restoring a copy of production data for testing, and forbids preparing data
on a development edition and moving it into production. No real users or
business decisions may depend on it. Older licensing guides use wording about
"commercial data" that people read differently, so a company with strict
compliance should confirm with its Microsoft licensing contact. Sources are
in `docs/research/2026-11-15-market-and-storage.md`.

## Known rough edges

- The first image build of a large database takes a while.
- Thin branches are for development, tests and CI. Long-lived branches with
  heavy scattered writes may bloat and slow down; use `--copy` for those
  until the x86-64 measurements are in.
- The pool has a fixed size chosen at `init` (60 GB by default).
- Sharing images needs 0.3; before that, each developer builds their own.
- Windows without Docker waits for 0.4.
