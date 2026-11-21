# Spec Delta

## MODIFIED Requirements

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
