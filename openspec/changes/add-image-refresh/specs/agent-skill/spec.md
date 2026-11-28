## MODIFIED Requirements

### Requirement: Guardrails
The guardrails section SHALL tell agents to work only in branches they created, never to run destructive SQL against a server that is not the SqlBranch engine, to reset or remove their branches when done, never to weaken a test to hide a database problem, never to run `sqlbranch pool upgrade` unless the user asked for it, because it cannot be undone and stops SqlBranch 0.2.x tools, and never to run `sqlbranch image prune` unless the user asked for it.

#### Scenario: Guardrail text
- **WHEN** the skill is generated
- **THEN** the guardrails section contains all six rules, the fifth names `sqlbranch pool upgrade` and says it cannot be undone, and the sixth names `sqlbranch image prune`

#### Scenario: Pool upgrade reference entry
- **WHEN** the skill is generated
- **THEN** the reference entry for `pool upgrade` shows `sqlbranch pool upgrade --yes` and says it destroys the pool's compatibility with SqlBranch 0.2.x tools and cannot be undone

## ADDED Requirements

### Requirement: Reference entries for image refresh commands
The generated reference entries SHALL show `image prune` with an example and what it deletes, and SHALL show the `--latest` option of `branch reset` with what it discards.

#### Scenario: Prune reference entry
- **WHEN** the skill is generated
- **THEN** the reference entry for `image prune` shows `sqlbranch image prune --keep 2` and says it deletes older image versions that no branch uses

#### Scenario: Reset to latest is marked as discarding edits
- **WHEN** the skill is generated
- **THEN** the reference entry for `branch reset` shows `--latest` and says it discards the branch's local edits
