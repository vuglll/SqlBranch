# Design

## Context

Current code (0.2.2): `ImageBuilder` seals files and writes the manifest last. `ImageCatalog` resolves one directory per name and lists an image once `/pool/images/<name>/manifest.json` exists. `BranchRecord` stores the image name, clone policy, creation time, labels and expiry, but no version. `BranchCreator` and `BranchResetter` take the branch lock and a shared image lock (`sqlbranch:image:<name>`, a session application lock in `master`, non-waiting). `ImageRemover.RemoveAsync` reads the engine credentials, takes the exclusive image lock, renames the directory to `.removing-<name>` and then deletes it.

`EngineCredentials.ReadAsync` requires a top-level string `password` in `/pool/engine/credentials.json` and raises `operation_failed` with "The engine credentials file in <pool> is not valid." otherwise. The pool marker `/pool/.sqlbranch-pool` is checked only for existence. See the [research note](../../../docs/research/2026-11-24-pool-format-upgrade-facts.md) for which 0.2.2 commands read the credentials.

F4 (`add-image-refresh`) builds on everything here: it adds later versions under the catalog defined below, the catalog lock, publication and prune.

## Goals / Non-Goals

Goals: an explicit, resumable, one-way upgrade; a fence that stops 0.2.x writers; the catalog, version IDs and branch base that F4 needs; every 0.2.2 command and option working on both formats. Non-goals: anything that creates a second version of a name; `init` creating format 2; detecting running old clients.

## Decisions

### D1. Opt-in command with `--yes` (maintainer decisions 1 and 8)

`sqlbranch pool upgrade --yes` is the only way to reach format 2. Without `--yes` it exits 2 `usage_error`, says the upgrade cannot be undone and changes nothing; no command prompts (cli-output rule). `--yes` takes no value; operands and other options are refused, all before Docker. `init` keeps creating format-1 pools.

Alternatives: no flag, running the command is the opt-in (an agent or a copied script line can run it unasked); a flag on the first F4 write such as `--upgrade-pool` (an irreversible change hidden in a nightly build line); silent migration (rejected by the maintainer); `init` creating format 2 (a 0.2.x test package on the same machine would break without anyone opting in).

### D2. The fence is the credentials file

0.2.2 ignores the marker's content, so the fence is the credentials file. Format 2 is `{"format":2,"engine":{"user":"sa","password":"..."},"upgrade":{"startedAt":"...","state":"running"|"done"},"note":"Written by SqlBranch 0.3 ..."}`. With no top-level `password`, every 0.2.2 command that reads it exits 1 before taking a lock. The rewrite uses the existing stdin path of `EngineCredentials.WriteAsync`: `umask 077`, write `credentials.json.new`, `chown 10001:0`, `chmod 600`, `sync`, `mv -f` over `credentials.json`, sync the directory. The password is never in argv, environment or logs. A rerun deletes a leftover `credentials.json.new` before writing; `status` lists one as an upgrade leftover (name only, never content). The reader accepts format 1 and 2 and refuses a format above 2 with `operation_failed` and an update hint.

Alternatives: a new marker version (0.2.2 never reads it); a lock file (0.2.2 never checks it). Rename atomicity on directory and loop-xfs pools at every kill point is **not verified**; spike 1.1 settles it.

### D3. Image catalog

Each image directory gains `catalog.json`: `{"format":1,"name":"shop","current":"shop@...","versions":[{"id":"shop@...","dir":"."}]}`. The legacy files at the top of the directory are the first version (`dir` "."); F4 puts later versions in `versions/<id>/`. A catalog is written to a temporary file in the same directory, flushed, renamed over the old one and the directory flushed. On a format-2 pool a name exists when its catalog has a non-null `current`, or when the directory has a 0.2.2 `manifest.json` and no catalog (a legacy image, D7). `image list` shows the current version of each existing name; `image export` reads it. `image rm` takes the existing exclusive image lock, then renames the directory away as today; the catalog goes with it.

Alternatives: one pool-wide catalog (one file every writer of every name must lock); a symlink per name (0.2.2 readers and `docker cp` handling of links unverified, and import refuses links).

### D4. Version IDs and the per-name high-water mark

