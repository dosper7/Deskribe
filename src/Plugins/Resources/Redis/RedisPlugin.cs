using Deskribe.Sdk;

namespace Deskribe.Plugins.Resources.Redis;

public class RedisPlugin : IPlugin
{
    public string Name => "Redis Resource Provider";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new RedisResourceProvider());
    }
}
