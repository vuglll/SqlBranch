# add-image-export-import integration runs

Date: 2026-11-19. Branch `feat/add-image-export-import`. Host: Docker Desktop
for macOS. Work-container image: `SQLBRANCH_IT_IMAGE=ubuntu:24.04`. No
container was left after any run.

## 6.1 Build

`dotnet build SqlBranch.slnx -c Release`: 0 warnings, 0 errors.

## 6.2 Unit tests

`dotnet test SqlBranch.slnx -c Release`: `SqlBranch.Core.Tests` 1081 passed,
`SqlBranch.Testing.Tests` 10 passed.

## 6.3 Integration suite

Full suite at `015eab1` (all product code of the change):

- Managed loop-xfs pool (clone mode thin): 136 passed, 1 failed, 5 skipped.
  The one failure, `ManagedPoolIntegrationTests.A_managed_pool_reports_thin_and_leaves_no_helper_container`,
  saw a `sqlbranch-work-*` helper container from another feature's
  integration run that was running at the same time on this host. Rerun
  alone with Docker otherwise idle, it passed (1 of 1).
- Plain pool (clone mode copy, `SQLBRANCH_IT_POOL_BACKEND=plain`): 116
  passed, 0 failed, 26 skipped.

Skipped tests are the opt-in real-cloud tests (task 5.4), the deliberate
copy-mode guard, and thin-only tests on the plain pool.

Commit `a1cc2e1` after the full run changed tests only. Its four integration
classes (file and cloud archives, secret leak, opt-in cloud) were rerun on
both pools: 15 passed, 2 skipped (opt-in), 0 failed each.

Lesson: two features' Docker integration suites must not run at the same
time on one host; the helper-container check of one sees the other.

## 6.5 OpenSpec

`openspec validate add-image-export-import --strict`: valid; `openspec
validate --all --strict`: 9 of 9.
