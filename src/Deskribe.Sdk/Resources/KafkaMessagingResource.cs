namespace Deskribe.Sdk.Resources;

public sealed record KafkaMessagingResource : DeskribeResource
{
    public List<KafkaTopic> Topics { get; init; } = [];
}

public sealed record KafkaTopic
{
    public required string Name { get; init; }
    public int? Partitions { get; init; }
    public int? RetentionHours { get; init; }
    public List<string> Owners { get; init; } = [];
    public List<string> Consumers { get; init; } = [];
}
