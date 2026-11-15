using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.Kafka;

public class KafkaResourceProvider : IResourceProvider
{
    public string ResourceType => "kafka.messaging";

    private static readonly string[] AllowedProperties = ["topics"];
    private static readonly string[] AllowedTopicProperties = ["name", "partitions", "retentionHours", "owners", "consumers"];

    public ResourceSchema GetSchema() => new()
    {
        ResourceType = "kafka.messaging",
        Description = "Kafka messaging with topic management",
        Properties =
        [
            new() { Name = "topics", ValueType = "array", Required = true, Description = "List of Kafka topics" }
        ],
        ProvidedOutputs = ["bootstrapServers", "endpoint"],
        SensitiveOutputs = []
    };

    public Task<ValidationResult> ValidateAsync(ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();
        ProviderSupport.RejectUnknown(resource, AllowedProperties, "kafka.messaging", errors);

        if (!resource.Properties.TryGetValue("topics", out var topics))
        {
            errors.Add("Kafka messaging resource must have at least one topic");
        }
        else if (topics.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Property 'topics' must be an array of topic objects");
        }
        else
        {
            var items = topics.EnumerateArray().ToList();
            if (items.Count == 0)
                errors.Add("Kafka messaging resource must have at least one topic");

            for (var i = 0; i < items.Count; i++)
                ValidateTopic(items[i], i, errors);
        }

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    private static void ValidateTopic(JsonElement item, int index, List<string> errors)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Topic at index {index} must be an object");
            return;
        }

        foreach (var prop in item.EnumerateObject())
        {
            if (!AllowedTopicProperties.Contains(prop.Name))
                errors.Add($"Unknown topic property '{prop.Name}' at index {index}. Allowed: {string.Join(", ", AllowedTopicProperties)}");
        }

        var name = "";
        if (item.TryGetProperty("name", out var n))
        {
            if (n.ValueKind == JsonValueKind.String) name = n.GetString() ?? "";
            else errors.Add($"Topic name at index {index} must be a string");
        }
        if (string.IsNullOrWhiteSpace(name) && !(item.TryGetProperty("name", out var n2) && n2.ValueKind != JsonValueKind.String))
            errors.Add("Kafka topic name is required");

        CheckPositiveInt(item, "partitions", name, "must have at least 1 partition", errors);
        CheckPositiveInt(item, "retentionHours", name, "must have retention of at least 1 hour", errors);

        var owners = CheckStringArray(item, "owners", name, errors);
        CheckStringArray(item, "consumers", name, errors);
        if (owners == 0 && !(item.TryGetProperty("owners", out var o) && o.ValueKind != JsonValueKind.Array))
            errors.Add($"Topic '{name}' must have at least one owner");
    }

    private static void CheckPositiveInt(JsonElement item, string prop, string topic, string message, List<string> errors)
    {
        if (!item.TryGetProperty(prop, out var v)) return;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var value))
            errors.Add($"Topic '{topic}' property '{prop}' must be an integer");
        else if (value < 1)
            errors.Add($"Topic '{topic}' {message}");
    }

    private static int CheckStringArray(JsonElement item, string prop, string topic, List<string> errors)
    {
        if (!item.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"Topic '{topic}' property '{prop}' must be an array of strings");
            return 0;
        }
        var count = 0;
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String)
                errors.Add($"Topic '{topic}' property '{prop}' must contain only strings");
            else
                count++;
        }
        return count;
    }

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
        var topicConfigs = ExtractTopics(resource).Select(t => new Dictionary<string, object?>
        {
            ["name"] = t.Name,
            ["partitions"] = t.Partitions ?? 3,
            ["retentionHours"] = t.RetentionHours ?? 168,
            ["owners"] = t.Owners,
            ["consumers"] = t.Consumers
        }).ToList();

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceKey = resource.Key,
            ResourceType = "kafka.messaging",
            Configuration = new Dictionary<string, object?>
            {
                ["appName"] = ctx.AppName,
                ["environment"] = ctx.Environment,
                ["region"] = ctx.Defaults.Region,
                ["topics"] = topicConfigs
            }
        });
    }

    private static List<KafkaTopicConfig> ExtractTopics(ResourceDescriptor resource)
    {
        if (!resource.Properties.TryGetValue("topics", out var topicsElement)
            || topicsElement.ValueKind != JsonValueKind.Array)
            return [];

        var topics = new List<KafkaTopicConfig>();
        foreach (var item in topicsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            topics.Add(new KafkaTopicConfig
            {
                Name = Str(item, "name") ?? "",
                Partitions = Int(item, "partitions"),
                RetentionHours = Int(item, "retentionHours"),
                Owners = Strings(item, "owners"),
                Consumers = Strings(item, "consumers")
            });
        }

        return topics;
    }

    private static string? Str(JsonElement item, string prop) =>
        item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement item, string prop) =>
        item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static List<string> Strings(JsonElement item, string prop) =>
        item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
}

internal sealed record KafkaTopicConfig
{
    public string Name { get; init; } = "";
    public int? Partitions { get; init; }
    public int? RetentionHours { get; init; }
    public List<string> Owners { get; init; } = [];
    public List<string> Consumers { get; init; } = [];
}
