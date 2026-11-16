using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire.Projections;

/// <summary>Outputs: connectionString, endpoint (host:port), host, port.</summary>
public class RedisAspireProjection : IAspireProjection
{
    public string ResourceType => "redis";

    public AspireProjectionResult Project(
        IDistributedApplicationBuilder builder, string appName, ResourceDescriptor resource, DeskribeAspireOptions options)
    {
        var name = ProjectionHelpers.ResourceName(appName, resource.Key);

        var redis = builder.AddRedis(name);

        var version = resource.Properties.TryGetValue("version", out var v) ? v.GetString() : null;
        if (version is not null)
            redis = redis.WithImageTag(version);

        // Aspire protects Redis with a generated password, which would make the password-less
        // host:port `endpoint` output unusable. Local containers are dev-only, so drop it.
        redis = redis.WithArgs(context =>
        {
            var i = context.Args.ToList().FindIndex(a => a is string s && s == "--requirepass");
            if (i < 0)
                return;
            var count = Math.Min(2, context.Args.Count - i);
            for (var n = 0; n < count; n++)
                context.Args.RemoveAt(i);
        });

        if (options.DeveloperTools)
            redis = redis.WithRedisInsight();
        redis = ProjectionHelpers.ApplyLifetime(redis, options);

        var endpoint = ReferenceExpression.Create($"{redis.Resource.Host}:{redis.Resource.Port}");

        return new AspireProjectionResult
        {
            AspireResourceName = name,
            WaitFor = redis.AsBuilder<RedisResource, IResource>(),
            Outputs = new Dictionary<string, ReferenceExpression>
            {
                ["connectionString"] = endpoint,
                ["endpoint"] = endpoint,
                ["host"] = ReferenceExpression.Create($"{redis.Resource.Host}"),
                ["port"] = ReferenceExpression.Create($"{redis.Resource.Port}")
            }
        };
    }
}
