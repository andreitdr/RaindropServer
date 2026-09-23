using System.Diagnostics.CodeAnalysis;

namespace RaindropServer.Configuration;

/// <summary>How much a <see cref="ConfigurationProblem"/> matters.</summary>
public enum ConfigurationSeverity
{
    /// <summary>The server runs, but the setting is probably not what its owner meant.</summary>
    Warning,

    /// <summary>The server cannot run with this setting as it stands.</summary>
    Error
}

/// <summary>One thing wrong with the server owner's configuration.</summary>
/// <param name="Setting">
/// The dotted path of the setting, spelled the way it is written in <c>raindrop.json</c> —
/// <c>voice.livekit.apiSecret</c> — so the owner can go straight to the line.
/// </param>
/// <param name="Severity">Whether this stops the server or is only worth saying.</param>
/// <param name="Message">What is wrong, in the owner's terms rather than the code's.</param>
/// <param name="Remedy">What to do about it, when there is something concrete to say.</param>
public sealed record ConfigurationProblem(
    string Setting,
    ConfigurationSeverity Severity,
    string Message,
    string? Remedy = null)
{
    /// <summary>The problem on one line, the way the console prints it.</summary>
    public override string ToString() =>
        $"{(Severity == ConfigurationSeverity.Error ? "error" : "warning")}: {Setting}: {Message}" +
        (Remedy is null ? "" : $" {Remedy}");
}

/// <summary>
/// What every component writes into while the server's configuration is checked at startup, so that
/// an owner with three typos is told about all three at once rather than one per restart.
/// </summary>
/// <remarks>
/// A component validates its own settings — it is the only thing that knows what they mean — and is
/// handed a <see cref="Section"/> of this report scoped to where those settings live, so it can name
/// <c>apiSecret</c> without knowing that the file calls it <c>voice.livekit.apiSecret</c>. The same
/// seam is what a plugin validates through: give its options a <c>Validate(ConfigurationReport)</c>
/// and register an <see cref="IStartupCheck"/> for anything that can only be found out by asking.
/// <para>
/// Nothing here throws. Collecting is the point: the caller decides when to stop, normally after
/// printing everything found.
/// </para>
/// </remarks>
public sealed class ConfigurationReport
{
    private readonly List<ConfigurationProblem> _problems;
    private readonly string _prefix;

    /// <summary>A report for a whole configuration, with settings named from its root.</summary>
    public ConfigurationReport() : this([], "")
    {
    }

    private ConfigurationReport(List<ConfigurationProblem> problems, string prefix)
    {
        _problems = problems;
        _prefix = prefix;
    }

    /// <summary>
    /// The same report, but naming settings relative to <paramref name="name"/> — what a subsystem
    /// is handed so that it validates its own section without knowing where it sits.
    /// </summary>
    public ConfigurationReport Section(string name) =>
        new(_problems, string.IsNullOrEmpty(_prefix) ? name : $"{_prefix}.{name}");

    /// <summary>Everything found so far, in the order it was found.</summary>
    public IReadOnlyList<ConfigurationProblem> Problems => _problems;

    /// <summary>Whether anything found stops the server from starting.</summary>
    public bool HasErrors => _problems.Any(problem => problem.Severity == ConfigurationSeverity.Error);

    /// <summary>Whether anything at all was found.</summary>
    public bool HasProblems => _problems.Count > 0;

    /// <summary>Records a setting the server cannot run with.</summary>
    public void Error(string setting, string message, string? remedy = null) =>
        Add(setting, ConfigurationSeverity.Error, message, remedy);

    /// <summary>Records a setting that works but is probably not what the owner meant.</summary>
    public void Warn(string setting, string message, string? remedy = null) =>
        Add(setting, ConfigurationSeverity.Warning, message, remedy);

    /// <summary>
    /// Records an error when a required setting is missing or blank, and says whether it is there —
    /// so a caller can skip the checks that only make sense once it is.
    /// </summary>
    public bool Require(string setting, [NotNullWhen(true)] string? value, string message, string? remedy = null)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return true;

        Error(setting, message, remedy);
        return false;
    }

    /// <summary>Records an error when a number falls outside the range the component can work with.</summary>
    public void Range(string setting, long value, long minimum, long maximum, string? unit = null)
    {
        if (value >= minimum && value <= maximum)
            return;

        var suffix = unit is null ? "" : $" {unit}";
        Error(setting, $"is {value}{suffix}; it must be between {minimum}{suffix} and {maximum}{suffix}.");
    }

    /// <summary>Records an error when a duration falls outside the range the component can work with.</summary>
    public void Range(string setting, TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        if (value >= minimum && value <= maximum)
            return;

        Error(setting, $"is {Describe(value)}; it must be between {Describe(minimum)} and {Describe(maximum)}.");
    }

    /// <summary>Everything found, one problem per line, errors first.</summary>
    public string Describe() =>
        string.Join(Environment.NewLine, Ordered().Select(problem => problem.ToString()));

    /// <summary>Errors first, then warnings, each in the order they were found.</summary>
    public IEnumerable<ConfigurationProblem> Ordered() =>
        _problems.Where(problem => problem.Severity == ConfigurationSeverity.Error)
            .Concat(_problems.Where(problem => problem.Severity == ConfigurationSeverity.Warning));

    /// <summary>
    /// Throws with every error found, for a host that would rather catch one exception than print a
    /// report. Warnings are left alone.
    /// </summary>
    /// <exception cref="InvalidOperationException">At least one error was recorded.</exception>
    public void ThrowIfInvalid()
    {
        var errors = _problems.Where(problem => problem.Severity == ConfigurationSeverity.Error).ToList();

        if (errors.Count == 0)
            return;

        throw new InvalidOperationException(errors.Count == 1
            ? $"{errors[0].Setting}: {errors[0].Message}{(errors[0].Remedy is null ? "" : $" {errors[0].Remedy}")}"
            : $"{errors.Count} settings are wrong:{Environment.NewLine}" +
              string.Join(Environment.NewLine, errors.Select(error => $"  - {error}")));
    }

    private void Add(string setting, ConfigurationSeverity severity, string message, string? remedy)
    {
        var path = string.IsNullOrEmpty(_prefix)
            ? setting
            : string.IsNullOrEmpty(setting) ? _prefix : $"{_prefix}.{setting}";

        _problems.Add(new ConfigurationProblem(path, severity, message, remedy));
    }

    private static string Describe(TimeSpan value) => value switch
    {
        { TotalDays: >= 1 } => $"{value.TotalDays:0.##} day(s)",
        { TotalHours: >= 1 } => $"{value.TotalHours:0.##} hour(s)",
        { TotalMinutes: >= 1 } => $"{value.TotalMinutes:0.##} minute(s)",
        _ => $"{value.TotalSeconds:0.##} second(s)"
    };
}
