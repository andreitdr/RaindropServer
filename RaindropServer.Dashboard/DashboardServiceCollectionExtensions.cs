using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RaindropServer.Configuration;
using RaindropServer.Dashboard.Metrics;

namespace RaindropServer.Dashboard;

public static class DashboardServiceCollectionExtensions
{
    /// <summary>
    /// Serves the owner's dashboard. The host must also listen on <see cref="DashboardOptions.Listener"/>
    /// (added to <see cref="ServerHostOptions.Listeners"/>) — without it the dashboard's endpoints answer
    /// on no port at all. Call after <c>AddRaindropServer</c>, whose services it reads.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid — see <see cref="DashboardOptions.Validate"/>.</exception>
    public static IServiceCollection AddRaindropDashboard(this IServiceCollection services, DashboardOptions options)
    {
        var report = new ConfigurationReport();
        options.Validate(report.Section("dashboard"));
        report.ThrowIfInvalid();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.TryAddSingleton(_ => ServerBuildInfo.For(Assembly.GetEntryAssembly() ?? typeof(DashboardOptions).Assembly));

        services.TryAddSingleton<ProcessMetrics>();
        services.TryAddSingleton(_ => DashboardEnvironment.Detect());
        services.TryAddSingleton<DashboardSampler>();
        services.AddHostedService(provider => provider.GetRequiredService<DashboardSampler>());

        services.TryAddSingleton<DashboardAuth>();
        services.TryAddSingleton<DashboardStatusService>();
        services.TryAddSingleton<DashboardRecovery>();
        services.TryAddSingleton<DashboardRoles>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRouteContributor, DashboardRoutes>());

        // An account file that is there but unreadable would refuse every sign-in; said at start instead.
        services.AddStartupCheck<DashboardStartupCheck>();

        return services;
    }
}

/// <summary>Reads the dashboard's account file before the server listens.</summary>
internal sealed class DashboardStartupCheck(DashboardAuth auth) : IStartupCheck
{
    public string Name => "dashboard";

    public Task CheckAsync(ConfigurationReport report, CancellationToken cancellationToken)
    {
        try
        {
            auth.Load();
        }
        catch (InvalidOperationException ex)
        {
            report.Section("dashboard").Error("credentialsPath", ex.Message,
                "Delete the file to set the dashboard up again with a new setup code.");
        }

        return Task.CompletedTask;
    }
}
