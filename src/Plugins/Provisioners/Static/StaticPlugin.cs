using Deskribe.Sdk;

namespace Deskribe.Plugins.Provisioner.Static;

[DeskribePlugin("static")]
public class StaticPlugin : IPlugin
{
    public string Name => "Static Provisioner";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterProvisioner(new StaticProvisioner());
    }
}
