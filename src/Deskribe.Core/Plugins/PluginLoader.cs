using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Plugins;

public static class PluginLoader
{
    public static string DefaultPluginsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".deskribe", "plugins");

    public static Assembly[] LoadFromDirectories(params string[] dirs) =>
        LoadFromDirectories(logger: null, dirs);

    public static Assembly[] LoadFromDirectories(ILogger? logger, params string[] dirs)
    {
        var assemblies = new List<Assembly>();

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var dll in Directory.GetFiles(dir, "*.dll"))
            {
                try
                {
                    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll));
                    assemblies.Add(assembly);
                }
                catch (Exception ex)
                {
                    // Skip DLLs that fail to load (native deps, wrong TFM, etc.), but don't stay silent
                    logger?.LogWarning("Skipping plugin DLL '{Dll}': {Error}", dll, ex.Message);
                }
            }
        }

        return assemblies.ToArray();
    }
}
