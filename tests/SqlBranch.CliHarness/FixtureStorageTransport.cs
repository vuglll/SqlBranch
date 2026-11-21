using System.Net;

namespace SqlBranch.IntegrationTests;

internal static class FixtureStorageTransport
{
    /// <summary>
    /// An HTTP handler for the Core storage clients, which run on the client, where the bridge address of a fixture container
    /// (and the account's blob host name) is not reachable: every connection goes to the container's published loopback port,
    /// and the only certificate the handler accepts is the one the fixture's test authority issued (by thumbprint).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(string thumbprint, int hostPort) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, hostPort), cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                string.Equals(certificate?.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase),
        },
    };
}
