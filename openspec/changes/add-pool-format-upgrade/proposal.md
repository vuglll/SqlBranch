# Proposal

## Why

Image refresh (F4, `add-image-refresh`) keeps several versions of an image and pins each branch to the version it came from. SqlBranch 0.2.2 tools and SqlBranch.Testing 0.2.x packages know nothing about versions: if they kept writing to such a pool they could delete a version a branch still needs or reset a branch to the wrong data. The pool therefore needs a new format that old clients cannot write to, an explicit way to move an existing pool to it, and the existing commands taught to read and write it.

This change is that prerequisite. It targets release 0.3 in `docs/roadmap.md` and is the queue row F4a, `add-pool-format-upgrade`, ahead of F4. It was split out of `add-image-refresh` after the second spec review on 2026-11-24; F4 now lists it under "Needs first".

Prerequisites, checked 2026-11-24 against `docs/roadmap.md` and `openspec/changes/archive/`:

- "Needs first" is "0.2 archived" (SqlBranch.Testing 0.2.x is what this change must keep working, then fence off): met 2026-11-18. There is no spike gate; the platform facts this change relies on are settled by its own first task group (see below).
- The draft is the `add-pool-format-upgrade` section of `docs/feature-drafts.md`, written from the F4 review on 2026-11-24. Its facts were re-checked against the code of release 0.2.2 and NuGet the same day; see [the dated research note](../../../docs/research/2026-11-24-pool-format-upgrade-facts.md).
- The released `sqlbranch` and `SqlBranch.Testing` packages 0.2.0, 0.2.1 and 0.2.2 are on NuGet (https://api.nuget.org/v3-flatcontainer/sqlbranch/index.json and https://api.nuget.org/v3-flatcontainer/sqlbranch.testing/index.json, checked 2026-11-24), so the old-client tests can install 0.2.2. Only 0.2.2 was examined; 0.2.0 and 0.2.1 are assumed to behave the same and are not tested.

## What Changes

- Every pool has a format. Pools written by 0.2.2, and pools `sqlbranch init` creates in this release, use format 1. `status` reports `poolFormat`.
- Add `sqlbranch pool upgrade --yes`, an explicit, one-way move from format 1 to format 2. Without `--yes` it exits 2 and changes nothing. It rewrites the owner-only engine credentials file so that SqlBranch 0.2.x tools stop before they change anything, waits up to `publishWait` for running operations, gives every existing image its first version ID and records the base of every existing branch (pinned, unknown or removed). It prints an irreversibility warning on standard error in text and JSON mode.
- An upgrade that times out, is killed or fills the pool is left unfinished: commands that change images or branches exit 3 `pool_upgrade_required` until `pool upgrade --yes` is run again, and `status` says so and names the command. A rerun keeps IDs already given and removes a leftover temporary credentials file.
- On a format-2 pool the existing commands use a per-name image catalog: every build source (backup file, backup URL, scripts, BACPAC) and plain import publish the name's first version with an ID `<name>@<UTC time to the ms>`, name existence is checked through the catalog, `image rm` removes the name from it, and IDs of a name keep increasing across removal and reuse (a high-water mark kept per name).
- `branch create` records the branch's base version; `branch reset` and fixture reset restore it, and refuse with `image_not_found` when it was removed or is unknown. `branch list`, `branch create`, `branch reset`, `image build`, `image import` and `image list` gain version fields (null on format 1).
- Images and branches that a 0.2.2 command wrote after the upgrade are adopted by the first command that uses them.
- New setting `publishWait` (default 10 minutes) and two new error codes, `catalog_busy` and `pool_upgrade_required`.
- The agent skill tells agents not to run `pool upgrade` unless the user asks.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `engine`: pool format, `pool upgrade`, unfinished upgrades, behavior of 0.2.x clients, error order, status.
- `images`: catalog-based name existence, version IDs on every build and import, `image rm` on format 2, version fields.
- `branches`: the recorded base, reset to it, refusal for a removed or unknown base, version fields.
- `configuration`: the `publishWait` setting.
- `test-fixtures`: fixture reset with a removed or unknown base.
- `agent-skill`: a guardrail for `pool upgrade`.

## Impact

Core pool, image and branch code, CLI metadata, status, on-pool metadata, JSON fields, two new error codes, user docs and the release notes.

**Breaking for older clients, after an opt-in upgrade.** Once a user runs `sqlbranch pool upgrade --yes`, the `sqlbranch` tool 0.2.x and SqlBranch.Testing 0.2.x can no longer use that pool:

- 0.2.2 `init` on the existing pool, `engine start`, `status`, every `image build`, `image import`, `image rm` and every `branch` command exit 1 with `operation_failed` and the message "The engine credentials file in <pool> is not valid." The released code cannot be changed, so the message does not say to upgrade. The docs and the release notes quote it and give the fix: update the tool and the package.
- SqlBranch.Testing 0.2.x throws the same message, and its availability check returns false, so **tests that skip on that check silently stop running** after the upgrade. The release notes warn about this.
- The upgrade cannot be undone. A pool that has not been upgraded keeps working with 0.2.2 and with this release.

**Known limitation, not blocked.** 0.2.2 `image list` and `image export` do not read the credentials file, so they keep running on an upgraded pool. They see only the version the upgrade created while it is kept: once F4 adds newer versions they can list or export an older version than the current one, and they do not list images first built after the upgrade.

**Behavior change on an upgraded pool.** A branch that is older than its image's last rebuild gets an unknown base, and a plain `branch reset` of it exits 1 `image_not_found` instead of resetting to whatever the image now holds. In this change the fix is to remove the branch and create it again; F4 adds `branch reset <name> --latest`.

The credentials-file rename at every kill point and the release of session locks held by killed clients are **not verified yet**; task group 1 settles them first after approval. That 0.2.2 `image list` ignores the new branch-record field is also checked there.

## Out of scope

- Making `sqlbranch init` create format-2 pools (maintainer decision 8; a later release may do it).
- Downgrading a pool, and any automatic or implicit upgrade.
- Everything F4 adds on top of format 2: `--replace`, `image list --versions`, `image prune`, `branch reset --latest`, `image import --replace`, publication waits, the `outdated` marker and the nightly workflow docs.
- Detecting running 0.2.2 processes; the docs tell users to stop them before upgrading.

## Decisions for the maintainer

- In this change alone, the only fix for a branch with an unknown or removed base is `branch rm` plus `branch create`. F4 adds `--latest`. If F4 might ship in a later release than this change, say so, and the release notes will say it too.
