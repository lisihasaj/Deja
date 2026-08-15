using System.Collections.Concurrent;
using System.Reflection;

namespace Deja.Docs.Services;

/// <summary>
/// Reads the demo components' own .razor source back out of the assembly (they are embedded by
/// the project file), so a demo's source tab can never drift from the code that is running.
/// </summary>
public static class DemoSourceLoader
{
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public static string Load(string fileName) => _cache.GetOrAdd(fileName, static file =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(file, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            return $"// Source for {file} was not embedded.";
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}
