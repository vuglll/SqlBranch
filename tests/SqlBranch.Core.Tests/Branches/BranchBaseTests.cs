using System.Text.Json;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Branches;

/// <summary>Task 3.6: the branch base on a format-2 pool (design D5) for reset and list.</summary>
public sealed class BranchBaseTests
{
    private const string Old = "shop@20261201T090000000Z";
    private const string New = "shop@20261203T020000000Z";

    private readonly BranchWorld _w = new();

    private (int Exit, JsonElement Json, string Out) Run(params string[] args)
    {
        var run = _w.Run([.. args, "--format", "json"]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement, run.Out);
    }

    private JsonElement Row(string name) =>
        Run("branch", "list").Json.GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("name").GetString() == name);

    private bool Changed() => _w.Ran("DROP DATABASE") || _w.Ran("SINGLE_USER") || _w.Ran("/next") || _w.Ran("FOR ATTACH") || _w.Ran("base64 -d");

    [Fact]
    public void Reset_keeps_the_base_and_reports_it()
    {
        _w.UseFormat2(Old);
        _w.AddBranch("dev1", imageVersion: Old, baseKnown: true);

        var run = Run("branch", "reset", "dev1");

        Assert.Equal(0, run.Exit);
        Assert.Equal(Old, run.Json.GetProperty("branch").GetProperty("imageVersion").GetString());
        Assert.Equal(Old, _w.RecordOf("dev1").ImageVersion);
        Assert.True(_w.RecordOf("dev1").BaseKnown);
        Assert.True(_w.Ran("'/pool/images/shop/data/Shop.mdf'"));
    }

    [Fact]
    public void Reset_of_a_record_without_base_fields_attaches_the_current_version_and_records_it()
    {
        _w.UseFormat2(New);
        _w.AddBranch("dev1");

        var run = Run("branch", "reset", "dev1");

        Assert.Equal(0, run.Exit);
        Assert.Equal(New, _w.RecordOf("dev1").ImageVersion);
        Assert.True(_w.RecordOf("dev1").BaseKnown);
    }

    [Fact]
    public void Reset_whose_base_image_has_an_unreadable_catalog_refuses_and_changes_nothing()
    {
        _w.UseFormat2(Old);
        _w.AddBranch("dev1", imageVersion: Old, baseKnown: true);
        _w.Catalog = "{not json";

        var run = Run("branch", "reset", "dev1");

        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("shop", run.Json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(Changed());
        Assert.Equal(Old, _w.RecordOf("dev1").ImageVersion);
    }

    [Theory]
    [InlineData("removed-and-reused")]
    [InlineData("removed-before-upgrade")]
    [InlineData("unknown")]
    public void Reset_of_a_removed_or_unknown_base_is_image_not_found_before_anything_changes(string kind)
    {
        _w.UseFormat2(New);
        switch (kind)
        {
            case "removed-and-reused": _w.AddBranch("dev1", imageVersion: Old, baseKnown: true); break;
            case "removed-before-upgrade": _w.AddBranch("dev1", imageVersion: null, baseKnown: true); break;
            default: _w.AddBranch("dev1", imageVersion: null, baseKnown: false); break;
        }

        var before = _w.Records["dev1"];
        var run = Run("branch", "reset", "dev1");

        Assert.Equal(1, run.Exit);
        var error = run.Json.GetProperty("error");
        Assert.Equal("image_not_found", error.GetProperty("code").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("dev1", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch branch rm dev1", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch branch create", message, StringComparison.Ordinal);
        Assert.Contains("no longer exists", message, StringComparison.Ordinal);
        Assert.False(Changed(), string.Join('\n', _w.Timeline));
        Assert.Equal(before, _w.Records["dev1"]);
    }

    [Fact]
    public void A_broken_branch_with_a_removed_base_is_refused_the_same_way()
    {
        _w.UseFormat2(New);
        _w.AddBranchWithoutDatabase("dev1", state: "broken");
        _w.Records["dev1"] = (_w.RecordOf("dev1") with { ImageVersion = Old, BaseKnown = true }).ToJson();

        var run = Run("branch", "reset", "dev1");

        Assert.Equal(1, run.Exit);
        Assert.Equal("image_not_found", run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Changed());
    }

    [Fact]
    public void List_shows_a_pinned_base_with_its_version_and_not_removed()
    {
        _w.UseFormat2(Old);
        _w.AddBranch("dev1", imageVersion: Old, baseKnown: true);

        var row = Row("dev1");

        Assert.Equal(Old, row.GetProperty("imageVersion").GetString());
        Assert.False(row.GetProperty("imageRemoved").GetBoolean());
    }

    [Fact]
    public void List_marks_a_base_removed_also_after_the_name_was_used_again()
    {
        _w.UseFormat2(New);
        _w.AddBranch("dev1", imageVersion: Old, baseKnown: true);

        var row = Row("dev1");

        Assert.Equal(Old, row.GetProperty("imageVersion").GetString());
        Assert.True(row.GetProperty("imageRemoved").GetBoolean());
        Assert.Equal("online", row.GetProperty("state").GetString());
    }

    [Fact]
    public void List_marks_a_base_removed_when_the_name_is_gone()
    {
        _w.UseFormat2(null);
        _w.Catalog = null;
        _w.ImageExists = false;
        _w.AddBranch("dev1", imageVersion: Old, baseKnown: true);

        Assert.True(Row("dev1").GetProperty("imageRemoved").GetBoolean());
    }

    [Fact]
    public void List_of_a_base_removed_before_the_upgrade_is_null_and_removed_also_after_the_name_was_used_again()
    {
        _w.UseFormat2(New);
        _w.AddBranch("pr-9", imageVersion: null, baseKnown: true);

        var row = Row("pr-9");

        Assert.Equal(JsonValueKind.Null, row.GetProperty("imageVersion").ValueKind);
        Assert.True(row.GetProperty("imageRemoved").GetBoolean());
    }

    [Fact]
    public void List_of_an_unknown_base_is_null_and_not_removed_while_the_name_exists()
    {
        _w.UseFormat2(New);
        _w.AddBranch("old1", imageVersion: null, baseKnown: false);

        var row = Row("old1");

        Assert.Equal(JsonValueKind.Null, row.GetProperty("imageVersion").ValueKind);
        Assert.False(row.GetProperty("imageRemoved").GetBoolean());
    }

    [Fact]
    public void List_of_a_legacy_record_on_a_format_2_pool_is_null_and_removed_by_name()
    {
        _w.UseFormat2(New);
        _w.AddBranch("dev1");

        var row = Row("dev1");
        Assert.Equal(JsonValueKind.Null, row.GetProperty("imageVersion").ValueKind);
        Assert.False(row.GetProperty("imageRemoved").GetBoolean());

        _w.Catalog = null;
        _w.ImageExists = false;
        Assert.True(Row("dev1").GetProperty("imageRemoved").GetBoolean());
    }

    [Fact]
    public void On_a_format_1_pool_list_and_reset_report_a_null_version()
    {
        _w.AddBranch("dev1");

        var row = Row("dev1");
        Assert.Equal(JsonValueKind.Null, row.GetProperty("imageVersion").ValueKind);
        Assert.False(row.GetProperty("imageRemoved").GetBoolean());

        var run = Run("branch", "reset", "dev1");
        Assert.Equal(0, run.Exit);
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("branch").GetProperty("imageVersion").ValueKind);
        Assert.DoesNotContain("baseKnown", _w.Records["dev1"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_output_shows_the_version()
    {
        _w.UseFormat2(Old);
        _w.AddBranch("dev1", imageVersion: Old, baseKnown: true);

        Assert.Contains($"Image version: {Old}", _w.Run("branch", "reset", "dev1").Out, StringComparison.Ordinal);
        Assert.Contains(Old, _w.Run("branch", "list").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_with_a_known_base_and_no_version_writes_an_explicit_null()
    {
        _w.AddBranch("dev1", imageVersion: null, baseKnown: true);

        Assert.Contains("\"imageVersion\": null", _w.Records["dev1"], StringComparison.Ordinal);
        Assert.Contains("\"baseKnown\": true", _w.Records["dev1"], StringComparison.Ordinal);
    }
}
