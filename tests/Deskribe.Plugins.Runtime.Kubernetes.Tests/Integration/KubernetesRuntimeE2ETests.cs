using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Deskribe.Sdk;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Xunit.Abstractions;

namespace Deskribe.Plugins.Runtime.Kubernetes.Tests.Integration;

/// <summary>
/// Runs against a real cluster (for example kind) when DESKRIBE_E2E_KUBECONFIG is set.
/// Optional: DESKRIBE_E2E_IMAGE (default nginx:1.27-alpine, an HTTP server on port 80).
/// No HTTP readiness probe (healthPath) is configured here: probes need kubelet-to-pod networking, which
/// sandboxed CI hosts may block. The probe rendering is covered by the unit tests.
/// All tests live in one class so xUnit runs them sequentially against the shared cluster.
/// </summary>
public sealed class KubernetesRuntimeE2ETests : IDisposable
{
    private const string FakeCrdLabel = "deskribe.dev/e2e-fake-crd";
    private const string ExternalSecretCrdName = "externalsecrets.external-secrets.io";

    private readonly ITestOutputHelper _output;
    private readonly string? _kubeconfig = Environment.GetEnvironmentVariable(KubernetesE2EFactAttribute.KubeconfigVariable);
    private readonly string _image = Environment.GetEnvironmentVariable("DESKRIBE_E2E_IMAGE") is { Length: > 0 } image ? image : "nginx:1.27-alpine";
    private readonly List<string> _namespacesToClean = [];
    private readonly Lazy<k8s.Kubernetes> _client;

    public KubernetesRuntimeE2ETests(ITestOutputHelper output)
    {
        _output = output;
        _client = new Lazy<k8s.Kubernetes>(() => new k8s.Kubernetes(ClientConfig()));
    }

    private k8s.Kubernetes Client => _client.Value;

    private KubernetesClientConfiguration ClientConfig() => KubernetesClientConfiguration.BuildConfigFromConfigFile(_kubeconfig);

