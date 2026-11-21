# Design

## Context

See `proposal.md`. `add-branching-core` (archived 2026-11-17) left the seams
this change uses: the image build pipeline (`ImageBuilder` with
`StepNames` and the `AfterStep` hook), `labels` on `BranchRecord` (always
empty so far), the branch lock in `EngineLocks`, `BranchRemover`, and
`SqlBranch.Core` as a library. Facts checked for this change are in
`docs/research/2026-11-17-test-fixtures-facts.md`.

## Goals / Non-Goals

**Goals:**

- A test gets a branch with one line and never leaks it, even after a crash.
- Safe under parallel test runs and next to branches people made by hand.
- No dependency on a particular test framework.
- Scripts the user brings cannot damage anything outside their build.

**Non-Goals:**

- Starting or installing the engine from tests. The fixture expects `init`
  to have run and says so when it has not.
- Replacing Testcontainers. Users can keep both.
- A public, versioned API for `SqlBranch.Core`.

## Decisions

### D1. The fixture calls the Core library, not the CLI

`SqlBranch.Testing` references `SqlBranch.Core` and calls the same operations
the CLI does (`BranchCreator`, `BranchResetter`, `BranchRemover`, the new
`BranchPruner`).

Alternatives considered: shell out to `sqlbranch`. Rejected: it needs the
tool installed on every test machine, costs a process per call, and turns
errors into text to parse.

### D2. API shape

```csharp
await using var db = await SqlBranchDatabase.CreateAsync("shop");
// db.Name, db.ConnectionString, db.CloneMode, db.ExpiresAt
await db.ResetAsync();
```

- `CreateAsync(string image, SqlBranchOptions? options = null, CancellationToken ct = default)`.
- `SqlBranchOptions`: `Prefix` (default `t`), `TimeLimit` (default 1 hour),
  `Labels`, `ConfigFile` (replaces the repository `.sqlbranch.yml`, like
  `--config`).
- `SqlBranchEnvironment` holds process-wide state: `IsAvailableAsync()`,
  `MaxConcurrentBranches` (null by default, a `SemaphoreSlim` when set; a
  waiting create honours its cancellation token), and `Log`
  (`Action<string>`, default writes to standard error).
- `SqlBranchDatabase` is `IAsyncDisposable`. `CloneMode` is `thin` or `copy`.
  `ToString()` returns `SqlBranchDatabase t-1a2b3c4d (thin)` and never the
  connection string.
- Errors: `SqlBranchUnavailableException` with a `FixCommand` property
  (`sqlbranch init`, `sqlbranch engine start`, `sqlbranch image build
  <image>`), and `ArgumentException` for a bad prefix, a `TimeLimit` that is
  zero, negative or over 365 days, or a caller label that breaks the label
  rules or uses the reserved `sqlbranch.` prefix, all checked before any
  runtime or engine call. Every exception message passes through the
  existing `SecretRedactor`, and inner `SqlException`s are wrapped so their
  text is redacted too.
