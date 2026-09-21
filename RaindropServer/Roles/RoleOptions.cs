namespace RaindropServer.Roles;

/// <summary>Server-owner-tunable rules for roles.</summary>
public sealed class RoleOptions
{
    /// <summary>
    /// What the built-in <see cref="RoleKind.Everyone"/> role is created with, on a server that has
    /// never had one. Only the seed: once the role exists, its permissions are whatever the server's
    /// administrators have since made them, and changing this setting does not reach back and
    /// overwrite their decision. Permissions added to this build after a server was first seeded reach
    /// its <c>everyone</c> role once, through <see cref="PermissionRollouts"/>, instead.
    /// </summary>
    public Permission DefaultMemberPermissions { get; set; } =
        Permission.ViewChannel | Permission.SendMessages | Permission.CreateChannels |
        Permission.Connect | Permission.Speak;

    /// <summary>
    /// Whether creating a channel makes you its moderator: able to rename it, delete it, and delete
    /// other members' messages in it, whatever your roles say. On by default, because it is the rule
    /// a server ran on before it had roles and because it keeps a member who can create a channel
    /// from being unable to tidy it up afterwards. Turn it off for a server where roles are meant to
    /// be the only answer to who may do what.
    /// </summary>
    public bool ChannelCreatorModeratesOwnChannels { get; set; } = true;

    /// <summary>
    /// Whether the first UUID to register is granted the <see cref="RoleKind.Owner"/> role. On by
    /// default: a self-hosted server has no account to administer it otherwise, and a fresh server
    /// where nobody can create a role is one nobody can ever configure. Turn it off on a server
    /// whose owner is granted the role from the console instead.
    /// </summary>
    public bool FirstMemberBecomesOwner { get; set; } = true;
}
