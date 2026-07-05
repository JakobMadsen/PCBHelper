namespace PCBHelper.Core;

public sealed class GuiReviewService
{
    private readonly KiCadCliLocator _cliLocator;
    private readonly KiCadExecutableLocator _guiLocator;
    private readonly ICommandRunner _runner;

    public GuiReviewService(KiCadCliLocator cliLocator, KiCadExecutableLocator guiLocator, ICommandRunner runner)
    {
        _cliLocator = cliLocator;
        _guiLocator = guiLocator;
        _runner = runner;
    }

    public async Task<ToolResponse<KiCadGuiCapabilities>> GetCapabilitiesAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var cli = _cliLocator.Locate();
        var gui = _guiLocator.Locate();
        var apiServerAvailable = false;
        string? apiServerMessage = null;

        if (cli.Found && cli.ExecutablePath is not null)
        {
            var help = await _runner.RunAsync(cli.ExecutablePath, new[] { "--help" }, null, cancellationToken);
            apiServerAvailable = help.StandardOutput.Contains("api-server", StringComparison.OrdinalIgnoreCase)
                || help.StandardError.Contains("api-server", StringComparison.OrdinalIgnoreCase);
            apiServerMessage = apiServerAvailable
                ? "kicad-cli advertises api-server."
                : "kicad-cli does not advertise api-server; live GUI IPC is unavailable in this build.";
        }

        var capabilities = new KiCadGuiCapabilities(
            cli.Found,
            cli.ExecutablePath,
            gui.Found,
            gui.ExecutablePath,
            apiServerAvailable,
            apiServerAvailable,
            apiServerMessage ?? cli.Message ?? gui.Message,
            "Reload or reopen the KiCad project if live refresh is unavailable.");

        return ToolResponse<KiCadGuiCapabilities>.Ok("Read KiCad GUI capabilities.", capabilities);
    }

    public async Task<ToolResponse<KiCadGuiActionResult>> RefreshProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var capabilities = await GetCapabilitiesAsync(projectPath, cancellationToken);
        if (capabilities.Data?.CanRefreshLive != true)
        {
            return ToolResponse<KiCadGuiActionResult>.Fail(
                "KiCad live refresh is unavailable.",
                "KICAD_IPC_UNAVAILABLE",
                capabilities.Data?.Fallback,
                new KiCadGuiActionResult(false, capabilities.Data?.Fallback));
        }

        return ToolResponse<KiCadGuiActionResult>.Ok("Requested KiCad project refresh.", new KiCadGuiActionResult(true, null));
    }

    public async Task<ToolResponse<KiCadGuiActionResult>> FocusComponentAsync(string projectPath, string reference, CancellationToken cancellationToken = default)
    {
        var capabilities = await GetCapabilitiesAsync(projectPath, cancellationToken);
        if (capabilities.Data?.CanFocusComponent != true)
        {
            return ToolResponse<KiCadGuiActionResult>.Fail(
                "KiCad component focus is unavailable.",
                "KICAD_IPC_UNAVAILABLE",
                capabilities.Data?.Fallback,
                new KiCadGuiActionResult(false, capabilities.Data?.Fallback));
        }

        return ToolResponse<KiCadGuiActionResult>.Ok($"Requested KiCad focus for {reference}.", new KiCadGuiActionResult(true, null));
    }
}

public sealed record KiCadGuiCapabilities(
    bool CliFound,
    string? CliPath,
    bool GuiFound,
    string? GuiPath,
    bool ApiServerAvailable,
    bool CanRefreshLive,
    string? Message,
    string Fallback)
{
    public bool CanFocusComponent => CanRefreshLive;
}

public sealed record KiCadGuiActionResult(bool Performed, string? Fallback);
