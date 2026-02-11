namespace Deskribe.Sdk.Resources;

public abstract record DeskribeResource
{
    public required string Type { get; init; }
    public string? Size { get; init; }
}
