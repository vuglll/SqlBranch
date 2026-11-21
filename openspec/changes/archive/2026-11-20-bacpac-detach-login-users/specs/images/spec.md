## RENAMED Requirements

- FROM: `### Requirement: Refuse login-mapped package users`
- TO: `### Requirement: Detach login-mapped package users`

## MODIFIED Requirements

### Requirement: Detach login-mapped package users
A logical build SHALL import user-defined users mapped to server logins as users without a login, keeping their database permissions, role memberships and ownership. It SHALL import a validated scratch copy without the package's logins or server objects referencing them, MUST NOT create or change any login named in the package or already on the engine, and MUST leave the supplied file unchanged.

#### Scenario: Two users depend on server logins
- **WHEN** the clone mode is `thin` or `copy`, the package maps `report_reader` and `sales_app` to logins of the same names, `report_reader` owns schema `reports` and `sales_app` has SELECT on `dbo.Orders`, and the engine already has a login named `sales_app`
- **THEN** the build exits 0, both users exist in a branch as users without a login with the ownership and grant intact, the existing `sales_app` login keeps its SID, state and memberships, and the supplied package file is unchanged

#### Scenario: Users linked as in an Azure SQL export
- **WHEN** the clone mode is `thin` or `copy` and each user references its `SqlLogin` element, one with a `Disambiguator`, through a `Login` relationship instead of a `LoginIdentifierScript` property
- **THEN** the users are detached the same way and the build exits 0

#### Scenario: A retained object references a removed login
- **WHEN** the clone mode is `thin` or `copy` and a retained model element other than a server role membership or server permission references a package login
- **THEN** the build exits 1 with `unsupported_feature` before import, names the element type and name, and publishes no image

#### Scenario: Damaged package with mapped users
- **WHEN** the clone mode is `thin` or `copy` and a package with login-mapped users has a model checksum that does not match `model.xml`
- **THEN** the build exits 1 with `bacpac_invalid` before any rewrite or import

#### Scenario: Package without login-mapped users
- **WHEN** the clone mode is `thin` or `copy` and the package has no `SqlLogin` and only contained users and users without a login
- **THEN** the build makes no additional copy and reports no detached users and no `rewrite` phase

### Requirement: Build size and phase times
Before import the build SHALL report the supplied package's bytes and warn that large operations can take hours. It SHALL report elapsed milliseconds per attempted phase, including a failed phase and `rewrite` when it ran. Text and versioned JSON SHALL identify size, phases, detached user names, `removedLogins` and `removedServerObjects`. In JSON mode live notices MUST use standard error so standard output holds one result.

#### Scenario: Timings with a fake clock
- **WHEN** the clone mode is `copy`, a 2048-byte package takes 120 ms to import and 15 ms to seal, and JSON output is requested
- **THEN** standard error reports 2048 bytes and the hours warning before import, and the single JSON result includes `schemaVersion`, `sourceSizeBytes` 2048 and `phaseMilliseconds` with `import` 120 and `seal` 15

#### Scenario: Detached users in JSON
- **WHEN** the clone mode is `copy`, the package maps `report_reader` and `sales_app` to two logins and one server role membership has `sales_app` as member, and JSON output is requested
- **THEN** the single JSON result includes `detachedUsers` with both names, `removedLogins` 2, `removedServerObjects` 1 and a `rewrite` phase time, and `sourceSizeBytes` and `bacpacSha256` describe the supplied package

#### Scenario: Detached users in text
- **WHEN** the clone mode is `copy`, the same package is built with text output
- **THEN** the result names both detached users and both counts
