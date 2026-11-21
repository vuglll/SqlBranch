# Tasks

The scripts are in `spike/` and its README has the commands. Record every
number and surprise in `docs/research/spike-results.md` as you go. A failed
gate is a result, not a reason to work around it silently.

The first pass runs on an Apple Silicon Mac with Colima. SQL Server runs
under Rosetta there, so the Mac decides correctness and the x86-64 Linux pass
decides performance.

## 1. Pool and engine on the Mac

- [x] 1.1 Run `spike/00-colima.sh` and verify Colima is running with the vz VM type and Rosetta, and `docker info` reports the Colima host
- [x] 1.2 Run `spike/01-pool.sh` and verify the output shows `reflink=1` and `PROBE: thin`
- [x] 1.3 Run `spike/02-engine.sh` and verify it prints the SQL Server version and `PROBE: thin (reflink works inside the container)`
- [x] 1.4 Stop and start Colima, re-run 01 and 02, and verify the engine comes back with its databases; record what did not survive the restart

## 2. Image and branch with the 10 GB database

- [x] 2.1 Download and extract StackOverflow2010 (about 10 GB) and run `spike/03-image.sh so2010 <folder>`; verify the image files are listed read-only and record `copy_ms` and `seal_ms`
- [x] 2.2 Run `spike/04-branch.sh dev1 so2010` and verify a query against `dev1` from a SQL client on the Mac returns rows; record `clone_ms`, `attach_ms` and `pool_growth_mib`
- [x] 2.3 Run `spike/05-verify.sh so2010` and verify it ends with `RESULT: 7 passed, 0 failed`; paste the output into the results file
- [x] 2.4 Run `MODE=never spike/04-branch.sh dev-copy so2010` and record the full-copy time next to the thin time

## 3. Gate G1 with the 50 GB database

- [x] 3.1 Download and extract StackOverflow2013 (about 50 GB), build image `so2013`, and record the exact data and log sizes the script prints
- [x] 3.2 Run `spike/06-bench.sh so2013` and record all five timings and the growth for 20 branches; G1 needs every `total_ms` under 10000 and growth under 5 percent
- [x] 3.3 Run `spike/05-verify.sh so2013` and verify 7 passed, 0 failed; record `write_ms` and `checkdb_ms`
- [x] 3.4 Read the error log lines the verify script prints and record whether any mention I/O problems on files under /pool (gate G5 on the Mac)

## 4. Gates G2 and G5 on x86-64 Linux

- [x] 4.1 Rent an x86-64 Linux VM with a data disk of at least 150 GB, format the disk as XFS with reflink, mount it at /mnt/sqlbranch-pool, and adapt scripts 02 to 06 to run without Colima; verify `05-verify.sh` passes there
- [x] 4.2 Run `06-bench.sh so2013` there and record the timings as the official G1 numbers
- [x] 4.3 Run the same insert and update workload on a branch (shared blocks) and on a `MODE=never` copy (no shared blocks), three runs each, and record the median times and the ratio (G2)
- [x] 4.4 Repeat the workload on the same branch and record whether the cost goes away once blocks are no longer shared
- [x] 4.5 Read the SQL Server error log after all runs and verify it has no I/O, FUA or file system warnings for files under /pool (G5)

## 5. Gate G3: Docker Desktop

- [x] 5.1 Run `spike/probe-docker-desktop.sh` and record whether it prints `THIN-OK` or the exact error
- [x] 5.2 Record G3 as: thin works on Docker Desktop, or thin on a Mac needs Colima; write down what the README must tell Mac users

## 6. Gate G4: GitHub-hosted runner

- [x] 6.1 Write a workflow that creates a loopback XFS pool with sudo on ubuntu-latest (reuse `spike/vm/pool.sh`), runs the engine, builds a small image from scripts and creates a branch, and record whether the reflink probe passes
- [x] 6.2 Record setup time for the pool and the engine, and the free disk space on the runner

## 7. Decision

- [x] 7.1 Fill in the gate table in `docs/research/spike-results.md` with pass or fail and the numbers for G1 to G5
- [x] 7.2 Write the go or no-go decision and, for each failed gate, the change to `docs/roadmap.md` it implies, and verify the roadmap is updated to match

## Workflow follow-up

- Archive this change once the decision is written and the roadmap is updated.
- If the decision is go, start `add-branching-core`.
