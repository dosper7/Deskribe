using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire;

/// <summary>
/// Turns one manifest resource into a local Aspire resource and describes which
/// <c>@resource(key).output</c> values it can provide.
/// </summary>
public interface IAspireProjection
{
    string ResourceType { get; }

    /// <summary>Creates the local resource for <paramref name="resource"/> (one call per resource key).</summary>
    AspireProjectionResult Project(
        IDistributedApplicationBuilder builder,
        string appName,
        ResourceDescriptor resource,
        DeskribeAspireOptions options);
}

public sealed class AspireProjectionResult
{
    /// <summary>
    /// The outputs this resource offers, by output name. Each value is something Aspire can
    /// inject into an environment variable (connection string expression, endpoint reference
    /// expression or literal).
    /// </summary>
    public required IReadOnlyDictionary<string, ReferenceExpression> Outputs { get; init; }

    /// <summary>The Aspire resource a consumer must wait for before starting.</summary>
    public required IResourceBuilder<IResource> WaitFor { get; init; }

    /// <summary>The Aspire resource name of the main container (for diagnostics).</summary>
    public required string AspireResourceName { get; init; }

    public string? ChildResourceName { get; init; }
}

public sealed class DeskribeAspireOptions
{
    /// <summary>Add PgAdmin, RedisInsight and Kafka UI next to the containers. Default true.</summary>
    public bool DeveloperTools { get; set; } = true;

    /// <summary>Keep containers (and their data) between AppHost runs. Default true.</summary>
    public bool PersistentContainers { get; set; } = true;
}
