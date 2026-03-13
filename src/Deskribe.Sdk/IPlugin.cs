namespace Deskribe.Sdk;

public interface IPlugin
{
    string Name { get; }
    void Register(IPluginRegistrar registrar);
}

public interface IPluginRegistrar
{
    void RegisterResourceProvider(IResourceProvider provider);
    void RegisterProvisioner(IProvisioner provisioner);
    void RegisterRuntimePlugin(IRuntimePlugin runtime);
}
