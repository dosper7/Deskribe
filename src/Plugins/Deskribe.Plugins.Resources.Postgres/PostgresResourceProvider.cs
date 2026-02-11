using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Resources.Postgres;

public class PostgresResourceProvider : IResourceProvider
{
    public string ResourceType => "postgres";

    private static readonly HashSet<string> ValidSizes = ["xs", "s", "m", "l", "xl"];
    private static readonly HashSet<string> ValidVersions = ["14", "15", "16", "17"];

    public Task<ValidationResult> ValidateAsync(DeskribeResource resource, ValidationContext ctx, CancellationToken ct)
    {
        if (resource is not PostgresResource pg)
            return Task.FromResult(ValidationResult.Invalid($"Expected PostgresResource but got {resource.GetType().Name}"));

        var errors = new List<string>();

        if (pg.Size is not null && !ValidSizes.Contains(pg.Size))
            errors.Add($"Invalid Postgres size '{pg.Size}'. Valid sizes: {string.Join(", ", ValidSizes)}");

        if (pg.Version is not null && !ValidVersions.Contains(pg.Version))
            errors.Add($"Invalid Postgres version '{pg.Version}'. Valid versions: {string.Join(", ", ValidVersions)}");

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(DeskribeResource resource, PlanContext ctx, CancellationToken ct)
    {
        var pg = resource as PostgresResource;
        var size = pg?.Size ?? "s";
        var version = pg?.Version ?? "16";
        var ha = pg?.Ha ?? ctx.EnvironmentConfig.Defaults.Ha ?? false;

        var releaseName = $"{ctx.AppName}-postgres";
        var ns = ctx.Platform.Defaults.NamespacePattern
            .Replace("{app}", ctx.AppName)
            .Replace("{env}", ctx.Environment);

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "postgres",
            Action = "create",
            PlannedOutputs = new Dictionary<string, string>
            {
                ["connectionString"] = $"Host={releaseName}.{ns}.svc.cluster.local;Port=5432;Database={ctx.AppName};Username=app;Password=<generated>",
                ["host"] = $"{releaseName}.{ns}.svc.cluster.local",
                ["port"] = "5432"
            },
            Configuration = new Dictionary<string, object?>
            {
                ["helmRelease"] = releaseName,
                ["helmChart"] = "oci://registry-1.docker.io/bitnamicharts/postgresql",
                ["version"] = version,
                ["size"] = size,
                ["ha"] = ha,
                ["namespace"] = ns
            }
        });
    }
}
