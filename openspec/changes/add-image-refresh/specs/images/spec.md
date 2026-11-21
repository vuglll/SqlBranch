## MODIFIED Requirements

### Requirement: Image names are unique
Except for backup or scripts builds with `--replace`, a build with the name of an existing image MUST fail without changing the pool.

#### Scenario: Name already used
- **WHEN** image `shop` exists and the user runs `sqlbranch image build shop --from-bak other.bak`
- **THEN** the command exits with code 1 and says the image already exists
- **AND** the existing image is unchanged

#### Scenario: Publish a replacement
- **WHEN** the clone mode is `thin` or `copy` and shop exists and `image build shop --from-bak next.bak --replace` succeeds
- **THEN** the command exits 0, shop names the sealed replacement, and both versions appear in `image list --versions`

### Requirement: Failed builds leave nothing behind
When a build fails at any step, from a backup or from scripts, SqlBranch SHALL remove the temporary database, the build's login and all files the build created, except files a script placed outside the build folder, which it SHALL leave in place and name. A failed replacement MUST leave the previously published version current and its branches intact.

#### Scenario: Restore fails halfway
- **GIVEN** this is the first build of that name
- **WHEN** the clone mode is `thin` or `copy` and a build fails during restore because the pool runs out of space
- **THEN** the command exits with code 1
- **AND** `sqlbranch image list` does not show the image
- **AND** `sqlbranch status` exits with code 0 and reports no mismatches

#### Scenario: Pool fills up during a script build
- **WHEN** the clone mode is `copy` and the pool runs out of space while `002_seed.sql` of `sqlbranch image build shopdev --from-scripts db` inserts rows
- **THEN** the command exits with code 1 and names `002_seed.sql`
- **AND** `sqlbranch image list` does not show `shopdev`
- **AND** `sqlbranch status` exits with code 0 and reports no mismatches

#### Scenario: Masking fails before publication
- **WHEN** the clone mode is `thin` or `copy` and a replacement of shop fails in mask.sql
- **THEN** the build exits 1, only the previous shop version is listed, its branch dev1 can still commit a row, and status exits 0 after cleanup

### Requirement: Remove an image
`sqlbranch image rm <name>` SHALL remove the image's files and manifest, and MUST NOT delete or change any branch file. Removal SHALL cover every retained version. A cleanup retry MUST delete only the retired files. If a successor exists, it MUST exit 1 with `image_exists`, name the current version and explain that a new removal follows the normal --force rules.

#### Scenario: Unused image
- **WHEN** the clone mode is `thin` or `copy`, image `crm` has no branches, and the user runs `sqlbranch image rm crm`
- **THEN** the command exits with code 0 and `sqlbranch image list` no longer shows `crm`

#### Scenario: Unknown image for rm
- **WHEN** the user runs `sqlbranch image rm nope`
- **THEN** the command exits with code 1 and `error.code` is "image_not_found"

### Requirement: Imported image names are unique
`image import` with `--replace` SHALL publish the validated archive as a new local version and preserve existing branches and their bases. When the target name is taken without `--replace`, it SHALL exit with code 1 and `error.code` "image_exists", naming `--as`, before writing any file. An import and a build or another import of the same name at the same time SHALL leave exactly one of them succeeding.

#### Scenario: Name already used on import
- **WHEN** the clone mode is `thin` or `copy`, image `shop` exists, and the user runs `sqlbranch image import shop.sbimg`
- **THEN** the command exits with code 1, `error.code` is "image_exists", the message names `--as`, and image `shop` is unchanged

#### Scenario: Import and build of one name at once
- **WHEN** the clone mode is `copy` and `sqlbranch image import shop.sbimg --as crm` and `sqlbranch image build crm --from-bak crm.bak` run at the same moment
- **THEN** one exits with code 0 and the other with code 1, and `sqlbranch status` exits with code 0

#### Scenario: Replace an occupied import name
- **WHEN** the clone mode is `thin` or `copy`, dev1 pins shop version 1 and the user runs `sqlbranch image import nightly.sbimg --as shop --replace` with a valid archive
- **THEN** it exits 0, shop has two versions, a new branch reads the imported rows and dev1 can still reset to version 1

### Requirement: Failed imports leave nothing behind
When an import fails or is cancelled after it started writing, SqlBranch SHALL wait for its file stream into the engine to end, then remove its build folder, and the image folder only if this import created it. It MUST NOT remove an image folder it did not create, and the failed version MUST NOT be listed. A replacement failure MUST preserve the previous current version and its branches. When the client is killed, `status` SHALL list the import's build folder as a build leftover.

