namespace RaindropServer.Roles;

/// <summary>The wire names for <see cref="RoleKind"/>, so a client knows which roles it must not offer to delete.</summary>
public static class RoleKindNames
{
    public const string Custom = "custom";
    public const string Everyone = "everyone";
    public const string Owner = "owner";

    public static string Of(RoleKind kind) => kind switch
    {
        RoleKind.Custom => Custom,
        RoleKind.Everyone => Everyone,
        RoleKind.Owner => Owner,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown role kind.")
    };
}
