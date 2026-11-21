using SqlBranch.Core.Configuration;

namespace SqlBranch.Core.Tests.Configuration;

public sealed class SettingsLoaderTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Settings Load(
        Dictionary<string, string>? flags = null,
        Dictionary<string, string>? env = null,
        string? workingDirectory = null,
        string? configFile = null) =>
        SettingsLoader.Load(new SettingsSources
        {
            Flags = flags ?? [],
            Environment = env ?? [],
            WorkingDirectory = workingDirectory ?? _tree.Dir("repo"),
            ConfigFile = configFile,
            HomeDirectory = _tree.Dir("home"),
        });

    private void RepoFile(string yaml) => _tree.File("repo/.sqlbranch.yml", yaml);

    private void UserFile(string yaml) => _tree.File("home/.sqlbranch/config.yml", yaml);

    // 3.1 defaults and precedence

    [Fact]
    public void No_configuration_gives_the_documented_defaults()
    {
        var s = Load();

        Assert.Equal(14330, s.Port);
        Assert.Equal("mcr.microsoft.com/mssql/server:2022-latest", s.EngineImage);
        Assert.Equal(EngineEdition.Developer, s.EngineEdition);
        Assert.Equal(PoolBackend.Auto, s.PoolBackend);
        Assert.Equal(60_000_000_000, s.PoolSize.Bytes);
        Assert.False(s.RequireThin);
        Assert.Null(s.DefaultImage);
        Assert.Null(s.Pool);
    }

    [Fact]
    public void Flag_beats_environment()
    {
        var s = Load(flags: new() { ["port"] = "16000" }, env: new() { ["SQLBRANCH_PORT"] = "15000" });
        Assert.Equal(16000, s.Port);
        Assert.Equal("--port", s.Origins["port"]);
    }

    [Fact]
    public void Flag_beats_file()
    {
        RepoFile("port: 15000");
        Assert.Equal(16000, Load(flags: new() { ["port"] = "16000" }).Port);
    }

    [Fact]
    public void Environment_beats_repository_file()
    {
        RepoFile("requireThin: false");
        var s = Load(env: new() { ["SQLBRANCH_REQUIRE_THIN"] = "true" });
        Assert.True(s.RequireThin);
        Assert.Equal("SQLBRANCH_REQUIRE_THIN", s.Origins["requireThin"]);
    }

    [Fact]
    public void Repository_file_beats_user_file()
    {
        RepoFile("defaultImage: shop");
        UserFile("defaultImage: crm");
        Assert.Equal("shop", Load().DefaultImage);
    }

    [Fact]
    public void User_file_beats_default()
    {
        UserFile("poolBackend: plain");
        var s = Load();
        Assert.Equal(PoolBackend.Plain, s.PoolBackend);
        Assert.EndsWith("config.yml", s.Origins["poolBackend"], StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_from_different_layers_combine()
    {
        RepoFile("defaultImage: shop");
        UserFile("port: 15500");
        var s = Load(env: new() { ["SQLBRANCH_POOL_BACKEND"] = "loop-xfs" });
        Assert.Equal(("shop", 15500, PoolBackend.LoopXfs), (s.DefaultImage, s.Port, s.PoolBackend));
    }

    [Fact]
    public void Empty_environment_variable_is_ignored()
    {
        RepoFile("port: 15000");
        Assert.Equal(15000, Load(env: new() { ["SQLBRANCH_PORT"] = "" }).Port);
    }

    // 3.2 discovery and --config

    [Fact]
    public void Repository_file_is_found_from_a_subfolder()
    {
        RepoFile("port: 15000");
        Assert.Equal(15000, Load(workingDirectory: _tree.Dir("repo/src/api")).Port);
    }

    [Fact]
    public void Nearest_file_wins_over_one_further_up()
    {
        RepoFile("port: 15000");
        _tree.File("repo/src/.sqlbranch.yml", "port: 15001");
        Assert.Equal(15001, Load(workingDirectory: _tree.Dir("repo/src/api")).Port);
    }

    [Fact]
    public void Config_option_replaces_the_repository_file()
    {
        RepoFile("port: 15000\ndefaultImage: shop");
        var other = _tree.File("elsewhere/team.yml", "port: 15002");

        var s = Load(configFile: other);

        Assert.Equal(15002, s.Port);
        Assert.Null(s.DefaultImage);
    }

    [Fact]
    public void Config_option_relative_path_is_resolved_from_the_working_directory()
    {
        _tree.File("repo/ci/sb.yml", "port: 15003");
        Assert.Equal(15003, Load(configFile: "ci/sb.yml").Port);
    }

    [Fact]
    public void Missing_config_option_file_is_a_config_error()
    {
        var ex = Assert.Throws<ConfigException>(() => Load(configFile: "nope.yml"));
        Assert.Contains("nope.yml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_file_is_allowed()
    {
        RepoFile(string.Empty);
        Assert.Equal(14330, Load().Port);
    }

    // 3.3 rejected files

    [Fact]
    public void Misspelled_key_names_the_key_and_the_file()
    {
        RepoFile("requireThinn: true");
        var ex = Assert.Throws<ConfigException>(() => Load());
        Assert.Equal("requireThinn", ex.Key);
        Assert.Contains(".sqlbranch.yml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("requireThinn", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("port: abc", "port")]
    [InlineData("port: 70000", "port")]
    [InlineData("requireThin: 5", "requireThin")]
    [InlineData("port: [1, 2]", "port")]
    [InlineData("defaultImage: \"\"", "defaultImage")]
    public void Wrong_type_names_the_key_and_the_file(string yaml, string key)
    {
        RepoFile(yaml);
        var ex = Assert.Throws<ConfigException>(() => Load());
        Assert.Equal(key, ex.Key);
        Assert.Contains(".sqlbranch.yml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_type_from_the_environment_names_the_variable()
    {
        var ex = Assert.Throws<ConfigException>(() => Load(env: new() { ["SQLBRANCH_PORT"] = "abc" }));
        Assert.Contains("SQLBRANCH_PORT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Supported_version_is_accepted_and_others_are_not()
    {
        RepoFile("version: 1\nport: 15000");
        Assert.Equal(15000, Load().Port);

        RepoFile("version: 2");
        var ex = Assert.Throws<ConfigException>(() => Load());
        Assert.Equal("version", ex.Key);
    }

    [Theory]
    [InlineData("password: x")]
    [InlineData("saPassword: x")]
    public void Password_key_is_refused(string yaml)
    {
        RepoFile(yaml);
        var ex = Assert.Throws<ConfigException>(() => Load());
        Assert.Contains("passwords are not read from config files", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_backend_lists_the_allowed_values()
    {
        RepoFile("poolBackend: zfs");
        var ex = Assert.Throws<ConfigException>(() => Load());
        Assert.Contains("auto, loop-xfs, plain", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_yaml_is_a_config_error()
    {
        RepoFile("port: [unclosed");
        Assert.Throws<ConfigException>(() => Load());
    }

    [Fact]
    public void A_list_at_the_top_is_a_config_error()
    {
        RepoFile("- port\n- 15000");
        Assert.Throws<ConfigException>(() => Load());
    }

    // 3.4 editions

    [Fact]
    public void Standard_developer_on_a_2025_image_is_accepted()
    {
        RepoFile("engineImage: mcr.microsoft.com/mssql/server:2025-latest\nengineEdition: standard-developer");
        Assert.Equal(EngineEdition.StandardDeveloper, Load().EngineEdition);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("enterprise")]
    [InlineData("express")]
    public void Production_editions_are_refused(string edition)
    {
        RepoFile($"engineEdition: {edition}");
        var ex = Assert.Throws<ConfigException>(() => Load());
        Assert.Contains("only development editions are supported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_value_in_a_lower_layer_is_still_refused()
    {
        RepoFile("poolBackend: zfs");
        var ex = Assert.Throws<ConfigException>(() => Load(env: new() { ["SQLBRANCH_POOL_BACKEND"] = "plain" }));
        Assert.Contains("poolBackend", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".sqlbranch.yml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_scalar_key_is_refused_with_a_clear_message()
    {
        RepoFile("? [a, b]\n: 1");
        var ex = Assert.Throws<ConfigException>(() => Load());
        Assert.Contains("setting names must be plain text", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_edition_error_names_where_the_edition_was_set()
    {
        var ex = Assert.Throws<ConfigException>(() => Load(env: new() { ["SQLBRANCH_ENGINE_EDITION"] = "standard-developer" }));
        Assert.Equal("SQLBRANCH_ENGINE_EDITION", ex.Origin);
        Assert.Contains("SQLBRANCH_ENGINE_EDITION", ex.Message, StringComparison.Ordinal);
        Assert.Contains("engineEdition", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_developer_on_the_default_2022_image_is_refused()
    {
        RepoFile("engineEdition: standard-developer");
        var ex = Assert.Throws<ConfigException>(() => Load());
        Assert.Contains("needs SQL Server 2025 or later", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2022-latest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tag_without_a_year_is_left_for_the_engine_check()
    {
        RepoFile("engineImage: registry.local/mssql:team\nengineEdition: enterprise-developer");
        Assert.Equal(EngineEdition.EnterpriseDeveloper, Load().EngineEdition);
    }

    // 3.5 poolSize

    [Fact]
    public void Pool_size_is_read()
    {
        RepoFile("poolSize: 100GB");
        Assert.Equal(100_000_000_000, Load().PoolSize.Bytes);
    }

    [Fact]
    public void Pool_size_that_is_not_a_size_is_refused()
    {
        RepoFile("poolSize: big");
        var ex = Assert.Throws<ConfigException>(() => Load());
        Assert.Equal("poolSize", ex.Key);
    }
}
