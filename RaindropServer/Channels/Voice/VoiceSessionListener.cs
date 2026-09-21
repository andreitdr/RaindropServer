using RaindropServer.Users;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// Ends a member's call when the session that joined it ends — logging out, changing password,
/// leaving the server. Only that session's call: logging out on a phone does not end a call on the
/// desktop.
/// </summary>
/// <remarks>
/// Queues the check rather than making it, because listeners run inside the request that changed the
/// sessions. A session that lapses quietly is never announced; <see cref="VoiceService.ReconcileAsync"/>
/// notices it on its next pass instead.
/// </remarks>
public sealed class VoiceSessionListener : ISessionListener
{
    private readonly VoiceService _voice;
    private readonly VoiceWorkQueue _work;

    public VoiceSessionListener(VoiceService voice, VoiceWorkQueue work)
    {
        _voice = voice;
        _work = work;
    }

    public Task OnSessionsChangedAsync(Guid userId, CancellationToken cancellationToken)
    {
        _work.Enqueue(ct => _voice.ReevaluateAsync(userId, ct));
        return Task.CompletedTask;
    }
}
