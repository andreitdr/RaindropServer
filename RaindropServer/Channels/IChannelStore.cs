namespace RaindropServer.Channels;

/// <summary>
/// Persistence for channels themselves. Like <see cref="Users.IUserStore"/> this is implemented once
/// per database module; what lives inside a channel is stored by that channel type's own module.
/// </summary>
public interface IChannelStore
{
    /// <summary>Finds a channel by its id, or null when no such channel exists here.</summary>
    Task<ChannelRecord?> FindByIdAsync(Guid channelId, CancellationToken cancellationToken);

    /// <summary>Finds a channel by its normalized (lower-cased) name, or null when the name is unclaimed.</summary>
    Task<ChannelRecord?> FindByNameAsync(string nameNormalized, CancellationToken cancellationToken);

    /// <summary>Every channel on this server, ordered by name. Filtering by who may see them is the caller's job.</summary>
    Task<IReadOnlyList<ChannelRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a brand-new channel row.</summary>
    Task InsertAsync(ChannelRecord channel, CancellationToken cancellationToken);

    /// <summary>Overwrites every mutable column of an existing row, matched on <see cref="ChannelRecord.ChannelId"/>.</summary>
    Task UpdateAsync(ChannelRecord channel, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the channel row. The contents are removed first, by the module for the channel's type
    /// — see <see cref="IChannelModule.DeleteContentsAsync"/>.
    /// </summary>
    Task DeleteAsync(Guid channelId, CancellationToken cancellationToken);
}
