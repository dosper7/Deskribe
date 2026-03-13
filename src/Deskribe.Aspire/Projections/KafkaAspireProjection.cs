using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire.Projections;

public class KafkaAspireProjection : IAspireProjection
{
    public string ResourceType => "kafka.messaging";

    public AspireProjectionResult Project(IDistributedApplicationBuilder builder, string appName, ResourceDescriptor resource)
    {
        var map = new DeskribeResourceMap(appName);

        var name = $"{appName}-kafka";

        var kafka = builder.AddKafka(name);

        kafka = kafka.WithKafkaUI();
        kafka = kafka.WithLifetime(ContainerLifetime.Persistent);

        map.AddConnectionStringResource(kafka);
        map.AddWaitForResource(kafka);
        map.RegisterResource("kafka.messaging", name);

        return new AspireProjectionResult { Map = map };
    }
}
