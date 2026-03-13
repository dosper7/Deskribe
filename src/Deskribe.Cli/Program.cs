using System.CommandLine;
using Deskribe.Cli.Commands;
using Deskribe.Core;
using Deskribe.Core.Engine;
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

services.AddDeskribe(
    typeof(PostgresPlugin).Assembly,
    typeof(RedisPlugin).Assembly,
    typeof(KafkaPlugin).Assembly,
    typeof(PulumiPlugin).Assembly,
    typeof(KubernetesPlugin).Assembly);

var serviceProvider = services.BuildServiceProvider();

var engine = serviceProvider.GetRequiredService<DeskribeEngine>();

var rootCommand = new RootCommand("Deskribe — Intent-as-Code platform for infrastructure and deployment");

rootCommand.AddCommand(ValidateCommand.Create(engine));
rootCommand.AddCommand(PlanCommand.Create(engine));
rootCommand.AddCommand(ApplyCommand.Create(engine));
rootCommand.AddCommand(DestroyCommand.Create(engine));

return await rootCommand.InvokeAsync(args);
