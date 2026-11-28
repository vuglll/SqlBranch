# Image refresh fact checks, 2026-11-19 (re-checked 2026-11-24)

On 2026-11-19 no network or Docker was available and no external source was
checked. On 2026-11-24 the code facts (now in the pool-format note linked below) were
re-checked against the current repository (version 0.2.2 in `Directory.Build.props`, the squashed
initial release). Docker is available on the maintainer's Linux machine, and
the platform spikes run as the first task group of `add-image-refresh` after
the change is approved. `add-image-refresh` now depends on
`add-pool-format-upgrade` (split out 2026-11-24), archived 2026-11-28.

## Prerequisites (checked 2026-11-24 against docs/roadmap.md)

- F1 `add-cloud-backup-source`: archived 2026-11-19
  (`openspec/changes/archive/2026-11-19-add-cloud-backup-source`).
- F3 `add-image-export-import`: archived 2026-11-19
  (`openspec/changes/archive/2026-11-19-add-image-export-import`).
- F2 `add-bacpac-source`: archived 2026-11-20
  (`openspec/changes/archive/2026-11-20-add-bacpac-source`).
- F4 needs "0.1 archived", met 2026-11-17, and since 2026-11-24
  `add-pool-format-upgrade` archived, met 2026-11-28. F4 has no spike
  gate.
- The follow-up draft `add-bacpac-replacement` in `docs/feature-drafts.md`
  needs "F2 archived and F4 version publication available". F2 is archived, so
  the open prerequisite is F4 itself.

## Platform questions (not verified yet)

| Platform question | Status | Settled by |
|---|---|---|
| What peak space do staged and URL restores, script growth, sealing and retained versions consume on thin XFS and copy pools? Does each out-of-space path preserve the old image? | not verified | Spike 1.1, free-byte samples per phase and actual exhaustion results |
| Can an attached branch keep reading and committing during restore, masking and publication, and how much do latency and throughput change? | not verified | Spike 1.2, baseline versus refresh with the same workload |
| Does SQL Server queue a new shared application-lock request behind a waiting exclusive one, and does a publisher that polls with `@LockTimeout = 0` ever delay a new create? | not verified (the `sp_getapplock` documentation on learn.microsoft.com was not reachable from the review session on 2026-11-24) | Spike 1.2, three-session lock test |
| Do same-pool rename, durable writes and engine-session locks yield a single complete catalog after process kill or engine restart on directory and loop-backed pools? Do surviving thin and copied branches retain their rows after unlink? | not verified | Spike 1.3, crash-point and branch-integrity matrix (the credentials-file rename moved to task 1.1 of `add-pool-format-upgrade`) |
| Do cron, a scheduled CI job, launchd and Windows Task Scheduler invoke the job under the intended account, working directory and secret environment, propagate failure and keep secrets out of logs and arguments? | not verified | Spike 1.4. Cron and scheduled CI run on Linux. launchd and Task Scheduler examples may ship marked "not verified" (maintainer decision, 2026-11-24) |

## Moved on 2026-11-24

The section "What a 0.2.2 client does on a pool upgraded to format 2", and the
credentials-file rename from spike 1.3, moved to
[2026-11-24-pool-format-upgrade-facts.md](2026-11-24-pool-format-upgrade-facts.md)
when `add-pool-format-upgrade` was split out of this change. Only release
0.2.2 was examined there. Its task 1.2 checks that 0.2.2 `image list` ignores
the new fields in `branch.json`, and its task 5.1 records what the released
0.2.2 tool and SqlBranch.Testing 0.2.2 print on an upgraded pool.
