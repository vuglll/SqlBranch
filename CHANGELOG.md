# Changelog

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
