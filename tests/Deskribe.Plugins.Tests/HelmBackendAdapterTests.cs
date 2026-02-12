using Deskribe.Plugins.Backend.Helm;

namespace Deskribe.Plugins.Tests;

public class HelmBackendAdapterTests
{
    [Fact]
    public void BuildHelmArgs_Postgres_GeneratesCorrectCommand()
    {
        var setValues = new List<string> { "image.tag=16", "auth.database=myapp" };
        var args = HelmBackendAdapter.BuildHelmArgs(
            "myapp-postgres", "oci://registry-1.docker.io/bitnamicharts/postgresql", "myapp-dev", setValues);

        Assert.Contains("upgrade --install myapp-postgres", args);
        Assert.Contains("oci://registry-1.docker.io/bitnamicharts/postgresql", args);
        Assert.Contains("--namespace myapp-dev", args);
        Assert.Contains("--create-namespace", args);
        Assert.Contains("--wait", args);
        Assert.Contains("--set image.tag=16", args);
        Assert.Contains("--set auth.database=myapp", args);
    }

    [Fact]
    public void BuildHelmArgs_NoSetValues_OmitsSetFlags()
    {
        var args = HelmBackendAdapter.BuildHelmArgs(
            "myapp-kafka", "oci://registry-1.docker.io/bitnamicharts/kafka", "myapp-dev", []);

        Assert.DoesNotContain("--set", args);
        Assert.Contains("upgrade --install myapp-kafka", args);
    }

    [Fact]
    public void BuildHelmArgs_Redis_GeneratesCorrectCommand()
    {
        var setValues = new List<string> { "image.tag=7" };
        var args = HelmBackendAdapter.BuildHelmArgs(
            "myapp-redis", "oci://registry-1.docker.io/bitnamicharts/redis", "myapp-dev", setValues);

        Assert.Contains("upgrade --install myapp-redis", args);
        Assert.Contains("--set image.tag=7", args);
    }

    [Fact]
    public void Name_ReturnsHelm()
    {
        var adapter = new HelmBackendAdapter();
        Assert.Equal("helm", adapter.Name);
    }
}
