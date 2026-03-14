using System.Reflection;
using Deskribe.Web.Api;
using Deskribe.Web.Components;
using Deskribe.Web.Services;
using Deskribe.Core;
using Deskribe.Core.Plugins;
using Deskribe.Plugins.Provisioner.PlatformOutput;
using Deskribe.Plugins.Provisioner.Pulumi;
using Deskribe.Plugins.Provisioner.Terraform;
using Deskribe.Plugins.Resources.Kafka;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Plugins.Resources.Redis;
using Deskribe.Plugins.Runtime.Kubernetes;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var builtInAssemblies = new Assembly[]
{
    typeof(PostgresPlugin).Assembly,
    typeof(RedisPlugin).Assembly,
    typeof(KafkaPlugin).Assembly,
    typeof(PulumiPlugin).Assembly,
    typeof(TerraformPlugin).Assembly,
    typeof(KubernetesPlugin).Assembly,
    typeof(PlatformOutputPlugin).Assembly,
};

var pluginsDir = builder.Configuration.GetValue<string>("Deskribe:PluginsDir")
    ?? PluginLoader.DefaultPluginsDirectory;

builder.Services.AddDeskribe(builtInAssemblies, pluginsDir);

builder.Services.AddSingleton<AppStateStore>();
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
