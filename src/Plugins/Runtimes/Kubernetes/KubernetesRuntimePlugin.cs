using System.Net;
using System.Text.Json;
using Deskribe.Sdk;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Deskribe.Plugins.Runtime.Kubernetes;

/// <summary>
/// Deploys Deskribe workloads to Kubernetes with server-side apply, prunes objects that are
/// no longer declared, and waits for rollouts. Settings come from <c>runtime.config</c> on every call.
/// </summary>
public class KubernetesRuntimePlugin : IRuntimePlugin
{
    public const string FieldManager = "deskribe";

    private static readonly HashSet<string> ImmediatelyFatalWaitingReasons = new(StringComparer.Ordinal)
    {
        "InvalidImageName", "ErrImageNeverPull"
    };

    private static readonly HashSet<string> ImagePullWaitingReasons = new(StringComparer.Ordinal)
    {
        "ErrImagePull", "ImagePullBackOff"
    };

    private static readonly TimeSpan ImagePullGrace = TimeSpan.FromSeconds(20);

    private readonly KubernetesClientConfiguration? _clientConfig;

    public KubernetesRuntimePlugin()
    {
    }

    public KubernetesRuntimePlugin(KubernetesClientConfiguration clientConfig)
    {
        _clientConfig = clientConfig;
    }

    public string Name => "kubernetes";

    /// <summary>Progress output (object names only, never values). Set to null to silence.</summary>
    public TextWriter? Log { get; set; } = Console.Error;

    /// <summary>How often rollout status is polled.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public Task<RuntimeArtifact> RenderAsync(RuntimeContext ctx, CancellationToken ct = default)
    {
        var rendered = ManifestRenderer.Render(ctx, KubernetesRuntimeSettings.From(ctx.Platform));
        return Task.FromResult(new RuntimeArtifact
        {
            Namespace = ctx.Namespace,
            Yaml = rendered.ToYaml(),
            ResourceNames = rendered.All.Select(o => o.Id).ToList()
        });
    }

    public Task<IReadOnlyList<GeneratedFile>> GenerateArtifactsAsync(RuntimeContext ctx, string outputDir, CancellationToken ct = default)
    {
        var withOpaqueSecrets = ctx.Workloads
            .Where(w => w.SecretKeys.Count > 0 && w.SecretsStrategy == SecretsStrategies.Opaque)
            .Select(w => w.Name)
            .ToList();
        if (withOpaqueSecrets.Count > 0)
            throw new InvalidOperationException(
                $"Cannot generate Kubernetes artifacts: workload(s) {string.Join(", ", withOpaqueSecrets.Select(n => $"'{n}'"))} have secret variables " +
                $"under the '{SecretsStrategies.Opaque}' secrets strategy, and secret values are never written to disk. " +
                $"Use the '{SecretsStrategies.ExternalSecrets}' strategy for GitOps artifacts, or deploy with apply.");

        var rendered = ManifestRenderer.Render(ctx, KubernetesRuntimeSettings.From(ctx.Platform));

        if (!string.IsNullOrEmpty(ctx.Team))
            KubernetesNames.RequirePathSegment("Team", ctx.Team);
        KubernetesNames.RequirePathSegment("App name", ctx.AppName);
        KubernetesNames.RequirePathSegment("Environment", ctx.Environment);

        var dir = string.IsNullOrEmpty(ctx.Team)
            ? Path.Combine(outputDir, ctx.AppName, ctx.Environment)
            : Path.Combine(outputDir, ctx.Team, ctx.AppName, ctx.Environment);

        var files = new List<GeneratedFile>();
        var resourceFiles = new List<string>();
        foreach (var obj in rendered.All)
        {
            var fileName = obj.Kind == "Namespace" ? "namespace.yaml" : $"{obj.Kind.ToLowerInvariant()}-{obj.Name}.yaml";
            resourceFiles.Add(fileName);
            files.Add(new GeneratedFile { Path = Path.Combine(dir, fileName), Content = obj.ToYaml(), Format = "yaml" });
        }

        var kustomization = new Dictionary<string, object>
        {
            ["apiVersion"] = "kustomize.config.k8s.io/v1beta1",
            ["kind"] = "Kustomization",
            ["resources"] = resourceFiles
        };
        files.Add(new GeneratedFile { Path = Path.Combine(dir, "kustomization.yaml"), Content = ManifestRenderer.ToYaml(kustomization), Format = "yaml" });

        return Task.FromResult<IReadOnlyList<GeneratedFile>>(files);
    }

