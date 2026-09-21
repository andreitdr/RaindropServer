using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// A plugin's say in who may join a call, asked after Raindrop's own checks (the channel is visible,
/// the caller holds <c>connect</c>) and before capacity. None is registered by default; register one
/// with <c>TryAddEnumerable</c> and every one of them runs.
/// </summary>
/// <remarks>
/// Refuse by throwing an <see cref="ApiException"/> — its status and code reach the client unchanged,
/// so pick a code a client can branch on. Anything else thrown refuses the join too, as a server error:
/// a policy that cannot decide must not let somebody in by accident.
/// </remarks>
public interface IVoiceJoinPolicy
{
    Task CheckAsync(VoiceJoinContext context, CancellationToken cancellationToken);
}

/// <param name="Session">The member joining.</param>
/// <param name="Channel">The voice channel.</param>
/// <param name="Permissions">What they hold in it, already expanded.</param>
public sealed record VoiceJoinContext(UserSession Session, ChannelRecord Channel, Permission Permissions);
