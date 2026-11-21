# MSSQL_PID values for the development editions

Checked on 2026-11-16 for task 4.15 of `add-branching-core`.

| Edition setting | `MSSQL_PID` value | Source |
|---|---|---|
| `developer` | `Developer` | Environment-variable page, Docker examples for 2022 and 2025 use `MSSQL_PID='Developer'` |
| `standard-developer` | `StandardDeveloper` | Edition table of the 2025 Linux page and the environment-variable page (2025 table) |
| `enterprise-developer` | `EnterpriseDeveloper` | Same two tables |

Sources, both read on 2026-11-16:

- https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-editions-and-components-2025?view=sql-server-ver17
  (table "SQL Server editions", column "Product ID (`MSSQL_PID`)")
- https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-configure-environment-variables?view=sql-server-ver17
  (table "SQL Server editions" for SQL Server 2025)

The 2025 environment-variable table does not list `Developer`, but the same
page's Docker example for `2025-latest` still passes `MSSQL_PID='Developer'`,
and the engine reports the edition back in the integration test.

The values in `EngineInitializer.EditionPid` match this table, so no code
change was needed. The integration test that starts each edition on a
`2025-latest` image is in `tests/SqlBranch.IntegrationTests`.
