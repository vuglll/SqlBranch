# Design

## Context

This change builds on `add-pool-format-upgrade`, which must be archived first. From it, a format-2 pool has: a per-name catalog `catalog.json` in each image directory listing the current ID and the retained versions (the first version's files stay at the top of the directory); version IDs `<name>@<UTC ms>` allocated under the name's image lock from a per-name high-water mark kept across removal; `imageVersion` and `baseKnown` in each `branch.json`; the `publishWait` setting; the error codes `catalog_busy` and `pool_upgrade_required`; and the rule that every command that changes images or branches re-reads the pool format after taking its locks.

Current code after that change: `BranchCreator` and `BranchResetter` take the branch lock and a shared image lock (`sqlbranch:image:<name>`, a session application lock in `master`, non-waiting). `ImageRemover` takes the exclusive image lock, renames the directory away, then deletes it. Holding today's exclusive build lock through an overnight restore and masking would block new clones for hours.

## Goals / Non-Goals

Publish image versions without disturbing attached branches. Keep old bases until operators can see what depends on them. Every new option is format-2 only. The scheduler, storage secrets and masking SQL belong to the team. F4 adds no service and no external dependency.

## Decisions

### D1. Plain resets stay pinned

Both CLI and fixture resets restore the recorded base (as `add-pool-format-upgrade` already does). Only `branch reset --latest` moves a branch to the current version. The refusal for a removed or unknown base now names both fixes: `branch reset <name> --latest`, and `branch rm` plus `branch create` (maintainer decision D2, 2026-11-24).

Alternatives: reset to current by default (changes 0.2 behavior and makes fixture data depend on refresh timing); a `--version <id>` option (arbitrary version selection is out of scope).

### D2. One catalog update publishes a version

A replacement builds a private candidate using the existing build ID and stale-build tracking, finishes restore, scripts, credential and login cleanup, backup cleanup and sealing, then moves the sealed files into `versions/<id>/` under the image directory on the same pool, flushes them, writes the new catalog to a temporary file, renames it over the old one and flushes the directory. The ID is allocated at publication from the per-name high-water mark. An ownership record survives until the catalog decision is known. On an ambiguous result the writer re-reads the catalog under lock: if the candidate is listed it succeeded; otherwise its files are leftovers to clean. A committed version is never undone, and the previous version is never deleted to compensate.

Alternatives: swap data files one by one (exposes a mixed image); a symlink switch (0.2.2 readers and `docker cp` handling of links unverified, and links are refused by import). Crash ordering is **not verified**; spike 1.3 settles it.

### D3. Locks

- **Writer reservation** `sqlbranch:image:<name>` (the existing image lock, exclusive, non-waiting) serializes builds, imports, removal, prune and the catalog rewrites of a name. A replacement build or import takes it at its start, before it stages anything, and holds it until publication ends, so it meets a running prune or removal only at its start. Its holder also takes a shared marker lock `sqlbranch:image-holder:<name>:<kind>` (kind `build`, `import`, `prune` or `removal`); a command that cannot get the reservation tests the four markers with `APPLOCK_TEST` to name the holder kind in its message (confirmed by the maintainer, decision D1, 2026-11-24). What happens when the reservation is held:

| Arriving command | Holder | Result |
|---|---|---|
| replacement build or import | build or import | exit 1 `image_exists` at once (overlapping replacements, unchanged) |
| replacement build or import | prune | before staging, poll as below for up to `publishWait`, then exit 1 `catalog_busy` naming the prune (maintainer decision 3) |
| replacement build or import | removal | exit 1 `catalog_busy` at once, naming the removal, before staging (maintainer decision D1) |
| `image rm` | build, import or prune | exit 1 `catalog_busy` at once, naming the holder kind |
| `image rm` | removal | exit 1 `catalog_busy` at once, naming the removal (maintainer decision D5) |
| `image prune` | build, import, removal or another prune of the same name | skip the name, reason `image_busy`, `version` null (maintainer decision D5 for prune meeting prune) |
| create or reset that must adopt a legacy image (`add-pool-format-upgrade` D7) | any | exit 1 `catalog_busy` naming the image, as that change already specifies; creates and resets of adopted images do not take the reservation |
| any other combination, including an unknown holder kind (no marker held any more because the holder just ended, or a create or reset adopting a legacy image, which takes no marker) | | exit 1 `catalog_busy` at once with "another operation" (maintainer decision D4) |

Rationale for a replacement meeting a removal: a replacement meets `rm` only at its start, when it tries the writer reservation, before it has staged or restored anything. Exiting at once is fine because the nightly job is simply rerun; waiting could publish right after an explicit removal and undo it.

- **Catalog lock** `sqlbranch:catalog:<name>`: create and reset take it shared from version selection through attach and writing the branch record. Publication, `image rm` and prune take it exclusively, only for a check and their catalog rename: the exclusive hold covers the reference check (for `image rm` and prune, the scan of branch records; for publication, the re-read of the catalog) **and** the rename, never file deletion. Checking references under the same hold as the rename means a create that finishes during the wait is seen: without `--force`, `image rm` then exits 1 `image_in_use` naming it.
- **Creates and resets meeting an exclusive hold.** A create or reset whose shared request meets one of those brief exclusive holds retries with `@LockTimeout = 0` every 250 ms for up to a fixed 30 seconds, never queueing a waiting request, then exits 1 `catalog_busy` naming the image (maintainer decision 2). The 30 seconds are fixed, not `publishWait`, because the exclusive holds only cover a reference check and one rename; spike 1.2 measures the longest of them, prune's hold with its scan of every branch record, against the 30 seconds.
- **Waiting without blocking readers.** A publisher or `image rm` never issues a waiting exclusive request. It calls `sp_getapplock` with `@LockTimeout = 0` repeatedly (every 250 ms, with stderr progress every 30 s) until it gets the lock or `publishWait` runs out (`catalog_busy`). Because no exclusive request is ever queued, a create or reset that arrives meanwhile gets its shared lock at once and uses the current version. The cost: a steady stream of overlapping creates can keep a publisher out until `catalog_busy`; that is accepted, the nightly job reruns, and `docs/images.md` says so (maintainer decision 6). Whether SQL Server would have queued a new shared request behind a waiting exclusive one is **not verified** (the documentation host was not reachable on 2026-11-24); spike 1.2 checks it, and checks that polling never delays a create.
- **Export lock** `sqlbranch:export:<versionId>`: export takes the catalog lock shared for a moment to select the current version (when that request meets one of the brief exclusive holds, it retries like a create, with `@LockTimeout = 0` every 250 ms for up to 30 seconds, then exits 1 `catalog_busy` naming the image; maintainer decision D3, 2026-11-24), takes the export lock shared for that version **before** it releases the catalog lock, and holds the export lock until the last byte is read, so no prune can unlist the version between selection and reading. A refresh can therefore publish during an export, and the export keeps reading the version it selected. A concurrent `image rm` still fails the export, as in F3 today.
- **Prune, per name:** tries the writer reservation without waiting (held by a build, import, removal or another prune: skip the name, reason `image_busy`, `version` null); tries the catalog lock exclusively without waiting (held by a create or reset: skip the name, reason `catalog_busy`, one entry with `version` null, maintainer decision 5); re-reads references; for each candidate tries its export lock exclusively without waiting (held: skip the version, reason `export_in_progress`); rewrites the catalog and **releases the catalog lock**; deletes the journalled files; then releases the writer reservation. Prune keeps the reservation through deletion, because `image rm` renames the whole image directory and would move a version directory prune is deleting, and the D4 journal names directories inside it by path. A replacement build that starts while the prune runs therefore waits before staging (above), and creates and resets are not held up by the deletion. Prune never waits and exits 0 for skips.
- Lock order is always branch, writer, catalog, export. Branch removal deletes its record last, so a reference is visible until the branch is gone.
- After taking its locks every writer re-reads the pool format (rule from `add-pool-format-upgrade`). A finished format-2 pool never returns to an unfinished state, so for the options added here the re-read can only confirm format 2.

Alternatives: keep the single exclusive image lock through masking (blocks creates for hours); release the writer reservation before prune deletes files (a concurrent `image rm` would rename the directory under the deletion); create and reset waiting `publishWait` on an exclusive hold (a test fixture would stall for minutes behind a rename that takes milliseconds) or failing at once (today's non-waiting behavior, which would fail creates during every publication); a blocking exclusive request for publication (may queue new creates behind it; maintainer decision 4 says creates must not be blocked); make prune wait (a nightly prune overlapping a long build or export would stall; maintainer decisions 3 and 5 prefer skip and exit 0); make `image rm` skip instead of wait (removal is an explicit, one-name request; maintainer decision 5 prefers wait then `catalog_busy`). SQL workload interference and release of session locks on client death are **not verified**; spikes 1.2 and 1.3 settle them.

### D4. Retention

Keep the newest n per name, including current, plus every referenced version, with no cap. A branch with an unknown base, or a base removed before the upgrade, references no version (maintainer decision 4): a plain reset of it is refused anyway, and `--latest` uses the current version, so keeping a guess at its base would only cost space. The docs say that the fix for such a branch is `branch reset <name> --latest` or `branch rm` plus `branch create`. In copy mode each retained version is a full set of files; budget it together with branch copies and replacement staging. Prune scans every branch record (online, offline, broken, expired) and fails closed for all names on an unreadable record, because a record names its image only inside the file. Each eligible version is unlisted by a catalog rewrite before its files are deleted; the intended deletion is journalled with the version's directory, and deletion and recovery touch only directories the journal names under `versions/`. For a version whose catalog entry has `dir` "." (the first version, at the top of the image directory), prune, its journal and its retry delete only the files named by the top-level `manifest.json`, then that manifest; they never touch `catalog.json` or its temporary file, the `versions/` directory, the `/pool/images/.ids` directory, or any other entry of the image directory. Branch folders, other names, kept versions and build or reset workspaces are never in a journal. Prune stops at the first unlisting failure; deletion failures are reported per version and retried by the next run.

Alternatives: a fixed cap on pinned versions (could destroy a branch's last reset source); automatic prune after publication (out of scope); keep the upgrade's first version while an unknown-base branch exists (the earlier draft; no reset can use it, so it only costs space).

### D5. Removal across versions

On format 2, `image rm` takes the writer reservation (exit 1 `catalog_busy` naming the holder when a replacement or prune holds it, D3), waits for the catalog lock as in D3, and under that exclusive hold checks branches across all versions (fail closed on an unreadable record) and unlists the whole name in one rename, as today. Its journal lists every version; a retry cleans only recorded leftovers, and when a newer image took the name the retry exits 1 `image_exists` naming the successor. On format 1 the 0.2.2 behavior is unchanged.

Alternatives: remove versions one by one (a partial failure would leave an image with an arbitrary current version).

### D6. Reset staging

Resolve the pinned version, or the current one with `--latest`, once under the shared catalog lock. Stage with the existing clone, free-space, requireThin and ownership checks. Journal the old and new base in the reset workspace before the drop. Before the drop, a failure removes staged files. After the drop, detach only an attachment the reset owns, remove partial files, keep the old base and mark the branch broken. On interruption, the next reset or prune reconciles the attachment and record under the same locks. Alternative: drop before staging (loses edits when preparation fails).

### D7. Nightly composition (F1 and F3)

Use a dedicated build pool and one scheduled writer. With the masking script trusted and storage secrets injected by the scheduler:

```sh
set -eu
sqlbranch image build shop --replace --from-bak https://acme.blob.core.windows.net/backups/shop.bak --post-script mask.sql
sqlbranch image export shop --to https://acme.blob.core.windows.net/images/shop.sbimg --force
```

A failed build skips export, and F3 keeps the previous archive when an export fails. A developer on an empty pool runs `image import shop.sbimg`; on a pool that has shop, `image import shop.sbimg --replace`, which stages, verifies and publishes under the same locks and wait as a build, keeping F3's checks and owned-file cleanup. `branch reset dev1 --latest` then adopts it. Alternative: remove and re-import (discards every reset base).

Cron and scheduled-CI examples are run in spike 1.4. launchd and Task Scheduler examples ship marked "not verified" until someone runs them.

## JSON output

The schemaVersion 1 envelope, error object, exit codes and existing fields stay. New fields only (the version fields of build, import, list, create, reset and rm come from `add-pool-format-upgrade`):

| Command | Change |
|---|---|
| image list | Unchanged shape; on format 2, `branches` summed across versions. |
| image list --versions | `versions`: `name`, `version`, `current`, `sizeBytes`, `branches`. |
| image prune | `removed` (IDs), `skipped` (`image`, `version`, `reason`: `export_in_progress` with the version ID; `image_busy` and `catalog_busy` once per name with `version` null); both kept on exit 1 next to `error`. |
| branch list | `outdated` (true, false or null). |

## Error codes

No new codes. Used here: `pool_upgrade_required` (exit 3) for the write options (`--replace`, `image prune`, `--latest`) on a format-1 pool or during an unfinished upgrade, and for `image list --versions` on a format-1 pool, after exit-2 checks, `docker_unavailable`, `not_initialized` and `engine_stopped`; `image list --versions` is a read and keeps working during an unfinished upgrade, showing images not yet catalogued with `version` null; `catalog_busy` (exit 1) when publication or `image rm` waited `publishWait`, when a create, reset or export met an exclusive hold for 30 seconds, when a replacement waited `publishWait` for a prune, when a replacement met a removal, when `image rm` met a replacement, prune or removal, and when any of them met an unknown holder kind (D3); `usage_error` and `invalid_name` for bad input; `image_not_found` for a removed or unknown base (with the `--latest` hint) or a missing latest target; `image_exists` for an occupied name without `--replace`, a replacement meeting another replacement (build or import) of the name, and an rm retry that finds a successor; `image_in_use` for protected removal; `pool_full`; `operation_failed` for unreadable branch records and unlisting or deletion failures. Specific build-source and engine errors are kept.

## Risks / Trade-offs

- **0.2.2 list and export on a format-2 pool** can show or export an older version. Documented, not blocked.
- **Publisher starvation** under a continuous stream of creates ends in `catalog_busy`; accepted, the nightly job reruns (maintainer decision 6). Spike 1.2 measures it.
- **Create or reset failing with `catalog_busy`** if an exclusive catalog hold lasts over 30 seconds (for example a rename on a stalled pool). The retry needs no queueing, so it cannot starve the holder.
- **Space.** Retained versions count as used space. Peak space and latency are **not verified**; spikes 1.1 and 1.2 set the operator guidance.
- **Crash safety.** Same-pool rename and flush ordering for publication and prune are **not verified**; spike 1.3 gates them.
- **Schedulers.** Cron and scheduled CI are verified in spike 1.4; launchd and Task Scheduler ship marked "not verified".
- F1's engine-version-dependent SAS check stays an engine check after local input checks pass.

Compatibility checks cover image immutability, uniqueness, concurrent builds, failed builds, space and removal rules; branch staging, broken reset, explicit copy and expiry; fixture reset; status; and schemaVersion 1, on format-1 and format-2 pools (task 8.2). No archive or cloud credential format changes.
