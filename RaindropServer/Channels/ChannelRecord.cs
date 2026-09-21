namespace RaindropServer.Channels;

/// <summary>
/// A channel on this server, as persisted by an <see cref="IChannelStore"/>. Everything here is
/// common to every kind of channel; what a channel holds — messages, a call — belongs to the module
/// for its <see cref="Type"/> and is stored separately.
/// </summary>
public sealed record ChannelRecord
{
    /// <summary>The server-generated id for this channel.</summary>
    public required Guid ChannelId { get; init; }

    /// <summary>Which kind of channel this is, and therefore which module owns its contents.</summary>
    public required ChannelType Type { get; init; }

    /// <summary>The channel name as its creator typed it, e.g. "general-chat".</summary>
    public required string Name { get; init; }

    /// <summary>The lower-cased name the uniqueness constraint is applied to.</summary>
    public required string NameNormalized { get; init; }

    /// <summary>
    /// The UUID of the member who created the channel. They moderate it — renaming, deleting, and
    /// removing other members' messages in it — unless the server owner has turned that off with
    /// <see cref="Roles.RoleOptions.ChannelCreatorModeratesOwnChannels"/>.
    /// </summary>
    public required Guid CreatedBy { get; init; }

    /// <summary>When the channel was created.</summary>
    public required DateTime CreatedAtUtc { get; init; }
}
