namespace SqlBranch.Core.LogicalSources;

/// <summary>Compressed size and elapsed milliseconds, including phases that threw.</summary>
public sealed class BacpacBuildProgress(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, long> _milliseconds = new(StringComparer.Ordinal);
    private BacpacDetachment _detachment = BacpacDetachment.Empty;

    public IReadOnlyList<string> DetachedUsers => _detachment.Users;
    public int RemovedLogins => _detachment.RemovedLogins;
    public int RemovedServerObjects => _detachment.RemovedServerObjects;

    public long? SourceSizeBytes { get; private set; }
    public IReadOnlyDictionary<string, long> PhaseMilliseconds => _milliseconds;
    public Action<string>? Notice { get; init; }

    internal void ReportDetachment(BacpacDetachment detachment) => _detachment = detachment;

    internal void ReportSize(long bytes)
    {
        SourceSizeBytes = bytes;
        Notice?.Invoke(FormattableString.Invariant($"BACPAC package size: {bytes} bytes. Large BACPAC operations can take hours."));
    }

    internal void ReportRewrite(long bytes) =>
        Notice?.Invoke(FormattableString.Invariant($"Writing a BACPAC scratch copy of about {bytes} bytes to detach login-mapped users."));

    internal IDisposable Measure(string phase) => new Measurement(this, phase);

    private sealed class Measurement(BacpacBuildProgress owner, string phase) : IDisposable
    {
        private readonly long _start = owner._clock.GetTimestamp();

        public void Dispose()
        {
            owner._milliseconds[phase] = owner._milliseconds.GetValueOrDefault(phase) +
                (long)owner._clock.GetElapsedTime(_start).TotalMilliseconds;
        }
    }
}
