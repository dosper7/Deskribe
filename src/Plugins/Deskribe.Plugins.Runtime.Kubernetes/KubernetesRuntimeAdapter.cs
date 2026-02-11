using System.Text;
using Deskribe.Sdk;

namespace Deskribe.Plugins.Runtime.Kubernetes;

public class KubernetesRuntimeAdapter : IRuntimeAdapter
{
    public string Name => "kubernetes";

    public Task<WorkloadManifest> RenderAsync(WorkloadPlan workload, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // Namespace
        sb.AppendLine("---");
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Namespace");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {workload.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    app.kubernetes.io/managed-by: deskribe");

        // Secret for env vars (if any non-reference env vars)
        if (workload.EnvironmentVariables.Count > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine("apiVersion: v1");
            sb.AppendLine("kind: Secret");
            sb.AppendLine("metadata:");
            sb.AppendLine($"  name: {workload.AppName}-env");
            sb.AppendLine($"  namespace: {workload.Namespace}");
            sb.AppendLine("type: Opaque");
            sb.AppendLine("stringData:");
            foreach (var (key, value) in workload.EnvironmentVariables)
            {
                sb.AppendLine($"  {key}: \"{EscapeYaml(value)}\"");
            }
        }

        // Deployment
        sb.AppendLine("---");
        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: Deployment");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {workload.AppName}");
        sb.AppendLine($"  namespace: {workload.Namespace}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    app: {workload.AppName}");
        sb.AppendLine($"    app.kubernetes.io/managed-by: deskribe");
        sb.AppendLine("spec:");
        sb.AppendLine($"  replicas: {workload.Replicas}");
        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");
        sb.AppendLine($"      app: {workload.AppName}");
        sb.AppendLine("  template:");
        sb.AppendLine("    metadata:");
        sb.AppendLine("      labels:");
        sb.AppendLine($"        app: {workload.AppName}");
        sb.AppendLine("    spec:");
        sb.AppendLine("      containers:");
        sb.AppendLine($"      - name: {workload.AppName}");
        sb.AppendLine($"        image: {workload.Image ?? "nginx:latest"}");
        sb.AppendLine("        resources:");
        sb.AppendLine("          requests:");
        sb.AppendLine($"            cpu: {workload.Cpu}");
        sb.AppendLine($"            memory: {workload.Memory}");
        sb.AppendLine("          limits:");
        sb.AppendLine($"            cpu: {workload.Cpu}");
        sb.AppendLine($"            memory: {workload.Memory}");

        if (workload.EnvironmentVariables.Count > 0)
        {
            sb.AppendLine("        envFrom:");
            sb.AppendLine($"        - secretRef:");
            sb.AppendLine($"            name: {workload.AppName}-env");
        }

        sb.AppendLine("        ports:");
        sb.AppendLine("        - containerPort: 8080");
        sb.AppendLine("          name: http");

        // Service
        sb.AppendLine("---");
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Service");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {workload.AppName}");
        sb.AppendLine($"  namespace: {workload.Namespace}");
        sb.AppendLine("spec:");
        sb.AppendLine("  selector:");
        sb.AppendLine($"    app: {workload.AppName}");
        sb.AppendLine("  ports:");
        sb.AppendLine("  - port: 80");
        sb.AppendLine("    targetPort: 8080");
        sb.AppendLine("    name: http");

        var resourceNames = new List<string>
        {
            $"Namespace/{workload.Namespace}",
            $"Deployment/{workload.Namespace}/{workload.AppName}",
            $"Service/{workload.Namespace}/{workload.AppName}"
        };

        if (workload.EnvironmentVariables.Count > 0)
            resourceNames.Insert(1, $"Secret/{workload.Namespace}/{workload.AppName}-env");

        return Task.FromResult(new WorkloadManifest
        {
            Namespace = workload.Namespace,
            Yaml = sb.ToString(),
            ResourceNames = resourceNames
        });
    }

    public Task ApplyAsync(WorkloadManifest manifest, CancellationToken ct)
    {
        // MVP: Print what would be applied
        // In production, this would use KubernetesClient to apply the manifest
        Console.WriteLine($"[Kubernetes] Would apply to namespace: {manifest.Namespace}");
        Console.WriteLine($"[Kubernetes] Resources:");
        foreach (var name in manifest.ResourceNames)
        {
            Console.WriteLine($"  - {name}");
        }
        Console.WriteLine();
        Console.WriteLine(manifest.Yaml);

        return Task.CompletedTask;
    }

    public Task DestroyAsync(string namespaceName, CancellationToken ct)
    {
        Console.WriteLine($"[Kubernetes] Would delete namespace: {namespaceName}");
        return Task.CompletedTask;
    }

    private static string EscapeYaml(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