IDs are `<name>@<UTC yyyyMMddTHHmmssfffZ>`. Under the image lock of the name, the writer allocates max(clock, last allocated + 1 ms) and stores it in `/pool/images/.ids/<name>`, one small file per name, written by temp file and rename. The file is kept when the name is removed, so a reused name keeps increasing. One file per name means writers of different names never contend. IDs are display values, not image names, and gaps after a failed build are harmless. An imported archive gets a local ID; the F3 manifest is not changed, so export and import of one version stay byte-equal.

Alternatives: manifest `builtAt` (an imported archive carries the remote build time, which repeats on re-import and can be ahead of the local clock); a counter (`shop@3` hides when it was built); one pool-wide file (needs a pool-wide lock).

### D5. Branch base

`branch.json` gains `imageVersion` (string or null) and `baseKnown` (boolean). On format 2, `branch create` records the current version's ID; a successful reset records the version it attached. A base is "removed" when the name's catalog does not list the ID (computed, not stored), "unknown" when `baseKnown` is false. Plain reset and fixture reset restore the recorded base; for a removed or unknown base they refuse before closing any connection (`image_not_found`, naming `branch rm` and `branch create`). The 0.2.2 reader ignores unknown fields in `branch.json`, which matters for 0.2.2 `image list`: **not verified yet**, task 1.2 checks it.

Alternatives: reset an unknown-base branch to the current version (silently returns different data than the branch was made from); store "removed" (goes stale when the catalog changes).

### D6. Upgrade steps

1. Record the start time and write the fence with state `running` (D2).
2. Take the exclusive image lock of every image and the lock of every branch. Each lock is tried without waiting, repeatedly, until all are held or `publishWait` (D9) runs out; progress goes to stderr. On timeout exit 1 `catalog_busy` naming the busy item and the rerun command, with the fence in place.
3. Write each image's catalog with first ID `<name>@<startedAt>` and its high-water mark.
4. Bind branches (D7), writing each record by temp file and rename.
5. Rewrite the fence with state `done`.

A rerun reads `startedAt` and the catalogs and records already written and continues from step 2, so IDs do not change. Running out of space in any step exits 1 `pool_full` with the fence left `running`; a rerun after space is freed finishes. On a finished format-2 pool the command reports `previousFormat` 2 and writes nothing. The stderr warning about irreversibility and 0.2.x tools is printed in text and JSON mode, before step 1.

While the state is `running`, every command that changes images or branches exits 3 `pool_upgrade_required` and says the upgrade did not finish; `image list`, `image export`, `branch list`, `branch conn` and `status` keep working; `status` exits 1 and names `sqlbranch pool upgrade --yes`. A timed-out upgrade therefore blocks writes until it is rerun (maintainer decision 6). `docs/engine.md` says exactly that and how to finish.

Alternatives: undo on timeout (the fence has already stopped 0.2.x tools; restoring the old file could race with a 0.2.2 command that read the new one); block new locks with a waiting request instead of polling (would also stall other readers; F4 spike 1.2 checks how SQL Server queues application locks).

### D7. Binding, and items written by older clients after the upgrade

A branch whose image exists and whose `createdAt` is at or after the modification time of the image's `manifest.json` (written locally at seal, also for imports) is pinned to the first version. A branch whose image is gone gets a removed base (its old image name and a null ID, shown with `imageRemoved` true). Any other branch gets an unknown base.

F4 cannot know that every 0.2.2 client has stopped: no registry of running processes exists, and a 0.2.2 command that read the old file just before the fence can still take a lock afterwards. Step 2 waits for every command that already holds a lock on an existing name; a 0.2.2 build, import or create of a new name that slips between its credential read and its lock can still write a legacy image or branch after the upgrade. Such items are read as legacy and adopted by the first command that uses them, under the image lock: an image becomes a first version with that command's start time; a branch gets a base by the rule above. The docs tell users to stop 0.2.x tools and test runs before upgrading.

Alternatives: trust `builtAt` (remote time for imports); bind every branch (a branch older than a rebuilt image would silently reset to the wrong data); refuse legacy items (a user would have to repair the pool by hand).

### D8. Re-read the format after locking, and error order

