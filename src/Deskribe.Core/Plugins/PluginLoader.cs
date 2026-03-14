using System.Reflection;
using System.Runtime.Loader;

namespace Deskribe.Core.Plugins;

public static class PluginLoader
{
    public static string DefaultPluginsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".deskribe", "plugins");

    public static Assembly[] LoadFromDirectories(params string[] dirs)
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
                catch
                {
                    // Silently skip DLLs that fail to load (native deps, wrong TFM, etc.)
                }
            }
        }

        return assemblies.ToArray();
    }
}
