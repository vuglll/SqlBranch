# Facts for add-cloud-backup-source (checked 2026-11-18)

Facts the `add-cloud-backup-source` change (F1, release 0.3) rests on. All were
read from primary documentation on 2026-11-18. None was run against a real
engine yet; the spikes in group 1 of the change's tasks settle the open ones.

## Restore from Azure Blob Storage

| Fact | Source |
|---|---|
| On SQL Server on Linux, including containers, the only supported backup to URL for Azure Blob Storage is to block blobs with a shared access signature (SAS). Storage-account-key (page blob) credentials are not supported there | https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/sql-server-backup-to-url |
| The SAS credential is created as `CREATE CREDENTIAL [https://<account>.blob.core.windows.net/<container>] WITH IDENTITY = 'SHARED ACCESS SIGNATURE', SECRET = '<SAS token>'`, and the token is stored without its leading `?` | same page |
| Backup to URL over `http` is not supported; the URL must use `https` | same page |
| `RESTORE`, `RESTORE FILELISTONLY`, `RESTORE HEADERONLY`, `RESTORE LABELONLY` and `RESTORE VERIFYONLY` are supported from URL. The restore option `FILE` is listed as not supported | same page |
| Appending to an existing backup blob is not supported, so a backup written by SQL Server to a URL holds one backup set | same page |
| A single block blob backup is limited to about 195.3 GB (50,000 blocks of 4 MB). Larger backups are striped over up to 64 URLs | same page |
| The backup device name is limited to 259 characters | same page |
| SQL Server 2019 and earlier limit SAS tokens to 256 characters | same page |

## Restore from S3-compatible storage

| Fact | Source |
|---|---|
| The S3 connector exists in SQL Server 2022 (16.x) and later, on Windows and Linux. URLs start with `s3://` and always use HTTPS; plain `http` endpoints are not supported | https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/sql-server-backup-to-url-s3-compatible-object-storage |
| Credential: `CREATE CREDENTIAL [s3://<endpoint>:<port>/<bucket>] WITH IDENTITY = 'S3 Access Key', SECRET = '<AccessKeyID>:<SecretKeyID>'`. Neither key may contain `:`. The documented examples also name a credential after a folder (`s3://<bucket>.s3.us-west-2.amazonaws.com/backup`) | same page |
| Both path style (`s3://<endpoint>:<port>/<bucket>/<file>`) and virtual host style (`s3://<bucket>.<domain>/<file>`) URLs are accepted | same page |
| Region defaults to `us-east-1` and is set on restore with `RESTORE_OPTIONS = '{"s3": {"region":"us-west-2"}}'` | same page |
| `RESTORE FILELISTONLY`, `HEADERONLY`, `LABELONLY` and `VERIFYONLY` are supported from S3. The restore option `FILE` is listed as not supported | same page |
| The whole URL is limited to 259 characters; the credential name to 128 characters | same page |
| Restore needs `ListBucket` and `GetObject` on the bucket | same page |
| On Linux, SQL Server trusts CA certificates placed in `/var/opt/mssql/security/ca-certificates` (first 50 only). They must be in place before the SQL Server process starts | same page |
| MinIO is listed among S3-compatible providers. Google Cloud Storage is not listed | https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/sql-server-backup-and-restore-with-s3-compatible-object-storage |

## Azure SQL Managed Instance

| Fact | Source |
|---|---|
| A copy-only backup restores to SQL Server 2022 only from an instance on the SQL Server 2022 update policy, and to SQL Server 2025 only from one on the SQL Server 2025 update policy. Backups from an Always-up-to-date instance restore only to Always-up-to-date instances. A policy change is permanent | https://learn.microsoft.com/en-us/azure/azure-sql/managed-instance/update-policy |
| Databases encrypted with service-managed TDE keys cannot be restored to SQL Server; customer-managed keys need the same key on the target | https://learn.microsoft.com/en-us/azure/azure-sql/managed-instance/restore-database-to-sql-server |
| The documented restore of a Managed Instance backup moves a file with logical name `XTP`, the In-Memory OLTP container | same page |
| After restoring a Managed Instance database to SQL Server and dropping an index, DBCC CHECKDB can report error 8992 | same page |