#### Scenario: Pool fills up during import
- **WHEN** the clone mode is `copy` and the pool runs out of space while `sqlbranch image import shop.sbimg --as shop2` writes the data file
- **THEN** the command exits with code 1, `sqlbranch image list` does not show `shop2`, and `sqlbranch status` exits with code 0

#### Scenario: Seal fails after the image folder was created
- **WHEN** the clone mode is `copy` and moving the files, setting their mode, or writing the manifest of `shop2` fails during `sqlbranch image import shop.sbimg --as shop2` (tested with a fake runtime, each step in turn)
- **THEN** the command exits with code 1, `/pool/images/shop2` does not exist, and `sqlbranch image import shop.sbimg --as shop2` then exits with code 0

#### Scenario: Cancelled import
- **WHEN** the clone mode is `thin` or `copy` and the process receives Ctrl+C while `sqlbranch image import shop.sbimg --as shop2` writes files
- **THEN** the command exits with a non-zero code, `sqlbranch image list` does not show `shop2`, and `sqlbranch status` exits with code 0

#### Scenario: Killed import
- **WHEN** the clone mode is `copy` and the client running `sqlbranch image import shop.sbimg --as shop2` was killed while writing files
- **THEN** `sqlbranch status` exits with code 1 and lists the import's build folder, and `sqlbranch image list` does not show `shop2`

#### Scenario: Replacement archive fails verification
- **WHEN** the clone mode is `thin` or `copy` and a file in a replacement archive for shop fails its SHA-256 check
- **THEN** import exits 1, only the original version is listed and dev1 can reset to its original rows

## ADDED Requirements

### Requirement: Publish complete versions
A backup or scripts build with `--replace` SHALL create the first version if absent. On refresh the old image SHALL remain current through restore, scripts, sealing and cleanup. Publication MUST make one complete version current in a single step. Existing branches MUST stay attached to their original versions. Competing builds of one name MUST follow the existing concurrent-build rule.

#### Scenario: First night
- **WHEN** the clone mode is `thin` or `copy` and shop is absent and `image build shop --from-scripts db --replace` succeeds
- **THEN** one current version of shop is listed, with zero branches

#### Scenario: Branch in use during masking
- **WHEN** the clone mode is `thin` or `copy` and dev1 holds an open connection and a replacement of shop pauses in its post-script
- **THEN** dev1 can read and commit before the script resumes, a new branch sees the old rows during the pause, and a create after success sees only the new image rows

#### Scenario: Overlapping replacements
- **WHEN** the clone mode is `thin` or `copy` and two valid replacements of shop overlap before either publishes
- **THEN** one exits 0, one exits 1, and the version count increases by one

### Requirement: Wait for active branch operations before publication
Build and import publication MUST wait up to 10 minutes for in-flight creates or resets on the image. While waiting they SHALL report progress on stderr. If the deadline expires they MUST exit 1 with `catalog_busy`, publish nothing and clean up the candidate.

#### Scenario: Reset holds the catalog through the deadline
- **WHEN** the clone mode is `copy` and an in-flight reset of dev1 holds shop for 10 minutes after a replacement is ready to publish
- **THEN** the writer reports that it is waiting for shop, exits 1 with `catalog_busy` at the deadline and only the previous version remains

### Requirement: Older clients cannot remove versioned images
Before publishing a versioned catalog the pool MUST enforce a format that pre-F4 image and branch writers refuse. Even a forced removal by an older client MUST fail before changing any image or branch files.

#### Scenario: Older client attempts forced removal
- **WHEN** the clone mode is `thin` or `copy`, shop has two versions and a pre-F4 client runs `image rm shop --force`
- **THEN** it exits 1 and both versions and their branch references remain intact

### Requirement: Space and recovery on refresh
A replacement SHALL check available space using the build source rules, counting retained versions as occupied. On exhaustion it MUST exit 1 with pool_full and available bytes, plus required bytes when known. Failure or cancellation before publication MUST remove only its own staged resources. If cleanup cannot finish, it MUST report the leftover and status MUST identify it for recovery.

#### Scenario: Too little space after staging
- **WHEN** the clone mode is `thin` or `copy` and shop exists, free space starts at 40 GiB and the local 20 GiB backup needs 50 GiB restored
- **THEN** refresh exits 1 with pool_full, 50 GiB needed and 20 GiB available, removes the staged backup and leaves shop current

