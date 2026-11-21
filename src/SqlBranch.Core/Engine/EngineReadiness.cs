using System.Diagnostics;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Engine;

/// <summary>How long to wait for the engine. The product uses the defaults; tests shorten them.</summary>
public sealed record EngineTimings
{
    /// <summary>The wait the spec fixes at 120 seconds.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public static EngineTimings Default { get; } = new();
}

/// <summary>Waits until the engine accepts a login.</summary>
public sealed class EngineReadiness
{
    public const int LogLines = 20;

    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly EngineTimings _timings;
    private readonly string _engineName;

    public EngineReadiness(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        EngineTimings? timings = null,
        string engineName = ContainerLabels.EngineName)
    {
        _engineName = engineName;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _timings = timings ?? EngineTimings.Default;
    }

    /// <summary>
    /// Tries to log in as <c>sa</c> until it works. Exits with
    /// <c>engine_not_ready</c> and the last log lines, secrets removed, when the
    /// time is up or the container has stopped.
    /// </summary>
    public async Task WaitAsync(EngineEndpoint endpoint, string password, CancellationToken cancellationToken) =>
        await WaitAnyAsync(endpoint, [password], cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Like <see cref="WaitAsync"/>, but each round tries the passwords in order and the result is the one that
    /// worked. Used when an engine is recreated over an existing pool: the stored password is tried first, and the
    /// new random one is the fallback.
    /// </summary>
    public async Task<string> WaitAnyAsync(EngineEndpoint endpoint, IReadOnlyList<string> passwords, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passwords);
        var clock = Stopwatch.StartNew();
        string? stopped = null;

        while (true)
        {
            var info = await _runtime.InspectAsync(_engineName, cancellationToken).ConfigureAwait(false);
            if (info is null || !info.Running)
            {
                stopped = "The engine container stopped before it accepted connections.";
                break;
            }

            foreach (var password in passwords)
            {
                if (await TryLoginAsync(endpoint, password, _timings.ReadyTimeout - clock.Elapsed, cancellationToken).ConfigureAwait(false))
                {
                    return password;
                }
            }

            var remaining = _timings.ReadyTimeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < _timings.PollInterval ? remaining : _timings.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        var log = await ReadLogAsync().ConfigureAwait(false);
        var seconds = (int)Math.Round(_timings.ReadyTimeout.TotalSeconds);
        throw new SqlBranchException(ErrorCodes.EngineNotReady,
            $"{stopped ?? $"The engine did not accept connections within {seconds} seconds."}" +
            (log.Length == 0 ? string.Empty : $"{Environment.NewLine}Last lines of the engine log:{Environment.NewLine}{log}"));
    }

    private async Task<bool> TryLoginAsync(EngineEndpoint endpoint, string password, TimeSpan remaining, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(remaining > TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
        try
        {
            await using var session = await _connector.ConnectAsync(endpoint, password, attempt.Token).ConfigureAwait(false);
            await session.ScalarAsync("SELECT 1", attempt.Token).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Any failure to connect means "not ready yet"; the wait decides when to give up.
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private async Task<string> ReadLogAsync()
    {
        try
        {
            var log = await _runtime.LogsAsync(_engineName, LogLines, CancellationToken.None).ConfigureAwait(false);
            var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return _redactor.Redact(string.Join(Environment.NewLine, lines.TakeLast(LogLines)));
        }
        catch (SqlBranchException)
        {
            return string.Empty;
        }
    }
}
