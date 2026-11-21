## ADDED Requirements

### Requirement: Export an image
`sqlbranch image export <name> --to <file>` SHALL write one compressed archive holding the image's manifest, its sealed database files and a SHA-256 for each file, and SHALL leave the image unchanged. Text and JSON output SHALL name the image, the destination, the archive size and the image size. The export MUST fail when the engine's file stream ends with an error or an entry's size differs from the manifest.

#### Scenario: Export to a local file
- **WHEN** the clone mode is `thin` or `copy`, image `shop` has one data file and one log file totalling 3 GiB, and the user runs `sqlbranch image export shop --to out/shop.sbimg --format json`
- **THEN** the command exits with code 0, `command` is "image export", and `export` has `image` "shop", `to` "out/shop.sbimg", `files` 2, `imageBytes` 3221225472 and `archiveBytes` equal to the size of `out/shop.sbimg`
- **AND** the SHA-256 of every file of image `shop` is unchanged

#### Scenario: File size differs from the manifest
- **WHEN** the clone mode is `copy` and the engine's stream for image `shop` delivers its data file 4096 bytes shorter than the manifest says (tested with a fake runtime)
- **THEN** `sqlbranch image export shop --to shop.sbimg` exits with code 1 and `shop.sbimg` does not exist

#### Scenario: Image does not exist
- **WHEN** the user runs `sqlbranch image export nope --to nope.sbimg`
- **THEN** the command exits with code 1, `error.code` is "image_not_found", and `nope.sbimg` does not exist

### Requirement: Archives hold one image and nothing else
An export archive SHALL be one compressed stream whose compression its first bytes identify, and SHALL hold exactly the image's manifest, the files the manifest lists, and their SHA-256 values. Import SHALL read every compression this version's export can write. It MUST NOT hold branch files, other images, credentials, logins, passwords, storage secrets, build folders or engine settings.

#### Scenario: Compression is recorded in the archive
- **WHEN** the clone mode is `thin` or `copy` and `sqlbranch image export shop --to shop.sbimg` exits with code 0
- **THEN** the first bytes of `shop.sbimg` are the gzip magic bytes `1f 8b`, unless spike 1.1 chose zstd, in which case they are `28 b5 2f fd`

#### Scenario: Archive content with branches present
- **WHEN** the clone mode is `thin` or `copy`, branches `pr-1` and `pr-2` exist from image `shop`, and `sqlbranch image export shop --to shop.sbimg` exits with code 0
- **THEN** the archive's entries are the manifest, the two files named in the manifest and the checksum list, in that order
- **AND** no entry contains the engine password or the name `pr-1`

### Requirement: Export to cloud storage
`--to` SHALL accept an `https://` Azure Blob Storage URL or an `s3://` URL in the forms `image build --from-bak` accepts, and write the archive as one blob or object at that URL without a temporary copy on the client. `--s3-region` SHALL set the region of an `s3://` URL, `us-east-1` by default.

#### Scenario: Export to S3-compatible storage
- **WHEN** the clone mode is `thin` or `copy`, a local S3-compatible server with a certificate the client trusts has bucket `images`, the S3 secrets are set, and `shop` is exported to `s3://172.17.0.5:9000/images/nightly/shop.sbimg`
- **THEN** the export exits with code 0 and object `nightly/shop.sbimg` in bucket `images` has the size reported as `archiveBytes`

#### Scenario: Export to Azure Blob Storage
- **WHEN** the clone mode is `copy`, `SQLBRANCH_AZURE_SAS_TOKEN` grants read, create and write on container `images`, and the user runs `sqlbranch image export shop --to https://acme.blob.core.windows.net/images/shop.sbimg` (tested with a fake storage service)
- **THEN** the command exits with code 0 and the blob is committed once, after its last block was uploaded

### Requirement: Export input is checked first
`image export` MUST exit with code 2 before running any `docker` command when the name breaks the image name rules (`error.code` "invalid_name"), `--to` is missing, `--to` is an existing directory, its parent folder does not exist or it lies under a pool directory, or `--s3-region` is misused as for `image build` (`error.code` "usage_error").

#### Scenario: Invalid image name on export
- **WHEN** the user runs `sqlbranch image export "Shop DB" --to shop.sbimg`
- **THEN** the command exits with code 2, states the naming rule, and no `docker` command ran

#### Scenario: No destination
- **WHEN** the user runs `sqlbranch image export shop`
- **THEN** the command exits with code 2, names `--to`, and no `docker` command ran

