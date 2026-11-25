# Pool format upgrade fact checks, 2026-11-24

Facts for the OpenSpec change `add-pool-format-upgrade`. The section on 0.2.2
clients moved here from
[2026-11-19-image-refresh-facts.md](2026-11-19-image-refresh-facts.md) when
`add-image-refresh` was split on 2026-11-24. Only the source of release 0.2.2
was examined while writing the proposal; 0.2.0 and 0.2.1 are run against an
upgraded pool by task 5.1, which records their transcripts here.

## Prerequisites (checked 2026-11-24 against docs/roadmap.md)

- "0.2 archived": met 2026-11-18 (`add-test-fixtures`).
- No spike gate. Task group 1 of the change settles the platform questions
  below before any code relies on them.

## Released packages (checked 2026-11-24)

Both lists were read by the orchestrator and again while writing this note,
on 2026-11-24:

- https://api.nuget.org/v3-flatcontainer/sqlbranch/index.json lists
  `0.2.0`, `0.2.1`, `0.2.2`.
- https://api.nuget.org/v3-flatcontainer/sqlbranch.testing/index.json lists
  `0.2.0`, `0.2.1`, `0.2.2`.

So the old-client tests (task 5.1) can install the released 0.2.2 tool and
package from NuGet.

## Platform checks (task group 1, run 2026-11-24)

Environment: Linux 6.18.44 container (ext4 root, 1 vCPU class VM), .NET SDK
10.0.115, Docker daemon started by hand, engine image
`mcr.microsoft.com/mssql/server:2022-latest` (SQL Server 16.0.4325.4
Developer), SqlBranch built from commit 435ae55 (0.2.2 code path), released
tool `sqlbranch` 0.2.2 from NuGet. Passwords never printed; outputs below were
filtered.

| Platform question | Status | Settled by |
|---|---|---|
| Is the credentials-file rewrite complete-old-or-complete-new at every kill point and across an engine restart? | verified on a directory pool in copy mode and on a loop-xfs pool in thin mode (rerun 2026-11-24, see below) | Task 1.1 |
| Is a session application lock in `master` released promptly when the client is killed? | verified for a killed client process: free in about 0.1 s (both runs) | Task 1.1 |
| Does 0.2.2 `image list`, `branch list` and `image export` ignore unknown fields in `branch.json`? | verified | Task 1.2 |

### Pools used

- Directory pool (`poolBackend plain`, `pool` /tmp/sbpool on ext4): clone mode
  copy (the clone probe finds no block sharing). This is the copy-mode pool.
- loop-xfs pool: NOT possible in this container. `sqlbranch init` with
  `poolBackend loop-xfs` exits with: "the mount step failed: nsenter: cannot
  open /proc/1/ns/mnt: Permission denied" (no fallback by design). A manual
  attempt confirmed the host also lacks `mkfs.xfs`, and `mount -o loop` of an
  image failed ("wrong fs type"; /dev/loop0 exists but cannot be attached).
  So no thin-mode (loop-xfs) pool was checked. The kill matrix therefore does
  not cover XFS. The credentials write is plain POSIX (write, rename on one
  directory), but this note makes no claim for XFS: rerun task 1.1 on the
  maintainer's machine for a loop-xfs pool if D2's "not verified" sentence is
  to be dropped for XFS. Thin-mode results are not reported as passed.

### 1.1 Kill matrix, credentials rewrite (directory pool, copy mode)

Method: the D2 sequence was run in a helper container (engine image,
`--user 0`, pool bind-mounted at /pool, password through stdin only):
`umask 077; cat > credentials.json.new; chown 10001:0; chmod 600; sync <tmp>;
mv -f <tmp> credentials.json; sync /pool/engine`. The helper was held after
step N and killed with `docker kill` (SIGKILL), then the file was checked,
the engine container restarted (`docker restart sqlbranch-engine`), the file
checked again, and the full sequence rerun after deleting a leftover
`credentials.json.new`. "Old" is the format-1 content, "new" the format-2
content of D2. Script kept only in the scratchpad.

| Killed after | Temp file left | credentials.json after kill | after engine restart | rerun |
|---|---|---|---|---|
| 0 partial write (10 bytes of 184, stdin held open) | yes, mode 600 owner 0:0, 10 bytes | old, mode 600, owner 10001:0 | old, 600, 10001:0 | exit 0, new, 600, 10001:0 |
| 1 temp write | yes, 600 0:0, 184 bytes | old, 600, 10001:0 | same | exit 0, new, 600, 10001:0 |
| 2 chown | yes, 600 10001:0 | old, 600, 10001:0 | same | exit 0, new, 600, 10001:0 |
| 3 chmod | yes, 600 10001:0 | old, 600, 10001:0 | same | exit 0, new, 600, 10001:0 |
| 4 sync | yes, 600 10001:0 | old, 600, 10001:0 | same | exit 0, new, 600, 10001:0 |
| 5 rename | none | new, 600, 10001:0 | same | exit 0, new, 600, 10001:0 |
| 6 directory sync | none | new, 600, 10001:0 | same | exit 0, new, 600, 10001:0 |

