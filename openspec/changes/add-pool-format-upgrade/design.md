# Design

## Context

Current code (0.2.2): `ImageBuilder` seals files and writes the manifest last. `ImageCatalog` resolves one directory per name and lists an image once `/pool/images/<name>/manifest.json` exists. `BranchRecord` stores the image name, clone policy, creation time, labels and expiry, but no version. `BranchCreator` and `BranchResetter` take the branch lock and a shared image lock (`sqlbranch:image:<name>`, a session application lock in `master`, non-waiting). `ImageRemover.RemoveAsync` reads the engine credentials, takes the exclusive image lock, renames the directory to `.removing-<name>` and then deletes it.

`EngineCredentials.ReadAsync` reads `/pool/engine/credentials.json` through a short-lived `PoolWork` work container (not the engine), requires a top-level string `password` and raises `operation_failed` with "The engine credentials file in <pool> is not valid." otherwise. `EngineCredentials.WriteAsync` writes the file in place (`cat >`, `chown`, `chmod 600`). The pool marker `/pool/.sqlbranch-pool` is checked only for existence. See the [research note](../../../docs/research/2026-11-24-pool-format-upgrade-facts.md) for which 0.2.2 commands read the credentials.

F4 (`add-image-refresh`) builds on everything here: it adds later versions under the catalog defined below, the catalog lock, publication and prune.

## Goals / Non-Goals

Goals: an explicit, resumable, one-way upgrade; a fence that stops 0.2.x writers; the catalog, version IDs and branch base that F4 needs; every 0.2.2 command and option working on both formats. Non-goals: anything that creates a second version of a name; `init` creating format 2; detecting running old clients.

## Decisions

### D1. Opt-in command with `--yes` (maintainer decisions 1 and 8)

`sqlbranch pool upgrade --yes` is the only way to reach format 2. Without `--yes` it exits 2 `usage_error`, says the upgrade cannot be undone and changes nothing; no command prompts (cli-output rule). `--yes` takes no value; operands and other command options are refused, all before Docker. The global options `--format`, `--config` and `--quiet` are accepted as on every command. System.CommandLine 3.0.0 accepts `--force=true`, `--force true` and `--force --force` for a boolean option (checked 2026-11-24, research note), so refusing `--yes=true`, `--yes true` and `--yes --yes` needs custom parsing of the raw tokens and its own test (task 4.1). `init` keeps creating format-1 pools.

Alternatives: no flag, running the command is the opt-in (an agent or a copied script line can run it unasked); a flag on the first F4 write such as `--upgrade-pool` (an irreversible change hidden in a nightly build line); silent migration (rejected by the maintainer); `init` creating format 2 (a 0.2.x test package on the same machine would break without anyone opting in).

### D2. The fence is the credentials file

0.2.2 ignores the marker's content, so the fence is the credentials file. Format 2 is `{"format":2,"engine":{"user":"sa","password":"..."},"upgrade":{"startedAt":"...","state":"running"|"done"},"note":"Written by SqlBranch 0.3 ..."}`. With no top-level `password`, every 0.2.2 command that reads it exits 1 before taking a lock. The rewrite is a **new write path**: today `WriteAsync` overwrites the file in place. It keeps the stdin transport (the password never in argv) and adds the temporary file: `umask 077`, write `credentials.json.new`, `chown 10001:0`, `chmod 600`, `sync`, `mv -f` over `credentials.json`, sync the directory. If any step before the rename fails (for example the pool is full), the run deletes its own `credentials.json.new`, leaves `credentials.json` unchanged and exits (`pool_full` for no space), so the pool stays format 1. The password is never in argv, environment or logs. A rerun deletes a leftover `credentials.json.new` before writing; `status` lists one as an upgrade leftover (name only, never content). The reader accepts format 1 and 2 and refuses a format above 2 with `operation_failed` and an update hint.

Alternatives: a new marker version (0.2.2 never reads it); a lock file (0.2.2 never checks it). Rename atomicity on directory and loop-xfs pools at every kill point is **not verified**; spike 1.1 settles it.

