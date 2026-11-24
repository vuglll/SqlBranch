# Proposal

## Why

A team needs a fresh masked image each morning while developers finish work on yesterday's branches. This change implements feature F4 (`add-image-refresh`) of release 0.3 in `docs/roadmap.md`: a scheduled build publishes only a complete image, and each developer chooses when to discard branch edits.

Prerequisites, checked 2026-11-24 against `docs/roadmap.md` and `openspec/changes/archive/`:

- F4's "Needs first" is "0.1 archived", met on 2026-11-17. F4 has no spike gate.
- The nightly recipe also uses F1 (cloud restore and masking hooks) and F3 (archive export and import). F1 was archived on 2026-11-19, F3 on 2026-11-19 and F2 (BACPAC source) on 2026-11-20, so the import requirements this change modifies are in the main `images` spec.
- BACPAC replacement stays out. Its draft, `add-bacpac-replacement` in `docs/feature-drafts.md`, needs "F2 archived and F4 version publication available". F2 is archived; F4 version publication is what this change builds, so that draft can start after this change is archived.

## What Changes

- Add `sqlbranch pool upgrade`, an explicit, one-way upgrade from pool format 1 to format 2. It rewrites the owner-only engine credentials file so SqlBranch 0.2.x tools stop before changing anything, gives every existing image its first version ID, and records each existing branch's base. `sqlbranch init` still creates format-1 pools. Nothing upgrades a pool without this command.
- Add `--replace` to backup and scripts builds and to `image import`. Keep sealed versions and switch the current version once, after every build step succeeds. Wait up to `publishWait` (new setting, default 10 minutes) for running creates and resets, then give up with the new error code `catalog_busy`.
- Add `image list --versions` with per-version sizes and branch counts, and `image prune --keep <n>`, which keeps the newest n versions per image plus every version a branch uses, with no cap. In copy mode each retained version is a full set of files.
- Plain CLI and fixture resets restore the branch's own version. Only `branch reset <name> --latest` moves it to the current version. `branch list` shows which branches are behind.
- The refresh options (`--replace`, `--versions`, `prune`, `--latest`) need format 2 and exit 3 with the new error code `pool_upgrade_required` on a format-1 pool.
- Document cron, scheduled CI, launchd and Windows Task Scheduler jobs, cloud restore with masking, export, and the developer import and reset steps.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `images`: publication, retention, removal, version listing, prune input and output, import `--replace`.
- `branches`: version references, the behind marker and reset selection.
- `engine`: pool format, `pool upgrade`, behavior of 0.2.x clients on an upgraded pool, and status of interrupted version work.
- `configuration`: the `publishWait` setting.
- `test-fixtures`: reset keeps the pinned base, and the failure when that base is gone.

## Impact

Core image and branch operations, CLI metadata, status, on-pool metadata, JSON fields, two new error codes and user docs change.

**Breaking for older clients, after an opt-in upgrade.** Once a user runs `sqlbranch pool upgrade`, the `sqlbranch` tool 0.2.2 (and any 0.2.x) and SqlBranch.Testing 0.2.x packages can no longer use that pool:

- 0.2.2 `init` on the existing pool, `engine start`, `status`, every `image build`, `image import`, `image rm` and every `branch` command exit 1 with `operation_failed` and the message "The engine credentials file in <pool> is not valid." The released code cannot be changed, so the message does not say to upgrade. The docs and release notes quote it and give the fix: update the tool and the package.
- SqlBranch.Testing 0.2.x throws with the same message, and its availability check returns false, so tests that skip on it are skipped instead of failing.
- The upgrade cannot be undone. A pool that has not been upgraded keeps working with 0.2.2 and with this release.

**Known limitation, not blocked.** 0.2.2 `image list` and `image export` do not read the credentials file, so they keep running on an upgraded pool. They see only the version the upgrade created (while it is retained): they can list or export an older version than the current one, do not list images first built after the upgrade, and a 0.2.2 export can fail if `image prune` removes its version during the export.

F3 archives keep carrying one manifest and its files. The fixture keeps its reset-to-base contract.

Peak space, workload impact during refresh, crash-safe publication and the credentials-file rename are **not verified yet**. Spikes 1.1-1.3 settle them as the first task group after approval (Docker is available on the maintainer's Linux machine). Real scheduler runs are spike 1.4; launchd and Task Scheduler examples may ship marked "not verified". See [the dated research note](../../../docs/research/2026-11-19-image-refresh-facts.md).

## Out of scope

Built-in scheduling, automatic branch resets, arbitrary version selection, archive history, incremental builds, automatic pruning, downgrading a pool, making `init` create format-2 pools, and performance guarantees. BACPAC replacement is deferred to `add-bacpac-replacement`.

## Decisions for the maintainer

- Should a later release make `sqlbranch init` create format-2 pools? This change keeps format 1 so that no one breaks a 0.2.x test package without opting in.
- `pool upgrade` has no confirmation prompt or `--yes` flag; running the command is the opt-in. Agents can run it unasked. Add `--yes` if that is a concern.
