# SqlBranch branch commands

## sqlbranch branch create

Create a writable database from a sealed image.

Allocate a private database before running data-dependent tests.

```sh
sqlbranch branch create agent-run42 --from shop --ephemeral --ttl 1h
```

**Destroys** nothing

## sqlbranch branch list

List branches, labels and expiry times.

Confirm which branches exist and verify cleanup.

```sh
sqlbranch branch list --format json
```

**Destroys** nothing

## sqlbranch branch prune

Remove expired short-lived branches.

Reclaim expired branches when authorized to maintain the pool.

```sh
sqlbranch branch prune
```

**Destroys** Expired short-lived branches and their data.

## sqlbranch branch conn

Return the connection string for a branch.

Pass credentials directly to the test process using a secret environment variable.

```sh
sqlbranch branch conn agent-run42 --quiet
```

**Destroys** nothing

## sqlbranch branch reset

Restore a branch from its image.

Repeat a test on a clean copy of the baseline.

```sh
sqlbranch branch reset agent-run42
```

**Destroys** All database changes in the named branch.

## sqlbranch branch rm

Remove a branch and close its connections.

Release the branch created for this run.

```sh
sqlbranch branch rm agent-run42
```

**Destroys** The named branch and its database files.
