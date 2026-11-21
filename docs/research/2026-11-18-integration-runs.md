# add-test-fixtures integration runs

Date: 2026-11-18. Branch `codex/add-test-fixtures` at `5b451d2` plus the
group 5 README commit. Host: Docker Desktop for macOS, 10 CPUs, 7.75 GiB for
the Linux VM. Work-container image: `SQLBRANCH_IT_IMAGE=ubuntu:24.04`. Each run
left no labeled container.

## 6.1 Build and unit tests

`dotnet build SqlBranch.slnx -c Release`: 0 warnings, 0 errors.
`dotnet test SqlBranch.slnx -c Release`: `SqlBranch.Core.Tests` 716 passed,
`SqlBranch.Testing.Tests` 10 passed (the stale-skill and README snippet tests
included). The integration project reports its tests as skipped without
`SQLBRANCH_IT=1`.

## 6.2 Integration suite, managed loop-xfs pool (clone mode thin)

`SQLBRANCH_IT=1 SQLBRANCH_IT_IMAGE=ubuntu:24.04 dotnet test tests/SqlBranch.IntegrationTests -c Release`

105 tests: 104 passed, 1 skipped, 0 failed, 38 minutes. The skip is the
deliberate copy-mode guard `Three_branches_from_a_1_GiB_image_grow_the_pool_by_under_48_MiB_is_skipped_not_passed_in_copy_mode`,
as in the 0.1 runs.

## 6.2 Integration suite, plain pool (clone mode copy)

`SQLBRANCH_IT=1 SQLBRANCH_IT_IMAGE=ubuntu:24.04 SQLBRANCH_IT_POOL_BACKEND=plain dotnet test tests/SqlBranch.IntegrationTests -c Release`

105 tests: 83 passed, 22 skipped, 0 failed, 22 minutes. The skips are the
thin-mode tests, reported as skipped rather than passed on a pool without
reflink.

## 6.3 OpenSpec

`openspec validate add-test-fixtures --strict`: valid.
