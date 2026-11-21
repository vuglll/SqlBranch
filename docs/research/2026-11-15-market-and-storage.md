# Research notes: market and storage (checked 2026-11-15)

Facts here were checked on 2026-11-15 unless marked otherwise. Re-check before
relying on them in a proposal.

## The gap

| Fact | Source |
|---|---|
| DBLab Engine gives Postgres thin clones and branching, Apache-2.0, using ZFS or LVM copy-on-write | https://postgres.ai/docs/database-lab |
| DBLab reports a 1 TiB database cloning in about 10 seconds | https://postgres.ai/blog/20250724-full-stack-preview-environments-with-dblab |
| DBLab has a paid edition from $62 a month (third-party summary; confirm on postgres.ai) | https://dev.co/devops/open-source/database-lab-engine |
| Redgate SQL Clone is Windows-based and SQL Server only; its successor Redgate Clone runs on Kubernetes | https://www.red-gate.com/hub/product-learning/flyway/provisioning-multi-database-environments/ |
| Windocks (now LangGrant) clones SQL Server databases with Windows virtual disks and differencing disks | https://langgrant.com/blog/sql-server-database-cloning-for-sql-containers-and-instances/ |
| dbaclone is a free PowerShell module for SQL Server clones; it needs Windows | https://github.com/dataplat/dbaclone |
| Testcontainers.MsSql shows 36.5M downloads on its publisher profile | https://nubrowse.com/profiles/HofmeisterAn/ |
| Databricks describes database branching as increasingly important for AI agents | https://databricks.com/blog/database-branching |
| A review of static masking tools found no open-source tool covering it end to end across engines; greenmask covers Postgres | https://bytebase-website.pages.dev/blog/static-data-masking-tools/ |

Not found in the search: an open-source, cross-platform, container-based
branching tool for SQL Server. That is an absence of evidence, not proof. Search
again before launch.

## Storage facts the design rests on

| Fact | Source |
|---|---|
| SQL Server on Linux supports XFS and ext4 for data files; other file systems such as BTRFS are not supported | https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-setup |
| Microsoft recommends XFS for data and log files | https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-performance-best-practices |
| SQL Server container images are supported only on Linux hosts with x86-64 CPUs | https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-setup |
| On XFS, `cp --reflink` makes a copy that shares blocks and is created at once whatever the file size; blocks are copied only when written | https://docs.oracle.com/en/operating-systems/oracle-linux/8/xfs/xfs-CreatinganXFSFileSystemWiththeReflinkFeature.html |
| The same reflink approach is used to clone whole Postgres data directories | https://vonng.com/en/pg/pg-clone/ |
| On Windows, SQL Server supports NTFS and ReFS volumes for database files | https://learn.microsoft.com/sk-sk/troubleshoot/sql/database-engine/install/windows/support-policy-sql-server |
| ReFS block cloning is used by ordinary Windows copy operations from Windows 11 24H2 and Windows Server 2025 | https://learn.microsoft.com/en-us/windows-server/storage/refs/block-cloning |
| Dev Drive allows at most 8176 clones of one data block | https://devblogs.microsoft.com/engineering-at-microsoft/copy-on-write-performance-and-debugging/ |

## Cloud database sources (checked 2026-11-16)

| Fact | Source |
|---|---|
| Azure SQL Database cannot produce a native backup file; export to BACPAC is the supported way out | https://learn.microsoft.com/en-us/answers/questions/5657455/how-to-back-up-azure-sql-db-into-bak-file |
| A BACPAC export is logical and generally slower than a native backup and restore | https://devblogs.microsoft.com/azure-sql/bacpac-and-dacpac-the-similarities-and-differences/ |
| For a transactionally consistent export, export a copy of the database, not the live one | https://techcommunity.microsoft.com:443/t5/azure-sql-blog/using-data-tier-applications-bacpac-to-migrate-a-database-from/ba-p/2335643 |
| Azure SQL Managed Instance can take a copy-only backup to a storage URL, and it restores to SQL Server 2022 | https://learn.microsoft.com/en-us/azure/azure-sql/managed-instance/restore-database-to-sql-server |
| That restore needs the instance to be on the matching update policy (2022 to SQL Server 2022, 2025 to SQL Server 2025); "Always-up-to-date" instances cannot be restored to SQL Server | https://learn.microsoft.com/en-us/azure/azure-sql/managed-instance/update-policy |
| Managed Instance blocks copy-only backups when a service-managed key protects the database | https://techcommunity.microsoft.com/t5/azure-sql-blog/restoring-a-sql-managed-instance-database-backup-to-sql-server/ba-p/3677783 |
| SQL Server 2022 can restore from a URL on Azure Blob Storage and on S3-compatible storage (`s3://` prefix), including on Linux and in containers | https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/sql-server-backup-and-restore-with-s3-compatible-object-storage |
| Native backups copied to S3 are the usual way to move SQL Server databases into Amazon RDS | https://aws.amazon.com/blogs/database/migrate-sql-server-databases-from-an-azure-sql-database-to-amazon-rds-for-sql-server-using-bacpac-method |

