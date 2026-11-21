## MODIFIED Requirements

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
A SAS token, S3 access key ID or S3 secret MUST NOT appear in any command's text or JSON output, warnings, error or exception messages, image manifests, files SqlBranch writes, `docker` command lines, or the engine container's settings. It SHALL reach only the engine's server credential for the duration of one build, and the storage service in the client's own HTTPS requests during one export or import. The test-only MinIO fixture is exempt for the keys it generates and passes to its own MinIO container.

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
