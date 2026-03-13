using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Aspire.Projections;
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

    private static readonly Dictionary<string, IAspireProjection> DefaultProjections = new()
    {
        ["postgres"] = new PostgresAspireProjection(),
        ["redis"] = new RedisAspireProjection(),
        ["kafka.messaging"] = new KafkaAspireProjection()
    };

    public static DeskribeResourceMap AddDeskribeManifest(
        this IDistributedApplicationBuilder builder,
        string manifestPath,
        IEnumerable<IAspireProjection>? additionalProjections = null)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Manifest not found: {fullPath}");

        var json = File.ReadAllText(fullPath);
        var manifest = JsonSerializer.Deserialize<DeskribeManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest from {fullPath}");

        return builder.AddDeskribeManifest(manifest, additionalProjections);
    }

    public static DeskribeResourceMap AddDeskribeManifest(
        this IDistributedApplicationBuilder builder,
        DeskribeManifest manifest,
        IEnumerable<IAspireProjection>? additionalProjections = null)
    {
        var map = new DeskribeResourceMap(manifest.Name);

        // Build projection registry from defaults + any additional
        var projections = new Dictionary<string, IAspireProjection>(DefaultProjections);
        if (additionalProjections is not null)
        {
            foreach (var p in additionalProjections)
                projections[p.ResourceType] = p;
        }

        foreach (var resource in manifest.Resources)
        {
            if (projections.TryGetValue(resource.Type, out var projection))
            {
                var result = projection.Project(builder, manifest.Name, resource);
                if (result.Map is not null)
                    map.Merge(result.Map);
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

    public void Merge(DeskribeResourceMap other)
    {
        Resources.AddRange(other.Resources);
        ConnectionStringResources.AddRange(other.ConnectionStringResources);
        WaitForResources.AddRange(other.WaitForResources);
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
