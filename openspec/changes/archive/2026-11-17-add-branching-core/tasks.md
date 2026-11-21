# Tasks

Specs say what to build, `design.md` says how. Tests that need Docker are
tagged `Integration` and run only when `SQLBRANCH_IT=1`. A task is verified
only with commands built in its own group or earlier ones; checks that need
images and branches together are in group 7.

## 1. Repository scaffold

- [x] 1.1 Create the solution with `src/SqlBranch.Core`, `src/SqlBranch.Cli`, `tests/SqlBranch.Core.Tests` and `tests/SqlBranch.IntegrationTests` on .NET 10 with nullable and warnings-as-errors, and verify `dotnet build` finishes with zero warnings
- [x] 1.2 Add Apache-2.0 LICENSE, README stub, .editorconfig and .gitignore, and verify they are present at the repository root
- [x] 1.3 Add a GitHub Actions workflow that builds and runs unit tests on pull requests to dev and main, and verify it passes on a test pull request
- [x] 1.4 Package `SqlBranch.Cli` as a dotnet tool with the command name `sqlbranch`, and verify `dotnet tool install` from the local package and `sqlbranch --version` work

## 2. Output, exit codes and redaction (cli-output, configuration)

- [x] 2.1 Implement the command result model, the text and JSON renderers and the exit-code mapping, and verify with unit tests that the JSON envelope has `schemaVersion`, `command`, `ok`, `elapsedMs` and, on failure, `error.code` and `error.message`
- [x] 2.2 Route results to standard output and all diagnostics to standard error, and verify with a unit test that a warning does not break JSON on standard output
- [x] 2.3 Add global options `--format`, `--quiet` and `--config`, make unknown options exit with code 2, and verify with CLI-level tests, including one that runs every command with standard input closed and asserts none waits for input
- [x] 2.4 Implement the secret redactor that every error message, `docker` command line and engine log excerpt passes through, and verify with unit tests that a registered password is replaced by `***` in each
- [x] 2.5 Document the JSON envelope, error codes and exit codes in `docs/cli-output.md`, and verify every error code used in the code appears in the document with a test that compares the two

## 3. Configuration (configuration)

- [x] 3.1 Implement settings with defaults and the precedence flag, environment, repository file, user file, default, and verify each precedence pair with unit tests
- [x] 3.2 Implement `.sqlbranch.yml` discovery from the current directory upward and `--config`, and verify with tests run from a nested folder
- [x] 3.3 Reject unknown keys, wrong types, unsupported `version` and any `password` key with exit code 2 naming the file and key, and verify with unit tests
- [x] 3.4 Implement `engineEdition` with its three allowed values, the SQL Server 2025 minimum read from year-prefixed tags, and the rejection of production editions, and verify each rule with unit tests
- [x] 3.5 Parse `poolSize` (default 60 GB) and reject non-size values such as `big` with exit code 2, and verify with unit tests
- [x] 3.6 Check a given `SQLBRANCH_SA_PASSWORD` against SQL Server's password policy and exit 2 before any action, and verify with unit tests for `abc` and a valid password
- [x] 3.7 Document every setting, its environment variable and default in `docs/configuration.md`, and verify the documented list matches the settings model with a test

## 4. Container runtime, pool and engine (engine)

