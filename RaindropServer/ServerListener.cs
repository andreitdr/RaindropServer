using System.Net;

namespace RaindropServer;

/// <summary>
/// A port of its own beside the one clients use, for something that is not for clients — the owner's
/// dashboard is one. <see cref="ServerHost"/> listens on it with the same certificate and TLS settings,
/// drops a connection from an address <see cref="AcceptsRemote"/> refuses before the TLS handshake,
/// and answers on it only the endpoints carrying a <see cref="ListenerScope"/> with its
/// <see cref="Name"/> — so gRPC is not reachable there, and nothing scoped to it is reachable anywhere
/// else.
/// </summary>
/// <param name="Name">What the listener is for; its endpoints carry <c>new ListenerScope(Name)</c>.</param>
/// <param name="Url">The address to listen on, e.g. <c>https://0.0.0.0:5002</c>. Must be https.</param>
/// <param name="AcceptsRemote">Whether a connection from this address may be served at all.</param>
public sealed record ServerListener(string Name, string Url, Func<IPAddress, bool> AcceptsRemote)
{
    /// <summary>The port <see cref="Url"/> names, or null when it is not an absolute URL with one.</summary>
    public int? Port => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : null;
}

/// <summary>
/// Endpoint metadata that ties an endpoint to one <see cref="ServerListener"/>: it answers on that
/// listener's port and nowhere else.
/// </summary>
public sealed record ListenerScope(string Name);
