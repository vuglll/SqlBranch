# Proposal

## Why

Release 0.1 is a command-line tool, which most developers will not pick up on
its own. The largest audience is teams that already run integration tests
against SQL Server in containers: Testcontainers.MsSql shows 45.6M total
downloads on nuget.org (https://www.nuget.org/packages/Testcontainers.MsSql,
checked 2026-11-17, recorded in
`docs/research/2026-11-17-test-fixtures-facts.md`). Those tests start from an
empty database or wait for a restore. A one-line fixture that hands each test
a fresh, realistic database is the way in, and it is the launch demo. AI
agents need the same thing with guardrails.

## What Changes

- Add the `SqlBranch.Testing` NuGet package: create a branch from C# in one
  line and remove it on dispose. It is one package; the Core library ships
  inside it and is not a public API yet.
- Add short-lived branches with a time limit, labels on create, a label
  filter on `branch list`, and `sqlbranch branch prune` to clean up after
  crashed test runs. `branch list` and `status` show expiry.
- Build images from a folder of SQL scripts, for teams without a backup to
  hand over.
- Run post-build scripts on any image before it is sealed. This is the hook
  for masking and trimming data. Build and post-build scripts run under a
  login that owns only the build database; script builds refuse to start
  while any database allows `guest`, a build fails when scripts leave a
  database DDL trigger or a database that is not online, read-write and
  multi-user. A backup with post-scripts is refused before ownership changes if
  an enabled database trigger handles the ownership change. An optional `--script-timeout` limits each script.
- Generate an agent skill from command metadata, ship it with each release,
  and add `sqlbranch skill export`.
- JSON output gains fields and two new documents; `schemaVersion` stays 1.

Targets release 0.2 in `docs/roadmap.md`.

**Prerequisite.** The roadmap's "Needs first" for 0.2 is `add-branching-core`
archived. It was archived on 2026-11-17, so the prerequisite is met. The 0.1
*release* (the merge of dev into main) is still held for its gate: x86-64
timings and G2, the native Linux XFS integration run, the README quick start
on clean Linux, and the Windows check. This change can be built on dev now,
but 0.2 is not merged into main or published until the 0.1 gate passes.

Out of scope: test-framework-specific packages, a public API for
`SqlBranch.Core`, the GitHub Action, the MCP server, a masking rules engine,
a background reaper for leaked branches, building images from scripts in
subfolders.

## Capabilities

### New Capabilities

- `test-fixtures`: the library API that gives automated tests their own branch and cleans it up.
- `agent-skill`: the generated skill that teaches AI coding agents to use SqlBranch safely.

### Modified Capabilities

- `images`: adds building from SQL scripts, post-build scripts and limited script rights; changes "Image manifest", "List images", "Recovery model of images", "Images carry a minimal log" and "Failed builds leave nothing behind".
- `branches`: adds short-lived branches, labels, the label filter and pruning; changes "List branches" and "Branch commands act only on branches".
- `engine`: changes "Status report" (expired branches), "Status reports mismatches" (leftover build logins) and "Commands before init" (`skill export` works before init).
- `configuration`: changes "Where the password may appear" to allow the testing library's connection-string property.

## Unverified facts

- Whether a build login that owns only the build database could reach a
  branch whose source had `guest` enabled. The change does not depend on the
  answer: script builds always refuse to start while such a database exists.
  Task 2.6e records the observed preflight rejection with the guest check enabled.
- The practical number of parallel fixture branches before engine memory
  runs out. Settled by the measurement in task 3.6.

## Impact

- New project `src/SqlBranch.Testing` and its tests; `SqlBranch.Core` stays
  non-packable and is bundled into the `SqlBranch.Testing` package.
- New folder `skills/sqlbranch/`, generated, committed, and checked by the
  unit tests.
- `SqlBranch.Core` gains short-lived branches, the image builder is split
  into source steps and shared seal steps, and the builder gains a script
  source, a post-build step and a build login.
- `docs/branches.md`, `docs/images.md`, `docs/cli-output.md`,
  `docs/engine.md`, a new `docs/testing.md` and `docs/agent-skill.md`, and
  the README's "Use it in tests" section.
