using Microsoft.AspNetCore.Builder;

namespace RaindropServer;

/// <summary>
/// Something that serves requests: it maps its routes, and any middleware they need, onto the running
/// application. <see cref="ServerHost"/> knows nothing about any one transport — the gRPC services, and
/// the voice module's signalling relay, are each a contributor registered with
/// <c>TryAddEnumerable</c>, and a plugin that serves something of its own is one more.
/// </summary>
public interface IRouteContributor
{
    /// <summary>Called once, while the application is being built and before it listens.</summary>
    void Map(WebApplication app);
}
