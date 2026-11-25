# Changelog

## 0.3.0 (unreleased)

Pool format upgrade and image refresh ship together in this release.

### Read this before you upgrade a pool

- **`sqlbranch pool upgrade --yes` cannot be undone, and 0.2.x clients cannot
  use the pool after it.** Stop every 0.2.x `sqlbranch` command and test run
  that uses the pool first, then update both the `sqlbranch` tool and the
  SqlBranch.Testing package in every test project to 0.3 or later. With the
  engine running, 0.2.0, 0.2.1 and 0.2.2 `image rm`, `branch create`,
  `branch list`, `status` and `engine start` exit 1 with `operation_failed`
  and this message. The pool is unchanged. SqlBranch.Testing 0.2.x create
  throws the same text:

  ```text
  The engine credentials file in <pool> is not valid.
  ```

  It gives no hint to upgrade. With the engine stopped, 0.2.x `branch list`
  exits 3 `engine_stopped` as before. 0.2.x `image list` and `image export`
  still run, but only while the engine runs. When image refresh adds newer
  versions they can list or export an older version than the current one.
- **Tests that skip on the SqlBranch.Testing 0.2.x availability check
  silently stop running after the upgrade.** The check returns false and
  logs nothing, so those tests are skipped instead of failing, and a green
  run can mean that nothing ran. Update the package in every test project,
  including CI projects, and watch the skipped count.

