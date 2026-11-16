namespace Deskribe.Core;

/// <summary>
/// Engine-wide defaults used when neither the environment config nor the platform config
/// specify a value. Hosts may bind this from configuration (section "Deskribe:Defaults")
/// or register a customized instance.
/// </summary>
public class EngineDefaults
{
    public string Runtime { get; set; } = "kubernetes";
    public string Region { get; set; } = "westeurope";
    public int Replicas { get; set; } = 2;
    public string Cpu { get; set; } = "250m";
    public string Memory { get; set; } = "512Mi";
    public string NamespacePattern { get; set; } = "{app}-{env}";
    public bool Ha { get; set; }
    public string SecretsStrategy { get; set; } = "opaque";
    public string? ExternalSecretsStore { get; set; }
}