Every command that changes images or branches reads the format before it locks, takes its locks, and reads it again. A change from format 1 to an upgrade in progress means exit 3 `pool_upgrade_required` with nothing changed; this closes the window between an F4-era command's check and the upgrade's fence. Environment errors are reported in this order: `not_initialized`, `engine_stopped` (the credentials are read through the running engine container), `pool_upgrade_required`. Input and configuration errors (exit 2) come first. F4 follows the same order for its format-2-only options.

Alternatives: read once (a create that read format 1 could write a branch record without a base after the fence).

### D9. `publishWait` setting

One setting, `publishWait` / `SQLBRANCH_PUBLISH_WAIT`, default `10m`, values `1m`-`1440m` or `1h`-`24h`, parsed like `--ttl` without the `d` unit. In this change it bounds the upgrade's wait (D6); F4 uses it for publication and removal. An invalid value fails every command with `config_error` (exit 2) before Docker, as the existing "Invalid configuration is reported" rule does for other settings. Alternatives: a fixed 10 minutes (maintainer asked for a setting); a per-command flag (scheduled jobs set an environment variable more easily); fail only commands that use it (inconsistent with every other setting).

### D10. What 0.2.x users see

0.2.2 `init` (existing pool), `engine start`, `status`, `image build`, `image import`, `image rm` and all branch commands exit 1 `operation_failed` with "The engine credentials file in <pool> is not valid.". The message cannot say "upgrade" because the released code is fixed; the docs and release notes quote it. SqlBranch.Testing 0.2.x throws the same text and its availability check returns false, so tests that skip on it stop running without failing; the release notes warn about this. 0.2.2 `image list` and `image export` do not read the credentials and are not blocked: they see the files at the top of an image directory, which is the first version, while it is kept. Only 0.2.2 was examined.

## JSON output

The schemaVersion 1 envelope, error object, exit codes and existing fields stay. New fields only:

| Command | Change |
|---|---|
| image build, image import | `image.version` (string; null on format 1). |
| image list | `version` per row (string; null on format 1). |
| image rm | `image.versions` (IDs removed; [] on format 1). |
| branch create, reset | `branch.imageVersion` (string; null on format 1 or an unknown base). |
| branch list | `imageVersion` (string or null). Existing `imageRemoved` also covers a removed base. |
| status | `poolFormat` (integer); an unfinished upgrade and a leftover temporary credentials file appear among the mismatches. |
| pool upgrade | `pool` {`format`, `previousFormat`}, `images` [{`name`, `version`}], `branches` {`pinned`, `unknownBase`, `removedBase`}. |

## Error codes

Two codes are **new**. Neither is in `src/SqlBranch.Core/Output/ErrorCode.cs` or the error table in `docs/cli-output.md` today; both are added to both, and the test that keeps the two in step must pass:

- `catalog_busy`, exit 1: a command waited `publishWait` for running image or branch operations and gave up. In this change only `pool upgrade` returns it; F4 adds publication and removal.
- `pool_upgrade_required`, exit 3: a pool upgrade did not finish, or (from F4 on) the command needs pool format 2. The message names `sqlbranch pool upgrade --yes`.

Existing codes: `usage_error` and `invalid_name` for bad input; `config_error` for a bad `publishWait`; `image_not_found` for a removed or unknown base; `image_exists` for an occupied name; `pool_full`; `engine_stopped` and `not_initialized`; `operation_failed` for a pool format above 2.

## Risks / Trade-offs

- **Older clients break.** After the upgrade, 0.2.x CLIs and SqlBranch.Testing 0.2.x fail with a message that does not mention upgrading, and tests that skip on availability go quiet. Mitigation: `--yes`, the stderr warning, the agent-skill guardrail, docs and release notes quoting the message.
- **A timed-out upgrade blocks writes** until it is rerun. Mitigation: `status` and `docs/engine.md` name the command.
- **Crash safety** of the credentials rename and lock release on client death are **not verified**; spike 1.1 gates them.
- **A 0.2.2 writer slipping past the fence** leaves a legacy item; D7 adopts it.

Compatibility checks: the existing unit and integration suites run on a format-1 and a format-2 pool (task 6.1). No archive or cloud credential format changes.