See [docs/engine.md](docs/engine.md#sqlbranch-pool-upgrade---yes) and
[docs/testing.md](docs/testing.md#older-packages-on-an-upgraded-pool).

### Added

- `sqlbranch pool upgrade --yes` moves a pool from format 1 to format 2. It
  changes pool metadata only, never image or branch data, databases or the
  engine container, and exits 0 without changes on a pool that is already
  format 2. Only one upgrade runs at a time. A running or unfinished upgrade
  blocks writes with `pool_upgrade_required` until the same command is run
  again. A branch record it cannot read is left as it is with a warning that
  names `sqlbranch branch rm <name>`.
- Pool formats. `init` still creates format-1 pools, `status` reports the
  format (`poolFormat`), and this release reads both formats.
- Image version IDs on format-2 pools, with a UTC publication time to the
  millisecond. IDs for a name keep increasing even when the clock goes back
  or the name is removed and reused. Branches record their base version;
  `branch reset` and fixture reset restore it. A removed or unknown base
  makes reset fail without changing the branch. After the upgrade, a branch
  older than its image's last build has an unknown base: remove it and create
  it again.
- Error codes `catalog_busy` (exit 1: a second upgrade, an upgrade that runs
  out of time, or an image in use during adoption) and
  `pool_upgrade_required` (exit 3: image and branch writes during an
  unfinished upgrade; the fix is `sqlbranch pool upgrade --yes`).
- `publishWait` / `SQLBRANCH_PUBLISH_WAIT` sets how long the upgrade waits for
  running operations. The default is `10m`; accepted values are `1m` to
  `1440m` or `1h` to `24h`. An invalid value fails every command with
  `config_error`.
- An image with an unreadable catalog on a format-2 pool shows in
  `image list` as "catalog unreadable" (`catalogReadable` false), and
  `sqlbranch image rm <name>` removes it.

<!-- F4 placeholder: add-image-refresh task 7.2 adds its 0.3.0 items to this entry (Added, Changed, Fixed). -->

### Fixed

- A BACPAC build that fails before reading the package, for example because
  the image name already exists, no longer reports "Detached database users:
  none" and zero counts. The detach results appear only once they are known.

## 0.2.2 (2026-11-21)

The first release from the public repository.

### Changed

- **Faster BACPAC builds with login-mapped users.** The scratch copy now keeps
  the package's unchanged entries byte for byte instead of decompressing and
  recompressing them: about 184 MB/s instead of about 8 MB/s, so the rewrite
  of a 10 GB package takes about a minute instead of about 20. Packages over
  4 GB (Zip64) are supported. A package the copy cannot reproduce safely, for
  example an encrypted or multi-disk archive, is refused with
  `bacpac_invalid`.

### Added

- Contributing guide (contributions need a signed Contributor License
  Agreement), code of conduct, security policy, issue and pull request
  templates.
- `NOTICE` and `THIRD-PARTY-NOTICES.md`, also included in both packages.
- Package project URL, copyright and tags.

## 0.2.1 (2026-11-20)

### Fixed

- **BACPAC builds from real Azure SQL exports.** Database users mapped to
  server logins are now imported as users without a login, keeping their
  permissions, role memberships and schema ownership, instead of failing the
  build. The package's logins are not created on the engine, and the supplied
  file is not modified: SqlBranch imports a validated scratch copy, which needs
  about one package size of extra client space. The result lists
  `detachedUsers`, `removedLogins` and `removedServerObjects`. Users mapped to
  Windows or external-provider logins still stop the build with
  `unsupported_feature`. The `bacpac_login_users` error is retired.
- Azure exports link a user to its login with a `Login` relationship; these
  users are now recognised.
- A failed BACPAC import now lists the SQL Server error numbers.
- The `SqlBranch.Testing` readme says how to install the `sqlbranch` tool.

BACPAC builds are still preview.

## 0.2.0 (2026-11-20)

The first release. It covers the 0.1 and 0.2 milestones of the
[roadmap](docs/roadmap.md), plus three cloud features marked preview.

Packages: `SqlBranch` (the `sqlbranch` .NET tool) and `SqlBranch.Testing`.

### Branching core (0.1)

- `sqlbranch init`, `status`, `doctor`, `engine start|stop`, `teardown`.
- `image build --from-bak`, `image list`, `image rm`. Images get a rebuilt,
  minimal log so branches stay cheap; `--keep-recovery-model` keeps the
  source's recovery model.
- `branch create`, `list`, `conn`, `reset`, `rm`, and `--copy` branches for
  benchmarks and write-heavy work.
- Thin mode by default: the tool creates and mounts its own XFS pool where the
  machine has none, and falls back to copy mode, always reported.
- A choice of development edition (`developer`, `standard-developer`,
  `enterprise-developer`), text and JSON output, stable exit codes, and
  `.sqlbranch.yml`.

### Tests and agents (0.2)

- `SqlBranch.Testing`: a fresh branch per test class or test, in one line of C#,
  for xUnit, NUnit and MSTest. See [docs/testing.md](docs/testing.md).
- Images built from SQL scripts, and post-build scripts (for example data
  masking) for backup images.
- Short-lived branches with a time limit, and `branch prune`.
- An agent skill generated from the command metadata.

### Preview

These work and are covered by integration tests, but have not yet been run
against real Azure or S3 accounts. Their release gate is 0.3.

- Building an image from a backup URL on Azure Blob Storage or S3.
- `image export` and `image import`, to a file or to cloud storage. Archives
  are zstd-compressed; import also reads gzip.
- Building an image from a BACPAC with SqlPackage (`--from-bacpac`). `doctor`
  reports SqlPackage as optional.

### Verified

- x86-64 Linux: a 50 GB image branches in about 0.4 s;
  `sqlbranch branch create` takes 2.1 to 2.5 s. The README quick start ran on a
  clean server, and the full integration suite passed on native XFS.
- Docker Desktop for macOS and Colima: thin mode through the managed pool.
- **Not verified:** Docker Desktop for Windows. If the managed pool cannot be
  mounted there, `init` falls back to copy mode and says so.

### Known limits

- Thin branches under heavy scattered writes cost about 5 times a full copy
  (gate G2); use `--copy` for that work.
- Full, single-file backups only; one engine, one host, one user. See the
  README's Limits section.
- For development and test use only, never production data.
