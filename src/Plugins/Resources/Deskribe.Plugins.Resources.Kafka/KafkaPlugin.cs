using Deskribe.Sdk;

namespace Deskribe.Plugins.Resources.Kafka;

public class KafkaPlugin : IPlugin
{
    public string Name => "Kafka Messaging Provider";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new KafkaResourceProvider());
        registrar.RegisterMessagingProvider(new KafkaMessagingProvider());
    }
}
