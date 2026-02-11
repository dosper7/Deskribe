using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Validation;

public class PolicyValidator
{
    private readonly ILogger<PolicyValidator> _logger;

    public PolicyValidator(ILogger<PolicyValidator> logger)
    {
        _logger = logger;
    }

    public Sdk.Models.ValidationResult Validate(DeskribeManifest manifest, PlatformConfig platform)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add("Manifest 'name' is required");
        }

        foreach (var resource in manifest.Resources)
        {
            if (!platform.Backends.ContainsKey(resource.Type))
            {
                warnings.Add($"Resource type '{resource.Type}' has no configured backend in platform config");
            }
        }

        // Validate service env vars reference existing resources
        var resourceTypes = manifest.Resources.Select(r => r.Type).ToHashSet();
        foreach (var service in manifest.Services)
        {
            foreach (var (envKey, envValue) in service.Env)
            {
                if (envValue.StartsWith("@resource("))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(envValue, @"@resource\(([^)]+)\)");
                    if (match.Success)
                    {
                        var refType = match.Groups[1].Value;
                        if (!resourceTypes.Contains(refType))
                        {
                            errors.Add($"Service env var '{envKey}' references resource type '{refType}' which is not declared in resources");
                        }
                    }
                }
            }
        }

        _logger.LogInformation("Policy validation: {ErrorCount} errors, {WarningCount} warnings", errors.Count, warnings.Count);

        return new Sdk.Models.ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings
        };
    }
}
