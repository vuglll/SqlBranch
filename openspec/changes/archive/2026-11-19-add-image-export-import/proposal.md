# Proposal

## Why

Building an image from a large backup takes a long time, and today every
machine has to do it again. One person or one build machine should build the
image once, and everyone else should fetch the result. This is the last piece
of the nightly recipe in `docs/roadmap.md` (F1 builds from a cloud backup, F3
shares the image, F4 refreshes branches).

Release: **0.3**, feature queue row **F3** (`add-image-export-import`).

Prerequisites ("Needs first": 0.1 archived; F1 for cloud URLs):

- 0.1 was archived on 2026-11-17 (`openspec/changes/archive/2026-11-17-add-branching-core`).
- F1 was archived on 2026-11-19 (`openspec/changes/archive/2026-11-19-add-cloud-backup-source`).
  Its URL forms, storage secret variables and redaction rules are in the main
  `images` and `configuration` specs, and this change reuses them. F1's
  real-cloud checks are on the 0.3 release gate; they test the engine's own
  storage access, which this change does not use, so they do not block it.
- No spike gate applies. The facts this change rests on were re-checked on
  2026-11-19 in `docs/research/2026-11-19-image-export-import-facts.md`.

## What Changes

- New `sqlbranch image export <name> --to <file-or-url> [--force] [--s3-region <region>]`
  writes one compressed archive: the image manifest first, then the sealed
  database files, then a SHA-256 for each file. It holds nothing else: no
  branches, credentials, logins or engine state.
- New `sqlbranch image import <file-or-url> [--as <name>] [--s3-region <region>]`
  reads the archive, refuses an image built by a newer SQL Server major version
  than the local engine (naming the engine image to use), checks free pool
  space, writes the files into the pool, checks every SHA-256, and seals the
  image under its own or the `--as` name. The imported image is a full copy;
  branches from it are thin or copy as the pool allows.
- The target or source is a local path, an Azure Blob Storage `https://` URL or
  an `s3://` URL, in the URL forms F1 accepts, with the secrets F1 reads
  (`SQLBRANCH_AZURE_SAS_TOKEN`, `SQLBRANCH_S3_ACCESS_KEY_ID`,
  `SQLBRANCH_S3_SECRET_ACCESS_KEY`). Unlike F1, the CLI talks to storage
  itself, because SQL Server cannot write arbitrary files to a URL. The
  archive streams between storage and the engine with no temporary copy on
  the client.
- Export never replaces an existing file or object unless `--force` is given,
  and even then the old one stays intact until the new archive is complete.
  Import never replaces an existing image.
- New `error.code` values, all exit 1: `destination_exists`, `archive_invalid`,
  `archive_corrupt`, `storage_unreachable`. Bad input exits 2 before any
  `docker` command.
- Archives are gzip-compressed by default; spike 1.1 may switch to zstd only
  if it is clearly better on large images. The compression is recorded in the
  archive's first bytes and import reads every compression export can write.
- New client dependencies: Azure.Storage.Blobs and AWSSDK.S3, with explicit
  credentials only and logging off; ZstdSharp.Port only if zstd is chosen
  (design D2, D5).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `images`: adds image export and import, their input checks, the integrity
  and version checks, overwrite and cleanup rules, and the new error codes.
- `configuration`: the storage secret sources, the early secret checks and the
  rule on where storage secrets may appear now cover export and import, not
  only URL builds.

## Decisions already made

- Decided by the human on 2026-11-19: Azure and S3 stay in this change; gzip
  by default; refuse only a newer major SQL Server version; the official SDKs;
  no 259 or 128 character URL limits for export and import.

## Out of scope

- Archive formats other than SqlBranch's own, and importing images from other
  tools.
- Exporting several images in one archive, partial or incremental archives,
  and deduplication between archives.
- Resuming an interrupted export or import, parallel transfers, and
  bandwidth limits.
- Encrypting or signing archives. The SHA-256 check guards against damage in
  transit, not against a malicious archive author; archives are trusted input
  the same way backups are.
- Replacing an existing image on import (`image rm` first), and refreshing
  branches after an import (F4).
- Recording where an imported image came from in its manifest.
- Other URL schemes (`file://`, `http://`, presigned URLs, Azure Files, GCS),
  storage account keys, Entra ID or AWS profile credentials.
- Making the client trust a private CA for S3-compatible storage.
- Scheduling exports; the nightly job is the team's own cron or CI.

## Impact

- Code: `SqlBranch.Core/Images` (exporter, importer, archive reader and
  writer), a storage transport for file, Azure Blob and S3, the URL parser
  and `StorageSecrets` from F1 (reused), `SecretRedactor` (storage SDK
  errors), `SqlBranch.Cli/Commands/ImageCommands.cs` (two commands and their
  metadata, so the generated agent skill lists them).
- Pool: import uses a build folder and the build lock, so a killed import is
  reported by `status` like a killed build.
- JSON output: `schemaVersion` stays 1; two new command documents and four new
  error codes (design, "JSON output").
- Docs: `docs/images.md`, `docs/configuration.md`, `docs/cli-output.md`, and
  the nightly recipe in `README.md`.
