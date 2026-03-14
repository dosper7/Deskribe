using Deskribe.Sdk;

namespace Deskribe.Plugins.Provisioner.PlatformOutput;

[DeskribePlugin("platform-output")]
public class PlatformOutputPlugin : IPlugin
{
    public string Name => "Platform Output Provisioner";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterProvisioner(new PlatformOutputProvisioner());
    }
}
