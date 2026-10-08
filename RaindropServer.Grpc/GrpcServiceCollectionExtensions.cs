using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RaindropServer.Events;
using RaindropServer.Grpc.Events;
using RaindropServer.Grpc.Services;

namespace RaindropServer.Grpc;

/// <summary>Registers the gRPC transport: every <c>raindrop.v1</c> service, and the event stream behind them.</summary>
public static class GrpcServiceCollectionExtensions
{
    /// <summary>
    /// The largest message either side may send. Every call is small except attachment frames, which
    /// are <see cref="AttachmentGrpcService.FrameBytes"/>; this leaves room for those and nothing larger.
    /// </summary>
    public const int MaximumMessageBytes = 1024 * 1024;

    /// <summary>
    /// Serves the domain <see cref="ServiceCollectionExtensions.AddRaindropServer"/> registered over gRPC.
    /// Call it after that, before <see cref="ServerHost.Start"/>.
    /// </summary>
    public static IServiceCollection AddRaindropGrpc(this IServiceCollection services)
    {
        services.AddGrpc(options =>
        {
            options.Interceptors.Add<RaindropInterceptor>();
            options.MaxReceiveMessageSize = MaximumMessageBytes;
            options.MaxSendMessageSize = MaximumMessageBytes;

            // The status message is the human half of an error; the details trailer carries the code.
            options.EnableDetailedErrors = false;
        });

        services.TryAddSingleton<ProtobufEventEncoder>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRouteContributor, GrpcRoutes>());

        return services;
    }

    /// <summary>Maps every service. Registered by <see cref="AddRaindropGrpc"/>.</summary>
    private sealed class GrpcRoutes : IRouteContributor
    {
        public void Map(WebApplication app)
        {
            app.MapGrpcService<ServerGrpcService>();
            app.MapGrpcService<UserGrpcService>();
            app.MapGrpcService<MemberGrpcService>();
            app.MapGrpcService<RoleGrpcService>();
            app.MapGrpcService<ChannelGrpcService>();
            app.MapGrpcService<TextChannelGrpcService>();
            app.MapGrpcService<VoiceGrpcService>();
            app.MapGrpcService<DeviceGrpcService>();
            app.MapGrpcService<DeliveryGrpcService>();
            app.MapGrpcService<AttachmentGrpcService>();
            app.MapGrpcService<EventGrpcService>();
        }
    }
}
