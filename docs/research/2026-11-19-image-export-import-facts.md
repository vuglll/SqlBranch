# Facts for add-image-export-import (checked 2026-11-19)

Facts the `add-image-export-import` change (F3, release 0.3) rests on. Each was
read from primary documentation or run locally on 2026-11-19. The open ones at
the end are settled by the spike and tests named there.

## Moving files in and out of the engine container

| Fact | Source |
|---|---|
| `docker cp` with `-` as the source reads a tar archive from standard input, and with `-` as the destination writes a tar archive to standard output | https://docs.docker.com/reference/cli/docker/container/cp/ |
| `docker cp` cannot copy "mounts created by the user in the container"; the page suggests running `tar` through `docker exec` instead | same page |
| Files copied into a container by `docker cp` are owned by root unless `-a` is given | same page |
| `mcr.microsoft.com/mssql/server:2022-latest` (Ubuntu 22.04) and `:2025-latest` (Ubuntu 24.04) ship GNU tar (1.34 and 1.35) and `sha256sum`, and ship neither `gzip` nor `zstd`. The container's default user is `mssql` (uid 10001) | Run locally: `docker run --rm --platform linux/amd64 --entrypoint sh mcr.microsoft.com/mssql/server:<tag> -c 'id; tar --version; command -v sha256sum zstd gzip'` |

## Compression on .NET 10

| Fact | Source |
|---|---|
| `System.IO.Compression.ZstandardStream` is documented only for .NET 11 (moniker `net-11.0`); .NET 10, which SqlBranch targets, has no built-in zstd. Built in on .NET 10: GZip, Deflate, ZLib, Brotli | https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.zstandardstream |
| `ZstdSharp.Port` is a pure managed zstd port on NuGet, latest stable 0.8.8, MIT licensed | https://api.nuget.org/v3-flatcontainer/zstdsharp.port/index.json and its nuspec |

## Azure Blob Storage block upload

| Fact | Source |
|---|---|
| A block blob is written with Put Block calls and committed with one Put Block List; at most 50,000 committed blocks, at most 4,000 MiB per block (service version 2019-12-12 and later) | https://learn.microsoft.com/en-us/rest/api/storageservices/put-block-list |
| Put Block List supports conditional headers, so a commit can be made only when no blob exists | same page |
| Uncommitted blocks are garbage collected when no successful Put Block or Put Block List happens on the blob within a week of the last Put Block | same page |
| Put Block List accepts a service, account or user delegation SAS; creating a new blob needs `c` or `w`, overwriting needs `w` (`c` only from x-ms-version 2026-04-06) | same page |

## Amazon S3 multipart upload

| Fact | Source |
|---|---|
| Multipart upload: at most 10,000 parts, part size 5 MiB to 5 GiB (no minimum for the last part), maximum object size 48.8 TiB | https://docs.aws.amazon.com/AmazonS3/latest/userguide/qfacts.html |
| `If-None-Match: *` on `CompleteMultipartUpload` makes the upload fail with `412 Precondition Failed` when an object with that key exists; it needs SigV4 signing | https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html |
| An in-progress multipart upload is not an object; AWS recommends a lifecycle rule with `AbortIncompleteMultipartUpload` to clean up uploads that were never completed or aborted | same page |

## SQL Server versions and CPU architecture

| Fact | Source |
|---|---|
| A database created by a more recent version of SQL Server cannot be attached to an earlier version | https://learn.microsoft.com/en-us/sql/relational-databases/databases/database-detach-and-attach-sql-server |
| SQL Server container images are supported only on x86-64 hosts; emulation such as Rosetta 2 or QEMU is not tested or supported | https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker |
| The local 2022 and 2025 engine images are `amd64` only; on this arm64 Mac they run under emulation | Run locally: `docker image inspect mcr.microsoft.com/mssql/server:<tag> --format '{{.Architecture}}'` and `uname -m` |

## Not verified (settled later)

