using RaindropServer.Channels;

namespace RaindropServer.Roles;

/// <summary>
/// Decides what a member may do. Every permission check on this server goes through this one method,
/// so what a role means is defined in exactly one place.
/// </summary>
public interface IPermissionResolver
{
    /// <summary>
    /// Resolves what <paramref name="userId"/> may do in <paramref name="channel"/>, or across the
    /// server when <paramref name="channel"/> is null.
    /// </summary>
    /// <param name="userId">The UUID of the member being asked about.</param>
    /// <param name="channel">
    /// The channel in question, or null for a server-wide check. Roles are server-wide, so this only
    /// changes the answer where a rule is about a particular channel — moderating one you created.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    /// The granted permissions, already expanded so that an administrator holds everything, or
    /// <see cref="Permission.None"/> for anyone who is not currently a member of this server.
    /// </returns>
    Task<Permission> GetAsync(Guid userId, ChannelRecord? channel, CancellationToken cancellationToken);
}
