using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Storage;

namespace SqlBranch.Core.LogicalSources;

/// <summary>Read-only access to one private storage object over authenticated HTTPS.</summary>
/// <remarks>
/// The URL carries no credentials, query or fragment. Implementations must verify the
/// server certificate and reject redirects. The returned stream owns the GET response
/// and must release it when disposed.
/// </remarks>
public interface IBacpacReadSource
{
    ValueTask<long> HeadAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken);
    ValueTask<Stream> GetAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken);
}

internal static class BacpacDownload
{
    public static async Task StageAsync(IBacpacReadSource? source, BackupUrl url, StorageSecrets secrets,
        BacpacScratch scratch, CancellationToken token, Func<Stream>? openOutput = null, Action<long>? reportSize = null)
    {
        if (source is null)
        {
            throw Unreachable(url);
        }

        var remaining = await ProviderAsync(() => source.HeadAsync(url, secrets, token), url, token).ConfigureAwait(false);
        if (remaining < 0)
        {
            throw Unreachable(url);
        }

        reportSize?.Invoke(remaining);
        try
        {
            await using var output = openOutput is null ? scratch.CreatePackage() : openOutput();
            var input = await ProviderAsync(() => source.GetAsync(url, secrets, token), url, token).ConfigureAwait(false);
            try
            {
                var buffer = new byte[64 * 1024];
                while (remaining > 0)
                {
                    var count = (int)Math.Min(remaining, buffer.Length);
                    var read = await ProviderAsync(() => input.ReadAsync(buffer.AsMemory(0, count), token), url, token).ConfigureAwait(false);
                    if (read <= 0 || read > count)
                    {
                        throw Unreachable(url);
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    remaining -= read;
                }

                // HEAD is the write bound. Read one extra byte to detect an object
                // that grew between HEAD and GET without writing beyond that bound.
                if (await ProviderAsync(() => input.ReadAsync(buffer.AsMemory(0, 1), token), url, token).ConfigureAwait(false) != 0)
                {
                    throw Unreachable(url);
                }
            }
            finally
            {
                await ProviderAsync(async () =>
                {
                    await input.DisposeAsync().ConfigureAwait(false);
                    return true;
                }, url, token, disposing: true).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
        }
        catch (IOException ex) when (BacpacScratch.IsFull(ex))
        {
            throw new SqlBranchException(ErrorCodes.TempFull, "Client scratch ran out of space while downloading the BACPAC.");
        }
    }

    private static SqlBranchException Unreachable(BackupUrl url, string guidance = "") =>
        new(ErrorCodes.StorageUnreachable, $"Could not read the complete BACPAC object from storage at {url.Url}.{guidance}");

    private static async ValueTask<T> ProviderAsync<T>(Func<ValueTask<T>> operation, BackupUrl url, CancellationToken token, bool disposing = false)
    {
        if (!disposing)
        {
            token.ThrowIfCancellationRequested();
        }
        try
        {
            return await operation().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Provider exceptions can carry credentials in any part of the exception.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            token.ThrowIfCancellationRequested();
            throw Unreachable(url, url.Kind == BackupUrlKind.S3 && StorageHttp.FindRefusedRedirect(ex) is not null
                ? $" The S3 endpoint returned a redirect. {StorageHttp.S3RegionGuidance}"
                : "");
        }
    }
}
