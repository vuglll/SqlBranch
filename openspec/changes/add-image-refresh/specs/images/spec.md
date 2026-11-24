## MODIFIED Requirements

### Requirement: Image names are unique
A build or import with the name of an existing image MUST fail without changing the pool. On a pool that uses format 2, a name exists when the image catalog lists a current version of it, or when an image of that name written by an older client is present. The only exception is a backup or scripts build, or an import, with `--replace` on a pool that uses format 2.

#### Scenario: Name already used
- **WHEN** image `shop` exists and the user runs `sqlbranch image build shop --from-bak other.bak`
- **THEN** the command exits with code 1 and says the image already exists
- **AND** the existing image is unchanged

#### Scenario: Name already used on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has version `shop@20261201T090000000Z`, and the user runs `sqlbranch image build shop --from-scripts db`
- **THEN** the command exits with code 1, `error.code` is "image_exists", and `sqlbranch image list --format json` shows shop with `version` "shop@20261201T090000000Z"

#### Scenario: Name used by an image an older client wrote
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, and image `crm` was written in the 0.2.2 layout after the upgrade (tested by writing its files directly)
- **THEN** `sqlbranch image build crm --from-bak crm.bak` exits with code 1, `error.code` is "image_exists", and crm's files are unchanged

#### Scenario: Publish a replacement
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has one version `shop@20261201T020000000Z`, and `sqlbranch image build shop --from-bak next.bak --replace` runs at 2026-12-02T02:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0 and reports version `shop@20261202T020000000Z`
- **AND** `sqlbranch image list --versions` lists both versions, with only `shop@20261202T020000000Z` marked current

### Requirement: Failed builds leave nothing behind
When a build fails at any step, from a backup or from scripts, SqlBranch SHALL remove the temporary database, the build's login and all files the build created, except files a script placed outside the build folder, which it SHALL leave in place and name. A failed replacement MUST leave the previously current version current and its branches intact.

#### Scenario: Restore fails halfway
- **WHEN** the clone mode is `thin` or `copy`, no image of that name existed before, and a build fails during restore because the pool runs out of space
- **THEN** the command exits with code 1
- **AND** `sqlbranch image list` does not show the image
- **AND** `sqlbranch status` exits with code 0 and reports no mismatches

#### Scenario: Pool fills up during a script build
- **WHEN** the clone mode is `copy` and the pool runs out of space while `002_seed.sql` of `sqlbranch image build shopdev --from-scripts db` inserts rows
- **THEN** the command exits with code 1 and names `002_seed.sql`
- **AND** `sqlbranch image list` does not show `shopdev`
- **AND** `sqlbranch status` exits with code 0 and reports no mismatches

#### Scenario: Masking fails before publication
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, branch `dev1` was created from `shop@20261201T020000000Z`, and `sqlbranch image build shop --from-bak next.bak --replace --post-script mask.sql` fails in `mask.sql`
- **THEN** the command exits with code 1 and `error.code` is "script_failed"
- **AND** `sqlbranch image list --versions` lists only `shop@20261201T020000000Z`, `dev1` can commit a row, and `sqlbranch status` exits with code 0

### Requirement: Remove an image
`sqlbranch image rm <name>` SHALL remove the image's files and manifest, and MUST NOT delete or change any branch file. On a pool that uses format 2 it SHALL remove every retained version of the name from the image catalog, and its JSON `image.versions` SHALL list the removed version IDs, newest first. On a pool that uses format 1, `image.versions` SHALL be [].

#### Scenario: Unused image
- **WHEN** the clone mode is `thin` or `copy`, image `crm` has no branches, and the user runs `sqlbranch image rm crm`
- **THEN** the command exits with code 0 and `sqlbranch image list` no longer shows `crm`

#### Scenario: Unknown image for rm
- **WHEN** the user runs `sqlbranch image rm nope`
- **THEN** the command exits with code 1 and `error.code` is "image_not_found"

#### Scenario: Remove on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, crm has version `crm@20261201T090000000Z` and no branches, and the user runs `sqlbranch image rm crm --format json`
- **THEN** the command exits with code 0, `image.versions` is ["crm@20261201T090000000Z"], and `sqlbranch image list` no longer shows crm

#### Scenario: Remove on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, crm has no branches, and the user runs `sqlbranch image rm crm --format json`
- **THEN** the command exits with code 0 and `image.versions` is []