#### Scenario: Destination is a directory
- **WHEN** folder `out` exists and the user runs `sqlbranch image export shop --to out`
- **THEN** the command exits with code 2, names `out`, and no `docker` command ran

#### Scenario: Destination folder missing
- **WHEN** folder `missing` does not exist and the user runs `sqlbranch image export shop --to missing/shop.sbimg`
- **THEN** the command exits with code 2, names `missing`, and no `docker` command ran

#### Scenario: Destination inside the pool
- **WHEN** the pool is the directory `/srv/sqlbranch-pool` and the user runs `sqlbranch image export shop --to /srv/sqlbranch-pool/import/shop.sbimg`
- **THEN** the command exits with code 2, says the destination must be outside the pool, and no `docker` command ran

#### Scenario: Region without an S3 destination
- **WHEN** the user runs `sqlbranch image export shop --to shop.sbimg --s3-region eu-west-1`
- **THEN** the command exits with code 2, names `--s3-region`, and no `docker` command ran

#### Scenario: Malformed region on export
- **WHEN** the user runs `sqlbranch image export shop --to s3://s3.amazonaws.com/images/shop.sbimg --s3-region "EU West"`
- **THEN** the command exits with code 2, names `--s3-region`, and no `docker` command ran

### Requirement: Archive URLs are checked first
A `--to` value of `image export` or a source of `image import` that starts with `http://`, `https://` or `s3://` (any case) SHALL be a URL. The command MUST exit with code 2 and `error.code` "usage_error" before any `docker` command or storage request when the URL uses `http://`, has a query string or fragment, has no host, has no container or bucket and file name, or ends in `/`, and MUST NOT echo a query string.

#### Scenario: Plain HTTP destination
- **WHEN** the user runs `sqlbranch image export shop --to http://acme.blob.core.windows.net/images/shop.sbimg`
- **THEN** the command exits with code 2, says only `https://` and `s3://` URLs are supported, and no `docker` command ran

#### Scenario: Pasted SAS URL as import source
- **WHEN** the user runs `sqlbranch image import "https://acme.blob.core.windows.net/images/shop.sbimg?sv=2024-11-04&sig=AbC123secret"`
- **THEN** the command exits with code 2 and names `SQLBRANCH_AZURE_SAS_TOKEN`
- **AND** neither standard output nor standard error contains `AbC123secret`

#### Scenario: URL with a fragment
- **WHEN** the user runs `sqlbranch image import s3://s3.amazonaws.com/images/shop.sbimg#v2`
- **THEN** the command exits with code 2 and no `docker` command ran

#### Scenario: URL without a host
- **WHEN** the user runs `sqlbranch image import s3:///images/shop.sbimg`
- **THEN** the command exits with code 2 and no `docker` command ran

#### Scenario: URL without a container
- **WHEN** the user runs `sqlbranch image export shop --to https://acme.blob.core.windows.net/shop.sbimg`
- **THEN** the command exits with code 2 and says the URL must name a container and a file

#### Scenario: URL ending in a slash
- **WHEN** the user runs `sqlbranch image export shop --to s3://s3.us-east-1.amazonaws.com/images/`
- **THEN** the command exits with code 2 and says the URL must name a file

### Requirement: Export does not overwrite without force
When the `--to` file or object already exists, `image export` MUST exit with code 1 and `error.code` "destination_exists", naming it and `--force`, before reading any image file, and leave it unchanged. With `--force` it SHALL replace it only after the new archive is complete. When a storage service that honours `If-None-Match: *` gets the object during the upload, the export MUST fail the same way without replacing it.

#### Scenario: Existing file without force
- **WHEN** the clone mode is `thin` or `copy`, `shop.sbimg` exists with SHA-256 `h1`, and the user runs `sqlbranch image export shop --to shop.sbimg`
- **THEN** the command exits with code 1, `error.code` is "destination_exists", the message names `--force`, and `shop.sbimg` still has SHA-256 `h1`

#### Scenario: Existing file with force
- **WHEN** the clone mode is `thin` or `copy`, `shop.sbimg` exists, and the user runs `sqlbranch image export shop --to shop.sbimg --force`
- **THEN** the command exits with code 0 and `sqlbranch image import shop.sbimg --as shop2` exits with code 0

#### Scenario: Object created during the upload
- **WHEN** the clone mode is `copy`, `s3://172.17.0.5:9000/images/shop.sbimg` does not exist when `sqlbranch image export shop --to s3://172.17.0.5:9000/images/shop.sbimg` starts, and another client writes that object before the export completes (tested with a fake storage service that honours `If-None-Match: *`)
- **THEN** the command exits with code 1, `error.code` is "destination_exists", and the other client's object is unchanged