| Question | Settled by |
|---|---|
| Export and import times for 10 GB and 50 GB images with gzip (fastest) versus zstd levels 1 and 3, and the archive sizes | Spike task 1.1, on the x86-64 run host: **50 GB measured 2026-11-20, see below; 10 GB not run** |
| Whether an image built by an engine under emulation on Apple Silicon imports and gives a working branch on x86-64, and the reverse. Expected yes, because both run the same x86-64 SQL Server binary and the archive holds plain files | Task 6.4: **yes in both directions, 2026-11-20, see below** |
| Whether `System.Formats.Tar` writes and reads entries over 8 GiB in PAX format | Unit test in task 2.1 |
| Whether the .NET client on Linux trusts an extra CA from `SSL_CERT_FILE`, which a CLI-level MinIO test would need | Not needed: the MinIO tests run the Core service with an injected HTTP handler (design D9) |
| Whether MinIO honours `If-None-Match: *` on `CompleteMultipartUpload` | Answered by spike 1.2 below (yes); task 5.2 still repeats it as a test |
| The exact Azure.Storage.Blobs and AWSSDK.S3 calls for conditional commit, explicit credentials and path-style S3 | Answered by spike 1.2 below |

## Spike 1.2: storage SDKs against MinIO and Azurite (run 2026-11-19)

Run on the arm64 Mac, .NET SDK 10.0.400, in a scratch project outside the
repository (deleted afterwards). One self-signed certificate for 127.0.0.1,
trusted by thumbprint through an injected HTTP handler (design D9), no real
cloud account. Servers are F1's pinned images: MinIO
`cgr.dev/chainguard/minio@sha256:16b824a0...` and Azurite
`mcr.microsoft.com/azure-storage/azurite@sha256:c26024d6...`, each started with
the label `sqlbranch-spike-f3=<run id>` and removed at the end by that label
(no spike container was left). The 200 MiB stream was random bytes, produced
without a file, sent in 4 parts (3 x 64 MiB and 1 x 8 MiB), and the SHA-256 of
the download was equal to the SHA-256 of what was sent on both servers.

Package versions used: `AWSSDK.S3` 4.1.2 (with `AWSSDK.Core` 4.x) and
`Azure.Storage.Blobs` 12.30.1 (with `Azure.Core` and `Azure.Storage.Common`).

### Calls that work: S3 (AWSSDK.S3, MinIO)

| Item | Result |
|---|---|
| Client | `new AmazonS3Client(new BasicAWSCredentials(id, secret), new AmazonS3Config { ServiceURL = "https://host:port", ForcePathStyle = true, AuthenticationRegion = "us-east-1", HttpClientFactory = <custom>, RequestChecksumCalculation = WHEN_REQUIRED, ResponseChecksumValidation = WHEN_REQUIRED, MaxErrorRetry = 0, LogResponse = false, LogMetrics = false })`. Credentials are passed explicitly, so no profile, environment or metadata source is read |
| Custom TLS trust | Subclass `Amazon.Runtime.HttpClientFactory` and override `CreateHttpClient(IClientConfig)` to return an `HttpClient` with the test handler. In 4.x there is no `DisposeHttpClients` to override |
| Upload | `InitiateMultipartUploadAsync`, `UploadPartAsync` (`PartNumber`, `PartSize`, `InputStream` over the buffered part, `IsLastPart`), `CompleteMultipartUploadAsync` with `PartETags` |
| Conditional commit | `CompleteMultipartUploadRequest.IfNoneMatch = "*"` exists in 4.1.2 |
| Abort | `AbortMultipartUploadAsync`; afterwards `ListMultipartUploadsAsync` shows 0 uploads and the object does not exist |
| Existence check | `GetObjectMetadataAsync`; `AmazonS3Exception` with `StatusCode == NotFound` means absent |
| Download | `GetObjectAsync(...).ResponseStream`, read as one stream |
| Time | 200 MiB in about 2 s on loopback |

