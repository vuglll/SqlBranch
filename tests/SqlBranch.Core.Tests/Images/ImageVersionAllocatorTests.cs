using System.Text.RegularExpressions;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Images;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Images;

/// <summary>Task 3.4: version IDs and the per-name high-water mark (design D4), on a fake pool that keeps the ID files.</summary>
public sealed partial class ImageVersionAllocatorTests
{
    private sealed class SetClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 12, 1, 2, 0, 0, TimeSpan.Zero);

    private readonly CliWorld _world = new();
    private readonly Dictionary<string, string> _files = [];
    private readonly List<string> _scripts = [];

    public ImageVersionAllocatorTests()
    {
        _world.WithEngine();
        _world.Runtime.ExecHandler = (_, command) =>
        {
            var script = command[2];
            lock (_files)
            {
                _scripts.Add(script);
                if (script.Contains("SQLBRANCH_LASTID=", StringComparison.Ordinal))
                {
                    var path = Regex.Match(script, @"\[ -f '(?<p>[^']+)' \]").Groups["p"].Value;
                    return FakeContainerRuntime.Ok("SQLBRANCH_LASTID=" + (_files.TryGetValue(path, out var text) ? text : "none"));
                }

                var write = Regex.Match(script, @"printf %s '(?<id>[^']+)' > '(?<tmp>[^']+)'; sync '[^']+'; mv -f '[^']+' '(?<dst>[^']+)'");
                if (write.Success)
                {
                    _files[write.Groups["dst"].Value] = write.Groups["id"].Value;
                }

                return FakeContainerRuntime.Ok();
            }
        };
    }

    private Task<string> Allocate(string name, SetClock clock) =>
        ImageVersions.AllocateAsync(_world.Runtime, ContainerLabels.EngineName, name, clock, CancellationToken.None);

    [Fact]
    public async Task The_first_id_is_the_clock_cut_to_the_millisecond()
    {
        var id = await Allocate("crm", new SetClock(T0.AddTicks(5_000)));

        Assert.Equal("crm@20261201T020000000Z", id);
        Assert.Equal(id, _files["/pool/images/.ids/crm"]);
    }

    [Fact]
    public async Task Two_allocations_in_one_millisecond_get_consecutive_milliseconds()
    {
        var clock = new SetClock(T0);

        Assert.Equal("crm@20261201T020000000Z", await Allocate("crm", clock));
        Assert.Equal("crm@20261201T020000001Z", await Allocate("crm", clock));
        Assert.Equal("crm@20261201T020000002Z", await Allocate("crm", clock));
    }

    [Fact]
    public async Task A_clock_moved_back_still_gives_an_increasing_id()
    {
        var clock = new SetClock(T0);
        await Allocate("crm", clock);
        clock.Now = T0.AddDays(-1);

        Assert.Equal("crm@20261201T020000001Z", await Allocate("crm", clock));
    }

    [Fact]
    public async Task A_clock_that_moves_on_gives_the_clock_time()
    {
        var clock = new SetClock(T0);
        await Allocate("crm", clock);
        clock.Now = T0.AddSeconds(30);

        Assert.Equal("crm@20261201T020030000Z", await Allocate("crm", clock));
    }

    [Fact]
    public async Task A_name_whose_catalog_was_removed_keeps_its_mark_because_the_id_file_stays()
    {
        var clock = new SetClock(T0);
        await Allocate("crm", clock);

        // 'image rm' renames the image folder away; the ID file is not part of it and is never deleted.
        Assert.DoesNotContain(_scripts, s => s.Contains("rm ", StringComparison.Ordinal));

        Assert.Equal("crm@20261201T020000001Z", await Allocate("crm", clock));
    }

    [Fact]
    public async Task Two_names_allocating_at_once_use_two_files_and_do_not_touch_each_other()
    {
        var clock = new SetClock(T0);

        var ids = await Task.WhenAll(Allocate("crm", clock), Allocate("shop", clock));

        Assert.Equal(["crm@20261201T020000000Z", "shop@20261201T020000000Z"], ids);
        Assert.Equal(2, _files.Count);
        Assert.Equal("shop@20261201T020000000Z", _files["/pool/images/.ids/shop"]);
    }

    [Fact]
    public async Task The_id_is_written_to_a_temporary_file_and_renamed()
    {
        await Allocate("crm", new SetClock(T0));

        var write = Assert.Single(_scripts, s => s.Contains("printf %s", StringComparison.Ordinal));
        Assert.Contains("> '/pool/images/.ids/crm.new'", write, StringComparison.Ordinal);
        Assert.Contains("mv -f '/pool/images/.ids/crm.new' '/pool/images/.ids/crm'", write, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shop@20261201T020000000Z")]
    [InlineData("garbage")]
    public async Task A_record_that_is_not_an_id_of_the_name_fails_with_operation_failed(string content)
    {
        _files["/pool/images/.ids/crm"] = content;

        var error = await Assert.ThrowsAsync<SqlBranchException>(() => Allocate("crm", new SetClock(T0)));

        Assert.Equal(SqlBranch.Core.Output.ErrorCodes.OperationFailed, error.Code);
        Assert.DoesNotContain(_scripts, s => s.Contains("printf %s", StringComparison.Ordinal));
    }

    [Fact]
    public void A_version_id_is_not_an_image_name_and_exits_2_before_any_docker_command()
    {
        var run = _world.Run("image", "rm", "shop@20261119T020000000Z", "--format", "json");

        Assert.Equal(2, run.Exit);
        Assert.Contains("\"invalid_name\"", run.Out, StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Calls);
    }
}