- [x] 4.1 Define `IContainerRuntime` and implement it by running the `docker` command, labeling everything it creates `io.sqlbranch.managed=true` and refusing to act on unlabeled containers, with a fake for tests; verify the command lines it builds with unit tests
- [x] 4.2 Implement the clone-mode probe in a short-lived labeled container with the pool mounted, and verify with unit tests on the fake and an integration test that reports the real mode of a directory
- [x] 4.3 Implement the pool marker and the directory pool: write the marker on init, refuse a non-empty directory without it (exit 2, nothing changed), and fix ownership only on an empty or marked directory; verify with integration tests for `notes.txt` and for `images/photo.png`, checking files and owners are unchanged
- [x] 4.4 Implement the `loop-xfs` pool: host space check against `poolSize` (exit 3 `host_disk_full`), print the pool file, size and mount point, then format and mount `/var/lib/sqlbranch/pool.xfs` from labeled privileged helpers that are removed after each step; verify the space check and the printed lines with unit tests on the fake, and with an integration test on a host without reflink that the probe reports `thin` and no helper container remains
- [x] 4.5 Implement `poolBackend` selection (`auto`, `loop-xfs`, `plain`), the fallback to copy mode under `auto` after removing what the managed-pool attempt created, the `sqlbranch-pool` volume on Docker Desktop, exit 3 under `loop-xfs`, and the refusal of a changed `poolSize`; verify each path with unit tests on the fake runtime, including a refused privileged container and a failed mount
- [x] 4.6 Implement engine creation in `init`: a random bootstrap password in the container, `ALTER LOGIN` to the real password after first start, credentials file with owner-only access, port on 127.0.0.1:14330 only, and a 120 s readiness wait that exits 3 `engine_not_ready`; verify with an integration test that a connection succeeds and `docker port` lists only `127.0.0.1:14330`
- [x] 4.7 Undo a failed first `init` in reverse order (helpers, engine, mount, pool file, credentials) with exit 1 `init_failed`, and verify with unit tests on the fake that inject a failure after each step and an integration test that fails the engine start after the pool is mounted
- [x] 4.8 Make `init` safe to repeat on an empty pool, refuse a foreign container with the engine's name (exit 3), record the backend and pool in engine labels, and refuse a changed `engineImage`, `engineEdition`, `port`, `pool` or `SQLBRANCH_SA_PASSWORD` (exit 2); verify with unit tests on the fake and integration tests for the repeat and the foreign container
- [x] 4.9 Verify with integration tests that the password is absent from the output of `init` (text and JSON) and from `docker inspect sqlbranch-engine` after `init` and after a container restart, and that a login with the real password still works after the restart
- [x] 4.10 Implement the pool lookup of design D2a, `engine start` and `engine stop` (stop is a no-op when stopped), exit 3 `not_initialized` for every command except `init` and `doctor` when nothing is found, remount a missing managed pool before starting, and exit 3 `pool_not_mounted` when the remount fails; verify with unit tests on the fake, including image and branch commands before `init`
- [x] 4.11 Implement `status` with pool space, counts, the edition, mismatches and leftover builds, resets and image removals, exiting 1 when it lists any, treating a running build as in progress and a user database outside the branches area as no mismatch; verify with unit tests on the fake for each kind of leftover, a held build lock and a `scratch` database, and with an integration test that the password is absent from its text and JSON output
- [x] 4.12 Implement `doctor` with pass, warn and fail checks (space warns under 10 GiB and fails under 1 GiB), exit 0 when only warnings occur, including the host architecture and copy-mode warnings, and verify each check with unit tests on the fake and with an integration test that the password is absent from its output
- [x] 4.13 Print the non-production notice at `init` and verify it with a test
- [x] 4.14 Pass the edition to the engine at first start and check the version of a tag without a year after start (remove the engine and exit 2 below 2025); verify with unit tests on the fake and an integration test that reads the edition back from the engine
- [x] 4.15 Confirm the product ID values for Standard Developer and Enterprise Developer against Microsoft's edition table, record the source and date in `docs/research/`, and verify with an integration test on a SQL Server 2025 image that each edition starts
- [x] 4.16 Set an 8 KiB copy-on-write extent hint on the branches directory at `init` and verify with an integration test that reads the hint back
- [x] 4.17 Implement `teardown --force` in the steps of design D15, with pool work containers when the engine is gone (exit 2 without `--force`, exit 3 `not_initialized` when nothing is found, skip what is already gone, exit 1 naming what is left); verify with unit tests on the fake that inject a failure after each step and then rerun, including a failed unmount, and integration tests on an empty `plain` pool with `readme.txt`, on an empty managed pool, and on a managed pool after it was unmounted
- [x] 4.18 Document `init`, `status`, `doctor`, `engine start|stop` and `teardown` in `docs/engine.md`, including what `init` creates and the fallback, and verify the documented commands run as written in an integration test on an empty pool

## 5. Images (images)

