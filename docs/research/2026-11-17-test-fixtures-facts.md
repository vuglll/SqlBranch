# Facts for add-test-fixtures (checked 2026-11-17)

Facts the `add-test-fixtures` change rests on, checked on 2026-11-17.

| Fact | Source |
|---|---|
| Testcontainers.MsSql shows 45.6M total downloads on nuget.org (latest version 4.17.0). This replaces the 36.5M figure from a third-party profile page in `2026-11-15-market-and-storage.md` | https://www.nuget.org/packages/Testcontainers.MsSql |
| `DROP DATABASE` on SQL Server needs `CONTROL` on the database, `ALTER ANY DATABASE`, membership in `dbcreator`, or membership in `db_owner` of the database being dropped. A login that only owns one database therefore cannot drop another database | https://learn.microsoft.com/en-us/sql/t-sql/statements/drop-database-transact-sql |
| `ALTER_DATABASE`, `CREATE_DATABASE`, `DROP_DATABASE`, `CREATE_LOGIN` and `DROP_LOGIN` are DDL events with server scope only, so a database-scoped DDL trigger cannot use them. `ALTER_AUTHORIZATION_DATABASE` (ALTER AUTHORIZATION ON DATABASE and `sp_changedbowner`) has server or database scope, as do `CREATE_TRIGGER`, `ALTER_TRIGGER` and `DROP_TRIGGER`. `DISABLE TRIGGER` is not listed as an event | https://learn.microsoft.com/en-us/sql/relational-databases/triggers/ddl-events |

## Open questions

- Cross-database access with the guest check bypassed remains unmeasured. The
  acceptance test exercises the supported build path with the guest check enabled.
- The exact moment an activation already running during the build freeze stops
  is still unmeasured. Since 2026-11-18 the build no longer depends on it: the
  triggers are compared again after the re-attach, before the owner changes to
  `sa` (task 2.6f).
- The practical fixture concurrency with 16 GB assigned to the Docker VM. The
  smaller allocation measured below establishes a working point for this host.

## Build ownership change and trigger dependencies (2026-11-17)

Design review of [D7, steps 5, 8 and 9](../../openspec/changes/add-test-fixtures/design.md).
During a backup build with post-build scripts, D7 checks the restored database's
triggers before handing ownership back to `sa`. An unchanged trigger can call a
routine whose definition the scripts changed. The trigger snapshot therefore
cannot establish that the ownership change will execute only code from the backup.
This finding comes from reviewing the design and has no runtime verification yet.

Proposed decision for group 2: when a backup build requests post-build scripts,
check for enabled database triggers that handle the ownership change before the
first ownership transfer. If one exists, stop with `ddl_trigger` and identify it.
Verify rejection with a harmless audit trigger and check that the post-build
script never starts. This would also reject legitimate audit triggers for that
event. The rule was approved on 2026-11-18 and incorporated into D7.

On 2026-11-18 the Docker integration test exercised harmless audit triggers
on SQL Server in the managed and plain pool fixtures. The catalog check
identified both a direct handler and a handler registered through
`DDL_DATABASE_LEVEL_EVENTS`. It accepted the database after both were
disabled while a table-creation trigger remained enabled. A backup-only CLI
build of the database with all three enabled succeeded, and image listing
returned all three names. The CLI rejected the same backup with a post-script that would throw if run.
Disabling both ownership handlers allowed a post-script to finish. Altering the
remaining table audit trigger in a post-script returned `ddl_trigger`.

On 2026-11-18 SQL Server rejected a harmless database-scoped trigger for
`ALTER_DATABASE` during script execution. The CLI returned `script_failed`.
Restoring the guest-enabled fixture backup produced `is_broker_enabled = 0`,
matching the backup-only image's recorded `serviceBroker` value of `disabled`.
The branch guest check returned `guest_database` naming the guest-enabled branch
before the build executed its scripts.

On 2026-11-18, the marker queue test passed in both thin and copy clone modes.
Before sealing, sending one message activated a procedure that inserted one row
in a table in the build database. Image listing reported `serviceBroker` as
`disabled`. A branch from each image had `is_broker_enabled = 0`, one marker
and no pending marker messages. Sending a second message in the branch put it
in `sys.transmission_queue`. It was still there two seconds later, and the
marker count was still one. These observations cover delivery in the sealed
image's branch. The timing of an activation already running during the freeze
remains unmeasured.

## Background code running at the freeze (2026-11-18)

Task 2.6f. A script started a Service Broker activation procedure, `EXECUTE AS
OWNER`, that kept creating and dropping trigger `t_late` for
`ALTER_AUTHORIZATION_DATABASE` for 40 seconds. The trigger would write a unique
token to the engine's error log with `RAISERROR ... WITH LOG`, which only `sa`
can do. The script waited until the procedure had started, so it was running
when the build froze the database. The build took about 10 seconds.

On Docker Desktop for macOS, in clone mode `thin` (managed loop-xfs pool) and
`copy` (plain pool), the build exited 0 both times. The error log had no line
with the token. No build login or build database was left. A branch of the
image had no database DDL trigger, and `status` reported no mismatches.

So the activation did not outlive the freeze and the detach that follows it:
with the procedure still looping, the detach would have failed. This was one
run per mode. The second trigger comparison before the change of owner to `sa`
covers the case regardless; its unit test makes a trigger appear after the
freeze and checks that the build fails with `ddl_trigger` and never sends
`ALTER AUTHORIZATION ... TO [sa]`.

## Parallel fixtures on Docker Desktop (2026-11-18)

The Mac reported 17,179,869,184 bytes of physical memory (16 GiB). Docker
reported 8,319,438,848 bytes (7.75 GiB) available to its Linux VM. The tests
used separate SqlBranch-owned SQL Server 2022 engines for the managed XFS
pool and plain copy pool, running one mode at a time.

For each mode, the test started 20 creates against one 100-row image, held
all 20 branches live, then opened a connection to each branch concurrently
and counted its rows. Every query returned 100 and every branch had a distinct
name. Disposal left a clean status. In the final verification run, creation took
9.0 seconds in thin mode and 10.2 seconds in copy mode. Including the queries,
the elapsed times were 9.1 and 10.3 seconds. This establishes a working
concurrency of 20 for this workload. The VM's exhaustion point and performance
with 16 GB assigned to Docker remain unmeasured on Docker Desktop (the Mac
has 16 GiB in total, so Docker Desktop cannot be given 16 GB).

## Parallel branches on native Linux, engine capped at 16 GB (2026-11-20)

Measured on native x86-64 Linux (Hetzner CCX33, 8 vCPU, 30 GiB), not Docker
Desktop: the engine container was capped with `docker update --memory 16g
--memory-swap 16g` and restarted, and SQL Server sized itself to the limit
(13,108 MB physical memory reported, 11,934 MB committed target). N `branch
create` processes started at once from a 100-row image, then one query per
branch, all at once.

| N | Create, all N | Failures | Queries | Engine memory |
|---|---|---|---|---|
| 20 | 7.6 s | 0 | 20/20 | 0.93 GiB |
| 50 | 19.8 s | 0 | 50/50 | 1.08 GiB |
| 100 | 41.9 s | 0 | 100/100 | 1.38 GiB |
| 200 | 87.9 s | 0 | 200/200 | 1.96 GiB |
| 400 | 217.3 s | 0 | 400/400 | 3.41 GiB |

No exhaustion point was found up to 400. Creation is close to serial (about
0.5 s per branch), so time, not memory, limits the practical number. Full
method in `docs/research/2026-11-20-x86-64-release-gate-run.md`.

