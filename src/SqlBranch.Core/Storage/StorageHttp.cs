namespace SqlBranch.Core.Storage;

internal static class StorageHttp
{
    internal const string S3RegionGuidance = "Check --s3-region.";

    private static readonly SocketsHttpHandler PlatformHandler = new() { AllowAutoRedirect = false, UseCookies = false };

    public static HttpClient SharedClient { get; } = CreateClient();

    public static HttpClient CreateClient(HttpMessageHandler? handler = null) =>
        new(new RejectRedirects(handler ?? PlatformHandler)) { Timeout = Timeout.InfiniteTimeSpan };

    internal static RefusedRedirectException? FindRefusedRedirect(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is RefusedRedirectException redirect)
            {
                return redirect;
            }
        }

        return null;
    }

    internal sealed class RefusedRedirectException(System.Net.HttpStatusCode status)
        : HttpRequestException("The storage endpoint returned a redirect.", null, status);

    // The SDKs must not see redirects either: S3 can follow a 307 in its own pipeline and re-sign the request.
    private sealed class RejectRedirects(HttpMessageHandler handler) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(handler, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await _inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new RefusedRedirectException(status);
            }

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
