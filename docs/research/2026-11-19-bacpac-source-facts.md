# F2 BACPAC source research

Checked 2026-11-19 for release 0.3. These findings distinguish documented provider behavior from measurements still needed on a SqlBranch engine.

## What a BACPAC carries

A BACPAC is a ZIP package of the database model and table data. Azure SQL Database can export one for import into SQL Server. It serves as a logical migration format. Native backup media and F3's archive of sealed engine files have different readers and different guarantees. For a transactionally consistent export Microsoft requires either a write-free source during export or an export from a consistent copy. [Microsoft export guidance](https://learn.microsoft.com/en-us/azure/azure-sql/database/database-export?view=azuresql)

DacFx import requires a new database or an existing database with no user-defined schema objects. It can fail during model deployment or data loading. Assume a failed import may have created a partial target. Use a fresh build-owned target and exercise cleanup after failure. [SqlPackage Import](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-import?view=sql-server-ver17)

## Execution environment

Microsoft publishes SqlPackage for Windows, macOS and Linux as a .NET tool and standalone downloads. The user installs it independently, for example with `dotnet tool install -g microsoft.sqlpackage`. SqlBranch resolves the executable on PATH or uses an explicit path. SqlPackage reports its build number with `/Version`. Microsoft also demonstrates installation in a custom container. F2 uses the host executable. [Install SqlPackage](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-download) · [Version command](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage) · [Container example](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-pipelines)

