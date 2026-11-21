using System.Text.Json;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

public sealed class BranchPruneTests
{
    private readonly BranchWorld _world = new();

    private void AddExpired(string name)
    {
        _world.AddBranch(name);
        _world.Records[name] = (_world.RecordOf(name) with { Ephemeral = true, ExpiresAt = DateTimeOffset.UnixEpoch }).ToJson();
    }

    [Fact]
    public void No_expired_branches_returns_three_empty_lists()
    {
        _world.AddBranch("dev1");
        var run = _world.Run("branch", "prune", "--format", "json");
        Assert.Equal(0, run.Exit);
        using var json = JsonDocument.Parse(run.Out);
        foreach (var field in new[] { "removed", "skipped", "failed" })
        {
            Assert.Empty(json.RootElement.GetProperty(field).EnumerateArray());
        }
        Assert.True(_world.Records.ContainsKey("dev1"));
    }

    [Fact]
    public void A_locked_branch_is_skipped_and_deletion_failure_does_not_stop_other_removals()
    {
        AddExpired("a");
        AddExpired("b");
        AddExpired("c");
        _world.World.Connector.HeldLocks.Add(EngineLocks.Branch("a"));
        _world.FailScript = s => s.StartsWith("rm -rf '/pool/branches/b'", StringComparison.Ordinal);
        var run = _world.Run("branch", "prune", "--format", "json");
        Assert.Equal(1, run.Exit);
        using var json = JsonDocument.Parse(run.Out);
        var root = json.RootElement;
        Assert.Equal("operation_failed", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("a", root.GetProperty("skipped")[0].GetString());
        Assert.Equal("c", root.GetProperty("removed")[0].GetString());
        Assert.Equal("/pool/branches/b", root.GetProperty("failed")[0].GetProperty("path").GetString());
        Assert.True(_world.World.Connector.Databases.ContainsKey("a"));
        Assert.False(_world.Records.ContainsKey("c"));
    }
}
