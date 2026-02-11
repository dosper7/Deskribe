namespace Deskribe.Sdk;

public interface IPlugin
{
    string Name { get; }
    void Register(IPluginRegistrar registrar);
}

public interface IPluginRegistrar
{
    void RegisterResourceProvider(IResourceProvider provider);
    void RegisterBackendAdapter(IBackendAdapter adapter);
    void RegisterRuntimeAdapter(IRuntimeAdapter adapter);
    void RegisterMessagingProvider(IMessagingProvider provider);
}