Every row parsed as complete JSON, equal to the whole old or whole new
content. A kill never left a mix. The leftover temp file survives an engine
restart (the rerun deleted it before writing; leftover temp files are what
`status` must list, D11). A first attempt of the matrix ran the helper as the
image's default user (`mssql`, 10001), where `chown 10001:0` fails; the
real write path must run the helper as root, as the existing `PoolWork`
helper does. That run is discarded.

Limits: this is a process kill (SIGKILL of the writer container). It is not a
power-loss or kernel-crash test; the effect of `sync` on durability was not
observable here.

### 1.1 Kill matrix rerun on a loop-xfs pool (thin mode)

Run 2026-11-24 on a Mac (Apple silicon) with Docker Desktop 29.9.3, engine
image `mcr.microsoft.com/mssql/server:2022-latest` (SQL Server 16.0.4325.4
Developer, amd64 under emulation), SqlBranch built from branch head 10bf291.
`sqlbranch init` with `poolBackend loop-xfs`, `poolSize 4GB`: clone mode thin,
pool file `/var/lib/sqlbranch/pool.xfs`, mount `/var/lib/sqlbranch/mnt`
(`stat -f` reports `xfs`). Same method as the directory pool above: helper
container as root with the mount at /pool, held after step N, `docker kill`,
check, `docker restart sqlbranch-engine`, check, delete the temp file and
rerun. Old content 59 bytes (format 1), new 102 bytes (format 2). Only
SHA-256 comparisons, modes and owners were printed. The pool was removed with
`teardown --force` afterwards.

| Killed after | Temp file left | credentials.json after kill | after engine restart | rerun |
|---|---|---|---|---|
| 0 partial write (10 bytes) | yes, 600 0:0, 10 bytes | old, 600, 10001:0 | same, temp file still there | done, new, 600, 10001:0 |
| 1 temp write | yes, 600 0:0, 102 bytes | old, 600, 10001:0 | same | done, new, 600, 10001:0 |
| 2 chown | yes, 600 10001:0 | old, 600, 10001:0 | same | done, new, 600, 10001:0 |
| 3 chmod | yes, 600 10001:0 | old, 600, 10001:0 | same | done, new, 600, 10001:0 |
| 4 sync | yes, 600 10001:0 | old, 600, 10001:0 | same | done, new, 600, 10001:0 |
| 5 rename | none | new, 600, 10001:0 | same | done, new, 600, 10001:0 |
| 6 directory sync | none | new, 600, 10001:0 | same | done, new, 600, 10001:0 |

Same result as the directory pool: every kill left the whole old or the whole
new content, never a mix. The same limits apply (process kill, not power loss).

Lock release on the same engine: a `sqlcmd` session took
`sqlbranch:image:probe` and `sqlbranch:pool-upgrade` (session owner, timeout
0) and waited; `APPLOCK_TEST` confirmed both held; the holder was killed with
`kill -9` inside the engine container while a second session polled every
20 ms. Both locks were free 102, 30 and 31 ms after the kill (three runs).

### 1.1 Session application locks released on client kill

A `sqlcmd` session in `master` took two session-owned exclusive application
locks with `sp_getapplock ... @LockOwner = 'Session', @LockTimeout = 0`:
`sqlbranch:image:probe` (image-lock name form) and `sqlbranch:pool-upgrade`,
then waited. A second session polled `APPLOCK_TEST` for both every 20 ms and
printed the engine time when both were free. The holder was killed with
`kill -9` (SIGKILL; the OS closes the socket). Four runs, kill command issued
to free, including about 80 ms of `docker exec` start-up in the measurement:
about 0.08 s, 0.10 s, 0.09 s, 0.09 s. So the locks are free within about
0.1 s after the client process dies. Not measured: a client that disappears
without closing its connection (host power loss, network partition); there
the engine releases the lock only when it notices the dead connection, which
can take much longer. Design text that relies on "free within N seconds"
should say it holds for a killed process, not for a lost network.

### 1.2 Released 0.2.2 against a branch with D5 fields

`dotnet tool install --tool-path <dir> SqlBranch --version 0.2.2`, reported
version `0.2.2`. Format-1 pool (directory, copy mode), image `tiny` built from
one SQL script (current build, 8.49 MiB), branch `b1` created from it. The
`branch.json` of `b1` had `imageVersion` ("tiny@20261124T134338000Z") and
`baseKnown` (true) added by hand (D5 fields). Then, with 0.2.2:

