namespace Deskribe.Sdk;

public interface IRuntimeAdapter
{
    string Name { get; }
    Task<WorkloadManifest> RenderAsync(WorkloadPlan workload, CancellationToken ct = default);
    Task ApplyAsync(WorkloadManifest manifest, CancellationToken ct = default);
    Task DestroyAsync(string namespaceName, CancellationToken ct = default);
}

public record WorkloadPlan
{
    public required string AppName { get; init; }
    public required string Environment { get; init; }
    public required string Namespace { get; init; }
    public string? Image { get; init; }
    public int Replicas { get; init; } = 2;
    public string Cpu { get; init; } = "250m";
    public string Memory { get; init; } = "512Mi";
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();
}

public record WorkloadManifest
{
    public required string Namespace { get; init; }
    public required string Yaml { get; init; }
    public List<string> ResourceNames { get; init; } = [];
}
