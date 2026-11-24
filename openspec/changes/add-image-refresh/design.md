# Design

## Context

Current code (0.2.2): `ImageBuilder` seals files and writes the manifest last. `ImageCatalog` resolves one directory per name and lists an image once `/pool/images/<name>/manifest.json` exists. `BranchRecord` stores the image name, clone policy, creation time, labels and expiry, but no version. `BranchCreator` and `BranchResetter` take the branch lock and a shared image lock (`sqlbranch:image:<name>`, a session application lock in `master`, non-waiting). `ImageRemover.RemoveAsync` reads the engine credentials, takes the exclusive image lock, renames the directory to `.removing-<name>` and then deletes it. Holding today's exclusive build lock through an overnight restore and masking would block new clones for hours.

`EngineCredentials.ReadAsync` requires a top-level string `password` in `/pool/engine/credentials.json` and raises `operation_failed` with "The engine credentials file in <pool> is not valid." otherwise. The pool marker `/pool/.sqlbranch-pool` is checked only for existence. See the [research note](../../../docs/research/2026-11-19-image-refresh-facts.md) for which 0.2.2 commands read the credentials.

## Goals / Non-Goals

Publish image versions without disturbing attached branches. Keep old bases until operators can see what depends on them. Make the pool-format break explicit and opt-in. The scheduler, storage secrets and masking SQL belong to the team. F4 adds no service and no external dependency.

## Decisions

### D1. Plain resets stay pinned

Both CLI and fixture resets restore the recorded base. Only `branch reset --latest` moves a branch to the current version.

Alternatives: reset to current by default (changes 0.2 behavior and makes fixture data depend on refresh timing); a `--version <id>` option (arbitrary version selection is out of scope).

### D2. One catalog update publishes a version

Each image directory keeps its legacy files in place as the first version and gains a small catalog file naming the current ID and the retained versions; later versions live in `versions/<id>/` under it. A replacement builds a private candidate using the existing build ID and stale-build tracking, finishes restore, scripts, credential and login cleanup, backup cleanup and sealing, then moves the sealed files into `versions/<id>/` on the same pool, flushes them, writes the new catalog to a temporary file, renames it over the old one and flushes the directory. An ownership record survives until the catalog decision is known. On an ambiguous result the writer re-reads the catalog under lock: if the candidate is listed it succeeded; otherwise its files are leftovers to clean. A committed version is never undone, and the previous version is never deleted to compensate.

Alternatives: swap data files one by one (exposes a mixed image); a symlink switch (0.2.2 readers and `docker cp` handling of links unverified, and links are refused by import). Crash ordering is **not verified**; spike 1.3 settles it.

### D3. Locks

- Writer reservation `sqlbranch:image:<name>` (the existing image lock, exclusive, non-waiting) serializes builds, imports and removal of a name. A second replacement that cannot take it exits 1 with `image_exists` and says another build or import is running (maintainer decision 5; this matches the code a duplicate name already gets).
- Catalog lock `sqlbranch:catalog:<name>`: create and reset take it shared from version selection through attach and writing the branch record. Publication takes it exclusively for the rename only. Publishers wait for it up to `publishWait` (D9), print progress on stderr, and on timeout clean the candidate, release the reservation and exit 1 with `catalog_busy`.
- Export lock `sqlbranch:export:<versionId>`: export takes it shared for the version it selected, from selection through the last byte read. Selection takes the catalog lock shared for a moment only, so a refresh can publish during an export.
- Prune, per name: tries the writer reservation without waiting (busy: skip the name, reason `image_busy`), takes the catalog lock exclusively, re-reads references, and for each candidate tries its export lock exclusively without waiting (held: skip the version, reason `export_in_progress`). It never waits on branch locks. Lock order is always branch, writer, catalog, export.
- Branch removal deletes its record last, so a reference is visible until the branch is gone.

Alternatives: keep the single exclusive image lock through masking (blocks creates for hours); make prune wait for export (a long export stalls a scheduled prune; maintainer decision 4 prefers skip and exit 0); make prune fail for a busy name (a nightly prune overlapping a long build would fail every night). SQL workload interference and release of session locks on client death are **not verified**; spikes 1.2 and 1.3 settle them.

### D4. Version IDs

IDs are `<name>@<UTC yyyyMMddTHHmmssfffZ>`. Under the writer reservation the writer allocates max(clock, last allocated + 1 ms) and stores the high-water mark in the image directory, kept across prune, and in a pool-wide file kept across removal and name reuse. IDs are display values, not image names, and gaps after a failed publish are harmless. Each branch stores its exact base ID. An imported archive gets a local ID; the F3 manifest is not changed, so export and import of one version stay byte-equal.

Alternatives: manifest `builtAt` (an imported archive carries the remote build time, which repeats on re-import and can be ahead of the local clock); a counter (`shop@3` hides when it was built).

### D5. Opt-in pool upgrade (maintainer decisions 1, 2, 10)

