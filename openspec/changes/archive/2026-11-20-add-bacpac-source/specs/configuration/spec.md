## MODIFIED Requirements

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

## ADDED Requirements

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
