using Pulumi.Automation;

namespace Deskribe.Plugins.Provisioner.Pulumi;

/// <summary>Seam over the Pulumi Automation API so the mapping logic can be tested without the Pulumi CLI.</summary>
internal interface IPulumiStackRunner
{
    /// <summary>Runs <c>pulumi up</c> and returns the stack outputs (null values for outputs without a value).</summary>
    Task<IReadOnlyDictionary<string, object?>> UpAsync(
        string projectDir, string stackName, IReadOnlyDictionary<string, string> config, CancellationToken ct);

    Task DestroyAsync(string projectDir, string stackName, CancellationToken ct);
}

internal sealed class AutomationApiStackRunner : IPulumiStackRunner
{
    public async Task<IReadOnlyDictionary<string, object?>> UpAsync(
        string projectDir, string stackName, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        using var stack = await LocalWorkspace.CreateOrSelectStackAsync(new LocalProgramArgs(stackName, projectDir), ct);
        foreach (var (key, value) in config)
            await stack.SetConfigAsync(key, new ConfigValue(value), ct);

        var result = await stack.UpAsync(new UpOptions(), ct);
        return result.Outputs.ToDictionary(o => o.Key, o => (object?)o.Value.Value);
    }

    public async Task DestroyAsync(string projectDir, string stackName, CancellationToken ct)
    {
        using var stack = await LocalWorkspace.CreateOrSelectStackAsync(new LocalProgramArgs(stackName, projectDir), ct);
        await stack.DestroyAsync(new DestroyOptions(), ct);
        await stack.Workspace.RemoveStackAsync(stackName, ct);
    }
}
