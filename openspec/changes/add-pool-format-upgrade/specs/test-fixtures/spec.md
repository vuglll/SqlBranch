## ADDED Requirements

### Requirement: Fixture reset with a removed or unknown base
On a pool that uses format 2, when the version a fixture branch was created from was removed or is unknown, the reset operation MUST throw without changing the branch. The message SHALL name the branch and its image, say that the version it was created from no longer exists, and name creating a new branch as the fix. It MUST NOT contain the engine password.

#### Scenario: Image removed under a running test
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, fixture branch `t-1a2b3c4d` was created from `shop@20261201T090000000Z`, shop is then removed with `sqlbranch image rm shop --force`, and the test calls reset
- **THEN** reset throws, the message names `t-1a2b3c4d` and `shop`, and the branch keeps its rows

#### Scenario: Unknown base after the upgrade
- **WHEN** the clone mode is `thin` or `copy`, fixture branch `t-5e6f7a8b` was created on a format-1 pool before shop was last rebuilt, `sqlbranch pool upgrade --yes` gave it an unknown base, and the test calls reset
- **THEN** reset throws, the message names `t-5e6f7a8b` and `shop` and does not contain the engine password, and the branch keeps its rows
