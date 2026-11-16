using Deskribe.Core.Engine;
using Deskribe.Core.Resolution;
using Deskribe.Core.Validation;

namespace Deskribe.Core.Tests;

public class QuantityTests
{
    [Theory]
    [InlineData("500m", 0.5)]
    [InlineData("2", 2)]
    [InlineData("0.5", 0.5)]
    [InlineData("1500m", 1.5)]
    public void ParsesCpu(string value, double expected)
    {
        Assert.True(Quantity.TryParseCpu(value, out var cores));
        Assert.Equal((decimal)expected, cores);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("500Mi")]
    [InlineData("-1")]
    [InlineData("1 m")]
    public void RejectsInvalidCpu(string value) => Assert.False(Quantity.TryParseCpu(value, out _));

    [Theory]
    [InlineData("1024", 1024)]
    [InlineData("1Ki", 1024)]
    [InlineData("512Mi", 536870912)]
    [InlineData("1Gi", 1073741824)]
    [InlineData("1Ti", 1099511627776)]
    [InlineData("1k", 1000)]
    [InlineData("1M", 1000000)]
    [InlineData("2G", 2000000000)]
    [InlineData("1T", 1000000000000)]
    public void ParsesMemory(string value, double expected)
    {
        Assert.True(Quantity.TryParseMemory(value, out var bytes));
        Assert.Equal((decimal)expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1GB")]
    [InlineData("lots")]
    [InlineData("500m")]
    [InlineData("1gi")]
    public void RejectsInvalidMemory(string value) => Assert.False(Quantity.TryParseMemory(value, out _));
}

public class DnsLabelTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("payments-api")]
    [InlineData("app1-dev")]
    public void AcceptsValidLabels(string value) => Assert.True(DnsLabel.IsValid(value));

    [Theory]
    [InlineData("")]
    [InlineData("Payments")]
    [InlineData("-api")]
    [InlineData("api-")]
    [InlineData("pay_api")]
    [InlineData("pay.api")]
    [InlineData("api\n")]
    public void RejectsInvalidLabels(string value) => Assert.False(DnsLabel.IsValid(value));

    [Theory]
    [InlineData("1api", false)]
    [InlineData("api1", true)]
    [InlineData("api\n", false)]
    [InlineData("-api", false)]
    public void ObjectNamesMustStartWithALetter(string value, bool valid) =>
        Assert.Equal(valid, DnsLabel.IsValidObjectName(value));

    [Theory]
    [InlineData("500m\n")]
    [InlineData("1Gi\n")]
    public void QuantitiesRejectTrailingNewline(string value)
    {
        Assert.False(Quantity.TryParseCpu(value, out _));
        Assert.False(Quantity.TryParseMemory(value, out _));
    }

    [Fact]
    public void RejectsLabelsLongerThan63() 
    {
        Assert.True(DnsLabel.IsValid(new string('a', 63)));
        Assert.False(DnsLabel.IsValid(new string('a', 64)));
    }
}

public class ResourceReferencesTests
{
    [Fact]
    public void ExtractsKeysWithDotsDashesAndUnderscores()
    {
        var refs = ResourceReferences.Extract("X", "@resource(kafka.messaging).endpoint;@resource(orders-db_2).host");

        Assert.Collection(refs,
            r => { Assert.Equal("kafka.messaging", r.ResourceKey); Assert.Equal("endpoint", r.Output); },
            r => { Assert.Equal("orders-db_2", r.ResourceKey); Assert.Equal("host", r.Output); });
    }

    [Theory]
    [InlineData("@resource(postgres)")]
    [InlineData("@resource(postgres).")]
    [InlineData("@resource().host")]
    [InlineData("@resource(pg db).host")]
    [InlineData("ok=@resource(pg).host bad=@resource(pg")]
    public void FindsMalformedReferences(string value) => Assert.NotEmpty(ResourceReferences.FindMalformed(value));

    [Fact]
    public void WellFormedReferencesAreNotMalformed() =>
        Assert.Empty(ResourceReferences.FindMalformed("Host=@resource(pg).host;Port=@resource(pg).port"));

    [Fact]
    public void ResolveSubstitutesKnownAndReportsUnknown()
    {
        var outputs = new Dictionary<string, Dictionary<string, string>> { ["pg"] = new() { ["host"] = "db.local" } };
        var unresolved = new List<ResourceReference>();

        var value = ResourceReferences.Resolve("CS", "Host=@resource(pg).host;Pw=@resource(pg).pw", outputs, unresolved);

        Assert.Equal("Host=db.local;Pw=@resource(pg).pw", value);
        Assert.Single(unresolved);
        Assert.Equal("@resource(pg).pw", unresolved[0].Expression);
    }

    [Theory]
    [InlineData(null, GenerateFormat.All)]
    [InlineData("all", GenerateFormat.All)]
    [InlineData("runtime-only", GenerateFormat.RuntimeOnly)]
    [InlineData("k8s-only", GenerateFormat.RuntimeOnly)]
    [InlineData("infra-only", GenerateFormat.InfraOnly)]
    [InlineData("terraform-only", GenerateFormat.InfraOnly)]
    public void GenerateFormatAliases(string? value, GenerateFormat expected) =>
        Assert.Equal(expected, GenerateFormats.Parse(value));

    [Fact]
    public void UnknownGenerateFormatIsAnError() =>
        Assert.Throws<DeskribeException>(() => GenerateFormats.Parse("helm-only"));
}