#### Scenario: Publication fails
- **WHEN** the clone mode is `thin` or `copy` and publishing a sealed second version fails before the switch
- **THEN** refresh exits 1, status exits 0 after cleanup, and a new branch of shop reads the first version

#### Scenario: Crash before switch
- **WHEN** the clone mode is `thin` or `copy` and the client is killed after sealing and before publication
- **THEN** shop still serves the first version, status exits 1 and identifies the unpublished build until its debris is cleaned

### Requirement: Inspect image versions
Each sealed version SHALL have a unique display ID of the form <name>@<UTC timestamp>. `image list --versions` SHALL list IDs newest first within each name, size in bytes (sum of sealed file lengths), current flag and exact branch count including broken or expired records. Ordinary list and status SHALL count logical images. Ordinary list SHALL count branches across retained versions.

#### Scenario: Two versions of shop
- **WHEN** the clone mode is `thin` or `copy` and shop has one branch on its first version and two on its second
- **THEN** ordinary list reports shop once with 3 branches, versions lists the current row with 2 and the older row with 1, and status counts one image

### Requirement: Prune unreferenced image versions
Across all images, `image prune --keep <n>` SHALL retain the newest n versions per name including current, plus every older referenced version with no cap. It MUST recheck eligibility under exclusion with create, reset, publish, export and remove before retiring a version. If reference data is unreadable it MUST fail closed with exit 1. Repeating a successful prune SHALL exit 0.

#### Scenario: Referenced versions beyond retention
- **WHEN** the clone mode is `thin` or `copy` and shop has four versions in publication order, dev1 pins the first, and crm has one
- **THEN** prune --keep 2 removes only the second shop version, exits 0 and leaves dev1 writable; a repeat reports zero removals

#### Scenario: Export still reading an older version
- **WHEN** the clone mode is `copy`, export is reading shop version 1, version 2 publishes and prune --keep 1 tries to retire version 1
- **THEN** version 1 remains readable through the last export read and the archive contains only version 1

#### Scenario: Unknown reference
- **WHEN** the clone mode is `thin` or `copy` and a branch record is unreadable at the reference scan
- **THEN** prune exits 1 naming the record and deletes no candidate covered by that scan

### Requirement: Retire versions safely
Prune MUST unlist a candidate before deleting its files. Image rm MUST apply its force check across all versions and unlist the entire name before deleting files. Forced removal MUST identify affected branches and loss of reset bases. Neither command may delete branch files. Failure before unlisting MUST exit 1 and keep the target intact. After unlisting, deletion failure MUST exit 1, identify debris in status and allow cleanup on retry.

#### Scenario: Reference to noncurrent base
- **WHEN** the clone mode is `thin` or `copy` and only the oldest shop version has a branch dev1
- **THEN** image rm shop exits 1 with image_in_use naming dev1 and --force, and every version remains available

#### Scenario: Forced removal of history
- **WHEN** the clone mode is `thin` or `copy` and image rm shop --force removes two versions including dev1’s base
- **THEN** it exits 0 naming dev1 and its missing reset base, neither version is listed, dev1 keeps its edits and its existing connection can commit

#### Scenario: Retirement fails before deletion
- **WHEN** the clone mode is `thin` or `copy` and an injected retirement rename fails during prune or image rm
- **THEN** the operation exits 1, its target remains listed and intact and no temporary retirement record remains

#### Scenario: Deletion fails after retirement
- **WHEN** the clone mode is `thin` or `copy` and deleting a retired shop version fails during prune or image rm
- **THEN** the command exits 1 with its leftover path, status exits 1 naming it, and rerunning the same command after fixing the fault clears the debris without touching dev1

### Requirement: Retry only the interrupted removal
A retry of image rm with pending deletion work SHALL finish only the recorded removal and report which versions it cleaned. It MUST NOT remove a successor that acquired the same name. If branch dependencies cannot be read reliably, a new removal MUST exit 1 before retiring any version, including with --force.

#### Scenario: Name reused before cleanup
- **WHEN** the clone mode is `copy`, a forced removal retired shop but left files, and a new shop exists when the user retries the removal
- **THEN** the retry removes only the retired files, exits 1 with `image_exists`, names the current version and leaves it and its branches intact. The message says to run a new `image rm shop` to remove it and that branches require `--force`

#### Scenario: Unknown dependents during forced removal
- **WHEN** the clone mode is `thin` or `copy` and image rm shop --force encounters an unreadable branch record
- **THEN** it exits 1 naming the record, leaves both shop versions listed and deletes no file

