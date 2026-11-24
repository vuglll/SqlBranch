## MODIFIED Requirements

### Requirement: Reset from test code
The object returned by the create operation SHALL offer a reset operation that returns the branch to the image version it was created from and keeps its name, connection string, expiry time and labels. Reset SHALL NOT move the branch to a newer version of the image.

#### Scenario: Reset between tests
- **WHEN** the clone mode is `thin` or `copy`, a test deletes all rows from table `Customers` and the next test calls reset
- **THEN** `Customers` has the image's rows again
- **AND** the connection string and the expiry time are unchanged

#### Scenario: Reset after the source image is refreshed
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, a fixture branch was created from `shop@20261201T020000000Z` whose `Customers` holds only row 1, shop is then replaced by `shop@20261202T020000000Z` whose `Customers` holds only row 2, and the test deletes row 1 and calls reset
- **THEN** `Customers` holds only row 1 and `sqlbranch branch list` shows the branch on `shop@20261201T020000000Z`

### Requirement: Fixture reset with a removed or unknown base
On a pool that uses format 2, when the version a fixture branch was created from was removed or is unknown, the reset operation MUST throw without changing the branch. The message SHALL name the branch and its image, say that the version it was created from no longer exists, and name `sqlbranch branch reset <branch> --latest` and creating a new branch as the fixes. It MUST NOT contain the engine password.

#### Scenario: Image removed under a running test
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, fixture branch `t-1a2b3c4d` was created from `shop@20261201T090000000Z`, shop is then removed with `sqlbranch image rm shop --force`, and the test calls reset
- **THEN** reset throws, the message names `t-1a2b3c4d`, `shop` and `sqlbranch branch reset t-1a2b3c4d --latest`, and the branch keeps its rows

#### Scenario: Unknown base after the upgrade
- **WHEN** the clone mode is `thin` or `copy`, fixture branch `t-5e6f7a8b` was created on a format-1 pool before shop was last rebuilt, `sqlbranch pool upgrade --yes` gave it an unknown base, and the test calls reset
- **THEN** reset throws, the message names `t-5e6f7a8b`, `shop` and `sqlbranch branch reset t-5e6f7a8b --latest` and does not contain the engine password, and the branch keeps its rows
