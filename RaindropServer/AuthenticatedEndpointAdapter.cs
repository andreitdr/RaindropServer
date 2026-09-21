using Microsoft.AspNetCore.Http;
using RaindropServer.Payloads;
using RaindropServer.Users;

namespace RaindropServer;

/// <summary>
/// Wraps an <see cref="IAuthenticatedEndpoint"/> as an <see cref="IEndpoint"/>, resolving the
/// caller's session from the <c>Authorization: Bearer</c> header before invoking the handler.
/// </summary>
[SkipEndpointDiscovery]
internal sealed class AuthenticatedEndpointAdapter : IEndpoint
{
    private readonly IAuthenticatedEndpoint _inner;
    private readonly ISessionStore _sessions;

    public AuthenticatedEndpointAdapter(IAuthenticatedEndpoint inner, ISessionStore sessions)
    {
        _inner = inner;
        _sessions = sessions;
    }

    public string Route => _inner.Route;
    public HttpMethod Method => _inner.Method;

    public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            var session = BearerToken.Authenticate(context, _sessions);
            await _inner.HandleAsync(session, context, cancellationToken);
        }
        catch (ApiException ex)
        {
            await ex.WriteToAsync(context, cancellationToken);
        }
    }
}

/// <summary>
/// Wraps an <see cref="IAuthenticatedTypedEndpoint{TRequest,TResponse}"/> as an <see cref="IEndpoint"/>,
/// combining session resolution with the wire-format handling of
/// <see cref="TypedEndpointAdapter{TRequest,TResponse}"/>.
/// </summary>
[SkipEndpointDiscovery]
internal sealed class AuthenticatedTypedEndpointAdapter<TRequest, TResponse> : IEndpoint
{
    private readonly IAuthenticatedTypedEndpoint<TRequest, TResponse> _inner;
    private readonly ISessionStore _sessions;
    private readonly PayloadCodecs _codecs;

    public AuthenticatedTypedEndpointAdapter(
        IAuthenticatedTypedEndpoint<TRequest, TResponse> inner, ISessionStore sessions, PayloadCodecs codecs)
    {
        _inner = inner;
        _sessions = sessions;
        _codecs = codecs;
    }

    public string Route => _inner.Route;
    public HttpMethod Method => _inner.Method;

    public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            var session = BearerToken.Authenticate(context, _sessions);
            var codec = _codecs.ForRequest(context.Request);
            var request = await context.ReadPayloadAsync<TRequest>(codec, cancellationToken);
            var response = await _inner.HandleAsync(session, request, cancellationToken);
            await context.WritePayloadAsync(response, cancellationToken);
        }
        catch (ApiException ex)
        {
            await ex.WriteToAsync(context, cancellationToken);
        }
    }
}

/// <summary>Reads and validates the <c>Authorization: Bearer</c> header.</summary>
internal static class BearerToken
{
    private const string Scheme = "Bearer ";

    public static UserSession Authenticate(HttpContext context, ISessionStore sessions)
    {
        string? header = context.Request.Headers.Authorization;

        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            throw ApiException.Unauthorized("missing_token", "An 'Authorization: Bearer <token>' header is required.");

        var token = header[Scheme.Length..].Trim();

        if (!sessions.TryValidate(token, out var session))
            throw ApiException.Unauthorized("invalid_session", "The session token is invalid or has expired. Log in again.");

        return session;
    }
}
