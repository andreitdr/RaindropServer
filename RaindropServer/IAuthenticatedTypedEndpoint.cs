using RaindropServer.Users;

namespace RaindropServer;

/// <summary>
/// The authenticated counterpart of <see cref="ITypedEndpoint{TRequest,TResponse}"/>: a typed request
/// and response, plus the caller's <see cref="UserSession"/>. The framework rejects the request with
/// 401 before the handler runs when no valid <c>Authorization: Bearer</c> token is present.
/// </summary>
/// <typeparam name="TRequest">The type to deserialize from the request body.</typeparam>
/// <typeparam name="TResponse">The type to serialize as the response body.</typeparam>
public interface IAuthenticatedTypedEndpoint<TRequest, TResponse>
{
    /// <summary>The route pattern, e.g. "/api/users/me/password".</summary>
    string Route { get; }

    /// <summary>The HTTP method this endpoint responds to, e.g. <see cref="HttpMethod.Post"/>.</summary>
    HttpMethod Method { get; }

    /// <summary>Handles the request and returns the response object to be serialized.</summary>
    Task<TResponse> HandleAsync(UserSession session, TRequest request, CancellationToken cancellationToken);
}
