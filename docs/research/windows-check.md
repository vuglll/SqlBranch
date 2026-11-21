# Windows check: Docker Desktop for Windows

Prepared for task 8.3 of `add-branching-core`. **Not run yet.** Until someone fills
in the results below, Docker Desktop for Windows is "not verified" in the README
and in `docs/roadmap.md`.

The question: on Docker Desktop for Windows, do `init`, an image build and a
branch create work, and does `init` get a thin pool (clone mode `thin`) or fall
back to copy mode?

## What you need

- Windows 10 or 11 with Docker Desktop, set to **Linux containers** and running.
- The .NET 10 SDK (`dotnet --version` prints 10.x).
- A checkout of this repository.
- About 5 GB free disk and a few minutes. The first run pulls the SQL Server
  image (about 1.5 GB).
- Windows PowerShell 5.1 or PowerShell 7 (`pwsh`).

You do not need a backup file: the script makes a small one in the new engine.

## Run it with Claude Code

If the person running the check uses Claude Code, they can let it do the run.
They need to have accepted the invitation to `vuglll/SqlBranch` first. Open
Claude Code in an empty folder (for example `C:\src`) and paste this prompt:

```text
I'm helping a friend test their project on Windows. Please run a check for me and report the results.

1. Check the prerequisites and tell me what's missing; don't install anything without asking me first:
   - Docker Desktop is running in Linux containers mode (`docker info`).
   - `dotnet --version` prints 10.x.
   - `git` is installed.
   - At least 5 GB of disk is free.
2. Get the code: run `git clone https://github.com/vuglll/SqlBranch.git`, then `cd SqlBranch` and `git checkout dev`.
3. Read `docs/research/windows-check.md` and follow its "Run it" section:
   - Run `powershell -ExecutionPolicy Bypass -File tools\windows-check.ps1` from the repo folder.
   - It takes a few minutes. The first run pulls a 1.5 GB SQL Server image.
4. If it exits 3 with `pool_unavailable`, run it once more with `-PoolBackend auto`.
5. Rules while running:
   - Don't edit any files in the repo until step 6.
   - Don't remove or change any Docker containers, volumes or images that the script didn't create.
   - If the script fails, don't try to fix it. The failure output is the result we need.
6. Record the results in the repo:
   - Create a branch `windows-check-results` from `dev`.
   - Copy each `windows-check-*.log` into `docs/research/` and rename it `windows-check-<yyyy-mm-dd>-<n>.log`. First replace my Windows username in any paths with `<user>`, and check the logs hold no password, token or connection string.
   - In `docs/research/windows-check.md`, fill in the "Results" table and the verdict line (thin pool works / copy mode only / does not work) from the logs. Include the Windows version, Docker Desktop version and backend (WSL 2 or Hyper-V), and the PowerShell and .NET versions. Change nothing else in the file.
   - Change no other files. Commit with the message `docs(research): record the Windows check results`, with no co-author or AI attribution lines.
   - Push the branch and open a pull request into `dev` with `gh pr create --base dev` (or give me the GitHub link to open one). Don't merge it.
7. Give me a short summary: the verdict, the pull request link, and anything that looked wrong.
```

The results arrive as a pull request into `dev`, which the maintainer reviews
and merges.

## Run it

From the repository folder:

```powershell
powershell -ExecutionPolicy Bypass -File tools\windows-check.ps1
```

or `pwsh -ExecutionPolicy Bypass -File tools\windows-check.ps1`. It prints each step and writes the same
text to `windows-check-<id>.log` in the current folder.

What it does, and what it protects:

- It builds the command line, then runs `init`, an image build from a small
  backup, `branch create`, `branch list`, `status`, `branch conn` (not logged),
  a TCP check of the engine port from Windows, a `SELECT 1` through `sqlcmd` in a separate container, `branch rm` and
  `teardown --force`.
- It uses names of its own, with a random id: container
  `sqlbranch-it-engine-<id>`, volume `sqlbranch-it-pool-<id>`, pool folder
  `/var/lib/sqlbranch/it-<id>` in the Docker VM, a free port and a random
  password. It never touches `sqlbranch-engine`, port 14330,
  `/var/lib/sqlbranch/pool.xfs` or `sqlbranch-pool`, so a real SqlBranch setup
  on the same machine is safe.
- It removes only what it created, then checks that no container or volume of
  its id is left. The password and the connection string are not written to the
  log.

Options: `-PoolBackend loop-xfs` (default) tries the managed XFS pool and fails
with exit 3 `pool_unavailable` if it cannot be mounted; run it again with
`-PoolBackend auto` to see the copy-mode fallback and its reason. `-BakPath
<file>` uses your own small full backup instead of making one. `-PoolSize` and
`-LogPath` are described in the script's help (`Get-Help .\tools\windows-check.ps1`).

The script exits 0 when every step gave the expected result, 1 when one did not,
and 2 when Docker or dotnet is missing.

## Send back

The whole log file. Read it first for anything that looks private (your user
name in a path is the likely one).

## Results

Fill this in from the log. Leave a field empty if the script did not get that
far.

| Field | Value |
|---|---|
| Date | |
| Windows version | |
| Docker Desktop version, and backend (WSL 2 or Hyper-V) | |
| `docker info` OS / architecture | |
| PowerShell edition and version | |
| .NET SDK version | |
| `-PoolBackend` used | |
| `init` exit code and clone mode | |
| Fallback reason, if copy mode | |
| Image build exit code | |
| `branch create` exit code and clone mode | |
| `branch conn` exit code | |
| TCP check from Windows to 127.0.0.1:port | |
| `SELECT 1` via sqlcmd in a separate container (host.docker.internal) | |
| `teardown --force` exit code | |
| "nothing left" check | |
| Anything unexpected | |

Verdict (fill in one): thin pool works / copy mode only / does not work.


## Result (2026-11-20)

**Not verified.** No Windows machine was available. Following task 8.3 of
`add-branching-core` ("if no Windows machine is available, record it as not
verified and verify the README says so"), the 0.1 gate records Docker Desktop
for Windows as not verified; the README's platform table says so, and
`init` falls back to copy mode if the managed pool cannot be mounted. The
same applies to F2's Windows checks (SqlPackage on Windows and the owner-only
scratch ACLs, tasks 1.1 and 3.3 of `add-bacpac-source`). This check stays
available for anyone with a Windows machine.
