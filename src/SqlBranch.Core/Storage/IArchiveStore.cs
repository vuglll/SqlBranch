namespace SqlBranch.Core.Storage;

/// <summary>
/// One archive object in Azure Blob Storage or S3-compatible storage (design D5). Every failure of the storage service is thrown as a
/// <see cref="SqlBranchException"/> with <c>storage_unreachable</c> (or <c>destination_exists</c>), already through the secret redactor,
/// so no caller ever sees an SDK exception.
/// </summary>
public interface IArchiveStore
{
    /// <summary>The object's URL as the user gave it. It has no query string and no secret.</summary>
    string DisplayUrl { get; }

    /// <summary>Whether an object exists at the URL.</summary>
    Task<bool> ExistsAsync(CancellationToken cancellationToken);

    /// <summary>Reads the object's length with HEAD. Throws <c>storage_unreachable</c> when there is none.</summary>
    Task<long> GetLengthAsync(CancellationToken cancellationToken);

    /// <summary>Opens the object as one read-only stream (one GET). Throws <c>storage_unreachable</c> when there is none.</summary>
    Task<Stream> OpenReadAsync(CancellationToken cancellationToken);

    /// <summary>Starts an upload. With <paramref name="failIfExists"/> its commit refuses to replace an object that appeared meanwhile.</summary>
    /// <param name="failIfExists">True without <c>--force</c>.</param>
    /// <param name="expectedBytes">The image's size, which sets the part size (S3 allows 10,000 parts).</param>
    /// <param name="cancellationToken">Cancels the start only; later calls take their own tokens.</param>
    Task<IArchiveUpload> BeginUploadAsync(bool failIfExists, long expectedBytes, CancellationToken cancellationToken);
}

/// <summary>An upload in parts. Parts go one at a time, in order; nothing is visible at the URL until <see cref="CompleteAsync"/>.</summary>
public interface IArchiveUpload : IAsyncDisposable
{
    /// <summary>The size of every part but the last.</summary>
    int PartSize { get; }

    /// <summary>Uploads the next part.</summary>
    Task UploadPartAsync(ReadOnlyMemory<byte> part, CancellationToken cancellationToken);

    /// <summary>Makes the object appear. Throws <c>destination_exists</c> when the upload was started with fail-if-exists and an object is there.</summary>
    Task CompleteAsync(CancellationToken cancellationToken);

    /// <summary>Drops what the upload staged, as far as the service allows. Never throws, and does not use a cancelled token.</summary>
    Task AbortAsync();
}

/// <summary>How the storage clients are built. Production uses none of it; tests give the HTTP handler of a fake storage service.</summary>
public sealed class StorageOptions
{
    /// <summary>The handler every storage request goes through, or null for the platform's own.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Overrides the part size, which is 64 MiB or more. Tests use it to make a small archive span several parts; the tool never sets it.</summary>
    public int? PartSize { get; init; }

    /// <summary>Overrides the pause before an Azure retry, so tests that fail requests on purpose do not wait; the tool never sets it.</summary>
    public TimeSpan? RetryDelay { get; init; }
}
