using System.Text;
using Deskribe.Sdk;

namespace Deskribe.Plugins.Runtime.Kubernetes.Tests;

using static TestData;

public class RenderingTests
{
    private readonly KubernetesRuntimePlugin _plugin = new() { Log = null };

    private async Task<List<Dictionary<object, object>>> Render(RuntimeContext ctx) =>
        ParseYaml((await _plugin.RenderAsync(ctx)).Yaml);

    [Fact]
    public async Task PlainEnv_IsInline_SecretEnv_GoesToOpaqueSecretViaEnvFrom()
    {
        var docs = await Render(Context());

        var deployment = Doc(docs, "Deployment", "api");
        var plain = PlainEnv(deployment);
        Assert.Equal("debug", plain["LOG_LEVEL"]);
        Assert.False(plain.ContainsKey("ConnectionStrings__Postgres"));
        Assert.Equal("api-env", Path(Container(deployment), "envFrom.0.secretRef.name"));

        var secret = Doc(docs, "Secret", "api-env");
        Assert.Equal("Opaque", secret["type"]);
        var data = (Dictionary<object, object>)secret["data"];
        Assert.Single(data);
        Assert.Equal("Host=pg;Database=orders;Token=tok-abc-42", Encoding.UTF8.GetString(Convert.FromBase64String((string)data["ConnectionStrings__Postgres"])));
        Assert.DoesNotContain(docs, d => (string)d["kind"] == "ExternalSecret");
    }

    [Fact]
    public async Task ExternalSecrets_RendersExternalSecret_WithTemplateKeys_AndIgnoresValues()
    {
        var workload = Workload(
            env: new() { ["LOG_LEVEL"] = "info", ["ConnectionStrings__Postgres"] = "SHOULD-NOT-APPEAR", ["Api__Key"] = "ALSO-HIDDEN" },
            secretKeys: ["ConnectionStrings__Postgres", "Api__Key"],
            strategy: SecretsStrategies.ExternalSecrets);
        var artifact = await _plugin.RenderAsync(Context([workload]));
        var docs = ParseYaml(artifact.Yaml);

        Assert.DoesNotContain("SHOULD-NOT-APPEAR", artifact.Yaml);
        Assert.DoesNotContain("ALSO-HIDDEN", artifact.Yaml);
        Assert.DoesNotContain(docs, d => (string)d["kind"] == "Secret");

        var es = Doc(docs, "ExternalSecret", "api-env");
        Assert.Equal("external-secrets.io/v1beta1", es["apiVersion"]);
        Assert.Equal("orders-dev", Path(es, "metadata.namespace"));
        Assert.Equal("default", Path(es, "spec.secretStoreRef.name"));
        Assert.Equal("ClusterSecretStore", Path(es, "spec.secretStoreRef.kind"));
        Assert.Equal("1h", Path(es, "spec.refreshInterval"));
        Assert.Equal("api-env", Path(es, "spec.target.name"));
        var data = ((List<object>)Path(es, "spec.data")!).Cast<Dictionary<object, object>>()
            .ToDictionary(d => (string)d["secretKey"], d => (string)Path(d, "remoteRef.key")!);
        Assert.Equal(2, data.Count);
        Assert.Equal("orders-dev-connectionstrings-postgres", data["ConnectionStrings__Postgres"]);
        Assert.Equal("orders-dev-api-key", data["Api__Key"]);

        var deployment = Doc(docs, "Deployment", "api");
        Assert.Equal("api-env", Path(Container(deployment), "envFrom.0.secretRef.name"));
        Assert.Equal(new Dictionary<string, string> { ["LOG_LEVEL"] = "info" }, PlainEnv(deployment));
        Assert.Contains("ExternalSecret/orders-dev/api-env", artifact.ResourceNames);
    }

    [Fact]
    public async Task ExternalSecrets_StoreAndKeyTemplate_FromRuntimeConfig_WorkloadStoreWins()
    {
        var config = new Dictionary<string, object>
        {
            ["externalSecretsStore"] = "platform-store",
            ["externalSecretsStoreKind"] = "SecretStore",
            ["externalSecretsRefreshInterval"] = "15m",
            ["externalSecretsKeyTemplate"] = "{team}/{app}/{env}/{workload}/{var}"
        };

        var docs = await Render(Context([Workload(strategy: SecretsStrategies.ExternalSecrets)], config));
        var es = Doc(docs, "ExternalSecret", "api-env");
        Assert.Equal("platform-store", Path(es, "spec.secretStoreRef.name"));
        Assert.Equal("SecretStore", Path(es, "spec.secretStoreRef.kind"));
        Assert.Equal("15m", Path(es, "spec.refreshInterval"));
        Assert.Equal("payments/orders/dev/api/connectionstrings-postgres", Path(es, "spec.data.0.remoteRef.key"));

        docs = await Render(Context([Workload(strategy: SecretsStrategies.ExternalSecrets, store: "vault")], config));
        Assert.Equal("vault", Path(Doc(docs, "ExternalSecret", "api-env"), "spec.secretStoreRef.name"));
    }

