## MODIFIED Requirements

### Requirement: Settings and defaults
SqlBranch SHALL support the settings `pool`, `poolBackend`, `poolSize`, `engineImage`, `engineEdition`, `port`, `requireThin`, `defaultImage` and `publishWait`, and SHALL work with no configuration at all using documented defaults.

#### Scenario: No configuration
- **WHEN** no config file, environment variable or flag is present and the user runs `sqlbranch init`
- **THEN** the engine uses port 14330, the default SQL Server 2022 image and the `developer` edition
- **AND** `poolBackend` is `auto`, `poolSize` is 60 GB, `requireThin` is off and `publishWait` is 10 minutes

## ADDED Requirements

### Requirement: Wait setting for running operations
The `publishWait` setting, environment variable `SQLBRANCH_PUBLISH_WAIT`, SHALL set how long a command that must wait for running image and branch operations waits before it exits 1 with `error.code` "catalog_busy". A value is a whole number followed by `m` (minutes) or `h` (hours). The default SHALL be `10m`.

#### Scenario: Wait set in the config file
- **WHEN** `.sqlbranch.yml` sets `publishWait: 30m`, the pool uses format 1, and a reset of `dev1` keeps running for 20 minutes after `sqlbranch pool upgrade --yes` starts (tested with a fake clock)
- **THEN** the upgrade waits and exits with code 0 after the reset finishes

#### Scenario: Environment beats the file
- **WHEN** `.sqlbranch.yml` sets `publishWait: 30m`, `SQLBRANCH_PUBLISH_WAIT=2m` is set, and a reset of `dev1` keeps running for 3 minutes after `sqlbranch pool upgrade --yes` starts (tested with a fake clock)
- **THEN** the upgrade exits with code 1 and `error.code` "catalog_busy" at the 2-minute mark

### Requirement: Wait values are checked
A `publishWait` value that is not a whole number from 1 to 1440 followed by `m`, or from 1 to 24 followed by `h`, MUST make any command exit 2 with `error.code` "config_error", naming `publishWait` and where the value came from, before any Docker command, as for every other invalid setting.

#### Scenario: Wait values that are refused
- **WHEN** `publishWait` is set, in turn, to `0m`, `-5m`, `10`, `10s`, `1.5h`, `25h`, `1441m` and `ten`, and the user runs `sqlbranch branch list`
- **THEN** each run exits with code 2, `error.code` is "config_error", the message names `publishWait` and its source, and no Docker command runs

#### Scenario: Largest values are accepted
- **WHEN** `SQLBRANCH_PUBLISH_WAIT` is `24h`, then `1440m`, then `007m`, and the user runs `sqlbranch pool upgrade --yes` on a machine where Docker is not running
- **THEN** each run exits with code 3 and `error.code` is "docker_unavailable", not a configuration error
