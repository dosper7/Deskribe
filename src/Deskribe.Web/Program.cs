using Deskribe.Web.Components;
using Deskribe.Web.Services;
using Deskribe.Core.Config;
using Deskribe.Core.Engine;
using Deskribe.Core.Merging;
using Deskribe.Core.Plugins;
using Deskribe.Core.Resolution;
using Deskribe.Core.Validation;
using Deskribe.Plugins.Backend.Pulumi;
using Deskribe.Plugins.Resources.Kafka;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Plugins.Resources.Redis;
using Deskribe.Plugins.Runtime.Kubernetes;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<ConfigLoader>();
builder.Services.AddSingleton<MergeEngine>();
builder.Services.AddSingleton<ResourceReferenceResolver>();
builder.Services.AddSingleton<PolicyValidator>();
builder.Services.AddSingleton<PluginHost>(sp =>
{
    var host = new PluginHost(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PluginHost>>());
    host.RegisterPlugin(new PostgresPlugin());
    host.RegisterPlugin(new RedisPlugin());
    host.RegisterPlugin(new KafkaPlugin());
    host.RegisterPlugin(new PulumiPlugin());
    host.RegisterPlugin(new KubernetesPlugin());
    return host;
});
builder.Services.AddSingleton<DeskribeEngine>();
builder.Services.AddSingleton<DeskribeUiService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapDefaultEndpoints();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
