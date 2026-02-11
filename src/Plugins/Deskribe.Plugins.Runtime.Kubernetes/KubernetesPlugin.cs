using Deskribe.Sdk;

namespace Deskribe.Plugins.Runtime.Kubernetes;

public class KubernetesPlugin : IPlugin
{
    public string Name => "Kubernetes Runtime Adapter";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterRuntimeAdapter(new KubernetesRuntimeAdapter());
    }
}
