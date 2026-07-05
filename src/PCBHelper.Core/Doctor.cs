namespace PCBHelper.Core;

public sealed class KiCadDoctorService
{
    private readonly KiCadCliLocator _locator;
    private readonly ICommandRunner _runner;

    public KiCadDoctorService(KiCadCliLocator locator, ICommandRunner runner)
    {
        _locator = locator;
        _runner = runner;
    }

    public async Task<ToolResponse<DoctorResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        var cli = _locator.Locate();
        if (!cli.Found || cli.ExecutablePath is null)
        {
            return ToolResponse<DoctorResult>.Fail(
                "kicad-cli was not found. Set KICAD_CLI or add kicad-cli to PATH.",
                "KICAD_CLI_NOT_FOUND",
                cli.Message);
        }

        var execution = await _runner.RunAsync(cli.ExecutablePath, new[] { "version" }, null, cancellationToken);
        var versionText = string.IsNullOrWhiteSpace(execution.StandardOutput)
            ? execution.StandardError.Trim()
            : execution.StandardOutput.Trim();
        var parsed = KiCadVersionParser.Parse(versionText);
        var supported = parsed?.Major >= 9;

        var result = new DoctorResult(
            cli.ExecutablePath,
            cli.Source,
            execution.ExitCode,
            versionText,
            parsed?.ToString(),
            supported);

        if (execution.ExitCode != 0)
        {
            return ToolResponse<DoctorResult>.Fail(
                $"kicad-cli was found but `kicad-cli version` exited with {execution.ExitCode}.",
                "KICAD_VERSION_FAILED",
                execution.StandardError,
                result);
        }

        if (!supported)
        {
            return ToolResponse<DoctorResult>.Fail(
                "kicad-cli was found, but PCBHelper V1 targets KiCad 9+.",
                "KICAD_VERSION_UNSUPPORTED",
                versionText,
                result);
        }

        return ToolResponse<DoctorResult>.Ok($"KiCad CLI is available: {versionText}", result);
    }
}

public sealed record DoctorResult(
    string ExecutablePath,
    string Source,
    int VersionCommandExitCode,
    string VersionOutput,
    string? ParsedVersion,
    bool IsSupported);
