# Group 2 review handoff

Review the Group 2 commit against its parent, `65a0781`, on `codex/add-test-fixtures`. The parent contains Group 1. Group 2 adds ordered SQL sources, post-build scripts on either source, per-script timeouts, and script provenance in image manifests. Older manifests default to a backup source with empty script lists. The acceptance criteria are in [the Group 2 checklist](tasks.md#2-image-sources-and-post-build-scripts-images) and [the image spec](specs/images/spec.md).

## Where to focus

- **Ownership transfer.** Follow the build through `ImageBuilder.cs`, `ImageBuilder.Scripts.cs` and `DatabaseTriggerCatalog.cs`. For a backup with post-scripts, an enabled database trigger handling the ownership change must fail with `ddl_trigger` before login creation, ownership transfer or any script. Event-group registrations count. Disabled matching handlers and unrelated enabled handlers permit the build. Backup-only builds preserve the original handlers.
- **Sealing.** Scripts run on a separate connection as a temporary login. Check the order of disabling the login and Broker, ending sessions, validating the trigger snapshot, database state and file locations, detaching, dropping the login, reattaching and restoring ownership to `sa`. The unit tests exercise statement ordering and inject failures at each build step.
- **Cleanup and recovery.** Review the outside-file path separately from ordinary cleanup. SqlBranch detaches the database and reports outside files so their owner can recover them. For interrupted builds, check the build lock before reclaiming a leftover login. Review the killed-build and bounded copy-pool tests alongside `StatusReporter.cs`.
- **Guest preflight.** An unreadable eligible catalog or a guest-enabled database blocks script execution. The scan skips databases outside ONLINE and those in SINGLE_USER. It runs once. A database made accessible after that scan is outside its coverage.

## What the tests establish

The focused Docker checks cover ownership triggers from backups, script-created triggers and file placement, guest preflight, and Broker delivery in both thin and copy modes. The Broker test first observes a marker written by an active queue. In a branch from the sealed image, Broker is disabled and a second message stays in the transmission queue throughout the two-second observation window. The marker count stays at one.

The exact timing of activation already in flight at the freeze and cross-database access with the guest guard bypassed remain unmeasured. The [dated research findings](../../../docs/research/2026-11-17-test-fixtures-facts.md) record the observed SQL Server behavior.

For verification, run `dotnet build`, `dotnet test tests/SqlBranch.Core.Tests` and `openspec validate add-test-fixtures --strict`. Run the Docker checks with `SQLBRANCH_IT=1` and each of `SQLBRANCH_IT_POOL_BACKEND=loop-xfs` and `SQLBRANCH_IT_POOL_BACKEND=plain`. The four focused test name filters are `Broker_delivers_a_marker`, `Backup_ownership_trigger_guard`, `Script_state_and_file_placement` and `Guest_branch_blocks`.
