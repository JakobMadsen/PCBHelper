using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCBHelper.Core;

/// <summary>
/// Runs the non-deterministic autorouter in an isolated copy and applies the exact
/// previewed board through the normal project transaction/rollback machinery.
/// </summary>
public sealed class AutorouteTransactionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ProjectDiscoveryService _projects;
    private readonly ProjectTransactionService _transactions;
    private readonly EngineeringGateService _gates;
    private readonly KiCadCliLocator _kiCad;
    private readonly FreeRoutingLocator _freeRouting;
    private readonly ICommandRunner _runner;

    public AutorouteTransactionService(
        ProjectDiscoveryService projects,
        ProjectTransactionService transactions,
        EngineeringGateService gates,
        KiCadCliLocator kiCad,
        FreeRoutingLocator freeRouting,
        ICommandRunner runner)
    {
        _projects = projects;
        _transactions = transactions;
        _gates = gates;
        _kiCad = kiCad;
        _freeRouting = freeRouting;
        _runner = runner;
    }

    public async Task<ToolResponse<AutoroutePreviewResult>> PreviewAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data?.BoardFile is null)
            return ToolResponse<AutoroutePreviewResult>.Fail(project.Summary, project.Error?.Code ?? "BOARD_FILE_MISSING", project.Error?.Message);

        var sandbox = Path.Combine(Path.GetTempPath(), "pcbhelper-autoroute", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);
            foreach (var file in Directory.GetFiles(project.Data.ProjectRoot, "*", SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(sandbox, Path.GetFileName(file)));

            var sandboxProjects = new ProjectDiscoveryService(ProjectScopePolicy.Unrestricted());
            var autorouter = new AutoroutingService(sandboxProjects, _kiCad, _freeRouting, _runner);
            var routed = await autorouter.AutorouteBoardAsync(sandbox, false, ripupExisting: true, cancellationToken);
            if (!routed.Success || routed.Data?.BoardFile is null)
                return ToolResponse<AutoroutePreviewResult>.Fail(routed.Summary, routed.Error?.Code ?? "AUTOROUTE_FAILED", routed.Error?.Message);

            var beforeContent = await File.ReadAllTextAsync(project.Data.BoardFile, cancellationToken);
            var afterContent = await File.ReadAllTextAsync(routed.Data.BoardFile, cancellationToken);
            var beforePads = PadNets(project.Data.BoardFile);
            var afterPads = PadNets(routed.Data.BoardFile);
            var changedPadNets = beforePads.Where(pair => !afterPads.TryGetValue(pair.Key, out var afterNet) || !string.Equals(pair.Value, afterNet, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (changedPadNets.Length > 0)
                return ToolResponse<AutoroutePreviewResult>.Fail(
                    "Autoroute changed or removed board pad net identity.",
                    "AUTOROUTE_NET_IDENTITY_CHANGED",
                    string.Join(", ", changedPadNets.Take(12).Select(pair => $"{pair.Key}: {pair.Value} -> {afterPads.GetValueOrDefault(pair.Key) ?? "<missing>"}")));
            var beforeHash = ProjectTransactionService.ContentHash(beforeContent);
            var afterHash = ProjectTransactionService.ContentHash(afterContent);
            if (string.Equals(beforeHash, afterHash, StringComparison.Ordinal))
                return ToolResponse<AutoroutePreviewResult>.Fail("Autorouter produced no board changes.", "AUTOROUTE_EMPTY");

            var previewId = Guid.NewGuid().ToString("N");
            var previewRoot = GetPreviewRoot(project.Data.ProjectRoot, previewId);
            Directory.CreateDirectory(previewRoot);
            var boardSnapshot = Path.Combine(previewRoot, "routed.kicad_pcb");
            await File.WriteAllTextAsync(boardSnapshot, afterContent, cancellationToken);
            var relativeBoard = Path.GetRelativePath(project.Data.ProjectRoot, project.Data.BoardFile);
            var manifest = new AutoroutePreviewManifest(previewId, relativeBoard, beforeHash, afterHash, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(previewRoot, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);

            return ToolResponse<AutoroutePreviewResult>.Ok(
                "Autoroute completed in isolation; the project board was not modified.",
                new AutoroutePreviewResult(previewId, relativeBoard, beforeHash, afterHash, manifest.CreatedAtUtc),
                new[] { "Apply uses the exact routed snapshot and rejects any intervening board change." });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ToolResponse<AutoroutePreviewResult>.Fail("Could not prepare autoroute preview.", "AUTOROUTE_PREVIEW_FAILED", exception.Message);
        }
        finally
        {
            var keepSandbox = string.Equals(Environment.GetEnvironmentVariable("PCBHELPER_KEEP_AUTOROUTE_SANDBOX"), "1", StringComparison.Ordinal);
            if (!keepSandbox && Directory.Exists(sandbox))
                try { Directory.Delete(sandbox, true); } catch (IOException) { }
        }
    }

    public async Task<ToolResponse<AutorouteApplyResult>> ApplyAsync(
        string projectPath,
        string previewId,
        string expectedAfterHash,
        CancellationToken cancellationToken = default)
    {
        if (!IsPreviewId(previewId))
            return ToolResponse<AutorouteApplyResult>.Fail("Preview id is invalid.", "AUTOROUTE_PREVIEW_INVALID");

        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<AutorouteApplyResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);

        var previewRoot = GetPreviewRoot(project.Data.ProjectRoot, previewId);
        var manifestPath = Path.Combine(previewRoot, "manifest.json");
        var snapshotPath = Path.Combine(previewRoot, "routed.kicad_pcb");
        if (!File.Exists(manifestPath) || !File.Exists(snapshotPath))
            return ToolResponse<AutorouteApplyResult>.Fail("Autoroute preview was not found.", "AUTOROUTE_PREVIEW_NOT_FOUND");

        try
        {
            var manifest = JsonSerializer.Deserialize<AutoroutePreviewManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), JsonOptions);
            if (manifest is null || !string.Equals(manifest.PreviewId, previewId, StringComparison.Ordinal)
                || !string.Equals(manifest.AfterHash, expectedAfterHash, StringComparison.OrdinalIgnoreCase))
                return ToolResponse<AutorouteApplyResult>.Fail("Autoroute preview hash does not match.", "AUTOROUTE_PREVIEW_HASH_MISMATCH");

            var boardPath = Path.GetFullPath(Path.Combine(project.Data.ProjectRoot, manifest.RelativeBoardPath));
            if (!boardPath.StartsWith(project.Data.ProjectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(boardPath))
                return ToolResponse<AutorouteApplyResult>.Fail("Preview board path is outside the project or missing.", "PROJECT_SCOPE_VIOLATION");

            var beforeContent = await File.ReadAllTextAsync(boardPath, cancellationToken);
            if (!string.Equals(ProjectTransactionService.ContentHash(beforeContent), manifest.BeforeHash, StringComparison.Ordinal))
                return ToolResponse<AutorouteApplyResult>.Fail("Board changed after autoroute preview.", "TRANSACTION_CONFLICT");

            var afterContent = await File.ReadAllTextAsync(snapshotPath, cancellationToken);
            if (!string.Equals(ProjectTransactionService.ContentHash(afterContent), manifest.AfterHash, StringComparison.Ordinal))
                return ToolResponse<AutorouteApplyResult>.Fail("Stored autoroute preview failed integrity validation.", "AUTOROUTE_PREVIEW_CORRUPT");

            var change = PreparedFileChange.Create(manifest.RelativeBoardPath, beforeContent, afterContent);
            var operation = new PreparedOperation("autoroute-board", "autoroute-board", "Apply exact isolated FreeRouting result.");
            var applied = await _transactions.ApplyAsync(project.Data.ProjectRoot, "Route all board connections", manifest.AfterHash,
                new[] { operation }, new[] { change }, cancellationToken: cancellationToken);
            if (!applied.Success || applied.Data is null)
                return ToolResponse<AutorouteApplyResult>.Fail(applied.Summary, applied.Error?.Code ?? "TRANSACTION_APPLY_FAILED", applied.Error?.Message);

            var gate = await _gates.RunAsync(project.Data.ProjectRoot, EngineeringGateRequirements.Default, cancellationToken);
            if (!gate.Success || gate.Data is null || gate.Data.Status == EngineeringGateStatus.ExecutionFailed)
            {
                var restored = await _transactions.RestoreAsync(project.Data.ProjectRoot, applied.Data.Transaction.TransactionId, CancellationToken.None);
                return ToolResponse<AutorouteApplyResult>.Fail(
                    restored.Success ? "Engineering gate execution failed; autoroute transaction was rolled back." : "Engineering gate execution failed and rollback was incomplete.",
                    restored.Success ? "ENGINEERING_GATE_EXECUTION_FAILED" : "TRANSACTION_INCOMPLETE",
                    gate.Error?.Message ?? gate.Summary);
            }

            var recorded = await _transactions.SetGateResultAsync(project.Data.ProjectRoot, applied.Data.Transaction.TransactionId, gate.Data, cancellationToken);
            return ToolResponse<AutorouteApplyResult>.Ok(
                gate.Data.Status == EngineeringGateStatus.Passed ? "Applied autoroute preview and passed engineering gates." : $"Applied autoroute preview; engineering gate status is {gate.Data.Status}.",
                new AutorouteApplyResult(previewId, recorded.Data ?? applied.Data, gate.Data));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ToolResponse<AutorouteApplyResult>.Fail("Could not apply autoroute preview.", "AUTOROUTE_APPLY_FAILED", exception.Message);
        }
    }

    private static string GetPreviewRoot(string projectRoot, string previewId) =>
        Path.Combine(projectRoot, ".pcbhelper", "autoroute-previews", previewId);

    private static bool IsPreviewId(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);
    private static Dictionary<string,string> PadNets(string boardFile)
    {
        var board=KiCadBoardParser.Parse(boardFile);var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var footprint in board.Footprints.Where(f=>f.Reference is not null))
        foreach(var pad in footprint.Pads.Where(p=>!string.IsNullOrWhiteSpace(p.NetName)))
            result[$"{footprint.Reference}.{pad.Name}"]=pad.NetName!;
        return result;
    }
}

public sealed record AutoroutePreviewManifest(string PreviewId, string RelativeBoardPath, string BeforeHash, string AfterHash, DateTimeOffset CreatedAtUtc);
public sealed record AutoroutePreviewResult(string PreviewId, string RelativeBoardPath, string BeforeHash, string AfterHash, DateTimeOffset CreatedAtUtc);
public sealed record AutorouteApplyResult(string PreviewId, ProjectTransactionResult Transaction, EngineeringGateResult EngineeringGate);
