# Proposal

## Why

A team needs a fresh masked image each morning while developers finish work on yesterday's branches. This change implements feature F4 (`add-image-refresh`) of release 0.3 in `docs/roadmap.md`: a scheduled build publishes only a complete image, and each developer chooses when to discard branch edits.

Prerequisites, checked 2026-11-24 against `docs/roadmap.md` and `openspec/changes/archive/`:

- F4's "Needs first" is now "0.1 archived; `add-pool-format-upgrade` archived". 0.1 was archived 2026-11-17. **`add-pool-format-upgrade` is not archived yet**: it was split out of this change on 2026-11-24 and is proposed alongside it. This change must not be applied before it is archived. It supplies pool format 2, `pool upgrade --yes`, the per-name image catalog, version IDs, the branch base field, the `publishWait` setting and the error codes `catalog_busy` and `pool_upgrade_required`. Two deltas here modify requirements that change adds (see Impact). F4 has no spike gate of its own beyond task group 1. F4a and F4 ship together in release 0.3 (maintainer decision 1, 2026-11-24); one 0.3 `CHANGELOG.md` entry covers both.
- The nightly recipe also uses F1 (cloud restore and masking hooks) and F3 (archive export and import). F1 and F3 were archived on 2026-11-19 and F2 (BACPAC source) on 2026-11-20, so the import requirements this change modifies are in the main `images` spec.
- BACPAC replacement stays out. Its draft, `add-bacpac-replacement` in `docs/feature-drafts.md`, needs "F2 archived and F4 version publication available"; it can start after this change is archived.

## What Changes

Every new option and command here works only on a pool that uses format 2. On a format-1 pool they exit 3 with `pool_upgrade_required` (owned by `add-pool-format-upgrade`) and name `sqlbranch pool upgrade --yes`. Nothing here upgrades a pool.

- Add `--replace` to backup and scripts builds and to `image import`. Keep sealed versions and switch the current version once, after every build step succeeds. Before publishing, wait up to `publishWait` for running creates and resets of the name, then give up with `catalog_busy`. Creates and resets that start while a publication waits are not blocked: they use the version current when they start. A create or reset that meets the brief exclusive catalog hold of a publication, removal or prune retries for up to 30 seconds without queueing, then exits `catalog_busy`. The loser of two overlapping replacements exits `image_exists`; a replacement meeting a running prune of the name waits up to `publishWait`, and one meeting a running removal exits `catalog_busy`.
- Add `image list --versions` with per-version sizes and branch counts, and `image prune --keep <n>`, which keeps the newest n versions per image plus every version a branch uses, with no cap. Branches with an unknown base reference no version, so nothing extra is kept for them; their fix is `branch reset <name> --latest` or remove and create. Prune skips, and reports, versions being exported, names being built and names with a running create or reset, and never deletes branch files or workspaces. In copy mode each retained version is a full set of files.
- `image rm` on a format-2 pool removes every version, names branches that lose their reset base, waits up to `publishWait` for running creates and resets (`catalog_busy`), exits `catalog_busy` at once when a replacement or prune of the name is running, and retries only its own leftovers.
- `image export` on a format-2 pool reads the version current when it starts.
- Plain CLI and fixture resets keep restoring the branch's own version. Only `branch reset <name> --latest` moves it to the current version. `branch list` shows which branches are behind (`outdated`).
- The agent skill tells agents not to run `image prune` unless the user asks, and marks `--latest` as discarding local edits.
- Document cron, scheduled CI, launchd and Windows Task Scheduler jobs, cloud restore with masking, export, and the developer import and reset steps.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `images`: publication, retention, removal across versions, version listing, prune input and output, import `--replace`, export version selection.
- `branches`: the behind marker, reset to latest, retrying a broken branch, the 30-second retry of create and reset, and the `--latest` hint on a refused reset.
- `engine`: the format-2 gate for refresh options, and status of interrupted version work.
- `test-fixtures`: reset keeps the pinned base across a refresh, and the `--latest` hint.
- `agent-skill`: a guardrail for `image prune`, and reference entries for `image prune` and `branch reset --latest`.

## Impact

Core image and branch operations, CLI metadata, status, on-pool metadata, JSON fields and user docs change. No new error codes: `catalog_busy` and `pool_upgrade_required` come from `add-pool-format-upgrade`. No new setting: `publishWait` comes from there too.

The deltas "Plain reset refuses a removed or unknown base" and "A broken branch can be reset again" (`branches`), "Reset from test code" and "Fixture reset with a removed or unknown base" (`test-fixtures`), and "Guardrails" (`agent-skill`) modify requirements that `add-pool-format-upgrade` adds or modifies, and start from its text. `openspec validate` reports that archive would refuse them until that change is archived; that is expected and is why this change depends on it.

0.2.2 `image list` and `image export` on an upgraded pool see only the version the upgrade created, while it is kept: after a refresh they list or export an older version than the current one, and a 0.2.2 export can fail if `image prune` removes its version during the export (0.2.2 takes no export lock). This is a documented limitation, not blocked.

F3 archives keep carrying one manifest and its files.

Peak space, workload impact during refresh, how SQL Server queues application-lock requests, and crash-safe publication are **not verified yet**. Spikes 1.1-1.3 settle them as the first task group after approval (Docker is available on the maintainer's Linux machine). Real scheduler runs are spike 1.4; launchd and Task Scheduler examples may ship marked "not verified". See [the dated research note](../../../docs/research/2026-11-19-image-refresh-facts.md).

## Out of scope

Built-in scheduling, automatic branch resets, arbitrary version selection, archive history, incremental builds, automatic pruning, BACPAC replacement (deferred to `add-bacpac-replacement`), performance guarantees, and everything `add-pool-format-upgrade` owns: pool formats, the upgrade, the catalog format, version-ID allocation, the branch base field, `publishWait` and making `init` create format-2 pools.
