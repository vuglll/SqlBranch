# Pool format upgrade fact checks, 2026-11-24

Facts for the OpenSpec change `add-pool-format-upgrade`. The section on 0.2.2
clients moved here from
[2026-11-19-image-refresh-facts.md](2026-11-19-image-refresh-facts.md) when
`add-image-refresh` was split on 2026-11-24. Only release 0.2.2 was examined;
0.2.0 and 0.2.1 are assumed to behave the same and are not tested.

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

## Platform questions (not verified yet)

| Platform question | Status | Settled by |
|---|---|---|
| Is the credentials-file rewrite (temp write, chown, chmod, sync, rename, directory sync) complete-old-or-complete-new at every kill point and across an engine restart, on directory and loop-xfs pools? | not verified | Task 1.1, kill matrix |
| Is a session application lock in `master` released promptly when the client holding it is killed? | not verified | Task 1.1 |
| Does 0.2.2 `image list`, `branch list` and `image export` ignore unknown fields in `branch.json`? | not verified | Task 1.2, with the released 0.2.2 tool |

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
| Fixture create throughput on a format-2 pool compared with format 1 | Task 4.6 |
| Is a pool-wide session application lock (`sqlbranch:pool-upgrade`) released when the upgrading client is killed? | Task 1.1, same check as the other session locks |
| What 0.2.0 and 0.2.1 (tool and SqlBranch.Testing) do on an upgraded pool | Task 5.1 |
