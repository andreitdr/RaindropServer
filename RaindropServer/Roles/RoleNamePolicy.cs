namespace RaindropServer.Roles;

/// <summary>
/// The rules a role name must satisfy. Names are unique per server so that "give them Moderator"
/// means one thing — the same reasoning as usernames and channel names, and deliberately looser
/// rules than either: a role name is a label a server owner writes for other people to read, not an
/// identifier anybody types into a mention.
/// </summary>
public static class RoleNamePolicy
{
    public const int MinimumLength = 1;
    public const int MaximumLength = 64;

    /// <summary>Lower-cases a name for uniqueness comparisons, so "Moderator" and "moderator" are one role.</summary>
    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    /// <summary>
    /// Validates a role name, returning the trimmed form. Throws an <see cref="ApiException"/>
    /// describing the first rule that was broken.
    /// </summary>
    public static string Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw ApiException.BadRequest("invalid_role_name", "A role name is required.");

        var trimmed = name.Trim();

        if (trimmed.Length is < MinimumLength or > MaximumLength)
            throw ApiException.BadRequest("invalid_role_name",
                $"A role name must be between {MinimumLength} and {MaximumLength} characters long.");

        // Control characters would let a role name break the layout of anything rendering it, and a
        // name is shown far more often than it is typed.
        foreach (var c in trimmed)
        {
            if (char.IsControl(c))
                throw ApiException.BadRequest("invalid_role_name",
                    "A role name may not contain control characters.");
        }

        return trimmed;
    }
}