#### Scenario: Unused image with two versions
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has versions `shop@20261201T020000000Z` and `shop@20261202T020000000Z` and no branches, and the user runs `sqlbranch image rm shop --format json`
- **THEN** the command exits with code 0 and `image.versions` is ["shop@20261202T020000000Z", "shop@20261201T020000000Z"]
- **AND** `sqlbranch image list --versions` lists no version of shop

### Requirement: Imported image names are unique
`image import` without `--replace` MUST NOT replace an existing image. When the target name is taken, it SHALL exit with code 1 and `error.code` "image_exists", naming `--as` and `--replace`, before writing any file. An import and a build or another import of the same name at the same time SHALL leave exactly one of them succeeding.

#### Scenario: Name already used on import
- **WHEN** the clone mode is `thin` or `copy`, image `shop` exists, and the user runs `sqlbranch image import shop.sbimg`
- **THEN** the command exits with code 1, `error.code` is "image_exists", the message names `--as` and `--replace`, and image `shop` is unchanged

#### Scenario: Import and build of one name at once
- **WHEN** the clone mode is `copy` and `sqlbranch image import shop.sbimg --as crm` and `sqlbranch image build crm --from-bak crm.bak` run at the same moment
- **THEN** one exits with code 0 and the other with code 1, and `sqlbranch status` exits with code 0

### Requirement: Failed imports leave nothing behind
When an import fails or is cancelled after it started writing, SqlBranch SHALL wait for its file stream into the engine to end, then remove its build folder, and the image folder only if this import created it. It MUST NOT remove an image folder it did not create, and the failed version MUST NOT be listed. When the client is killed, `status` SHALL list the import's build folder as a build leftover.

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

## ADDED Requirements

### Requirement: Replace an image on import
On a pool that uses format 2, `image import <archive> --replace` SHALL verify the archive with every existing import check and then publish it as a new current version of the target name, creating the name if it is absent. Existing versions and their branches MUST stay unchanged. A failed replacement import MUST leave the previously current version current.

#### Scenario: Replace an occupied import name
- **WHEN** the clone mode is `thin` or `copy`, `dev1` was created from `shop@20261201T020000000Z`, and the user runs `sqlbranch image import nightly.sbimg --as shop --replace` at 2026-12-02T07:00:00.000Z with a valid archive (tested with a fake clock)
- **THEN** the command exits with code 0 and reports version `shop@20261202T070000000Z`
- **AND** a new branch of shop reads the archive's rows, and `sqlbranch branch reset dev1` exits 0 and restores the rows of `shop@20261201T020000000Z`

#### Scenario: Replacement archive fails verification
- **WHEN** the clone mode is `thin` or `copy` and a file in `nightly.sbimg` fails its SHA-256 check during `sqlbranch image import nightly.sbimg --as shop --replace`
- **THEN** the command exits with code 1 and `error.code` is "archive_corrupt"
- **AND** `sqlbranch image list --versions` lists only `shop@20261201T020000000Z`, and `sqlbranch branch reset dev1` exits 0 with that version's rows

### Requirement: Publish the first version with replace
On a pool that uses format 2, a backup or scripts build with `--replace` for a name that does not exist SHALL build and publish it as the name's first version, the same as a build without `--replace`.

#### Scenario: First night
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, no image `shop` exists, and `sqlbranch image build shop --from-scripts db --replace` runs at 2026-12-01T02:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0
- **AND** `sqlbranch image list --versions` lists one version, `shop@20261201T020000000Z`, current, with 0 branches

### Requirement: The current version stays usable during a replacement
While a replacement build or import runs, the previously current version SHALL stay current and usable for new branches until the new version is published. Branches already attached MUST keep reading and committing throughout.

#### Scenario: Branch in use during masking
- **WHEN** the clone mode is `thin` or `copy`, `dev1` holds an open connection, and a replacement of shop is paused inside its post-build script (tested with a barrier in the script)
- **THEN** `dev1` can read and commit a row during the pause
- **AND** `sqlbranch branch create dev2 --from shop` during the pause exits 0 and `dev2` reads the old rows

### Requirement: Publication switches versions in one step
A replacement SHALL make the new version current only after restore, every script, sealing and build cleanup have succeeded, and in one step: no command SHALL ever see a mix of old and new files under one version. Existing branches MUST stay on the versions they were created from.

