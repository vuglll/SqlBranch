# SqlBranch skill commands

## sqlbranch skill export

Export the agent skill and command reference files.

Install the skill in a project or refresh its generated files after a CLI change.

```sh
sqlbranch skill export --dir .claude/skills/sqlbranch
```

**Destroys** With --force, replaces existing generated files. Removes stale export staging directories for this target.
