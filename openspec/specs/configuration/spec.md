# configuration Specification

## Purpose
Defines where SqlBranch reads its settings from, which source wins when they
disagree, how mistakes are reported, and how secrets are kept out of files and
logs.

## Requirements

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
The engine password SHALL appear only in the text and JSON output of `sqlbranch branch conn`, in the connection-string property of a branch object returned by the testing library, and in the owner-only credentials file. It MUST NOT appear in any other command's output, in warnings, error messages, exception messages or the engine container's settings.

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

#### Scenario: Testing library connection string
- **WHEN** test code creates a branch from image `shop` with the testing library
- **THEN** the returned object's connection string contains the engine password
- **AND** the object's string form and any exception the library throws do not contain it

### Requirement: Failed commands are redacted
When a `docker` command or SQL statement that SqlBranch ran fails, any command line, statement or log text shown in the error MUST have the engine password and any storage secret (SAS token, S3 access key ID or S3 secret) replaced by `***`.

#### Scenario: Failing docker command
- **WHEN** a `docker` command that SqlBranch runs fails and the error shows its command line
- **THEN** the shown command line contains `***` in place of the engine password

#### Scenario: Failing credential statement
- **WHEN** the statement that gives the S3 secret to the engine fails and the error shows the statement or the engine's message (tested with a fake connector whose error echoes the secret)
- **THEN** the shown text contains `***` in place of the S3 secret and the access key ID

#### Scenario: Secret in quoted and partial forms
- **WHEN** `SQLBRANCH_AZURE_SAS_TOKEN` is `sv=2024-11-04&sp=rl&sig=Ab'Cd` and the engine's error text shows the token with its quote doubled (`Ab''Cd`), or shows only the `sig` value (tested with a fake connector)
- **THEN** the shown text contains `***` in place of each form and contains neither `Ab''Cd` nor `Ab'Cd`

### Requirement: Storage secret sources
A build, export or import with an `https://` URL SHALL read its SAS token from `SQLBRANCH_AZURE_SAS_TOKEN`, dropping one leading `?`. A build, export or import with an `s3://` URL SHALL read `SQLBRANCH_S3_ACCESS_KEY_ID` and `SQLBRANCH_S3_SECRET_ACCESS_KEY`. Storage secrets SHALL be read only from these environment variables, never from flags or config files.

#### Scenario: SAS token with a leading question mark
- **WHEN** the clone mode is `copy`, `SQLBRANCH_AZURE_SAS_TOKEN` is `?sv=2024-11-04&sp=rl&sig=abc` and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak` (tested with a fake connector)
- **THEN** the credential the engine receives holds `sv=2024-11-04&sp=rl&sig=abc`

#### Scenario: Secret as a flag
- **WHEN** the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak --sas-token abc`
- **THEN** the command exits with code 2 and no `docker` command ran

#### Scenario: Secret in a config file
- **WHEN** `.sqlbranch.yml` contains `s3SecretAccessKey: abc`
- **THEN** any command exits with code 2 and names `s3SecretAccessKey` and the file path

#### Scenario: SAS token for an export
- **WHEN** the clone mode is `copy`, `SQLBRANCH_AZURE_SAS_TOKEN` is `?sv=2024-11-04&sp=rcw&sig=abc` and the user runs `sqlbranch image export shop --to https://acme.blob.core.windows.net/images/shop.sbimg` (tested with a fake storage service)
- **THEN** every request the storage service receives carries the query `sv=2024-11-04&sp=rcw&sig=abc` and none carries `??`

#### Scenario: Secret as a flag on import
- **WHEN** the user runs `sqlbranch image import s3://s3.amazonaws.com/images/shop.sbimg --s3-secret-access-key abc`
- **THEN** the command exits with code 2 and no `docker` command ran

### Requirement: Storage secrets are checked before any action
A URL build, export or import MUST exit with code 2 and `error.code` "config_error" before running any `docker` command, SQL or storage request when a secret its URL needs is unset or empty, or when the S3 access key ID or secret contains `:`. The message SHALL name the variable and MUST NOT contain its value.

#### Scenario: Missing SAS token
- **WHEN** `SQLBRANCH_AZURE_SAS_TOKEN` is not set and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak`
- **THEN** the command exits with code 2, names `SQLBRANCH_AZURE_SAS_TOKEN`, and no `docker` command ran

#### Scenario: Missing S3 secret
- **WHEN** `SQLBRANCH_S3_ACCESS_KEY_ID` is set, `SQLBRANCH_S3_SECRET_ACCESS_KEY` is empty, and the user runs `sqlbranch image build shop --from-bak s3://s3.amazonaws.com/backups/shop.bak`
- **THEN** the command exits with code 2 and names `SQLBRANCH_S3_SECRET_ACCESS_KEY`

#### Scenario: Colon in an S3 secret
- **WHEN** `SQLBRANCH_S3_SECRET_ACCESS_KEY` is `wJalr:XUtnFEMI` and the user runs `sqlbranch image build shop --from-bak s3://s3.amazonaws.com/backups/shop.bak`
- **THEN** the command exits with code 2, names `SQLBRANCH_S3_SECRET_ACCESS_KEY`, and neither standard output nor standard error contains `wJalr:XUtnFEMI`

#### Scenario: Missing SAS token on export
- **WHEN** `SQLBRANCH_AZURE_SAS_TOKEN` is not set and the user runs `sqlbranch image export shop --to https://acme.blob.core.windows.net/images/shop.sbimg`
- **THEN** the command exits with code 2, names `SQLBRANCH_AZURE_SAS_TOKEN`, no `docker` command ran, and no storage request was sent

