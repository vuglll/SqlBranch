# Tasks

After approval and merge into dev, implement with GPT-6.2 Sol on feat/add-bacpac-source. Use a dedicated pool, engine and port or the test world's unique IDs. For every group run a zero-warning dotnet build, dotnet test and the group's tagged integration tests with SQLBRANCH_IT=1. Tick only verified tasks, commit and stop for code review.

## 1. Verify the external tool and authentication

- [x] 1.1 Implement PATH and explicit path discovery and doctor reporting. Test precedence, actual /Version, missing executable, installation guidance and exit 3 on macOS and with the Linux SqlPackage executable in a local container. Document user installation and tested versions. Verify Windows at the 0.3 release gate.
- [x] 1.2 Spike real SqlPackage Import on the isolated engine using a throwaway build-owned login with a random password and access limited to the import database. Verify permissions and cleanup on success and failure. Inspect argv, inherited variables, output, diagnostics, temp artifacts and container settings. Only the throwaway password may appear in SqlPackage argv. Record results and required permissions.
- [x] 1.3 Spike import of packages with login-mapped users into a fresh isolated database under the intended restricted principal, with and without matching fixture logins. Compare model metadata, SqlPackage result, resulting principals and diagnostic behavior with a contained-user control. Record tool version and how to identify and name mappings even when Import fails or omits them.

## 2. Build a local BACPAC image

- [x] 2.1 Implement input and pre-init precedence. Table-test option conflicts, names, paths, unreadable inputs and content errors, with zero Docker calls for exit 2. Verify a readable corrupt package on a new machine gives not_initialized. Document the CLI and error precedence.
  Once BACPAC build support is implemented, document how to produce a
  `.bacpac` using the Azure portal or the user's own SqlPackage Export
  action. Explain that a transactionally consistent export needs a
  write-free source or a transactionally consistent copy. Show local
  `--from-bacpac` and Azure Blob/S3 URL examples with the existing storage
  credential settings. Keep examples requiring external resources out of
  fenced shell blocks executed by ImageDocsIntegrationTests.
  [Azure SQL BACPAC export](https://learn.microsoft.com/en-us/azure/azure-sql/database/database-export) · [SqlPackage Export](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-export)
- [x] 2.2 Implement package validation, mapped-user refusal and pool capacity checks. Test text, DACPAC, damaged ZIP and valid fixtures, named login-mapped users, exact free-space boundary, one byte short and subsequent pool exhaustion. Verify no mutation on refusal.
- [x] 2.3 Import into a fresh restricted target with owned paths. Verify branch data on the real engine and use a foreign sentinel to test collision and containment failures. Exercise rollback after partial import, cancellation, cleanup failure and abrupt termination. Verify cleanup or sweep of the build-owned import login, safe status output and preservation of live and foreign resources.
- [x] 2.4 Integrate masking and ImageSealer. Verify script order, trigger refusal, recovery override, rebuilt log, and manifest-last publication under injected failure. Document compatibility and recovery.
- [x] 2.5 Add provenance, size notices and timings. Test old manifests, text/JSON outputs, failed phases and a fake clock. Update F3's archiveFormat 1 importer to preserve unknown manifest fields. Round-trip local and URL provenance and an unrelated nested extension through import, rename and re-export.

## 3. Download a private package

- [x] 3.1 Use the approved Azure/S3 read transport. Trace calls to prove engine lookup, name lock and image existence checks precede every HEAD/GET. Test an existing shop image and concurrent builders, including lock release after failure.
- [x] 3.2 Validate URL/region/secrets and authenticated HTTPS using recording clients and isolated provider fixtures. Test download success, redirection, 403, invalid content and secret-bearing provider errors. Assert codes, source preservation, marker absence and a branch query.

## 4. Integration checks

- [x] 4.1 Run a clean zero-warning solution build, dotnet test and the complete suite with SQLBRANCH_IT=1 in the isolated test world. Verify local, remote, rollback and archive round-trip cases actually ran. Check native backup and script-source regression tests.
- [x] 4.3 Run openspec validate add-bacpac-source --strict and openspec validate --all --strict. Record commands, results, fixture disposition and outstanding measurements in the integration report. Check the report for credentials.

## Release gate (not part of this change's completion)

Moved here on 2026-11-20: each needs Windows or the x86-64 host. They gate release 0.3.

- Former 3.3 Implement per-build owner-only scratch and bounded download. Inspect Unix permissions and Windows ACLs. Exercise success, failure, mid-stream disconnect, temp_full, Ctrl+C and cleanup refusal on a replaced path. Document the temp location, deletion guarantees and recovery after forced termination.
- Former 4.2 Measure 10 GB and 50 GB logical-data imports on provisioned hardware. Record row counts, distribution, compressed bytes, host, engine and tool versions, clone mode, phase times, peak temp/pool use and capacity failures. Tick after both measurements are recorded.
