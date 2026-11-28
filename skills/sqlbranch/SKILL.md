---
name: sqlbranch
description: Use an isolated SQL Server database branch to reproduce data-dependent failures, test changes, and clean up after the run.
---

## When to use

Use SqlBranch for tests or development work that need a writable copy of a known SQL Server image.

## Setup check

Run `sqlbranch status --format json` and `sqlbranch image list --format json`.
Confirm the engine is running and identify the image the project expects.
If setup is missing, ask the maintainer which engine and image to use.

## Getting a branch

Choose a unique branch name for this run. Create it with
`sqlbranch branch create <name> --from <image> --ephemeral --ttl 1h`.
Keep the exact name for cleanup. If the name already exists, choose a new name.

## Working in a branch

Use the project's test command against that branch. If it expects a connection string,
capture `sqlbranch branch conn <name> --quiet` directly in the test process's
environment. Keep the value out of terminal output, shell traces, logs and commits.
Reproduce the failure, fix the cause, and rerun the same test.

## Cleaning up

Close test connections and run `sqlbranch branch rm <name>` when finished.
Confirm the name is gone with `sqlbranch branch list --format json`.
Use `sqlbranch branch reset <name>` between runs that need the original image data.

## Guardrails

- Work only in branches you created.
- Never run destructive SQL against a server other than the SqlBranch engine.
- Reset or remove your branches when done.
- Never weaken a test to hide a database problem.
- Never run `sqlbranch pool upgrade` unless the user asked for it: it cannot be undone and stops SqlBranch 0.2.x tools.

**Command reference**

- [setup](reference/setup.md)
- [engine](reference/engine.md)
- [image](reference/image.md)
- [branch](reference/branch.md)
- [pool](reference/pool.md)
- [skill](reference/skill.md)
