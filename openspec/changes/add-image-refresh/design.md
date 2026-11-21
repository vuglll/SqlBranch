# Design

## Context

ImageBuilder seals files and writes the manifest last. ImageCatalog resolves one directory per name. BranchRecord stores the image name and clone policy. BranchCreator and BranchResetter take a branch lock and a shared image lock. ImageRemover takes the exclusive image lock, renames the directory out of view and then deletes it. Holding today's exclusive build lock throughout a restore would turn an overnight replacement into an outage for new clones.

## Goals / Non-Goals

Publish generations independently of attached SQL databases. Keep old bases until operators can see what depends on them. The scheduler, storage credentials and masking SQL belong to the team. F4 adds no service or external dependency.

## Decisions

1. **Keep plain resets pinned.** Both CLI and fixture resets restore the recorded base. Only `branch reset --latest` moves the branch to the current version. Selecting current implicitly would change 0.1 behavior and make fixture rows depend on refresh timing.

2. **Publish one local catalog update.** Keep the logical image directory, with a small catalog naming the current ID and retained generations. Each entry resolves to one immutable manifest and file set. Build a private candidate using the existing build ID and stale-build tracking. Finish restore, masking, credential/login cleanup, backup cleanup and sealing before publication. Put the complete generation on the same pool, flush it and its metadata, then replace the catalog by same-directory rename and flush the parent. Keep an ownership record until the catalog decision is known. On ambiguous acknowledgement reread under lock: a committed candidate is success, an unpublished one belongs to cleanup. Never undo a committed generation or delete the previous one as compensation. Alternative: swapping data files individually can expose a mixed image. Crash ordering is unverified, spike 1.3.

3. **Separate a writer reservation from catalog access.** A non-waiting reservation per name serializes builds, F3 imports and removal. Readers can still use the prior catalog during staging. Use a short exclusive catalog lock to publish; create/reset take the shared lock from selection through attach and reference persistence. Build and import publishers waiting for active creates or resets report the image and wait on stderr for at most 10 minutes. On timeout release the reservation after cleaning the candidate, leave current unchanged and exit 1 with catalog_busy. Acquire branch lock first if needed, then catalog lock. Prune takes the writer reservation and exclusive catalog lock per name, re-reads references and never waits on branch locks. A held writer makes prune fail safely for that name. Branch removal deletes its reference last. Export takes a shared per-image export lock through the last read. While holding it, export briefly takes the shared catalog lock to select a generation, then releases the catalog lock before streaming. Prune takes that lock exclusively before the catalog lock and holds it through deletion. Publication needs only the catalog lock, so a refresh can finish during export. F3 aborts the destination if removal interrupts a stream. Ordinary branch queries take no catalog lock. Alternative: the existing single exclusive image lock through masking prevents concurrent creates. SQL workload interference and recovery of locks on disconnect need spikes 1.2 and 1.3.

4. **Use local publication identity.** IDs use the logical name plus UTC time with millisecond precision, e.g. `shop@20261119T020000000Z`. Under exclusive access allocate max(clock, last allocated millisecond + 1); persist the high-water mark across prune, removal and name reuse. A gap after failure is harmless. IDs are display-only. Each branch stores its exact base ID. The first local version of an imported archive gets a local ID beside the manifest, preserving F3 manifest equality. Alternative: manifest build time alone collides on repeated imports and clock rollback.

   Switch the pool format during an exclusive maintenance window with all legacy writers drained, before the first catalog mutation. Atomically replace the owner-only engine credential document with a format-2 envelope whose password is nested. The shipped pre-F4 credential reader requires a top-level password string. Its image rm, even with --force, exits 1 with operation_failed before acquiring the image lock or renaming a directory. F4 accepts both formats and resumes migration from the envelope after interruption. A new marker file alone would be ignored by old binaries. Verify the fence with the released client before enabling migration.

   Recognize the old single-manifest directory as generation one in place. Before the first catalog mutation, under the exclusive lock, idempotently bind pre-F4 records using the same legacy image association the old catalog used. Write the mapping before moving current or removing anything; a retry reuses it. Missing legacy images get an explicit removed marker before name reuse. A branch older than a recreated local image, or one whose provenance cannot be resolved, gets an unknown base; protect plausible retained bases from prune and require explicit --latest to adopt. A fresh imported manifest's remote builtAt cannot establish local provenance. Reads support the old layout throughout migration. Alternative: eagerly copying every legacy file doubles migration space.

