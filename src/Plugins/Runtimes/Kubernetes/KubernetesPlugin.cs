using Deskribe.Sdk;

namespace Deskribe.Plugins.Runtime.Kubernetes;

[DeskribePlugin("kubernetes")]
public class KubernetesPlugin : IPlugin
{
    public string Name => "Kubernetes Runtime Plugin";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterRuntimePlugin(new KubernetesRuntimePlugin());
    }
}
