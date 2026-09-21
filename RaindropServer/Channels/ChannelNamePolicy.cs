namespace RaindropServer.Channels;

/// <summary>
/// The rules a channel name must satisfy. Names are unique per server — like usernames, and for the
/// same reason: a member should be able to say "post it in #general" and mean one place.
/// </summary>
public static class ChannelNamePolicy
{
    public const int MinimumLength = 2;
    public const int MaximumLength = 64;

    private const string Separators = "_.-";

    /// <summary>Lower-cases a name for uniqueness comparisons, so "General" and "general" are one channel.</summary>
    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    /// <summary>
    /// Validates a channel name, returning the trimmed form. Throws an <see cref="ApiException"/>
    /// describing the first rule that was broken.
    /// </summary>
    public static string Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw ApiException.BadRequest("invalid_channel_name", "A channel name is required.");

        var trimmed = name.Trim();

        if (trimmed.Length is < MinimumLength or > MaximumLength)
            throw ApiException.BadRequest("invalid_channel_name",
                $"A channel name must be between {MinimumLength} and {MaximumLength} characters long.");

        // ASCII only, for the same reason usernames are: two channels whose names look identical but
        // are written in different scripts would defeat the uniqueness this enforces.
        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && !Separators.Contains(c))
                throw ApiException.BadRequest("invalid_channel_name",
                    "A channel name may only contain letters, digits, and the characters _ . -");
        }

        if (!char.IsAsciiLetterOrDigit(trimmed[0]) || !char.IsAsciiLetterOrDigit(trimmed[^1]))
            throw ApiException.BadRequest("invalid_channel_name",
                "A channel name must start and end with a letter or a digit.");

        return trimmed;
    }
}
