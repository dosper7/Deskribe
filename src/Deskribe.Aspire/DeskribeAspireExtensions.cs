using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire;

public static class DeskribeAspireExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static DeskribeResourceMap AddDeskribeManifest(
        this IDistributedApplicationBuilder builder,
        string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Manifest not found: {fullPath}");

        var json = File.ReadAllText(fullPath);
        var manifest = JsonSerializer.Deserialize<DeskribeManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest from {fullPath}");

        return builder.AddDeskribeManifest(manifest);
    }

    public static DeskribeResourceMap AddDeskribeManifest(
        this IDistributedApplicationBuilder builder,
        DeskribeManifest manifest)
    {
        var map = new DeskribeResourceMap(manifest.Name);

        foreach (var resource in manifest.Resources)
        {
            switch (resource.Type)
            {
                case "postgres":
                    AddPostgresResource(builder, manifest.Name, resource, map);
                    break;
                case "redis":
                    AddRedisResource(builder, manifest.Name, resource, map);
                    break;
                case "kafka.messaging":
                    AddKafkaResource(builder, manifest.Name, resource, map);
                    break;
            }
        }

        return map;
    }

    public static IResourceBuilder<TProject> WithDeskribeResources<TProject>(
        this IResourceBuilder<TProject> projectBuilder,
        DeskribeResourceMap resources) where TProject : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        foreach (var entry in resources.ConnectionStringResources)
        {
            projectBuilder = projectBuilder.WithReference(entry);
        }

        foreach (var entry in resources.WaitForResources)
        {
            projectBuilder = projectBuilder.WaitFor(entry);
        }

        return projectBuilder;
    }

    private static void AddPostgresResource(
        IDistributedApplicationBuilder builder,
        string appName,
        ResourceDescriptor resource,
        DeskribeResourceMap map)
    {
        var serverName = $"{appName}-postgres";
        var dbName = $"{appName}-db";

        var server = builder.AddPostgres(serverName);

        var version = resource.Properties.TryGetValue("version", out var v) ? v.GetString() : null;
        if (version is not null)
            server = server.WithImageTag(version);

        server = server.WithPgAdmin();
        server = server.WithLifetime(ContainerLifetime.Persistent);

        var db = server.AddDatabase(dbName);

        map.AddConnectionStringResource(db);
        map.AddWaitForResource(db);
        map.RegisterResource("postgres", serverName, dbName);
    }

    private static void AddRedisResource(
        IDistributedApplicationBuilder builder,
        string appName,
        ResourceDescriptor resource,
        DeskribeResourceMap map)
    {
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
    }

    private static void AddKafkaResource(
        IDistributedApplicationBuilder builder,
        string appName,
        ResourceDescriptor resource,
        DeskribeResourceMap map)
    {
        var name = $"{appName}-kafka";

        var kafka = builder.AddKafka(name);

        kafka = kafka.WithKafkaUI();
        kafka = kafka.WithLifetime(ContainerLifetime.Persistent);

        map.AddConnectionStringResource(kafka);
        map.AddWaitForResource(kafka);
        map.RegisterResource("kafka.messaging", name);
    }
}

public class DeskribeResourceMap
{
    public string AppName { get; }
    public List<DeskribeAspireResource> Resources { get; } = [];

    internal List<IResourceBuilder<IResourceWithConnectionString>> ConnectionStringResources { get; } = [];
    internal List<IResourceBuilder<IResource>> WaitForResources { get; } = [];

    public DeskribeResourceMap(string appName)
    {
        AppName = appName;
    }

    internal void AddConnectionStringResource<T>(IResourceBuilder<T> resource) where T : IResourceWithConnectionString
    {
        ConnectionStringResources.Add(resource.AsBuilder<T, IResourceWithConnectionString>());
    }

    internal void AddWaitForResource<T>(IResourceBuilder<T> resource) where T : IResource
    {
        WaitForResources.Add(resource.AsBuilder<T, IResource>());
    }

    internal void RegisterResource(string type, string aspireResourceName, string? childResourceName = null)
    {
        Resources.Add(new DeskribeAspireResource(type, aspireResourceName, childResourceName));
    }
}

internal static class ResourceBuilderCastHelper
{
    public static IResourceBuilder<TTarget> AsBuilder<TSource, TTarget>(this IResourceBuilder<TSource> source)
        where TSource : TTarget
        where TTarget : IResource
    {
        return (IResourceBuilder<TTarget>)(object)source;
    }
}

public record DeskribeAspireResource(string Type, string AspireResourceName, string? ChildResourceName);
