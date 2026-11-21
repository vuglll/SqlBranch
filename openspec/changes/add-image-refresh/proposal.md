# Proposal

## Why

A team needs a fresh masked image each morning while developers finish work on yesterday's branches. F4 of release 0.3 lets a scheduled build publish only a complete image and lets each developer choose when to discard branch edits.

The roadmap prerequisite is met: 0.1 was archived on 2026-11-17. F4 has no prerequisite spike gate. The nightly recipe also needs archived F1 (cloud restore and masking hooks) and approved, unarchived F3 (archive export/import). Task 4.3 requires F3 (PR #18) merged into dev. Archive F3 before F4 so the two modified import requirements exist in the main images spec.

## What Changes

- Add `--replace` to backup and scripts builds for repeatable nightly builds. Keep sealed versions and switch the current name once, after all build steps succeed.
- Add `image list --versions`, per-version sizes and branch counts and `image prune --keep <n>`. Keep the newest n per image plus every referenced base without a cap. In copy mode, each retained version consumes a full set of files.
- Plain CLI and fixture resets restore the branch’s own version. Only `branch reset <name> --latest` moves it to the current version. Branches continue serving their own data until reset. List shows which are behind.
- Document cron, launchd, Task Scheduler and CI examples, cloud restore with masking, F3 export and the developer import/reset handoff. Add `image import --replace` to publish a verified archive over an occupied name while preserving pinned versions.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `images` owns publication, retention, removal, inspection and prune input.
- `branches` owns version references, stale markers and CLI reset selection.
- `engine` reports abandoned version and reset work.
- `test-fixtures` preserves the pinned base on reset.

## Impact

Core image and branch operations, CLI metadata, status inspection, on-pool metadata, JSON fields and user docs change. Existing pools need a compatible first read. The test fixture retains its reset-to-base contract. F3 archives continue to carry a single manifest and its files.

Peak space, concurrent workload impact, crash-safe publication and real scheduler invocations are **not verified (no network in this run)**. Spikes 1.1–1.4 in tasks.md settle them. See [the dated research note](../../../docs/research/2026-11-19-image-refresh-facts.md).

## Out of scope

Built-in scheduling, automatic branch resets, arbitrary version selection, archive history, incremental builds, automatic pruning and performance guarantees. BACPAC replacement is deferred to a follow-up after F2 is archived.
