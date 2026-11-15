using Deskribe.Core.Engine;
using Deskribe.Core.Plugins;
using Deskribe.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Deskribe.Core.Tests;

[DeskribePlugin("fake")]
public sealed class FakePlugin : IPlugin
{
    public string Name => "fake";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new Support.FakeResourceProvider("fake-db", ["host"]));
        registrar.RegisterRuntimePlugin(new Support.FakeRuntime());
    }
}

public class ServiceRegistrationTests
{
    [Fact]
    public void AddDeskribeResolvesEngineAndDiscoversPlugins()
    {
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddDeskribe(typeof(FakePlugin).Assembly)
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<DeskribeEngine>());
        var registry = sp.GetRequiredService<PluginRegistry>();
        Assert.NotNull(registry.GetResourceProvider("fake-db"));
        Assert.NotNull(registry.GetRuntimePlugin("kubernetes"));
    }
}
