using Deskribe.Plugins.Resources.Kafka;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Tests;

public class KafkaProviderTests
{
    private readonly KafkaResourceProvider _provider = new();

    private static ValidationContext CreateContext() => new()
    {
        Platform = new PlatformConfig
        {
            Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
            Backends = new Dictionary<string, string> { ["kafka.messaging"] = "pulumi" }
        },
        Environment = "dev"
    };

    [Fact]
    public async Task Validate_PassesForValidResource()
    {
        var resource = new KafkaMessagingResource
        {
            Type = "kafka.messaging",
            Topics =
            [
                new KafkaTopic
                {
                    Name = "events",
                    Partitions = 6,
                    RetentionHours = 168,
                    Owners = ["team-a"],
                    Consumers = ["team-b"]
                }
            ]
        };

        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validate_FailsForNoTopics()
    {
        var resource = new KafkaMessagingResource { Type = "kafka.messaging" };
        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Contains("at least one topic", result.Errors[0]);
    }

    [Fact]
    public async Task Validate_FailsForTopicWithNoOwner()
    {
        var resource = new KafkaMessagingResource
        {
            Type = "kafka.messaging",
            Topics = [new KafkaTopic { Name = "events", Partitions = 3 }]
        };

        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.False(result.IsValid);
    }
}
