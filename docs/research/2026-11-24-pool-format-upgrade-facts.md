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
