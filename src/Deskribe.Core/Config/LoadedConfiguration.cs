using Deskribe.Sdk.Models;

namespace Deskribe.Core.Config;

/// <summary>Everything loaded and merged for one (manifest, platform, environment).</summary>
public sealed record LoadedConfiguration
{
    public required DeskribeManifest Manifest { get; init; }
    public required PlatformConfig Platform { get; init; }
    public required EnvironmentConfig EnvironmentConfig { get; init; }
    public required string Environment { get; init; }
    public required IReadOnlyList<string> KnownEnvironments { get; init; }

    /// <summary>Platform defaults with the environment's overrides applied.</summary>
    public required PlatformDefaults Defaults { get; init; }
    public required string Namespace { get; init; }
}
