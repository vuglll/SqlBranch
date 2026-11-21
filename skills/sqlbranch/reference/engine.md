# SqlBranch engine commands

## sqlbranch engine start

Start the engine and wait for SQL Server.

Resume a stopped engine before opening a branch.

```sh
sqlbranch engine start
```

**Destroys** nothing

## sqlbranch engine stop

Stop the SQL Server engine.

Shut down an idle development engine after coordinating with its users.

```sh
sqlbranch engine stop
```

**Destroys** Active connections are closed when the engine stops.