MinIO and `If-None-Match: *` on `CompleteMultipartUpload`: **honoured.**
Completing onto an existing key returned HTTP 412 `PreconditionFailed` and left
the old object. An object created between the last part and the complete call
(a race) also returned 412 and the other writer's object stayed (12 bytes).
Completing without the header over an existing key replaced it, which is what
`--force` needs. MinIO needed no extra setting. `RequestChecksumCalculation`
and `ResponseChecksumValidation` were set to `WHEN_REQUIRED` so the SDK does not
add checksum trailers that other S3-compatible servers may not accept; this
spike did not test the default `WHEN_SUPPORTED` against MinIO, so keep the
setting.

### Calls that work: Azure (Azure.Storage.Blobs, Azurite)

| Item | Result |
|---|---|
| Client | `new BlockBlobClient(new Uri("https://host:port/<account>/<container>/<blob>"), new AzureSasCredential(sas), options)`. The SAS is passed as the credential, not in the URI. `BlobClientOptions { Transport = new HttpClientTransport(httpClient), Retry.MaxRetries = 0 }`, `Diagnostics.IsLoggingEnabled = false`, `IsTelemetryEnabled = false` |
| Upload | `StageBlockAsync(blockId, stream)` for each part, then one `CommitBlockListAsync(ids, new CommitBlockListOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } })` |
| Block IDs | Base64 of a random 16-byte prefix plus the zero-padded 6-digit index: accepted, all IDs one length |
| Existence check | `ExistsAsync()` |
| Download | `OpenReadAsync()` stream |
| Time | 200 MiB in about 5.5 s on loopback |

Azurite and the conditional commit: committing with `If-None-Match: *` onto an
existing blob was refused with HTTP 409 `BlobAlreadyExists`, and the same for a
blob created after staging and before the commit (race); the other writer's blob
stayed. The code is 409, not 412, so the exporter must treat both 409 and 412 as
"destination exists". Real Azure was not checked (no account); the Azure
documentation says Put Block List accepts the conditional headers. Commit
without a condition replaced the blob.

Uncommitted blocks: after a staged upload was abandoned, `GetBlockList(All)` on
the never-committed blob showed 0 committed and 4 uncommitted blocks. There is
no SDK call to drop them, as the design says; they are collected by the service.

SAS permissions on Azurite (container SAS, service version of the SDK): `r+w`
and `r+w+c+l` can upload a new blob and `r+w` can overwrite one; `r+c` only (no
`w`) was refused with HTTP 403 `AuthorizationPermissionMismatch` for staging a
block, and read-only was refused the same way. So an export SAS needs `w`, which
also covers Put Block List; `c` alone is not enough for `StageBlock`.

### Tool package size

`dotnet pack -c Release` of `src/SqlBranch.Cli` (version 0.1.0, no other change)
on 2026-11-19, once as the repository is and once with `AWSSDK.S3` 4.1.2 and
`Azure.Storage.Blobs` 12.30.1 added to `SqlBranch.Core`:

| | Package (.nupkg) | Unpacked (sum of files) | Files |
|---|---|---|---|
| Before | 4,260,378 bytes (4.06 MiB) | 12,159,096 bytes | 72 |
| After | 6,865,979 bytes (6.55 MiB) | 19,323,819 bytes | 89 |
| Growth | +2,605,601 bytes (+2.49 MiB, +61%) | +7,164,723 bytes | +17 |

The largest new files are `Azure.Storage.Blobs.dll` (1.46 MB), `AWSSDK.S3.dll`
(1.20 MB) and `AWSSDK.Core.dll` (1.11 MB). `Azure.Core.dll` was already in the
package through `Microsoft.Data.SqlClient`.

### What this changes

Nothing in the specs. Notes for the tasks: set `RequestChecksumCalculation` and
`ResponseChecksumValidation` to `WHEN_REQUIRED`; map both 409 and 412 on commit
to `destination_exists`; export SAS URLs need write permission.

## Task 5.2: MinIO and `If-None-Match: *` again, through the product code