| Command | Exit | Result |
|---|---|---|
| `image list` | 0 | one row, tiny, 1 branch |
| `branch list` | 0 | one row, b1, image tiny, online |
| `image list --format json` | 0 | one image, `branches` 1, `branchNames` ["b1"], no new fields |
| `branch list --format json` | 0 | b1, `imageRemoved` false, no `imageVersion` field in the output |
| `image export tiny --to <file>.sbimg` | 0 | 341586-byte archive written (2 files, 8.49 MiB) |

(The option is `--to`; a first call with `--out` and one with a positional
path failed with exit 2 for the syntax only.) 0.2.2 ignores the unknown
`branch.json` fields and does not print them. D5's "not verified yet" is now
verified for 0.2.2 `image list`, `branch list` and `image export`; D5 needs
no revision.

## What a 0.2.2 client does on a pool upgraded to format 2

Source: current repository code (version 0.2.2 in `Directory.Build.props`),
read 2026-11-24.

1. `EngineCredentials.ReadAsync`
   ([src/SqlBranch.Core/Engine/EngineCredentials.cs](../../src/SqlBranch.Core/Engine/EngineCredentials.cs))
   parses `/pool/engine/credentials.json` and calls
   `GetProperty("password").GetString()` on the root object. A file without a
   top-level string `password` raises `operation_failed` (exit 1) with the
   message `The engine credentials file in <pool> is not valid.`, where
   `<pool>` is the pool directory path or `Docker volume <name>`
   (`PoolLocation.ToString`). The message does not mention upgrading, and a
   released client cannot be changed.
2. `ImageRemover.RemoveAsync`
   ([src/SqlBranch.Core/Images/ImageRemover.cs](../../src/SqlBranch.Core/Images/ImageRemover.cs))
   reads the credentials (line 49) before it takes the image lock (line 57)
   and before the `mv -T` that unlists the image (line 79). So 0.2.2
   `image rm`, with or without `--force`, fails on a format-2 pool before
   touching any file.
3. The same reader runs first in 0.2.2 `init` on an existing pool, `engine
   start`, `status`, every `image build` source, `image import`, `image rm`,
   and every branch command through `BranchEngine.OpenAsync`
   ([src/SqlBranch.Core/Branches/BranchEngine.cs](../../src/SqlBranch.Core/Branches/BranchEngine.cs))
   and `BranchLister`. All of them exit 1 with the message above.
4. 0.2.2 `image list` and `image export` do not read the credentials. They read
   `/pool/images/<name>/manifest.json` and the branch records directly
   (`ImageCatalog`, `ImageExporter`). `doctor`, `engine stop`, `teardown` and
   `skill export` do not read them either. These keep working on a format-2
   pool and are not blocked.
5. SqlBranch.Testing 0.2.x
   ([src/SqlBranch.Testing/FixtureHost.cs](../../src/SqlBranch.Testing/FixtureHost.cs))
   opens the engine through `BranchEngine.OpenAsync`. Its create, reset and
   dispose fail; `FixtureHost.Failure` turns the error into an exception whose
   message is the same text, with no fix command, because only
   `not_initialized`, `engine_stopped` and `image_not_found` get one. The
   availability check catches every exception and returns `false`, so tests
   that skip on it are skipped instead of failing.
6. 0.2.2 checks the pool marker `/pool/.sqlbranch-pool` only for existence
   (`[ -f ... ]` in
   [src/SqlBranch.Core/Pools/DirectoryPool.cs](../../src/SqlBranch.Core/Pools/DirectoryPool.cs),
   line 53), never its format version. A new marker version would not stop a
   0.2.2 writer. Only the credentials file does.
7. Every 0.2.2 writer takes a non-waiting session application lock in
   `master` (`sqlbranch:image:<name>` or `sqlbranch:branch:<name>`,
   [src/SqlBranch.Core/Engine/EngineLocks.cs](../../src/SqlBranch.Core/Engine/EngineLocks.cs))
   after it reads the credentials. A 0.2.2 command that read the file just
   before the upgrade rewrote it can still take a lock afterwards. No
   mechanism lets SqlBranch list running 0.2.2 processes.

Still to verify with the released 0.2.2 tool and SqlBranch.Testing 0.2.2
(task 5.1 of `add-pool-format-upgrade`): that the observed message and exit
code match points 1 to 5 on a real upgraded pool. Whether 0.2.2 `image list`
ignores the new fields in `branch.json` is task 1.2.

## Facts checked for the second revision (2026-11-24)

Source: repository code at commit b6a8b1f (`Directory.Build.props` version
0.2.2; `git diff 6eaa894 HEAD` shows no change to the files below since the
0.2.2 release commit), read 2026-11-24. The repository has no source for
0.2.0 or 0.2.1 (no tags; the first public commit is 0.2.2), so their behavior
is settled only by task 5.1, which installs all three versions from NuGet
(maintainer decision 10).