- [x] 5.1 Implement name validation shared by images and branches, applied before any SQL or file action, and verify valid and invalid names, including `../branches/pr-142` and the reserved `sqlbranch_` prefix, with unit tests
- [x] 5.2 Check the `--from-bak` file on the client (exit 2 when missing or unreadable) and map a file the engine cannot read as a backup to exit 1 `not_a_backup`; verify with unit tests and an integration test using a text file
- [x] 5.3 Implement backup inspection (header and file list), backup-set selection and the checks for backup type (`unsupported_backup_type`), version, FILESTREAM and memory-optimized filegroups, and verify each rejection with unit tests on recorded inspection results
- [x] 5.4 Check free pool space for the backup file before staging and for the restored files after the file list is read, exiting 1 `pool_full` with both numbers and deleting the staged copy, and verify both checks with unit tests on the fake
- [x] 5.5 Implement the build pipeline as ordered steps: stage the backup in `/pool/build/<id>/`, restore under `sqlbranch_build_<id>`, set SIMPLE recovery, checkpoint, detach, rebuild the log, seal files read-only, write the manifest, and delete the staging folder, reporting the clone mode; verify with an integration test that builds an image from a backup created during the test, finds no staged copy afterwards, and reads `cloneMode` from the JSON output
- [x] 5.6 Take the application lock for the image name and refuse an existing name with exit 1; verify with an integration test that two simultaneous builds of one name give exit codes 0 and 1 and that a rebuild of an existing name leaves it unchanged
- [x] 5.7 Implement `--keep-recovery-model`, record the original and sealed recovery models in the manifest, and verify both scenarios with an integration test that builds from a FULL-recovery database
- [x] 5.8 Implement cleanup on failure at every step, and verify with an integration test that injects a failure after restore and finds no image, no staging folder and `status` exiting 0
- [x] 5.9 Verify with an integration test that a build whose process is killed during restore is listed by `status` (exit 1) with its staging folder, and its temporary database when one is left, and that `status` exits 0 while a build is running
- [x] 5.10 Use a backup already under the pool's import folder without copying it and keep it after the build, and verify with an integration test that no second copy appears
- [x] 5.11 Implement `image list` with size, build time and branch count, and verify with an integration test
- [x] 5.12 Implement `image rm`: exit 1 `image_not_found` for an unknown name, rename to the removing folder before deleting, exit 1 naming a leftover path; verify with an integration test for an unused image and a unit test on the fake for a failed delete that `status` then reports
- [x] 5.13 Implement the in-use check of `image rm` (exit 1 listing the branches without `--force`) and removal with `--force`; verify with an integration test in each clone mode that a branch query still succeeds after a forced removal
- [x] 5.14 Verify the log rebuild with an integration test: build from a database with a 256 MB log, assert the image's log is under 8 MiB and the manifest records the original size
- [x] 5.15 Document image building, limits and the manifest in `docs/images.md`, and verify the documented commands run as written in an integration test

## 6. Branches (branches)

- [x] 6.1 Implement `branch create`: refuse a name used by any database, clone files in the engine container, attach, write `branch.json`, and return `server`, `database` and `user` without the password; verify with an integration test that reads the image's rows through a connection built from the returned `server`, `database` and `user` and the password in the credentials file, and that `branch create scratch` exits 1 when a user database `scratch` exists
- [x] 6.2 Undo a failed create (drop an attached database, delete the folder), and verify with unit tests on the fake that fail the attach and the copy, and an integration test that fails the attach and finds `status` exiting 0
- [x] 6.3 Take the application lock for the branch name and verify with an integration test that two simultaneous creates of one name give exit codes 0 and 1 and no mismatches
- [x] 6.4 Verify thin-mode space use with an integration test that creates three branches from a 1 GiB image and asserts growth under 48 MiB; report it as skipped when the mode is copy
- [x] 6.5 Implement the free-space check for copy creates and resets with exit 1 `pool_full`, and verify it with unit tests on the fake (the check is `PoolFreeSpace`/`BranchGuards.RequireSpaceForCopyAsync`, used by create now; reset calls it in 6.11)
- [x] 6.6 Implement `requireThin` for `branch create` and `branch reset` (exit 3 `thin_required`), exempting `--copy` branches in both clone modes, and verify with unit tests on the fake (the guard is `BranchGuards.RequireThin`, wired into create now with the `--copy` flag parsed; reset calls it in 6.11)
- [x] 6.7 Verify isolation with an integration test: a row inserted in one branch is absent from a sibling, from a later branch, and the image file hashes are unchanged
- [x] 6.8 Implement `branch list` with the states `online`, `offline` and `broken`, own changed-data size and a removed-image marker, and verify with an integration test that includes a forced image removal
- [x] 6.9 Implement `branch conn` with `--quiet`, the password in text and JSON, and exit 1 `branch_not_found`, and verify the printed string opens the branch database and reads the image's rows
- [x] 6.10 Guard `rm` and `reset`: name rules first, then a `branch.json` must exist; verify with an integration test that `branch rm scratch` on a user-made database exits 1 and the database survives, and that `branch rm ../images/shop` exits 2
- [x] 6.11 Implement `branch reset` in the checked order (image exists, space, clone to `next/`, drop, move, attach) with `broken` on an attach failure; verify with an integration test that a dropped table returns and the old string still works, with unit tests on the fake for the missing image, no space and failed attach, and that a second reset recovers a broken branch
- [x] 6.12 Implement `branch rm` with open connections closed first and exit 1 naming a folder it could not delete; verify with an integration test that holds a connection open and a unit test on the fake for the failed delete
- [x] 6.13 Implement `defaultImage` for `branch create` and verify both scenarios with tests
- [x] 6.14 Run DBCC CHECKDB on a written branch in an integration test, including one from the rebuilt-log image of 5.14 after one million inserted rows, and verify it reports no errors
- [x] 6.15 Implement `branch create --copy`, store the mode in `branch.json` and keep it on reset; verify on a thin pool with an integration test that the branch reports `copy`, the pool grows by at least 900 MiB for a 1 GiB image, and the mode survives a reset
- [x] 6.16 Document the branch commands in `docs/branches.md`, including a benchmark setup section (copy branch, quiet engine, matching edition and recovery model), and verify the documented commands run as written in an integration test

