# Spike: thin clones for SQL Server (Mac with Colima first)

Throwaway scripts that prove, by hand, the one step SqlBranch depends on.
Tasks and gates are in `openspec/changes/spike-thin-clone/tasks.md`. Write what
you see into `docs/research/spike-results.md`.

## What was checked before you run this

- The pool script and the file-cloning step were run on a real XFS loopback
  disk on Linux: a 1 GiB file cloned in about 12 ms, three clones used 0 MiB,
  and writes to one clone left the image and the other clones unchanged.
- **Not checked:** anything involving Colima, Rosetta or SQL Server itself.
  That is what this spike is for. Expect to fix a script or two.

## Requirements

- Apple Silicon Mac with about 80 GB free for the 10 GB database, 150 GB for
  the 50 GB one
- `brew install colima docker sevenzip`

## Steps

```bash
cd spike

./00-colima.sh 150        # start Colima with Rosetta and a 150 GB disk (Linux: see below)
./01-pool.sh 120          # 120 GB XFS pool inside the VM; expect "PROBE: thin"
./02-engine.sh            # SQL Server 2022 container with the pool at /pool
```

Get a database. Brent Ozar's Stack Overflow databases come as data files:
https://www.brentozar.com/archive/2015/10/how-to-download-the-stack-overflow-database-via-bittorrent/
Start with the small one (about 10 GB), then the medium one (about 50 GB).

```bash
mkdir -p ~/sqlbranch-data/so2010
7zz x ~/Downloads/StackOverflow2010.7z -o"$HOME/sqlbranch-data/so2010"

REBUILD_LOG=1 ./03-image.sh so2010 ~/sqlbranch-data/so2010    # copy in, attach, rebuild log, seal
./04-branch.sh dev1 so2010                      # prints clone_ms and attach_ms
./05-verify.sh so2010                           # isolation, hashes, CHECKDB, restart
./06-bench.sh so2010                            # 5 timed creates, growth for 20 branches
```

Connect from the Mac with any SQL client: server `127.0.0.1,14330`, user
`sa`, password in `spike/.sa-password`, database `dev1`.

## What good looks like

| Script | Expect |
|---|---|
| `01-pool.sh` | `reflink=1` and `PROBE: thin` |
| `02-engine.sh` | a version line, then `PROBE: thin (reflink works inside the container)` |
| `04-branch.sh` | `clone_ms` in the tens or hundreds, `pool_growth_mib` near 0 |
| `05-verify.sh` | `RESULT: 7 passed, 0 failed` |
| `06-bench.sh` | every `total_ms` under 10000, growth under 5 percent |

Compare with a full copy any time: `MODE=never ./04-branch.sh dev-copy so2010`.

## x86-64 Linux run (gates G2 and G5, official G1 timings)

Run this on a rented VM; delete the VM when you are done. Expect three to five
hours, most of it the full copies and the two `08-firstwrite.sh` runs.

**1. Rent the VM.** x86-64, 4 or more vCPUs, 16 GB memory, Ubuntu 24.04,
plus a separate empty data disk of at least 150 GB. Write down the provider,
instance type and disk type (local NVMe or network block storage): G2 depends
on the disk.

**2. Copy the scripts and the database from the Mac** (replace `vm` with the
VM's address; the `.sa-password` stays on the Mac, the VM makes its own):

```bash
rsync -av --exclude .sa-password --exclude logs ~/Repos/SqlBranch/spike/ vm:spike/
scp ~/Downloads/StackOverflow2013_201809117.7z vm:
```

**3. On the VM**, keep every output in `spike/logs/`:

```bash
cd ~/spike && mkdir -p logs
./00-linux.sh | tee logs/00.log            # Docker, xfsprogs, 7zip; facts in linux-facts.txt
newgrp docker                              # or log out and in, so docker works without sudo
lsblk                                      # find the empty data disk, e.g. nvme1n1 or sdb
POOL_DEVICE=/dev/<disk> ./01-pool.sh | tee logs/01.log    # formats it only if it is empty
./02-engine.sh | tee logs/02.log
7z x ~/StackOverflow2013_201809117.7z -o"$HOME/so2013"   # Ubuntu's 7zip package has 7z, not 7zz
REBUILD_LOG=1 ./03-image.sh so2013 ~/so2013 2>&1 | grep -v "upgrade step" | tee logs/03.log
./05-verify.sh so2013 2>&1 | tee logs/05.log      # G1 correctness and G5 error log
./06-bench.sh so2013 2>&1 | tee logs/06.log       # official G1 timings
./08-firstwrite.sh so2013 2>&1 | tee logs/08.log  # G2: thin branch vs full copy, 3 runs each
COW_EXTSIZE=8k ./08-firstwrite.sh so2013 2>&1 | tee logs/08-cow8k.log   # same with 8 KiB CoW extents
```

**4. Bring the results back and delete the VM:**

```bash
mkdir -p ~/Repos/SqlBranch/spike/linux-run && scp -r vm:spike/logs vm:spike/linux-facts.txt ~/Repos/SqlBranch/spike/linux-run/
```

`08-firstwrite.sh` prints one line per run, two repeat passes on one branch,
then the medians and `ratio thin/copy`. A ratio near 1.00 means first writes
to shared blocks cost nothing extra; the repeat passes show whether any
extra cost goes away once a branch's blocks are its own. Each line also shows
`pool_growth_mib` (space the writes unshared) and `extents` (how fragmented the
branch's files became). The second run sets XFS's copy-on-write extent size to
one SQL Server page instead of the default 128 KiB; compare the two.

## Things to know

- **Use `REBUILD_LOG=1` for images.** Without it every branch rewrites part
  of the source's log (68 MiB per branch on so2010, 15 percent growth for 20
  branches). With it: 0.18 percent. See `docs/research/spike-results.md`.

- **After a Colima restart**, run `./01-pool.sh` and `./02-engine.sh` again.
  The pool mount does not survive a restart; the data does.
- **Timings on a Mac are not the official numbers.** SQL Server runs under
  Rosetta here, which Microsoft does not test or support. Use the Mac run for
  go or no-go on correctness, and a rented x86-64 Linux VM for gate G2.
- **Gate G3** (Docker Desktop's own VM): `./probe-docker-desktop.sh` prints
  `THIN-OK` or the reason it cannot. For the full run on Docker Desktop:
  `source docker-desktop.env`, then `./01-pool-docker-desktop.sh` instead of
  `00`/`01`, then `02` to `07` as usual in that shell. The engine there is
  `sqlbranch-spike-dd` on port 14331.
- **If `colima ssh` does not pass the script through**, open a shell with
  `colima ssh`, then run `sudo bash` and paste `vm/pool.sh`.

## Cleaning up

```bash
./07-cleanup.sh dev1 verify-a verify-b   # named branches
./07-cleanup.sh --branches               # every branch
./07-cleanup.sh --pool                   # container, pool and all data
colima stop
```

To give the disk space back to the Mac, delete the VM **and** its data disk.
Plain `colima delete` keeps the data disk (`~/.colima/_lima/_disks/colima`),
which holds the pool, so it frees almost nothing:

```bash
colima delete --data --force
```

Rebuilding afterwards means starting again from `./00-colima.sh`.
