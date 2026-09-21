namespace RaindropServer.Channels;

/// <summary>
/// A channel that is about to be deleted, and who is deleting it. Handed to the
/// <see cref="IChannelModule"/> for the channel's type so it can deal with what the channel holds —
/// including preserving it — while the channel record is still there to describe it.
/// </summary>
/// <param name="Channel">The channel being deleted, as it is right now.</param>
/// <param name="DeletedBy">The UUID of the member deleting it.</param>
/// <param name="DeletedAtUtc">When the deletion was requested.</param>
public sealed record ChannelDeletion(ChannelRecord Channel, Guid DeletedBy, DateTime DeletedAtUtc);
