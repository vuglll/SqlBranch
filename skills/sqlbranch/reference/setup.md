# SqlBranch setup commands

## sqlbranch init

Initialize the pool and SQL Server engine.

Set up a development engine before creating images.

```sh
sqlbranch init
```

**Destroys** nothing

## sqlbranch doctor

Check Docker and the configured engine prerequisites.

Diagnose setup problems before or after init.

```sh
sqlbranch doctor
```

**Destroys** nothing

## sqlbranch status

Report engine and pool state, including mismatches.

Check the engine before starting work or after cleanup.

```sh
sqlbranch status --format json
```

**Destroys** nothing

## sqlbranch teardown

Remove the SqlBranch installation and its data.

Decommission the entire development engine with explicit authorization.

```sh
sqlbranch teardown --force
```

**Destroys** The engine, every database on it, all images and branches, and the SqlBranch-managed pool data.