#### Scenario: Create after publication
- **WHEN** the clone mode is `thin` or `copy`, `dev1` was created from `shop@20261201T020000000Z`, and `sqlbranch image build shop --from-bak next.bak --replace` exits 0 with version `shop@20261202T020000000Z`
- **THEN** `sqlbranch branch create dev2 --from shop` exits 0 and `dev2` reads only the rows of `next.bak`
- **AND** `dev1` still reads its own rows and `sqlbranch branch list` shows it on `shop@20261201T020000000Z`

### Requirement: Overlapping replacements of one name
When two replacement builds or imports of the same name overlap, exactly one SHALL publish and exit 0. The other MUST exit 1 with `error.code` "image_exists", say that another build or import of the name is running, publish nothing and remove what it created.

#### Scenario: Overlapping replacements
- **WHEN** the clone mode is `thin` or `copy`, shop has 1 version, and two `sqlbranch image build shop --from-bak next.bak --replace` commands overlap before either publishes
- **THEN** one exits with code 0 and the other with code 1 and `error.code` "image_exists"
- **AND** `sqlbranch image list --versions` lists 2 versions of shop, and `sqlbranch status` exits with code 0

### Requirement: Publication waits for branch operations
Before publishing, a replacement build or import SHALL wait for creates and resets of the same name that are already running, for at most the `publishWait` setting (default 10 minutes). While it waits it SHALL print a progress line on standard error that names the image.

#### Scenario: A reset finishes within the wait
- **WHEN** the clone mode is `copy`, `publishWait` is `10m`, and a reset of `dev1` from shop is still running for 2 minutes after a replacement of shop is ready to publish (tested with a fake clock)
- **THEN** standard error says the build is waiting for operations on image shop, and the build exits 0 after the reset finishes

### Requirement: A waiting publication does not block new branch operations
While a replacement build or import waits to publish, a `branch create` or `branch reset` of the same name that starts SHALL NOT wait for the publication: it SHALL use the version that is current when it starts and run to completion.

#### Scenario: Create arrives while a publisher waits
- **WHEN** the clone mode is `thin` or `copy`, shop's current version is `shop@20261201T020000000Z`, a reset of `dev1` holds shop, and a replacement of shop is waiting to publish
- **THEN** `sqlbranch branch create dev9 --from shop --format json` started during the wait exits 0 without waiting for the publication, with `branch.imageVersion` "shop@20261201T020000000Z"
- **AND** the replacement publishes after the reset and the create both finish

### Requirement: Publication wait timeout
When the `publishWait` time runs out before running creates and resets of the name finish, a replacement build or import MUST exit 1 with `error.code` "catalog_busy", publish nothing, remove its unpublished version and leave the previous version current.

#### Scenario: Reset holds the image through the deadline
- **WHEN** the clone mode is `copy`, `publishWait` is `10m`, and a reset of `dev1` keeps running for 11 minutes after a replacement of shop is ready to publish (tested with a fake clock)
- **THEN** the build exits with code 1 at the 10-minute mark and `error.code` is "catalog_busy"
- **AND** `sqlbranch image list --versions` lists only the previous version, and `sqlbranch status` exits 0 once the reset ends

### Requirement: Space checks on refresh
A replacement SHALL check free space with the same rules as a first build of its source, counting retained versions as used space. When space runs out it MUST exit 1 with `error.code` "pool_full", name the available bytes and, when known, the required bytes, remove its staged files and leave the previous version current.

#### Scenario: Too little space for the restore
- **WHEN** the clone mode is `thin` or `copy`, shop exists, the pool has 40 GiB free, and `sqlbranch image build shop --from-bak shop.bak --replace` stages a 20 GiB local backup whose database needs 50 GiB
- **THEN** the command exits with code 1, `error.code` is "pool_full", and the message names 50 GiB needed and 20 GiB available
- **AND** the staged backup is removed and `sqlbranch image list --versions` lists only the previous version

### Requirement: Failed publication keeps the previous version
When a replacement fails or is cancelled after sealing and before publication, it MUST exit with a non-zero code, remove its unpublished version and leave the previous version current. If removing it fails, the command SHALL name the leftover path and `status` SHALL list it.

