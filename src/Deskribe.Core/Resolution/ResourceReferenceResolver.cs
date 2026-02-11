using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Resolution;

public partial class ResourceReferenceResolver
{
    private readonly ILogger<ResourceReferenceResolver> _logger;

    [GeneratedRegex(@"@resource\((?<type>[a-zA-Z0-9_.]+)\)\.(?<property>[a-zA-Z0-9_]+)")]
    private static partial Regex ResourceRefPattern();

    public ResourceReferenceResolver(ILogger<ResourceReferenceResolver> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ResourceReference> ExtractReferences(Dictionary<string, string> envVars)
    {
        var refs = new List<ResourceReference>();

        foreach (var (key, value) in envVars)
        {
            var matches = ResourceRefPattern().Matches(value);
            foreach (Match match in matches)
            {
                refs.Add(new ResourceReference
                {
                    EnvVarName = key,
                    RawExpression = match.Value,
                    ResourceType = match.Groups["type"].Value,
                    Property = match.Groups["property"].Value
                });
            }
        }

        _logger.LogDebug("Extracted {Count} resource references", refs.Count);
        return refs;
    }

    public Dictionary<string, string> ResolveReferences(
        Dictionary<string, string> envVars,
        Dictionary<string, Dictionary<string, string>> resourceOutputs)
    {
        var resolved = new Dictionary<string, string>(envVars);

        foreach (var (key, value) in envVars)
        {
            resolved[key] = ResourceRefPattern().Replace(value, match =>
            {
                var resourceType = match.Groups["type"].Value;
                var property = match.Groups["property"].Value;

                if (resourceOutputs.TryGetValue(resourceType, out var outputs) &&
                    outputs.TryGetValue(property, out var resolvedValue))
                {
                    _logger.LogDebug("Resolved @resource({Type}).{Prop} = {Value}", resourceType, property, "***");
                    return resolvedValue;
                }

                _logger.LogWarning("Unresolved reference: @resource({Type}).{Property}", resourceType, property);
                return match.Value;
            });
        }

        return resolved;
    }

    public ValidationResult ValidateReferences(
        IReadOnlyList<ResourceReference> refs,
        IReadOnlySet<string> availableResourceTypes)
    {
        var errors = new List<string>();

        foreach (var r in refs)
        {
            if (!availableResourceTypes.Contains(r.ResourceType))
            {
                errors.Add($"Environment variable '{r.EnvVarName}' references unknown resource type '{r.ResourceType}'");
            }
        }

        return new ValidationResult(errors.Count == 0, errors);
    }
}

public record ResourceReference
{
    public required string EnvVarName { get; init; }
    public required string RawExpression { get; init; }
    public required string ResourceType { get; init; }
    public required string Property { get; init; }
}

public record ValidationResult(bool IsValid, List<string> Errors);
