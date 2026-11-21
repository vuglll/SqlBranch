## ADDED Requirements

### Requirement: Storage secret sources
A build from an `https://` URL SHALL read its SAS token from `SQLBRANCH_AZURE_SAS_TOKEN`, dropping one leading `?`. A build from an `s3://` URL SHALL read `SQLBRANCH_S3_ACCESS_KEY_ID` and `SQLBRANCH_S3_SECRET_ACCESS_KEY`. Storage secrets SHALL be read only from these environment variables, never from flags or config files.

#### Scenario: SAS token with a leading question mark
- **WHEN** the clone mode is `copy`, `SQLBRANCH_AZURE_SAS_TOKEN` is `?sv=2024-11-04&sp=rl&sig=abc` and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak` (tested with a fake connector)
- **THEN** the credential the engine receives holds `sv=2024-11-04&sp=rl&sig=abc`

#### Scenario: Secret as a flag
- **WHEN** the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak --sas-token abc`
- **THEN** the command exits with code 2 and no `docker` command ran

#### Scenario: Secret in a config file
- **WHEN** `.sqlbranch.yml` contains `s3SecretAccessKey: abc`
- **THEN** any command exits with code 2 and names `s3SecretAccessKey` and the file path

### Requirement: Storage secrets are checked before any action
A URL build MUST exit with code 2 and `error.code` "config_error" before running any `docker` command or SQL when a secret its URL needs is unset or empty, or when the S3 access key ID or secret contains `:`. The message SHALL name the variable and MUST NOT contain its value.

#### Scenario: Missing SAS token
- **WHEN** `SQLBRANCH_AZURE_SAS_TOKEN` is not set and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak`
- **THEN** the command exits with code 2, names `SQLBRANCH_AZURE_SAS_TOKEN`, and no `docker` command ran

#### Scenario: Missing S3 secret
- **WHEN** `SQLBRANCH_S3_ACCESS_KEY_ID` is set, `SQLBRANCH_S3_SECRET_ACCESS_KEY` is empty, and the user runs `sqlbranch image build shop --from-bak s3://s3.amazonaws.com/backups/shop.bak`
- **THEN** the command exits with code 2 and names `SQLBRANCH_S3_SECRET_ACCESS_KEY`

#### Scenario: Colon in an S3 secret
- **WHEN** `SQLBRANCH_S3_SECRET_ACCESS_KEY` is `wJalr:XUtnFEMI` and the user runs `sqlbranch image build shop --from-bak s3://s3.amazonaws.com/backups/shop.bak`
- **THEN** the command exits with code 2, names `SQLBRANCH_S3_SECRET_ACCESS_KEY`, and neither standard output nor standard error contains `wJalr:XUtnFEMI`

### Requirement: Where storage secrets may appear
A SAS token, S3 access key ID or S3 secret MUST NOT appear in any command's text or JSON output, warnings, error or exception messages, image manifests, files SqlBranch writes, `docker` command lines, or the engine container's settings. The only place it SHALL reach is the engine's server credential for the duration of one build. The test-only MinIO fixture is exempt for the keys it generates and passes to its own MinIO container.

#### Scenario: Successful URL build in JSON
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image build shop --from-bak s3://172.17.0.5:9000/backups/shop.bak --format json` exits with code 0
- **THEN** standard output and standard error contain neither the S3 access key ID nor the secret
- **AND** `docker inspect sqlbranch-engine` and `sqlbranch image list --format json` contain neither

#### Scenario: Failing URL build
- **WHEN** a URL build fails at any step (tested with a fake connector that fails each step in turn and records the SAS token)
- **THEN** for every step neither standard output nor standard error contains the SAS token

## MODIFIED Requirements

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