### Requirement: Failed exports leave the destination as it was
When an export fails or is cancelled at any step, SqlBranch SHALL exit with a non-zero code, delete its partial local file, abort its S3 multipart upload and never commit a partial Azure blob, so that the destination is as it was before. A file or object that existed before MUST keep its content.

#### Scenario: Engine stream breaks halfway to a file
- **WHEN** the clone mode is `copy` and reading the image files from the engine fails after half of the bytes (tested with a fake runtime) during `sqlbranch image export shop --to shop.sbimg`
- **THEN** the command exits with code 1, `shop.sbimg` does not exist, and the folder holds no partial file

#### Scenario: Failure while replacing a file
- **WHEN** the clone mode is `copy`, `shop.sbimg` exists with SHA-256 `h1`, and `sqlbranch image export shop --to shop.sbimg --force` fails halfway (tested with a fake runtime)
- **THEN** the command exits with code 1 and `shop.sbimg` still has SHA-256 `h1`

#### Scenario: Cancelled S3 export
- **WHEN** the clone mode is `thin` or `copy` and the process receives Ctrl+C while `sqlbranch image export shop --to s3://172.17.0.5:9000/images/shop.sbimg` uploads parts
- **THEN** the command exits with a non-zero code, the bucket has no object `shop.sbimg` and no unfinished multipart upload for that key

#### Scenario: Image removed during export
- **WHEN** the clone mode is `copy` and the engine's `tar` exits with a non-zero code because `sqlbranch image rm shop` deleted a file while `sqlbranch image export shop --to shop.sbimg` ran (tested with a fake runtime)
- **THEN** the export exits with code 1 and `shop.sbimg` does not exist

### Requirement: Import an image
`sqlbranch image import <file>` SHALL add the archive's image to the pool under the manifest's name, or under `--as <name>`, as a full copy, and SHALL report the clone mode. The imported manifest SHALL equal the exported one except for the name. Branches from it SHALL be created like branches from a built image.

#### Scenario: Import under a new name
- **WHEN** the clone mode is `thin`, `shop.sbimg` was exported from image `shop`, and the user runs `sqlbranch image import shop.sbimg --as shop2 --format json`
- **THEN** the command exits with code 0, `command` is "image import", `cloneMode` is "thin", and `image.name` is "shop2"
- **AND** `sqlbranch image list --format json` shows `shop2` with the same `sourceDatabase`, files, sizes, `engineVersion` and `builtAt` as `shop`, and the SHA-256 of each file of `shop2` equals that of the same file of `shop`
- **AND** `sqlbranch branch create pr-9 --from shop2` exits with code 0 and reports clone mode `thin`

#### Scenario: Import in copy mode
- **WHEN** the clone mode is `copy` and the user runs `sqlbranch image import shop.sbimg` on a pool with no image `shop`
- **THEN** the command exits with code 0, the output states clone mode `copy`, and `sqlbranch image list` shows `shop`

#### Scenario: Import from S3-compatible storage
- **WHEN** the clone mode is `thin` or `copy`, `s3://172.17.0.5:9000/images/nightly/shop.sbimg` was written by `image export`, the server's certificate is trusted by the client, and the S3 secrets are set
- **THEN** importing it with `--as shop3` exits with code 0 and `sqlbranch image list` shows `shop3`, with no copy of the archive in the client's working directory or temporary folder

### Requirement: Import input is checked first
`image import` MUST exit with code 2 before running any `docker` command when the local source does not exist, cannot be read or is a directory, `--as` breaks the image name rules (`error.code` "invalid_name"), or `--s3-region` is misused as for `image build` (`error.code` "usage_error").

#### Scenario: Missing archive file
- **WHEN** `nope.sbimg` does not exist and the user runs `sqlbranch image import nope.sbimg`
- **THEN** the command exits with code 2, names `nope.sbimg`, and no `docker` command ran

#### Scenario: Unreadable archive file
- **WHEN** `locked.sbimg` exists but the user may not read it, and the user runs `sqlbranch image import locked.sbimg`
- **THEN** the command exits with code 2, names `locked.sbimg`, and no `docker` command ran

#### Scenario: Archive path is a directory
- **WHEN** folder `out` exists and the user runs `sqlbranch image import out`
- **THEN** the command exits with code 2, names `out`, and no `docker` command ran