Not verified: exporting native backups out of Amazon RDS and Google Cloud
SQL, restore from URL inside the engine container under real conditions, and
BACPAC import times at 10 GB and 50 GB.

## Editions and licensing for test use (checked 2026-11-16, not legal advice)

| Fact | Source |
|---|---|
| Microsoft's guidance: a production backup may be restored on Developer edition for testing; data prepared on Developer edition may not be moved into production | https://www.microsoft.com/en-us/sql-server/blog/2024/02/15/how-sql-developers-can-maximize-savings |
| The 2017 licensing guide says Developer edition may not be used "in a production environment or with commercial data", which readers interpret differently | https://sqlservercentral.com/?p=3686696 |
| SQL Server 2025 adds Standard Developer edition: the Standard feature set, licensed for development and test only | https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2025 |
| On Linux and containers the edition is chosen with `MSSQL_PID`; Enterprise Developer is listed as `EnterpriseDeveloper` | https://learn.microsoft.com/sql/linux/sql-server-linux-editions-and-components-2025 |

The Standard Developer value (`StandardDeveloper`) was confirmed on
2026-11-16: see `2026-11-16-mssql-pid-values.md`.

## Spike findings (2026-11-15)

Full numbers are in `docs/research/spike-results.md`. What they establish:

- **SQL Server works on reflink-cloned files.** On a Mac (Colima and Docker
  Desktop, under Rosetta) and on a GitHub-hosted x86-64 runner: branches in
  0.25 to 0.6 s whatever the image size up to 51 GiB, isolation holds, image
  files are unchanged, CHECKDB is clean, branches survive a restart.
- **The log must be rebuilt at seal time.** Attach rewrites part of the
  source's log (68 MiB per branch for a 256 MB log). `DBCC SHRINKFILE` could
  not shrink it. `FOR ATTACH_REBUILD_LOG` after a clean detach gives a
  0.5 MB log and zero growth per branch.
- **Docker Desktop for macOS can host a thin pool.** Its VM accepts bind
  mounts of VM paths only under `/var/lib` and has no `mkfs.xfs`; a
  privileged helper container formats the file and mounts it with
  `nsenter -t 1 -m`. The mount is lost on restart, the file is not.
- **GitHub-hosted runners can too**, with `sudo` and a loopback XFS file.
  88 GiB free disk; nothing persists between jobs.
- **Scattered writes are the weak spot (dry run only).** With XFS's default
  128 KiB copy-on-write extents, 372,000 row updates grew a branch by
  7.1 GB; an 8 KiB hint halved that. The branch's files ended with about
  531,000 extents and a second pass ran 2 to 3 times slower. One run, under
  emulation, on a loopback disk.

## Still not verified

- **Gate G2 on x86-64 Linux:** official branch timings, first-write cost,
  and whether the fragmentation slowdown shows on real disks.
- **Docker Desktop for Windows** as a host for the managed pool.
- **A restart of Docker Desktop** with a managed pool (the spike did not
  test remounting there).
- **Microsoft.Data.SqlClient and Native AOT.** Not checked. 0.1 does not use
  AOT.
- **NuGet and GitHub name availability** for "SqlBranch". A web search found
  nothing with that name; the NuGet page itself could not be opened.

## Licensing notes (not legal advice)

- SQL Server Developer edition is licensed for development and test use, not
  production. Confirm the current terms before launch and say so in the README.
- "SQL Server" is a Microsoft trademark. The working name uses the generic
  "Sql". Have the final name checked before launch.

## Ideas checked and dropped on 2026-11-15

| Idea | Why dropped | Source |
|---|---|---|
| AI review for Azure DevOps | Copilot code review for Azure Repos is in technical preview | https://github.blog/changelog/2026-06-02-github-copilot-code-review-for-azure-repos-is-now-in-technical-preview/ |
| Execution plan analyzer | Performance Studio is free and open source | https://erikdarling.com/?p=13155 |
| OpenSpec scenario-to-test coverage | A coverage command is proposed in OpenSpec itself | https://github.com/Fission-AI/OpenSpec/pull/2029 |
| NuGet license-change scanner | Several exist already | https://github.com/helpdevwork/NuGETGuard |
| EF Core migration linter | MigrationGuard and others exist | https://www.nuget.org/packages/MigrationGuard.Core/0.2.0 |
