## ADDED Requirements

### Requirement: Engine trusts public storage certificates
The engine SHALL trust the public certificate authorities needed to reach Azure Blob Storage and Amazon S3 over HTTPS, so that a URL build from either service needs no certificate setup by the user.

#### Scenario: Build from Amazon S3 on a new engine
- **WHEN** the clone mode is `thin` or `copy`, the engine was created by `sqlbranch init` with default settings, and the user runs `sqlbranch image build shop --from-bak s3://acme-db.s3.eu-west-1.amazonaws.com/shop.bak --s3-region eu-west-1` with valid S3 secrets (opt-in cloud test)
- **THEN** the command exits with code 0

#### Scenario: Build from Azure Blob Storage on a new engine
- **WHEN** the clone mode is `thin` or `copy`, the engine was created by `sqlbranch init` with default settings, and the user runs `sqlbranch image build shop --from-bak https://acme.blob.core.windows.net/backups/shop.bak` with a valid SAS token (opt-in cloud test)
- **THEN** the command exits with code 0

## MODIFIED Requirements

### Requirement: Status reports mismatches
`sqlbranch status` SHALL list every branch folder with no attached database, every attached database with files in the branches area of the pool but no branch folder, the leftovers of a reset or image build that is no longer running, a `sqlbranch_build_<id>` login and a build's storage credential among them, and every image that was only partly removed, and SHALL exit with code 1 when it lists any.

#### Scenario: Branch folder without a database
- **WHEN** a branch folder `pr-7` exists in the pool but no database `pr-7` is attached
- **THEN** `sqlbranch status` exits with code 1, lists `pr-7` as a mismatch and says the database is missing

#### Scenario: User database is not a mismatch
- **WHEN** a user created database `scratch` through the engine connection, its files are outside the pool's branches area, and nothing else is out of place
- **THEN** `sqlbranch status` exits with code 0 and does not list `scratch`

#### Scenario: Build interrupted
- **WHEN** the client running `sqlbranch image build shop` was killed during the restore
- **THEN** `sqlbranch status` exits with code 1 and lists the build's staging folder, and its temporary database when one is left

#### Scenario: Build still running
- **WHEN** `sqlbranch image build shop` is restoring in another process and nothing else is out of place
- **THEN** `sqlbranch status` exits with code 0 and shows the build as in progress

#### Scenario: Reset interrupted
- **WHEN** the client running `sqlbranch branch reset pr-142` was killed after the new files were prepared and before they were attached
- **THEN** `sqlbranch status` exits with code 1 and lists the leftover reset files of `pr-142`

#### Scenario: Login left by a killed build
- **WHEN** the client running `sqlbranch image build shopdev --from-scripts db` was killed while a script ran, its temporary database and staging folder were removed by hand, and login `sqlbranch_build_7f3a` is left
- **THEN** `sqlbranch status` exits with code 1 and lists login `sqlbranch_build_7f3a` as a build leftover
- **AND** after `sqlbranch image build crm --from-bak crm.bak` exits with code 0, `sqlbranch status` exits with code 0

#### Scenario: Credential left by a failed drop
- **WHEN** the clone mode is `copy`, a URL build from `s3://172.17.0.5:9000/backups/shop.bak` exited with code 1 because dropping its credential failed (tested with a fake connector), and nothing else is out of place
- **THEN** `sqlbranch status` exits with code 1 and lists credential `s3://172.17.0.5:9000/backups` as a build leftover without showing its secret