Microsoft's published 170.5.96 package is a concrete version for the compatibility spike. Its licence grants development and testing use and sets conditions on redistribution, including downstream terms. SqlBranch distributes neither SqlPackage nor the DacFx library and does not install them on the user's behalf. Record the actual executable version on each test platform before declaring support. [Versioned package](https://www.nuget.org/packages/Microsoft.SqlPackage/170.5.96) · [Microsoft licence for that version](https://www.nuget.org/packages/Microsoft.SqlPackage/170.5.96/License)

### SqlPackage password input checked 2026-11-19

SqlPackage `/Version` returned `170.5.96.0` on macOS. Its documented
SQL password inputs for Import and Export are command-line switches or
credentials embedded in connection-string arguments. The CLI documents
no supported non-argv password input for either action, including stdin
or a private file.
Both of these local commands rejected `Profile` as an invalid argument
for the selected action before attempting a connection. The path is a
placeholder for an absent file.

```sh
sqlpackage /Action:Import '/Profile:<path-to-profile>'
sqlpackage /Action:Export '/Profile:<path-to-profile>'
```

The 2026-11-19 design uses a build-owned throwaway login. Only its randomly
generated, one-time password may appear in the SqlPackage import argv.
The login has access limited to the fresh import database and is removed
after success or handled failure.
The engine administrator password remains private to SqlBranch. Direct
Azure SQL export is deferred until it has a supported credential route.
[Import parameters](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-import) · [Export parameters](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-export)

## Size, elapsed time and portability

The service documentation describes export performance varying with indexes, size and available compute. SqlPackage can consume substantial temporary storage. A compressed package's byte length is a different quantity from source database space and imported data-file size. Timeouts, insufficient scratch space and model incompatibility deserve separate fixtures and phase-specific diagnostics. Managed import/export service quotas are not host-side DacFx quotas. [Azure export considerations](https://learn.microsoft.com/en-us/azure/azure-sql/database/database-export?view=azuresql) · [SqlPackage troubleshooting](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/troubleshooting-issues-and-performance-with-sqlpackage?view=sql-server-ver17)

No measured 10 GB or 50 GB SqlBranch import duration is available here. Benchmark both on an isolated, adequately provisioned runner. Record fixture data bytes, package bytes, engine version, clone mode, CPU, RAM, free and peak scratch space, storage type and wall time per phase. Treat results as observations for that configuration. Exercise disk exhaustion and timeout separately with small fixtures. A 16 GB host's RAM alone establishes neither a database-size limit nor a representative benchmark.

A concrete compatibility fixture is Azure SQL elastic query's CREATE EXTERNAL DATA SOURCE with TYPE = SHARD_MAP_MANAGER. Microsoft documents this syntax under Azure SQL Database, with different connector syntax for SQL Server. A local import cannot make remote data part of the package. Contained users can also change semantics across a round trip because SqlPackage generates new passwords. Exercise a target-incompatible model against the selected SQL Server engine and record the actual sanitized DacFx diagnostic. An object type alone cannot predict every model deployment error across engine versions. [External data source syntax by target](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-external-data-source-transact-sql?view=sql-server-ver17) · [Azure SQL elastic query](https://learn.microsoft.com/en-us/azure/azure-sql/database/elastic-query-overview?view=azuresql) · [SqlPackage database users](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage?view=sql-server-ver17)

## Research retained for the separate consistent-copy draft

Azure makes a database copy consistent at copy initiation. Seeding is asynchronous. Wait for ONLINE before export and keep writers away from the copy. The copy becomes an independent database. Its region, purchasing model, service objective, storage and lifetime affect incremental cost. Elastic pools have different billing from standalone provisioned databases. No single dollar amount follows from a 10 GB or 50 GB source. Warn before provisioning and identify a retained billable copy on cleanup failure. [Copy behavior and service objectives](https://learn.microsoft.com/en-us/azure/azure-sql/database/database-copy?view=azuresql) · [Azure SQL Database pricing](https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/)

The management API documents a create-or-update operation with Copy mode and a delete operation addressed by resource name. A lookup followed by deletion by name cannot by itself prove that the same instance still occupies that name. Neither a random suffix nor matching tags alone provide atomic protection against replacement. Before enabling a managed copy lifecycle, demonstrate safe creation and removal under collision, concurrent replacement, lost response and cancellation in an isolated Azure resource scope. Use no remote destructive SQL. If that proof fails, stop for a spec decision. [Create or update](https://learn.microsoft.com/en-us/rest/api/sql/databases/create-or-update?view=rest-sql-2023-08-01) · [Delete](https://learn.microsoft.com/en-us/rest/api/sql/databases/delete?view=rest-sql-2023-08-01)


## Group 1 verification

The macOS doctor integration test passed with SqlPackage `170.5.96.0`.
The Linux container doctor test also passed (1/1) with that version.
The Linux wrapper now removes its scratch directory recursively. The rerun
passed with exit 0.
Windows remains a 0.3 release gate.

### Task 1.2

The throwaway login needs ownership of the fresh import database via
`ALTER AUTHORIZATION ON DATABASE::<target> TO <login>`. It requires no server
permission grants. For contained targets, the engine administrator enables
`contained database authentication` and sets the target to partial containment.

| Surface | Inspection and result |
| --- | --- |
| SqlPackage argv | The throwaway password appeared exactly once. The engine administrator password was absent. |
| Child environment | Inherited variables were cleared. A probe using the same runner confirmed that the child received only the scratch directory variables (`HOME`, `TMPDIR`, `TMP`, and `TEMP`). |
| Stdout and stderr | Both passwords were absent from the captured streams. |
| Scratch `HOME` and `TMPDIR` files | A recursive scan of file contents as UTF-8 and UTF-16 found neither password. |
| Container settings | On 2026-11-19 the test scanned the full `docker inspect --type container` output (`Config` and its labels, `HostConfig`, `Mounts`); the rerun passed with neither password present. |

Diagnostics were inspected only with `/Diagnostics:False`. No diagnostic log
files were found, which verifies diagnostic file suppression for design D3.
Failure cleanup was exercised after SqlPackage exited non-zero. Timeout and
process termination cleanup were outside this run.

### Task 1.3 restricted import results, 2026-11-19

The restricted-import spike uses a login that owns only its empty target
database. The test checks for server permissions that could create a
database or alter logins, verifies that the login cannot access a separate
sentinel database, and checks cleanup after successful and failed imports.
The isolated engine returned `16.0.4325.4` from
`SELECT SERVERPROPERTY('ProductVersion')`. `sqlpackage /Version` returned
`170.5.96.0`.

The mapped fixture's initial export reported `SQL71501` because the user
depends on a login outside the extracted model. Both fixture exports exited 0
with the commands below. The mapped export skips extracted-model validation
with `/p:VerifyExtraction=False`. The contained export uses default
verification. [SqlPackage Export properties](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-export?view=sql-server-ver17#properties-specific-to-the-export-action)

```sh
sqlpackage /Action:Export /TargetFile:mapped.bacpac '/SourceServerName:127.0.0.1,<port>' \
  '/SourceDatabaseName:<mapped-source-db>' '/SourceUser:<export-login>' \
  '/SourcePassword:<one-time-export-password>' /SourceTrustServerCertificate:True \
  /Diagnostics:False /p:VerifyExtraction=False
sqlpackage /Action:Export /TargetFile:contained.bacpac '/SourceServerName:127.0.0.1,<port>' \
  '/SourceDatabaseName:<contained-source-db>' '/SourceUser:<export-login>' \
  '/SourcePassword:<one-time-export-password>' /SourceTrustServerCertificate:True \
  /Diagnostics:False
sqlpackage /Action:Import '/SourceFile:<package>' '/TargetServerName:127.0.0.1,<port>' \
  '/TargetDatabaseName:<fresh-target-db>' '/TargetUser:<target-db-owner-login>' \
  '/TargetPassword:<one-time-import-password>' /TargetTrustServerCertificate:True \
  /Diagnostics:False
```

The test imported `mapped.bacpac` with `f2_fixture_login` present, ran
`DROP LOGIN [f2_fixture_login]` as the fixture administrator, then imported
the same package into another fresh target. It also imported `contained.bacpac`
into a partially contained database and attempted to import `corrupt.bacpac`
into a fresh target. Each import used its own restricted database owner.
[Integration test command](../../tests/SqlBranch.IntegrationTests/README.md#sqlpackage-group-1)

| Import | Exit | Diagnostic codes | Catalog result |
| --- | --- | --- | --- |
| Mapped, login present | 0 | None | `f2_mapped_user`, `SQL_USER`, `INSTANCE`, SID matches `f2_fixture_login` |
| Mapped, login absent | 1 | `SQL72012`, `SQL72014`, `SQL72045` | Fixture principal absent |
| Contained control | 0 | None | `f2_contained_user`, `SQL_USER`, `DATABASE`, no server login match |
| Corrupt control | 1 | None | Fixture principal absent |

The failed mapped import named `f2_mapped_user` and `f2_fixture_login` in its
diagnostics. In `model.xml`, the `SqlUser` element named `[f2_mapped_user]`
carried `LoginIdentifierScript` and `AuthenticationType`, with no named
relationship to `[f2_fixture_login]`. The `SqlUser` element named
`[f2_contained_user]` carried `Password` and `AuthenticationType`.

A login-mapped user fails to import when its server login is absent.
SqlBranch must detect user-defined login-mapped users from `LoginIdentifierScript`
on their `SqlUser` elements in `model.xml` and report the affected database
user names from the elements' `Name` attributes before deployment, as required
by the `bacpac_login_users` refusal. The contained-user control's `Password`
property identifies a database-authenticated user in this fixture.

## Review process for the rest of F2 (2026-11-19)

The human asked to keep Claude usage low while another session runs the
Hetzner checks. For the remaining F2 work, Codex (`gpt-6.2-sol`) implements,
Codex (`gpt-6.2-astra`, read-only) gives the first review of each piece, and
Codex fixes its findings. The orchestrator runs every gate (build, unit tests,
Docker integration tests) and one final `sqlbranch-code-reviewer` review of all
remaining F2 work before the pull request, instead of one per group.

## Tasks 2.4 and 2.5: stopped after two fix rounds, then fixed (2026-11-19/20)

Uncommitted in the F2 worktree. Build 0 warnings; unit tests Core 1207,
Testing 10. With SqlPackage 170.5.96.0, 6 of 9 BacpacBuildIntegrationTests
pass. The three that reach the seal (masking, seal rollback, provenance round
trip) fail: after round 1 with "BACPAC cleanup could not verify or remove
...", after round 2 with "The engine could not verify or access the BACPAC
build paths." The seal-rollback test expects the injected seal error and gets
that message instead. The fix rounds addressed the cleanup after the seal; the
remaining failure is a path or ownership check that runs once the seal has
moved the files. Next step: read that check against what ImageSealer does on
the real engine, with Docker output.

Resolved on 2026-11-20 by debugging against the real engine. Two shell-script
bugs in BacpacOwnership, invisible to the fake runtime:
1. The seal-handoff guard had a stray `$` before each C# interpolation, so the
   shell ran `$[ ... ]` instead of `[ ... ]` and always failed.
2. The detached-files check used `a && b && c || d && e && f`. In sh, && and ||
   have equal precedence and group left to right, so the rebuilt-log `stat` ran
   even when the original log passed. A new `AnyOf` helper groups each
   alternative, with a test that runs it through /bin/sh.
After both fixes: 9 of 9 BacpacBuildIntegrationTests, the SqlPackage tests and
the F3 file-archive tests pass (19 of 19).

## State on 2026-11-20 (for continuing the loop)

Groups 1 to 3 done; review findings on the seal and download fixed and green
(16 of 16 BACPAC/SqlPackage integration tests on both pools). Rebased onto dev
after #21 and #22. Left: the final sqlbranch-code-reviewer review of all F2
work, gate C (full suite on loop-xfs and plain), and the PR into dev. Release
gate: 3.3 Windows ACLs, 4.2 10/50 GB import timings on x86-64.

## Gate C (2026-11-20)

Branch at 08f99bd plus the final-review fixes, rebased onto dev after #21/#22.
Build 0 warnings; unit tests Core 1229, Testing 10. Full integration suite
with SqlPackage 170.5.96.0: managed loop-xfs (thin) 153 passed, 5 skipped,
0 failed; plain (copy) 132 passed, 26 skipped, 0 failed; no container left.
After the final-review fixes, the BACPAC, SqlPackage, cloud-archive and docs
tests passed again on both pools (26 of 26 each). `openspec validate --all
--strict`: 10 of 10.

## Login detachment spike

The Azure-shaped fixture models the `Login` relationship described in the
login-detachment design. The reference and its target carry the same name
and disambiguator. This example shows the synthetic `sales_app` mapping.

```xml
<Element Type="SqlLogin" Name="[sales_app]" Disambiguator="3" />
<Element Type="SqlUser" Name="[sales_app]">
  <Relationship Name="Login">
    <Entry>
      <References Name="[sales_app]" Disambiguator="3" />
    </Entry>
  </Relationship>
</Element>
```

A real Azure SQL Database export (2026-11-20) used this shape for two
application users, with `AuthenticationType` 1 and a `SqlLogin` element per
user carrying a generated `Password`, `Sid` and `DefaultLanguage`.

Results on 2026-11-20 (SqlPackage 170.5.96.0, managed loop-xfs, thin), from
`BacpacLoginDetachSpikeTests`:

- A local export of the fixture (`Fixtures/bacpac-login-users.sql`) has no
  `SqlLogin` elements and no server role membership: a login-mapped user
  carries `LoginIdentifierScript`, `AuthenticationType` 1 and a `Login`
  relationship whose entry is a `PersistedResolvableAnnotation` targeting
  `SqlLogin`, not a `References`.
- A user created `WITHOUT LOGIN` serializes as one property,
  `<Property Name="IsWithoutLogin" Value="True" />`, and nothing else.
- Database grants (`SqlPermissionStatement` with a `Grantee`), database role
  membership (`SqlRoleMembership` with a `Member`) and schema ownership
  (`SqlSchema` `Authorizer`) reference the user by name, not the login.
- Replacing each mapped user's `LoginIdentifierScript`, `AuthenticationType`
  and `Login` relationship with `IsWithoutLogin`, removing every `SqlLogin`,
  and recomputing the `Origin.xml` model checksum imported under the
  restricted build login for both the local and the Azure shape.
  `report_reader` and `sales_app` came back as `SQL_USER` with authentication
  `NONE`, `reports` stayed owned by `report_reader`, `sales_app` kept SELECT
  on `dbo.Orders` and its database role, and `sys.server_principals` was
  unchanged.

## First real Azure SQL Database export (2026-11-21)

The first build from a real Azure SQL Database export ran with SqlBranch
0.2.1 from nuget.org on a Mac with Docker Desktop, thin pool. Names of the
database and its principals are left out on purpose.

| Measure | Value |
|---|---|
| Azure export with SqlPackage | 6 min 24 s |
| BACPAC size | 110,638,293 bytes (110.6 MB) |
| Model | 74 tables, 2 `SqlLogin` elements, 2 login-mapped users (`Login` relationship, as in the Azure shape above), 0 server objects referencing them |
| Image | 2 files, 1.07 GiB, recovery model FULL sealed as SIMPLE |
| Phases | rewrite 13,913 ms, import 33,567 ms, seal 3,488 ms, cleanup 1,082 ms |

0.2.0 failed this package in import with `[SQL72012, SQL72014, SQL72045]`,
because its check missed the `Login` relationship and the restricted login
cannot create logins. 0.2.1 detached both users and removed both logins.

**Open finding: the rewrite is slow.** 13.9 s for 110.6 MB is about 8 MB/s.
`BacpacPackage` copies every entry through `ZipArchiveEntry.Open()` into a new
entry at the default compression level, so the BCP data is inflated and
deflated again. At this rate a 10 GB package would spend around 20 minutes in
the rewrite. Copying the compressed entries unchanged (only `model.xml` and
`Origin.xml` change) would make it bounded by disk speed.

**Fixed in 0.2.2.** The rewrite now copies unchanged entries byte for byte.
The same package with 0.2.2 from nuget.org, same Mac: rewrite 574 ms (was
13,913 ms), import 33,139 ms, seal 3,554 ms, cleanup 1,103 ms; the same two
users detached and two logins removed, and the same 1.07 GiB image.