#### Scenario: Publication fails
- **WHEN** the clone mode is `thin` or `copy` and publishing a sealed second version of shop fails before the switch (tested with a fake runtime)
- **THEN** the command exits with code 1, `sqlbranch status` exits with code 0, and a new branch of shop reads the first version's rows

#### Scenario: Crash before switch
- **WHEN** the clone mode is `thin` or `copy` and the client is killed after sealing a second version of shop and before publication
- **THEN** a new branch of shop reads the first version's rows, and `sqlbranch status` exits with code 1 and lists the unpublished version's folder as a build leftover

### Requirement: List image versions
`sqlbranch image list --versions` SHALL list every retained version, grouped by image name in name order and newest first within a name, with its ID, whether it is current, its size in bytes (the sum of its sealed file lengths) and the number of branches whose base it is, broken and expired branches included.

#### Scenario: Two versions of shop
- **WHEN** the clone mode is `thin` or `copy`, one branch was created from `shop@20261201T020000000Z` and two from the current `shop@20261202T020000000Z`, and the user runs `sqlbranch image list --versions`
- **THEN** the command exits with code 0 and lists `shop@20261202T020000000Z` current with 2 branches, then `shop@20261201T020000000Z` with 1 branch

### Requirement: Ordinary list counts all versions
On a pool that uses format 2, `sqlbranch image list` SHALL show one row per image name, describing its current version, with a branch count summed across all its retained versions. `status` SHALL count image names, not versions.

#### Scenario: Ordinary list after a refresh
- **WHEN** the clone mode is `thin` or `copy`, one branch was created from `shop@20261201T020000000Z` and two from the current `shop@20261202T020000000Z`
- **THEN** `sqlbranch image list` exits with code 0 and shows shop once with 3 branches, and `sqlbranch status --format json` reports `images` 1

### Requirement: Prune keeps the newest versions and every base
On a pool that uses format 2, `sqlbranch image prune --keep <n>` SHALL, for every image name, keep the newest n versions, the current one included, plus every older version that is the base of any branch (online, offline, broken or expired), with no cap. It SHALL remove every other version. Running it again with nothing to remove SHALL exit 0 with `removed` [].

#### Scenario: Referenced versions beyond retention
- **WHEN** the clone mode is `thin` or `copy`, shop has versions `shop@20261201T020000000Z`, `shop@20261202T020000000Z`, `shop@20261203T020000000Z` and `shop@20261204T020000000Z`, `dev1` was created from the first, crm has one version, and the user runs `sqlbranch image prune --keep 2 --format json`
- **THEN** the command exits with code 0 and `removed` is ["shop@20261202T020000000Z"], and `dev1` can still commit a row
- **AND** running the same command again exits with code 0 and `removed` is []

### Requirement: Prune keeps the possible bases of unknown-base branches
While a branch of an image name has an unknown base, `image prune` SHALL keep the version that `pool upgrade` created for that name, whatever `--keep` says.

#### Scenario: Legacy branch with an unknown base
- **WHEN** the clone mode is `thin` or `copy`, `pool upgrade` created `shop@20261201T090000000Z`, later refreshes added `shop@20261202T020000000Z` and `shop@20261203T020000000Z`, branch `old1` of shop has an unknown base, and the user runs `sqlbranch image prune --keep 1 --format json`
- **THEN** the command exits with code 0 and `removed` is ["shop@20261202T020000000Z"]
- **AND** `sqlbranch image list --versions` lists `shop@20261203T020000000Z` (current) and `shop@20261201T090000000Z`

### Requirement: Prune skips versions being exported
When an `image export` is reading a version that prune would remove, prune SHALL leave that version in place and list it in `skipped` with reason "export_in_progress", continue with other versions, and exit 0 if nothing else failed.

#### Scenario: Export still reading an older version
- **WHEN** the clone mode is `copy`, an export of shop started while `shop@20261201T020000000Z` was current and is still streaming, `shop@20261202T020000000Z` has since been published, no branch uses either, and the user runs `sqlbranch image prune --keep 1 --format json`
- **THEN** the command exits with code 0, `removed` is [] and `skipped` is [{"image":"shop","version":"shop@20261201T020000000Z","reason":"export_in_progress"}]
- **AND** `sqlbranch image list --versions` lists both versions, and the finished archive holds the rows of `shop@20261201T020000000Z`

