# Proposal

## Why

SqlBranch rests on one unproven step: SQL Server working correctly and fast on
data files that were cloned with XFS reflink and then attached. No source found
on 2026-11-15 tests this (see `docs/research/2026-11-15-market-and-storage.md`).
Building anything before this is settled risks months of wasted work.

## What Changes

- Add throwaway scripts under `spike/` that build a pool, build an image from
  a backup, and clone and attach branches by hand.
- Measure clone time, disk use, isolation, integrity and first-write cost on
  native Linux.
- Try the same on Docker Desktop and on a GitHub-hosted runner.
- Write the results and a go or no-go decision to
  `docs/research/spike-results.md`.
- No product code and no user-visible behavior. This change sets
  `skip_specs: true`.

Targets release 0.0 in `docs/roadmap.md`.

## Capabilities

### New Capabilities

None. This is a research spike with no spec-level behavior.

### Modified Capabilities

None.

## Impact

- New folder `spike/` (deleted or kept as a reference once 0.1 ships).
- New file `docs/research/spike-results.md`.
- Gates G1 and G5 decide whether `add-branching-core` starts. Gates G3 and G4
  decide the scope of `add-docker-desktop-thin-pool` and `add-github-action`.
- First pass runs on an Apple Silicon Mac with Colima and Rosetta, with about
  80 GB free for the 10 GB database and 150 GB for the 50 GB one. That pass
  decides correctness. Performance numbers (G2) come from a rented x86-64
  Linux VM, because Microsoft does not support SQL Server under emulation.
- The storage step alone (XFS loopback pool, reflink clones, isolation) was
  checked on Linux on 2026-11-15; see `docs/research/spike-results.md`.
