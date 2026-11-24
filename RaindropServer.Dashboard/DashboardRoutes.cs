using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RaindropServer.Security;

namespace RaindropServer.Dashboard;

/// <summary>
/// The dashboard's page and its JSON API, every endpoint scoped to the dashboard's own listener
/// (<see cref="ListenerScope"/>), so none of it answers on the port clients use.
/// </summary>
/// <remarks>
/// <para>
/// Every response carries a strict content security policy (the page loads nothing but itself, and
/// runs no inline script), refuses framing, and is never cached. A request that changes something must
/// come from the dashboard's own origin, which with the <c>SameSite=Strict</c> cookie is what keeps
/// another site from acting through a signed-in browser.
/// </para>
/// </remarks>
internal sealed class DashboardRoutes(
    DashboardAuth auth,
    DashboardStatusService status,
    DashboardSampler sampler,
    DashboardRecovery recovery,
    DashboardOptions options) : IRouteContributor
{
    /// <summary>The cookie a signed-in browser holds. <c>__Host-</c>: https only, this host only, path /.</summary>
    public const string CookieName = "__Host-raindrop-dashboard";

    private const string SessionKey = "raindrop.dashboard.session";

    private readonly RequestThrottle _throttle = RequestThrottle.PerMinute(options.SignInAttemptsPerMinute);

    public void Map(WebApplication app)
    {
        var scope = new ListenerScope(DashboardOptions.ListenerName);

        RouteHandlerBuilder Page(RouteHandlerBuilder endpoint) =>
            endpoint.WithMetadata(scope).AddEndpointFilter(Headers).AddEndpointFilter(Errors);

        RouteHandlerBuilder Api(RouteHandlerBuilder endpoint) => Page(endpoint).AddEndpointFilter(SameOrigin);

        RouteHandlerBuilder Owner(RouteHandlerBuilder endpoint) => Api(endpoint).AddEndpointFilter(SignedIn);

        // ── The page ──
        var index = Resource("index.html");
        var script = Resource("app.js");
        var style = Resource("app.css");

        Page(app.MapGet("/", () => Results.Bytes(index, "text/html; charset=utf-8")));
        Page(app.MapGet("/app.js", () => Results.Bytes(script, "text/javascript; charset=utf-8")));
        Page(app.MapGet("/app.css", () => Results.Bytes(style, "text/css; charset=utf-8")));

        // ── Signing in ──
        Api(app.MapGet("/api/state", (HttpContext context) =>
        {
            var signedIn = auth.TryValidate(context.Request.Cookies[CookieName], out var session);
            return Results.Ok(new { setUp = auth.IsSetUp, signedIn, username = signedIn ? session.Username : null });
        }));

        Api(app.MapPost("/api/setup", (SetupRequest request, HttpContext context) =>
        {
            _throttle.Check(Caller(context));
            var session = auth.SetUp(request.Code, request.Username, request.Password);
            SetCookie(context, session);
            return Results.Ok(new { username = session.Username });
        }));

        Api(app.MapPost("/api/login", (SignInRequest request, HttpContext context) =>
        {
            _throttle.Check(Caller(context));
            try
            {
                var session = auth.SignIn(request.Username, request.Password);
                SetCookie(context, session);
                return Results.Ok(new { username = session.Username });
            }
            catch (ApiException ex) when (ex.Code == "invalid_credentials")
            {
                options.Audit($"failed sign-in from {Caller(context)}");
                throw;
            }
        }));

        Api(app.MapPost("/api/logout", (HttpContext context) =>
        {
            auth.SignOut(context.Request.Cookies[CookieName]);
            context.Response.Cookies.Delete(CookieName, CookieOptions(DateTimeOffset.UnixEpoch));
            return Results.NoContent();
        }));

        Owner(app.MapPost("/api/password", (PasswordRequest request, HttpContext context) =>
        {
            _throttle.Check(Caller(context));
            auth.ChangePassword(Session(context), request.CurrentPassword, request.NewPassword);
            return Results.NoContent();
        }));

        // ── What the server is doing ──
        Owner(app.MapGet("/api/status", async (CancellationToken cancellationToken) =>
            Results.Ok(await status.ReadAsync(cancellationToken))));

        // The page asks for the whole hour once, then only for what is newer than its last sample.
        Owner(app.MapGet("/api/history", (DateTime? since) => Results.Ok(new
        {
            intervalSeconds = DashboardSampler.Interval.TotalSeconds,
            samples = since is { } after
                ? sampler.History.Where(sample => sample.AtUtc > after.ToUniversalTime()).ToList()
                : sampler.History
        })));

        // ── Account recovery ──
        Owner(app.MapGet("/api/accounts/lookup", async (string? q, CancellationToken cancellationToken) =>
            Results.Ok(await recovery.LookupAsync(q, cancellationToken))));

        Owner(app.MapPost("/api/accounts/{userId:guid}/recovery", async (Guid userId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var claim = await recovery.IssueAsync(userId, Session(context).Username, cancellationToken);
            return Results.Ok(new { claim.UserId, claim.Username, claim.Code, claim.ExpiresAtUtc });
        }));

        Owner(app.MapGet("/api/recoveries", () => Results.Ok(recovery.Pending())));

        Owner(app.MapDelete("/api/recoveries/{userId:guid}", (Guid userId, HttpContext context) =>
        {
            recovery.Cancel(userId, Session(context).Username);
            return Results.NoContent();
        }));
    }

    // ── Filters ──────────────────────────────────────────────────────────────

    private static ValueTask<object?> Headers(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
            "form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        // No Strict-Transport-Security: the certificate is usually self-signed, and HSTS would make the
        // browser's certificate warning impossible to click through.
        return next(context);
    }

    /// <summary>A refusal as JSON — <c>{ code, message }</c> — with the HTTP status its kind means.</summary>
    private static async ValueTask<object?> Errors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (ApiException ex)
        {
            var status = ex.Kind switch
            {
                ApiErrorKind.BadRequest => StatusCodes.Status400BadRequest,
                ApiErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
                ApiErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                ApiErrorKind.NotFound => StatusCodes.Status404NotFound,
                ApiErrorKind.Conflict => StatusCodes.Status409Conflict,
                ApiErrorKind.TooLarge => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status503ServiceUnavailable
            };

            return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: status);
        }
    }

    /// <summary>
    /// Anything that changes something must name the dashboard's own origin. Browsers send
    /// <c>Origin</c> on every such fetch, so a page elsewhere cannot forge one.
    /// </summary>
    private static ValueTask<object?> SameOrigin(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;

        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            return next(context);

        var origin = request.Headers.Origin.ToString();
        var expected = $"{request.Scheme}://{request.Host}";

        return string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Json(
                new { code = "cross_origin", message = "This request did not come from the dashboard." },
                statusCode: StatusCodes.Status403Forbidden));
    }

    private ValueTask<object?> SignedIn(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!auth.TryValidate(context.HttpContext.Request.Cookies[CookieName], out var session))
            return ValueTask.FromResult<object?>(Results.Json(
                new { code = "signed_out", message = "Sign in to the dashboard." },
                statusCode: StatusCodes.Status401Unauthorized));

        context.HttpContext.Items[SessionKey] = session;
        return next(context);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DashboardSession Session(HttpContext context) => (DashboardSession)context.Items[SessionKey]!;

    private static string Caller(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private void SetCookie(HttpContext context, DashboardSession session) =>
        context.Response.Cookies.Append(CookieName, session.Token,
            CookieOptions(session.CreatedAtUtc + options.SessionLifetime));

    private static CookieOptions CookieOptions(DateTimeOffset expires) => new()
    {
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
        IsEssential = true
    };

    private static byte[] Resource(string name)
    {
        using var stream = typeof(DashboardRoutes).Assembly.GetManifestResourceStream($"dashboard/{name}")
                           ?? throw new InvalidOperationException($"The dashboard page '{name}' was not built into {typeof(DashboardRoutes).Assembly.GetName().Name}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

internal sealed record SetupRequest(string? Code, string? Username, string? Password);

internal sealed record SignInRequest(string? Username, string? Password);

internal sealed record PasswordRequest(string? CurrentPassword, string? NewPassword);
