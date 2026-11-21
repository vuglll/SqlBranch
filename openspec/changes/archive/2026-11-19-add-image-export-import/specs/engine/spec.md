# Spec Delta

## MODIFIED Requirements

### Requirement: Commands before init
`status`, `engine start`, `engine stop`, `teardown` and every image and branch command, `branch prune` included, MUST exit with code 3, `error.code` "not_initialized", and a message naming `sqlbranch init` when no SqlBranch engine container, managed pool file, `sqlbranch-pool` volume or marked pool directory exists. `skill export` needs no engine and SHALL work before init. `image import` SHALL check its archive head first (exit 1 `archive_invalid`).

#### Scenario: Engine start on a new machine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch engine start`
- **THEN** the command exits with code 3 and names `sqlbranch init`

#### Scenario: Branch create on a new machine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch branch create dev1 --from shop`
- **THEN** the command exits with code 3 and `error.code` is "not_initialized"

#### Scenario: Skill export on a new machine
- **WHEN** no pool, no engine and no Docker exist and the user runs `sqlbranch skill export --dir out/skill`
- **THEN** the command exits with code 0 and `out/skill/SKILL.md` exists

#### Scenario: Import of an invalid archive before init
- **WHEN** no pool and no engine exist and the user runs `sqlbranch image import notes.txt` on a text file
- **THEN** the command exits with code 1 and `error.code` is "archive_invalid", and no `docker` command ran
