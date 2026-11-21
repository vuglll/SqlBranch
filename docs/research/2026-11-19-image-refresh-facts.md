# Image refresh fact checks, 2026-11-19

No network or Docker was available. No external source was checked. Existing F1 and approved F3 requirements are inputs to the recipe. Their cloud release gates remain separate.

| Platform question | Status | Evidence to collect before relying on it |
|---|---|---|
| What peak space do staged and URL restores, script growth, sealing and retained versions consume on thin XFS and copy pools? Does each out-of-space path preserve the old image? | not verified (no network in this run) | Spike 1.1, free-byte samples per phase and actual exhaustion results |
| Can an attached branch keep reading and committing during restore, masking and publication, and how much do latency and throughput change? | not verified (no network in this run) | Spike 1.2, baseline versus refresh with the same workload |
| Do same-pool rename, durable writes and engine-session locks yield a single complete catalog after process kill or engine restart on directory and loop-backed pools? Do surviving thin and copied branches retain their rows after unlink? | not verified (no network in this run) | Spike 1.3, crash-point and branch-integrity matrix |
| Do the four external schedulers invoke the job under the intended account, working directory and secret environment, propagate failure and keep secrets out of logs and arguments? | not verified (no network in this run) | Spike 1.4, redacted smoke runs on representative hosts |

On 2026-11-19, the pre-F4 source at 9a52575 showed that `image rm` reads
the pool credentials before taking the image lock and renaming the directory.
The reader requires a top-level password string and raises `operation_failed`
if that key is absent. Task 2.1 verifies the proposed format change against
the released client. [ImageRemover](../../src/SqlBranch.Core/Images/ImageRemover.cs)
and [EngineCredentials](../../src/SqlBranch.Core/Engine/EngineCredentials.cs).
