using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    /// services use. This is how the owner's console acts on the live server — granting a role from
    /// the terminal has to go through the very <see cref="Roles.RoleService"/> that is answering
    /// requests, or it would be writing behind the running server's back.
    /// </summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// Builds and starts the server on a background thread.
    /// </summary>
    /// <param name="services">
    /// A configured <see cref="IServiceCollection"/>: the domain from
    /// <see cref="ServiceCollectionExtensions.AddRaindropServer"/>, and at least one
    /// <see cref="IRouteContributor"/> to serve it (the gRPC module's <c>AddRaindropGrpc</c>).
    /// </param>
    /// <param name="options">The addresses to listen on and the certificate to present.</param>
    /// <returns>A running <see cref="ServerHost"/> instance.</returns>
    public static ServerHost Start(IServiceCollection services, ServerHostOptions options)
    {
        options.Validate();

        var host = new ServerHost(Build(services, options));
        host.StartBackground();
        return host;
    }

    /// <summary>
    /// Builds the application without starting it: Kestrel configured from <paramref name="options"/>,
    /// every registered service copied in, and every <see cref="IRouteContributor"/> mapped.
    /// </summary>
    /// <param name="services">The services to run, as for <see cref="Start"/>.</param>
    /// <param name="options">The addresses to listen on and the certificate to present.</param>
    /// <param name="configure">
    /// A last word on the builder — how a test swaps Kestrel for an in-memory server. The options are
    /// not validated here, so a test needs no certificate.
    /// </param>
    public static WebApplication Build(
        IServiceCollection services, ServerHostOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.Urls);

        // Without this the host would take its default 30 seconds to drain, which is longer than
        // anything supervising a container waits before resorting to SIGKILL.
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownTimeout);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // HTTP/2 carries gRPC — every call and stream multiplexed over one connection. HTTP/1.1 stays
            // for the one thing that needs it: the voice signalling relay's WebSocket, which LiveKit's
            // client SDK opens as an ordinary upgrade. ALPN picks per connection.
            kestrel.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http1AndHttp2);

            if (options.Certificate is { } certificate)
                kestrel.ConfigureHttpsDefaults(https =>
                {
                    https.ServerCertificate = certificate;
                    https.SslProtocols = options.SslProtocols;

                    // Where TLS 1.2 is allowed, hold it to what 1.3 guarantees anyway: an ephemeral
                    // key exchange (forward secrecy) and an AEAD cipher. Only OpenSSL lets .NET choose
                    // cipher suites; elsewhere the operating system's defaults stand.
                    if (options.SslProtocols.HasFlag(SslProtocols.Tls12))
                        https.OnAuthenticate = (_, ssl) =>
                        {
                            if (OperatingSystem.IsLinux())
                                ssl.CipherSuitesPolicy = ModernCipherSuites;
                        };
                });
        });

        // Copy all user-registered services into the web application builder.
        foreach (var descriptor in services)
            builder.Services.Add(descriptor);

        builder.Logging.ClearProviders();
        configure?.Invoke(builder);

        var app = builder.Build();

        foreach (var contributor in app.Services.GetServices<IRouteContributor>())
            contributor.Map(app);

        return app;
    }

    [SupportedOSPlatform("linux")]
    private static CipherSuitesPolicy ModernCipherSuites => new(
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256
    ]);

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
