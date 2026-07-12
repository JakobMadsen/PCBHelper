using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class ProjectScopePolicyTests
{
    [Fact]
    public void Missing_Mcp_Scope_Is_Not_Configured()
    {
        var policy = ProjectScopePolicy.FromEnvironment(_ => null);

        var result = policy.Authorize(Environment.CurrentDirectory);

        Assert.False(result.Success);
        Assert.Equal("PROJECT_SCOPE_NOT_CONFIGURED", result.Error?.Code);
    }

    [Fact]
    public void Configured_Scope_Allows_Children_And_Rejects_Siblings()
    {
        using var allowed = new TempDirectory();
        using var outside = new TempDirectory();
        var child = Path.Combine(allowed.Path, "project");
        Directory.CreateDirectory(child);
        var policy = ProjectScopePolicy.FromEnvironment(name => name == "PCBHELPER_ALLOWED_ROOTS" ? allowed.Path : null);

        Assert.True(policy.Authorize(child).Success);
        var rejected = policy.Authorize(outside.Path);
        Assert.False(rejected.Success);
        Assert.Equal("PROJECT_SCOPE_VIOLATION", rejected.Error?.Code);
    }

    [Fact]
    public void Scoped_Project_Discovery_Uses_The_Same_Policy()
    {
        using var allowed = new TempDirectory();
        using var outside = new TempDirectory();
        var discovery = new ProjectDiscoveryService(
            ProjectScopePolicy.FromEnvironment(name => name == "PCBHELPER_ALLOWED_ROOTS" ? allowed.Path : null));

        Assert.True(discovery.GetSummary(allowed.Path).Success);
        Assert.Equal("PROJECT_SCOPE_VIOLATION", discovery.GetSummary(outside.Path).Error?.Code);
    }
}
