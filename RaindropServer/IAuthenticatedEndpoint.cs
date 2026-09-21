using Microsoft.AspNetCore.Http;
using RaindropServer.Users;

namespace RaindropServer;

/// <summary>
/// Like <see cref="IEndpoint"/>, but only reached when the request carries a valid session token
/// (<c>Authorization: Bearer &lt;token&gt;</c>). The framework resolves the session and returns
/// 401 before the handler runs, so implementations can assume an authenticated caller.
/// </summary>
public interface IAuthenticatedEndpoint
{
    /// <summary>The route pattern, e.g. "/api/users/me".</summary>
    string Route { get; }

    /// <summary>The HTTP method this endpoint responds to, e.g. <see cref="HttpMethod.Get"/>.</summary>
    HttpMethod Method { get; }

    /// <summary>Handles the request on behalf of the authenticated <paramref name="session"/>.</summary>
    Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken);
}
