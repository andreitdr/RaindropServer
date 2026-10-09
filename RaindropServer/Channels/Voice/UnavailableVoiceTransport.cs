namespace RaindropServer.Channels.Voice;

/// <summary>
/// The transport on a server with no media server configured. Voice channels can still be created,
/// listed and deleted — they are ordinary channels — but nobody can join one.
/// </summary>
/// <remarks>
/// Registered with <c>TryAdd</c> by <see cref="ServiceCollectionExtensions.AddRaindropVoice"/>, so any
/// real transport registered before it wins.
/// </remarks>
public sealed class UnavailableVoiceTransport : IVoiceTransport
{
    public string Name => "none";

    public bool IsAvailable => false;

    public Task<VoiceConnection> CreateConnectionAsync(VoiceGrant grant, CancellationToken cancellationToken) =>
        throw VoiceService.Unavailable();

    public Task SetPublishAsync(
        Guid channelId, Guid userId, bool canSpeak, bool canPublishCamera, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DisconnectAsync(Guid channelId, Guid userId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task CloseCallAsync(Guid channelId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<TransportCall>> ListCallsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TransportCall>>([]);
}
