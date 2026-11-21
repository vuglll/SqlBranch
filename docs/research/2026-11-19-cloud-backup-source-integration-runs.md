# add-cloud-backup-source integration runs

Date: 2026-11-19. Branch `feat/add-cloud-backup-source` at `36ed7bc`. Host:
Docker Desktop for macOS. Work-container image: `SQLBRANCH_IT_IMAGE=ubuntu:24.04`.
No container was left after either run.

## 6.1 Build

`dotnet build SqlBranch.slnx -c Release`: 0 warnings, 0 errors.

## 6.2 Tests

`dotnet test SqlBranch.slnx -c Release`: `SqlBranch.Core.Tests` 848 passed,
`SqlBranch.Testing.Tests` 10 passed.

Managed loop-xfs pool (clone mode thin),
`SQLBRANCH_IT=1 SQLBRANCH_IT_IMAGE=ubuntu:24.04 dotnet test tests/SqlBranch.IntegrationTests -c Release`:
119 tests, 116 passed, 3 skipped, 0 failed, 45 minutes. Skipped: the deliberate
copy-mode guard (as in earlier runs) and the two opt-in real-cloud tests (task
5.4), which skip without their variables.

Plain pool (clone mode copy), same command with
`SQLBRANCH_IT_POOL_BACKEND=plain`: 119 tests, 95 passed, 24 skipped, 0 failed,
26 minutes. Skipped: thin-only tests and the two opt-in cloud tests.

An earlier gate C attempt on 2026-11-19 was cut off when the laptop's battery
ran out (21 passed, 0 failed before it stopped); its orphaned test engine was
removed by hand.

## 6.3 OpenSpec

`openspec validate --all --strict`: 9 of 9 passed.