### Requirement: Prune skips a name being built
When a build, import or removal of an image name is running, `image prune` SHALL remove no version of that name, list the name in `skipped` with `version` null and reason "image_busy", continue with other names, and exit 0 if nothing else failed.

#### Scenario: Nightly build running during prune
- **WHEN** the clone mode is `thin` or `copy`, a replacement build of shop is restoring, crm has versions `crm@20261201T020000000Z` and `crm@20261202T020000000Z` with no branches, and the user runs `sqlbranch image prune --keep 1 --format json`
- **THEN** the command exits with code 0, `removed` is ["crm@20261201T020000000Z"] and `skipped` is [{"image":"shop","version":null,"reason":"image_busy"}]

### Requirement: Prune skips names with running branch operations
When a `branch create` or `branch reset` of an image name is running, `image prune` SHALL remove no version of that name, list each version it would have removed in `skipped` with reason "catalog_busy", continue with other names without waiting, and exit 0 if nothing else failed.

#### Scenario: Reset running during prune
- **WHEN** the clone mode is `thin` or `copy`, shop has unused older version `shop@20261201T020000000Z` and current `shop@20261202T020000000Z`, a reset of `dev1` to `shop@20261202T020000000Z` is running, and the user runs `sqlbranch image prune --keep 1 --format json`
- **THEN** the command exits with code 0 without waiting, `removed` is [] and `skipped` is [{"image":"shop","version":"shop@20261201T020000000Z","reason":"catalog_busy"}]
- **AND** after the reset ends, the same command exits 0 with `removed` ["shop@20261201T020000000Z"]

### Requirement: Prune fails closed on unreadable branch records
On a pool that uses format 2, if any branch record cannot be read when prune checks references, prune MUST remove no version of any image, exit 1 with `error.code` "operation_failed", name the unreadable record, and report `removed` [].

#### Scenario: Unknown reference
- **WHEN** the clone mode is `thin` or `copy`, the record of branch `x1` is not valid JSON, and shop and crm each have an unused older version, and the user runs `sqlbranch image prune --keep 1 --format json`
- **THEN** the command exits with code 1, `error.code` is "operation_failed", the message names the record of `x1`, and `removed` is []
- **AND** both older versions are still listed by `sqlbranch image list --versions`

### Requirement: Prune needs a running engine
`image prune` MUST exit with code 3 and change nothing when the engine is stopped (`error.code` "engine_stopped") or SqlBranch was never initialized (`error.code` "not_initialized").

#### Scenario: Prune with the engine stopped
- **WHEN** the engine was stopped with `sqlbranch engine stop` and the user runs `sqlbranch image prune --keep 1`
- **THEN** the command exits with code 3, `error.code` is "engine_stopped", and the message names `sqlbranch engine start`

#### Scenario: Prune before init
- **WHEN** no pool and no engine exist and the user runs `sqlbranch image prune --keep 1`
- **THEN** the command exits with code 3 and `error.code` is "not_initialized"

### Requirement: Prune unlists before it deletes
Prune MUST make a version unlisted and unusable for new branches before deleting its files. If unlisting fails, prune MUST exit 1, keep that version listed and intact, and stop.

#### Scenario: Retirement fails before deletion
- **WHEN** the clone mode is `thin` or `copy` and unlisting `shop@20261202T020000000Z` fails during `sqlbranch image prune --keep 1` (tested with a fake runtime)
- **THEN** the command exits with code 1, `removed` is [], and `shop@20261202T020000000Z` is still listed and its files are unchanged

### Requirement: Prune deletion failures can be retried
When deleting an unlisted version's files fails, prune MUST exit 1, still list its ID in `removed`, and name the leftover path. `status` SHALL list the path. Running prune again after the fault is fixed SHALL delete only those leftovers, exit 0 and list the cleaned IDs in `removed`.

#### Scenario: Deletion fails after retirement
- **WHEN** the clone mode is `thin` or `copy`, `dev1` uses `shop@20261203T020000000Z`, and deleting the files of `shop@20261201T020000000Z` fails during `sqlbranch image prune --keep 1 --format json`
- **THEN** the command exits with code 1, `removed` is ["shop@20261201T020000000Z"], and `sqlbranch status` exits with code 1 and names the leftover path
- **AND** after the fault is fixed, the same command exits 0 with `removed` ["shop@20261201T020000000Z"], `sqlbranch status` exits 0, and `dev1` is unchanged

