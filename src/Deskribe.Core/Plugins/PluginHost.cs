using Deskribe.Sdk;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Plugins;

public class PluginHost : IPluginRegistrar
{
    private readonly ILogger<PluginHost> _logger;
    private readonly Dictionary<string, IResourceProvider> _resourceProviders = new();
    private readonly Dictionary<string, IBackendAdapter> _backendAdapters = new();
    private readonly Dictionary<string, IRuntimeAdapter> _runtimeAdapters = new();
    private readonly Dictionary<string, IMessagingProvider> _messagingProviders = new();

    public PluginHost(ILogger<PluginHost> logger)
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

    public void RegisterBackendAdapter(IBackendAdapter adapter)
    {
        _logger.LogDebug("Registered backend adapter: {Name}", adapter.Name);
        _backendAdapters[adapter.Name] = adapter;
    }

    public void RegisterRuntimeAdapter(IRuntimeAdapter adapter)
    {
        _logger.LogDebug("Registered runtime adapter: {Name}", adapter.Name);
        _runtimeAdapters[adapter.Name] = adapter;
    }

    public void RegisterMessagingProvider(IMessagingProvider provider)
    {
        _logger.LogDebug("Registered messaging provider: {Type}", provider.ProviderType);
        _messagingProviders[provider.ProviderType] = provider;
    }

    public IResourceProvider? GetResourceProvider(string resourceType) =>
        _resourceProviders.GetValueOrDefault(resourceType);

    public IBackendAdapter? GetBackendAdapter(string name) =>
        _backendAdapters.GetValueOrDefault(name);

    public IRuntimeAdapter? GetRuntimeAdapter(string name) =>
        _runtimeAdapters.GetValueOrDefault(name);

    public IMessagingProvider? GetMessagingProvider(string providerType) =>
        _messagingProviders.GetValueOrDefault(providerType);

    public IReadOnlySet<string> GetRegisteredResourceTypes() =>
        _resourceProviders.Keys.ToHashSet();
}
