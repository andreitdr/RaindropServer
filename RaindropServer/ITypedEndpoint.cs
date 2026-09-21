namespace RaindropServer;

/// <summary>
/// Implement this interface to register a typed endpoint with the server. The library deserializes
/// the request body into <typeparamref name="TRequest"/> and serializes the returned
/// <typeparamref name="TResponse"/> back — in whichever wire format the caller negotiated, binary or
/// JSON. The handler never sees the difference.
/// </summary>
/// <typeparam name="TRequest">The type to deserialize from the request body.</typeparam>
/// <typeparam name="TResponse">The type to serialize as the response body.</typeparam>
public interface ITypedEndpoint<TRequest, TResponse>
{
    /// <summary>The route pattern, e.g. "/api/users/register".</summary>
    string Route { get; }

    /// <summary>The HTTP method this endpoint responds to, e.g. <see cref="HttpMethod.Post"/>.</summary>
    HttpMethod Method { get; }

    /// <summary>Handles the request and returns the response object to be serialized.</summary>
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}
