namespace RaindropServer.Channels;

// ── Shared shapes ────────────────────────────────────────────────────────────

/// <summary>
/// A channel as a client sees it, whatever kind it is. What you can do in it is answered here too,
/// so a client can grey out the delete button without guessing at the server's rules.
/// </summary>
/// <param name="ChannelId">The server-generated id to pass back on every call about this channel.</param>
/// <param name="Type">The channel type: <c>text</c> or <c>voice</c>.</param>
/// <param name="Name">The channel name, unique on this server.</param>
/// <param name="CreatedBy">The UUID of the member who created it.</param>
/// <param name="CreatedAtUtc">When it was created.</param>
/// <param name="YourPermissions">
/// What the calling member may do here, as <see cref="Roles.PermissionNames"/> values.
/// </param>
public record ChannelSummary(
    Guid ChannelId,
    string Type,
    string Name,
    Guid CreatedBy,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> YourPermissions);

// ── Requests ─────────────────────────────────────────────────────────────────

/// <param name="Type">The kind of channel to create: <c>text</c> or <c>voice</c>.</param>
/// <param name="Name">The name to claim; must be unused on this server.</param>
public record CreateChannelRequest(string Type, string Name);

/// <param name="ChannelId">The channel being asked about.</param>
public record ChannelRequest(Guid ChannelId);

/// <param name="ChannelId">The channel to rename.</param>
/// <param name="Name">The new name; must be unused on this server.</param>
public record RenameChannelRequest(Guid ChannelId, string Name);

// ── Responses ────────────────────────────────────────────────────────────────

/// <param name="Channels">Every channel the caller may see, ordered by name.</param>
public record ChannelListResponse(IReadOnlyList<ChannelSummary> Channels);

/// <param name="ChannelId">The channel that was deleted.</param>
/// <param name="Message">A human-readable summary of what went with it.</param>
public record ChannelDeletedResponse(Guid ChannelId, string Message);
