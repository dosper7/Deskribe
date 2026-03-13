using Aspire.Hosting;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire;

public interface IAspireProjection
{
    string ResourceType { get; }
    AspireProjectionResult Project(IDistributedApplicationBuilder builder, string appName, ResourceDescriptor resource);
}

public sealed class AspireProjectionResult
{
    public DeskribeResourceMap? Map { get; init; }
}
