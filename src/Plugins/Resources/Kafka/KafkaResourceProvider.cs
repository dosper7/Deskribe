using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.Kafka;

public class KafkaResourceProvider : IResourceProvider
{
    public string ResourceType => "kafka.messaging";

    public ResourceSchema GetSchema() => new()
    {
        ResourceType = "kafka.messaging",
        Description = "Kafka messaging with topic management",
        Properties =
        [
            new() { Name = "topics", ValueType = "array", Required = true, Description = "List of Kafka topics" }
        ],
        ProvidedOutputs = ["endpoint", "bootstrapServers"]
    };

    public Task<ValidationResult> ValidateAsync(ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();
        var topics = ExtractTopics(resource);

        if (topics.Count == 0)
            errors.Add("Kafka messaging resource must have at least one topic");

        foreach (var topic in topics)
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

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
        var topics = ExtractTopics(resource);

        var topicConfigs = topics.Select(t => new Dictionary<string, object?>
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

    private static List<KafkaTopicConfig> ExtractTopics(ResourceDescriptor resource)
    {
        if (!resource.Properties.TryGetValue("topics", out var topicsElement))
            return [];

        var topics = new List<KafkaTopicConfig>();
        foreach (var item in topicsElement.EnumerateArray())
        {
            topics.Add(new KafkaTopicConfig
            {
                Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Partitions = item.TryGetProperty("partitions", out var p) ? p.GetInt32() : null,
                RetentionHours = item.TryGetProperty("retentionHours", out var r) ? r.GetInt32() : null,
                Owners = item.TryGetProperty("owners", out var o)
                    ? o.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : [],
                Consumers = item.TryGetProperty("consumers", out var c)
                    ? c.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : []
            });
        }

        return topics;
    }
}

internal sealed record KafkaTopicConfig
{
    public string Name { get; init; } = "";
    public int? Partitions { get; init; }
    public int? RetentionHours { get; init; }
    public List<string> Owners { get; init; } = [];
    public List<string> Consumers { get; init; } = [];
}
