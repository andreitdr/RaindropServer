namespace RaindropServer.Channels.Voice;

// ── Shared shapes ────────────────────────────────────────────────────────────

/// <summary>
/// One member in one voice call, as clients see it — the whole state, so a client merges it on
/// (<paramref name="ChannelId"/>, <paramref name="UserId"/>) rather than applying a change.
/// </summary>
/// <param name="ChannelId">The voice channel whose call they are in.</param>
/// <param name="UserId">The member; resolve a name through <c>POST /api/users/lookup</c>.</param>
/// <param name="SelfMute">They muted their own microphone.</param>
/// <param name="SelfDeaf">They stopped hearing the call; implies <paramref name="SelfMute"/>.</param>
/// <param name="ServerMute">A moderator muted them for everyone.</param>
/// <param name="Suppressed">They may listen but not talk: they lack <c>speak</c>.</param>
/// <param name="Connected">
/// The media server has seen them connect. False for the moment between joining and connecting.
/// </param>
/// <param name="JoinedAtUtc">When they joined, which is also the order a call lists them in.</param>
public record VoiceParticipant(
    Guid ChannelId,
    Guid UserId,
    bool SelfMute,
    bool SelfDeaf,
    bool ServerMute,
    bool Suppressed,
    bool Connected,
    DateTime JoinedAtUtc);

// ── Requests ─────────────────────────────────────────────────────────────────

/// <param name="ChannelId">The voice channel to join.</param>
/// <param name="SelfMute">Join already muted, so nobody sees you unmuted for a moment first.</param>
/// <param name="SelfDeaf">Join already deafened.</param>
public record JoinVoiceRequest(Guid ChannelId, bool SelfMute = false, bool SelfDeaf = false);

/// <param name="ChannelId">
/// The call to leave. Named rather than implied, so a late leave from one device cannot end a call
/// another device has since joined.
/// </param>
public record LeaveVoiceRequest(Guid ChannelId);

/// <param name="SelfMute">Whether your microphone is muted.</param>
/// <param name="SelfDeaf">Whether you have stopped hearing the call; implies <paramref name="SelfMute"/>.</param>
public record VoiceSelfStateRequest(bool SelfMute, bool SelfDeaf);

/// <param name="UserId">The member to mute or unmute for everyone.</param>
/// <param name="Muted">True to mute, false to lift the mute.</param>
public record VoiceMuteRequest(Guid UserId, bool Muted);

// ── Responses ────────────────────────────────────────────────────────────────

/// <summary>What a client needs to connect to a call it has just been admitted to.</summary>
/// <param name="ChannelId">The voice channel.</param>
/// <param name="Transport">
/// Which media transport to connect with, e.g. <c>livekit</c>. A client that does not know it cannot
/// join, and says so.
/// </param>
/// <param name="Url">An absolute address to connect to, or null to connect through this server at <paramref name="Path"/>.</param>
/// <param name="Path">The path on this server to connect to when <paramref name="Url"/> is null.</param>
/// <param name="Token">The transport's short-lived credential. Join again for a fresh one.</param>
/// <param name="ExpiresAtUtc">When <paramref name="Token"/> stops opening new connections.</param>
/// <param name="You">Your own state in the call.</param>
/// <param name="Participants">Everybody in the call, you included, by join time.</param>
public record JoinVoiceResponse(
    Guid ChannelId,
    string Transport,
    string? Url,
    string? Path,
    string Token,
    DateTime ExpiresAtUtc,
    VoiceParticipant You,
    IReadOnlyList<VoiceParticipant> Participants);

/// <param name="Left">False when you were not in that call anyway — not an error.</param>
public record LeaveVoiceResponse(bool Left);

/// <param name="UserId">The member.</param>
/// <param name="Muted">Whether they are now muted for everyone.</param>
/// <param name="Changed">False when they already were (or already were not).</param>
public record VoiceMuteResponse(Guid UserId, bool Muted, bool Changed);

/// <summary>Whether voice works here at all, and who is in every call the caller can see.</summary>
/// <param name="Available">False when the server has no media server configured; joining is refused.</param>
/// <param name="Transport">The transport joining would use, e.g. <c>livekit</c>; <c>none</c> when unavailable.</param>
/// <param name="MaximumParticipants">The most members one call holds.</param>
/// <param name="Participants">Everybody in a call in a channel the caller may see, by channel then join time.</param>
public record VoiceStatusResponse(
    bool Available,
    string Transport,
    int MaximumParticipants,
    IReadOnlyList<VoiceParticipant> Participants);

// ── Events ───────────────────────────────────────────────────────────────────

/// <param name="ChannelId">The call they are no longer in.</param>
/// <param name="UserId">The member.</param>
/// <param name="Reason">A <see cref="VoiceLeaveReasons"/> value — an open set; treat unknown ones as <c>left</c>.</param>
public record VoiceLeftEvent(Guid ChannelId, Guid UserId, string Reason);

/// <summary>
/// Why somebody is no longer in a call. Open strings, like event types: a plugin may add its own, and
/// a client shows a generic message for one it does not know.
/// </summary>
public static class VoiceLeaveReasons
{
    /// <summary>They left.</summary>
    public const string Left = "left";

    /// <summary>They joined another call.</summary>
    public const string Moved = "moved";

    /// <summary>Their connection dropped, or never arrived.</summary>
    public const string Disconnected = "disconnected";

    /// <summary>They joined the same call from another device, which took over.</summary>
    public const string Replaced = "replaced";

    /// <summary>They can no longer see the channel, or no longer hold <c>connect</c>.</summary>
    public const string PermissionLost = "permission_lost";

    /// <summary>The session that joined logged out, lapsed or was revoked.</summary>
    public const string SessionEnded = "session_ended";

    /// <summary>The channel was deleted.</summary>
    public const string ChannelDeleted = "channel_deleted";

    /// <summary>The server is restarting, or ended every call for another reason of its own.</summary>
    public const string ServerEnded = "server_ended";
}