### D3. Image catalog

Each image directory gains `catalog.json`: `{"format":1,"name":"shop","current":"shop@...","versions":[{"id":"shop@...","dir":"."}]}`. The legacy files at the top of the directory are the first version (`dir` "."); F4 puts later versions in `versions/<id>/`. A catalog is written to a temporary file in the same directory, flushed, renamed over the old one and the directory flushed. On a format-2 pool a name exists when its catalog has a non-null `current`, or when the directory has a 0.2.2 `manifest.json` and no catalog (a legacy image, D7). `image list` shows the current version of each existing name; `image export` reads it. `image rm` takes the existing exclusive image lock, then renames the directory away as today; the catalog goes with it.

Unreadable catalog (maintainer decision D11, 2026-11-24): on a format-2 pool, an image directory whose `catalog.json` exists but cannot be parsed is still an existing name. `image list` shows it with `version` null and `catalogReadable` false (text: "catalog unreadable"); `catalogReadable` is true for every other row on format 2 and null on format 1. `status` lists it as a mismatch (exit 1) naming `sqlbranch image rm <name>`. `image rm` removes it as usual (exclusive image lock, rename away), keeps the name's high-water mark and reports `image.versions` []. Every other command that uses the name (`branch create`, `image export`, a build or import of the same name, the upgrade, adoption) exits 1 `operation_failed` saying the catalog of `<name>` is not valid and naming `sqlbranch image rm <name>`, and changes nothing.

During an unfinished upgrade, `image rm` of an image whose catalog cannot be parsed is allowed (default answer taken for the maintainer, D13, 2026-11-24): it is the fix the upgrade's own error names, and blocking it with `pool_upgrade_required` would leave only `teardown --force`. Every other write still exits `pool_upgrade_required`.

Unreadable credentials (maintainer decision D12, 2026-11-24): when the credentials file cannot be parsed and the pool holds format-2 content (an `/pool/images/.ids` directory or any image `catalog.json`), `image list` and `image export` exit 1 `operation_failed` with "The engine credentials file in <pool> is not valid." instead of falling back to the format-1 view. Without format-2 content they keep the 0.2.2 behavior.

Unreadable branch record (default answer taken for the maintainer, D14, 2026-11-25): the upgrade does not fail on a `branch.json` it cannot read or whose image name is invalid; it leaves that record untouched (no base written), prints one stderr warning naming the branch and `sqlbranch branch rm <name>`, and finishes, so one broken branch cannot trap the pool.

Alternatives: one pool-wide catalog (one file every writer of every name must lock); a symlink per name (0.2.2 readers and `docker cp` handling of links unverified, and import refuses links).

### D3a. Legacy images before adoption

An image an older client wrote after the upgrade (D7) has a `manifest.json` and no catalog. Until a command adopts it, `image list` shows it with `version` null, `image export` exports its top-level files, and `image rm` removes it with `image.versions` [] (no ID was ever given, so no high-water mark changes). Read-only commands (`image list`, `image export`, `branch list`, `branch conn`, `status`) never adopt: they take no exclusive lock and write nothing.

Alternatives: adopt on list (a read would need the exclusive image lock and could fail with `catalog_busy`); hide unadopted images (a user could not see or remove what is in the pool).

### D4. Version IDs and the per-name high-water mark

IDs are `<name>@<UTC yyyyMMddTHHmmssfffZ>`. Under the image lock of the name, the writer allocates max(clock, last allocated + 1 ms) and stores it in `/pool/images/.ids/<name>`, one small file per name, written by temp file and rename. The file is kept when the name is removed, so a reused name keeps increasing. One file per name means writers of different names never contend. IDs are display values, not image names, and gaps after a failed build are harmless. An imported archive gets a local ID; the F3 manifest is not changed, so export and import of one version stay byte-equal.

Alternatives: manifest `builtAt` (an imported archive carries the remote build time, which repeats on re-import and can be ahead of the local clock); a counter (`shop@3` hides when it was built); one pool-wide file (needs a pool-wide lock).