5. **Retain the latest n per name, plus every pin.** There is no cap on pinned versions. In copy mode, budget one full set of sealed files per retained version, plus branch copies and replacement staging. Prune counts current toward n, scans all branch records including offline, expired and broken branches and treats incomplete records as a failure. Retire each eligible version by atomically excluding it from the catalog before deleting it. Journal the intended deletion, consult the committed catalog on recovery and delete only verified unlisted, owned generations. Stop on first failure and report already retired IDs. A retry of rm with a pending tombstone cleans only that recorded removal and reports the cleaned IDs. If a successor exists, return exit 1 with image_exists, name its current ID and explain that a new image rm removes it under the usual --force rules. A new removal, including forced removal, refuses an unreadable dependency scan. Whole-name removal retains the existing single unlist step, across all versions. Forced removal lists all branches losing a base. Never reattach or edit their databases. Alternative: a fixed cap could destroy a branch's last reset source.

6. **Stage the selected reset base.** Resolve the pinned or explicitly requested latest version once under the shared catalog lock. After forced removal and reuse, require explicit --latest for the new lineage. Stage using the existing clone, free-space, requireThin and ownership checks. Preserve the old reference until attach and atomic branch-record replacement succeed. Use the existing reset workspace to journal old and intended references before drop. On failure before drop remove staged files. After drop detach only a partial owned attachment, remove partial files, retain the old pin and broken record. On interruption reconcile attachment and committed record under the same locks before allowing prune. Name any cleanup failure and leave the owned workspace discoverable by status for retry. Keep explicit-copy, labels and expiry. FixtureHost selects its recorded base, keeping repeated tests reproducible. Dropping before staging would lose edits on a preparation failure.

7. **Compose F1 and F3.** Use a dedicated build pool and a single scheduled writer. Assuming a trusted masking script and the team's storage secrets already injected into the job environment, the producer job body is:

   ```sh
   set -eu
   sqlbranch image build shop --replace --from-bak https://acme.blob.core.windows.net/backups/shop.bak --post-script mask.sql
   sqlbranch image export shop --to https://acme.blob.core.windows.net/images/shop.sbimg --force
   ```

   A failed build skips export. F3 preserves the previous archive on failed export. An empty developer pool can import the archive and create a branch. On an occupied name, ordinary import exits 1 with image_exists. Run `sqlbranch image import shop.sbimg --replace` to stage, verify and publish a new local version under the same lock and deadline as build. Preserve the F3 archive checks, manifest and owned-file cleanup. Old branches can still reset to their pinned rows after success or failure. After successful import, the developer can run `sqlbranch branch reset dev1 --latest` to adopt it. Removing and reimporting would discard existing reset bases.

## JSON output

Keep the schemaVersion 1 envelope, error object, exit codes and existing fields. Text uses the same IDs and marks current, older and removed bases.

| Command | Result |
|---|---|
| image build | Add image.version (string); keep cloneMode. |
| image list | Existing images array, one row per current logical image; branches counts across retained versions. |
| image list --versions | versions array, ordered by name then descending publication time. Each row has name, version, current (boolean) sizeBytes (sum of sealed file lengths) and branches (integer). |
| branch create / reset | Add branch.imageVersion (string); keep cloneMode and connection details already returned. |
| branch list | Add imageVersion (string, null for unknown legacy base) and outdated (true for a retained older base, false for current, null for removed/unknown). Keep imageRemoved and state. |
| image prune | removed lists retired IDs in order, including an unlisted ID whose deletion failed. On exit 1 include error and removed so a script can see partial progress. Empty success returns removed: []. |
| image rm | Existing affected-branch list spans all removed versions. |

Use existing codes: usage_error or invalid_name for malformed refresh input, existing config_error for bad settings, image_not_found for a removed or unknown pinned base (mention --latest) or missing latest target, image_in_use for protected removal, pool_full for insufficient space, catalog_busy for the publication wait timeout, operation_failed for other lock, publication or deletion failure. Preserve specific build-source and engine errors. All refresh paths redact both streams, exceptions and logs. Keep authentication material out of pool metadata and child argv; never expand a secret into shell or scheduler arguments.

## Risks / Trade-offs

Free space already excludes retained files. Reuse separate local staging and restore checks; URL restores skip staging. Script growth is unknown in advance. Report required bytes where known and measure free bytes at failure. Peak space and latency are **not verified (no network in this run)**; spikes 1.1–1.2 establish the operator guidance. Same-pool persistence and scheduler environments are **not verified (no network in this run)**; spikes 1.3–1.4 gate their use. Keep the research note current with measured limits. F1's engine-version-dependent SAS check remains an engine compatibility check once local input checks pass.

Compatibility checks cover the main image immutability, uniqueness, concurrent build, failed build, space and removal rules; branch staging, broken reset, explicit copy and expiry; fixture reset; status mismatches; and schema version 1. In the existing first-build failure scenarios there is no prior image to list. A replacement failure lists only the prior image. F3 still exports exactly one logical-name manifest, refuses an occupied import name without --replace, and cleans up only files owned by its operation. No archive or cloud credential format change is needed.
