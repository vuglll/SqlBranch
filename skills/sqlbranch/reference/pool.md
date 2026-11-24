# SqlBranch pool commands

## sqlbranch pool upgrade

Upgrade the pool to format 2 (image versions and branch bases). Run it only when the user asked for it.

Only when the user or maintainer explicitly asks for it, after stopping every SqlBranch 0.2.x tool and test run. Never run it on your own initiative.

```sh
sqlbranch pool upgrade --yes
```

**Destroys** The pool's compatibility with SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x packages. It cannot be undone.