    public async Task<RuntimeApplyResult> ApplyAsync(RuntimeContext ctx, CancellationToken ct = default)
    {
        var settings = KubernetesRuntimeSettings.From(ctx.Platform);
        if (ctx.Workloads.Count == 0)
            throw new InvalidOperationException(
                $"No workloads to apply for '{ctx.AppName}' in '{ctx.Environment}'. Use destroy to remove a deployment.");

        var rendered = ManifestRenderer.Render(ctx, settings);
        using var client = CreateClient();

        var applied = new List<string>();
        var pruned = new List<string>();
        if (await EnsureNamespaceAsync(client, ctx, settings, ct))
            applied.Add($"Namespace/{ctx.Namespace}");

        // A workload that moved from external-secrets to opaque: detach its Secret from the old ExternalSecret
        // before writing it, so the operator stops managing it and the garbage collector cannot delete it.
        foreach (var secret in rendered.Objects.Where(o => o.Kind == "Secret"))
            await ReleaseFromExternalSecretAsync(client, ctx, secret.Name, pruned, ct);

        var secretVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var obj in rendered.Objects)
        {
            if (obj.Body is V1Deployment deployment)
                StampSecretVersion(deployment, secretVersions);

            var result = await ApplyObjectAsync(client, obj, ct);
            if (result is V1Secret { Metadata.ResourceVersion: { } resourceVersion })
                secretVersions[obj.Name] = resourceVersion;
            applied.Add(obj.Id);
            Log?.WriteLine($"[kubernetes] applied {obj.Id}");
        }

        var deployments = rendered.Objects.Where(o => o.Kind == "Deployment").Select(o => o.Name).ToList();
        await WaitForRolloutAsync(client, ctx.Namespace, deployments, TimeSpan.FromSeconds(settings.RolloutTimeoutSeconds), ct);

        // Prune only after a successful rollout so the previous pods keep their Secret until they are replaced.
        var keep = rendered.Objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        pruned.AddRange(await PruneAsync(client, ctx, keep, ct));

