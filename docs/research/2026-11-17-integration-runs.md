# Integration runs for tasks 9.1, 9.3, 9.4 and 9.6

Host: macOS 26.6.2 (arm64), Docker Desktop (Docker 29.9.3, aarch64), the engine under emulation.
Date: 2026-11-17. Work-container image: `SQLBRANCH_IT_IMAGE=ubuntu:24.04`. Each run left no labeled container,
volume or `it-*` folder in the VM.

## 9.1 Build and unit tests

`dotnet build -warnaserror` in Release and in Debug: 0 warnings, 0 errors.
`dotnet test` (Debug and Release): `SqlBranch.Core.Tests` 654 passed, 0 failed. The integration project reports
its 86 tests as skipped without `SQLBRANCH_IT=1`, as designed.

## 9.4 Full suite on Docker Desktop defaults (clone mode thin)

`SQLBRANCH_IT=1 SQLBRANCH_IT_IMAGE=ubuntu:24.04 dotnet test tests/SqlBranch.IntegrationTests -c Release`

88 tests: 87 passed, 1 skipped, 0 failed, 23.3 minutes.

The one skip is `BranchCopyModeIntegrationTests.Three_branches_from_a_1_GiB_image_grow_the_pool_by_under_48_MiB_is_skipped_not_passed_in_copy_mode`.
It belongs to the copy-mode fixture (a plain folder pool, which is always copy) and exists to prove that a thin-size
test is reported as skipped, not passed, when the pool is copy. It is deliberate and unrelated to the thin pool of this
host. Every thin-mode test ran and passed, including the 100 GB pool test.

## 9.3 Full suite on a pool without reflink

`SQLBRANCH_IT=1 SQLBRANCH_IT_IMAGE=ubuntu:24.04 SQLBRANCH_IT_POOL_BACKEND=plain dotnet test tests/SqlBranch.IntegrationTests -c Release`

`SQLBRANCH_IT_POOL_BACKEND=plain` (new, see `tests/SqlBranch.IntegrationTests/README.md`) makes every test world use
`poolBackend: plain` on a client folder. 88 tests: 67 passed, 21 skipped, 0 failed, 10.6 minutes.

Skipped because the pool is not thin (12): `BranchCreateIntegrationTests`: `Image_rm_..._the_thin_branch_still_answers`,
`A_row_written_in_one_thin_branch_...`, `Branch_list_shows_state_..._in_a_thin_pool`, `Branch_conn_prints_..._thin_branch_...`,
`Branch_rm_leaves_a_user_database_alone_..._thin_branch_...`, `Branch_rm_and_reset_work_on_a_thin_branch_whose_database_is_offline`,
`Branch_reset_brings_a_dropped_table_back_in_a_thin_branch_...`, `Three_branches_from_a_1_GiB_image_grow_the_pool_by_under_48_MiB`,
`Branch_files_after_a_million_inserts_..._in_a_thin_pool`, `A_copy_branch_of_a_1_GiB_image_on_a_thin_pool_...`,
`A_copy_branch_on_a_thin_pool_is_still_a_copy_after_a_reset`; and `BranchCopyModeIntegrationTests.Three_branches_..._is_skipped_not_passed_in_copy_mode`.

Skipped because they are about the managed XFS pool (9): `ManagedPoolIntegrationTests.A_managed_pool_reports_thin_...`,
`LargePoolIntegrationTests.A_pool_size_of_100GB_...`, `TeardownIntegrationTests` (2 managed-pool tests),
`TeardownWithImagesIntegrationTests.Teardown_force_does_the_same_after_the_pool_was_unmounted`, and in `EngineInitIntegrationTests`
`Init_sets_the_8_KiB_...`, `The_first_init_names_...`, `Doctor_on_the_initialized_machine_...`, `Engine_start_remounts_an_unmounted_managed_pool_...`.
12 + 9 = 21.

## 9.6

`openspec validate add-branching-core --strict`: "Change 'add-branching-core' is valid".
