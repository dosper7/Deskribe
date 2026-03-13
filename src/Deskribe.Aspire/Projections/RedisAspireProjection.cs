using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire.Projections;

public class RedisAspireProjection : IAspireProjection
{
    public string ResourceType => "redis";

    public AspireProjectionResult Project(IDistributedApplicationBuilder builder, string appName, ResourceDescriptor resource)
    {
        var map = new DeskribeResourceMap(appName);

        var name = $"{appName}-redis";

        var redis = builder.AddRedis(name);

        var version = resource.Properties.TryGetValue("version", out var v) ? v.GetString() : null;
        if (version is not null)
            redis = redis.WithImageTag(version);

        redis = redis.WithRedisInsight();
        redis = redis.WithLifetime(ContainerLifetime.Persistent);

        map.AddConnectionStringResource(redis);
        map.AddWaitForResource(redis);
        map.RegisterResource("redis", name);

        return new AspireProjectionResult { Map = map };
    }
}
