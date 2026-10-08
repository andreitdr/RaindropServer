namespace RaindropServer.Channels;

/// <summary>
/// Everything that is specific to one kind of channel, seen from the shared code that does not care
/// which kind it is holding. Registering a module is what makes a <see cref="ChannelType"/> real:
/// <see cref="ChannelService"/> refuses to create a channel of a type no module claims, which is what
/// keeps a type from appearing before the code that runs it exists.
/// </summary>
/// <remarks>
/// A module is deliberately tiny. The interesting work of a channel type — sending messages, joining
/// a call — is its own service and its own calls; this is only the seam the shared channel
/// lifecycle needs.
/// </remarks>
public interface IChannelModule
{
    /// <summary>The channel type this module implements.</summary>
    ChannelType Type { get; }

    /// <summary>
    /// Deletes everything the channel holds, called just before the channel row itself goes. Deleting
    /// a channel deletes the conversation in it; the rule that a message outlives its author applies
    /// to messages, not to channels their owner tore down.
    /// </summary>
    /// <remarks>
    /// The whole <see cref="ChannelDeletion"/> is passed rather than an id so that a module can
    /// preserve what it is about to destroy: the channel's name and who is deleting it are gone from
    /// the record the moment the channel row is.
    /// </remarks>
    Task DeleteContentsAsync(ChannelDeletion deletion, CancellationToken cancellationToken);
}
