using System.Globalization;

namespace PCBHelper.Core;

public sealed class RoutingService
{
    private readonly ProjectDiscoveryService _projectDiscovery;

    public RoutingService(ProjectDiscoveryService projectDiscovery)
    {
        _projectDiscovery = projectDiscovery;
    }

    public ToolResponse<TrackListResult> ListTracks(string projectPath, string? net = null)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<TrackListResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var filter = ResolveOptionalNet(board.Data, net);
        if (!filter.Success)
        {
            return ToolResponse<TrackListResult>.Fail(filter.Summary, filter.Error?.Code ?? "NET_NOT_FOUND", filter.Error?.Message);
        }

        var tracks = board.Data.Segments
            .Where(segment => filter.Data is null || segment.NetCode == filter.Data.Code)
            .Select(segment => ToTrackSummary(board.Data, segment))
            .ToArray();
        return ToolResponse<TrackListResult>.Ok($"Found {tracks.Length} track segment(s).", new TrackListResult(board.Data.BoardFile, tracks));
    }

    public ToolResponse<ViaListResult> ListVias(string projectPath, string? net = null)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<ViaListResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var filter = ResolveOptionalNet(board.Data, net);
        if (!filter.Success)
        {
            return ToolResponse<ViaListResult>.Fail(filter.Summary, filter.Error?.Code ?? "NET_NOT_FOUND", filter.Error?.Message);
        }

        var vias = board.Data.Vias
            .Where(via => filter.Data is null || via.NetCode == filter.Data.Code)
            .Select(via => ToViaSummary(board.Data, via))
            .ToArray();
        return ToolResponse<ViaListResult>.Ok($"Found {vias.Length} via(s).", new ViaListResult(board.Data.BoardFile, vias));
    }

    public ToolResponse<NetRoutingResult> GetNetRouting(string projectPath, string net)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<NetRoutingResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var resolved = ResolveNet(board.Data, net);
        if (!resolved.Success || resolved.Data is null)
        {
            return ToolResponse<NetRoutingResult>.Fail(resolved.Summary, resolved.Error?.Code ?? "NET_NOT_FOUND", resolved.Error?.Message);
        }

        var pads = board.Data.Footprints
            .Where(static footprint => footprint.Reference is not null)
            .SelectMany(footprint => footprint.Pads
                .Where(pad => pad.NetCode == resolved.Data.Code)
                .Select(pad =>
                {
                    var absolute = BoardInspectionService.CalculateAbsolutePadPosition(footprint, pad);
                    return new RoutingPadSummary(
                        footprint.Reference!,
                        pad.Name,
                        pad.PinFunction,
                        footprint.Side,
                        absolute.X,
                        absolute.Y,
                        pad.Layers);
                }))
            .ToArray();
        var tracks = board.Data.Segments.Where(segment => segment.NetCode == resolved.Data.Code).Select(segment => ToTrackSummary(board.Data, segment)).ToArray();
        var vias = board.Data.Vias.Where(via => via.NetCode == resolved.Data.Code).Select(via => ToViaSummary(board.Data, via)).ToArray();

        return ToolResponse<NetRoutingResult>.Ok(
            $"Found routing for net {resolved.Data.Name}.",
            new NetRoutingResult(new RoutingNetSummary(resolved.Data.Code, resolved.Data.Name), pads, tracks, vias));
    }

    public ToolResponse<RoutingMutationResult> AddTrack(
        string projectPath,
        string net,
        double startXMillimeters,
        double startYMillimeters,
        double endXMillimeters,
        double endYMillimeters,
        string layer,
        double widthMillimeters,
        bool dryRun)
    {
        if (!IsFinite(startXMillimeters) || !IsFinite(startYMillimeters) || !IsFinite(endXMillimeters) || !IsFinite(endYMillimeters)
            || widthMillimeters <= 0
            || (startXMillimeters == endXMillimeters && startYMillimeters == endYMillimeters))
        {
            return ToolResponse<RoutingMutationResult>.Fail("Track geometry is invalid.", "INVALID_ROUTING_GEOMETRY");
        }

        if (!IsSupportedCopperLayer(layer))
        {
            return ToolResponse<RoutingMutationResult>.Fail("Only F.Cu and B.Cu are supported for V1 track routing.", "UNSUPPORTED_LAYER");
        }

        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var resolved = ResolveNet(board.Data, net);
        if (!resolved.Success || resolved.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(resolved.Summary, resolved.Error?.Code ?? "NET_NOT_FOUND", resolved.Error?.Message);
        }

        var uuid = Guid.NewGuid().ToString();
        var text = FormatSegment(startXMillimeters, startYMillimeters, endXMillimeters, endYMillimeters, widthMillimeters, layer, resolved.Data.Code, uuid);
        if (!dryRun)
        {
            File.WriteAllText(board.Data.BoardFile, InsertRoutingObject(board.Data.Text, text));
        }

        var item = new RoutingItemChange("track", uuid, null, text);
        return ToolResponse<RoutingMutationResult>.Ok(
            $"{(dryRun ? "Previewed" : "Added")} track {uuid}.",
            new RoutingMutationResult("add-track", dryRun, board.Data.BoardFile, item, null, null, Array.Empty<string>()));
    }

    public ToolResponse<RoutingMutationResult> DeleteTrack(string projectPath, string track, bool dryRun)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var segment = FindSegment(board.Data, track);
        if (segment is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail($"Track not found: {track}", "ROUTING_ITEM_NOT_FOUND");
        }

        if (!dryRun)
        {
            File.WriteAllText(board.Data.BoardFile, RemoveSpanWithTrailingWhitespace(board.Data.Text, segment.SourceStart, segment.SourceLength));
        }

        var item = new RoutingItemChange("track", segment.Id, segment.SourceText, null);
        return ToolResponse<RoutingMutationResult>.Ok(
            $"{(dryRun ? "Previewed deleting" : "Deleted")} track {segment.Id}.",
            new RoutingMutationResult("delete-track", dryRun, board.Data.BoardFile, item, null, null, Array.Empty<string>()));
    }

    public ToolResponse<RoutingMutationResult> AddVia(
        string projectPath,
        string net,
        double xMillimeters,
        double yMillimeters,
        double sizeMillimeters,
        double drillMillimeters,
        string layers,
        bool dryRun)
    {
        var parsedLayers = layers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!IsFinite(xMillimeters) || !IsFinite(yMillimeters)
            || sizeMillimeters <= 0
            || drillMillimeters <= 0
            || drillMillimeters > sizeMillimeters)
        {
            return ToolResponse<RoutingMutationResult>.Fail("Via geometry is invalid.", "INVALID_ROUTING_GEOMETRY");
        }

        if (parsedLayers.Length != 2 || parsedLayers.Any(layer => !IsSupportedCopperLayer(layer)))
        {
            return ToolResponse<RoutingMutationResult>.Fail("Only F.Cu,B.Cu through vias are supported for V1 routing.", "UNSUPPORTED_LAYER");
        }

        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var resolved = ResolveNet(board.Data, net);
        if (!resolved.Success || resolved.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(resolved.Summary, resolved.Error?.Code ?? "NET_NOT_FOUND", resolved.Error?.Message);
        }

        var uuid = Guid.NewGuid().ToString();
        var text = FormatVia(xMillimeters, yMillimeters, sizeMillimeters, drillMillimeters, parsedLayers, resolved.Data.Code, uuid);
        if (!dryRun)
        {
            File.WriteAllText(board.Data.BoardFile, InsertRoutingObject(board.Data.Text, text));
        }

        var item = new RoutingItemChange("via", uuid, null, text);
        return ToolResponse<RoutingMutationResult>.Ok(
            $"{(dryRun ? "Previewed" : "Added")} via {uuid}.",
            new RoutingMutationResult("add-via", dryRun, board.Data.BoardFile, item, null, null, Array.Empty<string>()));
    }

    public ToolResponse<RoutingMutationResult> DeleteVia(string projectPath, string via, bool dryRun)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var item = FindVia(board.Data, via);
        if (item is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail($"Via not found: {via}", "ROUTING_ITEM_NOT_FOUND");
        }

        if (!dryRun)
        {
            File.WriteAllText(board.Data.BoardFile, RemoveSpanWithTrailingWhitespace(board.Data.Text, item.SourceStart, item.SourceLength));
        }

        var change = new RoutingItemChange("via", item.Id, item.SourceText, null);
        return ToolResponse<RoutingMutationResult>.Ok(
            $"{(dryRun ? "Previewed deleting" : "Deleted")} via {item.Id}.",
            new RoutingMutationResult("delete-via", dryRun, board.Data.BoardFile, change, null, null, Array.Empty<string>()));
    }

    internal ToolResponse<RoutingMutationResult> RestoreRoutingChange(string projectPath, ChangeReport report, bool dryRun)
    {
        if (report.RoutingItemKind is null || report.RoutingItemId is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail("Change report does not contain routing restore data.", "ROUTING_RESTORE_DATA_MISSING");
        }

        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var before = report.RoutingItemAfter;
        var after = report.RoutingItemBefore;
        if (!dryRun)
        {
            var text = board.Data.Text;
            if (before is not null)
            {
                var existing = report.RoutingItemKind == "track"
                    ? FindSegment(board.Data, report.RoutingItemId)
                    : null;
                var via = report.RoutingItemKind == "via"
                    ? FindVia(board.Data, report.RoutingItemId)
                    : null;
                if (existing is null && via is null)
                {
                    return ToolResponse<RoutingMutationResult>.Fail($"Routing item not found: {report.RoutingItemId}", "ROUTING_ITEM_NOT_FOUND");
                }

                var start = existing?.SourceStart ?? via!.SourceStart;
                var length = existing?.SourceLength ?? via!.SourceLength;
                text = RemoveSpanWithTrailingWhitespace(text, start, length);
            }

            if (after is not null)
            {
                text = InsertRoutingObject(text, after);
            }

            File.WriteAllText(board.Data.BoardFile, text);
        }

        return ToolResponse<RoutingMutationResult>.Ok(
            $"{(dryRun ? "Previewed restore" : "Restored")} routing change {report.ChangeId}.",
            new RoutingMutationResult("restore-change", dryRun, board.Data.BoardFile, new RoutingItemChange(report.RoutingItemKind, report.RoutingItemId, before, after), null, null, Array.Empty<string>()));
    }

    private ToolResponse<KiCadBoardDocument> LoadBoard(string projectPath)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<KiCadBoardDocument>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (project.Data.BoardFile is null)
        {
            return ToolResponse<KiCadBoardDocument>.Fail("Operation requires a .kicad_pcb file.", "BOARD_FILE_MISSING");
        }

        return ToolResponse<KiCadBoardDocument>.Ok("Loaded board.", KiCadBoardParser.Parse(project.Data.BoardFile));
    }

    private static TrackSummary ToTrackSummary(KiCadBoardDocument board, KiCadSegment segment)
    {
        var net = segment.NetCode is null ? null : board.Nets.FirstOrDefault(item => item.Code == segment.NetCode.Value);
        return new TrackSummary(
            segment.Id,
            segment.Uuid,
            segment.NetCode,
            net?.Name,
            segment.StartXMillimeters,
            segment.StartYMillimeters,
            segment.EndXMillimeters,
            segment.EndYMillimeters,
            segment.Layer,
            segment.WidthMillimeters);
    }

    private static ViaSummary ToViaSummary(KiCadBoardDocument board, KiCadVia via)
    {
        var net = via.NetCode is null ? null : board.Nets.FirstOrDefault(item => item.Code == via.NetCode.Value);
        return new ViaSummary(
            via.Id,
            via.Uuid,
            via.NetCode,
            net?.Name,
            via.XMillimeters,
            via.YMillimeters,
            via.SizeMillimeters,
            via.DrillMillimeters,
            via.Layers);
    }

    private static ToolResponse<KiCadNet?> ResolveOptionalNet(KiCadBoardDocument board, string? net)
    {
        if (string.IsNullOrWhiteSpace(net))
        {
            return ToolResponse<KiCadNet?>.Ok("No net filter.", null);
        }

        var resolved = ResolveNet(board, net);
        return resolved.Success
            ? ToolResponse<KiCadNet?>.Ok(resolved.Summary, resolved.Data)
            : ToolResponse<KiCadNet?>.Fail(resolved.Summary, resolved.Error?.Code ?? "NET_NOT_FOUND", resolved.Error?.Message);
    }

    private static ToolResponse<KiCadNet> ResolveNet(KiCadBoardDocument board, string net)
    {
        var match = board.Nets.FirstOrDefault(item =>
            string.Equals(item.Name, net, StringComparison.OrdinalIgnoreCase)
            || item.Code.ToString(CultureInfo.InvariantCulture) == net);
        return match is null
            ? ToolResponse<KiCadNet>.Fail($"Net not found: {net}", "NET_NOT_FOUND")
            : ToolResponse<KiCadNet>.Ok($"Resolved net {match.Name}.", match);
    }

    private static KiCadSegment? FindSegment(KiCadBoardDocument board, string id)
    {
        return board.Segments.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Uuid, id, StringComparison.OrdinalIgnoreCase));
    }

    private static KiCadVia? FindVia(KiCadBoardDocument board, string id)
    {
        return board.Vias.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Uuid, id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSupportedCopperLayer(string layer)
    {
        return layer is "F.Cu" or "B.Cu";
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string FormatSegment(double startX, double startY, double endX, double endY, double width, string layer, int netCode, string uuid)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "  (segment",
            $"    (start {KiCadBoardParser.FormatNumber(startX)} {KiCadBoardParser.FormatNumber(startY)})",
            $"    (end {KiCadBoardParser.FormatNumber(endX)} {KiCadBoardParser.FormatNumber(endY)})",
            $"    (width {KiCadBoardParser.FormatNumber(width)})",
            $"    (layer \"{layer}\")",
            $"    (net {netCode})",
            $"    (uuid \"{uuid}\")",
            "  )",
            string.Empty
        });
    }

    private static string FormatVia(double x, double y, double size, double drill, IReadOnlyList<string> layers, int netCode, string uuid)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "  (via",
            $"    (at {KiCadBoardParser.FormatNumber(x)} {KiCadBoardParser.FormatNumber(y)})",
            $"    (size {KiCadBoardParser.FormatNumber(size)})",
            $"    (drill {KiCadBoardParser.FormatNumber(drill)})",
            $"    (layers \"{layers[0]}\" \"{layers[1]}\")",
            $"    (net {netCode})",
            $"    (uuid \"{uuid}\")",
            "  )",
            string.Empty
        });
    }

    private static string InsertRoutingObject(string boardText, string objectText)
    {
        var marker = "  (embedded_fonts no)";
        var markerIndex = boardText.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            return boardText.Insert(markerIndex, objectText);
        }

        var lastClose = boardText.LastIndexOf(')');
        return lastClose >= 0 ? boardText.Insert(lastClose, objectText) : boardText + objectText;
    }

    private static string RemoveSpanWithTrailingWhitespace(string text, int start, int length)
    {
        var end = start + length;
        while (end < text.Length && (text[end] == '\r' || text[end] == '\n'))
        {
            end++;
        }

        return text.Remove(start, end - start);
    }
}

