using System.Reflection;
using Deskribe.Core.Config;
using Deskribe.Core.Engine;
using Deskribe.Core.Merging;
using Deskribe.Core.Plugins;
using Deskribe.Core.Resolution;
using Deskribe.Core.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Deskribe.Core;

public static class DeskribeServiceExtensions
{
    public static IServiceCollection AddDeskribe(this IServiceCollection services, params Assembly[] pluginAssemblies)
    {
        return AddDeskribeCore(services, pluginAssemblies);
    }

    public static IServiceCollection AddDeskribe(this IServiceCollection services, Assembly[] builtIn, params string[] pluginDirs)
    {
        var externalAssemblies = PluginLoader.LoadFromDirectories(pluginDirs);
        var allAssemblies = builtIn.Concat(externalAssemblies).ToArray();
        return AddDeskribeCore(services, allAssemblies);
    }

    private static IServiceCollection AddDeskribeCore(IServiceCollection services, Assembly[] pluginAssemblies)
    {
        services.AddSingleton<ConfigLoader>();
        services.AddSingleton<MergeEngine>();
        services.AddSingleton<ResourceReferenceResolver>();
        services.AddSingleton<PolicyValidator>();
        services.AddSingleton(sp =>
        {
            var registry = new PluginRegistry(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PluginRegistry>>());
            registry.DiscoverAndRegisterAll(pluginAssemblies);
            return registry;
        });
        services.AddSingleton<DeskribeEngine>();

        return services;
    }
}
