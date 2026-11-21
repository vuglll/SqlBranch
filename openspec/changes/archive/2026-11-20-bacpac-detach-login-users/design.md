# Design

## Context

F2 imports a BACPAC with SqlPackage under a restricted, build-owned login that cannot create server logins (design D3 of `add-bacpac-source`). A package with login-mapped users makes SqlPackage create those logins, so the import fails. F2 chose to refuse such packages. Real exports almost always contain them.

## Goals / Non-Goals

**Goals:** import packages with login-mapped users under the same restricted login, keep each user's database-level permissions and ownership, and say which users changed.

**Non-Goals:** creating or changing server logins named in the package or already on the engine (the build-owned import login is unchanged); signing in to a branch as an application user (a possible later opt-in that creates per-branch logins); Microsoft Entra ID (external provider) users and Windows users, which stay `unsupported_feature`; changing the image manifest or archive format.

## Decisions

**D1. Rewrite the model, not the server.** Remove `SqlLogin` elements and turn login-mapped users into users without a login in a scratch copy of the package, then import that copy. Alternatives considered: creating the logins on the engine as administrator (server-wide names and SIDs collide between images, needs full authority, and would set passwords taken from the file); dropping the users (breaks their permissions and owned objects); pre-creating and later dropping logins (server-wide side effects and races between builds).

**D2. What the rewrite changes.** A user-defined `SqlUser` is login-mapped when it has a `Login` relationship or a `LoginIdentifierScript` property, resolved by model identity including `Disambiguator`. Its mapping is replaced with the serialized shape of a `CREATE USER ... WITHOUT LOGIN` user exported by the pinned SqlPackage (task 1.1); grants, role memberships and ownership elements are kept. Every `SqlLogin` is removed, including one no user references. Elements removed with a login are limited to server role memberships and server-level permissions whose member or grantee is that login. If any retained element still references a removed login, the build refuses before import with `unsupported_feature` and names the element type and name. Users mapped to Windows logins or external providers are not detached and keep today's behaviour.

**D3. Validate, then copy.** The existing package validation (ZIP, CRCs, model checksum when present) runs on the original first; a damaged package still exits `bacpac_invalid`. The original's SHA-256 and compressed length are captured before any rewrite and used for provenance and the pool-space check. The rewrite writes a second owner-only file in the build's scratch directory, streaming every entry except `model.xml` and `Origin.xml` unchanged, then writes the new `model.xml` and an `Origin.xml` whose model checksum is the SHA-256 of the exact bytes written (no checksum is added if the original had none; other checksum entries are kept). The copy is reopened and validated before SqlPackage reads it. The staged original keeps its identity; cleanup verifies and removes both files. Exhausted client scratch exits `temp_full`; unverifiable cleanup exits `operation_failed`; neither publishes. A package with no login-mapped users and no `SqlLogin` makes no additional copy.

**D4. Reporting.** Text and JSON results list the detached user names (control characters replaced, 128 characters each), `removedLogins` (count of `SqlLogin` elements) and `removedServerObjects` (count of other removed elements, each element counted once). The phase times include `rewrite` when it ran. The image manifest and archive format are unchanged: they keep the original package's basename, SHA-256 and URL.

## Risks / Trade-offs

- [A DacFx version reads the rewritten model differently] → the integration test imports a rewritten Azure-shaped package with the pinned SqlPackage and checks `sys.database_principals`.
- [Large packages need a second copy in scratch] → reported up front with the size notice; only packages with login-mapped users pay it.
- [Users that owned objects or schemas] → kept as users without a login, so ownership and permissions resolve.
