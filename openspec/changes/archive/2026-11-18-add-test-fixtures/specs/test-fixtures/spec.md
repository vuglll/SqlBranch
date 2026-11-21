# Spec Delta

## Purpose

Gives automated tests a library API that creates a private branch for a test
or test class, exposes its connection string, and removes the branch
afterwards, including after a crashed run.

## ADDED Requirements

### Requirement: Create a branch from test code
The testing library SHALL create a branch from a named image with one asynchronous call and SHALL return an object exposing the branch's name, its clone mode and a working connection string.

#### Scenario: Branch for a test
- **WHEN** the clone mode is `thin` or `copy`, image `shop` exists, and test code calls the create operation with image `shop`
- **THEN** it receives an object whose name matches `t-[0-9a-f]{8}`, whose clone mode equals the one `sqlbranch status` reports, and whose connection string opens database with that name
- **AND** a query through that connection string returns the image's rows

### Requirement: Dispose removes the branch
Disposing the object returned by the create operation SHALL close open connections to the branch, remove the branch and delete its files.

#### Scenario: Branch gone after dispose
- **WHEN** the clone mode is `thin` or `copy` and the object for branch `t-1a2b3c4d` is disposed at the end of a test while a pooled connection to it is still open
- **THEN** dispose completes without an exception
- **AND** `sqlbranch branch list` no longer shows `t-1a2b3c4d` and the pool has no files for it

### Requirement: Dispose never throws for cleanup problems
Dispose MUST NOT throw when the branch is already gone. When the engine is stopped, dispose MUST NOT throw, SHALL leave the branch, and SHALL write a message to the library's log saying that a later prune removes it after it expires.

#### Scenario: Branch removed by hand
- **WHEN** branch `t-1a2b3c4d` was removed with `sqlbranch branch rm t-1a2b3c4d` and the test then disposes its object
- **THEN** dispose completes without an exception

#### Scenario: Engine stopped before dispose
- **WHEN** the user ran `sqlbranch engine stop` while a test held branch `t-1a2b3c4d` and the test then disposes its object
- **THEN** dispose completes without an exception
- **AND** the log contains a message naming `t-1a2b3c4d` and `sqlbranch branch prune`
- **AND** after `sqlbranch engine start`, `sqlbranch branch list` still shows `t-1a2b3c4d`

### Requirement: Unique names under parallel runs
Branches created by the testing library SHALL get names of the form `<prefix>-<8 lowercase hexadecimal characters>`, with the prefix `t` by default, so that parallel tests never share a branch. When the name is already taken, the library SHALL retry with a new suffix up to 3 times before throwing.

#### Scenario: Twenty parallel creates
- **WHEN** the clone mode is `thin` or `copy` and twenty tests call the create operation at the same time against image `shop`
- **THEN** all twenty succeed with twenty different branch names

#### Scenario: Suffix already taken
- **WHEN** branch `t-00000001` exists and the name source returns `00000001` and then `00000002` (tested with a fake name source)
- **THEN** the create succeeds with branch `t-00000002`

#### Scenario: Four collisions
- **WHEN** the name source returns four suffixes whose branches all exist (tested with a fake name source)
- **THEN** the create throws an exception saying the branch already exists, after four attempts

### Requirement: Name prefix is checked
The name prefix MUST follow the branch name rules and MUST be at most 54 characters, so that the prefix, `-` and 8 hexadecimal characters fit in 63. An invalid prefix MUST make the create operation throw an argument exception before any call to the engine or Docker.

#### Scenario: Uppercase prefix
- **WHEN** test code calls the create operation with prefix `Orders`
- **THEN** it throws an argument exception that names the prefix rule
- **AND** no `docker` command and no SQL statement was run

#### Scenario: Prefix too long
- **WHEN** test code calls the create operation with a prefix of 55 characters `a`
- **THEN** it throws an argument exception that names the 54-character limit

### Requirement: Fixture branches are short-lived and labeled
Every branch created by the testing library SHALL be short-lived with a time limit of 1 hour unless the caller sets another, and SHALL carry the label `sqlbranch.owner=testing` in addition to any labels the caller sets.

#### Scenario: Default time limit and owner label
- **WHEN** a test creates a branch without options at 2026-12-01T10:00:00Z (tested with a fake clock)
- **THEN** `sqlbranch branch list --format json` shows the branch with `ephemeral` true, `expiresAt` "2026-12-01T11:00:00Z" and label `sqlbranch.owner` "testing"

### Requirement: Fixture options are checked
The create operation MUST throw an argument exception, before any call to the engine or Docker, when the time limit is zero, negative or longer than 365 days, or when a caller-supplied label breaks the branch label rules or has a key starting with `sqlbranch.`.

#### Scenario: Time limit too long
- **WHEN** test code calls the create operation with a time limit of 366 days
- **THEN** it throws an argument exception that names the 365-day maximum and no `docker` command ran

#### Scenario: Zero time limit
- **WHEN** test code calls the create operation with a time limit of zero
- **THEN** it throws an argument exception that names the time limit

#### Scenario: Reserved label key
- **WHEN** test code calls the create operation with label `sqlbranch.owner=mine`
- **THEN** it throws an argument exception saying the `sqlbranch.` prefix is reserved

### Requirement: Cleanup after crashed runs
Before its first create in a process, the testing library SHALL remove the branches that are expired, short-lived and labeled `sqlbranch.owner=testing`, and no other branch. A failing prune SHALL be logged and SHALL NOT fail the create; it SHALL be tried again before the next create until one succeeds.

