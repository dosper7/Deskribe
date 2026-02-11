namespace Deskribe.Sdk.Resources;

public sealed record PostgresResource : DeskribeResource
{
    public string? Version { get; init; }
    public bool? Ha { get; init; }
    public string? Sku { get; init; }
}
