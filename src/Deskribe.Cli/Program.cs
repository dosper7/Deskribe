using System.CommandLine;
using System.Reflection;
using Deskribe.Cli.Commands;
using Deskribe.Core;
using Deskribe.Core.Engine;
using Deskribe.Core.Plugins;
using Deskribe.Plugins.Provisioner.PlatformOutput;
using Deskribe.Plugins.Provisioner.Pulumi;
using Deskribe.Plugins.Provisioner.Terraform;
using Deskribe.Plugins.Resources.Kafka;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Plugins.Resources.Redis;
using Deskribe.Plugins.Runtime.Kubernetes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Parse --plugins-dir before DI setup (assemblies needed at registration time)
var pluginsDir = PluginLoader.DefaultPluginsDirectory;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--plugins-dir")
    {
        pluginsDir = args[i + 1];
        break;
    }
}

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

var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

services.AddDeskribe(builtInAssemblies, pluginsDir);

var serviceProvider = services.BuildServiceProvider();

var engine = serviceProvider.GetRequiredService<DeskribeEngine>();

var pluginsDirOption = new Option<string>(
    "--plugins-dir",
    () => PluginLoader.DefaultPluginsDirectory,
    "Directory to load external plugin DLLs from");

var rootCommand = new RootCommand("Deskribe — Intent-as-Code platform for infrastructure and deployment");
rootCommand.AddGlobalOption(pluginsDirOption);

rootCommand.AddCommand(ValidateCommand.Create(engine));
rootCommand.AddCommand(PlanCommand.Create(engine));
rootCommand.AddCommand(ApplyCommand.Create(engine));
rootCommand.AddCommand(DestroyCommand.Create(engine));
rootCommand.AddCommand(GenerateCommand.Create(engine));

return await rootCommand.InvokeAsync(args);
