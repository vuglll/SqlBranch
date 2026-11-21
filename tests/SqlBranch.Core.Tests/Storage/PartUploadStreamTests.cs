using SqlBranch.Core.Output;
using SqlBranch.Core.Storage;

namespace SqlBranch.Core.Tests.Storage;

/// <summary>The stream that cuts an archive into parts (design D5).</summary>
public sealed class PartUploadStreamTests
{
    private sealed class RecordingUpload(int partSize) : IArchiveUpload
    {
        public List<int> Parts { get; } = [];

        public List<byte> Bytes { get; } = [];

        public bool Completed { get; private set; }

        public bool Aborted { get; private set; }

        public int PartSize => partSize;

        public Task UploadPartAsync(ReadOnlyMemory<byte> part, CancellationToken cancellationToken)
        {
            Parts.Add(part.Length);
            Bytes.AddRange(part.ToArray());
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public Task AbortAsync()
        {
            Aborted = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Writes_are_cut_into_parts_of_the_part_size_and_the_rest_goes_as_the_last_part_before_the_commit()
    {
        var upload = new RecordingUpload(1000);
        var sink = new PartUploadStream(upload, CancellationToken.None);
        var data = new byte[2500];
        new Random(1).NextBytes(data);

        await sink.WriteAsync(data.AsMemory(0, 700));
        await sink.WriteAsync(data.AsMemory(700, 1800));
        Assert.Equal([1000, 1000], upload.Parts);
        Assert.False(upload.Completed);
        await sink.FinishAsync(CancellationToken.None);

        Assert.Equal([1000, 1000, 500], upload.Parts);
        Assert.Equal(data, upload.Bytes);
        Assert.Equal(2500, sink.TotalBytes);
        Assert.True(upload.Completed);
    }

    [Fact]
    public async Task An_exact_multiple_of_the_part_size_sends_no_empty_last_part()
    {
        var upload = new RecordingUpload(100);
        var sink = new PartUploadStream(upload, CancellationToken.None);

        await sink.WriteAsync(new byte[200]);
        await sink.FinishAsync(CancellationToken.None);

        Assert.Equal([100, 100], upload.Parts);
    }

    [Fact]
    public async Task An_abandoned_stream_drops_every_later_write_and_cannot_be_finished()
    {
        var upload = new RecordingUpload(100);
        var sink = new PartUploadStream(upload, CancellationToken.None);
        await sink.WriteAsync(new byte[50]);

        sink.Abandon();
        await sink.WriteAsync(new byte[500]);

        Assert.Empty(upload.Parts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.FinishAsync(CancellationToken.None));
        Assert.False(upload.Completed);
    }

    [Fact]
    public void Writes_through_the_synchronous_path_are_cut_the_same_way()
    {
        var upload = new RecordingUpload(10);
        var sink = new PartUploadStream(upload, CancellationToken.None);

        sink.Write(new byte[25], 0, 25);

        Assert.Equal([10, 10], upload.Parts);
        Assert.Equal(25, sink.Length);
    }

    private sealed class ThrowingStream(Exception toThrow) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw toThrow;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw toThrow;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static StorageReadStream ReadOver(Exception toThrow) =>
        new(new ThrowingStream(toThrow), null, ex => new StorageErrors("https://acme.blob.core.windows.net/images/shop.sbimg", new SqlBranch.Core.Secrets.SecretRedactor()).Unreachable(ex, "read the archive"));

    [Fact]
    public async Task A_timeout_in_the_middle_of_a_download_is_storage_unreachable_when_the_caller_did_not_cancel()
    {
        await using var stream = ReadOver(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));

        var error = await Assert.ThrowsAsync<SqlBranchException>(async () => await stream.ReadExactlyAsync(new byte[16], CancellationToken.None));

        Assert.Equal(ErrorCodes.StorageUnreachable, error.Code);
    }

    [Fact]
    public async Task A_cancellation_of_the_callers_token_in_the_middle_of_a_download_stays_a_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await using var stream = ReadOver(new TaskCanceledException("canceled", null, cancel.Token));

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await stream.ReadExactlyAsync(new byte[16], cancel.Token));
    }
}
