using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Tests;

internal static class T
{
    public static JsonElement E<TValue>(TValue value) => JsonSerializer.SerializeToElement(value);

    public static ResourceDescriptor Res(string type, string? name = null, string? size = null, params (string, object)[] props) => new()
    {
        Type = type,
        Name = name,
        Size = size,
        Properties = props.ToDictionary(p => p.Item1, p => E(p.Item2))
    };

    public static ValidationContext Vctx(PlatformConfig? platform = null) =>
        new() { Platform = platform ?? new PlatformConfig(), Environment = "dev" };

    public static PlanContext Pctx(PlatformConfig? platform = null, PlatformDefaults? defaults = null) => new()
    {
        Platform = platform ?? new PlatformConfig { Defaults = new PlatformDefaults { Region = "SHOULD-NOT-BE-USED", Ha = true } },
        EnvironmentConfig = new EnvironmentConfig { Name = "dev" },
        Defaults = defaults ?? new PlatformDefaults { Region = "northeurope", Ha = false },
        Environment = "dev",
        AppName = "myapp"
    };

    public static PlatformConfig Catalog(string type, object settings, bool legacy = false)
    {
        var dict = JsonSerializer.SerializeToElement(settings).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var outer = new Dictionary<string, Dictionary<string, JsonElement>> { [type] = dict };
        return legacy ? new PlatformConfig { ProvisionerConfigs = outer } : new PlatformConfig { Resources = outer };
    }

    public static ResourcePlanResult Plan(string key, string type, params (string, object?)[] config) => new()
    {
        ResourceKey = key,
        ResourceType = type,
        Configuration = config.ToDictionary(c => c.Item1, c => c.Item2)
    };

    public static ProvisionContext Ctx(IEnumerable<ResourcePlanResult> resources, object? config = null,
        string? team = "payments", string? region = "northeurope", PlatformConfig? platform = null) => new()
    {
        AppName = "myapp",
        Environment = "dev",
        Team = team,
        Platform = platform ?? new PlatformConfig { Defaults = new PlatformDefaults { Region = "SHOULD-NOT-BE-USED" } },
        Defaults = new PlatformDefaults { Region = region },
        Resources = [.. resources],
        Config = config is null
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.SerializeToElement(config).EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
    };
}
