using System.Text.Json;
using Deskribe.Plugins.Resources.Kafka;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Tests;

public class KafkaProviderTests
{
    private readonly KafkaResourceProvider _provider = new();

    private static ValidationContext CreateContext() => new()
    {
        Platform = new PlatformConfig
        {
            Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
            Provisioners = new Dictionary<string, string> { ["kafka.messaging"] = "pulumi" }
        },
        Environment = "dev"
    };

    private static ResourceDescriptor CreateKafkaResource(params object[] topics)
    {
        var topicsJson = JsonSerializer.SerializeToElement(topics);
        return new ResourceDescriptor
        {
            Type = "kafka.messaging",
            Properties = new Dictionary<string, JsonElement>
            {
                ["topics"] = topicsJson
            }
        };
    }

    [Fact]
    public async Task Validate_PassesForValidResource()
    {
        var resource = CreateKafkaResource(new
        {
            name = "events",
            partitions = 6,
            retentionHours = 168,
            owners = new[] { "team-a" },
            consumers = new[] { "team-b" }
        });

        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validate_FailsForNoTopics()
    {
        var resource = new ResourceDescriptor
        {
            Type = "kafka.messaging"
        };
        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Contains("at least one topic", result.Errors[0]);
    }

    [Fact]
    public async Task Validate_FailsForTopicWithNoOwner()
    {
        var resource = CreateKafkaResource(new
        {
            name = "events",
            partitions = 3
        });

        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.False(result.IsValid);
    }
}
