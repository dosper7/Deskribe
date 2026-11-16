using System.CommandLine;
using System.Reflection;
using Deskribe.Cli.Commands;
using Deskribe.Core;
using Deskribe.Core.Engine;
using Deskribe.Core.Plugins;
using Deskribe.Plugins.Provisioner.PlatformOutput;
using Deskribe.Plugins.Provisioner.Pulumi;
using Deskribe.Plugins.Provisioner.Static;
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

var verbose = args.Contains("--verbose");
Cli.Verbose = verbose;

var builtInAssemblies = new Assembly[]
{
    typeof(PostgresPlugin).Assembly,
    typeof(RedisPlugin).Assembly,
    typeof(KafkaPlugin).Assembly,
    typeof(PulumiPlugin).Assembly,
    typeof(TerraformPlugin).Assembly,
    typeof(KubernetesPlugin).Assembly,
    typeof(PlatformOutputPlugin).Assembly,
    typeof(StaticPlugin).Assembly,
};

var services = new ServiceCollection();

services.AddLogging(builder =>
{
    // Logs go to stderr so command output stays clean on stdout.
    builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.SetMinimumLevel(verbose ? LogLevel.Information : LogLevel.Warning);
});

services.AddDeskribe(builtInAssemblies, pluginsDir);

var serviceProvider = services.BuildServiceProvider();

var engine = serviceProvider.GetRequiredService<DeskribeEngine>();

var pluginsDirOption = new Option<string>(
    "--plugins-dir",
    () => PluginLoader.DefaultPluginsDirectory,
    "Directory to load external plugin DLLs from");
var verboseOption = new Option<bool>("--verbose", "Show informational engine logs");

var rootCommand = new RootCommand("Deskribe - intent-as-code: provision resources and deploy workloads from deskribe.json");
rootCommand.AddGlobalOption(pluginsDirOption);
rootCommand.AddGlobalOption(verboseOption);
foreach (var command in Commands.CreateAll(engine))
    rootCommand.AddCommand(command);

return await rootCommand.InvokeAsync(args);
