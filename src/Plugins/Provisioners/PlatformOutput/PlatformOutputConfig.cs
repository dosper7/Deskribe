namespace Deskribe.Plugins.Provisioner.PlatformOutput;

public sealed record PlatformOutputModuleConfig
{
    public string ModuleName { get; init; } = "";
    public string FileName { get; init; } = "terraform.tfvars.json";
    public Dictionary<string, string> Mappings { get; init; } = new();
}

public sealed record PlatformOutputProvisionerConfig
{
    public string BasePath { get; init; } = "{team}/{app}/{env}";
    public Dictionary<string, PlatformOutputModuleConfig> Modules { get; init; } = new();
}
