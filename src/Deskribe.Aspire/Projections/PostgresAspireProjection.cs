using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Deskribe.Sdk.Models;

namespace Deskribe.Aspire.Projections;

/// <summary>Outputs: connectionString, host, port, database.</summary>
public class PostgresAspireProjection : IAspireProjection
{
    public string ResourceType => "postgres";

    public AspireProjectionResult Project(
        IDistributedApplicationBuilder builder, string appName, ResourceDescriptor resource, DeskribeAspireOptions options)
    {
        var serverName = ProjectionHelpers.ResourceName(appName, resource.Key);
        var dbResourceName = ProjectionHelpers.ResourceName(appName, resource.Key, "db");
        var databaseName = resource.Key.Replace('-', '_').Replace('.', '_');

        var server = builder.AddPostgres(serverName);

        var version = resource.Properties.TryGetValue("version", out var v) ? v.GetString() : null;
        if (version is not null)
            server = server.WithImageTag(version);

        if (options.DeveloperTools)
            server = server.WithPgAdmin();
        server = ProjectionHelpers.ApplyLifetime(server, options);

        var db = server.AddDatabase(dbResourceName, databaseName);

        return new AspireProjectionResult
        {
            AspireResourceName = serverName,
            ChildResourceName = dbResourceName,
            WaitFor = db.AsBuilder<PostgresDatabaseResource, IResource>(),
            Outputs = new Dictionary<string, ReferenceExpression>
            {
                ["connectionString"] = db.Resource.ConnectionStringExpression,
                ["host"] = ReferenceExpression.Create($"{server.Resource.Host}"),
                ["port"] = ReferenceExpression.Create($"{server.Resource.Port}"),
                ["database"] = ProjectionHelpers.Literal(databaseName)
            }
        };
    }
}
