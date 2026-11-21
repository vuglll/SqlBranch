# Spike results

Fill this in while running `openspec/changes/spike-thin-clone/tasks.md`.
Paste real output. A failed gate is a result.

## Environment

| Item | Mac run | x86-64 Linux run |
|---|---|---|
| Date | 2026-11-15 | 2026-11-19/20 (UTC) |
| Machine, CPU, memory | Apple M4, 16 GB | Hetzner Cloud CCX33 (Helsinki): AMD EPYC-Milan, 8 dedicated vCPU, 30 GiB, KVM |
| Host OS and kernel | macOS 26.6.2 (25G83), Darwin 25.6.0 | Ubuntu 26.04.1 LTS, kernel 7.0.0-30-generic |
| Colima or Docker version | Colima 0.10.3 (VZ, Rosetta, aarch64, 4 CPU, 8 GiB, 150 GiB disk, virtiofs); Docker server 29.5.2; VM kernel 6.8.0-117-generic | Docker 29.1.3 (Ubuntu `docker.io`), native, no VM |
| SQL Server image tag and version | `mcr.microsoft.com/mssql/server:2022-latest` (sha256:4525e3ef…), SQL Server 2022 RTM-CU28 (KB5125472) 16.0.4325.4 X64, under Rosetta | Same image and build, native x86-64 |
| Pool: file system, size, how created | XFS `reflink=1`, 120 GB loopback file `/mnt/lima-colima/sqlbranch/pool.img` mounted at `/mnt/sqlbranch-pool` in the VM (`01-pool.sh 120`), bind-mounted at `/pool` in the container | XFS `reflink=1`, 190 GB sparse loopback file `/var/lib/sqlbranch/pool.img` (`01-pool.sh 190`) on the server's local disk (240 GB, ext4 root, shown to the guest as `QEMU HARDDISK`), mounted at `/mnt/sqlbranch-pool`. No Hetzner Volume was used, so the pool is XFS in a file on ext4 on a virtual disk |

## Storage step, checked on 2026-11-15 without SQL Server

On a Linux sandbox with a 2 GB XFS loopback pool and stand-in files:

- `cp --reflink=always` of a 1 GiB file took about 12 ms.
- Three clones of a 1 GiB image grew the pool by 0 MiB.
- Writing 50 MiB into one clone grew the pool by 50 MiB, left the image's
  SHA-256 unchanged and left a second clone identical to the image.

This shows the file system side works. It says nothing yet about SQL Server
on those files.

## Mac run, steps 00 to 02

`00-colima.sh 150`, `01-pool.sh 120` and `02-engine.sh` passed on Colima with
Rosetta (reported by the operator on 2026-11-15): the pool reported
`reflink=1` and `PROBE: thin`, and the engine printed its version and
`PROBE: thin (reflink works inside the container)`.

## Gates

