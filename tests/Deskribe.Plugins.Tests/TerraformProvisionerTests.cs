using Deskribe.Plugins.Provisioner.Terraform;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Tests;

public class TerraformProvisionerTests
{
    private readonly TerraformProvisioner _provisioner = new();

    private static DeskribePlan CreatePlan(string? region = "northeurope") => new()
    {
        AppName = "myapp",
        Environment = "dev",
        Platform = new PlatformConfig
        {
            Defaults = new PlatformDefaults { Region = region }
        },
        EnvironmentConfig = new EnvironmentConfig { Name = "dev" },
        ResourcePlans =
        [
            new ResourcePlanResult
            {
                ResourceType = "postgres",
                Action = "create",
                PlannedOutputs = new Dictionary<string, string> { ["port"] = "5432" },
                Configuration = new Dictionary<string, object?> { ["version"] = "16" }
            }
        ]
    };

    [Fact]
    public async Task Apply_ReturnsFailure_InsteadOfFakeSuccess()
    {
        var result = await _provisioner.ApplyAsync(CreatePlan(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("terraform", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.ResourceOutputs);
    }

    [Fact]
    public async Task GenerateArtifacts_UsesPlanRegion()
    {
        var result = await _provisioner.GenerateArtifactsAsync(CreatePlan("northeurope"), "out", CancellationToken.None);

        Assert.True(result.Success);
        var tfvars = result.Files.First(f => f.Path.EndsWith("terraform.tfvars.json"));
        Assert.Contains("northeurope", tfvars.Content);
    }

    [Fact]
    public async Task GenerateArtifacts_FallsBackToDefaultRegion()
    {
        var result = await _provisioner.GenerateArtifactsAsync(CreatePlan(region: null), "out", CancellationToken.None);

        Assert.True(result.Success);
        var tfvars = result.Files.First(f => f.Path.EndsWith("terraform.tfvars.json"));
        Assert.Contains("westeurope", tfvars.Content);
    }
}
