namespace Deskribe.Sdk.Models;

public sealed record ResourceSchema
{
    public required string ResourceType { get; init; }
    public string? Description { get; init; }
    public List<PropertySchema> Properties { get; init; } = [];
    public List<string> ProvidedOutputs { get; init; } = [];
}

public sealed record PropertySchema
{
    public required string Name { get; init; }
    public required string ValueType { get; init; }
    public bool Required { get; init; }
    public object? Default { get; init; }
    public string? Description { get; init; }
}