| Gate | Question | Mac (Rosetta) | x86-64 Linux | Pass? |
|---|---|---|---|---|
| G1 | 50 GB image: every branch under 10 s, 20 branches under 5 percent growth, isolation holds, CHECKDB clean, restart clean | so2013 (51 GiB, `REBUILD_LOG=1`): every branch 0.46–0.57 s, 7/7 verify, 20 branches grew the pool 17 MiB = 0.03 percent. Earlier on the 10 GB image: so2010 (original 256 MB log): every branch 0.46–0.61 s, 7/7 verify, but 20 branches grew the pool 15.11 percent (fails 5 percent). so2010r (log rebuilt at seal): every branch 0.41–0.58 s, 7/7 verify, 20 branches grew the pool 0.18 percent (passes) | so2013 (50.8 GiB, `REBUILD_LOG=1`): every branch 0.40–0.41 s (`06-bench.sh`), 20 branches grew the pool 17 MiB = 0.03 percent, `05-verify.sh` 7/7. Through the CLI (`tools/bench/clone-bench.sh`): 2.1–2.5 s per `branch create`, process start included, 325 KiB per branch | **Pass** (official numbers: x86-64) |
| G2 | First-write cost on shared blocks, as a ratio to a plain copy | not measured here | Scattered updates (every 10th `dbo.Posts` row, 1.71M rows): median 346 s on a branch against 64 s on a full copy, **ratio 5.44**; with `COW_EXTSIZE=8k` **4.52**. Inserts: ratio 0.91 and 1.03. The cost stays on later passes (fragmentation, not the copy). See the G2 section | Measured (no pass mark was set): thin branches are about 5 times slower for scattered updates on this pool |
| G3 | Thin pool on a Mac: Colima / Docker Desktop | Colima: yes (reflink works in the VM and in the container; branches share data blocks). Docker Desktop 4.99.0 (engine 29.9.3, LinuxKit 7.0.14, aarch64): `probe-docker-desktop.sh` printed `THIN-OK`; full run on so2010 with `01-pool-docker-desktop.sh`: branches 0.37–0.49 s, 20 branches 0.20 percent, 7/7 verify | n/a | Pass on both Colima and Docker Desktop |
| G4 | Thin pool on a GitHub-hosted runner | n/a | `ubuntu-latest` (Ubuntu 26.04.1, 4 CPU, 15 GiB): reflink pool in 0.4 s, engine ready in 12.6 s after a 7.5 s pull, 5 branches of a 1 GiB image in 249–272 ms, 8/8 checks | Pass |
| G5 | Error log free of I/O warnings on pool files | No lines for `I/O`, `FUA`, `stalled`, `Error: 82` after verify on so2010, so2010r and so2013; a scan of all 6 error logs (1530 lines) found none | After every run (verify, bench, both G2 series, 61 database start-ups, 21:44 to 23:10 UTC): no I/O, FUA, stall, `Error: 8xx` or 15-second warnings. The only hit was the expected `Operating system error 2` for the source log that `REBUILD_LOG=1` deletes on purpose | **Pass** |

## Numbers

### Image builds

