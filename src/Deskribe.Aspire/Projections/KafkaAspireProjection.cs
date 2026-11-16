using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire.Projections;

/// <summary>Outputs: bootstrapServers and endpoint (same value, host:port).</summary>
public class KafkaAspireProjection : IAspireProjection
{
    public string ResourceType => "kafka.messaging";

    public AspireProjectionResult Project(
        IDistributedApplicationBuilder builder, string appName, ResourceDescriptor resource, DeskribeAspireOptions options)
    {
        var name = ProjectionHelpers.ResourceName(appName, resource.Key);

        var kafka = builder.AddKafka(name);

        if (options.DeveloperTools)
            kafka = kafka.WithKafkaUI();
        kafka = ProjectionHelpers.ApplyLifetime(kafka, options);

        var bootstrap = ReferenceExpression.Create($"{kafka.Resource.Host}:{kafka.Resource.Port}");

        return new AspireProjectionResult
        {
            AspireResourceName = name,
            WaitFor = kafka.AsBuilder<KafkaServerResource, IResource>(),
            Outputs = new Dictionary<string, ReferenceExpression>
            {
                ["bootstrapServers"] = bootstrap,
                ["endpoint"] = bootstrap
            }
        };
    }
}
