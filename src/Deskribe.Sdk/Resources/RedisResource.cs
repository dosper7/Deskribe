namespace Deskribe.Sdk.Resources;

public sealed record RedisResource : DeskribeResource
{
    public string? Version { get; init; }
    public bool? Ha { get; init; }
    public int? MaxMemoryMb { get; init; }
}
