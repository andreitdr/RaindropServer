namespace RaindropServer.Users;

/// <summary>
/// The rules a username must satisfy on this server. Usernames are unique per server only — the
/// same person may well be "alice" here and "alice42" somewhere else.
/// </summary>
public static class UsernamePolicy
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 32;

    private const string Separators = "_.-";

    /// <summary>
    /// Lower-cases a username for uniqueness comparisons, so "Alice" and "alice" cannot both be claimed.
    /// </summary>
    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    /// <summary>
    /// Validates a username, returning the trimmed form. Throws an <see cref="ApiException"/>
    /// describing the first rule that was broken.
    /// </summary>
    public static string Validate(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw ApiException.BadRequest("invalid_username", "A username is required.");

        var trimmed = username.Trim();

        if (trimmed.Length is < MinimumLength or > MaximumLength)
            throw ApiException.BadRequest("invalid_username",
                $"A username must be between {MinimumLength} and {MaximumLength} characters long.");

        // Deliberately ASCII-only: visually identical characters from other scripts would let two
        // users claim names that look the same, which uniqueness is supposed to prevent.
        foreach (var c in trimmed)
        {
            if (!IsAsciiLetterOrDigit(c) && !Separators.Contains(c))
                throw ApiException.BadRequest("invalid_username",
                    "A username may only contain letters, digits, and the characters _ . -");
        }

        if (!IsAsciiLetterOrDigit(trimmed[0]) || !IsAsciiLetterOrDigit(trimmed[^1]))
            throw ApiException.BadRequest("invalid_username",
                "A username must start and end with a letter or a digit.");

        for (var i = 1; i < trimmed.Length; i++)
        {
            if (Separators.Contains(trimmed[i]) && Separators.Contains(trimmed[i - 1]))
                throw ApiException.BadRequest("invalid_username",
                    "A username may not contain two separators in a row.");
        }

        return trimmed;
    }

    private static bool IsAsciiLetterOrDigit(char c) => char.IsAsciiLetterOrDigit(c);
}
