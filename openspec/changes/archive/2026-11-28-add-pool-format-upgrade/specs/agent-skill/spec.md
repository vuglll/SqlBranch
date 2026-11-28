## MODIFIED Requirements

### Requirement: Guardrails
The guardrails section SHALL tell agents to work only in branches they created, never to run destructive SQL against a server that is not the SqlBranch engine, to reset or remove their branches when done, never to weaken a test to hide a database problem, and never to run `sqlbranch pool upgrade` unless the user asked for it, because it cannot be undone and stops SqlBranch 0.2.x tools.

#### Scenario: Guardrail text
- **WHEN** the skill is generated
- **THEN** the guardrails section contains all five rules, and the fifth names `sqlbranch pool upgrade` and says it cannot be undone

#### Scenario: Pool upgrade reference entry
- **WHEN** the skill is generated
- **THEN** the reference entry for `pool upgrade` shows `sqlbranch pool upgrade --yes` and says it destroys the pool's compatibility with SqlBranch 0.2.x tools and cannot be undone