### D5. Branch base

`branch.json` gains `imageVersion` (string or null) and `baseKnown` (boolean). On format 2, `branch create` records the current version's ID; a successful reset records the version it attached. The combinations are:

| `imageVersion` | `baseKnown` | Meaning | `branch list` |
|---|---|---|---|
| ID | true | pinned | `imageVersion` ID; `imageRemoved` true when the catalog no longer lists the ID, also after the name was used again |
| null | true | the image was already removed when the pool was upgraded | `imageVersion` null, `imageRemoved` true, also after the name was used again |
| null | false | unknown base | `imageVersion` null, `imageRemoved` false while the name exists |
| (fields absent) | (absent) | legacy record, not adopted yet (D7) | `imageVersion` null; `imageRemoved` by name, as in 0.2.2 |

A base is "removed" when the name's catalog does not list the ID or the ID is null with `baseKnown` true (computed, not stored), "unknown" when `baseKnown` is false. On format 2, `imageRemoved` is therefore computed from the base version, not from the name. Plain reset and fixture reset restore the recorded base; for a removed or unknown base they refuse before closing any connection (`image_not_found`, naming `branch rm` and `branch create`). SqlBranch.Testing's `FixtureHost.Failure` maps `image_not_found` to the fix `sqlbranch image build <image>` today; the reset path overrides that fix with "create a new branch", because rebuilding the image would not restore the old base. The 0.2.2 reader ignores unknown fields in `branch.json`, which matters for 0.2.2 `image list`: **not verified yet**, task 1.2 checks it.

Alternatives: reset an unknown-base branch to the current version (silently returns different data than the branch was made from); store "removed" (goes stale when the catalog changes).

### D6. Upgrade steps

0. Take the pool-wide upgrade lock `sqlbranch:pool-upgrade`, an exclusive session application lock in `master`, without waiting, and hold it on one open session until the command ends. If it is held, exit 1 `catalog_busy` with "Another `sqlbranch pool upgrade` is running." and change nothing (maintainer decision 7). Then read the format; a finished format 2 ends here (`previousFormat` 2).
1. Record the start time and write the fence with state `running` (D2). Space running out here leaves the pool at format 1 (D2).
2. Enumerate the **existing** image names (a directory under `/pool/images/` with a `manifest.json` or `catalog.json`) and the **existing** branch folders (every non-dot folder under `/pool/branches/`, with or without a `branch.json`, because a create still running makes the folder before it writes the record); step 4 binds only those that have a `branch.json` once locked. The enumeration skips dot-folders (`.removing-*`, `.ids`) and build staging (`/pool/build/<id>`, and an image directory a build is still sealing, which has no manifest yet), as the 0.2.2 `ImageCatalog` glob does. Take the exclusive image lock of every enumerated name and the lock of every enumerated branch. Each lock is tried without waiting, repeatedly, until all are held or `publishWait` (D9) runs out; progress goes to stderr. On timeout exit 1 `catalog_busy` naming **every** image and branch whose lock is still busy, and the rerun command, with the fence in place (maintainer decision D10, 2026-11-24). A create from an existing image holds that image's shared lock, so it is waited for through the image lock. Builds and imports of names that have no image directory, and creates from such images, hold no lock the upgrade knows about, so they are not waited for (maintainer decision 9): they finish in the format-1 layout and D7 adopts them. A 0.3 build or import re-reads the format just before it publishes: on a finished format 2 it publishes through the catalog; on format 1 or during an unfinished upgrade it writes the format-1 layout (manifest last).
3. Write each image's catalog with first ID `<name>@<startedAt>` and its high-water mark.
4. Bind branches (D7), writing each record by temp file and rename.

Steps 3 and 4 process exactly the images and branches locked in step 2. Anything published or created during the wait is left as a legacy item for adoption (D7); if the upgrade is interrupted and rerun, the rerun enumerates and locks again and so takes it in.
5. Rewrite the fence with state `done`.

