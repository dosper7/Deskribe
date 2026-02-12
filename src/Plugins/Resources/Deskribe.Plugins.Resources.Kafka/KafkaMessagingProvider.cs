using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Resources.Kafka;

public class KafkaMessagingProvider : IMessagingProvider
{
    public string ProviderType => "kafka.messaging";

    public Task<ValidationResult> ValidateAsync(KafkaMessagingResource resource, ValidationContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();

        foreach (var topic in resource.Topics)
        {
            if (topic.Partitions is not null and < 3)
                errors.Add($"Topic '{topic.Name}': platform minimum is 3 partitions");
        }

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(KafkaMessagingResource resource, PlanContext ctx, CancellationToken ct)
    {
        var aclEntries = new List<Dictionary<string, object>>();

        foreach (var topic in resource.Topics)
        {
            foreach (var owner in topic.Owners)
            {
                aclEntries.Add(new Dictionary<string, object>
                {
                    ["principal"] = owner,
                    ["topic"] = topic.Name,
                    ["permission"] = "WRITE"
                });
            }

            foreach (var consumer in topic.Consumers)
            {
                aclEntries.Add(new Dictionary<string, object>
                {
                    ["principal"] = consumer,
                    ["topic"] = topic.Name,
                    ["permission"] = "READ"
                });
            }
        }

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "kafka.messaging",
            Action = "create",
            Configuration = new Dictionary<string, object?>
            {
                ["acls"] = aclEntries,
                ["topicCount"] = resource.Topics.Count
            }
        });
    }
}
