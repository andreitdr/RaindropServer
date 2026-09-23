using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RaindropServer;
using RaindropServer.Channels.Voice;

namespace RaindropServer.Voice.LiveKit;

public static class LiveKitServiceCollectionExtensions
{
    /// <summary>
    /// Carries this server's voice calls on a LiveKit server. Call it <em>before</em>
    /// <c>AddRaindropServer</c>: the voice subsystem only fills in a transport when none is registered
    /// yet, and would otherwise settle on the one that refuses every join.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are incomplete — see <see cref="LiveKitOptions.Validate"/>.</exception>
    public static IServiceCollection AddRaindropLiveKit(
        this IServiceCollection services, Action<LiveKitOptions> configure)
    {
        var options = new LiveKitOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<LiveKitJwt>();
        services.TryAddSingleton<LiveKitRoomClient>();
        services.TryAddSingleton<IVoiceTransport, LiveKitVoiceTransport>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVoiceSignalRelay, LiveKitSignalRelay>());

        // Settings can only say so much on their own; this is what goes and looks before the server
        // starts listening, so a refused secret or an absent sidecar is reported then rather than by
        // the first member who cannot join a call.
        services.AddStartupCheck<LiveKitStartupCheck>();

        return services;
    }
}
