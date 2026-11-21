# Proposal

## Why

The first real BACPAC run (an Azure SQL Database export, 2026-11-20) failed. Most real databases have users mapped to server logins for their applications. The archived F2 design refuses those users with `bacpac_login_users`, which would make every such database unusable for a BACPAC build. Asking users to drop the users at the source before every export is not realistic.

The same run showed that Azure exports link a user to its login with a `Login` relationship rather than a `LoginIdentifierScript` property. Detection of both forms is fixed separately on `fix/bacpac-login-users`. This change replaces the refusal.

## What Changes

- Before import, a logical build rewrites a scratch copy of the package: it removes server logins and the server-level objects that reference them, turns each login-mapped user into a user without a login, and recomputes the model checksum.
- Database-level permissions, role memberships and object ownership of those users are kept.
- The text and JSON results name the detached users and count the removed server-level objects. The image manifest is unchanged.
- `bacpac_login_users` is no longer raised. Packages with no login-mapped users import as before, without a copy.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `images`: the login-mapped user requirement changes from refusal to detachment, and the build size and phase result names the detached users.

## Impact

`BacpacPackage` (model inspection and rewrite), `BacpacBuilder` (scratch copy, phase timing), the BACPAC CLI output, `docs/images.md`, `docs/cli-output.md`, unit tests with Azure-shaped fixtures, and an integration test that imports a rewritten package with the real SqlPackage. The `bacpac_login_users` error code is retired. The research facts note gets the Azure model shape.
