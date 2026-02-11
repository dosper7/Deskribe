using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Sdk.Models;
using SdkPostgresResource = Deskribe.Sdk.Resources.PostgresResource;
using SdkRedisResource = Deskribe.Sdk.Resources.RedisResource;
using SdkKafkaResource = Deskribe.Sdk.Resources.KafkaMessagingResource;
using DeskribeResource = Deskribe.Sdk.Resources.DeskribeResource;

namespace Deskribe.Aspire;

/// <summary>
/// Reads a developer's deskribe.json and dynamically creates Aspire resources.
/// Single source of truth: the manifest drives both local dev (Aspire) and prod (Pulumi/TF).
/// </summary>
public static class DeskribeAspireExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ManifestResourceJsonConverter() }
    };

    /// <summary>
    /// Reads a deskribe.json manifest and spins up all declared resources as Aspire containers.
    /// Returns a <see cref="DeskribeResourceMap"/> that can wire references to any project.
    /// </summary>
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

    /// <summary>
    /// Takes a pre-loaded manifest and spins up all declared resources as Aspire containers.
    /// </summary>
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
                    AddPostgresResource(builder, manifest.Name, resource as SdkPostgresResource, map);
                    break;
                case "redis":
                    AddRedisResource(builder, manifest.Name, resource as SdkRedisResource, map);
                    break;
                case "kafka.messaging":
                    AddKafkaResource(builder, manifest.Name, resource as SdkKafkaResource, map);
                    break;
            }
        }

        return map;
    }

    /// <summary>
    /// Wires all Deskribe resources to a project, injecting connection strings
    /// and waiting for readiness. The developer's @resource() references resolve
    /// automatically via Aspire's connection string injection.
    /// </summary>
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
        SdkPostgresResource? pgResource,
        DeskribeResourceMap map)
    {
        var serverName = $"{appName}-postgres";
        var dbName = $"{appName}-db";

        var server = builder.AddPostgres(serverName)
            .WithPgAdmin()
            .WithLifetime(ContainerLifetime.Persistent);

        var db = server.AddDatabase(dbName);

        map.AddConnectionStringResource(db);
        map.AddWaitForResource(db);
        map.RegisterResource("postgres", serverName, dbName);
    }

    private static void AddRedisResource(
        IDistributedApplicationBuilder builder,
        string appName,
        SdkRedisResource? redisResource,
        DeskribeResourceMap map)
    {
        var name = $"{appName}-redis";

        var redis = builder.AddRedis(name)
            .WithRedisInsight()
            .WithLifetime(ContainerLifetime.Persistent);

        map.AddConnectionStringResource(redis);
        map.AddWaitForResource(redis);
        map.RegisterResource("redis", name);
    }

    private static void AddKafkaResource(
        IDistributedApplicationBuilder builder,
        string appName,
        SdkKafkaResource? kafkaResource,
        DeskribeResourceMap map)
    {
        var name = $"{appName}-kafka";

        var kafka = builder.AddKafka(name)
            .WithKafkaUI()
            .WithLifetime(ContainerLifetime.Persistent);

        map.AddConnectionStringResource(kafka);
        map.AddWaitForResource(kafka);
        map.RegisterResource("kafka.messaging", name);
    }
}

/// <summary>
/// Holds references to all Aspire resources created from a deskribe.json manifest.
/// Used to wire them to projects via <see cref="DeskribeAspireExtensions.WithDeskribeResources"/>.
/// </summary>
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
    /// <summary>
    /// Casts an IResourceBuilder from a concrete type to an interface type.
    /// Aspire's IResourceBuilder is covariant-friendly in practice.
    /// </summary>
    public static IResourceBuilder<TTarget> AsBuilder<TSource, TTarget>(this IResourceBuilder<TSource> source)
        where TSource : TTarget
        where TTarget : IResource
    {
        return (IResourceBuilder<TTarget>)(object)source;
    }
}

public record DeskribeAspireResource(string Type, string AspireResourceName, string? ChildResourceName);

/// <summary>
/// Minimal JSON converter for DeskribeResource polymorphism — same logic as Core's ConfigLoader
/// but self-contained so the Aspire library doesn't depend on internal Core types.
/// </summary>
internal class ManifestResourceJsonConverter : JsonConverter<DeskribeResource>
{
    public override DeskribeResource? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("Resource must have a 'type' property");

        var type = typeProp.GetString();
        var rawJson = root.GetRawText();

        // Use a copy without this converter to avoid infinite recursion
        var innerOptions = new JsonSerializerOptions(options);
        for (int i = innerOptions.Converters.Count - 1; i >= 0; i--)
        {
            if (innerOptions.Converters[i] is ManifestResourceJsonConverter)
                innerOptions.Converters.RemoveAt(i);
        }

        return type switch
        {
            "postgres" => JsonSerializer.Deserialize<SdkPostgresResource>(rawJson, innerOptions),
            "redis" => JsonSerializer.Deserialize<SdkRedisResource>(rawJson, innerOptions),
            "kafka.messaging" => JsonSerializer.Deserialize<SdkKafkaResource>(rawJson, innerOptions),
            _ => throw new JsonException($"Unknown resource type: {type}")
        };
    }

    public override void Write(Utf8JsonWriter writer, DeskribeResource value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
