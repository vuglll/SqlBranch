# Image refresh fact checks, 2026-11-19 (re-checked 2026-11-24)

On 2026-11-19 no network or Docker was available and no external source was
checked. On 2026-11-24 the code facts below were re-checked against the
current repository (version 0.2.2 in `Directory.Build.props`, the squashed
initial release). Docker is available on the maintainer's Linux machine, and
the platform spikes run as the first task group of `add-image-refresh` after
the change is approved.

## Prerequisites (checked 2026-11-24 against docs/roadmap.md)

- F1 `add-cloud-backup-source`: archived 2026-11-19
  (`openspec/changes/archive/2026-11-19-add-cloud-backup-source`).
- F3 `add-image-export-import`: archived 2026-11-19
  (`openspec/changes/archive/2026-11-19-add-image-export-import`).
- F2 `add-bacpac-source`: archived 2026-11-20
  (`openspec/changes/archive/2026-11-20-add-bacpac-source`).
- F4 itself needs only "0.1 archived", met 2026-11-17. F4 has no spike gate.
- The follow-up draft `add-bacpac-replacement` in `docs/feature-drafts.md`
  needs "F2 archived and F4 version publication available". F2 is archived, so
  the open prerequisite is F4 itself.

## Platform questions (not verified yet)

| Platform question | Status | Settled by |
|---|---|---|
| What peak space do staged and URL restores, script growth, sealing and retained versions consume on thin XFS and copy pools? Does each out-of-space path preserve the old image? | not verified | Spike 1.1, free-byte samples per phase and actual exhaustion results |
| Can an attached branch keep reading and committing during restore, masking and publication, and how much do latency and throughput change? | not verified | Spike 1.2, baseline versus refresh with the same workload |
| Do same-pool rename, durable writes and engine-session locks yield a single complete catalog after process kill or engine restart on directory and loop-backed pools? Do surviving thin and copied branches retain their rows after unlink? Is the credentials-file rename durable at every kill point? | not verified | Spike 1.3, crash-point and branch-integrity matrix |
| Do cron, a scheduled CI job, launchd and Windows Task Scheduler invoke the job under the intended account, working directory and secret environment, propagate failure and keep secrets out of logs and arguments? | not verified | Spike 1.4. Cron and scheduled CI run on Linux. launchd and Task Scheduler examples may ship marked "not verified" (maintainer decision, 2026-11-24) |

## What a 0.2.2 client does on a pool upgraded to format 2

Source: current repository code, read 2026-11-24.

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

Still to verify with the released 0.2.2 tool (task 2.3): that the observed
message and exit code match points 1 to 5 on a real upgraded pool, and that
0.2.2 `image list` ignores the new fields F4 adds to `branch.json`.
