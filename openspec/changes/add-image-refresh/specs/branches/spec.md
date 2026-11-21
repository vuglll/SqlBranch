## MODIFIED Requirements

### Requirement: Reset a branch
`sqlbranch branch reset <name>` SHALL close open connections to the branch and return it to the exact state of its pinned source image version, keeping its name and connection string. If its base was removed or cannot be identified, plain reset MUST exit 1 with `image_not_found` before closing any connection and mention `--latest` to adopt a current version.

#### Scenario: Reset after changes
- **WHEN** the clone mode is `thin` or `copy`, a table is dropped in branch `pr-142`, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 0
- **AND** the table exists again with the image's rows
- **AND** the connection string from before the reset still works

#### Scenario: Plain reset keeps its base
- **WHEN** the clone mode is `thin` or `copy` and dev1 on the first shop version has local edits and shop was refreshed to a second version
- **THEN** branch reset dev1 exits 0, replaces its edits with the first version’s rows, and keeps its reference to the first version

### Requirement: A broken branch can be reset again
Running `branch reset` on a `broken` branch SHALL retry the reset from its image using the same pinned-base selection and removed-base safeguards as an online branch.

#### Scenario: Retry after a failed reset
- **WHEN** the clone mode is `thin`, branch `pr-142` is `broken`, and the user runs `sqlbranch branch reset pr-142`
- **THEN** the command exits with code 0 and `sqlbranch branch list` shows `pr-142` as `online`

## ADDED Requirements

### Requirement: Track the branch base
Create SHALL pin the complete current version. Branch list SHALL report the pinned ID and whether it is older than current, independently of online, offline, broken or expired status. A removed base MUST be marked removed, even after name reuse. A legacy branch with an identifiable original base SHALL retain that base on first refresh.

#### Scenario: Legacy branch after first refresh
- **WHEN** the clone mode is `thin` or `copy` and a pre-upgrade shop and its dev1 branch exist when shop is replaced
- **THEN** dev1 still reads its local edits, list shows state online, the first version ID and outdated true; a newly created dev2 shows the second ID and outdated false

### Requirement: Explicit reset to latest
`branch reset <name> --latest` SHALL adopt the current version and keep name, connection details, copy policy, labels and expiry. A removed base MUST block plain reset with exit 1 even after name reuse; explicit --latest may adopt a same-name successor. Without a current target --latest MUST exit 1 before closing connections. Repeated --latest MUST exit 2 before Docker.

#### Scenario: Name reused after forced removal
- **WHEN** the clone mode is `thin` or `copy` and dev1 lost its shop base, F3 imports shop under that freed name and dev1 still has local writes
- **THEN** plain reset exits 1 with `image_not_found`, mentions `--latest` and leaves dev1 unchanged
- **AND** `branch reset dev1 --latest` exits 0, replaces the writes with the imported rows and list shows the new base

#### Scenario: Import fails after forced removal
- **WHEN** the clone mode is `thin` or `copy` and the import of shop fails and no current shop exists
- **THEN** reset dev1 --latest exits 1 with image_not_found before drop, dev1 stays online with its edits, and no staged reset files remain

#### Scenario: Invalid branch name
- **WHEN** the user runs `sqlbranch branch reset master --latest`
- **THEN** it exits 2 before Docker and names the reserved name

#### Scenario: Missing branch name
- **WHEN** the user runs `sqlbranch branch reset --latest`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Value on latest
- **WHEN** the user runs `sqlbranch branch reset dev1 --latest=yes`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Repeated latest
- **WHEN** the user runs `sqlbranch branch reset dev1 --latest --latest`
- **THEN** it exits 2 with `usage_error` before any Docker command

### Requirement: Reset failure preserves reference safety
Reset MUST stage its target under protection from pruning and release temporary resources on every exit. A pre-drop failure MUST exit 1 and preserve branch and reference. After drop, a failure MUST exit 1, retain a broken branch record and protect its last confirmed base until recovery. Residual resources MUST be identified by status. Only a successful attach and record update may report the new base.

#### Scenario: Cannot stage latest
- **WHEN** the clone mode is `thin` or `copy` and a fault interrupts preparation for reset dev1 --latest
- **THEN** it exits 1, dev1 remains online on its old base with its edits, and no staged files remain after cleanup

#### Scenario: Attach fails after drop
- **WHEN** the clone mode is `thin` or `copy` and attachment fails while moving dev1 from the first to second shop version
- **THEN** reset exits 1, removes partial attachment and temporary files, lists dev1 as broken on the first base, and prune --keep 1 retains it; after repair, reset --latest succeeds and the first base has zero references

### Requirement: Branch version output
Branch create and reset SHALL report the chosen imageVersion in their JSON branch object. Each branch list row SHALL include imageVersion and outdated. Outdated SHALL be true for a retained older base, false for current and null for removed or unknown. A missing legacy ID SHALL be null. Text SHALL identify the same base status.

#### Scenario: Removed base in JSON
- **WHEN** the clone mode is `thin` or `copy`, shop was forcibly removed after dev1 was created from shop@20261119T020000000Z, and no reset has occurred
- **THEN** branch list exits 0 and its dev1 row has imageVersion "shop@20261119T020000000Z", imageRemoved true, outdated null and state "online"
