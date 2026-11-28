## MODIFIED Requirements

### Requirement: Reset from test code
The object returned by the create operation SHALL offer a reset operation that returns the branch to its base and keeps its name, connection string, expiry time and labels. On a pool that uses format 2 the base is the image version the branch was created from; on a pool that uses format 1 it is the image.

#### Scenario: Reset between tests
- **WHEN** the clone mode is `thin` or `copy`, a test deletes all rows from table `Customers` and the next test calls reset
- **THEN** `Customers` has the image's rows again
- **AND** the connection string and the expiry time are unchanged

#### Scenario: Fixture reset on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, a fixture branch was created from `shop@20261201T090000000Z`, and the test deletes all rows from `Customers` and calls reset
- **THEN** `Customers` has the rows of `shop@20261201T090000000Z` again and `sqlbranch branch list --format json` shows the branch with `imageVersion` "shop@20261201T090000000Z"

### Requirement: Clear failure when the environment is missing
When the engine was never initialized, is stopped, the image does not exist, or a pool upgrade did not finish, the create operation MUST throw an exception whose message states the cause and names the `sqlbranch` command that fixes it.

#### Scenario: Engine not initialized
- **WHEN** test code calls the create operation on a machine where `sqlbranch init` never ran
- **THEN** the exception message says the engine was not found and names `sqlbranch init`

#### Scenario: Engine stopped
- **WHEN** the engine was initialized and then stopped with `sqlbranch engine stop`, and test code calls the create operation
- **THEN** the exception message says the engine is stopped and names `sqlbranch engine start`

#### Scenario: Image missing
- **WHEN** the engine is running, no image `shop` exists, and test code calls the create operation with image `shop`
- **THEN** the exception message says image `shop` does not exist and names `sqlbranch image build shop`

#### Scenario: Unfinished pool upgrade
- **WHEN** the engine is running, `sqlbranch pool upgrade --yes` was killed before it finished, and test code using SqlBranch.Testing 0.3 calls the create operation with image `shop`
- **THEN** the exception message says the pool upgrade did not finish and names `sqlbranch pool upgrade --yes`, and no fixture branch is created

### Requirement: Availability check for skipping
The testing library SHALL offer a check that returns whether an engine is running and reachable and the pool can be used, without throwing, so that tests can be skipped where it is not. During an unfinished pool upgrade it SHALL return false and write a library log message naming `sqlbranch pool upgrade --yes`.

#### Scenario: No Docker on the machine
- **WHEN** the availability check runs on a machine without Docker
- **THEN** it returns false and does not throw

#### Scenario: Docker present, engine stopped
- **WHEN** Docker is running, `sqlbranch init` ran, and the engine was stopped with `sqlbranch engine stop`
- **THEN** the availability check returns false and does not throw

#### Scenario: Engine running
- **WHEN** the engine is running
- **THEN** the availability check returns true

#### Scenario: Availability during an unfinished upgrade
- **WHEN** the engine is running and `sqlbranch pool upgrade --yes` was killed before it finished
- **THEN** the SqlBranch.Testing 0.3 availability check returns false, does not throw, and the library log has a line naming `sqlbranch pool upgrade --yes`
- **AND** after `sqlbranch pool upgrade --yes` exits 0, the availability check returns true

## ADDED Requirements

### Requirement: Fixture reset with a removed or unknown base
On a pool that uses format 2, when the version a fixture branch was created from was removed or is unknown, the reset operation MUST throw without changing the branch. The message SHALL name the branch and its image, say that the version it was created from no longer exists, and name creating a new branch as the fix. It MUST NOT contain the engine password.

#### Scenario: Image removed under a running test
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, fixture branch `t-1a2b3c4d` was created from `shop@20261201T090000000Z`, shop is then removed with `sqlbranch image rm shop --force`, and the test calls reset
- **THEN** reset throws, the message names `t-1a2b3c4d` and `shop`, and the branch keeps its rows

#### Scenario: Unknown base after the upgrade
- **WHEN** the clone mode is `thin` or `copy`, fixture branch `t-5e6f7a8b` was created on a format-1 pool before shop was last rebuilt, `sqlbranch pool upgrade --yes` gave it an unknown base, and the test calls reset
- **THEN** reset throws, the message names `t-5e6f7a8b` and `shop` and does not contain the engine password, and the branch keeps its rows

### Requirement: Fixture reset and dispose during an unfinished upgrade
While a pool upgrade has not finished, the reset operation of SqlBranch.Testing 0.3 MUST throw without changing the branch, with a message that says the pool upgrade did not finish and names `sqlbranch pool upgrade --yes`. Dispose MUST NOT throw: it SHALL leave the branch, and write a library log line that names `sqlbranch pool upgrade --yes` and says that a later prune removes the branch after it expires.

#### Scenario: Reset during an unfinished upgrade
- **WHEN** the clone mode is `thin` or `copy`, fixture branch `t-9c0d1e2f` of shop holds one extra row, `sqlbranch pool upgrade --yes` was then killed before it finished, and the test calls reset
- **THEN** reset throws, the message names `sqlbranch pool upgrade --yes`, and `t-9c0d1e2f` still holds its extra row

#### Scenario: Dispose during an unfinished upgrade
- **WHEN** the clone mode is `thin` or `copy`, fixture branch `t-9c0d1e2f` exists, `sqlbranch pool upgrade --yes` was killed before it finished, and the test disposes its object
- **THEN** dispose completes without an exception, `sqlbranch branch list` still shows `t-9c0d1e2f`, and the library log has a line naming `sqlbranch pool upgrade --yes`
