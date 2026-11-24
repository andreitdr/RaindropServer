using System.Net;
using RaindropServer.Configuration;

namespace RaindropServer.Dashboard;

/// <summary>
/// The owner's dashboard: where it listens, who may reach it, where its sign-in is kept and how long a
/// signed-in browser stays signed in. Filled from raindrop.json's <c>dashboard</c> section.
/// </summary>
public sealed class DashboardOptions
{
    /// <summary>The name its listener and endpoints share; see <see cref="ServerListener"/>.</summary>
    public const string ListenerName = "dashboard";

    /// <summary>The shortest password the dashboard accepts.</summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>
    /// The private address ranges a fresh server lets reach the dashboard: loopback, the three IPv4
    /// private blocks, IPv6 unique-local and link-local. Everything on the public internet is dropped.
    /// </summary>
    public static readonly string[] DefaultAllowedNetworks =
    [
        "127.0.0.0/8", "::1/128",
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
        "fc00::/7", "fe80::/10"
    ];

    /// <summary>Whether the dashboard listens at all. Off unless configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>Where it listens: https, on a port clients do not use.</summary>
    public string Url { get; set; } = "https://0.0.0.0:5002";

    /// <summary>
    /// The networks, as CIDR blocks, whose addresses may connect. Anything else has its connection
    /// closed before the TLS handshake.
    /// </summary>
    public string[] AllowedNetworks { get; set; } = [.. DefaultAllowedNetworks];

    /// <summary>
    /// The file holding the dashboard's username and password hash. Kept beside the server's identity
    /// key, on storage that survives a redeploy; deleting it is the same as <c>dashboard-reset</c>.
    /// </summary>
    public string CredentialsPath { get; set; } = "dashboard-account.json";

    /// <summary>How long a signed-in browser may sit idle before it has to sign in again.</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a sign-in lasts however busy it is.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>How many setup and sign-in attempts one address may make in a minute.</summary>
    public int SignInAttemptsPerMinute { get; set; } = 10;

    /// <summary>
    /// Where the dashboard writes what the owner did through it — sign-ins, recovery codes issued — so
    /// a container's log keeps a trail. Never given a password or a code.
    /// </summary>
    public Action<string> Audit { get; set; } = message => Console.WriteLine($"dashboard: {message}");

    /// <summary>The listener <see cref="ServerHost"/> serves the dashboard on.</summary>
    public ServerListener Listener => new(ListenerName, Url, Allowlist.Contains);

    /// <summary>
    /// <see cref="AllowedNetworks"/> parsed. Entries that do not parse are left out here and reported by
    /// <see cref="Validate"/>, which stops the server before anything listens.
    /// </summary>
    public NetworkAllowlist Allowlist => NetworkAllowlist.Parse(AllowedNetworks, out _);

    /// <summary>Reports everything wrong with these settings.</summary>
    public void Validate(ConfigurationReport report)
    {
        if (!Enabled)
            return;

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port <= 0)
            report.Error("url", $"'{Url}' is not an https:// address with a port.",
                "Write it as https://0.0.0.0:5002: the dashboard is served with the server's certificate, never without TLS.");

        if (AllowedNetworks.Length == 0)
            report.Error("allowedNetworks", "is empty, so nobody could ever reach the dashboard.",
                "List the networks you manage the server from, e.g. \"192.168.0.0/16\", or turn the dashboard off.");

        NetworkAllowlist.Parse(AllowedNetworks, out var invalid);
        foreach (var entry in invalid)
            report.Error("allowedNetworks",
                $"'{entry}' is not a network in CIDR form.",
                "Write an address and prefix length with no host bits set, as in 192.168.1.0/24 or fd00::/8.");

        foreach (var network in NetworkAllowlist.Parse(AllowedNetworks, out _).Networks)
            if (network.PrefixLength == 0)
                report.Warn("allowedNetworks",
                    $"includes {network}, so the dashboard is reachable from any address; only its sign-in protects it.",
                    "List the networks you manage the server from instead.");

        if (string.IsNullOrWhiteSpace(CredentialsPath))
            report.Error("credentialsPath", "is empty; the dashboard needs a file to keep its sign-in in.");

        if (SessionIdleTimeout <= TimeSpan.Zero)
            report.Error("sessionIdleMinutes", "must be positive.");
        else if (SessionIdleTimeout > SessionLifetime)
            report.Warn("sessionIdleMinutes",
                $"is longer than a sign-in lasts ({SessionLifetime.TotalHours:0.#} hours), so it never takes effect.");
    }
}

/// <summary>The networks allowed to reach the dashboard.</summary>
public sealed class NetworkAllowlist
{
    private NetworkAllowlist(IReadOnlyList<IPNetwork> networks) => Networks = networks;

    /// <summary>The networks, in the order they were written.</summary>
    public IReadOnlyList<IPNetwork> Networks { get; }

    /// <summary>
    /// Parses CIDR blocks, collecting the entries that are not one into <paramref name="invalid"/>.
    /// </summary>
    public static NetworkAllowlist Parse(IEnumerable<string> entries, out List<string> invalid)
    {
        var networks = new List<IPNetwork>();
        invalid = [];

        foreach (var entry in entries)
        {
            // An address with bits set past its prefix (192.168.1.5/24) is refused rather than
            // widened: whoever wrote it may have meant that one machine, not its whole network.
            var text = entry.Trim();
            var slash = text.IndexOf('/');
            if (slash > 0 && IPNetwork.TryParse(text, out var network) &&
                IPAddress.TryParse(text[..slash], out var written) && written.Equals(network.BaseAddress))
                networks.Add(network);
            else
                invalid.Add(entry);
        }

        return new NetworkAllowlist(networks);
    }

    /// <summary>
    /// Whether <paramref name="address"/> is inside one of the networks. An IPv4 address that arrives
    /// as IPv6 (<c>::ffff:192.168.1.5</c>, as a dual-stack socket reports it) is judged as IPv4.
    /// </summary>
    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        foreach (var network in Networks)
            if (network.Contains(address))
                return true;

        return false;
    }
}