### Requirement: Prune deletes only the versions it unlisted
`image prune` MUST delete only the files of versions it unlisted in that run or that an earlier prune unlisted and recorded. It MUST NOT delete or change any branch file, any file of another image name or of a kept version, or any build, import or reset workspace.

#### Scenario: Branch files survive a prune
- **WHEN** the clone mode is `thin`, then `copy`, shop has versions `shop@20261201T020000000Z`, `shop@20261202T020000000Z` and `shop@20261203T020000000Z`, `dev1` uses the newest with committed writes, crm has one version, a reset workspace of `dev2` and a build of `sales` are in progress, and the user runs `sqlbranch image prune --keep 1`
- **THEN** the command exits with code 0, and the SHA-256 of every file of `dev1`, of crm, of `shop@20261203T020000000Z`, and of the workspaces of `dev2` and `sales` is unchanged

### Requirement: Removal checks branches across all versions
On a pool that uses format 2, `image rm <name>` without `--force` MUST exit 1 with `error.code` "image_in_use" when any branch uses any version of the name, list those branches, name `--force`, and leave every version listed.

#### Scenario: Reference to noncurrent base
- **WHEN** the clone mode is `thin` or `copy`, shop has two versions and only `dev1`, created from the older one, uses shop, and the user runs `sqlbranch image rm shop`
- **THEN** the command exits with code 1, `error.code` is "image_in_use", the message names `dev1` and `--force`, and both versions remain listed

### Requirement: Removal waits for running branch operations
On a pool that uses format 2, when a `branch create` or `branch reset` of the name is running, `image rm <name>` SHALL wait for it for at most `publishWait`. When the wait runs out it MUST exit 1 with `error.code` "catalog_busy", name the image, and leave every version listed and intact.

#### Scenario: Reset holds the image during removal
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `publishWait` is `10m`, and a reset of `dev1` from shop keeps running for 11 minutes after `sqlbranch image rm shop --force` starts (tested with a fake clock)
- **THEN** the removal exits with code 1 at the 10-minute mark, `error.code` is "catalog_busy", and `sqlbranch image list --versions` still lists every version of shop

#### Scenario: Create finishes within the wait
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `publishWait` is `10m`, crm has no branches, and a create of `tmp1` from crm finishes 1 minute after `sqlbranch image rm crm --force` starts (tested with a fake clock)
- **THEN** the removal exits with code 0, names `tmp1` as a branch that lost its reset base, and `tmp1` keeps its data

### Requirement: Forced removal names lost reset bases
On a pool that uses format 2, `image rm <name> --force` SHALL unlist every version of the name in one step before deleting files, and SHALL name each branch that loses its reset base together with that version ID. Those branches MUST keep their data and open connections.

#### Scenario: Forced removal of history
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has two versions and `dev1` was created from `shop@20261201T020000000Z`, and the user runs `sqlbranch image rm shop --force`
- **THEN** the command exits with code 0 and names `dev1` and `shop@20261201T020000000Z`
- **AND** no version of shop is listed, `dev1` keeps its edits, and its open connection can commit a row

### Requirement: Removal unlisting failure changes nothing
On a pool that uses format 2, when unlisting the name fails, `image rm` MUST exit 1 and leave every version listed and intact.

#### Scenario: Unlisting fails during removal
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has two versions and the rename that unlists shop fails during `sqlbranch image rm shop` (tested with a fake runtime)
- **THEN** the command exits with code 1, both versions remain listed, and no removal record remains in the pool

### Requirement: Retry only the interrupted removal
On a pool that uses format 2, when an earlier `image rm` unlisted a name but could not delete its files, running `image rm <name>` again SHALL delete only those recorded leftovers and list the cleaned version IDs. If no image of that name exists afterwards it SHALL exit 0. It MUST NOT remove a newer image that took the same name.

#### Scenario: Retry after a deletion failure
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, `sqlbranch image rm shop` unlisted `shop@20261201T020000000Z` and `shop@20261202T020000000Z` but deleting their files failed, and the fault is fixed
- **THEN** `sqlbranch image rm shop --format json` exits with code 0 and `image.versions` is ["shop@20261202T020000000Z", "shop@20261201T020000000Z"]
- **AND** `sqlbranch status` exits with code 0

