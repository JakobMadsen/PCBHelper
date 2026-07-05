using System.Diagnostics;
using System.Text.Json;

namespace PCBHelper.Contract.Tests;

public sealed class CliContractTests
{
    [Fact]
    public async Task Doctor_Json_Returns_Stable_Envelope()
    {
        var result = await RunCliAsync("doctor", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Summary_Json_Returns_Project_Shape()
    {
        using var fixture = TestFixture.CopyMinimalBoard();
        var result = await RunCliAsync("summary", fixture.Path, "--json");

        Assert.Equal(0, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");

        Assert.Equal("minimal-board", data.GetProperty("projectName").GetString());
        Assert.True(data.TryGetProperty("projectRoot", out _));
        Assert.True(data.TryGetProperty("projectFile", out _));
        Assert.True(data.TryGetProperty("schematicFile", out _));
        Assert.True(data.TryGetProperty("boardFile", out _));
        Assert.True(data.TryGetProperty("missingFiles", out _));
    }

    [Fact]
    public async Task Check_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyMinimalBoard();
        var result = await RunCliAsync("check", fixture.Path, "--json");

        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task BoardSummary_Json_Returns_Tutorial_Footprints()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("board-summary", fixture.Path, "--json");

        Assert.Equal(0, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var footprints = document.RootElement.GetProperty("data").GetProperty("footprints");
        var references = footprints.EnumerateArray()
            .Select(static item => item.GetProperty("reference").GetString())
            .ToHashSet();

        Assert.Contains("BT1", references);
        Assert.Contains("R1", references);
        Assert.Contains("D1", references);
    }

    [Theory]
    [InlineData("export")]
    [InlineData("package")]
    public async Task Manufacturing_Commands_Return_Stable_Json_Envelope(string command)
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync(command, fixture.Path, "--json");

        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Measure_Json_Returns_Distance_Data()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("measure", fixture.Path, "--from", "R1", "--to", "D1", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");

        Assert.Equal("R1", data.GetProperty("fromReference").GetString());
        Assert.Equal("D1", data.GetProperty("toReference").GetString());
        Assert.Equal(23, data.GetProperty("distanceMillimeters").GetDouble(), precision: 3);
    }

    [Fact]
    public async Task Move_DryRun_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("move", fixture.Path, "--ref", "D1", "--x", "75", "--y", "35", "--dry-run", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.Equal(75, root.GetProperty("data").GetProperty("after").GetProperty("xMillimeters").GetDouble(), precision: 3);
    }

    [Fact]
    public async Task Move_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("move", fixture.Path, "--ref", "D1", "--x", "75", "--y", "35", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.True(File.Exists(root.GetProperty("data").GetProperty("changeReportPath").GetString()));
        Assert.True(root.GetProperty("data").TryGetProperty("checkSummary", out _));
    }

    [Fact]
    public async Task SetSpacing_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("set-spacing", fixture.Path, "--fixed", "R1", "--moving", "D1", "--distance", "25", "--axis", "x", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");

        Assert.Equal("R1", data.GetProperty("fixedReference").GetString());
        Assert.Equal("D1", data.GetProperty("movingReference").GetString());
        Assert.Equal("x", data.GetProperty("axis").GetString());
        Assert.True(File.Exists(data.GetProperty("changeReportPath").GetString()));
    }

    [Fact]
    public async Task RestoreChange_DryRun_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var move = await RunCliAsync("move", fixture.Path, "--ref", "D1", "--x", "75", "--y", "35", "--json");
        Assert.Equal(0, move.ExitCode);
        using var moveDocument = JsonDocument.Parse(move.StandardOutput);
        var changeReportPath = moveDocument.RootElement.GetProperty("data").GetProperty("changeReportPath").GetString();

        var restore = await RunCliAsync("restore-change", fixture.Path, "--change", changeReportPath!, "--dry-run", "--json");

        Assert.Equal(0, restore.ExitCode);
        using var restoreDocument = JsonDocument.Parse(restore.StandardOutput);
        var data = restoreDocument.RootElement.GetProperty("data");

        Assert.True(data.GetProperty("dryRun").GetBoolean());
        Assert.Equal("D1", data.GetProperty("reference").GetString());
        Assert.Equal(68, data.GetProperty("after").GetProperty("xMillimeters").GetDouble(), precision: 3);
    }

    [Fact]
    public async Task Open_DryRun_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("open", fixture.Path, "--dry-run", "--json");

        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    private static async Task<ProcessResult> RunCliAsync(params string[] args)
    {
        var project = Path.Combine(RepoRoot.Path, "src", "PCBHelper.Cli", "PCBHelper.Cli.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot.Path
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("--");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
