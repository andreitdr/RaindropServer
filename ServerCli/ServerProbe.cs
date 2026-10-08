using System.Net.Security;
using Grpc.Core;
using Grpc.Net.Client;
using Proto = RaindropServer.Grpc.Protocol;

namespace ServerCli;

/// <summary>
/// <c>ServerCli probe &lt;url&gt;</c>: asks a running server for its handshake, the way a client's first
/// call does, and prints what it answered as <c>key=value</c> lines — what <c>deploy.sh</c> compares to
/// tell whether a server came back as the same server.
/// </summary>
/// <remarks>
/// Any certificate is accepted: the point is to read what the server presents, not to decide whether to
/// trust it. The identity key printed here is what a caller compares against the one it knew.
/// </remarks>
internal static class ServerProbe
{
    public static async Task<int> RunAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
        {
            Console.Error.WriteLine($"probe: '{url}' is not an https:// address.");
            return 2;
        }

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var client = new Proto.ServerService.ServerServiceClient(channel);

        try
        {
            var info = await client.GetServerInfoAsync(new Proto.GetServerInfoRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5));

            Console.WriteLine($"protocolVersion={info.ProtocolVersion}");
            Console.WriteLine($"tlsFingerprint={(info.HasTlsFingerprint ? info.TlsFingerprint : "")}");
            Console.WriteLine($"identityKey={(info.HasIdentityKey ? info.IdentityKey.ToBase64() : "")}");
            Console.WriteLine($"capabilities={string.Join(',', info.Capabilities)}");
            return 0;
        }
        catch (RpcException ex)
        {
            Console.Error.WriteLine($"probe: {address} did not answer: {ex.Status.Detail}");
            return 1;
        }
    }
}
