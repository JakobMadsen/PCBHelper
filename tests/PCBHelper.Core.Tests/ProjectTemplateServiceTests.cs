using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class ProjectTemplateServiceTests
{
    [Fact]
    public void CreateProjectFromTemplate_Creates_Complete_Circular_KiCad_Project()
    {
        using var parent = new TempDirectory();
        var projects = new ProjectDiscoveryService();
        var service = new ProjectTemplateService(ProjectScopePolicy.Unrestricted(), projects);

        var result = service.CreateProjectFromTemplate(
            ProjectTemplateService.CircularTemplateId, "ReceiverLab001", parent.Path, boardDiameterMm: 100);

        Assert.True(result.Success, result.Error?.Message ?? result.Summary);
        Assert.NotNull(result.Data);
        Assert.Equal("circle", result.Data.BoardShape);
        Assert.Equal(100, result.Data.BoardWidthMm);
        Assert.Equal(3, result.Data.CreatedFiles.Count);
        var summary = projects.GetSummary(result.Data.ProjectPath);
        Assert.True(summary.Success);
        Assert.Empty(summary.Data!.MissingFiles);
        var board = File.ReadAllText(summary.Data.BoardFile!);
        Assert.Contains("(gr_circle", board, StringComparison.Ordinal);
        Assert.Contains("(center 100 100)", board, StringComparison.Ordinal);
        Assert.Contains("(end 150 100)", board, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateProjectFromTemplate_Refuses_To_Overwrite_Existing_Path()
    {
        using var parent = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(parent.Path, "Existing"));
        var service = new ProjectTemplateService(ProjectScopePolicy.Unrestricted(), new ProjectDiscoveryService());

        var result = service.CreateProjectFromTemplate(
            ProjectTemplateService.RectangularTemplateId, "Existing", parent.Path);

        Assert.False(result.Success);
        Assert.Equal("PROJECT_DESTINATION_EXISTS", result.Error?.Code);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("bad/name")]
    [InlineData(" bad")]
    public void CreateProjectFromTemplate_Rejects_Unsafe_Project_Names(string projectName)
    {
        using var parent = new TempDirectory();
        var service = new ProjectTemplateService(ProjectScopePolicy.Unrestricted(), new ProjectDiscoveryService());

        var result = service.CreateProjectFromTemplate(
            ProjectTemplateService.CircularTemplateId, projectName, parent.Path);

        Assert.False(result.Success);
        Assert.Equal("PROJECT_NAME_INVALID", result.Error?.Code);
    }

    [Fact]
    public void CreateProjectFromTemplate_Enforces_Authorized_Roots()
    {
        using var allowed = new TempDirectory();
        using var outside = new TempDirectory();
        var scope = ProjectScopePolicy.FromEnvironment(name => name == "PCBHELPER_ALLOWED_ROOTS" ? allowed.Path : null);
        var service = new ProjectTemplateService(scope, new ProjectDiscoveryService(scope));

        var result = service.CreateProjectFromTemplate(
            ProjectTemplateService.CircularTemplateId, "Outside", outside.Path);

        Assert.False(result.Success);
        Assert.Equal("PROJECT_SCOPE_VIOLATION", result.Error?.Code);
    }
}