#### Scenario: Leftover from a killed test run
- **WHEN** expired fixture branch `t-1a2b3c4d` exists from a killed run and a new test process creates its first branch
- **THEN** `t-1a2b3c4d` no longer exists

#### Scenario: Expired branch from the command line survives
- **WHEN** branch `tmp1` was created with `sqlbranch branch create tmp1 --from shop --ephemeral --ttl 1m`, it has expired, and a new test process creates its first branch
- **THEN** `sqlbranch branch list` still shows `tmp1`

#### Scenario: First prune fails
- **WHEN** the first prune of a test process fails because removing expired branch `t-1a2b3c4d` fails (tested with a fake runtime)
- **THEN** the create still succeeds and the log names `t-1a2b3c4d`
- **AND** the next create in the same process runs the prune again and also succeeds

#### Scenario: Live branch of another test process survives
- **WHEN** another test process holds unexpired fixture branch `t-9f8e7d6c` and a new test process creates its first branch
- **THEN** `t-9f8e7d6c` still exists and still answers queries

### Requirement: Reset from test code
The object returned by the create operation SHALL offer a reset operation that returns the branch to its image state and keeps its name, connection string, expiry time and labels.

#### Scenario: Reset between tests
- **WHEN** the clone mode is `thin` or `copy`, a test deletes all rows from table `Customers` and the next test calls reset
- **THEN** `Customers` has the image's rows again
- **AND** the connection string and the expiry time are unchanged

### Requirement: Clear failure when the environment is missing
When the engine was never initialized, is stopped, or the image does not exist, the create operation MUST throw an exception whose message states the cause and names the `sqlbranch` command that fixes it.

#### Scenario: Engine not initialized
- **WHEN** test code calls the create operation on a machine where `sqlbranch init` never ran
- **THEN** the exception message says the engine was not found and names `sqlbranch init`

#### Scenario: Engine stopped
- **WHEN** the engine was initialized and then stopped with `sqlbranch engine stop`, and test code calls the create operation
- **THEN** the exception message says the engine is stopped and names `sqlbranch engine start`

#### Scenario: Image missing
- **WHEN** the engine is running, no image `shop` exists, and test code calls the create operation with image `shop`
- **THEN** the exception message says image `shop` does not exist and names `sqlbranch image build shop`

### Requirement: Secrets stay out of messages
Exception messages, the string form of the returned object and the library's log messages MUST NOT contain the engine password. Only the connection-string property SHALL contain it.

#### Scenario: Failing create
- **WHEN** a create fails because attaching the database fails
- **THEN** the exception's message and its string form, inner exceptions included, do not contain the engine password

#### Scenario: Object printed in a test log
- **WHEN** test code writes the returned object for branch `t-1a2b3c4d` to its output
- **THEN** the text contains `t-1a2b3c4d` and does not contain the engine password

### Requirement: Availability check for skipping
The testing library SHALL offer a check that returns whether an engine is running and reachable, without throwing, so that tests can be skipped where it is not.

#### Scenario: No Docker on the machine
- **WHEN** the availability check runs on a machine without Docker
- **THEN** it returns false and does not throw

#### Scenario: Docker present, engine stopped
- **WHEN** Docker is running, `sqlbranch init` ran, and the engine was stopped with `sqlbranch engine stop`
- **THEN** the availability check returns false and does not throw

#### Scenario: Engine running
- **WHEN** the engine is running
- **THEN** the availability check returns true

### Requirement: Concurrency limit
The testing library SHALL offer a per-process limit on live branches, off by default. With a limit of N, a create beyond N SHALL wait until a branch of that process is disposed, and SHALL stop waiting with a cancellation exception when its cancellation token is cancelled.

#### Scenario: Third create waits
- **WHEN** the limit is 2, a test process holds two live branches and calls create a third time
- **THEN** the third create does not finish until one of the two is disposed, and then succeeds

#### Scenario: Waiting create cancelled
- **WHEN** the limit is 1, one branch is live, and a second create's cancellation token is cancelled after 100 ms
- **THEN** the second create throws a cancellation exception and no third branch exists

### Requirement: Settings come from the usual places
The testing library SHALL read settings as the CLI does: `SQLBRANCH_` environment variables, then the nearest `.sqlbranch.yml` found by walking up from the process's current directory, then `~/.sqlbranch/config.yml`, then defaults. A config-file option SHALL replace the repository file, like `--config`.

#### Scenario: Test run from the build output folder
- **WHEN** `.sqlbranch.yml` in the repository root sets `port: 15000`, the engine listens on 15000, and tests run with current directory `tests/Shop.Tests/bin/Debug/net10.0`
- **THEN** the create operation succeeds and its connection string names port 15000

#### Scenario: Environment variable wins
- **WHEN** the engine listens on port 16000, `.sqlbranch.yml` in the repository root sets `port: 15000`, and `SQLBRANCH_PORT=16000` is set for the test process
- **THEN** the create operation succeeds and its connection string names port 16000

### Requirement: One package without a test framework
The library SHALL ship as a single package that carries everything it needs from SqlBranch, with no separate SqlBranch package to install. It MUST NOT depend on any test framework package.

#### Scenario: Package dependencies
- **WHEN** the package built by `dotnet pack` is inspected
- **THEN** its dependency list contains no xUnit, NUnit or MSTest package and no `SqlBranch.Core` package
- **AND** a test project that references only this package can create a branch
