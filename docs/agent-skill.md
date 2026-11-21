# SqlBranch for coding agents

Install the skill in a project from its root. The destination's parent must be writable so the CLI can stage files beside the destination. Export works before `sqlbranch init` and with Docker offline.

```sh
sqlbranch skill export --dir .claude/skills/sqlbranch --format json
```

The exported `SKILL.md` guides the agent through checking the engine and available images, creating its own branch, reproducing a failing test, verifying a fix, and removing the branch. It explains how to pass the connection string to a test without printing it. Command references under `reference/` list each command's purpose, when to use it, an example, and what it destroys.

To refresh an existing installation, use:

```sh
sqlbranch skill export --dir .claude/skills/sqlbranch --force
```

The CLI replaces generated files and preserves other project files. A killed export can leave a sibling named `<dir>.sqlbranch-tmp-<8 lowercase hex characters>`. The next export to that destination clears matching stale directories.

## Regenerating the repository copy

Edit the metadata beside the CLI command registration or the guide in `SkillGenerator.cs`, then run this from the SqlBranch repository root:

```sh
dotnet run --project src/SqlBranch.Cli -- skill export --dir skills/sqlbranch --force
```

Commit the updated files under `skills/sqlbranch/` with the command change. The unit suite compares those files with freshly generated output.
