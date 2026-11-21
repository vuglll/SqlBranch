# G4 probe: thin SQL Server clones on a GitHub-hosted runner

Kept here for reference. It first ran from a separate public repo
(`vuglll/sqlbranch-g4-probe`, deleted 2026-11-15) so it could use free
runner minutes; the results are in `docs/research/spike-results.md`. No
workflow is set up in this repo: `workflow.yml.example` is inactive until
copied to `.github/workflows/`. The pool step uses `../vm/pool.sh`.

One question: can a standard `ubuntu-latest` runner create an XFS pool with
reflink, run SQL Server 2022 on it, and make thin database branches?

The workflow runs `probe.sh`, which:

1. records the runner's OS, CPUs, memory and free disk
2. creates a sparse loopback XFS pool with `reflink=1` (`../vm/pool.sh`) and checks
   that `cp --reflink=always` works
3. starts Microsoft's SQL Server 2022 container (Developer edition) with the
   pool mounted, and checks reflink again as the engine's user
4. builds a 1 GB database from a script and seals it as a read-only image
5. creates 5 branches by cloning the image's files and attaching them, then
   checks timing, pool growth, isolation, that the image is unchanged, and
   CHECKDB

Results appear in the run's summary. Nothing here holds secrets or data: the
`sa` password is random for each run and masked in the log.

Running it starts Microsoft's SQL Server image with `ACCEPT_EULA=Y`, which
accepts the SQL Server Developer edition license terms.