A rerun takes the upgrade lock, reads `startedAt` and the catalogs and records already written and continues from step 2, so IDs do not change. Running out of space in step 1 leaves format 1 (D2); in a later step it exits 1 `pool_full` with the fence left `running`, and a rerun after space is freed finishes. On a finished format-2 pool the command reports `previousFormat` 2 and writes nothing. The stderr warning about irreversibility and 0.2.x tools is printed in text and JSON mode, before step 1.

While the state is `running`, every command that changes images or branches exits 3 `pool_upgrade_required` at its start or at its post-lock re-read (D8) and says the upgrade did not finish (or is running, when the upgrade lock is held); `image list`, `image export`, `branch list`, `branch conn` and `status` keep working, and so does `teardown --force` (maintainer decision D9, 2026-11-24): it removes everything, including the temporary credentials, catalog, ID and branch-record files and the `.ids` directory, on a format-1 pool, during an unfinished upgrade and on a format-2 pool. In SqlBranch.Testing 0.3 during an unfinished upgrade, fixture create and reset throw with a message naming `sqlbranch pool upgrade --yes`; fixture dispose does not throw, writes a library log line naming that command, and leaves the branch for the TTL prune (maintainer decision D8, 2026-11-24). `status` checks the upgrade lock with `APPLOCK_TEST` and never takes it (maintainer decision D6, 2026-11-24): held means the upgrade is live, shown as in progress with exit 0, like a running build (maintainer decision 8), and a `credentials.json.new` is then part of it; not held means the upgrade died, listed as a mismatch with exit 1 naming `sqlbranch pool upgrade --yes`, and a leftover `credentials.json.new` is listed too. A timed-out upgrade therefore blocks writes until it is rerun (maintainer decision 6). `docs/engine.md` says exactly that and how to finish.

Alternatives for step 0: no upgrade lock (two runs could interleave catalog and record writes and both report success); a lock file in the pool (survives a killed client, so a rerun would need a staleness rule). Alternatives: undo on timeout (the fence has already stopped 0.2.x tools; restoring the old file could race with a 0.2.2 command that read the new one); block new locks with a waiting request instead of polling (would also stall other readers; F4 spike 1.2 checks how SQL Server queues application locks).

### D7. Binding, and items written by older clients after the upgrade

A branch whose image exists and whose `createdAt` is at or after the `builtAt` stored in the image's `manifest.json` is pinned to the first version (maintainer decision 11). Both values come from the client's clock (`ImageBuilder.cs` line 634, `BranchCreator.cs` line 117; research note, 2026-11-24), while the manifest's modification time comes from the engine container's clock, which on macOS and Windows is a VM clock that can drift. An imported image keeps the archive's `builtAt`, the producer's build time, which is at or before the local import (confirmed by the maintainer, decision D1, 2026-11-24). Residual case: a branch created from an earlier image of the same name, which was then removed with `--force` and replaced by an import whose `builtAt` is older than the branch, is pinned to the imported version. That is what its 0.2.2 reset would have restored, and the docs name the case. A branch whose image is gone gets a removed base (its old image name and a null ID, shown with `imageRemoved` true). Any other branch gets an unknown base.

F4 cannot know that every 0.2.2 client has stopped: no registry of running processes exists, and a 0.2.2 command that read the old file just before the fence can still take a lock afterwards. Step 2 waits for every command that already holds a lock on an existing name; a 0.2.2 build, import or create of a new name that slips between its credential read and its lock can still write a legacy image or branch after the upgrade. The same holds for 0.3 builds, imports and creates of names the upgrade did not wait for (D6 step 2). Such items are read as legacy and adopted by the first command that writes with them (create, reset, build, import, rm), never by a read-only command (D3a). Adoption takes the name's image lock exclusively **without waiting**; if it is held, the command exits 1 `catalog_busy` naming the image and changes nothing. An adopted image becomes the first version with an ID allocated by the D4 rule, max(command start time, last allocated ID + 1 ms) from the name's high-water mark, so a name removed and reused before adoption still gets a larger ID than any it had. A legacy branch gets a base by the rule above, against the version whose files are at the top of the image directory. The docs tell users to stop 0.2.x tools and test runs before upgrading.

