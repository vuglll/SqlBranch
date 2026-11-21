# Agent skill acceptance on 2026-11-18

Codex (GPT-6) received the exported SqlBranch skill and a temporary Git repository named `invoice-report`. The repository contained a .NET invoice report, three database-dependent checks, a project-local CLI wrapper and a test runner. The supplied `invoices` image held paid and unpaid invoices on a dedicated SqlBranch engine. The agent received the failing-report task without being given the faulty query or the fix.

| Criterion | Result | Evidence |
|---|---|---|
| Create and use a private branch | Pass | The agent created `report-fix-g4-731` from `invoices` with a one-hour expiry and used it for every test invocation. |
| Reproduce and fix the data-dependent failure | Pass | Customer 7 initially returned 13 against an expected outstanding balance of 5. Filtering out paid invoices in `Report.cs` brought all three checks to green. |
| Preserve the test | Pass | Independent inspection of the fixture's Git diff showed a single production query change. The checks, runner and seed data stayed intact. |
| Clean up | Pass | The agent removed its branch. An independent branch listing was empty before the fixture engine was torn down. |

The agent used these commands from the fixture repository:

```sh
./sqlbranch status --format json
./sqlbranch image list --format json
./sqlbranch branch create report-fix-g4-731 --from invoices --ephemeral --ttl 1h
./check report-fix-g4-731
./check report-fix-g4-731
./check report-fix-g4-731
./sqlbranch branch rm report-fix-g4-731
./sqlbranch branch list --format json
git diff --check
git status --short
git diff -- Report.cs
```

The first test invocation reproduced the failure. The second inspected the schema and reproduced it again. The third ran after the agent removed the diagnostic and corrected the query.
