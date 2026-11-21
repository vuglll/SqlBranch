# x86-64 Linux release-gate run

Date: 2026-11-19/20 (UTC). Host: Hetzner Cloud CCX33, Helsinki: AMD EPYC-Milan,
8 dedicated vCPU, 30 GiB, 240 GB local disk (ext4 root, `QEMU HARDDISK`), no
extra Volume. Ubuntu 26.04.1 LTS, kernel 7.0.0-30-generic, Docker 29.1.3
(`docker.io`), .NET SDK 10.0.115 (Ubuntu's `dotnet-sdk-10.0`). Checkout: `dev`
at `4b9f7ec`. SQL Server 2022 RTM-CU28 16.0.4325.4, native x86-64.

The spike's section 4 (G1, G2, G5) on the same host is in
[spike-results.md](spike-results.md), with the CLI clone bench on the 50 GB
image (the official 0.1 G1 numbers).

## README quick start on clean Linux (0.1 gate, former 8.2)

Fresh server with only Docker and the .NET 10 SDK installed. The five commands
run as written, with `sqlbranch` as a shell function for
`dotnet run --project src/SqlBranch.Cli --` (the README's alias does the same in
an interactive shell). Source: a 14.8 GB compressed backup of StackOverflow2013.

| Step | Exit | Time |
|---|---|---|
| `dotnet build` | 0 | 3 s (restore done earlier), 0 warnings |
| `sqlbranch init` | 0 | 31 s; managed `loop-xfs` pool, clone mode `thin` |
| `sqlbranch image build so2013 --from-bak so2013.bak` | 0 | 117 s; 5 files, 50.78 GiB |
| `sqlbranch branch create pr-142 --from so2013` | 0 | 3.5 s (includes `dotnet run`) |
| `sqlbranch branch conn pr-142` | 0 | prints the connection string |
| `sqlbranch branch list` | 0 | `pr-142  so2013  thin ... online  288 KiB` |
| `sqlbranch branch rm pr-142` | 0 | 2.8 s |
| `sqlbranch status` | 0 | `Mismatches: none` |

One setting was not default: `SQLBRANCH_POOL_SIZE=120GB`. The default 60 GB
pool is too small for a 50 GB image built from a `.bak` (the Mac run needed
80 GB, see spike-results.md, task 9.5). The README's example (`shop.bak`)
works with the default; a reader with a large database needs to know about
`poolSize`.

## Integration suite on an XFS reflink pool (0.1 gate, former 9.2)

`SQLBRANCH_IT=1 SQLBRANCH_IT_IMAGE=ubuntu:24.04 dotnet test tests/SqlBranch.IntegrationTests -c Release`

142 tests: 133 passed, 3 failed, 6 skipped, 34.6 minutes.

Skipped, all as designed: the copy-mode guard
`Three_branches_from_a_1_GiB_image_grow_the_pool_by_under_48_MiB_is_skipped_not_passed_in_copy_mode`,
the four real-cloud opt-in tests (no `SQLBRANCH_IT_AZURE_*` or `SQLBRANCH_IT_S3_*`
variables), and `A_pool_size_of_100GB_..._on_Docker_Desktop` ("The container
host is 'Ubuntu 26.04.1 LTS', not Docker Desktop"). **No thin-mode test was
skipped.**

Failed:

1. `ManagedPoolIntegrationTests.A_managed_pool_reports_thin_and_leaves_no_helper_container`:
   it found a stopped `sqlbranch-engine` container that this run's quick start
   had left on the host. Passed when rerun after `teardown --force`. Not a
   product failure.
2. `BranchCopyModeIntegrationTests.Branch_list_shows_state_own_size_and_a_removed_image_in_a_copy_pool`:
   **fails again on rerun.** `branch create` on a plain (copy-mode) pool exits
   1: "The engine could not do this step (creating the branch folder), exit
   code 1: chown: changing ownership of '/pool/branches': Operation not
   permitted." `BranchCreator` runs `mkdir -p /pool/branches && chown 10001:0
   /pool/branches` through `docker exec` as the container's default user, and
   that user is `uid=10001(mssql) gid=10001(mssql) groups=10001(mssql)`. A
   process that is not root cannot give a folder to group 0, so on native
   Linux the first branch on a plain pool fails. Docker Desktop for macOS fakes
   ownership on its shared folders, which is why the Mac runs passed.
3. `ScriptPoolFullIntegrationTests.A_seed_script_that_exhausts_a_bounded_copy_pool_is_cleaned_up`:
   **fails again on rerun**, at `Assert.Equal(0, world.RunCli("status").Exit)`
   (line 44): after the expected `script_failed`, `status` exits 1 instead of 0.
   Also a plain pool; the cause was not traced in this run.

Rerun of the three after `teardown --force`: the filter matched 4 cases: 2 failed
(numbers 2 and 3), 2 passed (number 1 among them), 1 min 18 s.

Verdict for the gate item "green with no skipped thin-mode tests": every
thin-mode test passed and none was skipped. The suite is not green, because of
two copy-mode failures that exist only on native Linux. Fixing them is code
work for `dev`, not part of this run.

**Update, 2026-11-20:** both failures are fixed in vuglll/SqlBranch#22 (the
`chown` only runs as root; a 64 MiB build reserve lets cleanup drop the build
database on a full pool). On this host after that fix, the full suite with the
managed loop-xfs pool: 142 tests, 136 passed, 6 skipped by design, 0 failed
(37 min). The gate item is met. The suite was not rerun with
`SQLBRANCH_IT_POOL_BACKEND=plain` on Linux; the copy-mode class was, green
twice.

## Fixture concurrency with the engine capped at 16 GB (0.2 gate, former 3.6)

Recorded as **native Linux, engine capped at 16 GB**, not Docker Desktop: the
maintainer's Mac has 16 GiB of physical memory, so Docker Desktop there cannot
be given 16 GB. After `init`: `docker update --memory 16g --memory-swap 16g
sqlbranch-engine`, then a restart, so SQL Server sized itself to the limit
(`physical_memory_mb=13108`, `committed_target_mb=11934`; Docker reported a
limit of 17,179,869,184 bytes).

Image `shop` from one script: 100 rows, 8.49 MiB. For each N: N `branch create`
processes started at once (the CLI, `dotnet sqlbranch.dll`), then one
`SELECT COUNT(*)` per branch, all N at once, then the branches removed.

| N | Create, all N | Failures | Concurrent queries | Engine memory after | Remove, 16 at a time |
|---|---|---|---|---|---|
| 20 | 7.6 s | 0 | 20/20 in 0.4 s | 0.93 GiB | 5.0 s |
| 50 | 19.8 s | 0 | 50/50 in 0.9 s | 1.08 GiB | 12.8 s |
| 100 | 41.9 s | 0 | 100/100 in 1.8 s | 1.38 GiB | 25.6 s |
| 200 | 87.9 s | 0 | 200/200 in 3.6 s | 1.96 GiB | 50.0 s |
| 400 | 217.3 s | 0 | 400/400 in 7.9 s | 3.41 GiB | 103.6 s |

No limit was reached: 400 live branches used about a fifth of the 16 GB.
Creates are effectively serialized (about 0.4 to 0.55 s each, whatever N), so
the practical limit is how long a test run is willing to wait, not memory. This
measured CLI creates, not `SqlBranch.Testing` fixtures; both use the same
`BranchCreator`, and the integration test
`Twenty_parallel_creates_return_distinct_queryable_branches` passed in 14 s on
this host.

## Image export and import (0.3 gate, former 1.1 and 6.4)

Details in [2026-11-19-image-export-import-facts.md](2026-11-19-image-export-import-facts.md).
Zstd level 1 exported the 50 GB image 2.06 times faster than gzip `Fastest`
and produced a 14 percent smaller archive, meeting design D2's rule for
switching. On 2026-11-19, the maintainer chose zstd level 1 for new exports
and retained gzip support for imports. An image built on the Mac imported on
x86-64 and the reverse, each giving a working branch.
