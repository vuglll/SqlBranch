using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Storage;

/// <summary>Builds the storage client for a parsed URL.</summary>
public static class ArchiveStores
{
    /// <summary>Creates the client for <paramref name="url"/> from the secrets read for it.</summary>
    public static IArchiveStore Create(BackupUrl url, StorageSecrets secrets, StorageOptions options, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);
        return url.Kind == BackupUrlKind.AzureBlob
            ? new AzureArchiveStore(url, secrets.SasToken ?? throw new InvalidOperationException("No SAS token was read."), options, redactor)
            : new S3ArchiveStore(url, secrets.AccessKeyId ?? throw new InvalidOperationException("No S3 key was read."),
                secrets.SecretAccessKey ?? throw new InvalidOperationException("No S3 secret was read."), options, redactor);
    }

    /// <summary>Adapts archive stores to the metadata and streaming reads needed to stage a BACPAC.</summary>
    public static IBacpacReadSource CreateReadSource(
        Func<BackupUrl, StorageSecrets, SecretRedactor, IArchiveStore> storeFactory, SecretRedactor redactor) =>
        new BacpacReadSource(storeFactory, redactor);

    private sealed class BacpacReadSource(
        Func<BackupUrl, StorageSecrets, SecretRedactor, IArchiveStore> storeFactory, SecretRedactor redactor) : IBacpacReadSource
    {
        public async ValueTask<long> HeadAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken)
        {
            var store = storeFactory(url, secrets, redactor);
            using var owner = store as IDisposable;
            return await store.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<Stream> GetAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken)
        {
            var store = storeFactory(url, secrets, redactor);
            var owner = store as IDisposable;
            try
            {
                var stream = await store.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                if (owner is null)
                {
                    return stream;
                }

                var errors = new StorageErrors(url.Url, redactor);
                return new StorageReadStream(stream, owner, ex => errors.Unreachable(ex, "read the BACPAC"));
            }
            catch
            {
                owner?.Dispose();
                throw;
            }
        }
    }
}
