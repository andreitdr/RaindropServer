namespace RaindropServer.Configuration;

/// <summary>
/// Helps a mistyped setting name find its way home: <c>voice.livekid</c> is almost certainly
/// <c>voice.livekit</c>, and saying so turns a puzzling default into a one-word fix.
/// </summary>
public static class ConfigurationNames
{
    /// <summary>
    /// The candidate <paramref name="name"/> was most likely meant to be, or null when nothing is
    /// close enough to be worth guessing at.
    /// </summary>
    /// <remarks>
    /// "Close enough" is a third of the name's length, so a short name tolerates one wrong letter and
    /// a long one a few. Guessing too eagerly is worse than not guessing: it sends the owner to
    /// rename a setting that was never the problem.
    /// </remarks>
    public static string? Suggest(string name, IEnumerable<string> candidates)
    {
        var budget = Math.Max(1, name.Length / 3);
        string? best = null;

        foreach (var candidate in candidates)
        {
            var distance = Distance(name, candidate);

            if (distance > budget)
                continue;

            budget = distance;
            best = candidate;
        }

        return best;
    }

    /// <summary>Levenshtein distance, case-insensitively, over two rows rather than a whole matrix.</summary>
    private static int Distance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;

            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = char.ToLowerInvariant(left[row - 1]) == char.ToLowerInvariant(right[column - 1])
                    ? previous[column - 1]
                    : previous[column - 1] + 1;

                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