| Image | Data + log size | copy_ms | seal_ms | Notes |
|---|---|---|---|---|
| so2010 | 8.9 GiB (mdf 9,248,833,536 B + ldf 268,312,576 B; sizes 8820 MB + 255 MB) | 41889 | 2047 | StackOverflow2010.7z (1.17 GB) extracted in 14 s with `7zz`. Attach upgraded the database from version 655 to 957; the mdf grew from 8,980,398,080 B to 9,248,833,536 B in the process. |
| so2010r (`REBUILD_LOG=1`) | 8.7 GiB (mdf 9,248,833,536 B + ldf 516,096 B; log 0.49 MB, 2 VLFs) | 45141 | 2661 | Same source files. After the clean detach the ldf is deleted and the database reattached `FOR ATTACH_REBUILD_LOG`, then detached again. |
| so2013 (`REBUILD_LOG=1`) | 50.8 GiB (1 mdf + 3 ndf at 13,631,488,000 B each, 13000 MB; source log 249 MB, rebuilt to 0.49 MB) | 297135 | 4947 | StackOverflow2013_201809117.7z (10.1 GB) extracted in 96 s. Upgraded from version 655 to 957 at attach. `du` shows 51G. |
| so2013 on x86-64 (`REBUILD_LOG=1`) | 50.8 GiB, same files, log rebuilt to 0.49 MB | 58008 | 3251 | Downloaded straight to the server (10,136,477,638 B) and extracted in 69 s with `7z` (Ubuntu 26.04's `7zip` package has no `7zz`). The extracted folder also holds `Readme.txt`; `03-image.sh` copies only mdf, ndf and ldf. |
| so2010s (`DBCC SHRINKFILE`, abandoned) | 8.9 GiB, log still 255 MB | 43327 | 4358 | Shrink had no effect; image deleted. See Surprises. |

### Branch creation (`06-bench.sh`)

| Image | Run | clone_ms | attach_ms | total_ms |
|---|---|---|---|---|
| so2010 | dev1 (`04-branch.sh`) | 127 | 422 | 549 |
| so2010 | bench-t1 | 127 | 385 | 512 |
| so2010 | bench-t2 | 125 | 333 | 458 |
| so2010 | bench-t3 | 114 | 351 | 465 |
| so2010 | bench-t4 | 127 | 336 | 463 |
| so2010 | bench-t5 | 133 | 330 | 463 |

Every branch, including the four made by `05-verify.sh` (612, 513, 461, 453
ms), reported `pool_growth_mib=68`.

Pool growth for 20 branches: 1371 MiB, 15.11 percent of the image
(`image=so2010 size_gib=8`).

With the rebuilt log (`REBUILD_LOG=1 ./03-image.sh so2010r ...`):

| Image | Run | clone_ms | attach_ms | total_ms |
|---|---|---|---|---|
| so2010r | bench-t1 | 124 | 357 | 481 |
| so2010r | bench-t2 | 125 | 305 | 430 |
| so2010r | bench-t3 | 127 | 279 | 406 |
| so2010r | bench-t4 | 128 | 297 | 425 |
| so2010r | bench-t5 | 134 | 450 | 584 |

Every so2010r branch reported `pool_growth_mib=0`. Pool growth for 20
branches: 15 MiB, 0.18 percent of the image.

50 GB image (`image=so2013 size_gib=50`):

| Image | Run | clone_ms | attach_ms | total_ms |
|---|---|---|---|---|
| so2013 | dev2013 (`04-branch.sh`) | 162 | 360 | 522 |
| so2013 | bench-t1 | 164 | 350 | 514 |
| so2013 | bench-t2 | 166 | 322 | 488 |
| so2013 | bench-t3 | 167 | 340 | 507 |
| so2013 | bench-t4 | 165 | 327 | 492 |
| so2013 | bench-t5 | 157 | 306 | 463 |

Every so2013 branch reported `pool_growth_mib=0`. Pool growth for 20
branches: 17 MiB, 0.03 percent of the image. `clone_ms` rose from about
125 ms (one 9 GB file plus log) to about 165 ms (four 13 GB files plus log).

On x86-64 Linux (the official G1 numbers, `06-bench.sh so2013`):

| Image | Run | clone_ms | attach_ms | total_ms |
|---|---|---|---|---|
| so2013 | bench-t1 | 72 | 334 | 406 |
| so2013 | bench-t2 | 75 | 334 | 409 |
| so2013 | bench-t3 | 74 | 328 | 402 |
| so2013 | bench-t4 | 73 | 329 | 402 |
| so2013 | bench-t5 | 73 | 323 | 396 |

Every branch reported `pool_growth_mib=0`. Pool growth for 20 branches:
17 MiB, 0.03 percent of the image. Cloning the four 13 GB files takes about
74 ms here against about 165 ms on the Mac.

### Verify (`05-verify.sh`)

Second run, after the fix described below:

```
branch=verify-a image=so2010 mode=always clone_ms=121 attach_ms=340 total_ms=461 pool_growth_mib=68
branch=verify-b image=so2010 mode=always clone_ms=125 attach_ms=328 total_ms=453 pool_growth_mib=68
write_ms=3304
PASS  branch verify-a holds 1000000 written rows
PASS  branch verify-b does not see the writes (isolation)
PASS  image files unchanged after branch writes
PASS  CHECKDB clean on written branch
PASS  CHECKDB clean on untouched branch
checkdb_ms=35246
PASS  both branches ONLINE after restart
PASS  written rows survive the restart
RESULT: 7 passed, 0 failed
```

No error log lines matched the I/O search terms.

so2010r (rebuilt 0.49 MB log, so the write had to grow the log):

```
branch=verify-a image=so2010r mode=always clone_ms=120 attach_ms=412 total_ms=532 pool_growth_mib=0
branch=verify-b image=so2010r mode=always clone_ms=154 attach_ms=353 total_ms=507 pool_growth_mib=0
write_ms=3538
checkdb_ms=32966
RESULT: 7 passed, 0 failed
```

All seven checks are the same PASS lines as above; no error log lines
matched the I/O search terms.

so2013 (51 GiB):

```
branch=verify-a image=so2013 mode=always clone_ms=168 attach_ms=395 total_ms=563 pool_growth_mib=0
branch=verify-b image=so2013 mode=always clone_ms=175 attach_ms=325 total_ms=500 pool_growth_mib=0
write_ms=6946
PASS  branch verify-a holds 1000000 written rows
PASS  branch verify-b does not see the writes (isolation)
PASS  image files unchanged after branch writes
PASS  CHECKDB clean on written branch
PASS  CHECKDB clean on untouched branch
checkdb_ms=279668
PASS  both branches ONLINE after restart
PASS  written rows survive the restart
RESULT: 7 passed, 0 failed
```

Whole script, including hashing the 51 GiB image twice: 680 s.

so2013 on x86-64 Linux:

```
branch=verify-a image=so2013 mode=always clone_ms=75 attach_ms=352 total_ms=427 pool_growth_mib=0
branch=verify-b image=so2013 mode=always clone_ms=75 attach_ms=330 total_ms=405 pool_growth_mib=0
write_ms=3647
PASS  branch verify-a holds 1000000 written rows
PASS  branch verify-b does not see the writes (isolation)
PASS  image files unchanged after branch writes
PASS  CHECKDB clean on written branch
PASS  CHECKDB clean on untouched branch
checkdb_ms=131583
PASS  both branches ONLINE after restart
PASS  written rows survive the restart
RESULT: 7 passed, 0 failed
```

so2010, first run (stopped after the restart, see Surprises): verify-a 135 + 477 =
612 ms, verify-b 125 + 388 = 513 ms, `write_ms=3943`, `checkdb_ms=42074`,
the first five checks passed.

### Docker Desktop (G3)

2026-11-15, Docker Desktop 4.99.0 installed with `brew install --cask
docker-desktop` next to Colima. Engine 29.9.3, kernel 7.0.14-linuxkit,
aarch64, 10 CPUs, 7.7 GiB, 453 GB virtual disk.

```
$ ./probe-docker-desktop.sh
...
THIN-OK
```

13 s including the `ubuntu:24.04` pull. A privileged container in Docker
Desktop's VM can install xfsprogs, make a loopback XFS file system with
`reflink=1`, and `cp --reflink=always` on it.

That probe alone did not run SQL Server on the pool, so the full spike was
repeated on Docker Desktop.

How the pool works there (`spike/01-pool-docker-desktop.sh`, settings in
`spike/docker-desktop.env`):

- Docker Desktop takes bind-mount paths as Mac paths unless they are under
  `/var/lib` in its VM: `-v /mnt/...` fails with "The path ... is not shared
  from the host and is not known to Docker".
- The VM has `mount` and `losetup` but no `mkfs.xfs`. A privileged Ubuntu
  container formats `/var/lib/sqlbranch/pool.img` (60 GB sparse,
  `reflink=1`), and a privileged container with `--pid=host` runs
  `nsenter -t 1 -m` to mount it at `/var/lib/sqlbranch/pool` in the VM's
  own mount namespace. Ordinary containers, including the engine, then
  bind-mount it like any VM path.
- Same limitation as Colima: the mount does not survive a Docker Desktop
  restart; rerun the script. Not tested across a Docker Desktop restart.

Second engine `sqlbranch-spike-dd` on port 14331, same image
(16.0.4325.4 CU28 under Rosetta), ready in 24 s including the pull, `PROBE:
thin (reflink works inside the container)`.

| Step | Result |
|---|---|
| `REBUILD_LOG=1 ./03-image.sh so2010` | copy_ms=74517, seal_ms=3382, 8.7 GiB, log 0.49 MB |
| `./04-branch.sh dev1 so2010` | clone 120 + attach 280 = 400 ms, growth 0 MiB |
| `./05-verify.sh so2010` | 7 passed, 0 failed; verify-a 487 ms, verify-b 414 ms, `write_ms=3708`, `checkdb_ms=27989`, no I/O lines in logs 0 and 1 |
| `./06-bench.sh so2010`, 5 timed | 399, 372, 462, 392, 382 ms (clone 105–120, attach 267–342) |
| `./06-bench.sh so2010`, 20 branches | growth 17 MiB, 0.20 percent |

Docker Desktop is as good as Colima for this: thin pool, correctness and
timing all match. Mac users can use either; Docker Desktop needs the
`/var/lib` pool path and the nsenter mount, which the product would have to
do for them.

Side effect to know: starting Docker Desktop made `desktop-linux` the
default Docker context. The spike scripts use the default context, so it
was switched back with `docker context use colima`.

### GitHub-hosted runner (G4)

Ran from a separate public repo, `vuglll/sqlbranch-g4-probe`, to use free
runner minutes (only `probe.sh`, a copy of `spike/vm/pool.sh` and the
workflow; random `sa` password per run). Run 43752287388 on 2026-11-15,
whole job 46 s. That repo and its run logs were deleted the same day; the
probe is kept in `spike/g4-probe/` with its workflow as an inactive example.

| Item | Value |
|---|---|
| Runner | `ubuntu-latest` = Ubuntu 26.04.1 LTS, kernel 7.0.0-1019-azure, 4 CPU, 15 GiB, Docker 29.4.2 |
| Free disk | 88 GiB on `/` and on `/mnt` (same disk); the pool file went to `/var/lib/sqlbranch/pool.img` |
| Pool setup (40 GB sparse XFS, `reflink=1`, probe) | 411 ms, `PROBE: thin` |
| SQL Server image pull / engine ready | 7519 ms / 12607 ms; 16.0.4325.4 CU28, native x86-64 |
| Image built from a script (1 GiB data, rebuilt log) | 10202 ms, 1088 MiB |
| 5 branches: clone + attach | 54+195=249, 57+204=261, 57+215=272, 55+202=257, 59+201=260 ms |
| Pool growth for 5 branches | 3 MiB, 0.29 percent of the image |
| Writes into branch 1 (10k updates, 100k inserts) | 1114 ms |

```
PASS  reflink works on the runner
PASS  reflink works inside the container as the engine's user
PASS  every branch under 10 s (slowest 272 ms)
PASS  pool growth under 5 percent of the image
PASS  branch 1 holds its writes
PASS  branch 2 does not see branch 1's writes
PASS  image files unchanged
PASS  CHECKDB clean on the written branch
RESULT: 8 passed, 0 failed
```

88 GiB free means a CI job can hold an image of roughly 80 GB at most, and
it has to be downloaded or built in every job; the runner keeps nothing.

### First-write cost (G2, x86-64 Linux only)

**Dry run on the Mac (Rosetta, Colima loopback pool), only to test
`08-firstwrite.sh`; not the G2 numbers.** Image so2010r, one run each, two
repeat passes. The workload updates every 10th row of `dbo.Posts` (372,561
rows, scattered over most of the table) and inserts 1M rows into a new table.

| Setting | Thin update_ms | Copy update_ms | Update ratio | Thin growth | Thin extents after | Repeat pass 1 / pass 2 total_ms |
|---|---|---|---|---|---|---|
| XFS default CoW extent (128 KiB) | 51590 | 37169 | 1.39 | 7109 MiB | not recorded | 58278 / 105484 |
| `COW_EXTSIZE=8k` | 39009 | 27899 | 1.40 | 3228 MiB | 531,282 (copy: 6) | 39817 / 121945 |

Insert ratio 1.22 and 1.07. The full copy's update grew the pool 132 MiB.

What this suggests, to check on x86-64:

- **Space:** with the default hint, XFS unshares 128 KiB around every
  written 8 KiB page, so scattered updates cost far more space than the data
  changed: 7.1 GB for 372k row updates on an 8.7 GiB image. An 8 KiB hint
  halved that; what is left is about one page per updated row. Setting the
  hint on `/pool/branches` is cheap and is probably what the product should do.
- **Fragmentation is the bigger risk.** After one scattered update pass the
  branch's files had 531k extents against 6 for the copy, and the second pass
  on the same branch was 2 to 3 times slower than the first, not faster. On
  this loopback disk that is expected; whether it matters on local NVMe or
  network storage is what the x86-64 run must show.
- The first-write ratio itself was about 1.4 both times; with one run each
  under Rosetta that is only a hint.

**x86-64 Linux run (2026-11-19), the G2 numbers.** Image so2013 (50.8 GiB),
`08-firstwrite.sh so2013`, three thin and three full-copy runs interleaved,
OS page cache dropped before each, then two repeat passes on one branch. The
update touches every 10th row of `dbo.Posts` (1,713,976 rows); the insert
adds 1M rows of 200 bytes to a new table. Times in seconds.

| Setting | Thin update (3 runs) | Copy update (3 runs) | Median thin / copy | Update ratio | Insert ratio | Space unshared per thin run | Extents after (thin / copy) | Repeat pass 1 / 2 |
|---|---|---|---|---|---|---|---|---|
| XFS default CoW extent (128 KiB) | 291, 346, 356 | 64, 64, 66 | 346 / 64 | **5.44** | 0.91 | 28–38 GB | 2.64M / 25–39 | 336 / 311 |
| `COW_EXTSIZE=8k` | 407, 295, 290 | 65, 65, 63 | 295 / 65 | **4.52** | 1.03 | about 12–13 GB | 2.74M / 72–404 | 283 / 315 |

`pool_growth_mib` is noisy in this run (several runs show negative growth,
because XFS frees a dropped branch's blocks in the background while the next
run measures), so the space column uses the runs with a clean reading: 37.7,
28.3, 28.1 and 28.6 GB with the default hint, 13.0 and 12.4 GB with 8 KiB.

What it shows:

- **First writes to shared blocks are about 5 times slower than on a plain
  copy for scattered updates** (4.5 times with 8 KiB CoW extents). Appends
  cost nothing extra: the insert ratio is about 1.
- **The cost does not go away once the blocks are unshared (task 4.4).** The
  second repeat pass unshared only 0.2–0.26 GB more and still took 311 and
  315 s, about 5 times the copy. What stays is the fragmentation: about 2.6
  to 2.7 million extents per branch against at most a few hundred for a copy.
  So the slow part is reading and writing a file in millions of pieces, not the
  copy-on-write itself.
- **The 8 KiB hint halves the space** (about 12 GB instead of 28 GB unshared
  for 10 percent of the rows) and helps the time a little.
- The ratio is far worse than the Mac dry run's 1.4. The pool here is XFS
  inside a file on ext4 on a virtual disk, so every XFS extent is also mapped
  through ext4; that extra layer probably makes fragmented access slower than
  on XFS on a raw block device. A run on a Hetzner Volume (or another raw
  disk) formatted as XFS would show how much of the 5 times is this layer.

## Surprises and fixes

- **`05-verify.sh` exited silently after the engine restart.** The state
  query `STRING_AGG(name + '=' + state_desc, ',')` fails with
  `Msg 4191: Cannot resolve collation conflict for string_agg operation`
  (`name` and `state_desc` in `sys.databases` have different collations).
  The error happened inside `STATE=$(...)` under `set -e`, so the script
  exited without printing anything. Both branches were in fact ONLINE.
  Fix: give the expression an explicit `COLLATE DATABASE_DEFAULT`, and fall
  back to `"state query failed"` / `"query failed"` so a failing query shows
  up as a FAIL line instead of a silent exit. Rerun: 7 passed, 0 failed.
- **Each branch costs about 68 MiB, and it is the log file.** `filefrag -v`
  on dev1 in the VM: the mdf has 2,192,772 shared 4 KiB blocks and 20
  unshared (0.1 MiB); the ldf has 48,156 shared and 17,350 unshared
  (67.8 MiB). Attach rewrites about a quarter of the 256 MiB log. Data pages
  stay shared. The cost is per branch and does not scale with the data size,
  so it fails the 5 percent gate on a 9 GiB image (15.11 percent for 20
  branches) but would be about 3 percent on a 50 GB image (if the log is the same size).
- **`DBCC SHRINKFILE` cannot shrink that log.** After the upgrade the log has
  only two VLFs of about 128 MB each, the first one active
  (`log_reuse_wait_desc = NOTHING`, SIMPLE recovery). Three rounds of
  `CHECKPOINT; DBCC SHRINKFILE (2, 1)` left it at 255 MB, because a log
  cannot go below two VLFs.
- **Rebuilding the log fixes the growth.** After the clean detach,
  `03-image.sh` with `REBUILD_LOG=1` deletes the ldf and reattaches the data
  files `FOR ATTACH_REBUILD_LOG`, which creates a 0.49 MB log. Per-branch
  growth went from 68 MiB to 0, 20 branches from 1371 MiB (15.11 percent)
  to 15 MiB (0.18 percent), and `05-verify.sh` still passed 7/7 with
  `write_ms` 3538 against 3304. Rebuilding drops the log chain, which a
  sealed image does not need. The rebuilt log file is named
  `img_tmp_<image>_log.ldf` and is mode 0440 `mssql:mssql`; branches work
  with it as is.
- **The G5 search read the wrong error log.** `05-verify.sh` searched only
  the current log (`sp_readerrorlog 0`), but its own engine restart starts a
  new log, so the writes and CHECKDB it had just done were in log 1 and were
  never searched. Fixed: it now searches logs 0 and 1. Checked by hand
  after the so2013 run: all 6 logs, 1530 lines, contain no line with
  `I/O`, `FUA`, `stalled` or `Error: 8xx`. The search query itself works
  (log 0 returns 16 `Starting up database` lines).
- Attach of the 2018 database ran the upgrade from version 655 to 957 in
  the seal step; attach, upgrade, checkpoint and detach together took 2047 ms (`seal_ms`).

- **x86-64 run: `7zz` is not on Ubuntu 26.04.** The `7zip` package that
  `00-linux.sh` installs provides `7z`, `7za` and `7zr`; the README's
  `7zz x ...` line fails there. `7z x` works the same.
- **x86-64 run: the server got no SSH key at creation.** The key was added by
  hand through Hetzner's web console, which typed `~`, `&`, `>`, `_` and `|`
  wrongly (it assumed a different keyboard layout). Workaround that needs
  none of those characters: `tee` into `/root/.ssh/` with the file name built
  by `printf 'authorized\137keys'` inside backquotes, then paste the key line
  and press Ctrl+D. Tick the key in the create
  form next time.

## Decision

Go / no-go: **go on the Mac (Colima, Rosetta)** for G1, G3 (Colima) and
G5. On the 50 GB image every branch took about half a second, 20 branches
cost 0.03 percent of the image, and isolation, image immutability,
CHECKDB and restart all passed. This holds only with the log rebuilt at
seal time. Still open: the x86-64 Linux run (official timings, G2 first-write
cost). G3 passed on Colima and on Docker Desktop, G4 passed, both on
2026-11-15.

**x86-64 Linux, 2026-11-19: go.** G1 passes with the official numbers (every
branch of the 50.8 GiB image in about 0.4 s, 0.03 percent for 20 branches,
7/7 verify) and G5 passes (no I/O warnings in any error log). G2 is measured
and is a real cost: scattered updates on a thin branch run about 5 times
slower than on a full copy (4.5 with an 8 KiB CoW extent), the cost stays
after the blocks are unshared, and inserts cost nothing extra. That does not
stop the project, because branches are for development, tests and CI, but it
confirms the positioning the README already gives: thin branches for short
lived work, `--copy` for write-heavy work and benchmarks, and `branch reset`
to get rid of fragmentation. Not settled: how much of the 5 times comes from
the pool being a loopback file on ext4 (see the G2 section).

Changes this implies for `docs/roadmap.md`:

- G2: keep thin branches positioned for short-lived work and point
  write-heavy work at `--copy`; state the measured cost in the README instead
  of the dry-run warning. Keep the 8 KiB copy-on-write hint (it halves the
  space). Optional: repeat `08-firstwrite.sh` on XFS on a raw disk.
- Image build must rebuild the log (`FOR ATTACH_REBUILD_LOG` after a clean
  detach) before sealing; shrinking is not enough. Otherwise every branch
  pays for a rewrite of part of the source's log.

## Task 9.5: clone bench on a 50 GB image (Docker Desktop for macOS)

Run on 2026-11-17 with `tools/bench/clone-bench.sh so2013` (5 creates), to check the "every creation under 10 s"
limit on the Mac. **These are not the official G1 numbers.** G1 needs native x86-64 Linux with XFS; this engine ran
under emulation.

- Host: macOS 26.6.2, Apple silicon (arm64), Docker Desktop 4.99.0 (engine 29.9.3, linux/arm64 VM, 10 CPUs, 8 GB).
  The SQL Server 2022 container is linux/amd64, so it ran under emulation (the tool warned about it).
- Isolation: the CliHarness names of an integration test world (`sqlbranch-it-engine-<id>`,
  `sqlbranch-it-pool-<id>`, `/var/lib/sqlbranch/it-<id>`, random port, random password). The real defaults were not
  touched. Everything was removed afterwards.
- Source: the StackOverflow2013 database (4 data files of 13.6 GB and a 262 MB log, 2,465,713 rows in `dbo.Users`),
  extracted in a stream from the 10 GB `.7z` straight into the volume of a throwaway engine, attached
  (upgrade from version 655 to 957, SIMPLE recovery) and backed up with `COMPRESSION, CHECKSUM`.
  Extraction took 11 min, the backup 67 s (14.8 GB `.bak`).
- Pool: **80 GB, not the default 60 GB.** With 60 GB, `image build` stopped with "The pool is too full for the
  restored database files: it needs 51.03 GiB and 40.76 GiB is available" (the 14.8 GB `.bak` also sits in the pool
  during the build). 80 GB worked; a smaller size between 60 and 80 GB was not tried. Everything else was default
  (`loop-xfs`, clone mode `thin`).
