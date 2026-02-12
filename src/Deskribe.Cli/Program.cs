using System.CommandLine;
using Deskribe.Cli.Commands;
using Deskribe.Core.Config;
using Deskribe.Core.Engine;
using Deskribe.Core.Merging;
using Deskribe.Core.Plugins;
using Deskribe.Core.Resolution;
using Deskribe.Core.Validation;
using Deskribe.Plugins.Backend.Helm;
using Deskribe.Plugins.Backend.Pulumi;
using Deskribe.Plugins.Resources.Kafka;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Plugins.Resources.Redis;
using Deskribe.Plugins.Runtime.Kubernetes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

services.AddSingleton<ConfigLoader>();
services.AddSingleton<MergeEngine>();
services.AddSingleton<ResourceReferenceResolver>();
services.AddSingleton<PolicyValidator>();
services.AddSingleton<PluginHost>();
services.AddSingleton<DeskribeEngine>();

var serviceProvider = services.BuildServiceProvider();

// Register plugins
var pluginHost = serviceProvider.GetRequiredService<PluginHost>();
pluginHost.RegisterPlugin(new PostgresPlugin());
pluginHost.RegisterPlugin(new RedisPlugin());
pluginHost.RegisterPlugin(new KafkaPlugin());
pluginHost.RegisterPlugin(new HelmPlugin());
pluginHost.RegisterPlugin(new PulumiPlugin());
pluginHost.RegisterPlugin(new KubernetesPlugin());

var engine = serviceProvider.GetRequiredService<DeskribeEngine>();

var rootCommand = new RootCommand("Deskribe — Intent-as-Code platform for infrastructure and deployment");

rootCommand.AddCommand(ValidateCommand.Create(engine));
rootCommand.AddCommand(PlanCommand.Create(engine));
rootCommand.AddCommand(ApplyCommand.Create(engine));
rootCommand.AddCommand(DestroyCommand.Create(engine));

return await rootCommand.InvokeAsync(args);
