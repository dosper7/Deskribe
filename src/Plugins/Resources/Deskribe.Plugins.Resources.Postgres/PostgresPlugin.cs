using Deskribe.Sdk;

namespace Deskribe.Plugins.Resources.Postgres;

public class PostgresPlugin : IPlugin
{
    public string Name => "Postgres Resource Provider";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new PostgresResourceProvider());
    }
}