#### Scenario: Invalid name in --as
- **WHEN** the user runs `sqlbranch image import shop.sbimg --as ../branches/x`
- **THEN** the command exits with code 2, states the naming rule, and no `docker` command ran

#### Scenario: Region without an S3 source
- **WHEN** the user runs `sqlbranch image import shop.sbimg --s3-region eu-west-1`
- **THEN** the command exits with code 2, names `--s3-region`, and no `docker` command ran

### Requirement: Import refuses archives it cannot read
After the exit-2 checks and before any `docker` command, so also before the `not_initialized` check, `image import` SHALL read the archive's first entry and MUST exit with code 1 and `error.code` "archive_invalid" when the source is not a SqlBranch image archive, uses an unknown compression or a newer archive format, or its manifest fails the checks of "Archive manifests are checked".

#### Scenario: Not an archive
- **WHEN** `notes.txt` is a text file and the user runs `sqlbranch image import notes.txt`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

#### Scenario: Valid archive on a machine without an engine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch image import shop.sbimg` with a valid archive
- **THEN** the command exits with code 3 and `error.code` is "not_initialized"

#### Scenario: Invalid archive on a machine without an engine
- **WHEN** no pool and no engine exist and the user runs `sqlbranch image import notes.txt`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

#### Scenario: Newer archive format
- **WHEN** `future.sbimg` declares archive format 2 and the user runs `sqlbranch image import future.sbimg`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", the message says a newer SqlBranch wrote it, and no `docker` command ran

### Requirement: Archive manifests are checked
The archive's manifest is untrusted. `image import` MUST treat it as invalid when its image name breaks the name rules and no valid `--as` is given, `files` is empty, a `fileName` contains `/`, `\`, `..` or NUL, starts with `.`, or repeats ignoring case, a `type` is not `data` or `log`, a `sizeBytes` is negative, or the sizes' total overflows a 64-bit integer.

#### Scenario: Traversal in a manifest file name
- **WHEN** the manifest in `evil.sbimg` lists a file named `../branches/pr-1/x.mdf` and the user runs `sqlbranch image import evil.sbimg`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

#### Scenario: Negative size in a manifest
- **WHEN** the manifest in `neg.sbimg` lists a file with `sizeBytes` -1 and the user runs `sqlbranch image import neg.sbimg`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

#### Scenario: Unknown file type in a manifest
- **WHEN** the manifest in `odd.sbimg` lists a file with `type` "filestream" and the user runs `sqlbranch image import odd.sbimg`
- **THEN** the command exits with code 1, `error.code` is "archive_invalid", and no `docker` command ran

### Requirement: Import accepts only the listed files
`image import` SHALL write into the pool only regular files that the archive's manifest lists, under their plain file names. An archive entry that is not listed, appears twice, is a link or device, has a name holding a path separator or `..`, or whose header size differs from the manifest's size, MUST make the import exit with code 1 and `error.code` "archive_invalid" before that entry's bytes are written, and remove what it wrote.

#### Scenario: Path traversal entry
- **WHEN** the clone mode is `copy` and `evil.sbimg` holds an entry named `../branches/pr-1/x.mdf` after a valid manifest
- **THEN** `sqlbranch image import evil.sbimg` exits with code 1, `error.code` is "archive_invalid", no file outside the import's own build folder was written, and `sqlbranch status` exits with code 0

#### Scenario: Entry size differs from the manifest
- **WHEN** the clone mode is `copy` and the tar header of `shop.mdf` in `big.sbimg` says 10 GiB while the manifest says 1 GiB
- **THEN** `sqlbranch image import big.sbimg --as shop2` exits with code 1, `error.code` is "archive_invalid", and no byte of `shop.mdf` was written to the pool

#### Scenario: Symbolic link entry
- **WHEN** the clone mode is `copy` and `link.sbimg` holds a symbolic link entry named `shop.mdf`
- **THEN** `sqlbranch image import link.sbimg` exits with code 1, `error.code` is "archive_invalid", and `sqlbranch image list` does not show its image

### Requirement: Import refuses images from a newer SQL Server
When the archive's engine major version is newer than the local engine's, `image import` MUST exit with code 1 and `error.code` "version_mismatch" before writing any file, naming both versions and the manifest's engine image. When only the build number is newer within the same major version, it SHALL warn on standard error naming both versions and continue.