public sealed record TrackListResult(string BoardFile, IReadOnlyList<TrackSummary> Tracks);

public sealed record ViaListResult(string BoardFile, IReadOnlyList<ViaSummary> Vias);

public sealed record NetRoutingResult(
    RoutingNetSummary Net,
    IReadOnlyList<RoutingPadSummary> Pads,
    IReadOnlyList<TrackSummary> Tracks,
    IReadOnlyList<ViaSummary> Vias);

public sealed record RoutingNetSummary(int Code, string Name);

public sealed record RoutingPadSummary(
    string FootprintReference,
    string PadName,
    string? PinFunction,
    string FootprintSide,
    double? AbsoluteXMillimeters,
    double? AbsoluteYMillimeters,
    IReadOnlyList<string> Layers);

public sealed record TrackSummary(
    string Id,
    string? Uuid,
    int? NetCode,
    string? NetName,
    double? StartXMillimeters,
    double? StartYMillimeters,
    double? EndXMillimeters,
    double? EndYMillimeters,
    string? Layer,
    double? WidthMillimeters);

public sealed record ViaSummary(
    string Id,
    string? Uuid,
    int? NetCode,
    string? NetName,
    double? XMillimeters,
    double? YMillimeters,
    double? SizeMillimeters,
    double? DrillMillimeters,
    IReadOnlyList<string> Layers);

public sealed record RoutingItemChange(
    string Kind,
    string Id,
    string? BeforeText,
    string? AfterText);

public sealed record RoutingMutationResult(
    string Operation,
    bool DryRun,
    string ChangedFile,
    RoutingItemChange Item,
    string? ChangeReportPath,
    string? CheckSummary,
    IReadOnlyList<string> CheckReportPaths);
