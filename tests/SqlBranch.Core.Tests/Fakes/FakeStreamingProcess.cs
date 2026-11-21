using SqlBranch.Core.Containers;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>
/// A process with no operating system behind it: it hands out the stream a test gives it, exits with the code and error text a test
/// gives it, and takes <see cref="ExitDelay"/> to exit once killed, so a test can tell whether a caller waited for the exit.
/// </summary>
internal sealed class FakeStreamingProcess : IStreamingProcess
{
    private int _disposed;

    public FakeStreamingProcess(Stream stream, int exitCode = 0, string stdErr = "", CancellationToken cancellationToken = default)
    {
        Stream = stream;
        ExitCode = exitCode;
        StdErr = stdErr;
        if (cancellationToken.CanBeCanceled)
        {
            // Like the real client: the token only makes it end; whoever disposes it waits for the exit.
            cancellationToken.Register(() => _ = Task.Run(Terminate));
        }
    }

    public Stream Stream { get; }

    public int ExitCode { get; }

    public string StdErr { get; }

    /// <summary>How long the process takes to exit after it was killed.</summary>
    public TimeSpan ExitDelay { get; init; } = TimeSpan.Zero;

    /// <summary>What happened, in order: <c>killed</c>, <c>exited</c>, <c>input-closed</c>.</summary>
    public List<string> Events { get; } = [];

    public bool Killed => Events.Contains("killed");

    public bool Exited => Events.Contains("exited");

    /// <summary>Runs when <see cref="WaitAsync"/> closes standard input of a write process.</summary>
    public Action? OnInputClosed { get; init; }

    private readonly object _gate = new();
    private Task? _exit;

    public async Task<ProcessOutput> WaitAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Events.Add("input-closed");
        }

        OnInputClosed?.Invoke();
        if (cancellationToken.IsCancellationRequested)
        {
            await KillAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        lock (_gate)
        {
            if (!Exited)
            {
                Events.Add("exited");
            }
        }

        return new ProcessOutput(ExitCode, string.Empty, StdErr);
    }

    public Task KillAsync()
    {
        lock (_gate)
        {
            return _exit ??= ExitAsync();
        }
    }

    private void Terminate()
    {
        _ = KillAsync();
    }

    private async Task ExitAsync()
    {
        lock (_gate)
        {
            Events.Add("killed");
        }

        if (ExitDelay > TimeSpan.Zero)
        {
            await Task.Delay(ExitDelay).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (!Exited)
            {
                Events.Add("exited");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        bool running;
        lock (_gate)
        {
            running = !Exited;
        }

        if (running)
        {
            await KillAsync().ConfigureAwait(false);
        }
        else
        {
            await (_exit ?? Task.CompletedTask).ConfigureAwait(false);
        }

        await Stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A read-only stream of <see cref="Length"/> bytes that never exist in memory: every byte is the same fill value.</summary>
internal sealed class SyntheticStream : Stream
{
    private readonly byte _fill;
    private long _position;

    public SyntheticStream(long length, byte fill = 0)
    {
        Length = length;
        _fill = fill;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>The most bytes any one read was asked for.</summary>
    public int LargestRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        LargestRead = Math.Max(LargestRead, buffer.Length);
        var count = (int)Math.Min(buffer.Length, Length - _position);
        buffer[..count].Fill(_fill);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A write-only stream that counts and discards, optionally failing once a limit is passed.</summary>
internal sealed class CountingSink : Stream
{
    public long Count { get; private set; }

    /// <summary>When set, a write that would pass this many bytes throws <see cref="IOException"/>.</summary>
    public long? FailAfter { get; init; }

    /// <summary>When true, the first write throws <see cref="OperationCanceledException"/>, as a write cut short by Ctrl+C does.</summary>
    public bool CancelOnWrite { get; init; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => Count;

    public override long Position
    {
        get => Count;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (CancelOnWrite)
        {
            throw new OperationCanceledException();
        }

        if (FailAfter is { } limit && Count + buffer.Length > limit)
        {
            throw new IOException("No space left on device.");
        }

        Count += buffer.Length;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