## 7. Engine checks with images and branches (engine, configuration)

- [x] 7.1 Verify with an integration test that `init` run again on a pool with one image and two branches exits 0 and keeps them
- [x] 7.2 Verify with an integration test that the password is absent from the text and JSON output of every command except `branch conn`, including list output, warnings and a forced error
- [x] 7.3 Verify with an integration test that a row written to a branch survives `engine stop` and `engine start`, and, on a managed pool, an unmount followed by `engine start`
- [x] 7.4 Verify with an integration test that `status` exits 1 and names `pr-7` after the branch database is dropped behind the tool's back
- [x] 7.5 Verify with integration tests that `teardown --force` on a managed pool with an image, a branch and a user database `scratch` exits 0, lists `scratch`, and leaves no labeled container and no pool file, and that it does the same with the engine stopped and after the pool was unmounted
- [x] 7.6 Verify with an integration test on Docker Desktop that `poolSize: 100GB` gives a pool total between 99 GB and 101 GB

## 8. Bench and README

- [x] 8.1 Add `tools/bench/clone-bench.sh` that times five branch creations against a named image and prints the times and pool growth, and verify it runs against a small image

## 9. Integration checks

- [x] 9.1 Run `dotnet build` with warnings as errors and all unit tests, and verify both are clean
- [x] 9.3 Run the integration suite with `poolBackend: plain` on a pool without reflink and verify it is green with thin-mode tests reported as skipped
- [x] 9.4 Run the integration suite on Docker Desktop for macOS with default settings and verify it is green with clone mode `thin`
- [x] 9.5 Run `tools/bench/clone-bench.sh` against a 50 GB image on Docker Desktop for macOS with default settings and verify every creation takes under 10 seconds
- [x] 9.6 Run `openspec validate add-branching-core --strict` and verify it passes

## Release gate (not part of this change's completion)

These run before release 0.1 (the merge of `dev` into `main`), not before
this change is merged into `dev` or archived:

- Spike `spike-thin-clone` sections 4 and 7 are done.
- `tools/bench/clone-bench.sh` against a 50 GB image on native x86-64 Linux XFS (the setup of spike task 4.2) shows every creation under 10 seconds. These are the official G1 numbers; record them in `docs/research/spike-results.md`.
- If G1 or G5 fails on x86-64, hold the release and revise `docs/roadmap.md`.

Moved here from groups 8 and 9 when the change was archived (2026-11-17),
because each needs a machine the maintainer did not have at hand. The README
itself was written in group 8; only its check on a clean Linux machine is open.

- Former 8.2: Write the README: what it is, the four concepts, a five-command quick start, thin versus copy mode, `teardown`, limits, the G2 first-write cost (or, while the spike's G2 run is open, the dry-run warning from design D14 and the `--copy` advice), and the non-production notice; verify the quick start runs as written on a clean Linux machine
- Former 8.3: Run `init`, an image build and a branch create on Docker Desktop for Windows and record the result in `docs/research/`; if no Windows machine is available, record it as not verified and verify the README says so
- Former 9.2: Run the integration suite with `SQLBRANCH_IT=1` on a Linux host with an XFS reflink pool and verify it is green with no skipped thin-mode tests

## Workflow follow-up

- Record the 9.5 numbers in `docs/research/spike-results.md`.
- Publish 0.1 to NuGet by merging dev into main once the release gate is met.
- Update the release table in `docs/roadmap.md`.
