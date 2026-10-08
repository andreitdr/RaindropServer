namespace RaindropServer;

/// <summary>
/// Thrown from anywhere in the domain layer to refuse a request with a machine-readable code. The
/// transport turns it into its own kind of failure — the gRPC layer into a status plus an
/// <c>ErrorInfo</c> whose reason is <see cref="Code"/> — so services can simply throw without knowing
/// how they were called.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(ApiErrorKind kind, string code, string message) : base(message)
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>The broad class of failure, which decides the status a transport reports.</summary>
    public ApiErrorKind Kind { get; }

    /// <summary>A stable, machine-readable error code the client can branch on, e.g. "username_taken".</summary>
    public string Code { get; }

    public static ApiException BadRequest(string code, string message) => new(ApiErrorKind.BadRequest, code, message);
    public static ApiException Unauthorized(string code, string message) => new(ApiErrorKind.Unauthorized, code, message);
    public static ApiException Forbidden(string code, string message) => new(ApiErrorKind.Forbidden, code, message);
    public static ApiException NotFound(string code, string message) => new(ApiErrorKind.NotFound, code, message);
    public static ApiException Conflict(string code, string message) => new(ApiErrorKind.Conflict, code, message);
    public static ApiException TooLarge(string code, string message) => new(ApiErrorKind.TooLarge, code, message);
    public static ApiException ServiceUnavailable(string code, string message) => new(ApiErrorKind.Unavailable, code, message);
}

/// <summary>
/// What kind of refusal an <see cref="ApiException"/> is. Clients branch on the exception's code, never
/// on this; it exists so a transport can report a sensible status of its own.
/// </summary>
public enum ApiErrorKind
{
    /// <summary>The request itself is malformed or breaks a rule about its contents.</summary>
    BadRequest,

    /// <summary>No valid session, or credentials that do not match.</summary>
    Unauthorized,

    /// <summary>The caller is known but may not do this.</summary>
    Forbidden,

    /// <summary>What the request names does not exist, or the caller may not know it does.</summary>
    NotFound,

    /// <summary>The request conflicts with the current state: a name taken, an epoch moved on.</summary>
    Conflict,

    /// <summary>The request carries more than the server accepts in one go.</summary>
    TooLarge,

    /// <summary>Something the request needs is not available on this server right now.</summary>
    Unavailable
}
