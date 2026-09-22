using RaindropServer.Channels;
using RaindropServer.Roles;

namespace RaindropServer.E2ee;

/// <summary>
/// Decides who belongs in a channel's MLS group. The delivery service asks it before handing anybody
/// a group's traffic, before accepting their commit, and when it tells members who should be in the
/// group (<c>groups/roster</c>). Registered with <c>TryAdd</c>, so a plugin can replace it, for
/// example to keep a channel's group to a subset of the people who can see it.
/// </summary>
/// <remarks>
/// This is access control, which the server is trusted with anyway: it already decides who may see a
/// channel. What end-to-end encryption adds is that clients show every device that joins a group,
/// so a server that let somebody in cannot do it unnoticed.
/// </remarks>
public interface IE2eeMembershipPolicy
{
    Task<bool> IsAllowedAsync(Guid userId, ChannelRecord channel, CancellationToken cancellationToken);
}

/// <summary>
/// The default policy: a text channel's group is everyone who can see it; a voice channel's is
/// everyone who can see it and connect.
/// </summary>
public sealed class ChannelPermissionMembershipPolicy : IE2eeMembershipPolicy
{
    private readonly IPermissionResolver _permissions;

    public ChannelPermissionMembershipPolicy(IPermissionResolver permissions) => _permissions = permissions;

    public async Task<bool> IsAllowedAsync(Guid userId, ChannelRecord channel, CancellationToken cancellationToken)
    {
        var required = channel.Type == ChannelType.Voice
            ? Permission.ViewChannel | Permission.Connect
            : Permission.ViewChannel;

        return (await _permissions.GetAsync(userId, channel, cancellationToken)).Allows(required);
    }
}
