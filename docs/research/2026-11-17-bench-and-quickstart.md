# Bench and quick-start runs (tasks 8.1 and 8.2)

Evidence for task 8.1 (`tools/bench/clone-bench.sh`) and the macOS part of task 8.2
(the README quick start). Nothing here needs a secret; the bench and the commands
below print none, and the password of each run was random and is not recorded.

## Status

- 8.1: done. The script ran against a small image, in thin mode, five times.
- 8.2: **open.** The quick start was run on macOS only. The task asks for a clean
  Linux machine, and that check has not been done. Until it is, 8.2 stays unticked.

## Host

Docker Desktop on macOS, arm64 (the engine ran under emulation: the tool warned that the
container host is aarch64, not x86-64 Linux). Every run used the isolated names of the
CliHarness (`sqlbranch-it-engine-<id>`, `sqlbranch-it-pool-<id>`,
`/var/lib/sqlbranch/it-<id>`, a random port), never the real defaults. All of it was
removed afterwards: no container, volume or folder was left.

## Earlier runs (2026-11-16 and 2026-11-17, from the implementer's report)

Quick start, run as in the README with the isolated harness standing in for
`sqlbranch`: `init`, `image build shop --from-bak shop.bak`, `branch create pr-142 --from
shop`, `branch conn pr-142`, a query through the connection, `branch rm pr-142`,
`teardown --force`. Clone mode `thin`. `SELECT COUNT(*)` on the branch's table returned
100. Five creations in a bench run took 1.39, 1.21, 1.28, 1.17 and 1.18 s; the pool
grew about 281 KiB per branch.

## Re-run on 2026-11-17 (output captured)

Image `shop`: 2 files, 8.49 MiB, built from a small backup made in the engine (a
`Shop` database with `dbo.Item`, 100 rows). Command:
`SQLBRANCH="dotnet tests/SqlBranch.CliHarness/bin/Release/net10.0/SqlBranch.CliHarness.dll" tools/bench/clone-bench.sh shop 5 bench`

```
image: shop, branches: 5, names: bench-1 .. bench-5
pool used before: 209.4 MiB (219607040 bytes)
create bench-1: 1.336s (clone mode: thin)
create bench-2: 1.205s (clone mode: thin)
create bench-3: 1.162s (clone mode: thin)
create bench-4: 1.277s (clone mode: thin)
create bench-5: 1.412s (clone mode: thin)
pool used after:  210.8 MiB (221052928 bytes)
pool growth: 1.4 MiB (1445888 bytes) for 5 branches, 282.4 KiB per branch
creates: 5, average 1.28s, total 6.39s
removed bench-1
removed bench-2
removed bench-3
removed bench-4
removed bench-5
```

The times are the same as before within noise, about 1.2 to 1.4 s per create, well under
the 10 s release limit, and the pool grew about 282 KiB per branch. This is a small image
on an emulated engine; it says nothing about the 50 GB release check, which still needs
a large image on suitable hardware.

## Still open

- Quick start on a clean Linux machine (task 8.2).
- The 50 GB bench on real hardware.
