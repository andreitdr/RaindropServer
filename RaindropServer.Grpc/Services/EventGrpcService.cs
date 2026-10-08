using Grpc.Core;
using RaindropServer.Events;
using RaindropServer.Grpc.Events;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Grpc.Services;

/// <summary>
/// The push channel. Each call is one <see cref="GrpcEventConnection"/>, registered with the
/// <see cref="IEventConnectionRegistry"/> for as long as it is open — which sends <c>ready</c> first,
/// closes it when its session ends, and counts it towards its member being online.
/// </summary>
public sealed class EventGrpcService : Proto.EventService.EventServiceBase
{
    private readonly IEventConnectionRegistry _registry;
    private readonly ProtobufEventEncoder _encoder;
    private readonly EventOptions _options;

    public EventGrpcService(IEventConnectionRegistry registry, ProtobufEventEncoder encoder, EventOptions options)
    {
        _registry = registry;
        _encoder = encoder;
        _options = options;
    }

    public override async Task Connect(
        IAsyncStreamReader<Proto.ConnectRequest> requestStream,
        IServerStreamWriter<Proto.ConnectResponse> responseStream,
        ServerCallContext context)
    {
        var connection = new GrpcEventConnection(context.Session(), _encoder, _options);

        using (_registry.Register(connection))
            await connection.RunAsync(requestStream, responseStream, _registry, context.CancellationToken);
    }
}
