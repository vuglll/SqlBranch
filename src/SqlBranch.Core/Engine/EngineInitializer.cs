using System.Globalization;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Engine;

/// <summary>What <c>init</c> reports.</summary>
/// <param name="Repeated">The engine already existed; nothing was created.</param>
/// <param name="Version">The engine's product version.</param>
/// <param name="Edition">The edition the engine reports.</param>
/// <param name="Port">The port published on 127.0.0.1.</param>
/// <param name="Location">The pool the engine has mounted.</param>
/// <param name="Backend"><c>plain</c> or <c>loop-xfs</c>.</param>
/// <param name="Mode">The clone mode.</param>
/// <param name="Created">One line for each thing this run created on the container host.</param>
/// <param name="Notes">Things to tell the user, such as why the pool is in copy mode.</param>
/// <param name="Container">The engine container's name.</param>
/// <param name="HostArchitecture">The container host's architecture as Docker reports it.</param>
public sealed record InitOutcome(
    bool Repeated,
    string Version,
    string Edition,
    int Port,
    PoolLocation Location,
    string Backend,
    CloneMode Mode,
    IReadOnlyList<string> Created,
    IReadOnlyList<string>? Notes = null,
    string Container = ContainerLabels.EngineName,
    string HostArchitecture = "");

/// <summary>
/// <c>sqlbranch init</c>: the pool, then the engine container, then the real
/// password. A first run that fails undoes what it created; a repeated run
/// changes nothing but may start the engine.
/// </summary>
public sealed class EngineInitializer
{
    private const string EngineMount = "/var/opt/mssql";
    private const string EngineFolder = "engine";
    private const int SqlPort = 1433;

    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly ManagedPaths _paths;
    private readonly EngineTimings _timings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _engineName;
    private readonly string _poolVolume;
    private string _hostArchitecture = string.Empty;