**Command.** `sqlbranch pool upgrade` is the only way to reach format 2. `sqlbranch init` keeps creating format-1 pools in this release. F4-only options on a format-1 pool exit 3 with `pool_upgrade_required` and name the command. Alternatives: a flag on the first F4 write such as `--upgrade-pool` (an irreversible change hidden in a nightly build line, and every F4 command would need the flag); silent migration (rejected by the maintainer); new pools in format 2 from `init` (a 0.2.x test package on the same machine would break without anyone opting in; see open question in proposal.md).

**Fence.** 0.2.2 ignores the marker's version, so the fence is the credentials file. Format 2 is `{"format":2,"engine":{"user":"sa","password":"..."},"upgrade":{"startedAt":"...","state":"running"|"done"},"note":"Written by SqlBranch 0.3 ..."}`. With no top-level `password`, every 0.2.2 command that reads it exits 1 before taking a lock. The rewrite uses the existing stdin path of `EngineCredentials.WriteAsync`: `umask 077`, write `credentials.json.new`, `chown 10001:0`, `chmod 600`, `sync`, `mv -f` over `credentials.json`, sync the directory. The password is never in argv, environment or logs. Rename atomicity on directory and loop-xfs pools is **not verified**; spike 1.3 covers it. F4's reader accepts format 1 and 2, and refuses a format above 2 with `operation_failed` and an update hint.

**Steps.** (1) Record the start time and write the fence with state `running`. (2) Take the writer reservation and catalog lock of every image and the lock of every branch, waiting up to `publishWait`; on timeout exit 1 `catalog_busy` with the fence in place. (3) Write each image's catalog with first ID `<name>@<startedAt>`. (4) Bind branches (below), writing each record by temp file and rename. (5) Rewrite the fence with state `done`. A rerun reads `startedAt` and the catalogs already written and continues from step 2. While state is `running`, F4 refuses writes with `pool_upgrade_required` and `status` reports the unfinished upgrade.

**Binding.** A branch whose image exists and whose `createdAt` is at or after the modification time of the image's `manifest.json` (written locally at seal, also for imports) is pinned to the first version. A branch whose image is gone gets a removed base. Any other branch gets an unknown base: `imageVersion` null, plain reset refuses, and prune keeps the name's first version while such a branch exists. Alternatives: trust `builtAt` (remote time for imports); bind every branch (a branch older than a rebuilt image would silently reset to the wrong data).

**Older clients still running.** F4 cannot know that every 0.2.2 client has stopped: no registry of running processes exists, and a 0.2.2 command that read the old file just before the fence can still take a lock afterwards. What follows: the fence stops every 0.2.2 command that starts later; step 2 waits for every command that already holds a lock on an existing name; a 0.2.2 build, import or create of a new name that slips between its credential read and its lock can still write a legacy image or branch after the upgrade. F4 reads such items as legacy and binds them on first use under the writer reservation (images become a first version with the binding-time ID; branches follow the binding rule). The docs tell users to stop 0.2.x tools and test runs before upgrading.

**What 0.2.x users see.** 0.2.2 `init` (existing pool), `engine start`, `status`, `image build`, `image import`, `image rm` and all branch commands exit 1 `operation_failed` with "The engine credentials file in <pool> is not valid.". The message cannot say "upgrade" because the released code is fixed; the docs and release notes quote it. SqlBranch.Testing 0.2.x throws the same text and its availability check returns false, so tests that skip on it are skipped silently. 0.2.2 `image list` and `image export` do not read the credentials and are not blocked: they see only the files at the top of an image directory, which is the first version the upgrade created, while it is retained. So 0.2.2 can list or export an older version than current, does not list images first built after the upgrade, and an export can fail if prune removes that version during it (0.2.2 export takes no export lock). This is a documented known limitation.

### D6. Retention

Keep the newest n per name, including current, plus every referenced version, with no cap. In copy mode each retained version is a full set of files; budget it together with branch copies and replacement staging. Prune scans every branch record (online, offline, broken, expired) and fails closed for all names on an unreadable record, because a record names its image only inside the file. Each eligible version is unlisted by a catalog rewrite before its files are deleted; the intended deletion is journalled, and recovery deletes only unlisted versions owned by the journal. Prune stops at the first unlisting failure; deletion failures are reported per version and retried by the next run. `image rm` unlists the whole name in one step, as today, and its journal lets a retry clean only the recorded versions; when a newer image took the name, the retry exits 1 `image_exists` (maintainer decision 3).

