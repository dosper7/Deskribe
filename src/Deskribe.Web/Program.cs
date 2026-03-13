using Deskribe.Web.Api;
using Deskribe.Web.Components;
using Deskribe.Web.Services;
using Deskribe.Core;
using Deskribe.Plugins.Backend.Pulumi;
using Deskribe.Plugins.Backend.Terraform;
using Deskribe.Plugins.Resources.Kafka;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Plugins.Resources.Redis;
using Deskribe.Plugins.Runtime.Kubernetes;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDeskribe(
    typeof(PostgresPlugin).Assembly,
    typeof(RedisPlugin).Assembly,
    typeof(KafkaPlugin).Assembly,
    typeof(PulumiPlugin).Assembly,
    typeof(TerraformPlugin).Assembly,
    typeof(KubernetesPlugin).Assembly);

builder.Services.AddSingleton<DeskribeUiService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapDefaultEndpoints();

app.MapDeskribeApi();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