    public EngineInitializer(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        ManagedPaths? paths = null,
        EngineTimings? timings = null,
        Func<DateTimeOffset>? clock = null,
        string engineName = ContainerLabels.EngineName,
        string poolVolume = ContainerLabels.PoolVolume)
    {
        _engineName = engineName;
        _poolVolume = poolVolume;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _paths = paths ?? ManagedPaths.Default;
        _timings = timings ?? EngineTimings.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <param name="settings">The resolved settings.</param>
    /// <param name="givenPassword">The value of <c>SQLBRANCH_SA_PASSWORD</c>, or null.</param>
    /// <param name="defaultPoolDirectory">The pool directory when <c>pool</c> is not set.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<InitOutcome> InitializeAsync(
        Settings settings,
        string? givenPassword,
        string defaultPoolDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Nothing is created before the given password is checked and registered for redaction.
        PasswordPolicy.CheckGiven(givenPassword);
        _redactor.Register(givenPassword);

        _hostArchitecture = (await _runtime.GetInfoAsync(cancellationToken).ConfigureAwait(false)).Architecture;

        var existing = await _runtime.InspectAsync(_engineName, cancellationToken).ConfigureAwait(false);
        if (existing is { IsManaged: false })
        {
            throw new SqlBranchException(ErrorCodes.ForeignContainer,
                $"A container named '{existing.Name}' exists that SqlBranch did not create, so SqlBranch will not use or change it. " +
                "Rename or remove that container yourself, then run 'sqlbranch init' again.");
        }

        return existing is null
            ? await FirstInitAsync(settings, givenPassword, defaultPoolDirectory, cancellationToken).ConfigureAwait(false)
            : await RepeatInitAsync(settings, givenPassword, existing, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a first run created, so a failure can undo exactly that.</summary>
    private sealed class Created
    {
        public ProvisionedPool? Pool { get; set; }

        public bool EngineDir { get; set; }

        public bool BranchesDir { get; set; }

        public bool Engine { get; set; }

        public bool Credentials { get; set; }

        /// <summary>This run created something that a failure must remove.</summary>
        public bool Any =>
            Pool is { CreatedManagedPool: true } or { CreatedVolume: true } or { CreatedMarker: true } || EngineDir || BranchesDir || Engine || Credentials;
    }

    private async Task<InitOutcome> FirstInitAsync(
        Settings settings,
        string? givenPassword,
        string defaultPoolDirectory,
        CancellationToken cancellationToken)
    {
        var created = new Created();
        string? hintNote = null;
        try
        {
            var provisioner = new PoolProvisioner(_runtime, _paths, _clock, _poolVolume);
            var pool = created.Pool = await provisioner.ProvisionAsync(settings, defaultPoolDirectory, cancellationToken).ConfigureAwait(false);
            var image = settings.EngineImage;

            // An engine removed by hand leaves its credentials and system databases in the pool; they stay valid.
            var stored = await EngineCredentials.ReadAsync(_runtime, image, pool.Location, cancellationToken).ConfigureAwait(false);
            if (stored is not null && givenPassword is not null && !string.Equals(stored, givenPassword, StringComparison.Ordinal))
            {
                _redactor.Register(stored);
                throw PasswordChanged();
            }

            // The container's environment is visible in 'docker inspect', so it only ever gets a random bootstrap
            // password. An image keeps the system databases it finds and ignores the variable, so over an existing
            // pool the stored password is what works.
            var password = stored ?? givenPassword ?? PasswordGenerator.Generate();
            var bootstrap = PasswordGenerator.Generate();
            _redactor.Register(password);
            _redactor.Register(bootstrap);

            var engineDirScript =
                $"if [ -d {EngineCredentials.Directory} ]; then echo SQLBRANCH_ENGINEDIR=existing; " +
                $"else mkdir -p {EngineCredentials.Directory} && chown {DirectoryPool.EngineOwner} {EngineCredentials.Directory} && echo SQLBRANCH_ENGINEDIR=new; fi";
            var dir = await PoolWork.RunAsync(_runtime, image, pool.Location, engineDirScript, readOnly: false, cancellationToken).ConfigureAwait(false);
            if (!dir.Succeeded)
            {
                throw new SqlBranchException(ErrorCodes.OperationFailed, $"Could not create the engine folder in {pool.Location} (exit code {dir.ExitCode}).");
            }

            created.EngineDir = dir.StdOut.Contains("SQLBRANCH_ENGINEDIR=new", StringComparison.Ordinal);

            // Design D14: the 8 KiB copy-on-write hint only means something where clones share blocks.
            if (pool.Mode == CloneMode.Thin)
            {
                var hint = await CowHint.SetAsync(_runtime, pool.Location, cancellationToken).ConfigureAwait(false);
                created.BranchesDir = hint.CreatedFolder;
                if (hint.Result == CowHintResult.Unsupported)
                {
                    hintNote = $"Could not set the {CowHint.Size} copy-on-write hint on {CowHint.BranchesPath}. " +
                        "Branches work, but heavy scattered writes may use more space.";
                }
            }

            created.Engine = true;
            var run = await _runtime.RunAsync(EngineSpec(_engineName, settings, pool, bootstrap), cancellationToken).ConfigureAwait(false);
            if (!run.Succeeded)
            {
                throw new SqlBranchException(ErrorCodes.OperationFailed,
                    $"Could not start the engine container (exit code {run.ExitCode}): {FirstLine(run.StdErr)}");
            }

            var endpoint = EngineEndpoint.Loopback(settings.Port);
            var readiness = new EngineReadiness(_runtime, _connector, _redactor, _timings, _engineName);
            string worked;
            try
            {
                worked = await readiness.WaitAnyAsync(endpoint, stored is null ? [bootstrap] : [stored, bootstrap], cancellationToken).ConfigureAwait(false);
            }
            catch (SqlBranchException ex) when (RefusedEdition(ex, settings))
            {
                // SQL Server 2022 and older exit at start with "The provided PID [..] is invalid", so the
                // version check below never runs for them. The cause is the configuration, so it exits 2.
                throw new ConfigException(
                    $"edition {SettingNames.Of(settings.EngineEdition)} needs SQL Server {EditionRules.MinimumYearForNewEditions} or later, " +
                    $"but the engine image {image} refused it at start.",
                    settings.Origins.GetValueOrDefault(SettingKeys.EngineEdition), SettingKeys.EngineEdition);
            }

            if (stored is null)
            {
                created.Credentials = true;
                await EngineCredentials.WriteAsync(_runtime, image, pool.Location, password, cancellationToken).ConfigureAwait(false);
                await ChangePasswordAsync(endpoint, bootstrap, password, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.Equals(worked, stored, StringComparison.Ordinal))
            {
                // The system databases did not carry the stored password but accepted the bootstrap one: restore it.
                await ChangePasswordAsync(endpoint, worked, stored, cancellationToken).ConfigureAwait(false);
            }

            var (version, edition) = await EngineStarter.ReadVersionAsync(_connector, endpoint, password, settings.EngineEdition, image, cancellationToken).ConfigureAwait(false);
            return new InitOutcome(false, version, edition, settings.Port, pool.Location, pool.Backend, pool.Mode, CreatedLines(pool, created), NotesOf(pool, hintNote), _engineName, _hostArchitecture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var left = await UndoAsync(settings, created).ConfigureAwait(false);
            throw Describe(ex, created.Any, left);
        }
        catch (OperationCanceledException)
        {
            await UndoAsync(settings, created).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<InitOutcome> RepeatInitAsync(
        Settings settings,
        string? givenPassword,
        ContainerInfo engine,
        CancellationToken cancellationToken)
    {
        CheckUnchanged(settings, engine, _poolVolume);

        var backend = engine.Labels.GetValueOrDefault(ContainerLabels.Backend) ?? PoolProvisioner.BackendPlain;
        var location = PoolFinder.LocationOf(engine, _paths, _poolVolume);
        var found = new FoundPool(location, backend, PoolSource.EngineLabels, engine);

        var starter = new EngineStarter(_runtime, _connector, _redactor, _paths, _timings, _engineName);
        var access = await starter.StartAsync(engine, found, settings.Port, givenPassword, PasswordChanged, cancellationToken).ConfigureAwait(false);

        var (version, edition) = await EngineStarter.ReadVersionAsync(_connector, access.Endpoint, access.Password, settings.EngineEdition, engine.Image, cancellationToken).ConfigureAwait(false);
        var mode = await CloneProbe.ProbeInEngineAsync(_runtime, cancellationToken, _engineName).ConfigureAwait(false);
        return new InitOutcome(true, version, edition, settings.Port, location, backend, mode, [], null, _engineName, _hostArchitecture);
    }

    /// <summary>Exit 2 when a setting that is fixed at the first init has changed.</summary>
    private static void CheckUnchanged(Settings settings, ContainerInfo engine, string poolVolume)
    {
        if (!string.Equals(engine.Image, settings.EngineImage, StringComparison.Ordinal))
        {
            throw Fixed(settings, SettingKeys.EngineImage, engine.Image, settings.EngineImage);
        }

        var edition = engine.Labels.GetValueOrDefault(ContainerLabels.Edition);
        if (edition is not null && !string.Equals(edition, SettingNames.Of(settings.EngineEdition), StringComparison.Ordinal))
        {
            throw Fixed(settings, SettingKeys.EngineEdition, edition, SettingNames.Of(settings.EngineEdition));
        }

        var port = engine.Labels.GetValueOrDefault(ContainerLabels.Port);
        var wanted = settings.Port.ToString(CultureInfo.InvariantCulture);
        if (port is not null && !string.Equals(port, wanted, StringComparison.Ordinal))
        {
            throw Fixed(settings, SettingKeys.Port, port, wanted);
        }

        // Only a directory pool has a location the user chose.
        var backend = engine.Labels.GetValueOrDefault(ContainerLabels.Backend);
        var pool = engine.Labels.GetValueOrDefault(ContainerLabels.Pool);
        if (backend == PoolProvisioner.BackendPlain && pool is not null && pool != poolVolume &&
            settings.Pool is not null && !string.Equals(pool, settings.Pool, StringComparison.Ordinal))
        {
            throw Fixed(settings, SettingKeys.Pool, pool, settings.Pool);
        }
    }

    private static ConfigException Fixed(Settings settings, string key, string current, string wanted) =>
        new($"{key} is fixed when the engine is created. The engine uses '{current}', but {key} is now '{wanted}'. " +
            $"Set {key} back, or run 'sqlbranch teardown --force' and 'sqlbranch init' again.",
            settings.Origins.GetValueOrDefault(key), key);

    private static ConfigException PasswordChanged() =>
        new("the password is fixed when the engine is created, and this one differs from it. " +
            "Unset the variable, or run 'sqlbranch teardown --force' and 'sqlbranch init' again.",
            SettingKeys.SaPasswordVariable, null);

    private static ContainerSpec EngineSpec(string name, Settings settings, ProvisionedPool pool, string bootstrap) => new()
    {
        Name = name,
        Image = settings.EngineImage,
        Platform = ImagePlatform.For(settings.EngineImage),
        Detach = true,
        Labels = new Dictionary<string, string>
        {
            [ContainerLabels.Backend] = pool.Backend,
            [ContainerLabels.Pool] = pool.Location.Source,
            [ContainerLabels.Edition] = SettingNames.Of(settings.EngineEdition),
            [ContainerLabels.Port] = settings.Port.ToString(CultureInfo.InvariantCulture),
        },
        Env = new Dictionary<string, string>
        {
            ["ACCEPT_EULA"] = "Y",
            ["MSSQL_PID"] = EditionRules.ProductId(settings.EngineEdition),
            ["MSSQL_SA_PASSWORD"] = bootstrap,
        },
        Ports = [new PortSpec("127.0.0.1", settings.Port, SqlPort)],
        Mounts =
        [
            pool.Location.ToMount(),
            new MountSpec(pool.Location.Kind == PoolKind.Volume ? MountKind.Volume : MountKind.Bind, pool.Location.Source, EngineMount, SubPath: EngineFolder),
        ],
    };

    private static bool RefusedEdition(SqlBranchException ex, Settings settings) =>
        ex.Code == ErrorCodes.EngineNotReady &&
        settings.EngineEdition != EngineEdition.Developer &&
        EditionRules.ReleaseYearFromImage(settings.EngineImage) is null &&
        ex.Message.Contains("PID", StringComparison.Ordinal) &&
        ex.Message.Contains("is invalid", StringComparison.Ordinal);

    private async Task ChangePasswordAsync(EngineEndpoint endpoint, string bootstrap, string password, CancellationToken cancellationToken)
    {
        await using var session = await _connector.ConnectAsync(endpoint, bootstrap, cancellationToken).ConfigureAwait(false);
        var literal = password.Replace("'", "''", StringComparison.Ordinal);
        await session.ExecuteAsync($"ALTER LOGIN sa WITH PASSWORD = N'{literal}'", cancellationToken).ConfigureAwait(false);
    }

    private List<string> CreatedLines(ProvisionedPool pool, Created created)
    {
        var lines = new List<string>(pool.Lines.Where(l => !IsNotice(l)));
        if (pool.CreatedVolume)
        {
            lines.Add($"Pool volume: {_poolVolume}");
        }
        else if (!pool.CreatedManagedPool)
        {
            lines.Add($"Pool: {pool.Location}");
        }

        if (created.EngineDir || created.Engine)
        {
            lines.Add($"Engine container: {_engineName}");
        }

        return lines;
    }

    private static bool IsNotice(string line) => line.StartsWith("Falling back", StringComparison.Ordinal);

    private static List<string> NotesOf(ProvisionedPool pool, string? hintNote) =>
        [.. pool.Lines.Where(IsNotice), .. hintNote is null ? [] : new[] { hintNote }];

    /// <summary>
    /// Removes what the run created: the engine, the credentials and engine
    /// folder, the marker, then the mount, the pool file and the volume. The
    /// credentials live inside the pool, so they go while it is still mounted.
    /// Returns what could not be removed.
    /// </summary>
    private async Task<IReadOnlyList<string>> UndoAsync(Settings settings, Created created)
    {
        var left = new List<string>();
        var image = settings.EngineImage;
        var pool = created.Pool;

        async Task Step(string what, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Undo continues past a failed step and reports it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                left.Add($"{what} ({ex.Message})");
            }
        }

        if (created.Engine)
        {
            await Step($"container {_engineName}", () => _runtime.RemoveAsync(_engineName, CancellationToken.None)).ConfigureAwait(false);
        }

        if (pool is { CreatedManagedPool: false, CreatedVolume: false } || pool is null)
        {
            // The pool existed before this run, so only what this run put into it is removed.
            if (pool is not null)
            {
                var removals = new List<string>();
                if (created.Credentials)
                {
                    removals.Add($"rm -f {EngineCredentials.Path}");
                }

                if (created.EngineDir)
                {
                    removals.Add($"rm -rf {EngineCredentials.Directory}");
                }

                if (created.BranchesDir)
                {
                    removals.Add($"rmdir {CowHint.BranchesPath} 2>/dev/null || true");
                }

                if (pool.CreatedMarker)
                {
                    removals.Add($"rm -f {PoolMarker.Path}");
                }

                if (removals.Count > 0)
                {
                    await Step($"files in {pool.Location}", async () =>
                    {
                        var result = await PoolWork.RunAsync(_runtime, image, pool.Location, string.Join("; ", removals), readOnly: false, CancellationToken.None).ConfigureAwait(false);
                        if (!result.Succeeded)
                        {
                            throw new SqlBranchException($"exit code {result.ExitCode}");
                        }
                    }).ConfigureAwait(false);
                }
            }

            return left;
        }

        if (pool.CreatedManagedPool)
        {
            var managed = new ManagedPool(_runtime, _paths);
            await Step($"{_paths.MountPoint} and {_paths.PoolFile}", () => managed.RemoveAsync(CancellationToken.None)).ConfigureAwait(false);
            if (await StillThereAsync(managed).ConfigureAwait(false))
            {
                left.Add($"{_paths.MountPoint} and {_paths.PoolFile}");
            }
        }

        if (pool.CreatedVolume)
        {
            await Step($"volume {_poolVolume}", () => _runtime.RemoveVolumeAsync(_poolVolume, CancellationToken.None)).ConfigureAwait(false);
        }

        return left;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    /// <summary>True when the pool file or its mount survived the undo; the undo itself ignores a failed unmount.</summary>
    private static async Task<bool> StillThereAsync(ManagedPool managed)
    {
        try
        {
            return await managed.FileExistsAsync(CancellationToken.None).ConfigureAwait(false) ||
                await managed.IsMountedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlBranchException or ManagedPoolException)
        {
            return true;
        }
    }

    /// <summary>
    /// Keeps the cause's exit code when it says the environment or the
    /// configuration is at fault, and turns every other failure into <c>init_failed</c>.
    /// </summary>
    private static Exception Describe(Exception cause, bool createdAnything, IReadOnlyList<string> left)
    {
        var suffix = left.Count > 0
            ? $" Could not remove: {string.Join("; ", left)}. Run 'sqlbranch teardown --force' to clean up."
            : createdAnything ? " Everything this run created was removed." : string.Empty;

        return cause switch
        {
            ConfigException config => new ConfigException(config.Message + suffix, config),
            SqlBranchException { Code.ExitCode: not ExitCode.OperationFailed } coded => new SqlBranchException(coded.Code, coded.Message + suffix),
            _ => new SqlBranchException(ErrorCodes.InitFailed, $"init failed: {cause.Message}{suffix}"),
        };
    }
}