## Amazon RDS for SQL Server

| Fact | Source |
|---|---|
| `msdb.dbo.rds_backup_database` writes a native backup to an S3 bucket. `@type` is `FULL` (default) or `DIFFERENTIAL`; `@number_of_files` splits it into up to 10 files named `...1-of-N...` | https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/SQLServer.Procedural.Importing.Native.Using.html |
| With `@kms_master_key_arn`, the file is encrypted client-side; without it, only S3 server-side encryption (SSE-S3) applies | same page |

## Not verified

See also "Spike results, run 2026-11-19" at the end of this file; it settles the
certificate-trust and Azurite items below and leaves the rest open.

- Whether a stock `mcr.microsoft.com/mssql/server:2022-latest` container trusts
  the public CA of AWS S3 endpoints without any file in
  `/var/opt/mssql/security/ca-certificates`. Spike 1.1 settles it.
- Whether SQL Server finds a SAS credential named after a folder below the
  container (`https://<account>.blob.core.windows.net/<container>/<folder>`).
  Spike 1.1 settles it; the documented container-level name is the fallback.
- Whether every Managed Instance copy-only backup carries the `XTP`
  (memory-optimized) filegroup, which SqlBranch rejects today. Needs a backup
  from a real instance (spike 1.3).
- Whether Managed Instance refuses to take a copy-only backup of a database
  with service-managed TDE (the draft's claim). The restore guide only says such
  a backup cannot be restored to SQL Server.
- Whether a KMS-encrypted RDS backup is unreadable outside RDS. Inferred from
  "client-side encryption"; not tested.
- Whether Google Cloud Storage's S3-compatible endpoint works with the SQL
  Server S3 connector, and how Cloud SQL for SQL Server exports a `.bak`.
- Whether Azurite (the local Azure Storage emulator) over HTTPS works as a
  restore source for SQL Server. Spike 1.2 settles it.

## Spike results, run 2026-11-19 (spikes 1.1 partly, 1.2)

Run on a Mac (arm64, Docker Desktop 29.9.3) with no cloud account and no real
credential. The engine was a stock `mcr.microsoft.com/mssql/server:2022-latest`
started as `--platform linux/amd64` (16.0.4325.4, RTM-CU28) with an empty
`/var/opt/mssql/security/ca-certificates`. All containers carried the label
`sqlbranch-spike=1` and were removed at the end. Secrets below are placeholders:
`<sa-pw>`, `<minio-secret>`, `<az-key>`, `<sas>`. A statement is sent as
`sqlcmd -C -S localhost -U sa -P <sa-pw> -Q "<stmt>"` inside the engine
container.

### 1.1 (partly): is the public certificate of AWS and Azure trusted?

Dummy credentials and requests to public hosts only:

```
CREATE CREDENTIAL [s3://s3.eu-west-1.amazonaws.com/sqlbranch-nonexistent-test]
  WITH IDENTITY='S3 Access Key', SECRET='AKIADUMMYDUMMY:<dummy>';
RESTORE HEADERONLY FROM URL='s3://s3.eu-west-1.amazonaws.com/sqlbranch-nonexistent-test/x.bak';
CREATE CREDENTIAL [https://azureopendatastorage.blob.core.windows.net/nyctlc]
  WITH IDENTITY='SHARED ACCESS SIGNATURE', SECRET='sv=2022-11-02&ss=b&sig=<dummy>';
RESTORE HEADERONLY FROM URL='https://azureopendatastorage.blob.core.windows.net/nyctlc/x.bak';
```

| Case (no CA file in the engine) | Engine error | Meaning |
|---|---|---|
| S3 path style `s3://s3.eu-west-1.amazonaws.com/<bucket>/x.bak`, dummy credential, HEADERONLY, LABELONLY, FILELISTONLY | Msg 3201 "Cannot open backup device '...'. Operating system error 5(Access is denied.)" | TLS handshake succeeded, AWS answered 403: **AWS certificate trusted** |
| same, host `s3.us-east-1.amazonaws.com` | same, error 5 | trusted |
| S3 virtual-host style `s3://<bucket>.s3.eu-west-1.amazonaws.com/x.bak` | same, error 5 | trusted |
| Blob, existing public account `azureopendatastorage`, container `nyctlc`, dummy SAS | Msg 3201 ... Operating system error 5(Access is denied.) | **Azure certificate trusted** |
| same account, container that does not exist (`sqlbranchnocontainer`), dummy SAS | error 5 | trusted (Azure answers 403 either way, so a missing container is not told apart from a wrong SAS) |

Controls that show what the failures look like (all Msg 3201):

| Case | Operating system error |
|---|---|
| No credential matches the URL (S3 or Blob, real host) | 86 "The specified network password is not correct." |
| S3 credential, host that does not resolve (`no-such-host.invalid:9000`) | 12007 (text unavailable) |
| S3 credential, closed port | 12029 (text unavailable) |
| S3 credential, TLS certificate not trusted (MinIO test CA, before the CA was added, see 1.2) | **12175** (text unavailable) |
| Blob URL whose storage account does not resolve in DNS (`sqlbranchnonexist.blob.core.windows.net`) | Msg 3078 "The file name '...' is invalid as a backup device name for the specified device type." |

Result: **neither public certificate needs a file in `ca-certificates`.
Task 4.6 is not needed** (spike 1.1, 2026-11-19). The same engine trusted
`s3.eu-west-1.amazonaws.com`, `s3.us-east-1.amazonaws.com`, the
`<bucket>.s3.eu-west-1.amazonaws.com` form and a real
`*.blob.core.windows.net` account with an empty folder, and 12175 is what an
untrusted certificate looks like. Other regions or sovereign clouds were not
checked. Still open, because they need a real account: the folder-named SAS
credential on real Blob, the region option on real S3, and the engine's text
for a wrong secret and a missing object on real storage (a real bucket would
answer 403 or 404 differently from the 403 seen with a bucket that does not
exist). A real-account run is on the 0.3 release gate.

### 1.2: MinIO with a test CA, and Azurite

Image note: `minio/minio` is no longer on Docker Hub and `quay.io/minio/minio`
needs a login (both pull errors on 2026-11-19). The spike used
`cgr.dev/chainguard/minio:latest` (RELEASE.2026-11-16, arm64 and amd64, no shell,
pulled anonymously). The fixture of task 5.1 needs a pinned image reference
that still pulls anonymously; this one worked.

Setup (placeholders for secrets):

```
# per-run CA and a server certificate with SAN IP:<minio-ip>,IP:127.0.0.1,DNS:localhost
openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.crt -days 2 -subj "/CN=sqlbranch-spike-test-ca"
openssl req -newkey rsa:2048 -nodes -keyout certs/private.key -out srv.csr -subj "/CN=minio"
openssl x509 -req -in srv.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out certs/public.crt -days 2 -extfile ext.cnf
docker run -d --name spike-minio --label sqlbranch-spike=1 -p 127.0.0.1:19000:9000 \
  -e MINIO_ROOT_USER=<key-id> -e MINIO_ROOT_PASSWORD=<minio-secret> \
  -v $PWD/certs:/certs -v $PWD/data:/data cgr.dev/chainguard/minio:latest server /data --certs-dir /certs
# the IP is known only after the first start: start once, read it from
# `docker inspect`, write the certificate with it, `docker restart` (IP stayed the same)
curl --user <key-id>:<minio-secret> --aws-sigv4 aws:amz:us-east-1:s3 --cacert ca.crt -X PUT https://localhost:19000/spikebkt
curl ... -T spike.bak https://localhost:19000/spikebkt/folder/spike.bak
# engine on the default bridge, backup made with BACKUP DATABASE ... WITH COPY_ONLY (2.9 MB)
CREATE CREDENTIAL [s3://<minio-ip>:9000/spikebkt] WITH IDENTITY='S3 Access Key', SECRET='<key-id>:<minio-secret>';
docker exec -u 0 <engine> mkdir -p /var/opt/mssql/security/ca-certificates
docker cp ca.crt <engine>:/var/opt/mssql/security/ca-certificates/spike-ca.crt
docker restart <engine>
```

Results against MinIO (`s3://<minio-ip>:9000/spikebkt/folder/spike.bak`):

| Step | Result |
|---|---|
| Before the CA is placed | Msg 3201 ... Operating system error **12175** (certificate not trusted) |
| After placing the CA and restarting the engine | **LABELONLY, HEADERONLY, FILELISTONLY and RESTORE DATABASE (with MOVE) all work** from the path-style URL |
| `RESTORE_OPTIONS = '{"s3":{"region":"eu-west-1"}}'` on LABELONLY | **accepted** |
| same on HEADERONLY | **accepted** |
| same on FILELISTONLY | **accepted** |
| same on RESTORE DATABASE | accepted (restore completed) |
| Malformed option, `{"s3":{"regionX":"eu-west-1"}}` on LABELONLY | Msg 11559 "The RESTORE_OPTIONS JSON parameter region is missing or incorrectly formatted. Please check the formatting of the JSON." |
| `RESTORE_OPTIONS='not json'` | Msg 11558 "The RESTORE_OPTIONS JSON string could not be parsed. Please check the formatting of the JSON." |
| Credential named after a folder (`s3://<minio-ip>:9000/spikebkt/folder`), URL below it | **found**; restore read works |
| Credential named after the host only (`s3://<minio-ip>:9000`) | not used: error 86 |
| Wrong secret (credential with a wrong secret) | Msg 3201 "Cannot open backup device 's3://<minio-ip>:9000/<bucket>/folder/spike.bak'. Operating system error 5(Access is denied.)." |
| Wrong access key id | same, error 5 |
| Object missing in an existing bucket | Msg 3201 ... Operating system error 2(The system cannot find the file specified.) |
| Bucket missing | same, error 2 |
| Same texts for FILELISTONLY and RESTORE DATABASE | yes, identical Msg 3201 then Msg 3013 "RESTORE ... is terminating abnormally." |

MinIO ignores the region (it accepted `eu-west-1`), so this settles that the
three read statements and RESTORE parse the option and take the same JSON, not
that real S3 needs it. The engine's error log repeats the same error text and
does not contain the secret.

Azurite (`mcr.microsoft.com/azure-storage/azurite`, `azurite-blob --cert
--key --loose --skipApiVersionCheck`, own account `spikeacct` with a generated
key `<az-key>`, same test CA):

| Step | Result |
|---|---|
| `https://<azurite-ip>:10000/spikeacct/spikecont/...` path style, credential named after the container or the account | not usable: error 86 (credential not matched), or 87 for the credential `https://<ip>:10000/spikeacct`. Azurite saw no request from the engine |
| Virtual-host style: Azurite on port 443, certificate with SAN `DNS:spikeacct.blob.core.windows.net`, an `/etc/hosts` line in the engine container pointing the name at Azurite, credential `https://spikeacct.blob.core.windows.net/spikecont`, container SAS `sp=rl` | requests reach Azurite, then fail: the engine sends `PUT ...?comp=lease` and Azurite answers 403, so Msg 3201 ... Operating system error **13**(The data is invalid.) on every read statement |
| Same with a container SAS `sp=rwl` (write added) | **LABELONLY, HEADERONLY, FILELISTONLY and RESTORE DATABASE (with MOVE) all work** |
| Wrong SAS signature | error 5 |
| Missing blob | error 2 |
| Credential named after a folder below the container (`.../spikecont/folder`) | **not found: error 86** |

Result: **Azurite works as a Blob restore source**, but only over HTTPS on
port 443 in virtual-host style with a test certificate for the
`<account>.blob.core.windows.net` name, a hosts entry in the engine container
and a SAS that includes write permission, because the engine takes a blob lease
even when it only reads. Two questions follow for real Azure, both open: does
a real storage account also take the lease on a read-only restore (the
documented SAS permissions for restore are read and list), and does the
folder-named credential fail there as it does on Azurite (the lookup is done in
the engine, so Azurite is likely a good proxy, but it is not proof). The design
keeps the container-level name (D4's fallback).

### 1.3: Managed Instance

Not done. It needs a real Managed Instance on the SQL Server 2022 update
policy. The question (does a copy-only backup carry an `XTP` filegroup) stays
open and is on the 0.3 release gate in `docs/roadmap.md`.