Alternatives: the manifest's modification time (engine clock, can drift from the client clock that wrote `createdAt`); a bare start time for adopted images (could repeat or go below an ID the name had before removal); bind every branch (a branch older than a rebuilt image would silently reset to the wrong data); refuse legacy items (a user would have to repair the pool by hand).

### D8. Re-read the format after locking, and error order

Every command that changes images or branches reads the format before it locks, takes its locks, and reads it again. A change from format 1 to an upgrade in progress means exit 3 `pool_upgrade_required` with nothing changed; this closes the window between an F4-era command's check and the upgrade's fence. Environment errors are reported in this order: `docker_unavailable`, `not_initialized`, `engine_stopped`, `pool_upgrade_required`. Input and configuration errors (exit 2) come first. The reason for `engine_stopped` before `pool_upgrade_required` is not that the format needs the engine: the credentials (and so the format) are read through a `PoolWork` work container. It is that 0.2.2 already checks the engine state from container information before it reads the credentials (research note), and every format-checked command, `pool upgrade` included, needs a running engine anyway, so starting it is the first fix. F4 follows the same order for its format-2-only options.

A 0.3 create or reset that fails to take an image lock re-reads the pool format before it reports the failure (maintainer decision D7, 2026-11-24). During an unfinished or running upgrade (step 2 holds every existing image's lock exclusively) it exits 3 `pool_upgrade_required`, saying the upgrade is running or did not finish and naming `sqlbranch pool upgrade --yes`, instead of today's "being built or removed" text. On a pool whose format did not change it reports the lock failure as before.

The first read of the format is the existing credentials read (one file gives the password and the format), so it costs nothing extra. The re-read after locking runs `cat` of the file through `docker exec` in the running engine instead of starting another work container, so a create adds one exec, not one container. Whether the engine's user can read the 600 file through `docker exec` is **not verified**; task 4.6 checks it and falls back to a work container, and measures fixture create throughput on both formats.

Alternatives: read once (a create that read format 1 could write a branch record without a base after the fence); a work container for the re-read (one more container start per command, which slows fixture creates).

### D9. `publishWait` setting

One setting, `publishWait` / `SQLBRANCH_PUBLISH_WAIT`, default `10m`, values `1m`-`1440m` or `1h`-`24h`, parsed like `--ttl` without the `d` unit. In this change it bounds the upgrade's wait (D6); F4 uses it for publication and removal. An invalid value fails every command with `config_error` (exit 2) before Docker, as the existing "Invalid configuration is reported" rule does for other settings. Alternatives: a fixed 10 minutes (maintainer asked for a setting); a per-command flag (scheduled jobs set an environment variable more easily); fail only commands that use it (inconsistent with every other setting).

### D10. What 0.2.x users see

With the engine running, 0.2.2 `init` (existing pool), `engine start`, `status`, `image build`, `image import`, `image rm` and all branch commands exit 1 `operation_failed` with "The engine credentials file in <pool> is not valid.". With the engine stopped they check the container state first and report `engine_stopped` (exit 3) without reading the file, as before; only `engine start` and `init` read it and exit 1 (0.2.2 source, research note). The message cannot say "upgrade" because the released code is fixed; the docs and release notes quote it. SqlBranch.Testing 0.2.x throws the same text and its availability check returns false, so tests that skip on it stop running without failing; the release notes warn about this. 0.2.2 `image list` and `image export` do not read the credentials and are not blocked: they see the files at the top of an image directory, which is the first version, while it is kept. Only the 0.2.2 source was examined; task 5.1 runs 0.2.0, 0.2.1 and 0.2.2 of both packages from NuGet (maintainer decision 10).

### D11. What the upgrade touches

The upgrade writes only: the credentials file (and its temporary file), each image's `catalog.json`, the per-name ID files under `/pool/images/.ids/`, and each `branch.json`. It never changes, moves or deletes image data files or manifests, branch data files, attached databases, logins or credentials in SQL Server, and it never stops, restarts or reconfigures the engine container; the sessions it opens only take application locks. Every write is a temporary file plus rename in the same directory. The temporary files have fixed names: `credentials.json.new`, `catalog.json.new` in an image directory, `/pool/images/.ids/<name>.new` (a name cannot contain a dot, so this never collides with an ID file) and `branch.json.new` in a branch folder. A rerun deletes exactly those files and nothing else before it writes; `status` lists any of them left by a dead upgrade as upgrade leftovers (names only), and shows them as part of a running upgrade while the upgrade lock is held. Task 4.5 checks this.

Alternatives: move the first version's files into `versions/<id>/` at upgrade time (touches every image file, costs space in copy mode, and 0.2.2 `image list` and `image export` would lose them); restart the engine to drop old sessions (would cut every open branch connection).

## JSON output

The schemaVersion 1 envelope, error object, exit codes and existing fields stay. New fields only. On a format-1 pool they are null or [] (and `poolFormat` 1); under the existing schemaVersion rules additive fields do not change the version, so this output counts as 0.2.2-compatible (maintainer decision 13).

| Command | Change |
|---|---|
| image build, image import | `image.version` (string; null on format 1). |
| image list | `version` per row (string; null on format 1 and for a legacy image not adopted yet). |
| image rm | `image.versions` (IDs removed; [] on format 1 and for a legacy image not adopted yet). |
| branch create, reset | `branch.imageVersion` (string; null on format 1 or an unknown base). |
| branch list | `imageVersion` (string or null). Existing `imageRemoved` also covers a removed base. |
| status | `poolFormat` (integer); a running upgrade appears as in progress; a dead unfinished upgrade and its leftover temporary credentials file appear among the mismatches. |
| pool upgrade | `pool` {`format`, `previousFormat`}, `images` [{`name`, `version`}], `branches` {`pinned`, `unknownBase`, `removedBase`}. |

## Error codes

Two codes are **new**. Neither is in `src/SqlBranch.Core/Output/ErrorCode.cs` or the error table in `docs/cli-output.md` today; both are added to both, and the test that keeps the two in step must pass:

- `catalog_busy`, exit 1: a command waited `publishWait` for running image or branch operations and gave up, or met an operation it must not wait for. In this change: `pool upgrade` after its wait, a second `pool upgrade` while one is running, and a command that must adopt a legacy image whose lock is held. F4 adds publication, removal and branch operations.
- `pool_upgrade_required`, exit 3: a pool upgrade did not finish, or (from F4 on) the command needs pool format 2. The message names `sqlbranch pool upgrade --yes`.

Existing codes: `usage_error` and `invalid_name` for bad input; `config_error` for a bad `publishWait`; `image_not_found` for a removed or unknown base; `image_exists` for an occupied name; `pool_full`; `engine_stopped` and `not_initialized`; `operation_failed` for a pool format above 2.

## Risks / Trade-offs

- **Older clients break.** After the upgrade, 0.2.x CLIs and SqlBranch.Testing 0.2.x fail with a message that does not mention upgrading, and tests that skip on availability go quiet. Mitigation: `--yes`, the stderr warning, the agent-skill guardrail, docs and release notes quoting the message.
- **A timed-out upgrade blocks writes** until it is rerun. Mitigation: `status` and `docs/engine.md` name the command.
- **Crash safety** of the credentials rename and lock release on client death are **not verified**; spike 1.1 gates them.
- **A 0.2.2 writer slipping past the fence**, or a 0.3 build, import or create of a new name running during the upgrade, leaves a legacy item; D7 adopts it.
- **An imported image's `builtAt` is the producer's time**; a branch of a force-removed earlier image of the same name can be pinned to the import (D7). Documented.

Compatibility checks: the existing unit and integration suites run on a format-1 and a format-2 pool (task 6.1). No archive or cloud credential format changes.
