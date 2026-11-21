using SqlBranch.Core.Engine;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Engine;

public sealed class EngineLocksTests
{
    private const string Hostile = "sqlbranch:branch:x'; DROP DATABASE master; --";

    [Fact]
    public async Task The_resource_name_travels_as_a_parameter_and_never_in_the_statement()
    {
        var connector = new FakeEngineConnector { SaPassword = "p" };
        await using var session = await connector.ConnectAsync(EngineEndpoint.Loopback(1), "p", CancellationToken.None);

        var held = await EngineLocks.IsHeldAsync(session, Hostile, CancellationToken.None);

        Assert.False(held);
        var statement = Assert.Single(connector.Statements);
        Assert.DoesNotContain("DROP DATABASE", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("x'", statement, StringComparison.Ordinal);
        Assert.Equal(Hostile, Assert.Single(connector.Parameters)[EngineLocks.ResourceParameter]);
    }

    [Fact]
    public async Task A_lock_another_session_holds_is_reported_held()
    {
        var connector = new FakeEngineConnector { SaPassword = "p" };
        connector.HeldLocks.Add(Hostile);
        await using var session = await connector.ConnectAsync(EngineEndpoint.Loopback(1), "p", CancellationToken.None);

        Assert.True(await EngineLocks.IsHeldAsync(session, Hostile, CancellationToken.None));
    }
}
