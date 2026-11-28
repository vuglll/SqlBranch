# Integration tests

These tests use the real Docker. They run only when `SQLBRANCH_IT=1`; otherwise every one is reported as skipped.
Each test makes its own engine, port, pool folder and pool volume, and removes them at the end. They never touch
`sqlbranch-engine`, port 14330, `/var/lib/sqlbranch/pool.xfs` or the `sqlbranch-pool` volume.

| Variable | Meaning |
| --- | --- |
| `SQLBRANCH_IT` | `1` runs the tests. Anything else skips them. |
| `SQLBRANCH_IT_IMAGE` | The small image the work and probe containers use (default `mcr.microsoft.com/mssql/server:2022-latest`). `ubuntu:24.04` is enough and avoids a second large image. |
| `SQLBRANCH_IT_MINIO_IMAGE` | Replaces the pinned MinIO image of the cloud-source tests (`cgr.dev/chainguard/minio` by digest, which pulls anonymously) if the pinned one can no longer be pulled. |
| `SQLBRANCH_IT_AZURITE_IMAGE` | Replaces the pinned Azurite image of the Blob tests. |
| `SQLBRANCH_IT_AZURE_EXPORT_URL` (with `SQLBRANCH_AZURE_SAS_TOKEN`), `SQLBRANCH_IT_S3_EXPORT_URL` (with `SQLBRANCH_IT_S3_REGION` and the S3 keys) | Opt-in: a real location (an object that does not exist yet; the test never uses `--force`) for the export and import round trip of `ImageArchiveCloudOptInIntegrationTests`. |
| `SQLBRANCH_IT_AZURE_URL`, `SQLBRANCH_AZURE_SAS_TOKEN` | Opt-in: a real Azure Blob backup URL and its SAS token. Only the cloud tests read them. |
| `SQLBRANCH_IT_S3_URL`, `SQLBRANCH_IT_S3_REGION`, `SQLBRANCH_S3_ACCESS_KEY_ID`, `SQLBRANCH_S3_SECRET_ACCESS_KEY` | Opt-in: a real S3 backup URL, its region and keys. |
| `SQLBRANCH_IT_POOL_FORMAT` | `1` (default) keeps today's format-1 initialization; `2` runs `pool upgrade --yes` immediately after each test pool is initialized, before seeding or tests, and requires exit 0 plus `status --format json` reporting `poolFormat: 2`. Other values fail setup. Run both formats with each pool backend. Upgrade-transition walks skip under `2`; dedicated legacy pools explicitly keep format 1. |
| `SQLBRANCH_IT_POOL_BACKEND` | `plain` makes every test engine use a plain folder of the client (`poolBackend: plain`), a pool without reflink. Tests that check thin mode, or that are about the managed XFS pool, are then reported as skipped. Unset runs the default managed `loop-xfs` pool. |
| `SQLBRANCH_IT_SQLPACKAGE` | Absolute path to a user-installed `sqlpackage` for the doctor and restricted BACPAC import checks. |

```sh
SQLBRANCH_IT=1 SQLBRANCH_IT_IMAGE=ubuntu:24.04 dotnet test tests/SqlBranch.IntegrationTests
SQLBRANCH_IT=1 SQLBRANCH_IT_IMAGE=ubuntu:24.04 SQLBRANCH_IT_POOL_BACKEND=plain dotnet test tests/SqlBranch.IntegrationTests
```

A test that needs a clone mode or a pool the run does not have throws `SkipTestException`, which is reported as skipped,
never as passed.

The cloud-source tests (`MinioSourceIntegrationTests`, `MinioSecretLeakIntegrationTests`, `AzuriteSourceIntegrationTests`) start
their own MinIO, a second MinIO with a certificate authority the engine does not know, and Azurite, each labeled
`sqlbranch-it-fixture=<run id>`, and remove only those containers. The run's test CA goes into the test engine's
`/var/opt/mssql/security/ca-certificates` and the engine is restarted. The generated test keys appear only in those
containers' settings. The tests of `CloudOptInIntegrationTests` skip unless their variables are set; they never run in CI.

The export and import tests (`ImageFileArchiveIntegrationTests`, `ImageCloudArchiveIntegrationTests`,
`ImageArchiveSecretLeakIntegrationTests`) share the same fixture, so one engine and the same containers serve them and
the cloud-source tests. The storage clients of the tool run on the client and cannot reach a fixture container's bridge
address, so these tests run the real command with a storage factory whose HTTP handler connects to the container's
published loopback port and trusts only the fixture's test certificate authority. The Ctrl+C test starts the tool through
`perl`, which resets an ignored SIGINT, so it also works when the test run itself is in the background.

## SqlPackage group 1

On macOS, install [SqlPackage](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-download)
and place it on PATH. For the recorded version, use `dotnet tool install -g microsoft.sqlpackage --version 170.5.96`.
From the repo root, run the doctor and restricted import checks against a local Docker daemon.

```sh
docker pull --platform linux/amd64 mcr.microsoft.com/mssql/server:2022-latest
export SQLBRANCH_IT=1 SQLBRANCH_IT_SQLPACKAGE="$(command -v sqlpackage)"
test -x "$SQLBRANCH_IT_SQLPACKAGE"
# Task 1.1 on macOS
dotnet test tests/SqlBranch.IntegrationTests -c Release --filter 'Category=Integration&FullyQualifiedName~SqlPackageDoctorIntegrationTests' --logger 'console;verbosity=detailed'
# Tasks 1.2 and 1.3 on the isolated engine
dotnet test tests/SqlBranch.IntegrationTests -c Release --filter 'Category=Integration&FullyQualifiedName~SqlPackageImportIntegrationTests' --logger 'console;verbosity=detailed'
```

Run task 1.1 again with the Linux executable in a local SDK container. The
script installs the pinned tool, builds the tests inside the container, and
uses the local Docker daemon for the dedicated test pool. It defaults to
`/var/run/docker.sock`; set `SQLBRANCH_IT_DOCKER_SOCKET` for a different local socket.

```sh
SQLBRANCH_IT=1 bash tests/SqlBranch.IntegrationTests/sqlpackage-doctor-linux.sh
```

Record passes and the reported versions for both doctor runs. Record the import
test's model, exit-code, diagnostic and catalog observations in the F2 research
notes. Windows is a separate 0.3 release gate.

## BACPAC local import (F2 tasks 2.1–2.3)

Use the absolute path to the installed standalone SqlPackage executable or
.NET tool shim. The tests export the fixture with SqlPackage in a cleared
environment, using a temporary SQL login with a random password. They drop
the login after export. Each case uses its own labeled engine and pool.

Run the five local import cases with this command.

```sh
SQLBRANCH_IT=1 SQLBRANCH_IT_SQLPACKAGE=/absolute/path/to/sqlpackage \
  dotnet test tests/SqlBranch.IntegrationTests/SqlBranch.IntegrationTests.csproj \
  -c Release --filter 'Category=Integration&FullyQualifiedName~BacpacBuildIntegrationTests'
```

The row-count test imports through SqlPackage and passes the resulting data
through the existing native backup image route for its branch assertion.
The abrupt-termination case uses a POSIX process wrapper.
