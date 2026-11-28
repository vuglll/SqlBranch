# Private SQL Server branches for .NET tests

`SqlBranch.Testing` creates a writable database from a sealed SqlBranch image. Every test can use a separate branch while sharing the same SQL Server engine. The .NET 10 package includes its Core assembly and works with xUnit, NUnit and MSTest. The supported library API is in the `SqlBranch.Testing` namespace.

## Prepare an image

The package needs the `sqlbranch` command-line tool once per machine, to create the engine and build images. Tests do not run it. Install it with the same version as the package:

```sh
dotnet tool install --global SqlBranch
```

Start Docker, run `sqlbranch init`, and build an image for your tests. For example, put your schema and seed data in numbered SQL files under `db/` and run:

```sh
sqlbranch image build shop --from-scripts db
```

Add a reference to `SqlBranch.Testing` in the test project. To try an example from this checkout, run `SQLBRANCH_IT=1 dotnet test samples/Xunit`. The NUnit and MSTest samples use the same switch and image.

## Use a branch in a test

An xUnit test can scope the database to one method. The connection string contains the engine password, so keep it out of test output. Logging the fixture itself prints its branch name and clone mode.

```csharp
[Fact]
public async Task OpensPrivateDatabase()
{
    await using var db = await SqlBranchDatabase.CreateAsync("shop");
    await using var connection = new SqlConnection(db.ConnectionString);
    await connection.OpenAsync();
    Assert.Equal(db.Name, connection.Database);
}
```

For NUnit, allocate the branch in setup and dispose it in teardown. For parallel test cases that share a fixture instance, keep the branch local to the method or use a separate fixture instance per test case.

```csharp
private SqlBranchDatabase? db;

[SetUp]
public async Task SetUp() => db = await SqlBranchDatabase.CreateAsync("shop");

[TearDown]
public async Task TearDown()
{
    if (db is not null) await db.DisposeAsync();
}
```

MSTest also supports method-scoped disposal:

```csharp
[TestMethod]
public async Task OpensPrivateDatabase()
{
    await using var db = await SqlBranchDatabase.CreateAsync("shop");
    await using var connection = new SqlConnection(db.ConnectionString);
    await connection.OpenAsync();
    Assert.AreEqual(db.Name, connection.Database);
}
```

The runnable projects are under `samples/Xunit`, `samples/NUnit` and `samples/MSTest`.

## Lifecycle and configuration

- The default name is `t-` followed by eight lowercase hexadecimal characters. Set `SqlBranchOptions.Prefix` for a recognizable name. It follows the branch naming rules and can have up to 54 characters.
- Branches expire after one hour by default. Set `TimeLimit` to a positive duration of up to 365 days. Use `Labels` for test run metadata. The library sets `sqlbranch.owner=testing`.
- On a format-2 pool `ResetAsync()` restores the image version the branch was created from. When that version was removed (for example `sqlbranch image rm shop --force`) or its base is unknown, reset throws without changing the branch. The message names the branch and its image, says that the version it was created from no longer exists, and names creating a new branch as the fix; it never contains the engine password and does not suggest `sqlbranch image build`, because rebuilding the image would not restore the old base.
- `await db.ResetAsync()` restores the image data and keeps the name, connection string, labels and original expiry. Open new connections after a reset. Disposal closes connections on the server and removes the branch files.
- After a crash, the next test process prunes expired fixture branches on its first create. It retries a failed prune on later creates. If disposal fails, the library writes a cleanup message to standard error. Set `SqlBranchEnvironment.Log` to route those messages to your test runner, or run `sqlbranch branch prune` after expiry.
- Use `await SqlBranchEnvironment.IsAvailableAsync()` to check engine reachability. A missing engine, stopped engine or missing image on create raises `SqlBranchUnavailableException`. Its `FixCommand` identifies the relevant CLI command.
- Configuration follows the CLI. Environment variables override the repository and user settings. The library finds the nearest `.sqlbranch.yml` starting at the process working directory. Set `SqlBranchOptions.ConfigFile` to specify the repository config explicitly.

## Older packages on an upgraded pool

`sqlbranch pool upgrade --yes` cannot be undone and locks SqlBranch.Testing 0.2.x out of the pool. Before you run it,
stop every 0.2.x test run that uses the pool, in CI as well, and then update both the `sqlbranch` tool and the
SqlBranch.Testing reference in every test project to 0.3 or later. Updating the global tool does not update the package.
See [engine.md](engine.md#older-tools-on-an-upgraded-pool) for what the upgrade changes.

With the engine running, SqlBranch.Testing 0.2.0, 0.2.1 and 0.2.2 all behave the same on an upgraded pool: create
throws `InvalidOperationException`, after the package writes `Could not prune expired fixtures: <message>` to the log
(standard error by default), and `SqlBranchEnvironment.IsAvailableAsync()` returns false. The message has no fix command:

```text
The engine credentials file in <pool> is not valid.
```

`<pool>` is the pool directory or `Docker volume <name>`. With the engine stopped the package reports the stopped
engine, by the 0.2.2 source, as before the upgrade.

**Tests that skip when the 0.2.x availability check returns false are skipped silently after the upgrade.** The check
logs nothing and the tests do not fail, so a green run can mean nothing ran. Look for skipped counts in CI, and update the
package in every test project before you resume test runs.

## Parallel branches

Set `SqlBranchEnvironment.MaxConcurrentBranches` once in test-runner setup to cap the number of live fixture branches in that process. The default is unlimited. Creates above the limit wait for disposal and honor their cancellation token. Each test-runner process has its own limit.

On 2026-11-18 a Mac with 16 GiB of physical RAM and 7.75 GiB available to Docker Desktop sustained **20 live fixture branches** in both thin and copy modes. Each branch opened a connection and queried the same 100-row image. This is a measured working point for a small image. Larger images, heavier queries and multiple test-runner processes can use substantially more resources. On 2026-11-20, on native x86-64 Linux with the engine container capped at 16 GB (`docker update --memory 16g --memory-swap 16g`; not Docker Desktop), **400 branches** were created at once from a 100-row image, all 400 answered a query at the same time, and the engine used 3.4 GiB. No limit was reached. Creates run about one at a time (about 0.5 s each, 217 s for 400), so with many branches the wait for creation, not memory, sets the practical number. Details: [docs/research/2026-11-20-x86-64-release-gate-run.md](research/2026-11-20-x86-64-release-gate-run.md).

During an unfinished pool upgrade, fixture create and reset throw with
“The pool upgrade did not finish” and name `sqlbranch pool upgrade --yes` as the
fix. If the upgrade lock is still held, the message says an upgrade is running.
Reset leaves the branch and its data unchanged. The availability check returns
false and writes a library log line naming the same command; it returns true
again after the upgrade finishes and the engine is reachable.

Fixture disposal does not throw during an unfinished upgrade. It leaves the
branch and logs `sqlbranch pool upgrade --yes`, with instructions to run
`sqlbranch branch prune` after the branch expires. Finish the upgrade before
pruning expired fixtures.
