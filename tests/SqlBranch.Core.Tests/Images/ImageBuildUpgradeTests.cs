using System.Text.Json;
using System.Text.RegularExpressions;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

/// <summary>Task 4.7: a build re-reads the pool format just before it publishes (design D6 step 2) and the item it leaves is adopted later (D7).</summary>
public sealed partial class ImageBuildTests
{
    private sealed class MovingClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.UtcTicks;
    }

    [GeneratedRegex(@"printf %s '(?<b64>[A-Za-z0-9+/=]+)'")]
    private static partial Regex PrintedValue();

    [GeneratedRegex(@"printf %s '(?<id>[^']+)' > '/pool/images/\.ids/")]
    private static partial Regex IdWriteOf();

    /// <summary>Runs <paramref name="onManifest"/> when the build writes the manifest of <paramref name="image"/>, which is just before it publishes.</summary>
    private void AtManifestWrite(string image, Action onManifest)
    {
        var previous = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (script.Contains($"base64 -d > '/pool/images/{image}/manifest.json'", StringComparison.Ordinal))
            {
                onManifest();
            }

            return previous(container, command);
        };
    }

    [Theory]
    [InlineData(UpgradeRecord.Done, true)]
    [InlineData(UpgradeRecord.Running, false)]
    public void A_build_that_began_on_format_1_publishes_by_the_format_it_reads_just_before_it_publishes(string state, bool catalog)
    {
        AtManifestWrite("shop", () => Format2Pool.Apply(_world, state));

        var run = Build();

        Assert.Equal(0, run.Exit);
        Assert.Equal(catalog, run.Json.GetProperty("image").GetProperty("version").ValueKind == JsonValueKind.String);
        Assert.Equal(catalog, Ran("catalog.json.new"));
        Assert.Equal(catalog, Ran("/pool/images/.ids/shop.new"));
        // During an unfinished upgrade the image is left in the format-1 layout: the manifest is the last write of the image.
        Assert.True(Ran("base64 -d > '/pool/images/shop/manifest.json'"));
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public async Task Build_of_a_new_name_during_the_upgrade_is_left_for_adoption_and_the_upgrade_does_not_list_it(string mode)
    {
        _world.Host.EngineProbe = mode;
        _world.Files["/work/crm.bak"] = 1_000_000;
        var clock = new MovingClock(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero));
        _world.Clock = clock;
        string? manifest = null;
        string? catalog = null;
        var restoring = new ManualResetEventSlim();
        var proceed = new ManualResetEventSlim();
        Task<(int Exit, string Out, string Err)>? build = null;

        var previous = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (script == PoolUpgrader.EnumerationScript)
            {
                // The upgrade finds the branch dev1, which a reset still holds, and no image folder of crm2.
                return FakeContainerRuntime.Ok("SQLBRANCH_UPBRANCH|dev1\n");
            }

            if (script == SqlBranch.Core.Images.ImageCatalog.ListingScript)
            {
                return FakeContainerRuntime.Ok(
                    (manifest is null ? string.Empty : $"SQLBRANCH_IMAGE|crm2|{manifest}\n") + (catalog is null ? string.Empty : $"SQLBRANCH_CATALOG|crm2|{catalog}\n"));
            }

            if (script.Contains("SQLBRANCH_MANIFEST=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_MANIFEST=" + (manifest ?? "none"));
            }

            if (script.Contains("base64 -d > '/pool/images/crm2/manifest.json'", StringComparison.Ordinal))
            {
                manifest = PrintedValue().Match(script).Groups["b64"].Value;
            }
            else if (script.Contains("/pool/images/crm2/catalog.json.new", StringComparison.Ordinal) && script.Contains("base64 -d", StringComparison.Ordinal))
            {
                catalog = PrintedValue().Match(script).Groups["b64"].Value;
                _catalogFile = catalog;
            }
            else if (IdWriteOf().Match(script) is { Success: true } id)
            {
                _lastId = id.Groups["id"].Value;
            }

            return previous(container, command);
        };

        var restore = _world.Connector.ParameterizedExecuteHandler;
        _world.Connector.ParameterizedExecuteHandler = (sql, parameters) =>
        {
            restore?.Invoke(sql, parameters);
            if (sql.Contains("RESTORE DATABASE", StringComparison.Ordinal) && !restoring.IsSet)
            {
                // The build is restoring: it has taken the lock of crm2 and has no image folder yet.
                restoring.Set();
                if (!proceed.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("The upgrade never reached its wait.");
                }
            }
        };

        _world.Connector.HeldLocks.Add(EngineLocks.Branch("dev1"));
        _world.UpgradeDelay = (_, _) =>
        {
            // The upgrade waits for the reset of dev1. The build publishes now, then the reset ends.
            proceed.Set();
            if (!build!.Wait(TimeSpan.FromSeconds(30), CancellationToken.None))
            {
                throw new TimeoutException("The build never ended.");
            }

            _world.Connector.HeldLocks.Remove(EngineLocks.Branch("dev1"));
            return Task.CompletedTask;
        };

        build = Task.Run(() => _world.Run("image", "build", "crm2", "--from-bak", "crm.bak", "--format", "json"));
        Assert.True(restoring.Wait(TimeSpan.FromSeconds(30)));
        var upgrade = _world.Run("pool", "upgrade", "--yes", "--format", "json");
        var built = await build;

        Assert.Equal(0, built.Exit);
        Assert.Equal(0, upgrade.Exit);
        using var upgraded = JsonDocument.Parse(upgrade.Out);
        Assert.Empty(upgraded.RootElement.GetProperty("images").EnumerateArray());
        using var builtJson = JsonDocument.Parse(built.Out);
        Assert.Equal(JsonValueKind.Null, builtJson.RootElement.GetProperty("image").GetProperty("version").ValueKind);
        Assert.False(Ran("/pool/images/.ids/crm2"));
        Assert.False(Ran("/pool/images/crm2/catalog.json"));

        // The pool is format 2 now, and crm2 is a legacy image: listed with a null version.
        var listed = JsonDocument.Parse(_world.Run("image", "list", "--format", "json").Out).RootElement.GetProperty("images");
        var row = Assert.Single(listed.EnumerateArray());
        Assert.Equal("crm2", row.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("version").ValueKind);

        // The first command that writes with it adopts it.
        clock.Now = new DateTimeOffset(2026, 12, 1, 9, 5, 0, TimeSpan.Zero);
        _world.Connector.ParameterizedScalarHandler = (_, _) => 0;
        var created = JsonDocument.Parse(_world.Run("branch", "create", "d2", "--from", "crm2", "--format", "json").Out).RootElement;
        Assert.Equal("crm2@20261201T090500000Z", created.GetProperty("branch").GetProperty("imageVersion").GetString());
        listed = JsonDocument.Parse(_world.Run("image", "list", "--format", "json").Out).RootElement.GetProperty("images");
        Assert.Equal("crm2@20261201T090500000Z", Assert.Single(listed.EnumerateArray()).GetProperty("version").GetString());
    }
}