### Requirement: Validate refresh arguments locally
Refresh commands MUST reject invalid names, options, arity and retention before any Docker command with exit 2. Keep is required exactly once and accepts only ASCII digits 0-9 representing 1 through 2147483647. Version IDs are display values and MUST NOT be accepted as image names. BACPAC with --replace MUST exit 2 with usage_error before Docker. Replacement builds inherit all local build input and secret checks.

#### Scenario: BACPAC replacement is unsupported
- **WHEN** the user runs `sqlbranch image build shop --from-bacpac shop.bacpac --replace`
- **THEN** it exits 2 with `usage_error` before any Docker command, download or SqlPackage invocation

#### Scenario: Non-ASCII digit
- **WHEN** the user runs `sqlbranch image prune --keep ２`
- **THEN** it exits 2 with `usage_error` before any Docker command

#### Scenario: Missing keep
- **WHEN** the user runs `sqlbranch image prune`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Missing count
- **WHEN** the user runs `sqlbranch image prune --keep`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Zero count
- **WHEN** the user runs `sqlbranch image prune --keep 0`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Negative count
- **WHEN** the user runs `sqlbranch image prune --keep -1`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Fractional count
- **WHEN** the user runs `sqlbranch image prune --keep 1.5`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Nonnumeric count
- **WHEN** the user runs `sqlbranch image prune --keep abc`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Count overflow
- **WHEN** the user runs `sqlbranch image prune --keep 2147483648`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Repeated keep
- **WHEN** the user runs `sqlbranch image prune --keep 1 --keep 2`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Unexpected image operand
- **WHEN** the user runs `sqlbranch image prune shop --keep 2`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Unsupported force
- **WHEN** the user runs `sqlbranch image prune --force --keep 1`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Invalid replacement name
- **WHEN** the user runs `sqlbranch image build "Shop DB" --from-scripts db --replace`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Missing replacement source
- **WHEN** the user runs `sqlbranch image build shop --replace`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Conflicting replacement sources
- **WHEN** the user runs `sqlbranch image build shop --replace --from-scripts db --from-bak shop.bak`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Value on replace
- **WHEN** the user runs `sqlbranch image build shop --from-scripts db --replace=yes`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Value on versions
- **WHEN** the user runs `sqlbranch image list --versions=yes`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

#### Scenario: Version used as image name
- **WHEN** the user runs `sqlbranch image rm shop@20261119T020000000Z`
- **THEN** the command exits 2 before any Docker command, identifies the invalid or missing input and changes nothing

### Requirement: Refresh diagnostics keep secrets private
Refresh and scheduler examples MUST keep engine, build-login and storage secrets out of text and JSON results, diagnostics, errors, manifests, logs and spawned process arguments. URL builds SHALL use the existing secret sources and reject secret-bearing URLs before Docker without echoing the secret.

#### Scenario: Pasted signed URL
- **WHEN** a replacement build receives a backup URL with query sig=sentinel
- **THEN** it exits 2 before Docker and neither output stream contains sentinel

#### Scenario: Authenticated restore fails
- **WHEN** the clone mode is `thin` or `copy` and a replacement fails after accepting an S3 secret set to a test sentinel
- **THEN** it exits 1 and no captured stream, log, error, manifest or child argv contains the sentinel

### Requirement: Refresh output for scripts
With `--format json`, build SHALL add image.version, version listing SHALL return versions with name, version, current, sizeBytes and branches, and prune SHALL return removed IDs including on partial failure. All SHALL use schemaVersion 1. Existing output fields on ordinary build and list MUST retain their types and meanings.

#### Scenario: List the first version in JSON
- **WHEN** the clone mode is `copy`, shop was published at 02:00:00.000 UTC on 2026-11-19 with 8192 bytes of sealed files and no branches, and the user runs `sqlbranch image list --versions --format json`
- **THEN** the command exits 0 with schemaVersion 1 and versions equal to `[{"name":"shop","version":"shop@20261119T020000000Z","current":true,"sizeBytes":8192,"branches":0}]`

### Requirement: Schedule a masked refresh
The image guide SHALL give copy-paste nightly job examples for cron, launchd, Windows Task Scheduler and scheduled CI. Each SHALL export to shared storage only after a successful cloud build with masking. The developer instructions MUST cover both empty and occupied names, and state when a reset discards local edits.

#### Scenario: Masking script exits unsuccessfully in the nightly job
- **WHEN** the documented job runs against a fake CLI whose masked build exits 1
- **THEN** the job reports failure, makes zero export calls and never invokes image rm or branch reset
