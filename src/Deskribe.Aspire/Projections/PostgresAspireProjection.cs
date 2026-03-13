using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire.Projections;

public class PostgresAspireProjection : IAspireProjection
{
    public string ResourceType => "postgres";

    public AspireProjectionResult Project(IDistributedApplicationBuilder builder, string appName, ResourceDescriptor resource)
    {
        var map = new DeskribeResourceMap(appName);

        var serverName = $"{appName}-postgres";
        var dbName = $"{appName}-db";

        var server = builder.AddPostgres(serverName);

        var version = resource.Properties.TryGetValue("version", out var v) ? v.GetString() : null;
        if (version is not null)
            server = server.WithImageTag(version);

        server = server.WithPgAdmin();
        server = server.WithLifetime(ContainerLifetime.Persistent);

        var db = server.AddDatabase(dbName);

        map.AddConnectionStringResource(db);
        map.AddWaitForResource(db);
        map.RegisterResource("postgres", serverName, dbName);

        return new AspireProjectionResult { Map = map };
    }
}
