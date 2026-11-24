## MODIFIED Requirements

### Requirement: Guardrails
The guardrails section SHALL tell agents to work only in branches they created, never to run destructive SQL against a server that is not the SqlBranch engine, to reset or remove their branches when done, never to weaken a test to hide a database problem, and never to run `sqlbranch pool upgrade` or `sqlbranch image prune` unless the user asked for it.

#### Scenario: Guardrail text
- **WHEN** the skill is generated
- **THEN** the guardrails section contains all six rules, and the last two name `sqlbranch pool upgrade` and `sqlbranch image prune`

#### Scenario: Prune reference entry
- **WHEN** the skill is generated
- **THEN** the reference entry for `image prune` shows `sqlbranch image prune --keep 2` and says it deletes older image versions that no branch uses

#### Scenario: Reset to latest is marked as discarding edits
- **WHEN** the skill is generated
- **THEN** the reference entry for `branch reset` shows `--latest` and says it discards the branch's local edits
