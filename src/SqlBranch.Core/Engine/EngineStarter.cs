using System.Globalization;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Engine;

/// <summary>A running engine that a command can log in to.</summary>
/// <param name="Endpoint">Where it listens on the container host.</param>
/// <param name="Password">The <c>sa</c> password from the pool. Registered for redaction.</param>
public sealed record EngineAccess(EngineEndpoint Endpoint, string Password);

/// <summary>
/// Gets an existing engine container running and ready: remount a managed pool
/// that went away with a restart, read the stored password, start the
/// container, and wait for a login. <c>init</c> on a repeat and
/// <c>engine start</c> both use it.
/// </summary>
public sealed class EngineStarter
{
    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly ManagedPaths _paths;
    private readonly EngineTimings _timings;
    private readonly string _engineName;

    public EngineStarter(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        ManagedPaths? paths = null,
        EngineTimings? timings = null,
        string engineName = ContainerLabels.EngineName)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _paths = paths ?? ManagedPaths.Default;
        _timings = timings ?? EngineTimings.Default;
        _engineName = engineName;
    }

    /// <summary>The port the engine was created with: its label, else <paramref name="fallback"/>.</summary>
    public static int PortOf(ContainerInfo engine, int fallback)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return int.TryParse(engine.Labels.GetValueOrDefault(ContainerLabels.Port), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? port
            : fallback;
    }

    /// <summary>Exit 3 <c>pool_not_mounted</c> when a managed pool is not mounted and cannot be mounted again.</summary>
    public async Task EnsureMountedAsync(string backend, CancellationToken cancellationToken)
    {
        if (backend != PoolProvisioner.BackendLoopXfs)
        {
            return;
        }

        var managed = new ManagedPool(_runtime, _paths);
        if (await managed.IsMountedAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await managed.MountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ManagedPoolException ex)
        {
            throw new SqlBranchException(ErrorCodes.PoolNotMounted,
                $"The managed pool is not mounted and could not be remounted: {ex.Message}");
        }
    }

    /// <summary>
    /// Remounts the managed pool, starts the engine when it is stopped and waits
    /// up to 120 seconds for a login with the stored password. Nothing is
    /// started when the remount fails.
    /// </summary>
    /// <param name="engine">The engine container, as inspected.</param>
    /// <param name="found">The pool the engine uses.</param>
    /// <param name="fallbackPort">The port when the container carries no port label.</param>
    /// <param name="givenPassword">A password the user gave, which must match the stored one; null to skip the check.</param>
    /// <param name="onMismatch">Builds the exception for a given password that differs from the stored one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<EngineAccess> StartAsync(
        ContainerInfo engine,
        FoundPool found,
        int fallbackPort,
        string? givenPassword,
        Func<Exception>? onMismatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(found);

        await EnsureMountedAsync(found.Backend, cancellationToken).ConfigureAwait(false);

        var stored = await EngineCredentials.ReadAsync(_runtime, engine.Image, found.Location, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The engine credentials are missing from {found.Location}. Run 'sqlbranch teardown --force' and 'sqlbranch init' to start again.");
        _redactor.Register(stored);
        if (givenPassword is not null && !string.Equals(stored, givenPassword, StringComparison.Ordinal))
        {
            throw onMismatch?.Invoke() ?? new SqlBranchException(ErrorCodes.ConfigError, "The password differs from the stored one.");
        }

        if (!engine.Running)
        {
            await _runtime.StartAsync(_engineName, cancellationToken).ConfigureAwait(false);
        }

        var endpoint = EngineEndpoint.Loopback(PortOf(engine, fallbackPort));
        await new EngineReadiness(_runtime, _connector, _redactor, _timings, _engineName)
            .WaitAsync(endpoint, stored, cancellationToken).ConfigureAwait(false);
        return new EngineAccess(endpoint, stored);
    }

    /// <summary>Reads the product version and the edition from a running engine.</summary>
    public static async Task<(string Version, string Edition)> ReadVersionAsync(
        IEngineConnector connector,
        EngineEndpoint endpoint,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        await using var session = await connector.ConnectAsync(endpoint, password, cancellationToken).ConfigureAwait(false);
        return await ReadVersionAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the version and edition, and rejects a newer edition on an engine older than
    /// SQL Server 2025 when the image tag did not say so (exit 2, design D8).
    /// </summary>
    public static async Task<(string Version, string Edition)> ReadVersionAsync(
        IEngineConnector connector,
        EngineEndpoint endpoint,
        string password,
        EngineEdition wanted,
        string image,
        CancellationToken cancellationToken)
    {
        var result = await ReadVersionAsync(connector, endpoint, password, cancellationToken).ConfigureAwait(false);
        EditionRules.CheckRunningVersion(wanted, image, result.Version);
        return result;
    }

    /// <summary>Reads the product version and the edition over an open session.</summary>
    public static async Task<(string Version, string Edition)> ReadVersionAsync(IEngineSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var text = Convert.ToString(
            await session.ScalarAsync(
                "SELECT CONCAT(CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)), '|', CAST(SERVERPROPERTY('Edition') AS nvarchar(100)))",
                cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) ?? string.Empty;
        var parts = text.Split('|', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }
}