    [Fact]
    public async Task NoSecretKeys_RendersNoSecretObjectAndNoEnvFrom()
    {
        foreach (var strategy in SecretsStrategies.All)
        {
            var docs = await Render(Context([Workload(secretKeys: [], strategy: strategy)]));
            Assert.DoesNotContain(docs, d => (string)d["kind"] is "Secret" or "ExternalSecret");
            var deployment = Doc(docs, "Deployment", "api");
            Assert.Null(Path(Container(deployment), "envFrom"));
            Assert.Equal(2, PlainEnv(deployment).Count);
        }
    }

    [Theory]
    [InlineData("sealed-secrets")]
    [InlineData("Opaque")]
    [InlineData("")]
    public async Task UnknownStrategy_IsRejected(string strategy)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.RenderAsync(Context([Workload(strategy: strategy)])));
        Assert.Contains("secrets strategy", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingImage_IsRejected(string? image)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.RenderAsync(Context([Workload(image: image)])));
        Assert.Contains("'api'", ex.Message);
        Assert.Contains("image", ex.Message);
    }

    [Fact]
    public async Task Defaults_PortsAndNoProbe()
    {
        var docs = await Render(Context());
        var container = Container(Doc(docs, "Deployment", "api"));
        Assert.Equal("8080", Path(container, "ports.0.containerPort"));
        Assert.Equal("http", Path(container, "ports.0.name"));
        Assert.Null(Path(container, "readinessProbe"));
        Assert.Equal("ghcr.io/acme/api:1.0.0", container["image"]);

        var service = Doc(docs, "Service", "api");
        Assert.Equal("80", Path(service, "spec.ports.0.port"));
        Assert.Equal("8080", Path(service, "spec.ports.0.targetPort"));
        Assert.Equal("http", Path(service, "spec.ports.0.name"));
    }

    [Fact]
    public async Task RuntimeConfig_PortsAndHealthProbe_AreHonoured()
    {
        var docs = await Render(Context(config: new()
        {
            ["containerPort"] = 3000,
            ["servicePort"] = 8081,
            ["portName"] = "web",
            ["healthPath"] = "/healthz"
        }));

        var container = Container(Doc(docs, "Deployment", "api"));
        Assert.Equal("3000", Path(container, "ports.0.containerPort"));
        Assert.Equal("web", Path(container, "ports.0.name"));
        Assert.Equal("/healthz", Path(container, "readinessProbe.httpGet.path"));
        Assert.Equal("3000", Path(container, "readinessProbe.httpGet.port"));

        var service = Doc(docs, "Service", "api");
        Assert.Equal("8081", Path(service, "spec.ports.0.port"));
        Assert.Equal("3000", Path(service, "spec.ports.0.targetPort"));
        Assert.Equal("web", Path(service, "spec.ports.0.name"));
    }

    [Theory]
    [InlineData("containerPort", "abc")]
    [InlineData("containerPort", 70000)]
    [InlineData("portName", "Not_Valid")]
    [InlineData("createNamespace", 5)]
    [InlineData("rolloutTimeoutSeconds", 0)]
    [InlineData("externalSecretsStoreKind", "Vault")]
    [InlineData("healthPath", "healthz")]
    public async Task InvalidRuntimeConfig_IsRejected(string key, object value)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.RenderAsync(Context(config: new() { [key] = value })));
        Assert.Contains($"runtime.config.{key}", ex.Message);
    }

    [Fact]
    public async Task Namespace_IsRenderedWithOwnerMarker_OnlyWhenCreateNamespace()
    {
        var artifact = await _plugin.RenderAsync(Context());
        var ns = Doc(ParseYaml(artifact.Yaml), "Namespace", "orders-dev");
        Assert.Equal("deskribe", ((Dictionary<object, object>)Path(ns, "metadata.labels")!)["app.kubernetes.io/managed-by"]);
        Assert.Equal("orders/dev", ((Dictionary<object, object>)Path(ns, "metadata.annotations")!)["deskribe.dev/owner"]);
        Assert.Equal("Namespace/orders-dev", artifact.ResourceNames[0]);

        artifact = await _plugin.RenderAsync(Context(config: new() { ["createNamespace"] = false }));
        Assert.DoesNotContain(ParseYaml(artifact.Yaml), d => (string)d["kind"] == "Namespace");
        Assert.DoesNotContain("Namespace/orders-dev", artifact.ResourceNames);
    }

    [Fact]
    public async Task EveryNamespacedObject_CarriesDeskribeLabels_AndSelectorIsMinimal()
    {
        var docs = await Render(Context([Workload(), Workload(name: "worker", strategy: SecretsStrategies.ExternalSecrets)]));

        foreach (var doc in docs.Where(d => (string)d["kind"] != "Namespace"))
        {
            var labels = (Dictionary<object, object>)Path(doc, "metadata.labels")!;
            Assert.Equal("deskribe", labels["app.kubernetes.io/managed-by"]);
            Assert.Equal("orders", labels["deskribe.dev/app"]);
            Assert.Equal("dev", labels["deskribe.dev/environment"]);
            Assert.Equal("payments", labels["deskribe.dev/team"]);
            Assert.Contains((string)labels["app.kubernetes.io/name"], new[] { "api", "worker" });
        }

        var deployment = Doc(docs, "Deployment", "worker");
        var selector = (Dictionary<object, object>)Path(deployment, "spec.selector.matchLabels")!;
        Assert.Equal(2, selector.Count);
        Assert.Equal("orders", selector["deskribe.dev/app"]);
        Assert.Equal("worker", selector["app.kubernetes.io/name"]);
        var podLabels = (Dictionary<object, object>)Path(deployment, "spec.template.metadata.labels")!;
        Assert.All(selector, kv => Assert.Equal(kv.Value, podLabels[kv.Key]));
        Assert.Equal(selector.Count, ((Dictionary<object, object>)Path(Doc(docs, "Service", "worker"), "spec.selector")!).Count);
    }

    [Fact]
    public async Task NoTeam_OmitsTeamLabel()
    {
        var docs = await Render(Context([Workload(team: null)], team: null));
        var labels = (Dictionary<object, object>)Path(Doc(docs, "Deployment", "api"), "metadata.labels")!;
        Assert.False(labels.ContainsKey("deskribe.dev/team"));
    }

    [Fact]
    public async Task MultipleWorkloads_EachGetDeploymentServiceAndSecret()
    {
        var artifact = await _plugin.RenderAsync(Context([
            Workload("api", replicas: 3),
            Workload("worker", image: "ghcr.io/acme/worker:2", secretKeys: []),
        ]));

        Assert.Equal(
            ["Namespace/orders-dev", "Secret/orders-dev/api-env", "Service/orders-dev/api", "Deployment/orders-dev/api",
             "Service/orders-dev/worker", "Deployment/orders-dev/worker"],
            artifact.ResourceNames);
        var docs = ParseYaml(artifact.Yaml);
        Assert.Equal("3", Path(Doc(docs, "Deployment", "api"), "spec.replicas"));
        Assert.Equal("ghcr.io/acme/worker:2", Container(Doc(docs, "Deployment", "worker"))["image"]);
    }

    [Fact]
    public async Task DuplicateWorkloadNames_AreRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.RenderAsync(Context([Workload("api"), Workload("api")])));
    }

    [Theory]
    [InlineData("API")]
    [InlineData("1api")]
    [InlineData("api_v2")]
    public async Task InvalidWorkloadName_IsRejected(string name)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.RenderAsync(Context([Workload(name)])));
    }

    private static Dictionary<object, object> TemplateAnnotations(List<Dictionary<object, object>> docs, string workload = "api") =>
        (Dictionary<object, object>)Path(Doc(docs, "Deployment", workload), "spec.template.metadata.annotations")!;

    [Fact]
    public async Task EnvHash_ChangesWithPlainEnvAndSecretLayout_AndIsStable()
    {
        static string Hash(List<Dictionary<object, object>> docs) => (string)TemplateAnnotations(docs)["deskribe.dev/env-hash"];

        var baseline = Hash(await Render(Context()));
        Assert.Equal(baseline, Hash(await Render(Context())));

        var plainChanged = Hash(await Render(Context([Workload(env: new() { ["LOG_LEVEL"] = "info", ["ConnectionStrings__Postgres"] = "Host=pg;Database=orders;Token=tok-abc-42" })])));
        var keyAdded = Hash(await Render(Context([Workload(env: new() { ["LOG_LEVEL"] = "debug", ["ConnectionStrings__Postgres"] = "x", ["Other"] = "y" },
            secretKeys: ["ConnectionStrings__Postgres", "Other"])])));
        var strategyChanged = Hash(await Render(Context([Workload(strategy: SecretsStrategies.ExternalSecrets)])));
        Assert.Equal(4, new[] { baseline, plainChanged, keyAdded, strategyChanged }.Distinct().Count());
    }

    [Theory]
    [InlineData("other")]
    [InlineData("")]
    [InlineData("Host=pg;Database=orders;Token=tok-abc-43")]
    public async Task RenderedManifests_ExceptTheSecret_DoNotDependOnSecretValues(string otherValue)
    {
        var baseline = (await _plugin.RenderAsync(Context())).Yaml;
        var changed = (await _plugin.RenderAsync(Context([Workload(env: new() { ["LOG_LEVEL"] = "debug", ["ConnectionStrings__Postgres"] = otherValue })]))).Yaml;

        static string WithoutSecret(string yaml) =>
            string.Join("---\n", yaml.Split("---\n").Where(doc => !doc.Contains("\nkind: Secret\n", StringComparison.Ordinal)));
        Assert.NotEqual(baseline, changed);
        Assert.Equal(WithoutSecret(baseline), WithoutSecret(changed));
        Assert.Equal(["deskribe.dev/env-hash"], TemplateAnnotations(ParseYaml(changed)).Keys.Cast<string>());
    }

    [Fact]
    public async Task GeneratedArtifacts_DoNotDependOnExternalSecretValues()
    {
        const string dir = "out";
        RuntimeContext Ctx(string value) => Context([Workload(env: new() { ["LOG_LEVEL"] = "debug", ["TOKEN"] = value }, secretKeys: ["TOKEN"], strategy: SecretsStrategies.ExternalSecrets)]);
        var a = await _plugin.GenerateArtifactsAsync(Ctx("one"), dir);
        var b = await _plugin.GenerateArtifactsAsync(Ctx("two"), dir);
        Assert.Equal(a.Select(f => (f.Path, f.Content)), b.Select(f => (f.Path, f.Content)));
    }

    [Theory]
    [InlineData("api\n")]
    [InlineData("payments-api\n")]
    public async Task WorkloadName_WithTrailingNewline_IsRejected(string name)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.RenderAsync(Context([Workload(name)])));
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("app")]
    [InlineData("environment")]
    [InlineData("team")]
    [InlineData("secretKey")]
    [InlineData("portName")]
    public async Task OtherNames_WithTrailingNewline_AreRejected(string which)
    {
        var ctx = which switch
        {
            "namespace" => Context(ns: "orders-dev\n", workloads: [Workload(ns: "orders-dev\n")]),
            "app" => Context(app: "orders\n"),
            "environment" => Context(environment: "dev\n"),
            "team" => Context(team: "payments\n"),
            "secretKey" => Context([Workload(env: new() { ["TOKEN\n"] = "x" }, secretKeys: ["TOKEN\n"])]),
            _ => Context(config: new() { ["portName"] = "http\n" })
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.RenderAsync(ctx));
    }

    [Fact]
    public async Task YamlSignificantValues_CannotInjectStructure()
    {
        const string evil = "x\n---\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: injected\n# \"quoted\" 'single' {flow: [1,2]} &anchor *ref : - |";
        var workload = Workload(
            env: new() { ["PLAIN"] = evil, ["SECRET"] = evil },
            secretKeys: ["SECRET"]);
        var es = Workload(name: "worker", env: new() { ["SECRET"] = evil }, secretKeys: ["SECRET"], strategy: SecretsStrategies.ExternalSecrets);
        var config = new Dictionary<string, object>
        {
            ["externalSecretsStore"] = "store\nkind: Injected",
            ["externalSecretsKeyTemplate"] = "{app}\n  injected: true\n{var}"
        };

        var artifact = await _plugin.RenderAsync(Context([workload, es], config));
        var docs = ParseYaml(artifact.Yaml);

        Assert.Equal(7, docs.Count);
        Assert.DoesNotContain(docs, d => (string)d["kind"] == "ConfigMap");
        Assert.Equal(evil, PlainEnv(Doc(docs, "Deployment", "api"))["PLAIN"]);
        var secretData = (Dictionary<object, object>)Doc(docs, "Secret", "api-env")["data"];
        Assert.Equal(evil, Encoding.UTF8.GetString(Convert.FromBase64String((string)secretData["SECRET"])));

        var externalSecret = Doc(docs, "ExternalSecret", "worker-env");
        Assert.Equal("store\nkind: Injected", Path(externalSecret, "spec.secretStoreRef.name"));
        Assert.Equal("ClusterSecretStore", Path(externalSecret, "spec.secretStoreRef.kind"));
        Assert.Equal("orders\n  injected: true\nsecret", Path(externalSecret, "spec.data.0.remoteRef.key"));
    }

    [Fact]
    public async Task OpaqueSecretValues_AreNotRenderedInline()
    {
        var artifact = await _plugin.RenderAsync(Context());
        Assert.DoesNotContain("tok-abc-42", artifact.Yaml);
    }
}
