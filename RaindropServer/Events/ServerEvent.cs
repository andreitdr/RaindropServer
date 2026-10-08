namespace RaindropServer.Events;

/// <summary>
/// One frame on the event stream: what happened, and the thing it happened to.
/// </summary>
/// <remarks>
/// Generic rather than carrying an <c>object</c>, so a transport's <see cref="IEventEncoder"/> sees the
/// payload's declared type. On the gRPC stream it becomes one case of <c>raindrop.v1.ServerEvent</c>,
/// chosen by <paramref name="Type"/>.
/// </remarks>
/// <param name="Type">One of the <see cref="ServerEventTypes"/> names.</param>
/// <param name="Data">The payload; its shape is fixed by <paramref name="Type"/>.</param>
public sealed record ServerEvent<T>(string Type, T Data);

/// <summary>
/// The names of every event this server sends. Inside the server, events travel by name, which is what
/// lets a plugin add its own; each built-in name is one case of the protocol's <c>ServerEvent</c>, and a
/// client too old to know a case ignores it.
/// </summary>
public static class ServerEventTypes
{
    /// <summary>The stream is open. Sent first, once; a client resynchronises after it.</summary>
    public const string Ready = "ready";

    /// <summary>A message was posted. Data: <c>ChannelMessage</c>.</summary>
    public const string MessageCreated = "message.created";

    /// <summary>A message was deleted. Data: the tombstone, a <c>ChannelMessage</c> with no content.</summary>
    public const string MessageDeleted = "message.deleted";

    /// <summary>A channel was created. Data: <c>ChannelSummary</c>, with the recipient's own permissions.</summary>
    public const string ChannelCreated = "channel.created";

    /// <summary>A channel was renamed. Data: <c>ChannelSummary</c>, with the recipient's own permissions.</summary>
    public const string ChannelRenamed = "channel.renamed";

    /// <summary>A channel and everything in it were deleted. Data: <see cref="ChannelDeletedEvent"/>.</summary>
    public const string ChannelDeleted = "channel.deleted";

    /// <summary>Somebody registered or rejoined. Data: <see cref="MemberEvent"/>.</summary>
    public const string MemberJoined = "member.joined";

    /// <summary>A member changed their username. Data: <see cref="MemberEvent"/>.</summary>
    public const string MemberRenamed = "member.renamed";

    /// <summary>A member left the server, preserving or erasing their data. Data: <see cref="MemberEvent"/>.</summary>
    public const string MemberLeft = "member.left";

    /// <summary>A member came online or went offline. Data: <see cref="MemberPresenceEvent"/>.</summary>
    public const string MemberPresence = "member.presence";

    /// <summary>A member was given or lost a role. Data: <see cref="MemberRolesEvent"/>.</summary>
    public const string MemberRoles = "member.roles";

    /// <summary>A role was created. Data: <c>RoleSummary</c>.</summary>
    public const string RoleCreated = "role.created";

    /// <summary>A role was renamed, moved, or given different permissions. Data: <c>RoleSummary</c>.</summary>
    public const string RoleUpdated = "role.updated";

    /// <summary>A role was deleted and taken away from everyone. Data: <see cref="RoleDeletedEvent"/>.</summary>
    public const string RoleDeleted = "role.deleted";

    /// <summary>
    /// Somebody joined a voice call, or their state in it changed (muted, deafened, connected). Data:
    /// <c>VoiceParticipant</c>, the whole state — merge on channel and user.
    /// </summary>
    public const string VoiceState = "voice.state";

    /// <summary>Somebody is no longer in a voice call. Data: <c>VoiceLeftEvent</c>, with why.</summary>
    public const string VoiceLeft = "voice.left";
}

// ── Payloads that have no call's response to reuse ───────────────────────────

/// <param name="UserId">The UUID of the connection's own member.</param>
/// <param name="ProtocolVersion">The Raindrop protocol this server speaks, as in the handshake.</param>
/// <param name="HeartbeatInterval">How often the client should send a heartbeat on its stream.</param>
public record ReadyEvent(Guid UserId, string ProtocolVersion, TimeSpan HeartbeatInterval);

/// <param name="ChannelId">The channel that no longer exists.</param>
public record ChannelDeletedEvent(Guid ChannelId);

/// <param name="UserId">The member it is about.</param>
/// <param name="Username">Their username now — null once they have left and it has been erased.</param>
public record MemberEvent(Guid UserId, string? Username);

/// <param name="UserId">The member it is about.</param>
/// <param name="Presence">A <c>PresenceStatusNames</c> value: <c>online</c> or <c>offline</c>.</param>
public record MemberPresenceEvent(Guid UserId, string Presence);

/// <param name="UserId">The member whose roles changed.</param>
/// <param name="RoleId">The role given or taken away.</param>
/// <param name="Assigned">True when they now hold it, false when they no longer do.</param>
public record MemberRolesEvent(Guid UserId, Guid RoleId, bool Assigned);

/// <param name="RoleId">The role that no longer exists.</param>
public record RoleDeletedEvent(Guid RoleId);
