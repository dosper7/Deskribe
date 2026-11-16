using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Aspire.Projections;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire;

public static partial class DeskribeAspireExtensions
{
    [GeneratedRegex(@"@resource\((?<key>[^)\s]+)\)\.(?<output>[A-Za-z0-9_]+)")]
    private static partial Regex ReferencePattern();

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
        ["kafka.messaging"] = new KafkaAspireProjection(),
        ["kafka"] = new KafkaAspireProjection()
    };

    /// <summary>
    /// Registers (or overrides) the projection used for a resource type, so consumers
    /// can extend the built-in defaults without editing the library.
    /// </summary>
    public static void RegisterProjection(string resourceType, IAspireProjection projection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentNullException.ThrowIfNull(projection);
        DefaultProjections[resourceType] = projection;
    }

    public static DeskribeResourceMap AddDeskribeManifest(
        this IDistributedApplicationBuilder builder,
        string manifestPath,
        IEnumerable<IAspireProjection>? additionalProjections = null,
        DeskribeAspireOptions? options = null)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Manifest not found: {fullPath}");

        var json = File.ReadAllText(fullPath);
        var manifest = JsonSerializer.Deserialize<DeskribeManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest from {fullPath}");

        return builder.AddDeskribeManifest(manifest, additionalProjections, options);
    }

    public static DeskribeResourceMap AddDeskribeManifest(
        this IDistributedApplicationBuilder builder,
        DeskribeManifest manifest,
        IEnumerable<IAspireProjection>? additionalProjections = null,
        DeskribeAspireOptions? options = null)
    {
        options ??= new DeskribeAspireOptions();
        var map = new DeskribeResourceMap(manifest);

        // Registry defaults, overridden per call by additionalProjections
        var projections = new Dictionary<string, IAspireProjection>(DefaultProjections);
        if (additionalProjections is not null)
        {
            foreach (var p in additionalProjections)
                projections[p.ResourceType] = p;
        }

        foreach (var resource in manifest.Resources)
        {
            if (map.Entries.ContainsKey(resource.Key))
                throw new InvalidOperationException(
                    $"Manifest '{manifest.Name}' declares more than one resource with key '{resource.Key}'. " +
                    "Give each resource of the same type a distinct \"name\".");

            if (!projections.TryGetValue(resource.Type, out var projection))
                throw new InvalidOperationException(
                    $"Resource '{resource.Key}' has type '{resource.Type}', which has no Aspire projection. " +
                    $"Known types: {string.Join(", ", projections.Keys.Order())}. " +
                    "Register one with DeskribeAspireExtensions.RegisterProjection or pass it in additionalProjections.");

            var result = projection.Project(builder, manifest.Name, resource, options);
            map.Add(resource, result);
        }

        return map;
    }

    // ReferenceExpression treats literal text as a format string.
    private static string EscapeBraces(string text) => text.Replace("{", "{{").Replace("}", "}}");

    /// <summary>
    /// Applies the manifest service's <c>env</c> map to the project: references
    /// (<c>@resource(key).output</c>) are replaced by the local resource's output, literals are passed
    /// through, and the project waits for every resource it references.
    /// </summary>
    /// <param name="serviceName">
    /// The manifest service to wire. May be omitted when the manifest declares exactly one service.
    /// </param>
    public static IResourceBuilder<TProject> WithDeskribeResources<TProject>(
        this IResourceBuilder<TProject> projectBuilder,
        DeskribeResourceMap resources,
        string? serviceName = null) where TProject : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        var service = resources.ResolveService(serviceName);
        var waitKeys = new HashSet<string>();

        foreach (var (envVar, value) in service.Env)
        {
            var matches = ReferencePattern().Matches(value);
            if (matches.Count == 0)
            {
                projectBuilder = projectBuilder.WithEnvironment(envVar, value);
                continue;
            }

            var expression = new ReferenceExpressionBuilder();
            var position = 0;
            foreach (Match match in matches)
            {
                var key = match.Groups["key"].Value;
                var output = match.Groups["output"].Value;
                var resolved = resources.GetOutput(envVar, key, output);
                waitKeys.Add(key);

                if (match.Index > position)
                    expression.AppendLiteral(EscapeBraces(value[position..match.Index]));
                expression.AppendFormatted(resolved);
                position = match.Index + match.Length;
            }
            if (position < value.Length)
                expression.AppendLiteral(EscapeBraces(value[position..]));

            projectBuilder = projectBuilder.WithEnvironment(envVar, expression.Build());
        }

        foreach (var key in waitKeys)
            projectBuilder = projectBuilder.WaitFor(resources.Entries[key].Result.WaitFor);

        return projectBuilder;
    }
}

public class DeskribeResourceMap
{
    public string AppName => Manifest.Name;
    public DeskribeManifest Manifest { get; }
    public List<DeskribeAspireResource> Resources { get; } = [];

    internal Dictionary<string, (ResourceDescriptor Descriptor, AspireProjectionResult Result)> Entries { get; } = new();

    public DeskribeResourceMap(DeskribeManifest manifest)
    {
        Manifest = manifest;
    }

    internal void Add(ResourceDescriptor descriptor, AspireProjectionResult result)
    {
        Entries[descriptor.Key] = (descriptor, result);
        Resources.Add(new DeskribeAspireResource(descriptor.Key, descriptor.Type, result.AspireResourceName, result.ChildResourceName));
    }

    internal ServiceDefinition ResolveService(string? serviceName)
    {
        var services = Manifest.Services;
        var known = string.Join(", ", services.Select(s => s.Name is null ? "<unnamed>" : $"'{s.Name}'"));

        if (serviceName is null)
        {
            return services.Count switch
            {
                1 => services[0],
                0 => throw new InvalidOperationException($"Manifest '{AppName}' declares no services."),
                _ => throw new InvalidOperationException(
                    $"Manifest '{AppName}' declares {services.Count} services ({known}); " +
                    "pass the serviceName to WithDeskribeResources.")
            };
        }

        return services.FirstOrDefault(s => s.Name == serviceName)
            ?? throw new InvalidOperationException(
                $"Manifest '{AppName}' has no service named '{serviceName}'. Services: {(services.Count == 0 ? "none" : known)}.");
    }

    internal ReferenceExpression GetOutput(string envVar, string key, string output)
    {
        if (!Entries.TryGetValue(key, out var entry))
            throw new InvalidOperationException(
                $"Environment variable '{envVar}' references @resource({key}).{output}, but the manifest has no resource " +
                $"with key '{key}'. Resource keys: {string.Join(", ", Entries.Keys)}.");

        if (!entry.Result.Outputs.TryGetValue(output, out var expression))
            throw new InvalidOperationException(
                $"Environment variable '{envVar}' references @resource({key}).{output}, but resource '{key}' " +
                $"({entry.Descriptor.Type}) does not offer output '{output}'. Offered outputs: {string.Join(", ", entry.Result.Outputs.Keys)}.");

        return expression;
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

public record DeskribeAspireResource(string Key, string Type, string AspireResourceName, string? ChildResourceName);