Alternatives: a fixed cap on pinned versions (could destroy a branch's last reset source); automatic prune after publication (out of scope).

### D7. Reset staging

Resolve the pinned version, or the current one with `--latest`, once under the shared catalog lock. Stage with the existing clone, free-space, requireThin and ownership checks. Journal the old and new base in the reset workspace before the drop. Before the drop, a failure removes staged files. After the drop, detach only an attachment the reset owns, remove partial files, keep the old base and mark the branch broken. On interruption, the next reset or prune reconciles the attachment and record under the same locks. `FixtureHost` resets to the recorded base; with a removed or unknown base it throws a message that names the branch, the image and `sqlbranch branch reset <branch> --latest`. Alternative: drop before staging (loses edits when preparation fails).

### D8. Nightly composition (F1 and F3)

Use a dedicated build pool and one scheduled writer. With the masking script trusted and storage secrets injected by the scheduler:

```sh
set -eu
sqlbranch image build shop --replace --from-bak https://acme.blob.core.windows.net/backups/shop.bak --post-script mask.sql
sqlbranch image export shop --to https://acme.blob.core.windows.net/images/shop.sbimg --force
```

A failed build skips export, and F3 keeps the previous archive when an export fails. A developer on an empty pool runs `image import shop.sbimg`; on a pool that has shop, `image import shop.sbimg --replace`, which stages, verifies and publishes under the same locks and wait as a build, keeping F3's checks and owned-file cleanup. `branch reset dev1 --latest` then adopts it. Alternative: remove and re-import (discards every reset base).

Cron and scheduled-CI examples are run in spike 1.4. launchd and Task Scheduler examples ship marked "not verified" until someone runs them (maintainer decision 6).

### D9. `publishWait` setting

One setting, `publishWait` / `SQLBRANCH_PUBLISH_WAIT`, default `10m`, values `1m`-`1440m` or `1h`-`24h`, parsed like `--ttl` without the `d` unit. It bounds the publication wait (D3) and the upgrade drain (D5). Invalid values are `config_error` (exit 2) before Docker. Alternatives: a fixed 10 minutes (maintainer decision 8 asks for a setting); a per-command flag (scheduled jobs set an environment variable more easily, and a flag can be added later).

## JSON output

The schemaVersion 1 envelope, error object, exit codes and existing fields stay. New fields only:

| Command | Change |
|---|---|
| image build, image import | `image.version` (string; null on format 1). |
| image list | Unchanged shape; one row per name, `branches` summed across versions. |
| image list --versions | `versions`: `name`, `version`, `current`, `sizeBytes`, `branches`. |
| image prune | `removed` (IDs), `skipped` (`image`, `version`, `reason`); both kept on exit 1 next to `error`. |
| image rm | `image.versions` (IDs removed or cleaned; [] on format 1). |
| branch create, reset | `branch.imageVersion` (string; null on format 1). |
| branch list | `imageVersion`, `outdated` (true, false or null). |
| status | `poolFormat` (integer). |
| pool upgrade | `pool` {`format`, `previousFormat`}, `images` [{`name`, `version`}], `branches` {`pinned`, `unknownBase`, `removedBase`}. |

## Error codes

Two codes are **new**. Neither is in `src/SqlBranch.Core/Output/ErrorCode.cs` or the error table in `docs/cli-output.md` today; both must be added to both, and the test that keeps the two in step must pass:

- `catalog_busy`, exit 1: a replacement build or import, or `pool upgrade`, waited `publishWait` for running operations on a name and gave up. Nothing was published.
- `pool_upgrade_required`, exit 3: the command needs pool format 2, or a pool upgrade did not finish. The message names `sqlbranch pool upgrade` and that SqlBranch 0.2.x tools cannot use an upgraded pool.

Existing codes: `usage_error` and `invalid_name` for bad input; `config_error` for a bad `publishWait`; `image_not_found` for a removed or unknown base (with the `--latest` hint) or a missing latest target; `image_exists` for an occupied name without `--replace`, a lost replacement race, and an rm retry that finds a successor; `image_in_use` for protected removal; `pool_full`; `engine_stopped` and `not_initialized`; `operation_failed` for unreadable branch records, unlisting or deletion failures, and a pool format above 2. Specific build-source and engine errors are kept.

## Risks / Trade-offs

- **Older clients break.** After `pool upgrade`, 0.2.x CLIs and SqlBranch.Testing 0.2.x packages fail with a message that does not mention upgrading. Mitigation: opt-in command, stderr warning, docs and release notes quoting the message.
- **0.2.2 list and export on a format-2 pool** can show or export an older version. Documented, not blocked.
- **Space.** Retained versions are excluded from free space. Peak space and latency are **not verified**; spikes 1.1 and 1.2 set the operator guidance.
- **Crash safety.** Same-pool rename and flush ordering, and session-lock release on client death, are **not verified**; spike 1.3 gates them.
- **Schedulers.** Cron and scheduled CI are verified in spike 1.4; launchd and Task Scheduler ship marked "not verified".
- F1's engine-version-dependent SAS check stays an engine check after local input checks pass.

Compatibility checks cover image immutability, uniqueness, concurrent builds, failed builds, space and removal rules; branch staging, broken reset, explicit copy and expiry; fixture reset; status; and schemaVersion 1. F3 still exports exactly one manifest per archive, refuses an occupied name without `--replace`, and cleans up only files its operation owns. No archive or cloud credential format changes.