- Dispose: `branch_not_found` is swallowed; `engine_stopped`,
  `docker_unavailable` and connection failures are logged through `Log`
  ("branch t-1a2b3c4d was left; `sqlbranch branch prune` removes it after it
  expires") and swallowed. Other failures are logged and swallowed too,
  because a throwing dispose hides the test's own failure.
- Settings: `SettingsLoader` with `WorkingDirectory` set to
  `Environment.CurrentDirectory`. Test runners start in `bin/<config>/<tfm>`,
  so the walk up to the nearest `.sqlbranch.yml` finds the repository file,
  as the configuration spec's "Config file discovery" says. `SQLBRANCH_`
  variables win over the file, as for the CLI.
- No xUnit, NUnit or MSTest reference: each framework's own fixture
  mechanism holds the object.

Alternatives considered: one package per framework (deferred until someone
asks); a static per-test helper bound to `AsyncLocal` (rejected: hides
lifetime and breaks under parallel runners); a builder object (rejected: more
API for three options).

### D3. Short-lived branches and prune

`branch.json` gains `ephemeral` (default false) and `expiresAt` (UTC, null
for ordinary branches), next to the existing `createdAt`. `labels` already
exists on `BranchRecord` and is now filled from `--label`. Old records load
with the defaults. `expired` is computed at read time from a
`TimeProvider`, so tests use a fake clock; it is not stored.

- `--ttl` accepts `<n>m`, `<n>h`, `<n>d` with n ≥ 1 and at most 365d, and
  needs `--ephemeral`. One `Duration` parser serves `--ttl`,
  `--script-timeout` and the fixture's `TimeLimit` check.
- Label keys: 1 to 63 characters `[a-z0-9._-]`, starting with a letter or
  digit. The `sqlbranch.` key prefix is reserved for SqlBranch when labels
  are *set*: `branch create --label` refuses it (exit 2) and the fixture
  throws `ArgumentException` for a caller label using it. Core adds
  `sqlbranch.owner=testing` itself. Filtering with `branch list --label
  sqlbranch.owner=testing` is allowed, so people can find fixture branches.
- `branch reset` rewrites `branch.json` keeping `ephemeral`, `expiresAt` and
  `labels` (decision Q9: reset never extends the limit).
- `BranchPruner` lists records, keeps those with `ephemeral` and
  `expiresAt < now` (and, for the fixture, the owner label), and for each one
  tries the branch lock with `EngineLocks.TryTakeAsync`. A held lock means a
  create, reset or removal is running: the branch is reported as skipped.
  Otherwise it calls the same removal as `BranchRemover` (close connections
  with `SINGLE_USER WITH ROLLBACK IMMEDIATE`, drop, delete the folder). A
  `broken` record has no database; removal then deletes the folder only. A
  failure is recorded and the loop goes on; any failure gives exit 1 with
  `error.code` "operation_failed" and the leftovers in `failed`.
- The fixture prunes before a create until one prune in the process has
  succeeded: a `bool` flag behind a `SemaphoreSlim`, set only on success. A
  failed prune is logged through `Log` and the create goes on; the next
  create tries again, and so does the next process. A faulted `Lazy<Task>`
  is not used, because it would cache the failure and break every later
  create in the process.
- Name collisions: the fixture draws 8 hex characters from
  `RandomNumberGenerator` (a replaceable name source in tests). On
  `branch_exists` it draws again, up to 3 retries, then throws.

Alternatives considered: a background reaper container, as Testcontainers
uses (rejected: one more moving part; prune-on-next-run is enough for one
machine); a fourth branch state `expired` (rejected: state describes the
database, and an expired branch is still online); fixture prune of every
expired branch (rejected: it would delete short-lived branches a person
made with the CLI, decision Q1).

### D4. Images from scripts, and the builder split

`ImageBuilder` is split into source steps and shared seal steps:

| Source | Source steps |
|---|---|
| backup | `check-name`, `check-backup-space`, `stage`, `inspect`, `check-restore-space`, `restore` |
| scripts | `check-name`, `check-scripts`, `create-database`, `run-scripts` |

Shared steps, in order: `post-scripts`, `check-database`, `recovery-model`,
`checkpoint`, `detach`, `rebuild-log`, `seal`, `manifest`. `StepNames`
becomes `StepNames(ImageSource)` and returns the source steps plus the
shared steps. For a backup build the 0.1 names keep their order, with
`post-scripts` and `check-database` inserted after `restore`; the unit test
that asserts 12 steps is updated to the new count, the per-step failure
tests run over every step of both lists, and the `AfterStep` hook and
`KilledBuildIntegrationTests` keep working as they are.

Error order: `check-name` (name rules, image exists, build lock) only reads,
so it runs first and `image_exists` wins over `guest_database`. The guest
check (D7 step 1) runs right after it and before any step that changes
something. The login (D7 steps 2 to 6) is created right after the database
exists (`create-database` or `restore`); `check-database` (D7 steps 7 and
8) ends the script part, and the login is dropped right after `detach` (D7
step 9). A backup build without post-scripts creates no login and skips the
freeze.

Scripts are read on the client, in `StringComparer.Ordinal` order of their
file names, from the folder only (no subfolders), as UTF-8 with BOM
detection. Every source and post-build script is read and split on the client before
the build's first step, so a bad script stops the build before any SQL
runs. Batches are split on lines
matching `^\s*GO\s*$` (case-insensitive). A line `GO <n>` is refused with
exit 2 naming the script and line: supporting it means running a batch n
times, which no script in the target audience needs and which complicates
error reporting; refusing is simpler and can be lifted later. Batches are
sent through SqlClient on the build login's connection. The output prints each script's run time. A failing batch stops
the build with `error.code` "script_failed" (exit 1), naming the script, the
batch's first line and the SQL error, and the normal cleanup runs. There is
no free-space check before a script build: its size is unknown until the
scripts have run. Running out of space fails the build and the cleanup
removes everything.

The empty database is created by the engine's `sa` connection with
`CREATE DATABASE [sqlbranch_build_<id>]` with its files in the build folder,
so the rest of the pipeline treats it like a restored database. The
manifest's `sourceDatabase` for a script image is the image name.

Script time limit: script batches use `CommandTimeout = 0`, not the 120 s the
engine connector sets on its own commands
(`src/SqlBranch.Core/Engine/EngineConnector.cs`, `Command(string sql)`), so
the scripts connection is built separately. With `--script-timeout`, a
`CancellationTokenSource` with that delay covers the whole script; on
cancellation SqlClient sends an attention, the build fails with
`script_failed` naming the script and the limit, and the normal cleanup
runs. A distinct error code was considered and not used: the user's fix is
the same as for any failing script (change the script or the limit), and
the message says which.

Alternatives considered: run `sqlcmd` inside the engine container (rejected:
its presence and path differ between engine images, and errors come back as
text); send each file as one batch (rejected: most migration scripts use
`GO`); use a migration library such as DbUp (rejected: a new dependency for
a loop of 30 lines); a separate builder class per source (rejected: the
cleanup and seal logic would be duplicated).

### D5. Post-build scripts

`--post-script <file>` (repeatable) runs in the shared `post-scripts` step,
for both sources, on the build login's connection, in the order given. A
failing script fails the build and triggers the normal cleanup. Files are
checked for existence and readability while the command line is parsed,
before any SQL (exit 2). The manifest records each post-script's name and
SHA-256. This is the seam for masking: users bring their own scripts now,
and a rules engine can plug into the same step later.

Alternatives considered: run the scripts on a branch after sealing and seal
again (rejected: doubles the files and the build time); name the scripts in
`.sqlbranch.yml` (rejected for 0.2: per-image settings do not exist yet, and
a flag is explicit); a masking rules engine now (deferred, out of scope).

### D6. Agent skill generated from command metadata

Each CLI command declares a `CommandMetadata` record with four `required`
members: `Purpose`, `WhenToUse`, `Example`, `Destroys` (`"nothing"` for
read-only commands). A missing member is compiler error CS9035, so
`dotnet build` fails and names the member and the file. A unit test walks
every command in the command tree and fails on an empty or whitespace value.

A generator in `SqlBranch.Cli` writes `SKILL.md` and one reference file per
command group (`reference/branch.md`, `reference/image.md`, ...). Output is
deterministic: commands in tree order, LF line endings, no timestamps.

The stale check is a unit test in `tests/SqlBranch.Core.Tests` (which already references the CLI project): it generates in
memory, walks up from the test's directory to the repository root, and
compares with `skills/sqlbranch/`. On a difference it fails, names the file,
and prints `dotnet run --project src/SqlBranch.Cli -- skill export --dir
skills/sqlbranch --force`. A CI step that runs the same command and `git
diff --exit-code` is an extra; CI currently runs only on demand
(`workflow_dispatch`), so the unit test is the real gate.

`sqlbranch skill export --dir <path> [--force]`:

1. Check the folder and its parent: the parent must exist or be creatable
   and be writable (the temporary sibling folder lives there), and the
   target, if it exists, must be writable; a probe file is written and
   deleted in each. Failure: exit 2, `usage_error`, nothing written. Then
   delete stale siblings named `<dir>.sqlbranch-tmp-` plus exactly 8 lowercase
   hex characters (`^.+\.sqlbranch-tmp-[0-9a-f]{8}$`) that a killed export
   left; no other folder is touched. A bad `--dir` is a fault
   in the command line, like a missing `--from-bak` file, which is also 2;
   3 is for Docker, the engine and the pool, which this command never uses.
2. Without `--force`: if any generated file name exists, exit 1
   `skill_exists` naming `--force`, nothing written.
3. Write every generated file into a temporary sibling folder
   `<dir>.sqlbranch-tmp-<8 hex>`. A failure here: delete the temporary folder,
   exit 1, `operation_failed`, the message names the failed file; the target
   is untouched.
4. Move into place. A missing target: rename the temporary folder to the
   target (one rename). An existing target (`--force`): rename each file
   that will be replaced to a backup inside the temporary folder, then move
   the new files in; if a move fails, move the new files back out and the
   backups back in, then delete the temporary folder and exit 1. Files not
   in the generated list are never opened.

The command builds no settings and no runtime, so it works before `init`.

Skill guardrails: work only in branches the agent created; never run
destructive SQL against a server that is not the SqlBranch engine; reset or
remove branches when done; never weaken a test to avoid a database problem.

Alternatives considered: a hand-written skill (rejected: drifts from the
tool); generate from System.CommandLine help text (rejected: it has no "when
to use" or "what it destroys"); XML doc comments (rejected: not available at
run time for `skill export`); a stale check only in CI (rejected: CI runs on
demand only, decision Q7).

### D7. Build login for scripts

Rule for all SQL in this section: every database name read from
`sys.databases` is quoted with `QUOTENAME` (in .NET, the existing
`Quoting.Identifier`) wherever it appears, including three-part names such
as `[odd]]name].sys.database_permissions`.

The build changes ownership before the scripts and after the log rebuild. Both
changes are privileged. An unchanged trigger can call a routine a script
modified, so checking the trigger definition alone cannot protect either transfer.

For every build that runs source or post-build scripts, SqlBranch, as `sa`:

1. Guest check, after `check-name` and before anything changes: for every
   database with `database_id > 4`, `state_desc = 'ONLINE'` and
   `user_access_desc <> 'SINGLE_USER'`, read
   `<db>.sys.database_permissions` for a granted `CONNECT` to `guest`.
   Databases in any other state (RESTORING build databases of concurrent
   builds, OFFLINE, RECOVERY_PENDING, ...) and SINGLE_USER databases (held
   during `branch rm`, `reset` or `prune`, fourth review, Q4) are skipped:
   the login cannot enter them either. A hit, or an ONLINE database that
   cannot be read (fail closed): exit 1, `guest_database`, naming it, and the
   message says which of the two it was; nothing is changed, users'
   databases included (third review, Q1). One code for both: a script
   reading `error.code` takes the same action (that database blocks script
   builds), and the message tells a person whether to disable `guest` or
   look at why the database cannot be read. Exit 1, not 3: 3 means Docker,
   the engine or the pool is not ready, while here the environment works
   and the build refuses on the content of a database, like `image_in_use`.
   Timing gap (third review, Q2): the check is a snapshot. A branch created from
   a guest-enabled image, or a backup restored, while a long script build
   runs is not blocked and not seen. `docs/images.md` says so.
2. Removes leftover build logins (below).
3. Takes the trigger snapshot: name, `object_id`, `modify_date` and
   `is_disabled` of every row in `<build>.sys.triggers WHERE parent_class =
   0`. For a restored backup with post-scripts, join the trigger ids to
   `sys.trigger_events` and check for the effective event
   `ALTER_AUTHORIZATION_DATABASE`, including registrations via event groups.
   Before login creation, ownership transfer or script execution, reject any
   enabled match with `ddl_trigger`, naming the triggers. An unreadable catalog
   fails the build. Disabled matches and triggers for other events pass this check.
4. `CREATE LOGIN [sqlbranch_build_<id>] WITH PASSWORD = '<32 random
   characters>', CHECK_POLICY = ON, DEFAULT_DATABASE = [sqlbranch_build_<id>]`.
   The password lives only in memory, is added to `SecretRedactor`, and is
   never written to a file or log.
5. `ALTER AUTHORIZATION ON DATABASE::[sqlbranch_build_<id>] TO
   [sqlbranch_build_<id>]`. The login becomes the database owner and maps to
   `dbo`, so every object or schema a script creates (`CREATE SCHEMA sales`)
   belongs to `dbo`. No database user is created, so there is none to drop
   and no error 15138 ("the database principal owns a schema"). Step 3 has already checked restored triggers.
6. Opens a second connection as that login and runs the scripts.
7. Freeze, at the start of `check-database`, on success only: close the
   scripts connection; `ALTER LOGIN [...] DISABLE`; `ALTER DATABASE [...]
   SET DISABLE_BROKER WITH ROLLBACK IMMEDIATE` to stop message delivery and
   request immediate rollback; then kill any remaining session of the login.
   The freeze must finish before validation and detach.
   The image is sealed with Service Broker disabled and the manifest
   records `serviceBroker: "disabled"` (final approval, item 2): turning it
   back on in a branch would also revive any queue a script left. Images
   built without any script keep the restored database's state and record
   it as `enabled` or `disabled`.
8. `check-database` reads (no DDL), and the first problem fails the build:
   - the triggers again; any trigger not in the snapshot, or with a changed
     `modify_date`, or enabled when it was disabled, fails with
     `ddl_trigger` naming it (third review, Q3). A trigger dropped by a
     script is fine. With step 7 this is a second layer: no trigger can fire
     under `sa` after the scripts anyway.
   - `state_desc`, `is_read_only` and `user_access_desc` from
     `sys.databases`; anything but ONLINE, read-write and MULTI_USER fails
     with `script_failed` naming the state (third review, Q4).
   - `sys.master_files` for the database: a path outside the build folder,
     or a FILESTREAM (`FD`) or memory-optimized (`FX`) filegroup, fails with
     `unsupported_feature` naming the path or filegroup (fourth review, Q3).
     This is the code the main spec's "Unsupported database features are
     rejected" uses for the same filegroups in a backup, so a script that
     matches on it handles both sources alike. A file outside the build
     folder is the same kind of problem, a database layout SqlBranch cannot
     seal, and every script succeeded, so `script_failed` would mislead.
9. Success: `recovery-model` (`ALTER DATABASE ... SET RECOVERY`) and
   `checkpoint` (`CHECKPOINT`, not a DDL event) run, then `detach`
   (`sp_detach_db`, not a DDL event) while the build login still owns the
   database, then `DROP LOGIN`: the login owns no attached database any
   more, so the drop succeeds. Between `check-database` and the detach
   nothing runs inside the database that can fire a database trigger. The
   `rebuild-log` attach as `sa` makes `sa` the owner in `sys.databases`.
   Then, still in `rebuild-log`, after `SET RECOVERY` and before the final
   detach, `sa` runs `ALTER AUTHORIZATION ON DATABASE::[...] TO [sa]`
   (final approval, item 1). The build login is gone, its sessions ended and
   Service Broker is disabled, so no code of the login can run; the change
   gives `dbo` the `sa` SID, so `EXECUTE AS OWNER` and `EXECUTE AS USER =
   'dbo'` work in branches. Step 3 excludes enabled restored handlers of the ownership
   change. Step 8 excludes newly created, changed or newly enabled handlers.
10. Failure at any point after step 4, the owner is never reset:
    - when the database is ONLINE, `SET SINGLE_USER WITH ROLLBACK
      IMMEDIATE` as its own statement; an OFFLINE or otherwise not-online
      database skips it;
    - when every file in `sys.master_files` is inside the build folder,
      `DROP DATABASE`, and the build folder cleanup removes files an
      OFFLINE drop leaves; otherwise `sp_detach_db`, the files outside the
      build folder are left in place and named in the error, and the build
      folder cleanup removes the rest;
    - then kill the login's sessions and `DROP LOGIN`. With the database
      gone, the login owns nothing and the drop succeeds.

`ddl_trigger` is a code of its own, not `script_failed`: every script
succeeded (or, for a backup trigger, no script ran), the failure is about a
database DDL trigger, and the fix (remove the trigger from the scripts, or
disable it in the source database) differs from fixing a SQL error.

Backup images and database DDL triggers: a trigger that came in the backup
and was not changed is kept and listed in the manifest as `ddlTriggers`
(fourth review, Q2). Real databases often carry audit DDL triggers; they are
the user's own code from their own database, and branches already carry them
in 0.1. For a backup without post-scripts, keep these triggers. For a backup
with post-scripts, apply the event check in step 3, even to audit-only triggers.

Outside the build database the login has only what `public` grants on the
server and what `guest` grants in `master`, `tempdb` and `msdb`. It cannot
create databases or logins, and it cannot drop another database (DROP
DATABASE needs `CONTROL`, `ALTER ANY DATABASE`, `dbcreator` or `db_owner` of
the target; source in the research note). `USE [pr-142]` fails with error
916 because the login has no user there and step 1 guarantees `guest` has
no `CONNECT`.

Leftover logins: a killed build can leave `sqlbranch_build_<id>`. `status`
lists it as a build leftover when the build lock `sqlbranch:build:<id>` is
not held. Every `image build` removes such logins in step 2: for each login
matching `sqlbranch_build_[0-9a-f]+` whose build lock is free, it drops the
leftover build database `sqlbranch_build_<id>` if one exists (never running
`sa` DDL inside it), kills the login's sessions, then drops the login. If any
of that fails, the build writes a warning naming the login and goes on
(third review, Q5); `status` keeps listing it. A leftover login that owns any
database other than its own build database is not touched and only warned
about. Logins with any other name, and logins whose build lock is held, are
never touched. `teardown --force` removes the engine container and with it
every login.

Alternatives considered: run scripts as `sa` (rejected: one bad script can
drop branches, first review, Q2); a database user in
`db_owner` (rejected: objects and schemas a script creates can be owned by
that user, and `DROP USER` then fails with error 15138); contained database
users (rejected: needs the server-wide `contained database authentication`
option and changes the image's containment); `EXECUTE AS USER` on the `sa`
connection (rejected: a script can `REVERT`); deny the login in each
guest-enabled database (rejected: it changes users' databases); reset the owner immediately after the scripts
(rejected because background work can still be running before the freeze); disable every database DDL trigger as
`sa` before an owner change (rejected: `DISABLE TRIGGER` is not in the
documented event list, so whether it fires a trigger is unverified, and it
changes the user's triggers); fail every backup with database DDL triggers
when post-scripts are given (rejected: blocks databases with audit triggers,
fourth review, Q2).

### D8. Packaging

`SqlBranch.Testing` is packable; `SqlBranch.Core` stays `IsPackable=false`.
The project reference to Core uses `PrivateAssets="all"`, and a
`TargetsForTfmSpecificBuildOutput` target adds `SqlBranch.Core.dll` (and its
XML docs) to the package's `lib/net10.0/`. Core's own package references
(`Microsoft.Data.SqlClient`, `YamlDotNet`) are repeated in
`SqlBranch.Testing.csproj` so they become the package's dependencies. Core's
types stay public in the assembly but are documented as not a supported API.

Alternatives considered: publish `SqlBranch.Core` as its own package
(rejected: a public API promise too early, decision Q8); merge Core into the
Testing assembly with ILRepack (rejected: breaks debugging and source link);
link Core's source files into the Testing project (rejected: two compiled
copies of the same types).

## JSON output changes

All changes add fields or documents; no field is removed or changes meaning.
Per `docs/cli-output.md`, `schemaVersion` changes only when a field is
removed or changes meaning, so it stays 1.

| Command | Change |
|---|---|
| `branch list` | Each branch gains `ephemeral` (bool), `expiresAt` (UTC or null), `expired` (bool) and `labels` (object). `state` keeps its three values |
| `branch create` | `branch` gains `ephemeral`, `expiresAt` and `labels` |
| `branch prune` | New document: `removed` (names), `skipped` (names), `failed` (objects with `name`, `path`, `message`) |
| `image build`, `image list` | Each image gains `sourceType` (`backup` or `scripts`), `scripts` and `postScripts` (objects with `name`, `sha256`, empty when unused), `ddlTriggers` (names) and `serviceBroker` (`enabled` or `disabled`). `backupFile` and `backupSha256` are null for a script image |
| `status` | Gains `expiredBranches` (names). `branches` stays the total count |
| `skill export` | New document: `directory`, `written` (relative paths) |

New error codes: `script_failed` (1), `skill_exists` (1), `guest_database`
(1), `ddl_trigger` (1). They go in the error-code table of `docs/cli-output.md`. The
`skill export` document carries `written` only on success; on failure
nothing was changed.

## Risks / Trade-offs

- [Leaked branches fill the pool] → Time limits, prune on next run, and
  `status` lists expired branches.
- [Parallel test classes exhaust engine memory] → Task 3.6 measures the
  practical limit and documents it; `MaxConcurrentBranches` caps it.
- [A build login reaches a branch through `guest`] → A source database with
  `guest` enabled and rights granted to `public` would let any login in.
  Script builds always check for `guest` `CONNECT` first and refuse (D7).
  Task 2.6d also tests the unguarded case to confirm the check is needed.
- [A guest-enabled user database blocks script builds] → The error names
  it; the user disables `guest` there or removes it. SqlBranch never changes
  it.
- [Guest check is a snapshot] → A branch or backup build started during a
  long script build is not blocked (third review, Q2). Documented in
  `docs/images.md`.
- [Ownership changes invoke database code] → Check restored handlers before the
  first transfer and check for new, changed or newly enabled triggers after the
  freeze, before the final transfer.
- [Background code outlives the scripts] → D7 step 7 disables the login and
  Service Broker before the checks. Task 2.6b checks activation before sealing and delivery in a
  branch after sealing. The instant an in-flight activation ends remains unmeasured.
- [Owner mismatch inside images] → Fixed for script builds by the owner
  reset after the attach (D7 step 9); `dbo` has the `sa` SID.
- [Script images hide slow builds] → Build output shows time per script.
- [Skill drifts from the tool] → Generated, committed, and checked by a unit
  test.
- [Core shipped inside a package is used as an API] → Documented as
  unsupported; a later change may publish it.

## Open Questions

- Should the fixture offer a per-test reset helper that is faster than a
  full reset for small changes? Does not change the specs here.