- `image build so2013 --from-bak`: 6 min 47 s. Image: 5 files, 50.78 GiB. Pool used after the build: 52.4 GiB.

```
image: so2013, branches: 5, names: bench-1 .. bench-5
pool used before: 52.4 GiB (56231763968 bytes)
create bench-1: 1.904s (clone mode: thin)
create bench-2: 1.453s (clone mode: thin)
create bench-3: 1.448s (clone mode: thin)
create bench-4: 1.496s (clone mode: thin)
create bench-5: 1.544s (clone mode: thin)
pool used after:  52.4 GiB (56233447424 bytes)
pool growth: 1.6 MiB (1683456 bytes) for 5 branches, 328.8 KiB per branch
creates: 5, average 1.57s, total 7.84s
```

- Verdict: every creation took under 10 s (1.45 to 1.90 s) on a 50.78 GiB image, in thin mode, and the pool grew
  about 329 KiB per branch. A separate branch answered `SELECT COUNT(*) FROM dbo.Users` (2,465,713) and
  `dbo.Posts` (17,142,169).
- This is evidence for the Mac, not for the release gate. The official run on native x86-64 Linux XFS is still open.

## Release gate: clone bench on x86-64 Linux (the official 0.1 numbers)

Run on 2026-11-20 on the same Hetzner CCX33 as the spike's section 4, with
the product, not the spike scripts: `dev` at `4b9f7ec`, .NET SDK 10.0.115.
Default settings except `SQLBRANCH_POOL_SIZE=120GB` (managed `loop-xfs` pool,
clone mode `thin`, engine `sqlbranch-engine` on 127.0.0.1:14330). The source
was a `BACKUP ... WITH COMPRESSION, CHECKSUM` of a so2013 branch (14.8 GB, 113
s). `image build so2013 --from-bak`: 117 s, 5 files, 50.78 GiB.

```
image: so2013, branches: 5, names: bench-1 .. bench-5
pool used before: 53.1 GiB (56997855232 bytes)
create bench-1: 2.501s (clone mode: thin)
create bench-2: 2.142s (clone mode: thin)
create bench-3: 2.180s (clone mode: thin)
create bench-4: 2.217s (clone mode: thin)
create bench-5: 2.121s (clone mode: thin)
pool used after:  53.1 GiB (56999518208 bytes)
pool growth: 1.6 MiB (1662976 bytes) for 5 branches, 324.8 KiB per branch
creates: 5, average 2.23s, total 11.16s
```

`SQLBRANCH` was `dotnet run --no-build -c Release --project src/SqlBranch.Cli --`,
so each time includes starting the .NET host through `dotnet run`. Verdict:
every creation is under 10 s on native x86-64 Linux XFS. This is the release
gate's G1 check.

