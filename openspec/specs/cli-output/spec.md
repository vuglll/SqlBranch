# cli-output Specification

## Purpose
Defines what every `sqlbranch` command prints and returns, so that people can
read the output and scripts, CI jobs and AI agents can rely on it.

## Requirements

### Requirement: Text and JSON formats
Every command SHALL print human-readable text by default and SHALL print a single JSON document on standard output when `--format json` is given.

#### Scenario: JSON output parses
- **WHEN** the user runs `sqlbranch branch list --format json`
- **THEN** standard output is one valid JSON document and nothing else

### Requirement: JSON envelope
Every JSON document SHALL contain `schemaVersion` with the value 1, `command`, `ok`, and `elapsedMs`. On failure it SHALL also contain `error` with a stable `code` and a `message`.

#### Scenario: Successful command
- **WHEN** `sqlbranch branch create pr-142 --from shop --format json` succeeds
- **THEN** the document has `schemaVersion` 1, `command` "branch create", `ok` true, and a `branch` object with `name`, `image`, `cloneMode`, `server`, `database` and `user`, and no password

#### Scenario: Failed command
- **WHEN** the same command fails because the image does not exist
- **THEN** the document has `ok` false and `error.code` "image_not_found"

### Requirement: Exit codes
Commands SHALL exit with 0 on success, 1 when the operation failed, 2 for a usage or configuration error, and 3 when the environment is not ready.

#### Scenario: Unknown option
- **WHEN** the user runs `sqlbranch branch create pr-1 --frm shop`
- **THEN** the command exits with code 2

#### Scenario: Engine not running
- **WHEN** the engine is stopped and the user runs `sqlbranch branch list`
- **THEN** the command exits with code 3 and says how to start the engine

### Requirement: Diagnostics go to standard error
Progress messages, warnings and error text SHALL go to standard error, and results SHALL go to standard output.

#### Scenario: Piped JSON stays clean
- **WHEN** a command prints a warning while run with `--format json`
- **THEN** the warning is on standard error and standard output still parses as JSON

### Requirement: Commands never prompt
No command SHALL wait for interactive input. A command that needs a decision MUST fail and name the flag that supplies it.

#### Scenario: Forced removal needs a flag
- **WHEN** the user runs `sqlbranch image rm shop` while branches use the image and standard input is not a terminal
- **THEN** the command exits with code 1 without waiting and names `--force`

### Requirement: Quiet mode
With `--quiet`, a successful command in text format SHALL print only its essential result.

#### Scenario: Quiet connection string
- **WHEN** the user runs `sqlbranch branch conn pr-142 --quiet`
- **THEN** standard output is the connection string alone on one line

### Requirement: Clone mode is always visible
Any command that creates image or branch files SHALL state the clone mode in both text and JSON output.

#### Scenario: Copy mode shown on create
- **WHEN** a branch is created in `copy` mode
- **THEN** the text output contains the word `copy` next to the clone mode label and the JSON `cloneMode` is "copy"

#### Scenario: Clone mode shown on image build
- **WHEN** the clone mode is `thin` and the user runs `sqlbranch image build shop --from-bak shop.bak --format json`
- **THEN** the JSON `cloneMode` is "thin"
