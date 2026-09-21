namespace RaindropServer;

/// <summary>
/// Marks a class that implements <see cref="IEndpoint"/> but must not be picked up by
/// auto-discovery — used by the framework's own adapters, which are constructed explicitly
/// around a user-supplied endpoint rather than resolved on their own.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SkipEndpointDiscoveryAttribute : Attribute;
