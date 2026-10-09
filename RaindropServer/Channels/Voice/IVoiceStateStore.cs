namespace RaindropServer.Channels.Voice;

/// <summary>
/// Who is in which voice call right now, and their state in it. Never persisted by the default
/// implementation: a call cannot outlive the process that admitted its participants, since their
/// sessions do not either.
/// </summary>
/// <remarks>
/// The operations are whole decisions rather than reads and writes, so that capacity and "one call
/// per member" are enforced atomically — two members racing for the last place, or one member joining
/// from two devices at once, cannot both win. A replacement (a store shared by several processes,
/// say) has to keep that property.
/// </remarks>
public interface IVoiceStateStore
{
    /// <summary>
    /// Puts a member in a call. Moves them out of any other call first, and replaces their own earlier
    /// record if they were already in this one — the second device wins, without taking a second
    /// place. Refuses, changing nothing, when the call already holds <paramref name="capacity"/> other
    /// members.
    /// </summary>
    ValueTask<VoiceJoinOutcome> JoinAsync(
        VoiceParticipantRecord participant, int capacity, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a member out of whichever call they are in, as long as <paramref name="predicate"/> (if
    /// given) holds for their record — which is how a late "leave" from one device is kept from ending
    /// the call another device has since joined. Returns the record removed, or null when there was
    /// nothing to remove.
    /// </summary>
    ValueTask<VoiceParticipantRecord?> LeaveAsync(
        Guid userId, Func<VoiceParticipantRecord, bool>? predicate, CancellationToken cancellationToken);

    /// <summary>
    /// Changes a member's record in place, as long as <paramref name="predicate"/> (if given) still
    /// holds for it. Returns the new record, or null when there was none to change.
    /// </summary>
    ValueTask<VoiceParticipantRecord?> UpdateAsync(
        Guid userId,
        Func<VoiceParticipantRecord, VoiceParticipantRecord> update,
        Func<VoiceParticipantRecord, bool>? predicate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Turns a member's camera on or off in whichever call they are in, as long as
    /// <paramref name="predicate"/> (if given) still holds for their record. Refuses, changing
    /// nothing, when turning it on and <paramref name="cameraCapacity"/> other members in that call
    /// already have theirs on, or when they may not use their camera at all.
    /// </summary>
    /// <remarks>
    /// Its own operation rather than a <see cref="UpdateAsync"/> call because the cap has to be
    /// counted and the record written together — two members racing for the last camera place must
    /// not both win, for the same reason capacity lives inside <see cref="JoinAsync"/>.
    /// </remarks>
    ValueTask<VoiceCameraOutcome> SetCameraAsync(
        Guid userId,
        bool on,
        int cameraCapacity,
        Func<VoiceParticipantRecord, bool>? predicate,
        CancellationToken cancellationToken);

    /// <summary>The call a member is in, or null when they are in none.</summary>
    ValueTask<VoiceParticipantRecord?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Everybody in one call, or in every call when <paramref name="channelId"/> is null, by join time.</summary>
    ValueTask<IReadOnlyList<VoiceParticipantRecord>> ListAsync(Guid? channelId, CancellationToken cancellationToken);

    /// <summary>Empties one call, returning who was in it.</summary>
    ValueTask<IReadOnlyList<VoiceParticipantRecord>> RemoveChannelAsync(Guid channelId, CancellationToken cancellationToken);
}

/// <summary>One member in one voice call.</summary>
public sealed record VoiceParticipantRecord
{
    public required Guid ChannelId { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>
    /// The session that joined. Kept so that logging out on one device ends that device's call and no
    /// other, and so that a session which quietly lapses is noticed on the next reconcile.
    /// </summary>
    public required string SessionToken { get; init; }

    /// <summary>They muted their own microphone.</summary>
    public bool SelfMute { get; init; }

    /// <summary>They stopped hearing the call (and, with it, muted themselves).</summary>
    public bool SelfDeaf { get; init; }

    /// <summary>A moderator muted them for everyone — see <see cref="IVoiceMuteStore"/>.</summary>
    public bool ServerMute { get; init; }

    /// <summary>They may listen but not talk: they lack the <c>speak</c> permission.</summary>
    public bool Suppressed { get; init; }

    /// <summary>They asked for their camera to be on, and were allowed it.</summary>
    public bool CameraOn { get; init; }

    /// <summary>
    /// They may not turn their camera on: they lack the <c>video</c> permission, or every camera
    /// place in the call is taken. The counterpart of <see cref="Suppressed"/> for the camera.
    /// </summary>
    public bool CameraSuppressed { get; init; }

    /// <summary>
    /// The media server has confirmed their connection. False from the moment they are admitted until
    /// the reconciler sees them in the call; a record that stays unconfirmed is dropped.
    /// </summary>
    public bool Connected { get; init; }

    public required DateTime JoinedAtUtc { get; init; }

    /// <summary>
    /// The last time the media server reported them in the call — their join time until it first
    /// does. A record not seen for <see cref="VoiceOptions.JoinGrace"/> is dropped.
    /// </summary>
    public required DateTime LastSeenUtc { get; init; }

    /// <summary>Whether the media server should accept their microphone.</summary>
    public bool CanSpeak => !ServerMute && !Suppressed;

    /// <summary>
    /// Whether the media server should accept their camera. Unlike <see cref="CanSpeak"/> this also
    /// requires them to have asked: the camera grant follows the camera being on, so a member who
    /// may use their camera but has it off cannot send one.
    /// </summary>
    public bool CanPublishCamera => CameraOn && !CameraSuppressed;
}

/// <summary>What <see cref="IVoiceStateStore.JoinAsync"/> decided.</summary>
/// <param name="Full">The call was full; nothing changed and <paramref name="Participant"/> is null.</param>
/// <param name="MovedFrom">The record they had in another call, which the join ended.</param>
/// <param name="Replaced">Their earlier record in this same call, from another session.</param>
/// <param name="Participant">Their record now.</param>
public readonly record struct VoiceJoinOutcome(
    bool Full,
    VoiceParticipantRecord? MovedFrom,
    VoiceParticipantRecord? Replaced,
    VoiceParticipantRecord? Participant);

/// <summary>What <see cref="IVoiceStateStore.SetCameraAsync"/> decided.</summary>
/// <param name="Full">
/// Every camera place in the call was taken; nothing changed. Never set when turning a camera off.
/// </param>
/// <param name="NotAllowed">
/// They may not use their camera here (<see cref="VoiceParticipantRecord.CameraSuppressed"/>);
/// nothing changed.
/// </param>
/// <param name="Participant">
/// Their record now — unchanged when it already said what was asked for — or null when they were in
/// no call, or the predicate did not hold.
/// </param>
public readonly record struct VoiceCameraOutcome(
    bool Full,
    bool NotAllowed,
    VoiceParticipantRecord? Participant);
