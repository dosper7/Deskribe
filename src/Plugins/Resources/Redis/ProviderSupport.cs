using System.Text.Json;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.Redis;

internal static class ProviderSupport
{
    /// <summary>Capability catalog entry for the type, falling back per key to provisionerConfigs.</summary>
    public static JsonElement? Setting(PlatformConfig platform, string type, string key)
    {
        if (platform.Resources.TryGetValue(type, out var catalog) && catalog.TryGetValue(key, out var v))
            return v;
        if (platform.ProvisionerConfigs.TryGetValue(type, out var legacy) && legacy.TryGetValue(key, out var l))
            return l;
        return null;
    }

    public static HashSet<string>? StringSet(PlatformConfig platform, string type, string key)
    {
        if (Setting(platform, type, key) is not { ValueKind: JsonValueKind.Array } element)
            return null;
        return [.. element.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)];
    }

    public static string? String(PlatformConfig platform, string type, string key) =>
        Setting(platform, type, key) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public static string? Port(PlatformConfig platform, string type) =>
        Setting(platform, type, "port") switch
        {
            { ValueKind: JsonValueKind.String } e => e.GetString(),
            { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var p) => p.ToString(),
            _ => null
        };

    public static void RejectUnknown(ResourceDescriptor resource, IReadOnlyCollection<string> allowed, string label, List<string> errors)
    {
        foreach (var name in resource.Properties.Keys)
        {
            if (!allowed.Contains(name))
                errors.Add($"Unknown property '{name}' on {label} resource '{resource.Key}'. Allowed properties: {string.Join(", ", allowed)}");
        }
    }

    public static void CheckKind(ResourceDescriptor resource, string name, JsonValueKind kind, string expected, List<string> errors)
    {
        if (resource.Properties.TryGetValue(name, out var v) && v.ValueKind != kind)
            errors.Add($"Property '{name}' on resource '{resource.Key}' must be {expected}");
    }

    public static bool CheckBool(ResourceDescriptor resource, string name, List<string> errors)
    {
        if (resource.Properties.TryGetValue(name, out var v)
            && v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add($"Property '{name}' on resource '{resource.Key}' must be a boolean");
            return false;
        }
        return true;
    }

    public static bool? ReadBool(ResourceDescriptor resource, string name) =>
        resource.Properties.TryGetValue(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    public static string? ReadString(ResourceDescriptor resource, string name) =>
        resource.Properties.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
