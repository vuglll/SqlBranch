using SqlBranch.Core.Output;

namespace SqlBranch.Core.Storage;

/// <summary>
/// A write-only stream that cuts what it is given into parts of <see cref="IArchiveUpload.PartSize"/> and uploads each one before it
/// takes more, so memory stays near one part (design D5). <see cref="FinishAsync"/> sends the last part and commits.
/// After <see cref="Abandon"/> it takes and sends nothing, so disposing an archive writer on a failure uploads no more.
/// </summary>
public sealed class PartUploadStream : Stream
{
    private readonly IArchiveUpload _upload;
    private readonly byte[] _buffer;
    private readonly CancellationToken _cancellationToken;
    private int _filled;
    private bool _abandoned;
    private bool _finished;

    public PartUploadStream(IArchiveUpload upload, CancellationToken cancellationToken)
    {
        _upload = upload ?? throw new ArgumentNullException(nameof(upload));
        _buffer = new byte[upload.PartSize];
        _cancellationToken = cancellationToken;
    }

    /// <summary>The bytes accepted so far: the size of the archive once finished.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>The number of parts uploaded.</summary>
    public int Parts { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => TotalBytes;

    public override long Position
    {
        get => TotalBytes;
        set => throw new NotSupportedException();
    }

    /// <summary>From now on writes are dropped.</summary>
    public void Abandon() => _abandoned = true;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) =>
        WriteAsync(buffer.ToArray(), _cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_abandoned)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_finished, this);
        while (!buffer.IsEmpty)
        {
            var take = Math.Min(buffer.Length, _buffer.Length - _filled);
            buffer.Span[..take].CopyTo(_buffer.AsSpan(_filled));
            _filled += take;
            TotalBytes += take;
            buffer = buffer[take..];
            if (_filled == _buffer.Length)
            {
                await SendAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Uploads what is left as the last part and commits the object.</summary>
    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        if (_abandoned)
        {
            throw new InvalidOperationException("The upload was abandoned.");
        }

        if (_filled > 0 || Parts == 0)
        {
            await SendAsync(cancellationToken).ConfigureAwait(false);
        }

        _finished = true;
        await _upload.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        var length = _filled;
        _filled = 0;
        await _upload.UploadPartAsync(_buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        Parts++;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>A read stream over a storage download that reports a failure in the middle of the body as <c>storage_unreachable</c>.</summary>
internal sealed class StorageReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IDisposable? _owner;
    private readonly Func<Exception, Exception> _map;

    public StorageReadStream(Stream inner, IDisposable? owner, Func<Exception, Exception> map)
    {
        _inner = inner;
        _owner = owner;
        _map = map;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _inner.Read(buffer);
        }
        catch (Exception ex) when (ex is not SqlBranchException and not OperationCanceledException)
        {
            throw _map(ex);
        }
        catch (OperationCanceledException ex)
        {
            // A synchronous read has no token: a cancellation here is the SDK's own timeout.
            throw _map(ex);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SqlBranchException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // A TaskCanceledException while the caller's token is not cancelled is the SDK's timeout, not the user's Ctrl+C.
            throw _map(ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _inner.Dispose();
            }
            finally
            {
                _owner?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
