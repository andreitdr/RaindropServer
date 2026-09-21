namespace RaindropServer.Channels.Voice;

/// <summary>
/// Members muted in every voice call on this server by a moderator — "mute for everyone". Persisted,
/// unlike the calls themselves: a mute is a moderation decision, and a restart must not undo it.
/// Implemented once per database module — see <see cref="Data.IDatabaseDriver"/>.
/// </summary>
/// <remarks>
/// Keyed by member, not by channel. The mute follows them into every call until a moderator lifts it,
/// which is why setting one needs <c>mute_members</c> server-wide rather than in one channel.
/// </remarks>
public interface IVoiceMuteStore
{
    /// <summary>The mute on a member, or null when they are not muted.</summary>
    Task<VoiceMuteRecord?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Mutes a member. Returns false, changing nothing, when they already were.</summary>
    Task<bool> AddAsync(VoiceMuteRecord mute, CancellationToken cancellationToken);

    /// <summary>Lifts a member's mute. Returns false when they were not muted.</summary>
    Task<bool> RemoveAsync(Guid userId, CancellationToken cancellationToken);
}

/// <param name="UserId">The member muted.</param>
/// <param name="MutedBy">The moderator who muted them.</param>
/// <param name="MutedAtUtc">When.</param>
public sealed record VoiceMuteRecord(Guid UserId, Guid MutedBy, DateTime MutedAtUtc);
