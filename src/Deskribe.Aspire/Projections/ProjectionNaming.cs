using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Deskribe.Aspire.Projections;

internal static partial class ProjectionHelpers
{
    [GeneratedRegex("[^a-zA-Z0-9]+")]
    private static partial Regex NonAlphanumeric();

    /// <summary>Aspire resource name unique per app and resource key.</summary>
    public static string ResourceName(string appName, string key, string? suffix = null)
    {
        var name = $"{appName}-{NonAlphanumeric().Replace(key, "-").Trim('-')}";
        return suffix is null ? name : $"{name}-{suffix}";
    }

    public static ReferenceExpression Literal(string value) =>
        ReferenceExpression.Create($"{value}");

    public static IResourceBuilder<T> ApplyLifetime<T>(IResourceBuilder<T> builder, DeskribeAspireOptions options)
        where T : ContainerResource =>
        options.PersistentContainers ? builder.WithLifetime(ContainerLifetime.Persistent) : builder;
}