1. **Credentials read and write.** `EngineCredentials.ReadAsync`
   ([src/SqlBranch.Core/Engine/EngineCredentials.cs](../../src/SqlBranch.Core/Engine/EngineCredentials.cs),
   line 33) runs `PoolWork.RunAsync` with `readOnly: true`: a short-lived work
   container with the pool mounted, not the engine. `WriteAsync` (lines 70-72)
   writes in place: `umask 077; cat > credentials.json; chown; chmod 600`.
   There is no temporary file, sync or rename today, so the format-2 rewrite
   is a new write path.
2. **Engine state is checked before the credentials.** 0.2.2 `status` returns
   `engine_stopped` (exit 3) from the container state without reading the
   credentials
   ([src/SqlBranch.Cli/Commands/StatusCommand.cs](../../src/SqlBranch.Cli/Commands/StatusCommand.cs),
   lines 36-39). Branch commands (`BranchEngine.OpenAsync` lines 55-56,
   `BranchLister` lines 98-99), `image rm` (`ImageRemover` lines 47-49),
   `image build` and `image import` (`ImageCommands.cs` line 154,
   `ImageCommands.Bacpac.cs` line 51) call `ImageCatalog.RequireRunningEngine`
   before `EngineCredentials.ReadAsync`. `engine start`
   (`EngineStarter.cs` line 104) and `init` on an existing pool
   (`EngineInitializer.cs` line 140) read the credentials themselves. So on an
   upgraded pool, 0.2.2 commands exit 1 with the credentials message only while
   the engine runs; with the engine stopped they report `engine_stopped` as
   before, except `engine start` and `init`.
3. **Build time in the manifest.** `ImageManifest` stores `builtAt`
   ([src/SqlBranch.Core/Images/ImageManifest.cs](../../src/SqlBranch.Core/Images/ImageManifest.cs),
   line 33). A build sets it from the client's clock, `_time.GetUtcNow()`
   ([src/SqlBranch.Core/Images/ImageBuilder.cs](../../src/SqlBranch.Core/Images/ImageBuilder.cs),
   line 634), for every source (backup file, URL, scripts, BACPAC). An import
   keeps the archive's value: `state.Manifest = source with { Name = state.Target }`
   ([src/SqlBranch.Core/Images/ImageImporter.cs](../../src/SqlBranch.Core/Images/ImageImporter.cs),
   line 262), so an imported image's `builtAt` is the producer's build time,
   at or before the local import. A branch's `createdAt` also comes from the
   client's clock (`BranchCreator.cs` line 117). `manifest.json`'s
   modification time instead comes from the engine container's clock, which
   on macOS and Windows is a VM clock that can drift from the host.
4. **Fixture fix text.** `FixtureHost.Failure`
   ([src/SqlBranch.Testing/FixtureHost.cs](../../src/SqlBranch.Testing/FixtureHost.cs),
   line 101) maps `image_not_found` to the fix `sqlbranch image build <image>`
   and appends "Run '<fix>'." unless the message already holds it. For a
   removed or unknown base that fix is wrong (the fix is a new branch), so the
   reset path must override it.
5. **Availability check.** `FixtureHost.IsAvailableAsync` (lines 34-43) opens
   `BranchEngine.OpenAsync` and returns false on any exception; it writes no
   log line today.
6. **Boolean options accept values.** Checked by running the 0.2.2 Release
   build (`src/SqlBranch.Cli/bin/Release/net10.0/sqlbranch.dll`, System.CommandLine
   3.0.0) on 2026-11-24: `sqlbranch skill export --force=true`,
   `--force true` and `--force --force` all exit 0; `--force=yes` exits 2
   `usage_error`. Refusing `pool upgrade --yes=true`, `--yes true` and
   `--yes --yes` therefore needs custom parsing and its own test.

Still not verified, and settled by tasks of the change:

| Question | Settled by |
|---|---|
| Can `docker exec` in the running engine read the 600, uid-10001 credentials file, so the format re-read after locking needs no extra work container? | Task 4.6 (falls back to a work container if not) |
| Is a pool-wide session application lock (`sqlbranch:pool-upgrade`) released when the upgrading client is killed? | Task 1.1, same check as the other session locks |
| What 0.2.0 and 0.2.1 (tool and SqlBranch.Testing) do on an upgraded pool | Task 5.1 |

Measured by the orchestrator on 2026-11-25 (Docker Desktop, macOS arm64): fixture create plus dispose, median of 20 sequential runs.

| Pool backend | Format 1 | Format 2 |
|---|---|---|
| `loop-xfs` (thin) | 1950 ms | 1964 ms |
| `plain` (copy) | 2220 ms | 2314 ms |

Format 2 is not more than 10% slower on either backend.
