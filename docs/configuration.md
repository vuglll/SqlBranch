# Configuration

SqlBranch works with no configuration at all. Every setting has a default,
and you change one with a flag, an environment variable or a config file.

## Where settings come from

When a setting is given in more than one place, the first of these wins:

1. A command-line flag, for example `sqlbranch init --port 16000`.
2. An environment variable starting with `SQLBRANCH_`.
3. The repository file: the nearest `.sqlbranch.yml` in the current directory
   or any parent directory. `--config <file>` uses that file instead.
4. The user file: `~/.sqlbranch/config.yml`.
5. The default.

## Settings

| Setting | Environment variable | Default | Meaning |
|---|---|---|---|
| `pool` | `SQLBRANCH_POOL` | (see below) | Directory for the pool when it is used as it is. |
| `poolBackend` | `SQLBRANCH_POOL_BACKEND` | `auto` | `auto`, `loop-xfs` or `plain`. |
| `poolSize` | `SQLBRANCH_POOL_SIZE` | `60GB` | Size of a managed pool, set when it is created. |
| `engineImage` | `SQLBRANCH_ENGINE_IMAGE` | `mcr.microsoft.com/mssql/server:2022-latest` | SQL Server container image for the engine. |
| `engineEdition` | `SQLBRANCH_ENGINE_EDITION` | `developer` | `developer`, `standard-developer` or `enterprise-developer`. |
| `port` | `SQLBRANCH_PORT` | `14330` | Engine port on 127.0.0.1. |
| `requireThin` | `SQLBRANCH_REQUIRE_THIN` | `false` | Refuse to create thin-mode branches as full copies. |
| `defaultImage` | `SQLBRANCH_DEFAULT_IMAGE` | (none) | Image `branch create` uses without `--from`. |

Details:

- **`pool`**: with `poolBackend: plain`, or when the directory already
  supports reflink, the default is `~/.sqlbranch/pool` on Linux. A managed
  pool always lives at `/var/lib/sqlbranch/` on the container host. A value
  must be an absolute path that starts with `/` and has no `:`. A bare name
  such as `sb` or `sqlbranch-pool` is refused (Docker would take it for a
  volume), and so is `/a:b`. The error names `pool` and where the value came
  from, and the exit code is 2.
- **`poolBackend`**: `auto` uses the pool directory when it can share blocks,
  otherwise creates a managed XFS pool, otherwise falls back to copy mode
  and says why. `loop-xfs` always uses a managed pool and fails if it cannot.
  `plain` always uses the directory as it is.
- **`poolSize`**: a number and a unit. `KB`, `MB`, `GB` and `TB` are powers of
  1000; `KiB`, `MiB`, `GiB` and `TiB` are powers of 1024. A pool cannot be
  resized after it is created.
- **`engineEdition`**: only development editions are allowed.
  `standard-developer` and `enterprise-developer` need SQL Server 2025 or
  later. When the image tag names an older release (`2022-latest`), the
  setting is refused at once; when the tag names none, it is checked after
  the engine starts.
- **`engineImage`, `engineEdition`, `port` and `pool`** are fixed when the
  engine is first created. A later `init` with a different value is refused.

## The config file

```yaml
version: 1
defaultImage: shop
requireThin: true
port: 15000
```

- Keys are the setting names above, plus an optional `version`, which must
  be `1`.
- An unknown key, a value of the wrong type or another `version` is an
  error (exit code 2) that names the file and the key.
- Each value is a single value, not a list or a mapping.

## Storage secrets

A build from a backup URL, and an `image export` or `image import` with an
`https://` or `s3://` URL, read their secret only from the environment, and only
for the URL kind that needs it:

| Variable | Used for |
|---|---|
| `SQLBRANCH_AZURE_SAS_TOKEN` | An `https://` Azure Blob Storage URL. One leading `?` is dropped. |
| `SQLBRANCH_S3_ACCESS_KEY_ID` | An `s3://` URL. Must not contain `:`. |
| `SQLBRANCH_S3_SECRET_ACCESS_KEY` | An `s3://` URL. Must not contain `:`. |

There are no flags and no config keys for them: flags show in shell history and
the process list, and config files are committed to repositories. A key such as
`s3SecretAccessKey` in a config file is an unknown key and exits 2, like any
other. An unset or empty variable exits 2 `config_error` before any `docker`
command, SQL or storage request runs, naming the variable and never its value.
Every form of the secret, including its URL-encoded form, is replaced by `***`
in any message. For an export or import the client sends the secret to the
storage service itself, in its own HTTPS requests, and nowhere else: it is not
passed to the engine container. The storage libraries are given only these
variables and read no other credential source (no AWS profile or `AWS_*`
variable, no Azure managed identity).

## The engine password

The `sa` password is never a setting and is never read from a config file;
a key containing `password` is refused. By default `sqlbranch init`
generates one. To choose it, set `SQLBRANCH_SA_PASSWORD` before the first
`init`. It must meet SQL Server's policy: at least 8 characters, from three
of uppercase letters, lowercase letters, digits and symbols. The password is
fixed at the first `init`.
