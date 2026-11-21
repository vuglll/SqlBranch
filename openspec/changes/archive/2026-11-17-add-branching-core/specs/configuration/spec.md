# Spec Delta

## Purpose

Defines where SqlBranch reads its settings from, which source wins when they
disagree, how mistakes are reported, and how secrets are kept out of files and
logs.

## ADDED Requirements

### Requirement: Settings and defaults
SqlBranch SHALL support the settings `pool`, `poolBackend`, `poolSize`, `engineImage`, `engineEdition`, `port`, `requireThin` and `defaultImage`, and SHALL work with no configuration at all using documented defaults.

#### Scenario: No configuration
- **WHEN** no config file, environment variable or flag is present and the user runs `sqlbranch init`
- **THEN** the engine uses port 14330, the default SQL Server 2022 image and the `developer` edition
- **AND** `poolBackend` is `auto`, `poolSize` is 60 GB and `requireThin` is off

### Requirement: Pool backend
The `poolBackend` setting SHALL accept `auto`, `loop-xfs` and `plain`, with `auto` as the default. `plain` SHALL use the pool directory as it is. Any other value MUST be rejected with exit code 2.

#### Scenario: Plain backend on a reflink file system
- **WHEN** `poolBackend` is `plain` and the pool directory is on XFS with reflink
- **THEN** `sqlbranch init` exits with code 0 and reports pool backend `plain` and clone mode `thin`

#### Scenario: Unknown backend
- **WHEN** `.sqlbranch.yml` sets `poolBackend: zfs`
- **THEN** any command exits with code 2 and lists the allowed values

### Requirement: Pool size
The `poolSize` setting SHALL set the size of a managed pool when it is created.

#### Scenario: Larger pool
- **WHEN** `poolSize` is `100GB` and the user runs `sqlbranch init` for the first time on Docker Desktop
- **THEN** the command exits with code 0
- **AND** `sqlbranch status` reports a total pool space between 99 GB and 101 GB

### Requirement: Pool size values are checked
A `poolSize` value that is not a size, such as `big`, MUST be rejected with exit code 2.

#### Scenario: Not a size
- **WHEN** `.sqlbranch.yml` sets `poolSize: big`
- **THEN** any command exits with code 2 and names `poolSize`

### Requirement: Pools are not resized
A `poolSize` different from an existing managed pool's size MUST make `sqlbranch init` exit with code 2 and say resizing is not supported, leaving the pool unchanged.

#### Scenario: Size changed after creation
- **WHEN** the pool was created at 60 GB and `poolSize` is later set to `100GB`
- **THEN** `sqlbranch init` exits with code 2 and says resizing is not supported
- **AND** the pool and its contents are unchanged

### Requirement: Engine edition
The `engineEdition` setting SHALL accept `developer`, `standard-developer` and `enterprise-developer`, with `developer` as the default.

#### Scenario: Standard Developer on a SQL Server 2025 image
- **WHEN** `engineImage` is a SQL Server 2025 image, `engineEdition` is `standard-developer`, and the user runs `sqlbranch init`
- **THEN** the command exits with code 0 and `sqlbranch status` reports the edition as Standard Developer

### Requirement: Only development editions
Any `engineEdition` value other than the three development editions, including production editions, MUST be rejected with exit code 2.

#### Scenario: Production edition requested
- **WHEN** `.sqlbranch.yml` sets `engineEdition: standard`
- **THEN** any command exits with code 2 and says only development editions are supported

### Requirement: Edition needs a matching engine version
`standard-developer` and `enterprise-developer` MUST be rejected with exit code 2 when the engine image is older than SQL Server 2025, with a message naming the image and the minimum version. The version is read from the image tag, or from the started engine when the tag does not name it.

#### Scenario: Standard Developer on SQL Server 2022
- **WHEN** `engineImage` is a SQL Server 2022 image and `engineEdition` is `standard-developer`
- **THEN** `sqlbranch init` exits with code 2 and says this edition needs SQL Server 2025 or later

#### Scenario: Custom tag without a version
- **WHEN** `engineImage` is `registry.local/mssql:team` holding SQL Server 2022 and `engineEdition` is `standard-developer`
- **THEN** `sqlbranch init` exits with code 2, names SQL Server 2025 as the minimum, and leaves no engine container

### Requirement: Edition is fixed at first init
Running `sqlbranch init` with an `engineEdition` different from the one the existing engine was created with MUST fail with exit code 2, name the current edition, and leave the engine unchanged.

#### Scenario: Edition changed after init
- **WHEN** the engine was created as `developer` and the user runs `sqlbranch init` with `engineEdition: standard-developer`
- **THEN** the command exits with code 2 and names `developer` as the current edition
- **AND** existing images and branches are untouched

### Requirement: Engine image is fixed at first init
Running `sqlbranch init` with an `engineImage` different from the one the existing engine was created with MUST fail with exit code 2, name the current image, and leave the engine unchanged.

#### Scenario: Image changed after init
- **WHEN** the engine was created from `mcr.microsoft.com/mssql/server:2022-latest` and the user runs `sqlbranch init` with `engineImage` set to a SQL Server 2025 image
- **THEN** the command exits with code 2 and names the 2022 image as current
- **AND** existing images and branches are untouched

### Requirement: Port and pool are fixed at first init
Running `sqlbranch init` with a `port` or `pool` different from the existing engine's MUST fail with exit code 2, name the current value, and leave the engine and pool unchanged.

