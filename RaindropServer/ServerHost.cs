using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RaindropServer;

/// <summary>
/// Hosts the ASP.NET Core server on a dedicated background thread,
/// leaving the calling thread free for other work (e.g. parsing user input).
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _runTask;

    private ServerHost(WebApplication app)
    {
        _app = app;
    }

    /// <summary>
    /// The running server's services, so the process hosting it can reach the same instances its
    /// endpoints use. This is how the owner's console acts on the live server — granting a role from
    /// the terminal has to go through the very <see cref="Roles.RoleService"/> that is answering
    /// requests, or it would be writing behind the running server's back.
    /// </summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// Builds and starts the server on a background thread, listening without TLS.
    /// </summary>
    /// <param name="services">
    /// A configured <see cref="IServiceCollection"/> containing one or more <see cref="IEndpoint"/> registrations
    /// (use <see cref="ServiceCollectionExtensions.AddRaindropServer"/> and
    /// <see cref="ServiceCollectionExtensions.AddEndpoint{T}"/> to set it up).
    /// </param>
    /// <param name="urls">
    /// One or more URLs the server will listen on, e.g. <c>"http://localhost:5000"</c>.
    /// Defaults to <c>"http://localhost:5000"</c> when not specified.
    /// </param>
    /// <returns>A running <see cref="ServerHost"/> instance.</returns>
    public static ServerHost Start(IServiceCollection services, params string[] urls) =>
        Start(services, new ServerHostOptions { Urls = urls.Length == 0 ? ["http://localhost:5000"] : urls });

    /// <summary>
    /// Builds and starts the server on a background thread.
    /// </summary>
    /// <param name="services">
    /// A configured <see cref="IServiceCollection"/> containing one or more <see cref="IEndpoint"/> registrations.
    /// </param>
    /// <param name="options">The addresses to listen on and the certificate to present.</param>
    /// <returns>A running <see cref="ServerHost"/> instance.</returns>
    public static ServerHost Start(IServiceCollection services, ServerHostOptions options)
    {
        options.Validate();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.Urls);

        // Without this the host would take its default 30 seconds to drain, which is longer than
        // anything supervising a container waits before resorting to SIGKILL.
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownTimeout);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // HTTP/2 multiplexes every call over one connection and compresses headers, which for a
            // chat client making many small requests is a bigger win than the payload format itself.
            kestrel.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http1AndHttp2);

            if (options.Certificate is { } certificate)
                kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate);
        });

        // Copy all user-registered services into the web application builder.
        foreach (var descriptor in services)
            builder.Services.Add(descriptor);

        builder.Logging.ClearProviders();

        var app = builder.Build();

        // The event stream is a WebSocket; the middleware pings idle ones so that proxies and NAT
        // boxes do not quietly drop them.
        var events = app.Services.GetService<Events.EventOptions>() ?? new Events.EventOptions();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = events.KeepAliveInterval });

        // Map every registered IEndpoint implementation.
        var endpoints = app.Services.GetServices<IEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var captured = endpoint;

            // Over HTTP/2 a WebSocket is opened with an extended CONNECT rather than a GET upgrade, so
            // a GET route answers both — which is what lets the event stream share the one multiplexed
            // connection a client already has open for everything else.
            string[] methods = captured.Method == HttpMethod.Get
                ? [HttpMethod.Get.Method, HttpMethod.Connect.Method]
                : [captured.Method.Method];

            app.MapMethods(
                captured.Route,
                methods,
                (HttpContext ctx, CancellationToken ct) => captured.HandleAsync(ctx, ct));
        }

        var host = new ServerHost(app);
        host.StartBackground();
        return host;
    }

    private void StartBackground()
    {
        _runTask = Task.Run(async () =>
        {
            try
            {
                await _app.StartAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                // Surface a failure to bind the port to whoever disposes the host, rather than
                // leaving them waiting on a server that never came up.
                _started.TrySetException(ex);
                throw;
            }

            _started.TrySetResult();

            // Waits until something asks the host to stop — DisposeAsync, normally — and then
            // performs the stop itself. It is deliberately the only thing that stops the host: a
            // second, concurrent StopAsync would run two shutdowns over the same hosted services.
            await _app.WaitForShutdownAsync();
        });
    }

    /// <summary>
    /// Completes once the server is listening, or throws the reason it could not — a port already in
    /// use, most often. Startup happens on the background thread, so without awaiting this a caller
    /// would announce a server that never came up.
    /// </summary>
    public Task WaitUntilStartedAsync() => _started.Task;

    /// <summary>
    /// Gracefully stops the server, waiting at most <see cref="ServerHostOptions.ShutdownTimeout"/>
    /// for connections to drain.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Startup runs on the background thread, so a host disposed straight away can still be in
        // the middle of it — and stopping a host that has not finished starting throws.
        await _started.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // There is nothing to stop if it never managed to start.
        if (_started.Task.IsCompletedSuccessfully && _runTask is not null)
        {
            // Ask for the shutdown rather than performing it: the background task is already waiting
            // to do exactly that, and awaiting it is how we know the host has finished stopping.
            _app.Lifetime.StopApplication();
            await _runTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await _cts.CancelAsync();
        await _app.DisposeAsync();
        _cts.Dispose();
    }
}
