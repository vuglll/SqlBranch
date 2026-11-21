# Tasks

After approval and merge into dev, implement with GPT-6.2 Sol (medium) on feat/bacpac-detach-login-users, after `fix/bacpac-login-users` is merged. For every group run a zero-warning build, the unit tests, and the BACPAC integration tests with SQLBRANCH_IT=1 and SQLBRANCH_IT_SQLPACKAGE set; a skipped SqlPackage test does not count. Tick only verified tasks, commit and stop for review.

## 1. Confirm the model shape

- [x] 1.1 With the pinned SqlPackage, export a fixture database made by checked-in DDL: logins `report_reader` and `sales_app` with users of the same names, `report_reader` owning schema `reports`, `sales_app` granted SELECT on `dbo.Orders` and in a database role, login `sales_app` in a server role, and a control user created `WITHOUT LOGIN`. Record the elements of each, the tool version and the clone mode. Add the findings and the Azure `Login`-relationship shape to `docs/research/2026-11-19-bacpac-source-facts.md`.
- [x] 1.2 Rewrite that export by hand as in design D2, plus an Azure-shaped variant with `Login` relationships and a `Disambiguator`, import both under the restricted build login, and record `sys.database_principals` (type `SQL_USER`, authentication `NONE`), ownership and grants. Fixtures are synthetic; no customer package is stored.

## 2. Rewrite and import

- [x] 2.1 Implement the rewrite in `BacpacPackage`: mapping detection by model identity, the without-login shape from 1.1, login and server-object removal, the dangling-reference refusal, and the checksum rules of design D3. Unit-test both shapes, a disambiguated name, built-in and contained users, a standalone login, server role membership and permission removal, a dangling reference, missing and mismatched checksums, and a package that needs no rewrite.
- [x] 2.2 In `BacpacBuilder`, capture the original digest and length before the rewrite, write and validate the second scratch file, time `rewrite`, and remove both files on success, failure and Ctrl+C. Unit-test `temp_full` while writing, cancellation, failed ownership verification, a concurrent build's files, and that the supplied file and provenance are unchanged.
- [x] 2.3 Report `detachedUsers`, `removedLogins` and `removedServerObjects` in JSON and text. Retire `bacpac_login_users` from the error codes and `docs/cli-output.md`.

## 3. Prove it end to end

- [x] 3.1 Integration tests on loop-xfs and plain: build from both 1.2 fixtures (local, and one through the URL test storage), create a branch, and check users, ownership, grants and role membership, a pre-existing `sales_app` engine login unchanged, `bacpacSha256` of the supplied file, and an export/import round trip of the image.
- [x] 3.2 Update `docs/images.md`: login-mapped users are detached, how to see which, and that signing in as them is not supported.
