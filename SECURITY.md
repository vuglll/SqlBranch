# Security policy

## Supported versions

Only the latest release receives security fixes.

## Reporting a vulnerability

Do not open a public issue. Use the repository's
**Security → Report a vulnerability** form (GitHub private vulnerability
reporting). Include the version (`sqlbranch --version`), your platform, the
steps to reproduce and the impact you expect.

You will get an acknowledgement within a week. Fixes are released as a patch
version, and the advisory is published once a fixed release is available.

## Scope

SqlBranch is for development and test use only. The engine runs SQL Server
Developer edition, listens on `127.0.0.1` only, and stores its `sa` password in
a file in the pool. In scope are, for example: a secret appearing in output,
logs or files where the documentation says it does not; the engine or pool
being reachable or writable beyond the documented boundary; and deleting or
changing anything SqlBranch did not create. Running SqlBranch against
production data is out of scope; the documentation forbids it.
