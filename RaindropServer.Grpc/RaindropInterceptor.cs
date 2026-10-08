using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using RaindropServer.Users;
using Status = Google.Rpc.Status;

namespace RaindropServer.Grpc;

/// <summary>
/// Runs around every call, of every shape. It does the two things each service would otherwise repeat:
/// <list type="bullet">
/// <item>resolves <c>authorization: Bearer &lt;token&gt;</c> to a <see cref="UserSession"/> before the
/// handler runs, refusing the call when there is none — unless the method is marked
/// <see cref="AllowAnonymousAttribute"/>;</item>
/// <item>turns an <see cref="ApiException"/> into a status whose <c>ErrorInfo.reason</c> is the error code
/// clients branch on, and anything unexpected into <c>INTERNAL</c> with nothing of it leaked.</item>
/// </list>
/// </summary>
public sealed class RaindropInterceptor : Interceptor
{
    /// <summary>The <c>ErrorInfo.domain</c> every Raindrop error carries.</summary>
    public const string ErrorDomain = "raindrop";

    private const string SessionKey = "raindrop.session";

    private readonly ISessionStore _sessions;

    public RaindropInterceptor(ISessionStore sessions) => _sessions = sessions;

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation) =>
        GuardAsync(context, () => continuation(request, context));

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation) =>
        GuardAsync(context, () => continuation(requestStream, context));

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation) =>
        GuardAsync(context, async () =>
        {
            await continuation(request, responseStream, context);
            return true;
        });

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
        GuardAsync(context, async () =>
        {
            await continuation(requestStream, responseStream, context);
            return true;
        });

    /// <summary>The session the interceptor resolved for this call. Throws on an anonymous method.</summary>
    public static UserSession SessionOf(ServerCallContext context) =>
        context.UserState.TryGetValue(SessionKey, out var session) && session is UserSession resolved
            ? resolved
            : throw new InvalidOperationException(
                $"{context.Method} is anonymous, so it has no session; it cannot ask for one.");

    /// <summary>The status an <see cref="ApiException"/> becomes on the wire.</summary>
    public static RpcException ToRpcException(ApiException exception) =>
        ToRpcException(StatusFor(exception.Kind), exception.Code, exception.Message);

    /// <summary>A status with an <c>ErrorInfo</c> whose reason is <paramref name="reason"/>.</summary>
    public static RpcException ToRpcException(StatusCode code, string reason, string message)
    {
        var status = new Status
        {
            Code = (int)code,
            Message = message,
            Details = { Any.Pack(new ErrorInfo { Reason = reason, Domain = ErrorDomain }) }
        };

        return status.ToRpcException();
    }

    private async Task<TResponse> GuardAsync<TResponse>(ServerCallContext context, Func<Task<TResponse>> call)
    {
        try
        {
            if (!IsAnonymous(context))
                context.UserState[SessionKey] = Authenticate(context);

            return await call();
        }
        catch (ApiException ex)
        {
            throw ToRpcException(ex);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The client hung up or the deadline passed; gRPC already knows, and there is nobody to tell.
            throw;
        }
        catch (Exception ex)
        {
            // Whatever it was stays in the server's log; the client learns only that it was not its fault.
            Console.Error.WriteLine($"[raindrop] {context.Method} failed: {ex}");
            throw ToRpcException(StatusCode.Internal, "internal_error", "Something went wrong on the server.");
        }
    }

    /// <summary>
    /// The same rules sessions have always had: no token is <c>missing_token</c>, a token the store does not
    /// recognise (or that has lapsed) is <c>invalid_session</c>. Validating also slides the session.
    /// </summary>
    private UserSession Authenticate(ServerCallContext context)
    {
        var header = context.RequestHeaders.GetValue("authorization");

        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw ApiException.Unauthorized("missing_token", "Send the session token as `authorization: Bearer <token>`.");

        var token = header["Bearer ".Length..].Trim();

        if (token.Length == 0 || !_sessions.TryValidate(token, out var session))
            throw ApiException.Unauthorized("invalid_session", "The session is not valid; log in again.");

        return session;
    }

    private static bool IsAnonymous(ServerCallContext context) =>
        context.GetHttpContext().GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    private static StatusCode StatusFor(ApiErrorKind kind) => kind switch
    {
        ApiErrorKind.BadRequest => StatusCode.InvalidArgument,
        ApiErrorKind.Unauthorized => StatusCode.Unauthenticated,
        ApiErrorKind.Forbidden => StatusCode.PermissionDenied,
        ApiErrorKind.NotFound => StatusCode.NotFound,
        ApiErrorKind.Conflict => StatusCode.FailedPrecondition,
        ApiErrorKind.TooLarge => StatusCode.ResourceExhausted,
        ApiErrorKind.Unavailable => StatusCode.Unavailable,
        _ => StatusCode.Unknown
    };
}

/// <summary>The session a call runs as, resolved by <see cref="RaindropInterceptor"/>.</summary>
public static class ServerCallContextExtensions
{
    public static UserSession Session(this ServerCallContext context) => RaindropInterceptor.SessionOf(context);
}
