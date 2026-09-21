using Microsoft.AspNetCore.Http;
using RaindropServer.Payloads;

namespace RaindropServer;

/// <summary>
/// Thrown from an endpoint to return a non-success HTTP status with a machine-readable body.
/// Every endpoint adapter catches it and writes an <see cref="ApiError"/> as JSON, so handlers
/// can simply throw instead of touching <see cref="HttpContext"/>.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    /// <summary>The HTTP status code to return.</summary>
    public int StatusCode { get; }

    /// <summary>A stable, machine-readable error code the client can branch on, e.g. "username_taken".</summary>
    public string Code { get; }

    public static ApiException BadRequest(string code, string message) => new(StatusCodes.Status400BadRequest, code, message);
    public static ApiException Unauthorized(string code, string message) => new(StatusCodes.Status401Unauthorized, code, message);
    public static ApiException Forbidden(string code, string message) => new(StatusCodes.Status403Forbidden, code, message);
    public static ApiException NotFound(string code, string message) => new(StatusCodes.Status404NotFound, code, message);
    public static ApiException Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);
    public static ApiException UnsupportedMediaType(string code, string message) => new(StatusCodes.Status415UnsupportedMediaType, code, message);
    public static ApiException ServiceUnavailable(string code, string message) => new(StatusCodes.Status503ServiceUnavailable, code, message);

    /// <summary>
    /// Writes this exception to the response as an <see cref="ApiError"/>, in whichever wire format
    /// the caller asked for.
    /// </summary>
    public async Task WriteToAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCode;
        await context.WritePayloadAsync(new ApiError(Code, Message), cancellationToken);
    }
}

/// <param name="Error">A stable, machine-readable error code, e.g. "username_taken".</param>
/// <param name="Message">A human-readable explanation, safe to show to the user.</param>
public record ApiError(string Error, string Message);
