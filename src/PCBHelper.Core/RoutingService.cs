using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

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
            .Where(segment => filter.Data is null || NetReferenceMatches(segment.NetCode, segment.NetName, filter.Data))
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
            .Where(via => filter.Data is null || NetReferenceMatches(via.NetCode, via.NetName, filter.Data))
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
                .Where(pad => NetReferenceMatches(pad.NetCode, pad.NetName, resolved.Data))
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
        var tracks = board.Data.Segments.Where(segment => NetReferenceMatches(segment.NetCode, segment.NetName, resolved.Data)).Select(segment => ToTrackSummary(board.Data, segment)).ToArray();
        var vias = board.Data.Vias.Where(via => NetReferenceMatches(via.NetCode, via.NetName, resolved.Data)).Select(via => ToViaSummary(board.Data, via)).ToArray();

        return ToolResponse<NetRoutingResult>.Ok(
            $"Found routing for net {resolved.Data.Name}.",
            new NetRoutingResult(new RoutingNetSummary(resolved.Data.Code, resolved.Data.Name), pads, tracks, vias));
    }

    public ToolResponse<UnroutedConnectionListResult> ListUnroutedConnections(string projectPath, string? net = null)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<UnroutedConnectionListResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var filter = ResolveOptionalNet(board.Data, net);
        if (!filter.Success)
        {
            return ToolResponse<UnroutedConnectionListResult>.Fail(filter.Summary, filter.Error?.Code ?? "NET_NOT_FOUND", filter.Error?.Message);
        }

        var nets = filter.Data is null ? board.Data.Nets : new[] { filter.Data };
        var unrouted = nets
            .Select(netItem => BuildUnroutedNet(board.Data, netItem))
            .Where(static item => item.MissingConnections.Count > 0)
            .ToArray();

        return ToolResponse<UnroutedConnectionListResult>.Ok(
            $"Found {unrouted.Sum(static item => item.MissingConnections.Count)} unrouted connection(s).",
            new UnroutedConnectionListResult(board.Data.BoardFile, unrouted));
    }

    public ToolResponse<RoutingClearanceValidationResult> ValidateTrackClearance(
        string projectPath,
        string net,
        string points,
        string layer,
        double widthMillimeters)
    {
        var parsedPoints = ParsePolylinePoints(points);
        if (!parsedPoints.Success || parsedPoints.Data is null)
        {
            return ToolResponse<RoutingClearanceValidationResult>.Fail(parsedPoints.Summary, parsedPoints.Error?.Code ?? "INVALID_ROUTING_GEOMETRY", parsedPoints.Error?.Message);
        }

        return ValidateTrackClearance(projectPath, net, parsedPoints.Data, layer, widthMillimeters);
    }

    public ToolResponse<RoutingClearanceValidationResult> ValidateTrackClearance(
        string projectPath,
        string net,
        IReadOnlyList<RoutingPoint> points,
        string layer,
        double widthMillimeters)
    {
        var geometry = ValidatePolylineGeometry(points, layer, widthMillimeters);
        if (!geometry.Success)
        {
            return ToolResponse<RoutingClearanceValidationResult>.Fail(geometry.Summary, geometry.Error?.Code ?? "INVALID_ROUTING_GEOMETRY", geometry.Error?.Message);
        }

        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<RoutingClearanceValidationResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var resolved = ResolveNet(board.Data, net);
        if (!resolved.Success || resolved.Data is null)
        {
            return ToolResponse<RoutingClearanceValidationResult>.Fail(resolved.Summary, resolved.Error?.Code ?? "NET_NOT_FOUND", resolved.Error?.Message);
        }

        var clearance = ResolveMinimumClearanceMillimeters(projectPath);
        var violations = ValidatePolylineAgainstBoard(board.Data, resolved.Data.Code, points, layer, widthMillimeters, clearance);
        if (violations.Count > 0)
        {
            return ToolResponse<RoutingClearanceValidationResult>.Fail(
                $"Routing clearance violation: {violations[0].Message}",
                "ROUTING_CLEARANCE_VIOLATION",
                violations[0].Message);
        }

        return ToolResponse<RoutingClearanceValidationResult>.Ok(
            "Track clearance validation passed.",
            new RoutingClearanceValidationResult(
                board.Data.BoardFile,
                new RoutingNetSummary(resolved.Data.Code, resolved.Data.Name),
                layer,
                widthMillimeters,
                clearance,
                points,
                Array.Empty<RoutingClearanceViolation>()));
    }

    public ToolResponse<RoutingMutationResult> AddTrackPolyline(
        string projectPath,
        string net,
        string points,
        string layer,
        double widthMillimeters,
        bool dryRun)
    {
        var parsedPoints = ParsePolylinePoints(points);
        if (!parsedPoints.Success || parsedPoints.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(parsedPoints.Summary, parsedPoints.Error?.Code ?? "INVALID_ROUTING_GEOMETRY", parsedPoints.Error?.Message);
        }

        return AddTrackPolyline(projectPath, net, parsedPoints.Data, layer, widthMillimeters, dryRun);
    }

    public ToolResponse<RoutingMutationResult> AddTrackPolyline(
        string projectPath,
        string net,
        IReadOnlyList<RoutingPoint> points,
        string layer,
        double widthMillimeters,
        bool dryRun)
    {
        var validation = ValidateTrackClearance(projectPath, net, points, layer, widthMillimeters);
        if (!validation.Success || validation.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(validation.Summary, validation.Error?.Code ?? "ROUTING_CLEARANCE_VALIDATION_FAILED", validation.Error?.Message);
        }

        var board = KiCadBoardParser.Parse(validation.Data.BoardFile);
        var texts = new List<string>();
        var uuids = new List<string>();
        for (var index = 0; index < points.Count - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            var uuid = Guid.NewGuid().ToString();
            uuids.Add(uuid);
            texts.Add(FormatSegment(start.XMillimeters, start.YMillimeters, end.XMillimeters, end.YMillimeters, widthMillimeters, layer, validation.Data.Net.Code, uuid));
        }

        var text = string.Concat(texts);
        if (!dryRun)
        {
            File.WriteAllText(board.BoardFile, InsertRoutingObject(board.Text, text));
        }

        var item = new RoutingItemChange("track-polyline", uuids[0], null, text);
        return ToolResponse<RoutingMutationResult>.Ok(
            $"{(dryRun ? "Previewed" : "Added")} track polyline with {uuids.Count} segment(s).",
            new RoutingMutationResult("add-track-polyline", dryRun, board.BoardFile, item, null, null, Array.Empty<string>()));
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

        var clearance = ResolveMinimumClearanceMillimeters(projectPath);
        var violations = ValidateViaAgainstBoard(board.Data, resolved.Data.Code, xMillimeters, yMillimeters, sizeMillimeters, parsedLayers, clearance);
        if (violations.Count > 0)
        {
            return ToolResponse<RoutingMutationResult>.Fail(
                $"Routing clearance violation: {violations[0].Message}",
                "ROUTING_CLEARANCE_VIOLATION",
                violations[0].Message);
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
                if (report.RoutingItemKind == "track-polyline")
                {
                    var segments = ExtractUuids(before)
                        .Select(uuid => FindSegment(board.Data, uuid))
                        .Where(static item => item is not null)
                        .Cast<KiCadSegment>()
                        .OrderByDescending(static item => item.SourceStart)
                        .ToArray();
                    if (segments.Length == 0)
                    {
                        return ToolResponse<RoutingMutationResult>.Fail($"Routing item not found: {report.RoutingItemId}", "ROUTING_ITEM_NOT_FOUND");
                    }

                    foreach (var segment in segments)
                    {
                        text = RemoveSpanWithTrailingWhitespace(text, segment.SourceStart, segment.SourceLength);
                    }
                }
                else
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

    private UnroutedNetSummary BuildUnroutedNet(KiCadBoardDocument board, KiCadNet net)
    {
        var nodes = new List<RoutingConnectivityNode>();
        var union = new DisjointSet();
        var pointIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        int AddPoint(double x, double y, RoutingPadEndpoint? pad)
        {
            var key = PointKey(x, y);
            if (!pointIndex.TryGetValue(key, out var index))
            {
                index = nodes.Count;
                pointIndex[key] = index;
                nodes.Add(new RoutingConnectivityNode(x, y, new List<RoutingPadEndpoint>()));
                union.Add();
            }

            if (pad is not null)
            {
                nodes[index].Pads.Add(pad);
            }

            return index;
        }

        foreach (var footprint in board.Footprints.Where(static item => item.Reference is not null))
        {
            foreach (var pad in footprint.Pads.Where(pad => NetReferenceMatches(pad.NetCode, pad.NetName, net)))
            {
                var absolute = BoardInspectionService.CalculateAbsolutePadPosition(footprint, pad);
                if (absolute.X is null || absolute.Y is null)
                {
                    continue;
                }

                AddPoint(
                    absolute.X.Value,
                    absolute.Y.Value,
                    new RoutingPadEndpoint(footprint.Reference!, pad.Name, pad.PinFunction, absolute.X.Value, absolute.Y.Value));
            }
        }

        foreach (var segment in board.Segments.Where(segment => NetReferenceMatches(segment.NetCode, segment.NetName, net)))
        {
            if (segment.StartXMillimeters is null || segment.StartYMillimeters is null || segment.EndXMillimeters is null || segment.EndYMillimeters is null)
            {
                continue;
            }

            var start = AddPoint(segment.StartXMillimeters.Value, segment.StartYMillimeters.Value, null);
            var end = AddPoint(segment.EndXMillimeters.Value, segment.EndYMillimeters.Value, null);
            union.Union(start, end);
        }

        foreach (var via in board.Vias.Where(via => NetReferenceMatches(via.NetCode, via.NetName, net)))
        {
            if (via.XMillimeters is null || via.YMillimeters is null)
            {
                continue;
            }

            AddPoint(via.XMillimeters.Value, via.YMillimeters.Value, null);
        }

        var components = nodes
            .Select((node, index) => new { Root = union.Find(index), Node = node })
            .GroupBy(static item => item.Root)
            .Select(group =>
            {
                var pads = group.SelectMany(static item => item.Node.Pads).ToArray();
                var points = group.Select(static item => new RoutingPoint(item.Node.XMillimeters, item.Node.YMillimeters)).ToArray();
                return new RoutedNetComponent(pads, points);
            })
            .Where(static component => component.Pads.Count > 0)
            .ToArray();

        var missing = new List<UnroutedConnectionSummary>();
        for (var index = 0; index < components.Length - 1; index++)
        {
            var best = FindNearestConnection(components[index], components[index + 1]);
            if (best is not null)
            {
                missing.Add(best);
            }
        }

        return new UnroutedNetSummary(new RoutingNetSummary(net.Code, net.Name), components, missing);
    }

    private static UnroutedConnectionSummary? FindNearestConnection(RoutedNetComponent from, RoutedNetComponent to)
    {
        RoutingPadEndpoint? bestFrom = null;
        RoutingPadEndpoint? bestTo = null;
        var bestDistance = double.MaxValue;

        foreach (var fromPad in from.Pads)
        {
            foreach (var toPad in to.Pads)
            {
                var distance = Distance(fromPad.XMillimeters, fromPad.YMillimeters, toPad.XMillimeters, toPad.YMillimeters);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestFrom = fromPad;
                    bestTo = toPad;
                }
            }
        }

        return bestFrom is null || bestTo is null
            ? null
            : new UnroutedConnectionSummary(bestFrom, bestTo, bestDistance);
    }

    private static ToolResponse<IReadOnlyList<RoutingPoint>> ParsePolylinePoints(string points)
    {
        if (string.IsNullOrWhiteSpace(points))
        {
            return ToolResponse<IReadOnlyList<RoutingPoint>>.Fail("Routing points are required.", "INVALID_ROUTING_GEOMETRY");
        }

        var parsed = new List<RoutingPoint>();
        foreach (var part in points.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var coordinates = part.Split(',', StringSplitOptions.TrimEntries);
            if (coordinates.Length != 2
                || !double.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !IsFinite(x)
                || !IsFinite(y))
            {
                return ToolResponse<IReadOnlyList<RoutingPoint>>.Fail($"Invalid routing point: {part}", "INVALID_ROUTING_GEOMETRY");
            }

            parsed.Add(new RoutingPoint(x, y));
        }

        return ToolResponse<IReadOnlyList<RoutingPoint>>.Ok("Parsed routing points.", parsed);
    }

    private static ToolResponse<object> ValidatePolylineGeometry(IReadOnlyList<RoutingPoint> points, string layer, double widthMillimeters)
    {
        if (points.Count < 2 || widthMillimeters <= 0 || !IsFinite(widthMillimeters))
        {
            return ToolResponse<object>.Fail("Track geometry is invalid.", "INVALID_ROUTING_GEOMETRY");
        }

        if (!IsSupportedCopperLayer(layer))
        {
            return ToolResponse<object>.Fail("Only F.Cu and B.Cu are supported for V1 track routing.", "UNSUPPORTED_LAYER");
        }

        for (var index = 0; index < points.Count; index++)
        {
            if (!IsFinite(points[index].XMillimeters) || !IsFinite(points[index].YMillimeters))
            {
                return ToolResponse<object>.Fail("Track geometry is invalid.", "INVALID_ROUTING_GEOMETRY");
            }

            if (index > 0
                && points[index - 1].XMillimeters == points[index].XMillimeters
                && points[index - 1].YMillimeters == points[index].YMillimeters)
            {
                return ToolResponse<object>.Fail("Track geometry is invalid.", "INVALID_ROUTING_GEOMETRY");
            }
        }

        return ToolResponse<object>.Ok("Routing geometry is valid.", new object());
    }

    private double ResolveMinimumClearanceMillimeters(string projectPath)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data?.ProjectFile is null || !File.Exists(project.Data.ProjectFile))
        {
            return 0.2;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(project.Data.ProjectFile));
            if (document.RootElement.TryGetProperty("board", out var board)
                && board.TryGetProperty("design_settings", out var designSettings)
                && designSettings.TryGetProperty("rules", out var rules)
                && rules.TryGetProperty("min_clearance", out var minClearance)
                && minClearance.TryGetDouble(out var value))
            {
                return Math.Max(0, value);
            }
        }
        catch (JsonException)
        {
        }

        return 0.2;
    }

    private static IReadOnlyList<RoutingClearanceViolation> ValidatePolylineAgainstBoard(
        KiCadBoardDocument board,
        int netCode,
        IReadOnlyList<RoutingPoint> points,
        string layer,
        double widthMillimeters,
        double clearanceMillimeters)
    {
        var proposedRadius = widthMillimeters / 2;
        var obstacles = BuildCopperObstacles(board, netCode, layer);
        var violations = new List<RoutingClearanceViolation>();

        for (var index = 0; index < points.Count - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            foreach (var obstacle in obstacles)
            {
                var gap = obstacle.Kind == "track"
                    ? SegmentToSegmentDistance(start, end, obstacle.Start!.Value, obstacle.End!.Value) - proposedRadius - obstacle.RadiusMillimeters
                    : PointToSegmentDistance(obstacle.Start!.Value, start, end) - proposedRadius - obstacle.RadiusMillimeters;
                if (gap < clearanceMillimeters - 0.000001)
                {
                    violations.Add(new RoutingClearanceViolation(
                        obstacle.Kind,
                        obstacle.Id,
                        obstacle.NetCode,
                        obstacle.NetName,
                        gap,
                        clearanceMillimeters,
                        $"Proposed track segment {index + 1} is {FormatDistance(gap)} mm from {obstacle.Kind} {obstacle.Id} on net {obstacle.NetName}."));
                }
            }
        }

        return violations;
    }

    private static IReadOnlyList<RoutingClearanceViolation> ValidateViaAgainstBoard(
        KiCadBoardDocument board,
        int netCode,
        double xMillimeters,
        double yMillimeters,
        double sizeMillimeters,
        IReadOnlyList<string> layers,
        double clearanceMillimeters)
    {
        var proposed = new RoutingPoint(xMillimeters, yMillimeters);
        var proposedRadius = sizeMillimeters / 2;
        var obstacles = layers.SelectMany(layer => BuildCopperObstacles(board, netCode, layer)).DistinctBy(static item => item.Id).ToArray();
        var violations = new List<RoutingClearanceViolation>();
        foreach (var obstacle in obstacles)
        {
            var gap = obstacle.Kind == "track"
                ? PointToSegmentDistance(proposed, obstacle.Start!.Value, obstacle.End!.Value) - proposedRadius - obstacle.RadiusMillimeters
                : Distance(proposed.XMillimeters, proposed.YMillimeters, obstacle.Start!.Value.XMillimeters, obstacle.Start.Value.YMillimeters) - proposedRadius - obstacle.RadiusMillimeters;
            if (gap < clearanceMillimeters - 0.000001)
            {
                violations.Add(new RoutingClearanceViolation(
                    obstacle.Kind,
                    obstacle.Id,
                    obstacle.NetCode,
                    obstacle.NetName,
                    gap,
                    clearanceMillimeters,
                    $"Proposed via is {FormatDistance(gap)} mm from {obstacle.Kind} {obstacle.Id} on net {obstacle.NetName}."));
            }
        }

        return violations;
    }

    private static IReadOnlyList<CopperObstacle> BuildCopperObstacles(KiCadBoardDocument board, int targetNetCode, string layer)
    {
        var obstacles = new List<CopperObstacle>();
        foreach (var footprint in board.Footprints.Where(static footprint => footprint.Reference is not null))
        {
            foreach (var pad in footprint.Pads.Where(pad => pad.NetCode is not null && pad.NetCode != targetNetCode && PadTouchesLayer(pad, layer)))
            {
                var absolute = BoardInspectionService.CalculateAbsolutePadPosition(footprint, pad);
                if (absolute.X is null || absolute.Y is null)
                {
                    continue;
                }

                var radius = Math.Max(pad.SizeXMillimeters ?? 0.6, pad.SizeYMillimeters ?? 0.6) / 2;
                obstacles.Add(new CopperObstacle(
                    "pad",
                    $"{footprint.Reference}.{pad.Name}",
                    pad.NetCode,
                    pad.NetName,
                    new RoutingPoint(absolute.X.Value, absolute.Y.Value),
                    null,
                    radius));
            }
        }

        foreach (var segment in board.Segments.Where(segment => segment.NetCode is not null && segment.NetCode != targetNetCode && segment.Layer == layer))
        {
            if (segment.StartXMillimeters is null || segment.StartYMillimeters is null || segment.EndXMillimeters is null || segment.EndYMillimeters is null)
            {
                continue;
            }

            var net = board.Nets.FirstOrDefault(item => item.Code == segment.NetCode);
            obstacles.Add(new CopperObstacle(
                "track",
                segment.Id,
                segment.NetCode,
                net?.Name,
                new RoutingPoint(segment.StartXMillimeters.Value, segment.StartYMillimeters.Value),
                new RoutingPoint(segment.EndXMillimeters.Value, segment.EndYMillimeters.Value),
                (segment.WidthMillimeters ?? 0.25) / 2));
        }

        foreach (var via in board.Vias.Where(via => via.NetCode is not null && via.NetCode != targetNetCode && ViaTouchesLayer(via, layer)))
        {
            if (via.XMillimeters is null || via.YMillimeters is null)
            {
                continue;
            }

            var net = board.Nets.FirstOrDefault(item => item.Code == via.NetCode);
            obstacles.Add(new CopperObstacle(
                "via",
                via.Id,
                via.NetCode,
                net?.Name,
                new RoutingPoint(via.XMillimeters.Value, via.YMillimeters.Value),
                null,
                (via.SizeMillimeters ?? 0.8) / 2));
        }

        return obstacles;
    }

    private static bool PadTouchesLayer(KiCadPad pad, string layer)
    {
        return pad.Layers.Any(item =>
            item == layer
            || item == "*.Cu"
            || item.Equals("\"*.Cu\"", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ViaTouchesLayer(KiCadVia via, string layer)
    {
        return via.Layers.Any(item => item == layer);
    }

    private static double SegmentToSegmentDistance(RoutingPoint a1, RoutingPoint a2, RoutingPoint b1, RoutingPoint b2)
    {
        if (SegmentsIntersect(a1, a2, b1, b2))
        {
            return 0;
        }

        return new[]
        {
            PointToSegmentDistance(a1, b1, b2),
            PointToSegmentDistance(a2, b1, b2),
            PointToSegmentDistance(b1, a1, a2),
            PointToSegmentDistance(b2, a1, a2)
        }.Min();
    }

    private static double PointToSegmentDistance(RoutingPoint point, RoutingPoint start, RoutingPoint end)
    {
        var dx = end.XMillimeters - start.XMillimeters;
        var dy = end.YMillimeters - start.YMillimeters;
        if (dx == 0 && dy == 0)
        {
            return Distance(point.XMillimeters, point.YMillimeters, start.XMillimeters, start.YMillimeters);
        }

        var t = ((point.XMillimeters - start.XMillimeters) * dx + (point.YMillimeters - start.YMillimeters) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        return Distance(point.XMillimeters, point.YMillimeters, start.XMillimeters + t * dx, start.YMillimeters + t * dy);
    }

    private static bool SegmentsIntersect(RoutingPoint a1, RoutingPoint a2, RoutingPoint b1, RoutingPoint b2)
    {
        static double Direction(RoutingPoint a, RoutingPoint b, RoutingPoint c)
            => (c.XMillimeters - a.XMillimeters) * (b.YMillimeters - a.YMillimeters)
                - (b.XMillimeters - a.XMillimeters) * (c.YMillimeters - a.YMillimeters);

        static bool OnSegment(RoutingPoint a, RoutingPoint b, RoutingPoint c)
            => Math.Min(a.XMillimeters, b.XMillimeters) - 0.000001 <= c.XMillimeters
                && c.XMillimeters <= Math.Max(a.XMillimeters, b.XMillimeters) + 0.000001
                && Math.Min(a.YMillimeters, b.YMillimeters) - 0.000001 <= c.YMillimeters
                && c.YMillimeters <= Math.Max(a.YMillimeters, b.YMillimeters) + 0.000001;

        var d1 = Direction(a1, a2, b1);
        var d2 = Direction(a1, a2, b2);
        var d3 = Direction(b1, b2, a1);
        var d4 = Direction(b1, b2, a2);

        return (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            || (Math.Abs(d1) < 0.000001 && OnSegment(a1, a2, b1))
            || (Math.Abs(d2) < 0.000001 && OnSegment(a1, a2, b2))
            || (Math.Abs(d3) < 0.000001 && OnSegment(b1, b2, a1))
            || (Math.Abs(d4) < 0.000001 && OnSegment(b1, b2, a2));
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string PointKey(double x, double y)
    {
        return $"{Math.Round(x, 4).ToString(CultureInfo.InvariantCulture)},{Math.Round(y, 4).ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatDistance(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> ExtractUuids(string text)
    {
        return Regex.Matches(text, @"\(uuid\s+""?(?<uuid>[0-9a-fA-F-]+)""?\)")
            .Select(static match => match.Groups["uuid"].Value)
            .ToArray();
    }

    private static TrackSummary ToTrackSummary(KiCadBoardDocument board, KiCadSegment segment)
    {
        var net = ResolveItemNet(board, segment.NetCode, segment.NetName);
        return new TrackSummary(
            segment.Id,
            segment.Uuid,
            segment.NetCode,
            segment.NetName ?? net?.Name,
            segment.StartXMillimeters,
            segment.StartYMillimeters,
            segment.EndXMillimeters,
            segment.EndYMillimeters,
            segment.Layer,
            segment.WidthMillimeters);
    }

    private static ViaSummary ToViaSummary(KiCadBoardDocument board, KiCadVia via)
    {
        var net = ResolveItemNet(board, via.NetCode, via.NetName);
        return new ViaSummary(
            via.Id,
            via.Uuid,
            via.NetCode,
            via.NetName ?? net?.Name,
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

    private static KiCadNet? ResolveItemNet(KiCadBoardDocument board, int? code, string? name)
    {
        return board.Nets.FirstOrDefault(net => NetReferenceMatches(code, name, net));
    }

    private static bool NetReferenceMatches(int? code, string? name, KiCadNet net)
    {
        return code == net.Code
            || (!string.IsNullOrWhiteSpace(name) && string.Equals(name, net.Name, StringComparison.OrdinalIgnoreCase));
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

public readonly record struct RoutingPoint(double XMillimeters, double YMillimeters);

public sealed record UnroutedConnectionListResult(
    string BoardFile,
    IReadOnlyList<UnroutedNetSummary> Nets);

public sealed record UnroutedNetSummary(
    RoutingNetSummary Net,
    IReadOnlyList<RoutedNetComponent> Components,
    IReadOnlyList<UnroutedConnectionSummary> MissingConnections);

public sealed record RoutedNetComponent(
    IReadOnlyList<RoutingPadEndpoint> Pads,
    IReadOnlyList<RoutingPoint> Points);

public sealed record RoutingPadEndpoint(
    string FootprintReference,
    string PadName,
    string? PinFunction,
    double XMillimeters,
    double YMillimeters);

public sealed record UnroutedConnectionSummary(
    RoutingPadEndpoint From,
    RoutingPadEndpoint To,
    double DistanceMillimeters);

public sealed record RoutingClearanceValidationResult(
    string BoardFile,
    RoutingNetSummary Net,
    string Layer,
    double WidthMillimeters,
    double ClearanceMillimeters,
    IReadOnlyList<RoutingPoint> Points,
    IReadOnlyList<RoutingClearanceViolation> Violations);

public sealed record RoutingClearanceViolation(
    string ObstacleKind,
    string ObstacleId,
    int? ObstacleNetCode,
    string? ObstacleNetName,
    double GapMillimeters,
    double RequiredClearanceMillimeters,
    string Message);

internal sealed record RoutingConnectivityNode(
    double XMillimeters,
    double YMillimeters,
    List<RoutingPadEndpoint> Pads);

internal sealed record CopperObstacle(
    string Kind,
    string Id,
    int? NetCode,
    string? NetName,
    RoutingPoint? Start,
    RoutingPoint? End,
    double RadiusMillimeters);

internal sealed class DisjointSet
{
    private readonly List<int> _parents = new();

    public void Add()
    {
        _parents.Add(_parents.Count);
    }

    public int Find(int value)
    {
        if (_parents[value] == value)
        {
            return value;
        }

        _parents[value] = Find(_parents[value]);
        return _parents[value];
    }

    public void Union(int left, int right)
    {
        var leftRoot = Find(left);
        var rightRoot = Find(right);
        if (leftRoot != rightRoot)
        {
            _parents[rightRoot] = leftRoot;
        }
    }
}