#### Scenario: Port changed after init
- **WHEN** the engine was created on port 14330 and the user runs `sqlbranch init --port 16000`
- **THEN** the command exits with code 2 and names 14330 as the current port

### Requirement: Precedence
When a setting is given in more than one place, SqlBranch SHALL use, in order, the command-line flag, the `SQLBRANCH_` environment variable, the repository config file, the user config file, then the default.

#### Scenario: Flag beats file
- **WHEN** `.sqlbranch.yml` sets `port: 15000` and the user runs `sqlbranch init --port 16000` on a machine with no engine
- **THEN** the command exits with code 0 and the engine listens on port 16000

#### Scenario: Environment beats file
- **WHEN** `.sqlbranch.yml` sets `requireThin: false`, `SQLBRANCH_REQUIRE_THIN=true` is set, and the user runs `sqlbranch status --format json`
- **THEN** the command exits with code 0 and reports `requireThin` as true

### Requirement: Config file discovery
SqlBranch SHALL read `.sqlbranch.yml` from the current directory or the nearest parent directory that has one, and a user file at `~/.sqlbranch/config.yml`. `--config <file>` SHALL replace the repository file.

#### Scenario: Run from a subfolder
- **WHEN** `.sqlbranch.yml` in the repository root sets `port: 15000` and the user runs `sqlbranch init` from `src/api` on a machine with no engine
- **THEN** the command exits with code 0 and the engine listens on port 15000

### Requirement: Invalid configuration is reported
A config file with an unknown key, a wrong value type or an unsupported `version` MUST cause exit code 2 with the file path and the offending key.

#### Scenario: Misspelled key
- **WHEN** `.sqlbranch.yml` contains `requireThinn: true`
- **THEN** any command exits with code 2 and names `requireThinn` and the file path

### Requirement: Default image
When `defaultImage` is set, `sqlbranch branch create <name>` without `--from` SHALL use that image.

#### Scenario: Default image used
- **WHEN** `defaultImage: shop` is set and the user runs `sqlbranch branch create dev1`
- **THEN** the command exits with code 0 and branch `dev1` is created from image `shop`

### Requirement: An image must be named
When `defaultImage` is not set, `sqlbranch branch create <name>` without `--from` MUST exit with code 2 and name the `--from` flag.

#### Scenario: No image given
- **WHEN** no `defaultImage` is set and the user runs `sqlbranch branch create dev1`
- **THEN** the command exits with code 2 and names `--from`

### Requirement: Engine password source
`sqlbranch init` SHALL generate a 32-character engine administrator password, or take it from `SQLBRANCH_SA_PASSWORD`, and SHALL store it only inside the pool with owner-only access.

#### Scenario: Generated password
- **WHEN** `SQLBRANCH_SA_PASSWORD` is not set and the user runs `sqlbranch init`
- **THEN** the command exits with code 0 and `sqlbranch branch conn` for any branch prints a working connection string

### Requirement: The password is fixed at first init
When the engine exists and `SQLBRANCH_SA_PASSWORD` is set to a value different from the stored password, `sqlbranch init` MUST exit with code 2, say the password is set at the first init, and change nothing.

#### Scenario: Different password on a later init
- **WHEN** the engine exists and the user runs `sqlbranch init` with `SQLBRANCH_SA_PASSWORD` set to a new valid password
- **THEN** the command exits with code 2 and names `SQLBRANCH_SA_PASSWORD`
- **AND** the stored password still works

### Requirement: Weak passwords are refused early
A `SQLBRANCH_SA_PASSWORD` that does not meet SQL Server's password policy (at least 8 characters from three of: uppercase, lowercase, digits, symbols) MUST make `sqlbranch init` exit with code 2 before creating anything.

#### Scenario: Short password
- **WHEN** `SQLBRANCH_SA_PASSWORD` is `abc` and the user runs `sqlbranch init` on a clean machine
- **THEN** the command exits with code 2, names `SQLBRANCH_SA_PASSWORD`, and no pool or container is created

### Requirement: Passwords are not read from config files
A config file that contains a `password` key MUST cause exit code 2.

#### Scenario: Password in a config file
- **WHEN** `.sqlbranch.yml` contains a `password` key
- **THEN** the command exits with code 2 and says passwords are not read from config files

### Requirement: Where the password may appear
The engine password SHALL appear only in the text and JSON output of `sqlbranch branch conn` and in the owner-only credentials file. It MUST NOT appear in any other command's output, in warnings, error messages or the engine container's settings.

#### Scenario: Status output
- **WHEN** the user runs `sqlbranch status --format json`
- **THEN** the output does not contain the engine password

#### Scenario: Branch create in JSON
- **WHEN** the user runs `sqlbranch branch create pr-142 --from shop --format json`
- **THEN** the output does not contain the engine password

#### Scenario: Branch conn in JSON
- **WHEN** the user runs `sqlbranch branch conn pr-142 --format json`
- **THEN** the command exits with code 0 and `connectionString` contains the engine password

#### Scenario: Container settings
- **WHEN** `sqlbranch init` has finished and the user runs `docker inspect sqlbranch-engine`
- **THEN** the output does not contain the engine password

### Requirement: Failed commands are redacted
When a `docker` command or SQL statement that SqlBranch ran fails, any command line, statement or log text shown in the error MUST have the engine password replaced by `***`.

#### Scenario: Failing docker command
- **WHEN** a `docker` command that SqlBranch runs fails and the error shows its command line
- **THEN** the shown command line contains `***` in place of the engine password
