using Deskribe.Core.Plugins;
using Deskribe.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Deskribe.Web.Api;

public static class DeskribeApi
{
    public static WebApplication MapDeskribeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/apps", (DeskribeUiService uiService) =>
        {
            return uiService.GetApplications();
        });

        api.MapGet("/apps/{name}/manifest", async (string name, DeskribeUiService uiService, CancellationToken ct) =>
        {
            return await uiService.GetManifestAsync(name, ct);
        });

        api.MapGet("/apps/{name}/validate/{env}", async (string name, string env, DeskribeUiService uiService, CancellationToken ct) =>
        {
            return await uiService.ValidateAsync(name, env, ct);
        });

        api.MapGet("/apps/{name}/plan/{env}", async (string name, string env, DeskribeUiService uiService, CancellationToken ct) =>
        {
            return await uiService.PlanAsync(name, env, ct: ct);
        });

        api.MapGet("/plugins", (PluginRegistry pluginRegistry) =>
        {
            return pluginRegistry.GetAllResourceProviders()
                .Values
                .Select(p => p.GetSchema())
                .ToList();
        });

        return app;
    }
}
