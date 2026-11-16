using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Core.Tests.Support;

public sealed class FakeResourceProvider : IResourceProvider
{
    public FakeResourceProvider(string type, string[] outputs, string[]? sensitive = null)
    {
        ResourceType = type;
        Outputs = outputs;
        Sensitive = sensitive ?? [];
    }

    public string ResourceType { get; }
    public string[] Outputs { get; }
    public string[] Sensitive { get; }
    public List<string> ValidationErrors { get; } = [];
    public Func<ResourceDescriptor, string>? KeyOverride { get; set; }
    public List<PlanContext> PlanContexts { get; } = [];

    public ResourceSchema GetSchema() => new()
    {
        ResourceType = ResourceType,
        ProvidedOutputs = [.. Outputs],
        SensitiveOutputs = [.. Sensitive]
    };

    public Task<ValidationResult> ValidateAsync(ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct = default) =>
        Task.FromResult(ValidationErrors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. ValidationErrors]));

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct = default)
    {
        PlanContexts.Add(ctx);
        return Task.FromResult(new ResourcePlanResult
        {
            ResourceKey = KeyOverride?.Invoke(resource) ?? resource.Key,
            ResourceType = resource.Type,
            Configuration = new() { ["size"] = resource.Size }
        });
    }
}

public sealed class FakeProvisioner : IProvisioner
{
    public FakeProvisioner(string name) => Name = name;

    public string Name { get; }

    /// <summary>Resource key → outputs this provisioner reports on apply.</summary>
    public Dictionary<string, Dictionary<string, string>> Outputs { get; } = new();
    public List<string> ApplyErrors { get; } = [];
    public bool ThrowOnDestroy { get; set; }
    public bool FailDestroy { get; set; }
    public List<GeneratedFile> FilesToGenerate { get; } = [];

    public List<ProvisionContext> ApplyCalls { get; } = [];
    public List<ProvisionContext> GenerateCalls { get; } = [];
    public List<ProvisionContext> DestroyCalls { get; } = [];

    public Task<ProvisionResult> ApplyAsync(ProvisionContext ctx, CancellationToken ct = default)
    {
        ApplyCalls.Add(ctx);
        if (ApplyErrors.Count > 0)
            return Task.FromResult(ProvisionResult.Failed([.. ApplyErrors]));
        return Task.FromResult(new ProvisionResult
        {
            Success = true,
            ResourceOutputs = ctx.Resources
                .Where(r => Outputs.ContainsKey(r.ResourceKey))
                .ToDictionary(r => r.ResourceKey, r => Outputs[r.ResourceKey])
        });
    }

    public Task<ArtifactResult> GenerateArtifactsAsync(ProvisionContext ctx, string outputDir, CancellationToken ct = default)
    {
        GenerateCalls.Add(ctx);
        return Task.FromResult(new ArtifactResult { Success = true, Files = [.. FilesToGenerate] });
    }

    public Task<ProvisionResult> DestroyAsync(ProvisionContext ctx, CancellationToken ct = default)
    {
        DestroyCalls.Add(ctx);
        if (ThrowOnDestroy)
            throw new InvalidOperationException($"{Name} exploded");
        return Task.FromResult(FailDestroy ? ProvisionResult.Failed($"{Name} could not destroy") : new ProvisionResult { Success = true });
    }
}

public sealed class FakeRuntime : IRuntimePlugin
{
    public FakeRuntime(string name = "kubernetes") => Name = name;

    public string Name { get; }
    public bool ThrowOnDestroy { get; set; }
    public Func<RuntimeContext, string, IReadOnlyList<GeneratedFile>>? GenerateFiles { get; set; }

    public List<RuntimeContext> ApplyCalls { get; } = [];
    public List<RuntimeContext> GenerateCalls { get; } = [];
    public List<RuntimeContext> StatusCalls { get; } = [];
    public List<RuntimeContext> DestroyCalls { get; } = [];

    public string? RenderError { get; set; }
    public List<RuntimeContext> RenderCalls { get; } = [];

    public Task<RuntimeArtifact> RenderAsync(RuntimeContext ctx, CancellationToken ct = default)
    {
        RenderCalls.Add(ctx);
        if (RenderError is not null)
            throw new InvalidOperationException(RenderError);
        return Task.FromResult(new RuntimeArtifact { Namespace = ctx.Namespace, Yaml = "" });
    }

    public Task<IReadOnlyList<GeneratedFile>> GenerateArtifactsAsync(RuntimeContext ctx, string outputDir, CancellationToken ct = default)
    {
        GenerateCalls.Add(ctx);
        IReadOnlyList<GeneratedFile> files = GenerateFiles?.Invoke(ctx, outputDir)
            ?? ctx.Workloads.Select(w => new GeneratedFile
            {
                Path = Path.Combine(outputDir, w.Name, "deployment.yaml"),
                Content = $"name: {w.Name}\nimage: {w.Image}\n",
                Format = "yaml"
            }).ToList();
        return Task.FromResult(files);
    }

    public Task<RuntimeApplyResult> ApplyAsync(RuntimeContext ctx, CancellationToken ct = default)
    {
        ApplyCalls.Add(ctx);
        return Task.FromResult(new RuntimeApplyResult
        {
            Namespace = ctx.Namespace,
            Applied = ctx.Workloads.Select(w => $"Deployment/{w.Name}").ToList(),
            Pruned = ["Deployment/old"]
        });
    }

    public Task<RuntimeStatus> GetStatusAsync(RuntimeContext ctx, CancellationToken ct = default)
    {
        StatusCalls.Add(ctx);
        return Task.FromResult(new RuntimeStatus { Namespace = ctx.Namespace, Exists = true });
    }

    public Task DestroyAsync(RuntimeContext ctx, CancellationToken ct = default)
    {
        DestroyCalls.Add(ctx);
        if (ThrowOnDestroy)
            throw new InvalidOperationException("runtime exploded");
        return Task.CompletedTask;
    }
}