#### Scenario: SQL Server 2025 image on a 2022 engine
- **WHEN** the clone mode is `copy`, the local engine runs 16.0.4135.4, and `shop.sbimg` was exported from an image with engine version 17.0.1000.7 and engine image `mcr.microsoft.com/mssql/server:2025-latest`
- **THEN** `sqlbranch image import shop.sbimg` exits with code 1, `error.code` is "version_mismatch", the message names 17.0.1000.7, 16.0.4135.4 and `mcr.microsoft.com/mssql/server:2025-latest`, and the pool is unchanged

#### Scenario: Newer build of the same major version
- **WHEN** the clone mode is `copy`, the local engine runs 16.0.4100.1, and `shop.sbimg` was exported with engine version 16.0.4135.4
- **THEN** `sqlbranch image import shop.sbimg` exits with code 0 and standard error names both versions

### Requirement: Import checks every file's SHA-256
`image import` SHALL compute the SHA-256 and size of every file it writes and compare them with the archive's checksum list and manifest. On any difference, a missing checksum list or a truncated archive, it MUST exit with code 1 and `error.code` "archive_corrupt", naming the file, and leave no image.

#### Scenario: Corrupted byte
- **WHEN** the clone mode is `thin` or `copy` and one byte of the data file inside `shop.sbimg` was changed after export
- **THEN** `sqlbranch image import shop.sbimg --as shop2` exits with code 1, `error.code` is "archive_corrupt", and the message names the data file
- **AND** `sqlbranch image list` does not show `shop2` and `sqlbranch status` exits with code 0

#### Scenario: Truncated archive
- **WHEN** the clone mode is `copy` and `shop.sbimg` was cut to half its size
- **THEN** `sqlbranch image import shop.sbimg --as shop2` exits with code 1, `error.code` is "archive_corrupt", and `sqlbranch image list` does not show `shop2`

### Requirement: Import checks space first
Before writing any file, `image import` SHALL check that free pool space is at least the total size of the files the manifest lists, and MUST exit with code 1 and `error.code` "pool_full" naming both sizes when it is not.

#### Scenario: Not enough space to import
- **WHEN** the clone mode is `thin` or `copy`, the archive's image files total 50 GiB, and free pool space is 40 GiB
- **THEN** `sqlbranch image import shop.sbimg` exits with code 1, `error.code` is "pool_full", the message names 50 GiB needed and 40 GiB available, and the pool is unchanged

### Requirement: Imported image names are unique
`image import` MUST NOT replace an existing image. When the target name is taken, it SHALL exit with code 1 and `error.code` "image_exists", naming `--as`, before writing any file. An import and a build or another import of the same name at the same time SHALL leave exactly one of them succeeding.

#### Scenario: Name already used on import
- **WHEN** the clone mode is `thin` or `copy`, image `shop` exists, and the user runs `sqlbranch image import shop.sbimg`
- **THEN** the command exits with code 1, `error.code` is "image_exists", the message names `--as`, and image `shop` is unchanged

#### Scenario: Import and build of one name at once
- **WHEN** the clone mode is `copy` and `sqlbranch image import shop.sbimg --as crm` and `sqlbranch image build crm --from-bak crm.bak` run at the same moment
- **THEN** one exits with code 0 and the other with code 1, and `sqlbranch status` exits with code 0

### Requirement: Failed imports leave nothing behind
When an import fails or is cancelled after it started writing, SqlBranch SHALL wait for its file stream into the engine to end, then remove its build folder, and the image folder only if this import created it. It MUST NOT remove an image folder it did not create, and the image MUST NOT be listed. When the client is killed, `status` SHALL list the import's build folder as a build leftover.

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

### Requirement: Unreachable archive storage
When storage cannot be reached, refuses the secret, presents a certificate the client does not trust, or has no object at the import URL, `image export` and `image import` MUST exit with code 1 and `error.code` "storage_unreachable", naming the URL and the reason, and follow the cleanup rules of export and import.

#### Scenario: Wrong S3 secret on import
- **WHEN** the clone mode is `thin` or `copy` and the user runs `sqlbranch image import s3://172.17.0.5:9000/images/shop.sbimg` with a wrong `SQLBRANCH_S3_SECRET_ACCESS_KEY`
- **THEN** the command exits with code 1, `error.code` is "storage_unreachable", no `docker` command ran, and the pool is unchanged

#### Scenario: Missing blob on import
- **WHEN** the user runs `sqlbranch image import https://acme.blob.core.windows.net/images/nope.sbimg` with a valid SAS token (tested with a fake storage service that returns 404)
- **THEN** the command exits with code 1, `error.code` is "storage_unreachable", and the message names `https://acme.blob.core.windows.net/images/nope.sbimg`
