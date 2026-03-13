using Deskribe.Sdk;

namespace Deskribe.Plugins.Backend.Terraform;

[DeskribePlugin("terraform")]
public class TerraformPlugin : IPlugin
{
    public string Name => "Terraform Provisioner";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterProvisioner(new TerraformProvisioner());
    }
}
