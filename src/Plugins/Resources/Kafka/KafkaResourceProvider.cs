using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Resources.Kafka;

public class KafkaResourceProvider : IResourceProvider
{
    public string ResourceType => "kafka.messaging";

    public Task<ValidationResult> ValidateAsync(DeskribeResource resource, ValidationContext ctx, CancellationToken ct)
    {
        if (resource is not KafkaMessagingResource kafka)
            return Task.FromResult(ValidationResult.Invalid($"Expected KafkaMessagingResource but got {resource.GetType().Name}"));

        var errors = new List<string>();

        if (kafka.Topics.Count == 0)
            errors.Add("Kafka messaging resource must have at least one topic");

        foreach (var topic in kafka.Topics)
        {
            if (string.IsNullOrWhiteSpace(topic.Name))
                errors.Add("Kafka topic name is required");

            if (topic.Partitions is < 1)
                errors.Add($"Topic '{topic.Name}' must have at least 1 partition");

            if (topic.RetentionHours is < 1)
                errors.Add($"Topic '{topic.Name}' must have retention of at least 1 hour");

            if (topic.Owners.Count == 0)
                errors.Add($"Topic '{topic.Name}' must have at least one owner");
        }

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(DeskribeResource resource, PlanContext ctx, CancellationToken ct)
    {
        var kafka = (KafkaMessagingResource)resource;

        var topicConfigs = kafka.Topics.Select(t => new Dictionary<string, object?>
        {
            ["name"] = t.Name,
            ["partitions"] = t.Partitions ?? 3,
            ["retentionHours"] = t.RetentionHours ?? 168,
            ["owners"] = t.Owners,
            ["consumers"] = t.Consumers
        }).ToList();

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "kafka.messaging",
            Action = "create",
            PlannedOutputs = new Dictionary<string, string>
            {
                ["endpoint"] = $"<pending:{ctx.AppName}-kafka>",
                ["bootstrapServers"] = $"<pending:{ctx.AppName}-kafka-bootstrap>"
            },
            Configuration = new Dictionary<string, object?>
            {
                ["appName"] = ctx.AppName,
                ["environment"] = ctx.Environment,
                ["region"] = ctx.Platform.Defaults.Region,
                ["topics"] = topicConfigs
            }
        });
    }
}
