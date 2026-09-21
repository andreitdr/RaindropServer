namespace RaindropServer.Roles;

/// <summary>
/// A set of permissions that a newer build hands to the built-in <c>everyone</c> role once, on a
/// server that was seeded before those permissions existed.
/// </summary>
/// <param name="Id">
/// Stable, never reused: it is the key recorded in <c>raindrop_permission_rollouts</c> once the grant
/// has been made.
/// </param>
/// <param name="GrantToEveryone">What the <c>everyone</c> role gains, OR-ed into what it already has.</param>
public sealed record PermissionRollout(string Id, Permission GrantToEveryone);

/// <summary>
/// Every rollout this build knows, in the order they were introduced.
/// </summary>
/// <remarks>
/// <see cref="RoleOptions.DefaultMemberPermissions"/> only seeds a brand-new server and deliberately
/// never reaches back to overwrite what administrators have since made the role. That is right for
/// permissions an owner has already had the chance to decide on, and wrong for ones that did not exist
/// yet: an existing server upgraded to a build with voice channels would otherwise have nobody but
/// its administrators able to join one. So each new permission is offered here exactly once — applied
/// on the first start of the build that introduced it, recorded, and never applied again — which
/// leaves an owner free to take it away afterwards. A fresh server records every rollout as applied
/// without granting anything, since its seed already reflects this build.
/// <para>
/// The list lives in code rather than configuration on purpose: the shipped <c>raindrop.json</c>
/// names default permissions explicitly, and an owner's copy of it predating a permission must not be
/// what decides whether their members ever receive it.
/// </para>
/// </remarks>
public static class PermissionRollouts
{
    public static IReadOnlyList<PermissionRollout> All { get; } =
    [
        new("voice", Permission.Connect | Permission.Speak)
    ];
}
