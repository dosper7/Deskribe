using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Provisioner.Static;

/// <summary>
/// For infrastructure the platform already runs: declares outputs instead of creating anything.
/// Config: <c>outputs</c> (resource key or type → output name → template; key wins) and optional
/// <c>environments.&lt;env&gt;.outputs</c> of the same shape, overriding per environment.
/// Templates support {app}, {env}, {team}, {region}, {key}; <c>env:NAME</c> reads a process environment variable.
/// </summary>
public class StaticProvisioner : IProvisioner
{
    private const string EnvPrefix = "env:";

    private readonly Func<string, string?> _getEnvironmentVariable;

    public StaticProvisioner() : this(Environment.GetEnvironmentVariable) { }

    internal StaticProvisioner(Func<string, string?> getEnvironmentVariable) =>
        _getEnvironmentVariable = getEnvironmentVariable;

    public string Name => "static";

    public Task<ProvisionResult> ApplyAsync(ProvisionContext ctx, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var global = ReadOutputs(ctx.Config, errors, "outputs");
        Dictionary<string, Dictionary<string, string>> perEnv = [];
        if (ctx.Config.TryGetValue("environments", out var envs))
        {
            if (envs.ValueKind == JsonValueKind.Object && envs.TryGetProperty(ctx.Environment, out var envConfig)
                && envConfig.ValueKind == JsonValueKind.Object)
            {
                var asDict = envConfig.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                perEnv = ReadOutputs(asDict, errors, $"environments.{ctx.Environment}.outputs");
            }
            else if (envs.ValueKind != JsonValueKind.Object)
            {
                errors.Add("'environments' must be an object");
            }
        }

        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var resource in ctx.Resources)
        {
            var merged = new Dictionary<string, string>();
            foreach (var source in new[] { global, perEnv })
            {
                // type first so that the key-specific entry wins within the same level
                foreach (var selector in new[] { resource.ResourceType, resource.ResourceKey })
                {
                    if (!source.TryGetValue(selector, out var outputs)) continue;
                    foreach (var (name, template) in outputs)
                        merged[name] = template;
                }
            }

            if (merged.Count == 0) continue;

            var resolved = new Dictionary<string, string>();
            foreach (var (name, template) in merged)
            {
                var value = Resolve(template, ctx, resource, out var error);
                if (error is not null) errors.Add($"Static output '{resource.ResourceKey}.{name}': {error}");
                else resolved[name] = value!;
            }
            result[resource.ResourceKey] = resolved;
        }

        return Task.FromResult(errors.Count > 0
            ? ProvisionResult.Failed([.. errors])
            : new ProvisionResult { Success = true, ResourceOutputs = result });
    }

    public Task<ArtifactResult> GenerateArtifactsAsync(ProvisionContext ctx, string outputDir, CancellationToken ct = default) =>
        Task.FromResult(new ArtifactResult { Success = true });

    public Task<ProvisionResult> DestroyAsync(ProvisionContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new ProvisionResult { Success = true });

    private string? Resolve(string template, ProvisionContext ctx, ResourcePlanResult resource, out string? error)
    {
        error = null;
        if (template.StartsWith(EnvPrefix, StringComparison.Ordinal))
        {
            var variable = template[EnvPrefix.Length..].Trim();
            var value = variable.Length == 0 ? null : _getEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(value))
            {
                error = $"environment variable '{variable}' is not set";
                return null;
            }
            return value;
        }

        return template
            .Replace("{app}", ctx.AppName)
            .Replace("{env}", ctx.Environment)
            .Replace("{team}", ctx.Team ?? "")
            .Replace("{region}", ctx.Defaults.Region ?? "")
            .Replace("{key}", resource.ResourceKey);
    }

    private static Dictionary<string, Dictionary<string, string>> ReadOutputs(
        IReadOnlyDictionary<string, JsonElement> config, List<string> errors, string label)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        if (!config.TryGetValue("outputs", out var outputs)) return result;

        if (outputs.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"'{label}' must be an object of resource key or type to output values");
            return result;
        }

        foreach (var selector in outputs.EnumerateObject())
        {
            if (selector.Value.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"'{label}.{selector.Name}' must be an object of output name to value");
                continue;
            }
            var map = new Dictionary<string, string>();
            foreach (var output in selector.Value.EnumerateObject())
            {
                if (output.Value.ValueKind != JsonValueKind.String)
                    errors.Add($"'{label}.{selector.Name}.{output.Name}' must be a string");
                else
                    map[output.Name] = output.Value.GetString()!;
            }
            result[selector.Name] = map;
        }
        return result;
    }
}