### Requirement: Retry stops at a newer image of the same name
On a pool that uses format 2, when a newer image took the name before the retry, `image rm <name>` SHALL clean the recorded leftovers, then exit 1 with `error.code` "image_exists", name the newer current version, and say that a new `image rm <name>` removes it and needs `--force` if it has branches.

#### Scenario: Name reused before cleanup
- **WHEN** the clone mode is `copy`, the pool uses format 2, a forced removal unlisted shop but left its files, then `sqlbranch image build shop --from-bak shop.bak` published `shop@20261203T020000000Z` with branch `dev2`, and the user runs `sqlbranch image rm shop`
- **THEN** the command removes only the leftover files and exits with code 1 and `error.code` "image_exists"
- **AND** the message names `shop@20261203T020000000Z`, `image rm shop` and `--force`, and `dev2` and the new version are unchanged

### Requirement: Removal fails closed on unreadable branch records
On a pool that uses format 2, when any branch record cannot be read, `image rm` MUST exit 1 with `error.code` "operation_failed" and name the unreadable record before unlisting any version, with or without `--force`.

#### Scenario: Unknown dependents during forced removal
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, shop has two versions, the record of branch `x1` is not valid JSON, and the user runs `sqlbranch image rm shop --force`
- **THEN** the command exits with code 1, `error.code` is "operation_failed", the message names the record of `x1`, both shop versions remain listed, and no file is deleted

### Requirement: Prune count is checked before any action
`image prune` MUST take `--keep` exactly once, with a value of ASCII digits `0`-`9` only, leading zeros allowed, between 1 and 2147483647. Any other value, a missing or repeated `--keep`, an image operand or an unknown option MUST exit 2 with `error.code` "usage_error", name the input and run no Docker command.

#### Scenario: Prune input table
- **WHEN** the user runs `sqlbranch image prune` with each of these arguments: (none), `--keep`, `--keep 0`, `--keep -1`, `--keep +1`, `--keep 1.5`, `--keep abc`, `--keep ２` (full-width digit), `--keep 2147483648`, `--keep 1 --keep 2`, `shop --keep 2`, `--force --keep 1`
- **THEN** each exits with code 2 and `error.code` "usage_error", names `--keep` or the unexpected argument, and runs no Docker command

#### Scenario: Leading zeros are accepted
- **WHEN** the clone mode is `thin` or `copy` and the user runs `sqlbranch image prune --keep 007`
- **THEN** the command keeps the newest 7 versions of each name, the same as `--keep 7`, and exits with code 0

### Requirement: Replace and versions flags take no value
`--replace` on `image build` and `image import`, and `--versions` on `image list`, MUST be given without a value. A value such as `--replace=yes` MUST exit 2 with `error.code` "usage_error" before any Docker command.

#### Scenario: Value on replace
- **WHEN** the user runs `sqlbranch image build shop --from-scripts db --replace=yes`
- **THEN** the command exits with code 2, `error.code` is "usage_error", it names `--replace`, and no Docker command runs

#### Scenario: Value on versions
- **WHEN** the user runs `sqlbranch image list --versions=yes`
- **THEN** the command exits with code 2, `error.code` is "usage_error", it names `--versions`, and no Docker command runs

### Requirement: Replacement builds keep every build input check
A build with `--replace` MUST apply every input check of the same build without `--replace`, with the same exit codes and error codes, before any Docker command.

#### Scenario: Invalid replacement name
- **WHEN** the user runs `sqlbranch image build "Shop DB" --from-scripts db --replace`
- **THEN** the command exits with code 2, `error.code` is "invalid_name", and no Docker command runs

#### Scenario: Missing replacement source
- **WHEN** the user runs `sqlbranch image build shop --replace`
- **THEN** the command exits with code 2, `error.code` is "usage_error", and no Docker command runs

#### Scenario: Conflicting replacement sources
- **WHEN** the user runs `sqlbranch image build shop --replace --from-scripts db --from-bak shop.bak`
- **THEN** the command exits with code 2, `error.code` is "usage_error", and no Docker command runs

### Requirement: BACPAC builds cannot replace
`image build --from-bacpac` with `--replace` MUST exit 2 with `error.code` "usage_error" before any Docker command, download or SqlPackage run, and the message SHALL say that BACPAC builds cannot replace an image.