        return new RuntimeApplyResult { Namespace = ctx.Namespace, Applied = applied, Pruned = pruned };
    }

    public async Task<RuntimeStatus> GetStatusAsync(RuntimeContext ctx, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var list = await client.AppsV1.ListNamespacedDeploymentAsync(
            ctx.Namespace, labelSelector: KubernetesLabels.Selector(ctx.AppName, ctx.Environment), cancellationToken: ct);

        var workloads = list.Items
            .OrderBy(d => d.Metadata.Name, StringComparer.Ordinal)
            .Select(d => new WorkloadStatus
            {
                Name = d.Metadata.Name,
                Image = d.Spec?.Template?.Spec?.Containers?.FirstOrDefault()?.Image,
                DesiredReplicas = d.Spec?.Replicas ?? 1,
                ReadyReplicas = CurrentReadyReplicas(d)
            })
            .ToList();

        return new RuntimeStatus { Namespace = ctx.Namespace, Exists = workloads.Count > 0, Workloads = workloads };
    }

    /// <summary>
    /// Ready replicas that run the current spec. During (or after a failed) rollout the
    /// previous version's pods are still ready, and counting them would report a
    /// deployment as healthy when the requested version never started.
    /// </summary>
    private static int CurrentReadyReplicas(V1Deployment deployment)
    {
        var status = deployment.Status;
        if (status is null || (status.ObservedGeneration ?? 0) < (deployment.Metadata.Generation ?? 0))
            return 0;
        return Math.Min(status.ReadyReplicas ?? 0, status.UpdatedReplicas ?? 0);
    }

    public async Task DestroyAsync(RuntimeContext ctx, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await PruneAsync(client, ctx, new HashSet<string>(), ct);

        V1Namespace ns;
        try
        {
            ns = await client.CoreV1.ReadNamespaceAsync(ctx.Namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return;
        }

        var owner = Get(ns.Metadata.Annotations, KubernetesLabels.OwnerAnnotation);
        if (owner != KubernetesLabels.Owner(ctx.AppName, ctx.Environment))
            return;

        var remaining = await FindForeignObjectsAsync(client, ctx, ct);
        if (remaining.Count > 0)
        {
            Log?.WriteLine($"[kubernetes] kept Namespace/{ctx.Namespace}: it still contains objects not deployed by this app ({string.Join(", ", remaining.Take(5))}{(remaining.Count > 5 ? ", ..." : "")})");
            return;
        }

        try
        {
            await client.CoreV1.DeleteNamespaceAsync(ctx.Namespace, cancellationToken: ct);
            Log?.WriteLine($"[kubernetes] deleted Namespace/{ctx.Namespace}");
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private k8s.Kubernetes CreateClient() =>
        new(_clientConfig ?? KubernetesClientConfiguration.BuildDefaultConfig());

    /// <returns>True when the namespace was created.</returns>
    private async Task<bool> EnsureNamespaceAsync(IKubernetes client, RuntimeContext ctx, KubernetesRuntimeSettings settings, CancellationToken ct)
    {
        try
        {
            var existing = await client.CoreV1.ReadNamespaceAsync(ctx.Namespace, cancellationToken: ct);
            if (existing.Status?.Phase == "Terminating" || existing.Metadata.DeletionTimestamp is not null)
                throw new InvalidOperationException($"Namespace '{ctx.Namespace}' is being deleted; wait for the deletion to finish and apply again.");
            return false;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Forbidden && !settings.CreateNamespace)
        {
            // Cannot read namespaces; trust the platform that it exists.
            return false;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            if (!settings.CreateNamespace)
                throw new InvalidOperationException(
                    $"Namespace '{ctx.Namespace}' does not exist and runtime.config.createNamespace is false. Ask the platform team to create it, or enable createNamespace.");
        }

        var ns = new V1Namespace
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = ctx.Namespace,
                Labels = ManifestRenderer.NamespaceLabels(),
                Annotations = ManifestRenderer.NamespaceAnnotations(ctx)
            }
        };

        try
        {
            await client.CoreV1.CreateNamespaceAsync(ns, fieldManager: FieldManager, cancellationToken: ct);
            Log?.WriteLine($"[kubernetes] created Namespace/{ctx.Namespace}");
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
        catch (HttpOperationException ex)
        {
            throw new InvalidOperationException($"Failed to create Namespace/{ctx.Namespace}: {Describe(ex, includeMessage: true)}", ex);
        }
    }

    /// <summary>
    /// Points the pod template at the current <c>resourceVersion</c> of the opaque Secret it reads, so a changed
    /// secret value rolls the pods without anything derived from the values appearing outside the Secret.
    /// Server-side apply of unchanged content keeps the resourceVersion, so an unchanged re-apply does not roll.
    /// </summary>
    private static void StampSecretVersion(V1Deployment deployment, IReadOnlyDictionary<string, string> secretVersions)
    {
        var template = deployment.Spec.Template;
        var secretNames = template.Spec.Containers
            .SelectMany(c => c.EnvFrom ?? [])
            .Select(e => e.SecretRef?.Name)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var versions = secretNames.Where(secretVersions.ContainsKey).Select(n => secretVersions[n]).ToList();
        if (versions.Count == 0)
            return;
        template.Metadata.Annotations ??= new Dictionary<string, string>();
        template.Metadata.Annotations[KubernetesLabels.SecretVersionAnnotation] = string.Join(",", versions);
    }

    /// <summary>
    /// Before Deskribe writes the opaque Secret <paramref name="name"/>, removes this app's (now undeclared)
    /// ExternalSecret of the same name with orphan propagation: the operator stops managing the Secret, and the
    /// garbage collector strips the ownerReference instead of deleting the Secret, so it exists throughout.
    /// Afterwards the Secret must have no owner left, otherwise it could be collected after Deskribe writes it.
    /// </summary>
    private async Task ReleaseFromExternalSecretAsync(IKubernetes client, RuntimeContext ctx, string name, List<string> pruned, CancellationToken ct)
    {
        var ns = ctx.Namespace;
        var id = $"Secret/{ns}/{name}";
        var externalSecretId = $"ExternalSecret/{ns}/{name}";
        string? releasedUid = null;

        var externalSecret = await ReadExternalSecretAsync(client, ns, name, ct);
        if (externalSecret is { } es)
        {
            if (!IsManagedBy(es.Labels, ctx))
                throw new InvalidOperationException(
                    $"Cannot apply {id}: {externalSecretId} exists and was not deployed by Deskribe for '{ctx.AppName}' in '{ctx.Environment}', " +
                    "so the External Secrets Operator would keep writing that Secret. Remove the ExternalSecret or rename the workload.");

            releasedUid = es.Uid;
            if (!es.Deleting)
            {
                try
                {
                    await client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                        ManifestRenderer.ExternalSecretGroup, ManifestRenderer.ExternalSecretVersion, ns, ManifestRenderer.ExternalSecretPlural, name,
                        new V1DeleteOptions { PropagationPolicy = "Orphan", Preconditions = new V1Preconditions { Uid = es.Uid } }, cancellationToken: ct);
                    pruned.Add(externalSecretId);
                    Log?.WriteLine($"[kubernetes] deleted {externalSecretId} (orphaning {id})");
                }
                catch (HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                {
                }
            }

            var deadline = DateTimeOffset.UtcNow + ExternalSecretDeletionTimeout;
            while (await ReadExternalSecretAsync(client, ns, name, ct) is { } remaining && remaining.Uid == es.Uid)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new InvalidOperationException(
                        $"Cannot apply {id}: {externalSecretId} is still being deleted after {(int)ExternalSecretDeletionTimeout.TotalSeconds}s" +
                        $"{(remaining.Finalizers.Count > 0 ? $" (finalizers: {string.Join(", ", remaining.Finalizers)})" : "")}. Nothing was changed; apply again once it is gone.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }

        V1Secret secret;
        try
        {
            secret = await client.CoreV1.ReadNamespacedSecretAsync(name, ns, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var owners = secret.Metadata.OwnerReferences ?? [];
        if (owners.Count == 0)
            return;

        // The garbage collector normally removes the reference itself while orphaning; also drop references to an
        // ExternalSecret that no longer exists, which would otherwise get the Secret collected.
        var stale = new List<V1OwnerReference>();
        var foreign = new List<V1OwnerReference>();
        foreach (var owner in owners)
        {
            var isExternalSecret = owner.Kind == "ExternalSecret" && owner.ApiVersion.StartsWith(ManifestRenderer.ExternalSecretGroup + "/", StringComparison.Ordinal);
            if (isExternalSecret && (owner.Uid == releasedUid || (owner.Name == name && await ReadExternalSecretAsync(client, ns, name, ct) is null)))
                stale.Add(owner);
            else
                foreign.Add(owner);
        }

        if (foreign.Count > 0)
            throw new InvalidOperationException(
                $"Cannot apply {id}: it is owned by {string.Join(", ", foreign.Select(o => $"{o.Kind}/{o.Name}"))}, which Deskribe does not manage, " +
                "and would be deleted together with its owner. Remove the owner or rename the workload.");

        var patch = new
        {
            metadata = new
            {
                resourceVersion = secret.Metadata.ResourceVersion,
                ownerReferences = stale.Select(o => new Dictionary<string, string> { ["$patch"] = "delete", ["uid"] = o.Uid }).ToList()
            }
        };
        try
        {
            await client.CoreV1.PatchNamespacedSecretAsync(new V1Patch(patch, V1Patch.PatchType.StrategicMergePatch), name, ns, fieldManager: FieldManager, cancellationToken: ct);
            Log?.WriteLine($"[kubernetes] removed owner reference from {id}");
        }
        catch (HttpOperationException ex)
        {
            throw new InvalidOperationException($"Failed to release {id} from {externalSecretId}: {Describe(ex, includeMessage: false)}", ex);
        }
    }

    private static readonly TimeSpan ExternalSecretDeletionTimeout = TimeSpan.FromSeconds(60);

    private sealed record ExternalSecretInfo(string Uid, IReadOnlyDictionary<string, string> Labels, bool Deleting, IReadOnlyList<string> Finalizers);

    /// <returns>Null when the object, or the ExternalSecret resource itself, does not exist.</returns>
    private static async Task<ExternalSecretInfo?> ReadExternalSecretAsync(IKubernetes client, string ns, string name, CancellationToken ct)
    {
        object result;
        try
        {
            result = await client.CustomObjects.GetNamespacedCustomObjectAsync(
                ManifestRenderer.ExternalSecretGroup, ManifestRenderer.ExternalSecretVersion, ns, ManifestRenderer.ExternalSecretPlural, name, ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var json = result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
        var metadata = json.GetProperty("metadata");
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Object)
            foreach (var label in l.EnumerateObject())
                labels[label.Name] = label.Value.GetString() ?? "";
        var finalizers = metadata.TryGetProperty("finalizers", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
        var deleting = metadata.TryGetProperty("deletionTimestamp", out var d) && d.ValueKind != JsonValueKind.Null;
        return new ExternalSecretInfo(metadata.GetProperty("uid").GetString()!, labels, deleting, finalizers);
    }

    private static bool IsManagedBy(IReadOnlyDictionary<string, string> labels, RuntimeContext ctx) =>
        labels.GetValueOrDefault(KubernetesLabels.ManagedBy) == KubernetesLabels.ManagedByValue
        && labels.GetValueOrDefault(KubernetesLabels.App) == ctx.AppName
        && labels.GetValueOrDefault(KubernetesLabels.Environment) == ctx.Environment;

    /// <returns>The object as returned by the API server.</returns>
    private static async Task<object> ApplyObjectAsync(IKubernetes client, ManifestObject obj, CancellationToken ct)
    {
        var patch = new V1Patch(obj.Body, V1Patch.PatchType.ApplyPatch);
        var ns = obj.Namespace!;
        try
        {
            return obj.Kind switch
            {
                "Secret" => await client.CoreV1.PatchNamespacedSecretAsync(patch, obj.Name, ns, fieldManager: FieldManager, force: true, cancellationToken: ct),
                "Service" => await client.CoreV1.PatchNamespacedServiceAsync(patch, obj.Name, ns, fieldManager: FieldManager, force: true, cancellationToken: ct),
                "Deployment" => await client.AppsV1.PatchNamespacedDeploymentAsync(patch, obj.Name, ns, fieldManager: FieldManager, force: true, cancellationToken: ct),
                "ExternalSecret" => await client.CustomObjects.PatchNamespacedCustomObjectAsync(
                    patch, ManifestRenderer.ExternalSecretGroup, ManifestRenderer.ExternalSecretVersion, ns,
                    ManifestRenderer.ExternalSecretPlural, obj.Name, fieldManager: FieldManager, force: true, cancellationToken: ct),
                _ => throw new InvalidOperationException($"Don't know how to apply {obj.Id}.")
            };
        }
        catch (HttpOperationException ex) when (obj.Kind == "ExternalSecret" && ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Failed to apply {obj.Id}: the ExternalSecret resource ({ManifestRenderer.ExternalSecretApiVersion}) is not available in the cluster. " +
                "Install the External Secrets Operator, or use the 'opaque' secrets strategy.", ex);
        }
        catch (HttpOperationException ex)
        {
            // Secret API errors are reduced to code and reason so no value can leak into the message.
            throw new InvalidOperationException($"Failed to apply {obj.Id}: {Describe(ex, includeMessage: obj.Kind != "Secret")}", ex);
        }
    }

    private async Task<List<string>> PruneAsync(IKubernetes client, RuntimeContext ctx, IReadOnlySet<string> keep, CancellationToken ct)
    {
        var ns = ctx.Namespace;
        var selector = KubernetesLabels.Selector(ctx.AppName, ctx.Environment);
        var pruned = new List<string>();
        var background = new V1DeleteOptions { PropagationPolicy = "Background" };

        async Task DeleteAsync(string id, Func<Task> delete)
        {
            if (keep.Contains(id))
                return;
            try
            {
                await delete();
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }
            pruned.Add(id);
            Log?.WriteLine($"[kubernetes] deleted {id}");
        }

        var deployments = await client.AppsV1.ListNamespacedDeploymentAsync(ns, labelSelector: selector, cancellationToken: ct);
        foreach (var d in deployments.Items.Where(IsLive))
            await DeleteAsync($"Deployment/{ns}/{d.Metadata.Name}",
                () => client.AppsV1.DeleteNamespacedDeploymentAsync(d.Metadata.Name, ns, background, cancellationToken: ct));

        var services = await client.CoreV1.ListNamespacedServiceAsync(ns, labelSelector: selector, cancellationToken: ct);
        foreach (var s in services.Items.Where(IsLive))
            await DeleteAsync($"Service/{ns}/{s.Metadata.Name}",
                () => client.CoreV1.DeleteNamespacedServiceAsync(s.Metadata.Name, ns, background, cancellationToken: ct));

        try
        {
            var externalSecrets = await client.CustomObjects.ListNamespacedCustomObjectAsync(
                ManifestRenderer.ExternalSecretGroup, ManifestRenderer.ExternalSecretVersion, ns, ManifestRenderer.ExternalSecretPlural,
                labelSelector: selector, cancellationToken: ct);
            foreach (var name in CustomObjectNames(externalSecrets))
            {
                // If the target Secret is declared again (now opaque), keep it: orphan instead of cascading.
                var options = keep.Contains($"Secret/{ns}/{name}") ? new V1DeleteOptions { PropagationPolicy = "Orphan" } : background;
                await DeleteAsync($"ExternalSecret/{ns}/{name}",
                    () => client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                        ManifestRenderer.ExternalSecretGroup, ManifestRenderer.ExternalSecretVersion, ns,
                        ManifestRenderer.ExternalSecretPlural, name, options, cancellationToken: ct));
            }
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // ExternalSecret CRD not installed: nothing of that kind can exist.
        }

        // Secrets owned by another object (for example created by an ExternalSecret) are garbage-collected with their owner.
        // A Secret that a declared ExternalSecret targets (the workload moved from opaque to external-secrets) is left
        // for the operator to take over: deleting it would break pods until the operator recreates it.
        var secrets = await client.CoreV1.ListNamespacedSecretAsync(ns, labelSelector: selector, cancellationToken: ct);
        foreach (var s in secrets.Items.Where(s => IsLive(s) && (s.Metadata.OwnerReferences?.Count ?? 0) == 0
                                                    && !keep.Contains($"ExternalSecret/{ns}/{s.Metadata.Name}")))
            await DeleteAsync($"Secret/{ns}/{s.Metadata.Name}",
                () => client.CoreV1.DeleteNamespacedSecretAsync(s.Metadata.Name, ns, background, cancellationToken: ct));

        return pruned;
    }

    private static string? Get(IDictionary<string, string>? values, string key) =>
        values is not null && values.TryGetValue(key, out var value) ? value : null;

    private static bool IsLive(IMetadata<V1ObjectMeta> obj) => obj.Metadata.DeletionTimestamp is null;

    private static IEnumerable<string> CustomObjectNames(object list)
    {
        var json = list is JsonElement element ? element : JsonSerializer.SerializeToElement(list);
        if (!json.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("metadata", out var metadata))
                continue;
            if (metadata.TryGetProperty("deletionTimestamp", out var deleting) && deleting.ValueKind != JsonValueKind.Null)
                continue;
            if (metadata.TryGetProperty("name", out var name) && name.GetString() is { } value)
                yield return value;
        }
    }

    /// <summary>Lists objects in the namespace that this app did not deploy and that nothing else owns.</summary>
    private static async Task<List<string>> FindForeignObjectsAsync(IKubernetes client, RuntimeContext ctx, CancellationToken ct)
    {
        var ns = ctx.Namespace;
        var found = new List<string>();

        bool Foreign(V1ObjectMeta m) =>
            m.DeletionTimestamp is null
            && (m.OwnerReferences?.Count ?? 0) == 0
            && !(Get(m.Labels, KubernetesLabels.ManagedBy) == KubernetesLabels.ManagedByValue
                 && Get(m.Labels, KubernetesLabels.App) == ctx.AppName
                 && Get(m.Labels, KubernetesLabels.Environment) == ctx.Environment);

        void Add(string kind, IEnumerable<V1ObjectMeta> items) =>
            found.AddRange(items.Where(Foreign).Select(m => $"{kind}/{ns}/{m.Name}"));

        Add("Deployment", (await client.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: ct)).Items.Select(i => i.Metadata));
        Add("StatefulSet", (await client.AppsV1.ListNamespacedStatefulSetAsync(ns, cancellationToken: ct)).Items.Select(i => i.Metadata));
        Add("DaemonSet", (await client.AppsV1.ListNamespacedDaemonSetAsync(ns, cancellationToken: ct)).Items.Select(i => i.Metadata));
        Add("CronJob", (await client.BatchV1.ListNamespacedCronJobAsync(ns, cancellationToken: ct)).Items.Select(i => i.Metadata));
        Add("Job", (await client.BatchV1.ListNamespacedJobAsync(ns, cancellationToken: ct)).Items.Select(i => i.Metadata));
        Add("Pod", (await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct)).Items.Select(i => i.Metadata));
        Add("Service", (await client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct)).Items.Select(i => i.Metadata));
        Add("PersistentVolumeClaim", (await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: ct)).Items.Select(i => i.Metadata));
        Add("ConfigMap", (await client.CoreV1.ListNamespacedConfigMapAsync(ns, cancellationToken: ct)).Items
            .Where(c => c.Metadata.Name != "kube-root-ca.crt").Select(i => i.Metadata));
        Add("Secret", (await client.CoreV1.ListNamespacedSecretAsync(ns, cancellationToken: ct)).Items
            .Where(s => s.Type != "kubernetes.io/service-account-token").Select(i => i.Metadata));

        try
        {
            var externalSecrets = await client.CustomObjects.ListNamespacedCustomObjectAsync(
                ManifestRenderer.ExternalSecretGroup, ManifestRenderer.ExternalSecretVersion, ns, ManifestRenderer.ExternalSecretPlural,
                labelSelector: $"!{KubernetesLabels.App}", cancellationToken: ct);
            found.AddRange(CustomObjectNames(externalSecrets).Select(n => $"ExternalSecret/{ns}/{n}"));
            var otherApps = await client.CustomObjects.ListNamespacedCustomObjectAsync(
                ManifestRenderer.ExternalSecretGroup, ManifestRenderer.ExternalSecretVersion, ns, ManifestRenderer.ExternalSecretPlural,
                labelSelector: $"{KubernetesLabels.App},{KubernetesLabels.App}!={ctx.AppName}", cancellationToken: ct);
            found.AddRange(CustomObjectNames(otherApps).Select(n => $"ExternalSecret/{ns}/{n}"));
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
        }

        return found;
    }

    private async Task WaitForRolloutAsync(IKubernetes client, string ns, IReadOnlyList<string> deployments, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var pending = new List<string>(deployments);
        var pullErrorSince = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var failures = new List<(string Name, string Message)>();

        while (true)
        {
            foreach (var name in pending.ToList())
            {
                var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct);
                if (IsRolledOut(deployment))
                {
                    pending.Remove(name);
                    Log?.WriteLine($"[kubernetes] rolled out Deployment/{ns}/{name}");
                    continue;
                }

                var progressing = deployment.Status?.Conditions?.FirstOrDefault(c => c.Type == "Progressing");
                if (progressing is { Status: "False", Reason: "ProgressDeadlineExceeded" })
                {
                    failures.Add((name, "progress deadline exceeded"));
                    pending.Remove(name);
                    continue;
                }

                var pods = await CurrentPodsAsync(client, deployment, ct);
                var waiting = pods
                    .SelectMany(p => p.Status?.ContainerStatuses ?? [])
                    .Select(c => c.State?.Waiting?.Reason)
                    .OfType<string>()
                    .ToList();

                var fatal = waiting.FirstOrDefault(ImmediatelyFatalWaitingReasons.Contains);
                if (fatal is null && waiting.FirstOrDefault(ImagePullWaitingReasons.Contains) is { } pullError)
                {
                    var since = pullErrorSince.TryGetValue(name, out var s) ? s : pullErrorSince[name] = DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - since >= ImagePullGrace)
                        fatal = pullError;
                }
                else
                {
                    pullErrorSince.Remove(name);
                }

                if (fatal is not null)
                {
                    failures.Add((name, $"pods cannot start ({fatal})"));
                    pending.Remove(name);
                }
            }

            if (pending.Count == 0 || DateTimeOffset.UtcNow >= deadline)
                break;

            var delay = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(delay < PollInterval ? delay : PollInterval, ct);
        }

        foreach (var name in pending)
            failures.Add((name, $"rollout did not complete within {(int)timeout.TotalSeconds}s"));

        if (failures.Count == 0)
            return;

        var messages = new List<string>();
        foreach (var (name, summary) in failures)
        {
            var diagnosis = await DiagnoseAsync(client, ns, name, ct);
            messages.Add($"Workload '{name}' (Deployment/{ns}/{name}) failed to roll out: {summary}.{(diagnosis.Count > 0 ? "\n  " + string.Join("\n  ", diagnosis) : "")}");
        }

        throw new KubernetesRolloutException(string.Join("\n", messages), failures.Select(f => f.Name).ToList());
    }

    private static bool IsRolledOut(V1Deployment d)
    {
        var desired = d.Spec?.Replicas ?? 1;
        var status = d.Status;
        if (status is null || (status.ObservedGeneration ?? 0) < (d.Metadata.Generation ?? 0))
            return false;
        var updated = status.UpdatedReplicas ?? 0;
        var total = status.Replicas ?? 0;
        var available = status.AvailableReplicas ?? 0;
        return updated >= desired && total <= updated && available >= updated;
    }

    /// <summary>Pods belonging to the Deployment's newest ReplicaSet (all matching pods if it cannot be determined).</summary>
    private static async Task<IList<V1Pod>> CurrentPodsAsync(IKubernetes client, V1Deployment d, CancellationToken ct)
    {
        var ns = d.Metadata.NamespaceProperty;
        var matchLabels = d.Spec?.Selector?.MatchLabels;
        if (matchLabels is null || matchLabels.Count == 0)
            return [];
        var selector = string.Join(",", matchLabels.Select(kv => $"{kv.Key}={kv.Value}"));

        const string revisionAnnotation = "deployment.kubernetes.io/revision";
        var revision = Get(d.Metadata.Annotations, revisionAnnotation);
        var replicaSets = await client.AppsV1.ListNamespacedReplicaSetAsync(ns, labelSelector: selector, cancellationToken: ct);
        var current = replicaSets.Items.FirstOrDefault(rs =>
            rs.Metadata.OwnerReferences?.Any(o => o.Uid == d.Metadata.Uid) == true
            && revision is not null
            && Get(rs.Metadata.Annotations, revisionAnnotation) == revision);
        var hash = Get(current?.Metadata.Labels, "pod-template-hash");
        if (hash is not null)
            selector += $",pod-template-hash={hash}";

        return (await client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: ct)).Items;
    }

    private static async Task<List<string>> DiagnoseAsync(IKubernetes client, string ns, string name, CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            var d = await client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct);
            var desired = d.Spec?.Replicas ?? 1;
            lines.Add($"replicas: {d.Status?.UpdatedReplicas ?? 0}/{desired} updated, {d.Status?.AvailableReplicas ?? 0}/{desired} available");

            foreach (var c in d.Status?.Conditions ?? [])
            {
                var healthy = c.Status == "True" && (c.Type != "Progressing" || c.Reason == "NewReplicaSetAvailable");
                if (!healthy && c.Type is "Progressing" or "Available" or "ReplicaFailure")
                    lines.Add($"condition {c.Type}={c.Status} ({c.Reason}): {c.Message}");
            }

            var pods = await CurrentPodsAsync(client, d, ct);
            foreach (var pod in pods.Take(5))
            {
                var podName = pod.Metadata.Name;
                foreach (var cond in pod.Status?.Conditions ?? [])
                {
                    if (cond is { Type: "PodScheduled", Status: "False" })
                        lines.Add($"pod {podName}: not scheduled ({cond.Reason}): {cond.Message}");
                }

                foreach (var cs in pod.Status?.ContainerStatuses ?? [])
                {
                    if (cs.State?.Waiting is { } waiting)
                        lines.Add($"pod {podName}: container '{cs.Name}' waiting: {waiting.Reason}{(string.IsNullOrEmpty(waiting.Message) ? "" : $" - {waiting.Message}")}");
                    if (cs.LastState?.Terminated is { } last)
                        lines.Add($"pod {podName}: container '{cs.Name}' last terminated: {last.Reason} (exit code {last.ExitCode}), restarts {cs.RestartCount}");
                    if (cs.State?.Running is not null && cs.Ready != true)
                        lines.Add($"pod {podName}: container '{cs.Name}' running but not ready");
                }

                var events = await client.CoreV1.ListNamespacedEventAsync(
                    ns, fieldSelector: $"involvedObject.kind=Pod,involvedObject.name={podName},type=Warning", cancellationToken: ct);
                foreach (var e in events.Items.OrderByDescending(e => e.LastTimestamp ?? e.EventTime ?? e.Metadata.CreationTimestamp).Take(2))
                    lines.Add($"pod {podName}: event {e.Reason}: {e.Message}");
            }
        }
        catch (HttpOperationException ex)
        {
            lines.Add($"could not read diagnosis: {Describe(ex, includeMessage: true)}");
        }

        return lines.Distinct().ToList();
    }

    private static string Describe(HttpOperationException ex, bool includeMessage)
    {
        var code = (int)ex.Response.StatusCode;
        string? reason = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(ex.Response.Content ?? "");
            if (doc.RootElement.TryGetProperty("reason", out var r)) reason = r.GetString();
            if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString();
        }
        catch (JsonException)
        {
        }

        var text = $"HTTP {code}{(reason is null ? "" : $" {reason}")}";
        return includeMessage && !string.IsNullOrEmpty(message) ? $"{text}: {message}" : text;
    }
}
