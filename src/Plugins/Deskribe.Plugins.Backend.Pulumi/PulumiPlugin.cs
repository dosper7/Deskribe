using Deskribe.Sdk;

namespace Deskribe.Plugins.Backend.Pulumi;

public class PulumiPlugin : IPlugin
{
    public string Name => "Pulumi Backend Adapter";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterBackendAdapter(new PulumiBackendAdapter());
    }
}