#### Scenario: Missing S3 key on import
- **WHEN** `SQLBRANCH_S3_ACCESS_KEY_ID` is empty, `SQLBRANCH_S3_SECRET_ACCESS_KEY` is set, and the user runs `sqlbranch image import s3://s3.amazonaws.com/images/shop.sbimg`
- **THEN** the command exits with code 2, names `SQLBRANCH_S3_ACCESS_KEY_ID`, no `docker` command ran, and no storage request was sent

### Requirement: Where storage secrets may appear
A SAS token, S3 access key ID or S3 secret MUST NOT appear in text or JSON output, warnings, errors, exceptions, image manifests, files SqlBranch writes, process argv or engine container settings. It SHALL reach only the engine's server credential during a native URL build, and the storage service in the client's own HTTPS requests during one export, import or BACPAC build. The test-only MinIO fixture is exempt for the keys it generates and passes to its own MinIO container.

#### Scenario: Successful URL build in JSON
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak --format json` exits with code 0
- **THEN** standard output and standard error contain neither the S3 access key ID nor the secret
- **AND** `docker inspect sqlbranch-engine` and `sqlbranch image list --format json` contain neither

#### Scenario: Failing URL build
- **WHEN** a URL build fails at any step (tested with a fake connector that fails each step in turn and records the SAS token)
- **THEN** for every step neither standard output nor standard error contains the SAS token

#### Scenario: Export and import in JSON
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image export shop --to s3://172.17.0.5:9000/images/shop.sbimg --format json` and `sqlbranch image import s3://172.17.0.5:9000/images/shop.sbimg --as shop2 --format json` both exit with code 0
- **THEN** standard output and standard error of both contain neither the S3 access key ID nor the secret
- **AND** the archive, `manifest.json` of `shop2`, `docker inspect sqlbranch-engine`, and every `docker` command line SqlBranch ran contain neither

#### Scenario: Failing export and import
- **WHEN** an export or import to or from `https://acme.blob.core.windows.net/images/shop.sbimg` fails at any step (tested with a fake storage service that fails each request in turn and echoes the request URL in its error)
- **THEN** for every step neither standard output nor standard error contains the SAS token or its `sig` value, and the shown URL has no query string

#### Scenario: BACPAC build with an expired SAS
- **WHEN** the storage service rejects the SAS for `https://acme.blob.core.windows.net/db/shop.bacpac` and echoes its signature in a diagnostic
- **THEN** the build exits 1 with `storage_unreachable`, only the storage service receives the SAS, and neither output stream, logs, argv, manifests nor container settings contain the signature

### Requirement: User-installed SqlPackage
For logical builds and `doctor`, SqlBranch SHALL resolve a user-installed SqlPackage from `--sqlpackage <path>` when supplied, otherwise from PATH. Doctor SHALL report the selected executable and version or installation guidance in text and JSON. A logical build with a missing or non-executable tool MUST exit 3 with `sqlpackage_missing` and installation guidance before downloading or importing.

#### Scenario: Explicit path overrides PATH
- **WHEN** PATH resolves one installation and `doctor --sqlpackage /opt/tools/sqlpackage` selects a second installation reporting version 170.5.96
- **THEN** doctor reports the selected path and version in text and JSON and exits 0 on an otherwise healthy installation

#### Scenario: Missing prerequisite
- **WHEN** an initialized engine and an unused image name are available but SqlPackage is not on PATH and no `--sqlpackage` is given
- **THEN** the logical build exits 3 with `sqlpackage_missing`, suggests `dotnet tool install -g microsoft.sqlpackage`, and makes no download request

### Requirement: Doctor treats SqlPackage as optional
`doctor` SHALL report SqlPackage as an optional check needed only for `--from-bacpac`. When it is not on PATH and no `--sqlpackage` is given, doctor MUST report it as not installed with installation guidance and MUST NOT fail or change its exit code because of it. An explicit `--sqlpackage` path that is missing or does not report a version MUST make doctor exit 3 with `sqlpackage_missing`.

#### Scenario: SqlPackage not installed
- **WHEN** Docker and the engine are healthy, SqlPackage is not on PATH and the user runs `sqlbranch doctor --format json`
- **THEN** doctor exits 0 and reports SqlPackage as not installed, needed only for `--from-bacpac`, with `dotnet tool install -g microsoft.sqlpackage`

#### Scenario: Explicit path is wrong
- **WHEN** the user runs `sqlbranch doctor --sqlpackage /missing/sqlpackage`
- **THEN** doctor exits 3 with `sqlpackage_missing` and names the path

### Requirement: Logical source secret boundaries
BACPAC builds MUST keep credentials, tokens and storage keys out of logs, both output streams, argv, child environments, container settings, image/archive metadata and recovery records. The sole argv exception SHALL be the build-owned one-time import password in SqlPackage argv. BACPAC builds SHALL sanitize tool and provider errors and MUST NOT forward credentials across a redirect to another authority.

#### Scenario: Import subprocess diagnostics
- **WHEN** a failing import emits its one-time password in its diagnostic
- **THEN** the build exits 1 with `operation_failed`, and neither output stream, logs, child environments, recovery files, manifests nor inspected container settings contain the password or its encoded form, and only the SqlPackage import argv contains it

#### Scenario: Bad region or storage secret
- **WHEN** an S3 build specifies an invalid region flag value, or a required storage variable is empty or contains a disallowed colon
- **THEN** the command exits 2 with `usage_error` for the flag or `config_error` for the variable before Docker, SQL or storage requests and identifies the input without its value

#### Scenario: Redirect to a second host
- **WHEN** an Azure Blob request returns an HTTP redirect to another authority
- **THEN** the command exits 1 with `storage_unreachable` and the second host receives no request carrying the SAS
