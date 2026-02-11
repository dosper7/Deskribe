using Deskribe.Core.Resolution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deskribe.Core.Tests;

public class ResourceReferenceResolverTests
{
    private readonly ResourceReferenceResolver _resolver = new(NullLogger<ResourceReferenceResolver>.Instance);

    [Fact]
    public void ExtractReferences_FindsSimpleReference()
    {
        var envVars = new Dictionary<string, string>
        {
            ["DB_CONN"] = "@resource(postgres).connectionString"
        };

        var refs = _resolver.ExtractReferences(envVars);

        Assert.Single(refs);
        Assert.Equal("DB_CONN", refs[0].EnvVarName);
        Assert.Equal("postgres", refs[0].ResourceType);
        Assert.Equal("connectionString", refs[0].Property);
    }

    [Fact]
    public void ExtractReferences_FindsDottedResourceType()
    {
        var envVars = new Dictionary<string, string>
        {
            ["KAFKA"] = "@resource(kafka.messaging).endpoint"
        };

        var refs = _resolver.ExtractReferences(envVars);

        Assert.Single(refs);
        Assert.Equal("kafka.messaging", refs[0].ResourceType);
        Assert.Equal("endpoint", refs[0].Property);
    }

    [Fact]
    public void ExtractReferences_FindsMultipleReferences()
    {
        var envVars = new Dictionary<string, string>
        {
            ["DB"] = "@resource(postgres).connectionString",
            ["CACHE"] = "@resource(redis).endpoint",
            ["STATIC"] = "some-static-value"
        };

        var refs = _resolver.ExtractReferences(envVars);

        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void ExtractReferences_ReturnsEmptyForNoReferences()
    {
        var envVars = new Dictionary<string, string>
        {
            ["KEY"] = "plain-value",
            ["OTHER"] = "another-value"
        };

        var refs = _resolver.ExtractReferences(envVars);

        Assert.Empty(refs);
    }

    [Fact]
    public void ResolveReferences_ReplacesWithActualValues()
    {
        var envVars = new Dictionary<string, string>
        {
            ["DB_CONN"] = "@resource(postgres).connectionString",
            ["REDIS"] = "@resource(redis).endpoint",
            ["STATIC"] = "keep-this"
        };

        var outputs = new Dictionary<string, Dictionary<string, string>>
        {
            ["postgres"] = new() { ["connectionString"] = "Host=db.local;Port=5432" },
            ["redis"] = new() { ["endpoint"] = "redis.local:6379" }
        };

        var resolved = _resolver.ResolveReferences(envVars, outputs);

        Assert.Equal("Host=db.local;Port=5432", resolved["DB_CONN"]);
        Assert.Equal("redis.local:6379", resolved["REDIS"]);
        Assert.Equal("keep-this", resolved["STATIC"]);
    }

    [Fact]
    public void ResolveReferences_KeepsUnresolvedReferences()
    {
        var envVars = new Dictionary<string, string>
        {
            ["DB_CONN"] = "@resource(postgres).connectionString"
        };

        var outputs = new Dictionary<string, Dictionary<string, string>>();

        var resolved = _resolver.ResolveReferences(envVars, outputs);

        Assert.Equal("@resource(postgres).connectionString", resolved["DB_CONN"]);
    }

    [Fact]
    public void ValidateReferences_PassesForKnownTypes()
    {
        var refs = new List<ResourceReference>
        {
            new() { EnvVarName = "DB", RawExpression = "@resource(postgres).connectionString", ResourceType = "postgres", Property = "connectionString" }
        };
        var types = new HashSet<string> { "postgres", "redis" };

        var result = _resolver.ValidateReferences(refs, types);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateReferences_FailsForUnknownTypes()
    {
        var refs = new List<ResourceReference>
        {
            new() { EnvVarName = "DB", RawExpression = "@resource(mysql).connectionString", ResourceType = "mysql", Property = "connectionString" }
        };
        var types = new HashSet<string> { "postgres", "redis" };

        var result = _resolver.ValidateReferences(refs, types);

        Assert.False(result.IsValid);
        Assert.Contains("mysql", result.Errors[0]);
    }
}
