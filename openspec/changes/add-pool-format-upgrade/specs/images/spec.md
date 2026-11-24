## MODIFIED Requirements

### Requirement: Image names are unique
A build or import with the name of an existing image MUST fail without changing the pool. On a pool that uses format 2, a name exists when the image catalog lists a current version of it, or when an image of that name written by an older client is present.

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

### Requirement: Remove an image
`sqlbranch image rm <name>` SHALL remove the image's files and manifest, and MUST NOT delete or change any branch file. On a pool that uses format 2 it SHALL remove the name from the image catalog, and its JSON `image.versions` SHALL list the removed version IDs. On a pool that uses format 1, and for an image written in the format-1 layout that no command has adopted yet, `image.versions` SHALL be [].

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

#### Scenario: Remove an image not adopted yet
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, image `crm` was written in the 0.2.2 layout after the upgrade (tested by writing its files directly) and has no branches, and the user runs `sqlbranch image rm crm --format json`
- **THEN** the command exits with code 0, `image.versions` is [], and `sqlbranch image list` no longer shows crm

## ADDED Requirements

### Requirement: Builds and imports publish versions on a format-2 pool
On a pool that uses format 2, every `image build` source (backup file, backup URL, scripts, BACPAC) and `image import` SHALL publish the new image as the first version of its name, with a version ID, and only after the image is sealed. A failed build or import MUST NOT add a version.

#### Scenario: Script build on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, and `sqlbranch image build shopdev --from-scripts db` publishes at 2026-12-02T02:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0, `sqlbranch image list --format json` shows shopdev with `version` "shopdev@20261202T020000000Z", and `sqlbranch branch create d1 --from shopdev` exits 0

#### Scenario: BACPAC build and import on a format-2 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, and the user runs `sqlbranch image build sales --from-bacpac sales.bacpac` and `sqlbranch image import shop.sbimg --as shop3`
- **THEN** both exit with code 0 and `sqlbranch image list --format json` shows sales and shop3, each with a non-null `version`

#### Scenario: Failed build on a format-2 pool
- **WHEN** the clone mode is `copy`, the pool uses format 2, and `sqlbranch image build shopdev --from-scripts db` fails in `002_seed.sql`
- **THEN** the command exits with code 1, `sqlbranch image list` does not show shopdev, and `sqlbranch status` exits with code 0

### Requirement: Image version IDs
On a pool that uses format 2, every version SHALL have an ID of the form `<name>@<yyyyMMdd>T<HHmmssfff>Z`, the UTC publication time to the millisecond. IDs of one name MUST be unique and increase with publication order, also when the clock goes back or the name is removed and used again.

#### Scenario: Name removed and rebuilt in one millisecond
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, crm was published as `crm@20261201T020000000Z`, and `sqlbranch image rm crm` and `sqlbranch image build crm --from-bak crm.bak` both run while the fake clock reads 2026-12-01T02:00:00.000Z
- **THEN** the build exits 0 and `sqlbranch image list --format json` shows crm with `version` "crm@20261201T020000001Z"

#### Scenario: Clock moved back
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, crm was published as `crm@20261201T020000000Z` and then removed, and the next build of crm publishes while the fake clock reads 2026-11-30T02:00:00.000Z
- **THEN** the new version is `crm@20261201T020000001Z`

### Requirement: Version IDs are not image names
A version ID MUST NOT be accepted where an image name is expected. Such input MUST exit 2 with `error.code` "invalid_name" before any Docker command.

#### Scenario: Version used as image name
- **WHEN** the user runs `sqlbranch image rm shop@20261119T020000000Z`
- **THEN** the command exits with code 2, `error.code` is "invalid_name", and no Docker command runs

### Requirement: Build JSON reports the version
With `--format json`, a successful `image build` or `image import` SHALL add `image.version` to its existing fields, which keep their types and meanings: the version ID (string) on a pool that uses format 2, and null on a pool that uses format 1. `image list` rows SHALL add `version` the same way, null also for an image written in the format-1 layout that no command has adopted yet.

#### Scenario: Version in build JSON
- **WHEN** the clone mode is `copy`, the pool uses format 2, and `sqlbranch image build shop --from-scripts db --format json` publishes at 2026-12-01T02:00:00.000Z (tested with a fake clock)
- **THEN** the command exits with code 0, `schemaVersion` is 1, `image.version` is "shop@20261201T020000000Z" and `image.cloneMode` is "copy"

#### Scenario: Version in build JSON on a format-1 pool
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 1, and the user runs `sqlbranch image build shop --from-scripts db --format json`
- **THEN** the command exits with code 0 and `image.version` is null

### Requirement: Unreadable image catalog
On a pool that uses format 2, an image whose `catalog.json` cannot be parsed SHALL still be listed, with `version` null and `catalogReadable` false, and `status` SHALL report it as a mismatch. `image rm` SHALL remove it and keep the name's high-water mark. Every other command that uses the name MUST exit 1 with `error.code` "operation_failed", name `sqlbranch image rm <name>`, and change nothing.

#### Scenario: Catalog flag on readable catalogs
- **WHEN** the pool uses format 2 with readable catalogs, or uses format 1, and the user runs `sqlbranch image list --format json`
- **THEN** every row has `catalogReadable` true on format 2 and null on format 1

#### Scenario: List an image with an unreadable catalog
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, and the `catalog.json` of image crm holds invalid JSON (tested by writing it directly)
- **THEN** `sqlbranch image list --format json` exits with code 0 and shows crm with `version` null and `catalogReadable` false, the text output marks crm "catalog unreadable", and `sqlbranch status` exits with code 1 and names `sqlbranch image rm crm`

#### Scenario: Remove an image with an unreadable catalog
- **WHEN** the clone mode is `thin` or `copy`, the pool uses format 2, crm has an unreadable catalog and no branches, and the user runs `sqlbranch image rm crm --format json`
- **THEN** the command exits with code 0, `image.versions` is [], `sqlbranch image list` no longer shows crm, and a later build of crm gets a version ID above crm's high-water mark

#### Scenario: Use an image with an unreadable catalog
- **WHEN** the pool uses format 2, crm has an unreadable catalog, and the user runs `sqlbranch branch create d1 --from crm` or `sqlbranch image export crm --to crm.sbimg`
- **THEN** each command exits with code 1, `error.code` is "operation_failed", the message names `sqlbranch image rm crm`, and no file in the pool changes

### Requirement: Unreadable credentials on a format-2 pool
When the engine credentials file cannot be parsed and the pool holds format-2 content (an `images/.ids` directory or any image `catalog.json`), `image list` and `image export` MUST exit 1 with `error.code` "operation_failed" and "The engine credentials file in <pool> is not valid." instead of showing a format-1 view. Without format-2 content they SHALL behave as in 0.2.2.

#### Scenario: Image list with unreadable credentials on a format-2 pool
- **WHEN** the pool was upgraded to format 2 and its `credentials.json` then holds invalid JSON (tested by writing it directly)
- **THEN** `sqlbranch image list` and `sqlbranch image export shop --to shop.sbimg` exit with code 1, `error.code` is "operation_failed", and neither prints any image row

