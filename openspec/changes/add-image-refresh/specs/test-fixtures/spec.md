## MODIFIED Requirements

### Requirement: Reset from test code
The object returned by the create operation SHALL offer a reset operation that returns the branch to its image state and keeps its name, connection string, expiry time and labels. Reset SHALL use the branch's pinned image version.

#### Scenario: Reset between tests
- **WHEN** the clone mode is `thin` or `copy`, a test deletes all rows from table `Customers` and the next test calls reset
- **THEN** `Customers` has the image's rows again
- **AND** the connection string and the expiry time are unchanged

#### Scenario: Reset after the source image is refreshed
- **WHEN** the clone mode is `thin` or `copy`, a fixture branch from shop version 1 has Customers row 1, shop is refreshed with row 2 instead, and the test deletes row 1 and calls fixture reset
- **THEN** Customers contains only row 1 and the branch remains on version 1
