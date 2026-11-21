using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Pools;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>Tasks 4.14 and 4.15: the edition reaches the engine, and the engine reports it back.</summary>
[Trait("Category", "Integration")]
public sealed class EditionIntegrationTests(ITestOutputHelper output)
{
    private const string Image2025 = "mcr.microsoft.com/mssql/server:2025-latest";

    [IntegrationTheory]
    [InlineData("developer", "Developer", "Developer Edition")]
    [InlineData("standard-developer", "StandardDeveloper", "Standard Developer Edition")]
    [InlineData("enterprise-developer", "EnterpriseDeveloper", "Enterprise Developer Edition")]
    public async Task Each_development_edition_starts_on_a_2025_image_and_the_engine_reports_it(string setting, string pid, string reported)
    {
        await using var world = new EngineWorld();
        world.Environment["SQLBRANCH_ENGINE_IMAGE"] = Image2025;
        world.Environment["SQLBRANCH_ENGINE_EDITION"] = setting;

        var init = world.RunCli("init", "--format", "json");
        output.WriteLine($"{setting}: init took {init.Elapsed.TotalSeconds:F1} s");
        Assert.True(init.Exit == 0, init.Out + init.Err);

        var env = (await EngineWorld.DockerAsync("inspect", "--format", "{{range .Config.Env}}{{println .}}{{end}}", world.EngineName)).StdOut;
        Assert.Contains($"MSSQL_PID={pid}", env.Split('\n'));

        var status = JsonDocument.Parse(world.RunCli("status", "--format", "json").Out).RootElement.GetProperty("engine");
        Assert.Contains(reported, status.GetProperty("edition").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("17.", status.GetProperty("version").GetString(), StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task A_tag_without_a_year_that_runs_2022_is_refused_with_exit_2_and_everything_is_removed()
    {
        await using var world = new EngineWorld();
        // A local-only tag of the 2022 image whose name carries no year. Removing the tag never removes the image.
        var tag = $"mcr.microsoft.com/mssql/server:sqlbranch-it-team-{world.Id}";
        Assert.Equal(0, (await EngineWorld.DockerAsync("tag", EngineWorld.EngineImage, tag)).ExitCode);
        try
        {
            world.Environment["SQLBRANCH_ENGINE_IMAGE"] = tag;
            world.Environment["SQLBRANCH_ENGINE_EDITION"] = "standard-developer";

            var init = world.RunCli("init", "--format", "json");

            output.WriteLine(init.Out);
            Assert.Equal(2, init.Exit);
            Assert.Contains("2025", init.Out, StringComparison.Ordinal);
            Assert.Null(await world.Runtime.InspectAsync(world.EngineName, CancellationToken.None));
            var pool = new ManagedPool(world.Runtime, world.Paths);
            Assert.False(await pool.FileExistsAsync(CancellationToken.None));
            Assert.False(await pool.IsMountedAsync(CancellationToken.None));
        }
        finally
        {
            await EngineWorld.DockerAsync("rmi", tag);
        }
    }
}
