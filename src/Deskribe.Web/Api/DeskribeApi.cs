using Deskribe.Core;
using Deskribe.Core.Plugins;
using Deskribe.Sdk.Models;
using Deskribe.Web.Services;

namespace Deskribe.Web.Api;

/// <summary>Read-only API. Secret variable values are never included in responses.</summary>
public static class DeskribeApi
{
    public const string SecretPlaceholder = "(secret)";

    public static WebApplication MapDeskribeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.AddEndpointFilter(MapErrors);

        api.MapGet("/apps", (DeskribeUiService ui) => ui.GetApplications());

        api.MapGet("/apps/{name}/manifest", async (string name, DeskribeUiService ui, CancellationToken ct) =>
            Results.Ok(await ui.GetManifestAsync(name, ct)));

        api.MapGet("/apps/{name}/validate/{env}", async (string name, string env, DeskribeUiService ui, CancellationToken ct) =>
        {
            var result = await ui.ValidateAsync(name, env, ct);
            return result.IsValid ? Results.Ok(result) : Results.BadRequest(new { errors = result.Errors, warnings = result.Warnings });
        });

        // ?image=name=image:tag (repeatable), or a single ?image=image:tag
        api.MapGet("/apps/{name}/plan/{env}", async (string name, string env, string[]? image, DeskribeUiService ui, CancellationToken ct) =>
        {
            var images = DeskribeUiService.ParseImages(image is null ? null : string.Join('\n', image));
            return Results.Ok(PlanResponse.From(await ui.PlanAsync(name, env, images, ct)));
        });

        api.MapGet("/apps/{name}/status/{env}", async (string name, string env, DeskribeUiService ui, CancellationToken ct) =>
            Results.Ok(await ui.GetStatusAsync(name, env, ct)));

        api.MapGet("/plugins", (PluginRegistry registry) =>
            registry.GetAllResourceProviders().Values.Select(p => p.GetSchema()).ToList());

        return app;
    }

    private static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (AppNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (DeskribeValidationException ex)
        {
            return Results.BadRequest(new { errors = ex.Result.Errors, warnings = ex.Result.Warnings });
        }
        catch (DeskribeException ex)
        {
            return Results.BadRequest(new { errors = new[] { ex.Message }, warnings = Array.Empty<string>() });
        }
    }
}

/// <summary>The plan as exposed over HTTP: no platform config, no secret values.</summary>
public record PlanResponse(
    string App, string? Team, string Environment, string Namespace,
    IReadOnlyList<ResourcePlanResult> Resources, IReadOnlyList<WorkloadResponse> Workloads,
    IReadOnlyList<Binding> Bindings, IReadOnlyList<string> Warnings)
{
    public static PlanResponse From(DeskribePlan plan) => new(
        plan.AppName, plan.Team, plan.Environment, plan.Namespace, plan.ResourcePlans,
        plan.Workloads.Select(w => new WorkloadResponse(
            w.Name, w.Image, w.Replicas, w.Cpu, w.Memory, w.SecretsStrategy,
            w.EnvironmentVariables.ToDictionary(
                kv => kv.Key,
                kv => w.SecretKeys.Contains(kv.Key) ? DeskribeApi.SecretPlaceholder : kv.Value))).ToList(),
        plan.Bindings, plan.Warnings);
}

public record WorkloadResponse(
    string Name, string? Image, int Replicas, string Cpu, string Memory, string SecretsStrategy,
    IReadOnlyDictionary<string, string> EnvironmentVariables);
