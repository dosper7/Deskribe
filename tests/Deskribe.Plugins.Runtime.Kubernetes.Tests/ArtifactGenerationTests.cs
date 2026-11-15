using Deskribe.Sdk;

namespace Deskribe.Plugins.Runtime.Kubernetes.Tests;

using static TestData;

public class ArtifactGenerationTests
{
    private readonly KubernetesRuntimePlugin _plugin = new() { Log = null };
    private static readonly string Out = System.IO.Path.Combine("out", "k8s");

    private static RuntimeContext EsContext(string environment = "dev", string? team = "payments", params WorkloadPlan[] workloads) =>
        Context(
            workloads.Length > 0 ? workloads : [Workload(strategy: SecretsStrategies.ExternalSecrets, environment: environment, ns: $"orders-{environment}", team: team)],
            environment: environment, ns: $"orders-{environment}", team: team);

    [Fact]
    public async Task WritesSelfContainedDirectory_PerTeamAppEnvironment()
    {
        var files = await _plugin.GenerateArtifactsAsync(EsContext(), Out);

        var dir = System.IO.Path.Combine(Out, "payments", "orders", "dev");
        Assert.All(files, f => Assert.Equal(dir, System.IO.Path.GetDirectoryName(f.Path)));
        Assert.All(files, f => Assert.Equal("yaml", f.Format));
        Assert.Equal(
            ["namespace.yaml", "externalsecret-api-env.yaml", "service-api.yaml", "deployment-api.yaml", "kustomization.yaml"],
            files.Select(f => System.IO.Path.GetFileName(f.Path)));
    }

    [Fact]
    public async Task WithoutTeam_DirectoryIsAppEnvironment()
    {
        var files = await _plugin.GenerateArtifactsAsync(EsContext(team: null), Out);
        Assert.All(files, f => Assert.Equal(System.IO.Path.Combine(Out, "orders", "dev"), System.IO.Path.GetDirectoryName(f.Path)));
    }

    [Fact]
    public async Task Kustomization_ListsEveryManifestFile()
    {
        var files = await _plugin.GenerateArtifactsAsync(EsContext(workloads: [
            Workload("api", strategy: SecretsStrategies.ExternalSecrets),
            Workload("worker", secretKeys: [], strategy: SecretsStrategies.ExternalSecrets)
        ]), Out);

        var kustomization = files.Single(f => f.Path.EndsWith("kustomization.yaml"));
        var doc = ParseYaml(kustomization.Content).Single();
        Assert.Equal("kustomize.config.k8s.io/v1beta1", doc["apiVersion"]);
        Assert.Equal("Kustomization", doc["kind"]);
        var resources = ((List<object>)doc["resources"]).Cast<string>().ToList();
        var manifests = files.Where(f => f != kustomization).Select(f => System.IO.Path.GetFileName(f.Path)).ToList();
        Assert.Equal(manifests, resources);
        Assert.Equal(6, resources.Count);

        foreach (var file in files.Where(f => f != kustomization))
            Assert.Single(ParseYaml(file.Content));
    }

    [Fact]
    public async Task Environments_DoNotShareFiles()
    {
        var dev = await _plugin.GenerateArtifactsAsync(EsContext("dev"), Out);
        var prod = await _plugin.GenerateArtifactsAsync(EsContext("prod"), Out);
        Assert.Empty(dev.Select(f => f.Path).Intersect(prod.Select(f => f.Path)));
    }

    [Fact]
    public async Task OpaqueSecrets_AreRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.GenerateArtifactsAsync(Context(), Out));
        Assert.Contains("'api'", ex.Message);
        Assert.Contains("opaque", ex.Message);
        Assert.DoesNotContain("tok-abc-42", ex.Message);
    }

    [Fact]
    public async Task OpaqueStrategy_WithoutSecretKeys_IsAllowed()
    {
        var files = await _plugin.GenerateArtifactsAsync(Context([Workload(secretKeys: [])]), Out);
        Assert.DoesNotContain(files, f => f.Content.Contains("kind: Secret"));
        Assert.Contains(files, f => f.Path.EndsWith("deployment-api.yaml"));
    }

    [Fact]
    public async Task CreateNamespaceFalse_OmitsNamespaceFile()
    {
        var ctx = EsContext() with { Platform = Platform(new() { ["createNamespace"] = false }) };
        var files = await _plugin.GenerateArtifactsAsync(ctx, Out);
        Assert.DoesNotContain(files, f => f.Path.EndsWith("namespace.yaml"));
        Assert.DoesNotContain("namespace.yaml", files.Single(f => f.Path.EndsWith("kustomization.yaml")).Content);
    }

    [Fact]
    public async Task PathTraversal_InNames_IsRejected()
    {
        var ctx = EsContext() with { Team = ".." };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => _plugin.GenerateArtifactsAsync(ctx, Out));
    }
}
