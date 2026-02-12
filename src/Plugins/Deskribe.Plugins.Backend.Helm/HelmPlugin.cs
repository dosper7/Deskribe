using Deskribe.Sdk;

namespace Deskribe.Plugins.Backend.Helm;

public class HelmPlugin : IPlugin
{
    public string Name => "Helm Backend Adapter";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterBackendAdapter(new HelmBackendAdapter());
    }
}