    private KubernetesRuntimePlugin Plugin() => new(ClientConfig()) { Log = new TestOutputWriter(_output), PollInterval = TimeSpan.FromSeconds(1) };

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..6]}";

    private Dictionary<string, object> Config(int rolloutTimeoutSeconds = 120, bool createNamespace = true) => new()
    {
        ["containerPort"] = 80,
        ["servicePort"] = 8080,
        ["rolloutTimeoutSeconds"] = rolloutTimeoutSeconds,
        ["createNamespace"] = createNamespace
    };

    private WorkloadPlan Workload(string ns, string app, string name = "api", Dictionary<string, string>? env = null,
        IEnumerable<string>? secretKeys = null, string? image = null, string strategy = SecretsStrategies.Opaque) =>
        TestData.Workload(name: name, image: image ?? _image, env: env ?? [], secretKeys: secretKeys ?? [], strategy: strategy,
            app: app, environment: "e2e", ns: ns, team: "platform", replicas: 1) with { Cpu = "50m", Memory = "64Mi" };

    private RuntimeContext Context(string ns, string app, IEnumerable<WorkloadPlan> workloads, Dictionary<string, object>? config = null) =>
        TestData.Context(workloads, config ?? Config(), app: app, environment: "e2e", ns: ns, team: "platform");

    private RuntimeContext Empty(string ns, string app) => Context(ns, app, []);

    [KubernetesE2EFact]
    public async Task Lifecycle_Apply_Reapply_Prune_Status_Destroy()
    {
        var ns = Track(Unique("dsk-life"));
        const string app = "shop";
        var plugin = Plugin();

        // 1. First apply: namespace created, rollout completes, plain env inline and secret env via the Secret.
        var env = new Dictionary<string, string> { ["LOG_LEVEL"] = "debug", ["DB_PASSWORD"] = "first-value" };
        var result = await plugin.ApplyAsync(Context(ns, app, [Workload(ns, app, env: env, secretKeys: ["DB_PASSWORD"])]));

        Assert.Equal(
            [$"Namespace/{ns}", $"Secret/{ns}/api-env", $"Service/{ns}/api", $"Deployment/{ns}/api"],
            result.Applied);
        Assert.Empty(result.Pruned);

        var namespaceObject = await Client.CoreV1.ReadNamespaceAsync(ns);
        Assert.Equal("deskribe", namespaceObject.Metadata.Labels["app.kubernetes.io/managed-by"]);
        Assert.Equal($"{app}/e2e", namespaceObject.Metadata.Annotations["deskribe.dev/owner"]);

        var deployment = await Client.AppsV1.ReadNamespacedDeploymentAsync("api", ns);
        Assert.Equal(1, deployment.Status.ReadyReplicas);
        var container = deployment.Spec.Template.Spec.Containers.Single();
        Assert.Equal([("LOG_LEVEL", "debug")], container.Env.Select(e => (e.Name, e.Value)));
        Assert.Equal("api-env", container.EnvFrom.Single().SecretRef.Name);
        Assert.Null(container.ReadinessProbe);
        Assert.Equal("first-value", await SecretValue(ns, "api-env", "DB_PASSWORD"));

        var pod = await CurrentPod(ns, "api");
        Assert.Equal("api-env", pod.Spec.Containers.Single().EnvFrom.Single().SecretRef.Name);
        Assert.Contains(pod.Spec.Containers.Single().Env, e => e.Name == "LOG_LEVEL" && e.Value == "debug");
        await AssertEnvInPod(ns, pod.Metadata.Name, "DB_PASSWORD", "first-value");
        await AssertEnvInPod(ns, pod.Metadata.Name, "LOG_LEVEL", "debug");

        var status = await plugin.GetStatusAsync(Empty(ns, app));
        Assert.True(status.Exists);
        var workloadStatus = Assert.Single(status.Workloads);
        Assert.Equal("api", workloadStatus.Name);
        Assert.Equal(_image, workloadStatus.Image);
        Assert.True(workloadStatus.Ready);

        // 2. Re-apply unchanged: idempotent, no new rollout.
        var revision = deployment.Metadata.Annotations["deployment.kubernetes.io/revision"];
        await plugin.ApplyAsync(Context(ns, app, [Workload(ns, app, env: env, secretKeys: ["DB_PASSWORD"])]));
        deployment = await Client.AppsV1.ReadNamespacedDeploymentAsync("api", ns);
        Assert.Equal(revision, deployment.Metadata.Annotations["deployment.kubernetes.io/revision"]);

        // 3. Changed secret value only: pods roll and see the new value.
        env["DB_PASSWORD"] = "second-value";
        await plugin.ApplyAsync(Context(ns, app, [Workload(ns, app, env: env, secretKeys: ["DB_PASSWORD"])]));
        deployment = await Client.AppsV1.ReadNamespacedDeploymentAsync("api", ns);
        Assert.NotEqual(revision, deployment.Metadata.Annotations["deployment.kubernetes.io/revision"]);
        Assert.Equal("second-value", await SecretValue(ns, "api-env", "DB_PASSWORD"));
        var rolledPod = await CurrentPod(ns, "api");
        Assert.NotEqual(pod.Metadata.Name, rolledPod.Metadata.Name);
        await AssertEnvInPod(ns, rolledPod.Metadata.Name, "DB_PASSWORD", "second-value");

        // 4. Second workload added, then removed: it is pruned.
        var api = Workload(ns, app, env: env, secretKeys: ["DB_PASSWORD"]);
        result = await plugin.ApplyAsync(Context(ns, app, [api, Workload(ns, app, name: "worker", env: new() { ["MODE"] = "batch" })]));
        Assert.Contains($"Deployment/{ns}/worker", result.Applied);
        Assert.Contains($"Service/{ns}/worker", result.Applied);
        Assert.Equal(2, (await plugin.GetStatusAsync(Empty(ns, app))).Workloads.Count);

        result = await plugin.ApplyAsync(Context(ns, app, [api]));
        Assert.Equal([$"Deployment/{ns}/worker", $"Service/{ns}/worker"], result.Pruned.Order());
        Assert.False(await Exists(() => Client.AppsV1.ReadNamespacedDeploymentAsync("worker", ns)));
        Assert.False(await Exists(() => Client.CoreV1.ReadNamespacedServiceAsync("worker", ns)));

        // 5. Last secret variable removed: the env Secret is pruned and envFrom disappears.
        result = await plugin.ApplyAsync(Context(ns, app, [Workload(ns, app, env: new() { ["LOG_LEVEL"] = "info" })]));
        Assert.Equal([$"Secret/{ns}/api-env"], result.Pruned);
        Assert.False(await Exists(() => Client.CoreV1.ReadNamespacedSecretAsync("api-env", ns)));
        deployment = await Client.AppsV1.ReadNamespacedDeploymentAsync("api", ns);
        Assert.Null(deployment.Spec.Template.Spec.Containers.Single().EnvFrom);
        await AssertEnvInPod(ns, (await CurrentPod(ns, "api")).Metadata.Name, "LOG_LEVEL", "info");

        // 6. Destroy: Deskribe created the namespace for this app/env, so it is removed.
        await plugin.DestroyAsync(Empty(ns, app));
        Assert.False(await Exists(() => Client.AppsV1.ReadNamespacedDeploymentAsync("api", ns)));
        await AssertNamespaceGone(ns);
        Assert.False((await plugin.GetStatusAsync(Empty(ns, app))).Exists);

        // Destroying again is not an error.
        await plugin.DestroyAsync(Empty(ns, app));
    }

    [KubernetesE2EFact]
    public async Task Destroy_InSharedNamespace_RemovesOnlyThisApp_AndKeepsNamespaceItDoesNotOwn()
    {
        var ns = Track(Unique("dsk-shared"));
        var plugin = Plugin();

        // Namespace provided by the platform (no Deskribe owner marker).
        await Client.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = ns, Labels = new Dictionary<string, string> { ["team"] = "platform" } } });

        await plugin.ApplyAsync(Context(ns, "alpha", [Workload(ns, "alpha", name: "alpha", env: new() { ["K"] = "v", ["S"] = "a" }, secretKeys: ["S"])]));
        await plugin.ApplyAsync(Context(ns, "beta", [Workload(ns, "beta", name: "beta", env: new() { ["S"] = "b" }, secretKeys: ["S"])]));

        // An existing namespace is used as is.
        var namespaceObject = await Client.CoreV1.ReadNamespaceAsync(ns);
        Assert.Null((namespaceObject.Metadata.Annotations?.ContainsKey("deskribe.dev/owner") == true ? "present" : null));
        Assert.Equal(new Dictionary<string, string> { ["team"] = "platform", ["kubernetes.io/metadata.name"] = ns }, namespaceObject.Metadata.Labels);

        await plugin.DestroyAsync(Empty(ns, "alpha"));

        Assert.False(await Exists(() => Client.AppsV1.ReadNamespacedDeploymentAsync("alpha", ns)));
        Assert.False(await Exists(() => Client.CoreV1.ReadNamespacedServiceAsync("alpha", ns)));
        Assert.False(await Exists(() => Client.CoreV1.ReadNamespacedSecretAsync("alpha-env", ns)));
        Assert.True(await Exists(() => Client.AppsV1.ReadNamespacedDeploymentAsync("beta", ns)));
        Assert.True(await Exists(() => Client.CoreV1.ReadNamespacedServiceAsync("beta", ns)));
        Assert.True(await Exists(() => Client.CoreV1.ReadNamespacedSecretAsync("beta-env", ns)));
        Assert.True((await plugin.GetStatusAsync(Empty(ns, "beta"))).Exists);
        Assert.False((await plugin.GetStatusAsync(Empty(ns, "alpha"))).Exists);

        // Beta does not own the namespace either, so destroying it leaves the namespace.
        await plugin.DestroyAsync(Empty(ns, "beta"));
        Assert.False(await Exists(() => Client.AppsV1.ReadNamespacedDeploymentAsync("beta", ns)));
        Assert.Null((await Client.CoreV1.ReadNamespaceAsync(ns)).Metadata.DeletionTimestamp);
    }

    [KubernetesE2EFact]
    public async Task Destroy_OwnedNamespace_IsKeptWhileAnotherAppStillUsesIt()
    {
        var ns = Track(Unique("dsk-owned"));
        var plugin = Plugin();

        // gamma creates (and owns) the namespace; delta deploys into it afterwards.
        await plugin.ApplyAsync(Context(ns, "gamma", [Workload(ns, "gamma", name: "gamma")]));
        await plugin.ApplyAsync(Context(ns, "delta", [Workload(ns, "delta", name: "delta")]));
        Assert.Equal("gamma/e2e", (await Client.CoreV1.ReadNamespaceAsync(ns)).Metadata.Annotations["deskribe.dev/owner"]);

        await plugin.DestroyAsync(Empty(ns, "gamma"));
        Assert.False(await Exists(() => Client.AppsV1.ReadNamespacedDeploymentAsync("gamma", ns)));
        Assert.True(await Exists(() => Client.AppsV1.ReadNamespacedDeploymentAsync("delta", ns)));
        Assert.Null((await Client.CoreV1.ReadNamespaceAsync(ns)).Metadata.DeletionTimestamp);

        // delta does not own the namespace: its objects go, the namespace stays.
        await plugin.DestroyAsync(Empty(ns, "delta"));
        Assert.False(await Exists(() => Client.AppsV1.ReadNamespacedDeploymentAsync("delta", ns)));
        Assert.Null((await Client.CoreV1.ReadNamespaceAsync(ns)).Metadata.DeletionTimestamp);

        // Now that nothing else lives there, the owner's destroy removes it.
        await plugin.DestroyAsync(Empty(ns, "gamma"));
        await AssertNamespaceGone(ns);
    }

    [KubernetesE2EFact]
    public async Task FailingRollout_ReportsWorkloadAndCause_WithinShortTimeout()
    {
        var ns = Track(Unique("dsk-fail"));
        var plugin = Plugin();
        var badImage = _image.Split(':')[0] + ":deskribe-tag-that-does-not-exist";

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<KubernetesRolloutException>(() => plugin.ApplyAsync(
            Context(ns, "broken", [Workload(ns, "broken", name: "web", image: badImage)], Config(rolloutTimeoutSeconds: 60))));
        stopwatch.Stop();
        _output.WriteLine(ex.Message);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(90), $"took {stopwatch.Elapsed}");
        Assert.Equal(["web"], ex.FailedWorkloads);
        Assert.Contains("'web'", ex.Message);
        Assert.Contains($"Deployment/{ns}/web", ex.Message);
        Assert.Matches("ErrImagePull|ImagePullBackOff", ex.Message);
    }

    [KubernetesE2EFact]
    public async Task MissingNamespace_WithCreateNamespaceDisabled_FailsClearly()
    {
        var ns = Unique("dsk-missing");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Plugin().ApplyAsync(
            Context(ns, "app", [Workload(ns, "app")], Config(createNamespace: false))));
        Assert.Contains(ns, ex.Message);
        Assert.Contains("createNamespace", ex.Message);
        Assert.False(await Exists(() => Client.CoreV1.ReadNamespaceAsync(ns)));
    }

    [KubernetesE2EFact]
    public async Task ExternalSecrets_AppliedThroughCustomObjects_Pruned_AndKustomizeOutputApplies()
    {
        var plugin = Plugin();
        var installedFake = false;

        if (!await Exists(() => Client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(ExternalSecretCrdName)))
        {
            // Without the CRD: pruning/destroy skip the kind, and applying an ExternalSecret fails clearly.
            var plainNs = Track(Unique("dsk-nocrd"));
            await plugin.ApplyAsync(Context(plainNs, "nocrd", [Workload(plainNs, "nocrd")]));
            await plugin.DestroyAsync(Empty(plainNs, "nocrd"));
            await AssertNamespaceGone(plainNs);

            var esNs = Track(Unique("dsk-nocrd"));
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ApplyAsync(Context(esNs, "nocrd",
                [Workload(esNs, "nocrd", env: new() { ["TOKEN"] = "x" }, secretKeys: ["TOKEN"], strategy: SecretsStrategies.ExternalSecrets)])));
            Assert.Contains("ExternalSecret", missing.Message);
            Assert.Contains("not available", missing.Message);

            await InstallFakeExternalSecretCrd();
            installedFake = true;
        }

        try
        {
            var ns = Track(Unique("dsk-es"));
            const string app = "vaulted";
            await Client.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = ns } });

            // Stand-in for the External Secrets Operator: the target Secret already exists, unlabelled.
            await Client.CoreV1.CreateNamespacedSecretAsync(new V1Secret
            {
                Metadata = new V1ObjectMeta { Name = "api-env" },
                StringData = new Dictionary<string, string> { ["API_TOKEN"] = "from-the-store" }
            }, ns);

            var workload = Workload(ns, app, env: new() { ["API_TOKEN"] = "ignored", ["REGION"] = "eu" },
                secretKeys: ["API_TOKEN"], strategy: SecretsStrategies.ExternalSecrets);
            var config = Config();
            config["externalSecretsStore"] = "e2e-store";
            var result = await plugin.ApplyAsync(Context(ns, app, [workload], config));
            Assert.Contains($"ExternalSecret/{ns}/api-env", result.Applied);

            var es = JsonSerializer.SerializeToElement(await Client.CustomObjects.GetNamespacedCustomObjectAsync(
                "external-secrets.io", "v1beta1", ns, "externalsecrets", "api-env"));
            Assert.Equal("e2e-store", es.GetProperty("spec").GetProperty("secretStoreRef").GetProperty("name").GetString());
            Assert.Equal($"{app}-e2e-api_token", es.GetProperty("spec").GetProperty("data")[0].GetProperty("remoteRef").GetProperty("key").GetString());
            Assert.Equal("deskribe", es.GetProperty("metadata").GetProperty("labels").GetProperty("app.kubernetes.io/managed-by").GetString());
            Assert.DoesNotContain("ignored", es.GetRawText());
            await AssertEnvInPod(ns, (await CurrentPod(ns, "api")).Metadata.Name, "API_TOKEN", "from-the-store");

            // Secret key removed: the ExternalSecret is pruned; the operator-managed Secret is not Deskribe's to delete.
            result = await plugin.ApplyAsync(Context(ns, app, [workload with { SecretKeys = [] }], config));
            Assert.Equal([$"ExternalSecret/{ns}/api-env"], result.Pruned);
            Assert.False(await Exists(() => Client.CustomObjects.GetNamespacedCustomObjectAsync("external-secrets.io", "v1beta1", ns, "externalsecrets", "api-env")));
            Assert.True(await Exists(() => Client.CoreV1.ReadNamespacedSecretAsync("api-env", ns)));

            // The generated GitOps directory works with kubectl apply -k.
            if (FindKubectl() is not null)
            {
                var genNs = Track(Unique("dsk-gen"));
                var outDir = Path.Combine(AppContext.BaseDirectory, "e2e-artifacts", genNs);
                var files = await plugin.GenerateArtifactsAsync(Context(genNs, "gitops", [Workload(genNs, "gitops",
                    env: new() { ["A"] = "1", ["T"] = "x" }, secretKeys: ["T"], strategy: SecretsStrategies.ExternalSecrets)]), outDir);
                foreach (var file in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                    await File.WriteAllTextAsync(file.Path, file.Content);
                }
                var dir = Path.GetDirectoryName(files[0].Path)!;
                var (code, stdout, stderr) = await Kubectl("apply", "-k", dir);
                Assert.True(code == 0, stderr);
                _output.WriteLine(stdout);
                Assert.Contains($"namespace/{genNs}", stdout);
                Assert.Contains("externalsecret.external-secrets.io/api-env", stdout);
                Assert.Contains("deployment.apps/api", stdout);
                Assert.Contains("service/api", stdout);
                var deployed = await Client.AppsV1.ReadNamespacedDeploymentAsync("api", genNs);
                Assert.Equal("gitops", deployed.Metadata.Labels["deskribe.dev/app"]);
                Directory.Delete(outDir, recursive: true);
                await plugin.DestroyAsync(Empty(genNs, "gitops"));
                await AssertNamespaceGone(genNs);
            }

            await plugin.DestroyAsync(Empty(ns, app));
            Assert.Null((await Client.CoreV1.ReadNamespaceAsync(ns)).Metadata.DeletionTimestamp);
        }
        finally
        {
            if (installedFake)
                await Client.ApiextensionsV1.DeleteCustomResourceDefinitionAsync(ExternalSecretCrdName);
        }
    }

    private async Task InstallFakeExternalSecretCrd()
    {
        await Client.ApiextensionsV1.CreateCustomResourceDefinitionAsync(new V1CustomResourceDefinition
        {
            Metadata = new V1ObjectMeta { Name = ExternalSecretCrdName, Labels = new Dictionary<string, string> { [FakeCrdLabel] = "true" } },
            Spec = new V1CustomResourceDefinitionSpec
            {
                Group = "external-secrets.io",
                Scope = "Namespaced",
                Names = new V1CustomResourceDefinitionNames { Plural = "externalsecrets", Singular = "externalsecret", Kind = "ExternalSecret", ListKind = "ExternalSecretList" },
                Versions =
                [
                    new V1CustomResourceDefinitionVersion
                    {
                        Name = "v1beta1",
                        Served = true,
                        Storage = true,
                        Schema = new V1CustomResourceValidation
                        {
                            OpenAPIV3Schema = new V1JSONSchemaProps { Type = "object", XKubernetesPreserveUnknownFields = true }
                        }
                    }
                ]
            }
        });

        for (var i = 0; i < 60; i++)
        {
            var crd = await Client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(ExternalSecretCrdName);
            if (crd.Status?.Conditions?.Any(c => c.Type == "Established" && c.Status == "True") == true)
            {
                await Task.Delay(1000);
                return;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("Fake ExternalSecret CRD did not become established.");
    }

    private string Track(string ns)
    {
        _namespacesToClean.Add(ns);
        return ns;
    }

    private async Task<string> SecretValue(string ns, string name, string key)
    {
        var secret = await Client.CoreV1.ReadNamespacedSecretAsync(name, ns);
        return Encoding.UTF8.GetString(secret.Data[key]);
    }

    /// <summary>A ready, non-terminating pod of the Deployment's current template.</summary>
    private async Task<V1Pod> CurrentPod(string ns, string deploymentName)
    {
        var deployment = await Client.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, ns);
        var hash = deployment.Spec.Template.Metadata.Annotations["deskribe.dev/env-hash"];
        var selector = string.Join(",", deployment.Spec.Selector.MatchLabels.Select(kv => $"{kv.Key}={kv.Value}"));
        var pods = await Client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector);
        return pods.Items.Single(p =>
            p.Metadata.DeletionTimestamp is null
            && p.Metadata.Annotations?.TryGetValue("deskribe.dev/env-hash", out var podHash) == true && podHash == hash
            && p.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true);
    }

    private async Task AssertEnvInPod(string ns, string pod, string name, string expected)
    {
        if (FindKubectl() is null)
        {
            _output.WriteLine("kubectl not found; skipping in-container environment check.");
            return;
        }
        var (code, stdout, stderr) = await Kubectl("exec", "-n", ns, pod, "--", "printenv", name);
        Assert.True(code == 0, $"printenv {name} failed: {stderr}");
        Assert.Equal(expected, stdout.TrimEnd('\n'));
    }

    private async Task AssertNamespaceGone(string ns)
    {
        try
        {
            var namespaceObject = await Client.CoreV1.ReadNamespaceAsync(ns);
            Assert.NotNull(namespaceObject.Metadata.DeletionTimestamp);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static async Task<bool> Exists<T>(Func<Task<T>> read)
    {
        try
        {
            await read();
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string? FindKubectl() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(dir => Path.Combine(dir, "kubectl"))
        .FirstOrDefault(File.Exists);

    private async Task<(int Code, string Stdout, string Stderr)> Kubectl(params string[] args)
    {
        var psi = new ProcessStartInfo(FindKubectl()!) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--kubeconfig");
        psi.ArgumentList.Add(_kubeconfig!);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    public void Dispose()
    {
        if (!_client.IsValueCreated && _namespacesToClean.Count == 0)
            return;
        foreach (var ns in _namespacesToClean)
        {
            try
            {
                Client.CoreV1.DeleteNamespace(ns);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"cleanup of namespace {ns} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Client.Dispose();
    }

    private sealed class TestOutputWriter(ITestOutputHelper output) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            try
            {
                output.WriteLine(value ?? "");
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
