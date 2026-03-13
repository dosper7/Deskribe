using System.Reflection;
using Deskribe.Sdk;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Plugins;

public class PluginRegistry : IPluginRegistrar
{
    private readonly ILogger<PluginRegistry> _logger;
    private readonly Dictionary<string, IResourceProvider> _resourceProviders = new();
    private readonly Dictionary<string, IProvisioner> _provisioners = new();
    private readonly Dictionary<string, IRuntimePlugin> _runtimePlugins = new();

    public PluginRegistry(ILogger<PluginRegistry> logger)
    {
        _logger = logger;
    }

    public void RegisterPlugin(IPlugin plugin)
    {
        _logger.LogInformation("Registering plugin: {Name}", plugin.Name);
        plugin.Register(this);
    }

    public void RegisterResourceProvider(IResourceProvider provider)
    {
        _logger.LogDebug("Registered resource provider: {Type}", provider.ResourceType);
        _resourceProviders[provider.ResourceType] = provider;
    }

    public void RegisterProvisioner(IProvisioner provisioner)
    {
        _logger.LogDebug("Registered provisioner: {Name}", provisioner.Name);
        _provisioners[provisioner.Name] = provisioner;
    }

    public void RegisterRuntimePlugin(IRuntimePlugin runtime)
    {
        _logger.LogDebug("Registered runtime plugin: {Name}", runtime.Name);
        _runtimePlugins[runtime.Name] = runtime;
    }

    public void DiscoverAndRegisterAll(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var pluginTypes = assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false }
                    && typeof(IPlugin).IsAssignableFrom(t)
                    && t.GetCustomAttribute<DeskribePluginAttribute>() is not null);

            foreach (var type in pluginTypes)
            {
                var plugin = (IPlugin)Activator.CreateInstance(type)!;
                RegisterPlugin(plugin);
            }
        }
    }

    public IResourceProvider? GetResourceProvider(string resourceType) =>
        _resourceProviders.GetValueOrDefault(resourceType);

    public IProvisioner? GetProvisioner(string name) =>
        _provisioners.GetValueOrDefault(name);

    public IRuntimePlugin? GetRuntimePlugin(string name) =>
        _runtimePlugins.GetValueOrDefault(name);

    public IReadOnlyDictionary<string, IResourceProvider> GetAllResourceProviders() =>
        _resourceProviders;

    public IReadOnlySet<string> GetRegisteredResourceTypes() =>
        _resourceProviders.Keys.ToHashSet();
}