#### Scenario: BACPAC replacement is unsupported
- **WHEN** the user runs `sqlbranch image build shop --from-bacpac shop.bacpac --replace`
- **THEN** the command exits with code 2 and `error.code` is "usage_error", with zero Docker commands, downloads and SqlPackage runs

### Requirement: Refresh keeps secrets private
Replacement builds and imports, prune and the documented scheduler examples MUST keep the engine password, the build login password and storage secrets out of text and JSON output, errors, manifests, logs and the arguments of every process SqlBranch starts. URL builds SHALL take secrets only from the existing secret sources.

#### Scenario: Pasted signed URL
- **WHEN** the user runs `sqlbranch image build shop --replace --from-bak "https://acme.blob.core.windows.net/backups/shop.bak?sv=2024&sig=sentinel"`
- **THEN** the command exits with code 2 before any Docker command and neither output stream contains `sentinel`

#### Scenario: Authenticated restore fails
- **WHEN** the clone mode is `thin` or `copy`, `SQLBRANCH_S3_SECRET_ACCESS_KEY` is `sentinel-s3`, and a replacement from an `s3://` URL fails during restore
- **THEN** the command exits with code 1 and no output stream, log, error, manifest or recorded child argument contains `sentinel-s3`

### Requirement: Export reads the version current when it starts
On a pool that uses format 2, `image export <name>` SHALL write the version that is current when the export starts, even if a newer version is published or prune runs while it reads, and the archive MUST hold only that version's files.

#### Scenario: Publication during an export
- **WHEN** the clone mode is `thin` or `copy`, shop's current version is `shop@20261201T020000000Z`, `sqlbranch image export shop --to shop.sbimg` is paused after it started reading (tested with a barrier), and a replacement of shop then publishes `shop@20261202T020000000Z`
- **THEN** the export exits with code 0 and `sqlbranch image import shop.sbimg --as old1` then exits 0 with the rows of `shop@20261201T020000000Z`

### Requirement: Version list JSON
With `--format json`, `image list --versions` SHALL return `versions`, an array of objects with `name`, `version`, `current` (boolean), `sizeBytes` (integer) and `branches` (integer), in the listed order, with `schemaVersion` 1.

#### Scenario: List the first version in JSON
- **WHEN** the clone mode is `copy`, shop was published at 2026-11-19T02:00:00.000Z with 8192 bytes of sealed files and no branches, and the user runs `sqlbranch image list --versions --format json`
- **THEN** the command exits 0 with `schemaVersion` 1 and `versions` equal to `[{"name":"shop","version":"shop@20261119T020000000Z","current":true,"sizeBytes":8192,"branches":0}]`

### Requirement: Prune JSON
With `--format json`, `image prune` SHALL return `removed` (version IDs in the order they were unlisted) and `skipped` (objects with `image`, `version` and `reason`: "export_in_progress", "image_busy" or "catalog_busy"), both always present. On exit 1 the document SHALL hold `error` together with `removed` and `skipped` so a script sees partial progress.

#### Scenario: Nothing to prune
- **WHEN** the clone mode is `thin` or `copy`, every image has one version, and the user runs `sqlbranch image prune --keep 1 --format json`
- **THEN** the command exits with code 0, `schemaVersion` is 1, `removed` is [] and `skipped` is []

### Requirement: Nightly job examples
The image guide SHALL give a copy-paste nightly job for cron, a scheduled CI job, launchd and Windows Task Scheduler around one fail-fast job body that builds with `--replace` and masking, then exports. Examples not yet run on their platform SHALL be marked "not verified".

#### Scenario: Masking script exits unsuccessfully in the nightly job
- **WHEN** the documented job body runs against a recording CLI whose `image build` exits 1
- **THEN** the job exits non-zero and the recording shows zero `image export`, `image rm` and `branch reset` calls

### Requirement: Developer import instructions
The image guide SHALL tell a developer how to take the nightly archive into an empty pool with `image import` and into a pool that already has the name with `image import --replace`, and SHALL state that `branch reset <name> --latest` discards the branch's local edits.

#### Scenario: Guide covers both pools
- **WHEN** a reviewer reads the nightly section of `docs/images.md`
- **THEN** it shows `sqlbranch image import shop.sbimg`, `sqlbranch image import shop.sbimg --replace` and `sqlbranch branch reset dev1 --latest`, and says that the reset discards local edits
