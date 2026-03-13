using Deskribe.Sdk;

namespace Deskribe.Plugins.Backend.Pulumi;

[DeskribePlugin("pulumi")]
public class PulumiPlugin : IPlugin
{
    public string Name => "Pulumi Provisioner";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterProvisioner(new PulumiProvisioner());
    }
}
