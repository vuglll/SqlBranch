# Proposal

## Why

Release 0.3 queue row F2 gives teams using Azure SQL Database a logical schema-and-data route into SqlBranch. Its prerequisite, release 0.2, is archived as `2026-11-18-add-test-fixtures` and supplies the restricted post-build script and sealing pipeline.

## What Changes

- Build from a local BACPAC or an Azure Blob or S3 object using `--from-bacpac`.
- Report package size before expensive work, warn that large operations can take hours, and report elapsed time per phase.
- Import into a private empty database on the owned engine, run existing masking scripts, and publish a sealed image only after cleanup. Rebase the “Where storage secrets may appear” and “Commands before init” deltas on the F3 specs merged into dev. Retain F3’s archiveFormat 1 and preserve unknown manifest fields on import.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `images` gains logical sources, provenance, progress reporting and login-mapped user refusal.
- `configuration` defines SqlPackage discovery and secret handling for client-side BACPAC operations.
- `engine` defines check ordering, local import ownership and interruption reporting.

## Impact

Users install SqlPackage separately. Logical builds and `doctor` resolve it from PATH or `--sqlpackage <path>`. Remote inputs use an owner-only client temp directory. Cleanup covers success, failure and Ctrl+C. Client and pool space failures have distinct codes. Live Azure SQL export and database copy are deferred to the unscheduled `add-consistent-azure-sql-export` draft. Stripping login-mapped users is deferred.

The research records Microsoft’s licence and a published tool version. Group 1 verifies real SqlPackage import with a throwaway login restricted to the import database, and the behavior of login-mapped users. Import durations at 10 GB and 50 GB still require measurement. Sources checked 2026-11-19. [Microsoft SqlPackage licence](https://www.nuget.org/packages/Microsoft.SqlPackage/170.5.96/License) · [Installation](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-download) · [Research and verification limits](../../../docs/research/2026-11-19-bacpac-source-facts.md)
