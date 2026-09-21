using System.Reflection;

namespace RaindropServer.Data;

/// <summary>
/// Finds the installed <see cref="IDatabaseDriver"/> modules. Drivers ship as their own assemblies
/// so that a server only ever loads the database client it actually uses: a SQLite server never
/// touches the Postgres or SQL Server libraries.
/// </summary>
/// <remarks>
/// Assemblies are looked for in three places: those already loaded, files named
/// <c>RaindropServer.Data.*.dll</c> next to the executable, and every <c>*.dll</c> in a
/// <c>drivers/</c> folder next to the executable — which is where a third-party driver can simply
/// be dropped in.
/// </remarks>
public static class DatabaseDriverRegistry
{
    private const string DriverAssemblyPrefix = "RaindropServer.Data.";
    private const string DriverFolder = "drivers";

    private static readonly Lock Gate = new();
    private static IReadOnlyList<IDatabaseDriver>? _drivers;

    /// <summary>Every driver installed on this server, discovered once and then cached.</summary>
    public static IReadOnlyList<IDatabaseDriver> Available
    {
        get
        {
            lock (Gate)
                return _drivers ??= Discover();
        }
    }

    /// <summary>The installed driver names, for error messages and for the CLI to print.</summary>
    public static string DescribeAvailable()
    {
        var names = Available.Select(d => d.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 0 ? "(none installed)" : string.Join(", ", names);
    }

    /// <summary>
    /// Returns the driver a server owner enabled by name, or throws listing what is actually installed.
    /// </summary>
    public static IDatabaseDriver Resolve(string provider)
    {
        var matches = Available
            .Where(d => string.Equals(d.Name, provider, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"No database driver named '{provider}' is installed. Available drivers: {DescribeAvailable()}."),
            _ => throw new InvalidOperationException(
                $"More than one installed database driver is named '{provider}': " +
                $"{string.Join(", ", matches.Select(d => d.GetType().Assembly.GetName().Name))}.")
        };
    }

    /// <summary>Forgets the cached discovery, so a driver loaded after startup is picked up.</summary>
    public static void Refresh()
    {
        lock (Gate)
            _drivers = null;
    }

    private static List<IDatabaseDriver> Discover()
    {
        foreach (var path in DriverAssemblyPaths())
        {
            try
            {
                Assembly.LoadFrom(path);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                // Not a managed assembly, or already loaded from somewhere else — either way, skip it.
            }
        }

        var drivers = new List<IDatabaseDriver>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (!typeof(IDatabaseDriver).IsAssignableFrom(type) || !type.IsClass || type.IsAbstract)
                    continue;

                // A driver has to be constructible with no arguments; its options arrive per call.
                if (type.GetConstructor(Type.EmptyTypes) is null)
                    continue;

                if (Activator.CreateInstance(type) is IDatabaseDriver driver)
                    drivers.Add(driver);
            }
        }

        return drivers;
    }

    private static IEnumerable<string> DriverAssemblyPaths()
    {
        var baseDirectory = AppContext.BaseDirectory;

        // Driver assemblies sitting next to the executable are only *referenced*, and .NET loads
        // assemblies lazily — so without this they would stay invisible until something used a type
        // from them, which is exactly what discovery is trying to avoid having to do.
        if (Directory.Exists(baseDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(baseDirectory, $"{DriverAssemblyPrefix}*.dll"))
                yield return path;
        }

        var driverDirectory = Path.Combine(baseDirectory, DriverFolder);

        if (Directory.Exists(driverDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(driverDirectory, "*.dll", SearchOption.AllDirectories))
                yield return path;
        }
    }
}
