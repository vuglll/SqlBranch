# agent-skill Specification

## Purpose
Ships a skill file that teaches AI coding agents when and how to use
SqlBranch, and the guardrails that keep them inside their own branches.

## Requirements

### Requirement: Skill is generated from command metadata
The skill SHALL be generated from metadata declared by each command: its purpose, when to use it, an example command line, and what it destroys (`nothing` for commands that remove nothing). `dotnet build` MUST fail when a command omits any of these four fields, and the unit tests MUST fail when one is empty.

#### Scenario: Command without an example
- **WHEN** a command is added whose metadata has no example
- **THEN** `dotnet build` fails and the error names the command's source file and the field `Example`

#### Scenario: Empty field
- **WHEN** a command's metadata sets its purpose to an empty string
- **THEN** `dotnet test` fails and names the command and the field `Purpose`

#### Scenario: Read-only command
- **WHEN** the skill is generated
- **THEN** the reference entry for `branch list` says it destroys nothing, and the entry for `branch prune` says it removes expired short-lived branches and their data

### Requirement: Required sections
The generated SKILL.md SHALL contain these sections: when to use, setup check, getting a branch, working in a branch, cleaning up, guardrails.

#### Scenario: Sections present
- **WHEN** the skill is generated
- **THEN** SKILL.md contains all six section headings

### Requirement: Guardrails
The guardrails section SHALL tell agents to work only in branches they created, never to run destructive SQL against a server that is not the SqlBranch engine, to reset or remove their branches when done, never to weaken a test to hide a database problem, and never to run `sqlbranch pool upgrade` unless the user asked for it, because it cannot be undone and stops SqlBranch 0.2.x tools.

#### Scenario: Guardrail text
- **WHEN** the skill is generated
- **THEN** the guardrails section contains all five rules, and the fifth names `sqlbranch pool upgrade` and says it cannot be undone

#### Scenario: Pool upgrade reference entry
- **WHEN** the skill is generated
- **THEN** the reference entry for `pool upgrade` shows `sqlbranch pool upgrade --yes` and says it destroys the pool's compatibility with SqlBranch 0.2.x tools and cannot be undone

### Requirement: Size limit
SKILL.md MUST stay under 200 lines. Per-command details SHALL live in reference files that the skill links to.

#### Scenario: Many commands
- **WHEN** the tool has 20 commands
- **THEN** SKILL.md is under 200 lines and each command has an entry in a reference file

### Requirement: Committed copy stays current
The generated skill SHALL be committed under `skills/sqlbranch/`. The unit test suite run by `dotnet test` MUST fail when the committed copy differs from freshly generated output, and the failure message SHALL give the command that regenerates it.

#### Scenario: Stale committed skill
- **WHEN** a command's example changes and `skills/sqlbranch/` is not regenerated
- **THEN** `dotnet test` fails, names the file that differs, and prints the regenerate command

### Requirement: Export into a project
`sqlbranch skill export --dir <path>` SHALL write SKILL.md and the reference files into the folder, creating it if needed, and SHALL report the files written. It needs no engine and no Docker.

#### Scenario: Export into a new folder
- **WHEN** `.claude/skills/sqlbranch` does not exist and the user runs `sqlbranch skill export --dir .claude/skills/sqlbranch --format json`
- **THEN** the command exits with code 0, `SKILL.md` exists in the folder, and `written` lists `SKILL.md` and every reference file

### Requirement: Export does not overwrite without force
When any file the export would write already exists, `skill export` MUST exit with code 1, `error.code` "skill_exists", name `--force`, and change nothing. With `--force` it SHALL overwrite only the files it generates and SHALL leave every other file in the folder unchanged.

#### Scenario: Export into an existing folder
- **WHEN** `.claude/skills/sqlbranch/SKILL.md` already exists and the user runs `sqlbranch skill export --dir .claude/skills/sqlbranch`
- **THEN** the command exits with code 1 and names `--force`
- **AND** the existing files are unchanged

#### Scenario: Forced export keeps the user's files
- **WHEN** the folder holds an older generated `SKILL.md` and the user's own `notes.md`, and the user runs `sqlbranch skill export --dir .claude/skills/sqlbranch --force`
- **THEN** the command exits with code 0, `SKILL.md` matches the generated output, and `notes.md` is unchanged

### Requirement: Export failures are reported
`skill export` MUST exit with code 2 and `error.code` "usage_error", writing nothing, when the target folder or its parent cannot be created or written. It SHALL write the files into a temporary folder `<dir>.sqlbranch-tmp-<8 hex characters>` next to the target and move them into place only at the end, so that a failure exits with code 1, leaves the target as it was and removes the temporary folder.

#### Scenario: Target is a file
- **WHEN** `out` is an existing regular file and the user runs `sqlbranch skill export --dir out/skill`
- **THEN** the command exits with code 2, names `out/skill` and writes nothing

#### Scenario: Write fails halfway
- **WHEN** `out/skill` holds an older generated `SKILL.md`, and writing the third generated file fails (tested with a fake file system) during `sqlbranch skill export --dir out/skill --force --format json`
- **THEN** the command exits with code 1 and `error.message` names the third file
- **AND** `out/skill` holds exactly the files it held before, unchanged, and no temporary folder is left next to it

#### Scenario: Parent folder not writable
- **WHEN** `out/skill` exists and is writable but `out` is read-only, and the user runs `sqlbranch skill export --dir out/skill --force`
- **THEN** the command exits with code 2, names `out` and writes nothing

### Requirement: Stale temporary folders are removed
When a killed export left folders named `<dir>.sqlbranch-tmp-` followed by exactly 8 lowercase hexadecimal characters next to the target, the next `skill export` to that target SHALL delete them, and SHALL NOT delete any other folder.

#### Scenario: Leftover from a killed export
- **WHEN** `out/skill.sqlbranch-tmp-1a2b3c4d`, `out/skill.sqlbranch-tmp-keep` and `out/other` exist and the user runs `sqlbranch skill export --dir out/skill`
- **THEN** the command exits with code 0
- **AND** `out/skill.sqlbranch-tmp-1a2b3c4d` no longer exists, and `out/skill.sqlbranch-tmp-keep` and `out/other` are unchanged