Date: 2026-11-19. `ImageCloudArchiveIntegrationTests` exports through `ExportToStoreAsync` to F1's pinned MinIO with a handler that
writes the key from another client after the first part is uploaded. The conditional complete is refused (`destination_exists`), the
other client's object keeps its bytes and no multipart upload is left. MinIO honours `If-None-Match: *`, as spike 1.2 found, so the
note in `docs/images.md` about servers that ignore the header is about other S3-compatible servers only. The same test class ran
against Azurite: an existing blob is `destination_exists` (Azurite answers 409), is unchanged, and `--force` replaces it. Both ran
on the default managed pool (clone mode `thin`) and on `SQLBRANCH_IT_POOL_BACKEND=plain` (`copy`).

## x86-64 run (2026-11-20): codec timing and cross-architecture

Host and checkout as in [2026-11-20-x86-64-release-gate-run.md](2026-11-20-x86-64-release-gate-run.md)
(Hetzner CCX33, AMD EPYC-Milan, 8 vCPU, `dev` at `4b9f7ec`).

### Former task 1.1: codec timing on the 50 GB image

A throwaway console app (not in the repository; `ZstdSharp.Port` 0.8.8) ran
the export data path: `docker exec <engine> tar -cf - -C /pool/images/so2013/data .`
into the codec into a file on the host's disk, then the file through the
decoder into `docker exec -i <engine> tar -xf -` in a staging folder in the
pool. The OS page cache was dropped before each codec. Image: so2013, 5 files,
50.78 GiB (54,526,468,096 B). One run each.

| Codec | Export | Import | Archive | Export vs gzip | Round trip vs gzip |
|---|---|---|---|---|---|
| gzip `Fastest` | 397.2 s | 200.6 s | 13,289,751,646 B (24.4 %) | 1.00 | 1.00 |
| zstd level 1 | 192.6 s | 161.4 s | 11,390,235,005 B (20.9 %) | **2.06** | **1.69** |
| zstd level 3 | 267.8 s | 170.6 s | 10,515,428,363 B (19.3 %) | 1.48 | 1.36 |

The product itself (`sqlbranch image export so2013 --to so2013.sbimg`, gzip
`Fastest`): 436.7 s, archive 12.38 GiB; `image import --as so2013b`: 215.0 s,
then a branch from it was created (exit 0). Its times match the gzip row plus
the manifest and hashing work.

**Against design D2** ("gzip stays unless zstd is at least 1.5 times faster at
a similar or smaller size on the 50 GB image"): zstd level 1 is 2.06 times
faster on export and 1.69 times on a full round trip, and 14 percent smaller.
It meets the rule. Gzip compression limited export to about 137 MB/s of input.
Both codecs imported in about 160 to 200 s. The 10 GB image was not timed
because so2010 was unavailable on the host. The rule uses the 50 GB image.

**Maintainer decision (2026-11-19).** Write new archives with zstd level 1
and keep gzip support for imports.

### Former task 6.4: Apple Silicon to x86-64 and back

Mac: Apple M4, Docker Desktop (linux/arm64 VM, 7.75 GiB), engine under
emulation, default `init` (60 GB managed pool, `thin`), removed afterwards with
`teardown --force`. Linux: the CCX33 above, native. Both run SQL Server 2022
16.0.4325.4. Image source on each side: one script creating `dbo.Item` with
200,000 rows of about 600 bytes (136.49 MiB), with `Name` set to the side's
name.

| Step | Result |
|---|---|
| Mac: `image build xmac --from-scripts`, `image export xmac --to xmac.sbimg` | exit 0, 136.49 MiB, archive 3.05 MiB, 1 s |
| x86-64: `image import xmac.sbimg --as xmac`, `branch create`, count | exit 0, `thin`, `200000 rows, names=mac` |
| x86-64: `image build xlin --from-scripts`, `image export xlin` | exit 0, archive 2.09 MiB |
| Mac: `image import xlin.sbimg --as xlin`, `branch create`, count | exit 0, `thin`, `200000 rows, names=linux` |

Images move between an emulated engine on Apple Silicon and a native x86-64
engine in both directions and give working branches.

