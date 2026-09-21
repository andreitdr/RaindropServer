using RaindropServer.Events;
using RaindropServer.Roles;

namespace RaindropServer.Channels;

/// <summary>
/// Who may hear about something in a channel: exactly those who may see it.
/// </summary>
/// <remarks>
/// Lives apart from <see cref="ChannelService.AudienceFor"/>, which is the usual way to reach it, so
/// that a channel module — which <see cref="ChannelService"/> itself depends on, and so cannot depend
/// on it back — can address the same audience with nothing more than the resolver.
/// </remarks>
public static class ChannelAudience
{
    /// <summary>The members allowed to see <paramref name="channel"/>, as an event audience.</summary>
    public static EventAudience For(IPermissionResolver permissions, ChannelRecord channel) =>
        async (userId, cancellationToken) =>
            (await permissions.GetAsync(userId, channel, cancellationToken)).Allows(Permission.ViewChannel);
}
