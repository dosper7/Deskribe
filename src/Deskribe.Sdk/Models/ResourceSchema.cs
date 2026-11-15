namespace Deskribe.Sdk.Models;

public sealed record ResourceSchema
{
    public required string ResourceType { get; init; }
    public string? Description { get; init; }
    public List<PropertySchema> Properties { get; init; } = [];

    /// <summary>Every output an app may reference with <c>@resource(key).output</c>.</summary>
    public List<string> ProvidedOutputs { get; init; } = [];

    /// <summary>
    /// The subset of <see cref="ProvidedOutputs"/> that carry credentials. Environment
    /// variables referencing these are delivered through the secrets strategy.
    /// </summary>
    public List<string> SensitiveOutputs { get; init; } = [];
}

public sealed record PropertySchema
{
    public required string Name { get; init; }
    public required string ValueType { get; init; }
    public bool Required { get; init; }
    public object? Default { get; init; }
    public string? Description { get; init; }
}
