using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed class SchematicPresentationService
{
    private const double Grid = 1.27;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ProjectDiscoveryService _projects;

    public SchematicPresentationService(ProjectDiscoveryService projects)
    {
        _projects = projects;
    }

    public ToolResponse<SchematicReadabilityReport> Analyze(string projectPath)
    {
        var loaded = Load(projectPath);
        if (!loaded.Success || loaded.Data is null)
            return ToolResponse<SchematicReadabilityReport>.Fail(
                loaded.Summary,
                loaded.Error?.Code ?? "SCHEMATIC_LOAD_FAILED",
                loaded.Error?.Message);

        var model = SchematicPresentationModel.Create(loaded.Data.Document, loaded.Data.Presentation);
        if (!model.Success || model.Data is null)
            return ToolResponse<SchematicReadabilityReport>.Fail(
                model.Summary,
                model.Error?.Code ?? "SCHEMATIC_PRESENTATION_UNSUPPORTED",
                model.Error?.Message);

        return ToolResponse<SchematicReadabilityReport>.Ok(
            "Analyzed schematic readability.",
            SchematicReadabilityAnalyzer.Analyze(model.Data, movedSymbolCount: 0, totalDisplacementMillimeters: 0));
    }

    public ToolResponse<SchematicPresentationMutationResult> Arrange(string projectPath, bool dryRun)
    {
        var loaded = Load(projectPath);
        if (!loaded.Success || loaded.Data is null)
            return ToolResponse<SchematicPresentationMutationResult>.Fail(
                loaded.Summary,
                loaded.Error?.Code ?? "SCHEMATIC_LOAD_FAILED",
                loaded.Error?.Message);

        if (loaded.Data.Document.TextBoxes.Count > 0)
            return ToolResponse<SchematicPresentationMutationResult>.Fail(
                "Schematic relayout cannot yet preserve the meaning of text boxes while moving their contents.",
                "SCHEMATIC_TEXT_BOX_RELAYOUT_UNSUPPORTED",
                $"Found {loaded.Data.Document.TextBoxes.Count} top-level text box(es); no project file was changed.");

        var beforeModel = SchematicPresentationModel.Create(
            loaded.Data.Document,
            loaded.Data.Presentation,
            requireRelayoutSafeTopLevel: true);
        if (!beforeModel.Success || beforeModel.Data is null)
            return ToolResponse<SchematicPresentationMutationResult>.Fail(
                beforeModel.Summary,
                beforeModel.Error?.Code ?? "SCHEMATIC_PRESENTATION_UNSUPPORTED",
                beforeModel.Error?.Message);

        var beforeReport = SchematicReadabilityAnalyzer.Analyze(beforeModel.Data, 0, 0);
        var planned = SchematicPresentationPlanner.Plan(beforeModel.Data);
        if (!planned.Success || planned.Data is null)
            return ToolResponse<SchematicPresentationMutationResult>.Fail(
                planned.Summary,
                planned.Error?.Code ?? "SCHEMATIC_LAYOUT_FAILED",
                planned.Error?.Message);

        var afterText = SchematicPresentationWriter.Rewrite(beforeModel.Data, planned.Data);
        var afterDocument = KiCadSchematicParser.ParseText(loaded.Data.Document.SchematicFile, afterText);
        var afterModel = SchematicPresentationModel.Create(
            afterDocument,
            loaded.Data.Presentation,
            requireRelayoutSafeTopLevel: true);
        if (!afterModel.Success || afterModel.Data is null)
            return ToolResponse<SchematicPresentationMutationResult>.Fail(
                "The planned schematic could not be parsed into the presentation model.",
                "SCHEMATIC_LAYOUT_INVALID",
                afterModel.Error?.Message);

        var beforeConnectivity = beforeModel.Data.ConnectivitySignature;
        var afterConnectivity = afterModel.Data.ConnectivitySignature;
        if (!string.Equals(beforeConnectivity, afterConnectivity, StringComparison.Ordinal))
        {
            return ToolResponse<SchematicPresentationMutationResult>.Fail(
                "The planned schematic did not preserve the canonical pin/net partition.",
                "SCHEMATIC_CONNECTIVITY_MISMATCH",
                $"Before: {beforeConnectivity} After: {afterConnectivity}");
        }

        var afterReport = SchematicReadabilityAnalyzer.Analyze(
            afterModel.Data,
            planned.Data.MovedSymbolCount,
            planned.Data.TotalDisplacementMillimeters);
        var certificate = new SchematicConnectivityCertificate(
            "pin-net-partition-v1",
            beforeConnectivity,
            afterConnectivity,
            true);

        if (!dryRun)
            File.WriteAllText(loaded.Data.Document.SchematicFile, afterText);

        var snapshot = new ChangeFileSnapshot(
            loaded.Data.Document.SchematicFile,
            loaded.Data.Document.Text,
            afterText);
        return ToolResponse<SchematicPresentationMutationResult>.Ok(
            $"{(dryRun ? "Previewed" : "Arranged")} schematic presentation; readability {beforeReport.CompositeScore:0.##} -> {afterReport.CompositeScore:0.##}.",
            new SchematicPresentationMutationResult(
                dryRun,
                beforeReport,
                afterReport,
                certificate,
                new[] { snapshot }),
            afterReport.CompositeScore < beforeReport.CompositeScore
                ? new[] { "The advisory readability score regressed; electrical connectivity was still preserved." }
                : Array.Empty<string>());
    }

    private ToolResponse<LoadedSchematicPresentation> Load(string projectPath)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<LoadedSchematicPresentation>.Fail(
                project.Summary,
                project.Error?.Code ?? "PROJECT_NOT_FOUND",
                project.Error?.Message);
        if (project.Data.SchematicFile is null || !File.Exists(project.Data.SchematicFile))
            return ToolResponse<LoadedSchematicPresentation>.Fail(
                "A schematic is required.",
                "SCHEMATIC_NOT_FOUND");

        DesignIntentPresentation? presentation = null;
        var intentPath = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "design-intent.json");
        if (File.Exists(intentPath))
        {
            try
            {
                presentation = JsonSerializer.Deserialize<DesignIntentDocument>(
                    File.ReadAllText(intentPath),
                    JsonOptions)?.Presentation;
            }
            catch (JsonException exception)
            {
                return ToolResponse<LoadedSchematicPresentation>.Fail(
                    "Design intent JSON is invalid.",
                    "DESIGN_INTENT_INVALID",
                    exception.Message);
            }
        }

        return ToolResponse<LoadedSchematicPresentation>.Ok(
            "Loaded schematic presentation input.",
            new LoadedSchematicPresentation(
                KiCadSchematicParser.Parse(project.Data.SchematicFile),
                presentation ?? new DesignIntentPresentation()));
    }

    private sealed record LoadedSchematicPresentation(
        KiCadSchematicDocument Document,
        DesignIntentPresentation Presentation);
}

internal sealed class SchematicPresentationModel
{
    private SchematicPresentationModel(
        KiCadSchematicDocument document,
        IReadOnlyList<SchematicPresentationSymbol> symbols,
        IReadOnlyList<SchematicPresentationNet> nets,
        DesignIntentPresentation presentation,
        string connectivitySignature)
    {
        Document = document;
        Symbols = symbols;
        Nets = nets;
        Presentation = presentation;
        ConnectivitySignature = connectivitySignature;
    }

    public KiCadSchematicDocument Document { get; }
    public IReadOnlyList<SchematicPresentationSymbol> Symbols { get; }
    public IReadOnlyList<SchematicPresentationNet> Nets { get; }
    public DesignIntentPresentation Presentation { get; }
    public string ConnectivitySignature { get; }

    public static ToolResponse<SchematicPresentationModel> Create(
        KiCadSchematicDocument document,
        DesignIntentPresentation presentation,
        bool requireRelayoutSafeTopLevel = false)
    {
        if (requireRelayoutSafeTopLevel)
        {
            var unsupportedKeyword = FindUnsupportedTopLevelKeyword(document.Text);
            if (unsupportedKeyword is not null)
            {
                return ToolResponse<SchematicPresentationModel>.Fail(
                    $"Schematic relayout does not support top-level {unsupportedKeyword} constructs.",
                    "SCHEMATIC_PRESENTATION_UNSUPPORTED");
            }
        }

        var logical = SchematicLogicalModel.Build(document);
        if (logical.UnknownSymbolReferences.Count > 0)
        {
            return ToolResponse<SchematicPresentationModel>.Fail(
                $"Schematic symbol is not in the approved pin-map catalog: {logical.UnknownSymbolReferences[0]}.",
                "SCHEMATIC_SYMBOL_UNSUPPORTED");
        }
        var conflictingPin = logical.Pins.FirstOrDefault(static pin => pin.HasNetConflict);
        if (conflictingPin is not null)
        {
            return ToolResponse<SchematicPresentationModel>.Fail(
                $"Pin {conflictingPin.Reference}.{conflictingPin.Pin} resolves to multiple net names.",
                "SCHEMATIC_NET_CONFLICT");
        }

        var symbols = new List<SchematicPresentationSymbol>();
        foreach (var symbol in document.Symbols.Where(static item => !string.IsNullOrWhiteSpace(item.Reference)))
        {
            if (symbol.LibId is null || symbol.XMillimeters is null || symbol.YMillimeters is null)
                return ToolResponse<SchematicPresentationModel>.Fail(
                    $"Schematic symbol {symbol.Reference} has incomplete placement data.",
                    "SCHEMATIC_PRESENTATION_UNSUPPORTED");
            var catalog = SchematicSymbolCatalog.Find(symbol.LibId);
            if (catalog is null)
                continue;

            var pins = catalog.Pins
                .Where(pin => pin.Unit == symbol.Unit)
                .Select(pin =>
                {
                    var point = SchematicGeometry.TransformPin(symbol, pin);
                    var logicalPin = logical.Pins.First(item =>
                        string.Equals(item.Reference, symbol.Reference, StringComparison.OrdinalIgnoreCase)
                        && item.Unit == symbol.Unit
                        && string.Equals(item.Pin, pin.Name, StringComparison.OrdinalIgnoreCase));
                    return new SchematicPresentationPin(
                        $"{symbol.Reference}.{pin.Name}",
                        pin.Name,
                        point.X,
                        point.Y,
                        point.DirectionX,
                        point.DirectionY,
                        logicalPin.Net);
                })
                .ToArray();
            var bounds = SchematicGeometry.Bounds(symbol, catalog);
            symbols.Add(new SchematicPresentationSymbol(
                symbol.Reference!,
                symbol.Unit,
                symbol.LibId,
                symbol.XMillimeters.Value,
                symbol.YMillimeters.Value,
                NormalizeRotation(symbol.RotationDegrees),
                bounds,
                pins,
                symbol));
        }

        foreach (var wire in document.Wires)
        {
            var midpointX = (wire.X1Millimeters + wire.X2Millimeters) / 2;
            var midpointY = (wire.Y1Millimeters + wire.Y2Millimeters) / 2;
            if (logical.Connectivity.NetNamesAtPoint(midpointX, midpointY).Count == 0)
            {
                return ToolResponse<SchematicPresentationModel>.Fail(
                    "Every routed wire island must have a net label before automatic relayout.",
                    "SCHEMATIC_NET_NAME_REQUIRED");
            }
        }

        var updatedSymbols = symbols.ToArray();
        var nets = symbols.SelectMany(static symbol => symbol.Pins)
            .Where(static pin => !string.IsNullOrWhiteSpace(pin.Net))
            .GroupBy(static pin => pin.Net!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SchematicPresentationNet(group.Key, group.OrderBy(static pin => pin.Key, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
        var signature = string.Join(
            "|",
            nets.Select(net => $"{net.Name}:{string.Join(",", net.Pins.Select(static pin => pin.Key).Order(StringComparer.OrdinalIgnoreCase))}"));

        var intentError = ValidatePresentationIntent(document, presentation, updatedSymbols);
        return intentError is null
            ? ToolResponse<SchematicPresentationModel>.Ok(
                "Built schematic presentation model.",
                new SchematicPresentationModel(document, updatedSymbols, nets, presentation, signature))
            : ToolResponse<SchematicPresentationModel>.Fail(intentError, "DESIGN_INTENT_INVALID");
    }

    private static string? FindUnsupportedTopLevelKeyword(string text)
    {
        foreach (var keyword in new[]
        {
            "sheet", "bus", "bus_entry", "hierarchical_label", "global_label",
            "no_connect", "text", "bitmap", "polyline", "rectangle"
        })
        {
            if (Regex.IsMatch(text, $@"(?m)^\s{{2}}\( {Regex.Escape(keyword)}\b".Replace("( ", "(")))
                return keyword;
        }

        return null;
    }

    private static string? ValidatePresentationIntent(
        KiCadSchematicDocument document,
        DesignIntentPresentation presentation,
        IReadOnlyList<SchematicPresentationSymbol> symbols)
    {
        var known = symbols.Select(static item => item.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in presentation.Blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Id) || !ids.Add(block.Id))
                return "Presentation blocks require unique non-empty ids.";
            foreach (var reference in block.References)
            {
                if (!known.Contains(reference))
                    return $"Presentation block {block.Id} references unknown symbol {reference}.";
                if (!assigned.Add(reference))
                    return $"Schematic symbol {reference} belongs to more than one presentation block.";
            }
            if (block.TextBoxUuid is not null
                && !document.TextBoxes.Any(box => string.Equals(box.Uuid, block.TextBoxUuid, StringComparison.OrdinalIgnoreCase)))
                return $"Presentation block {block.Id} references unknown text box {block.TextBoxUuid}.";
        }

        var duplicateBox = presentation.Blocks
            .Where(static block => !string.IsNullOrWhiteSpace(block.TextBoxUuid))
            .GroupBy(static block => block.TextBoxUuid!, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateBox is not null)
            return $"Text box {duplicateBox.Key} belongs to more than one presentation block.";

        var unknownLock = presentation.LockedReferences.FirstOrDefault(reference => !known.Contains(reference));
        return unknownLock is null ? null : $"Presentation lock references unknown symbol {unknownLock}.";
    }

    private static double NormalizeRotation(double? rotation)
    {
        var normalized = ((rotation ?? 0) % 360 + 360) % 360;
        return Math.Round(normalized / 90) * 90 % 360;
    }
}

internal sealed class SchematicLogicalModel
{
    private SchematicLogicalModel(
        IReadOnlyList<SchematicLogicalPin> pins,
        IReadOnlyList<string> unknownSymbolReferences,
        SchematicConnectivity connectivity)
    {
        Pins = pins;
        UnknownSymbolReferences = unknownSymbolReferences;
        Connectivity = connectivity;
    }

    public IReadOnlyList<SchematicLogicalPin> Pins { get; }
    public IReadOnlyList<string> UnknownSymbolReferences { get; }
    public SchematicConnectivity Connectivity { get; }

    public static SchematicLogicalModel Build(KiCadSchematicDocument document)
    {
        var connectivity = SchematicConnectivity.Build(document);
        var pins = new List<SchematicLogicalPin>();
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in document.Symbols
                     .Where(static symbol => !string.IsNullOrWhiteSpace(symbol.Reference))
                     .GroupBy(static symbol => symbol.Reference!, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var catalog = first.LibId is null ? null : SchematicSymbolCatalog.Find(first.LibId);
            if (catalog is null)
            {
                unknown.Add(first.Reference!);
                continue;
            }

            foreach (var definition in catalog.Pins)
            {
                var symbol = group.FirstOrDefault(item => item.Unit == definition.Unit);
                if (symbol?.XMillimeters is null || symbol.YMillimeters is null)
                    continue;
                var point = SchematicGeometry.TransformPin(symbol, definition);
                var names = connectivity.NetNamesAtPoint(point.X, point.Y)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                pins.Add(new SchematicLogicalPin(
                    first.Reference!,
                    symbol.Unit,
                    first.LibId!,
                    definition.Name,
                    point.X,
                    point.Y,
                    names.Length == 1 ? names[0] : null,
                    names.Length > 1));
            }
        }

        return new SchematicLogicalModel(
            pins,
            unknown.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            connectivity);
    }
}

internal static class SchematicPresentationPlanner
{
    private const double Grid = 1.27;
    private const int ColumnPitch = 24;
    private const int RowPitch = 14;

    public static ToolResponse<SchematicPresentationPlan> Plan(SchematicPresentationModel model)
    {
        if (model.Symbols.Count == 0)
            return ToolResponse<SchematicPresentationPlan>.Fail(
                "Schematic contains no placed symbols.",
                "SCHEMATIC_LAYOUT_EMPTY");

        var locked = model.Presentation.LockedReferences.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockOrder = BuildBlockOrder(model);
        var placements = new List<SchematicPlannedSymbol>();
        var rowByColumn = new Dictionary<int, int>();
        var baseX = 30.48;
        var baseY = 35.56;

        foreach (var symbol in model.Symbols
                     .OrderBy(symbol => blockOrder.GetValueOrDefault(symbol.Reference))
                     .ThenBy(static symbol => symbol.Reference, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static symbol => symbol.Unit))
        {
            if (locked.Contains(symbol.Reference))
            {
                placements.Add(new SchematicPlannedSymbol(
                    symbol.Reference,
                    symbol.Unit,
                    symbol.X,
                    symbol.Y,
                    symbol.RotationDegrees));
                continue;
            }

            var column = blockOrder.GetValueOrDefault(symbol.Reference);
            var row = rowByColumn.GetValueOrDefault(column);
            rowByColumn[column] = row + 1;
            var rotation = PreferredRotation(symbol);
            placements.Add(new SchematicPlannedSymbol(
                symbol.Reference,
                symbol.Unit,
                Snap(baseX + (column * ColumnPitch * Grid)),
                Snap(baseY + (row * RowPitch * Grid)),
                rotation));
        }

        var placedSymbols = ApplyPlacements(model.Symbols, placements);
        var routed = SchematicOrthogonalRouter.Route(model.Nets, placedSymbols, model.Presentation);
        if (!routed.Success || routed.Data is null)
            return ToolResponse<SchematicPresentationPlan>.Fail(
                routed.Summary,
                routed.Error?.Code ?? "SCHEMATIC_ROUTE_FAILED",
                routed.Error?.Message);

        var moved = 0;
        var displacement = 0.0;
        foreach (var placement in placements)
        {
            var original = model.Symbols.Single(symbol =>
                string.Equals(symbol.Reference, placement.Reference, StringComparison.OrdinalIgnoreCase)
                && symbol.Unit == placement.Unit);
            var distance = Math.Sqrt(Math.Pow(placement.X - original.X, 2) + Math.Pow(placement.Y - original.Y, 2));
            if (distance > 0.001 || Math.Abs(placement.RotationDegrees - original.RotationDegrees) > 0.001)
                moved++;
            displacement += distance;
        }

        return ToolResponse<SchematicPresentationPlan>.Ok(
            "Planned deterministic schematic presentation.",
            new SchematicPresentationPlan(
                placements,
                routed.Data.Wires,
                routed.Data.Labels,
                moved,
                displacement));
    }

    private static IReadOnlyDictionary<string, int> BuildBlockOrder(SchematicPresentationModel model)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in model.Presentation.Blocks.OrderBy(static item => item.Order).ThenBy(static item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var reference in block.References)
                result[reference] = Math.Max(0, block.Order);
        }

        var unassigned = model.Symbols.Select(static item => item.Reference)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(reference => !result.ContainsKey(reference))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unassigned.Length == 0)
            return result;

        var adjacency = unassigned.ToDictionary(
            static reference => reference,
            static _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        foreach (var net in model.Nets)
        {
            var references = net.Pins.Select(static pin => pin.Key.Split('.')[0])
                .Where(adjacency.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var first in references)
            foreach (var second in references)
                if (!string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
                    adjacency[first].Add(second);
        }

        var source = unassigned
            .OrderBy(reference => SourcePriority(model.Symbols.First(symbol =>
                string.Equals(symbol.Reference, reference, StringComparison.OrdinalIgnoreCase))))
            .ThenBy(static reference => reference, StringComparer.OrdinalIgnoreCase)
            .First();
        var queue = new Queue<string>();
        queue.Enqueue(source);
        result[source] = result.Count == 0 ? 0 : result.Values.Max() + 1;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in adjacency[current].Order(StringComparer.OrdinalIgnoreCase))
            {
                var rank = result[current] + 1;
                if (result.TryGetValue(next, out var existing) && existing <= rank)
                    continue;
                result[next] = rank;
                queue.Enqueue(next);
            }
        }

        var nextRank = result.Count == 0 ? 0 : result.Values.Max() + 1;
        foreach (var reference in unassigned.Where(reference => !result.ContainsKey(reference)))
            result[reference] = nextRank++;
        return result;
    }

    private static int SourcePriority(SchematicPresentationSymbol symbol)
    {
        if (symbol.SymbolId.StartsWith("Connector_", StringComparison.OrdinalIgnoreCase)
            || symbol.SymbolId.Contains("Battery", StringComparison.OrdinalIgnoreCase)
            || symbol.SymbolId.Contains("Photo", StringComparison.OrdinalIgnoreCase))
            return 0;
        return symbol.Pins.Count(pin => pin.Net is not null);
    }

    private static double PreferredRotation(SchematicPresentationSymbol symbol)
    {
        if (symbol.Pins.Count == 2)
        {
            var first = symbol.Pins[0];
            var second = symbol.Pins[1];
            if (Math.Abs(first.X - second.X) < 0.001)
                return 90;
        }

        return symbol.RotationDegrees;
    }

    private static IReadOnlyList<SchematicPresentationSymbol> ApplyPlacements(
        IReadOnlyList<SchematicPresentationSymbol> symbols,
        IReadOnlyList<SchematicPlannedSymbol> placements)
    {
        return symbols.Select(symbol =>
        {
            var placement = placements.Single(item =>
                string.Equals(item.Reference, symbol.Reference, StringComparison.OrdinalIgnoreCase)
                && item.Unit == symbol.Unit);
            var catalog = SchematicSymbolCatalog.Find(symbol.SymbolId)!;
            var placedDocumentSymbol = symbol.Source with
            {
                XMillimeters = placement.X,
                YMillimeters = placement.Y,
                RotationDegrees = placement.RotationDegrees
            };
            var pins = catalog.Pins.Where(pin => pin.Unit == symbol.Unit).Select(pin =>
            {
                var point = SchematicGeometry.TransformPin(placedDocumentSymbol, pin);
                var original = symbol.Pins.Single(item => string.Equals(item.Pin, pin.Name, StringComparison.OrdinalIgnoreCase));
                return original with
                {
                    X = point.X,
                    Y = point.Y,
                    DirectionX = point.DirectionX,
                    DirectionY = point.DirectionY
                };
            }).ToArray();
            return symbol with
            {
                X = placement.X,
                Y = placement.Y,
                RotationDegrees = placement.RotationDegrees,
                Bounds = SchematicGeometry.Bounds(placedDocumentSymbol, catalog),
                Pins = pins,
                Source = placedDocumentSymbol
            };
        }).ToArray();
    }

    private static double Snap(double value) =>
        Math.Round(value / Grid, MidpointRounding.AwayFromZero) * Grid;
}

internal static class SchematicOrthogonalRouter
{
    private const double Grid = 1.27;

    public static ToolResponse<SchematicRoutingPlan> Route(
        IReadOnlyList<SchematicPresentationNet> originalNets,
        IReadOnlyList<SchematicPresentationSymbol> symbols,
        DesignIntentPresentation presentation)
    {
        var pinsByKey = symbols.SelectMany(static symbol => symbol.Pins).ToDictionary(static pin => pin.Key, StringComparer.OrdinalIgnoreCase);
        var nets = originalNets.Select(net => new SchematicPresentationNet(
            net.Name,
            net.Pins.Select(pin => pinsByKey[pin.Key] with { Net = net.Name }).ToArray())).ToArray();
        var obstacles = BuildObstacles(symbols);
        var wires = new List<SchematicPlannedWire>();
        var labels = new List<SchematicPlannedLabel>();
        var occupied = new List<SchematicPlannedWire>();
        var feedback = presentation.FeedbackNets.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var net in nets.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (net.Pins.Count == 0)
                continue;
            var useLabels = net.Pins.Count == 1 || IsGlobalNet(net.Name) || net.Pins.Count > 4;
            if (useLabels)
            {
                foreach (var pin in net.Pins.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var end = (
                        X: Snap(pin.X + (pin.DirectionX * Grid)),
                        Y: Snap(pin.Y + (pin.DirectionY * Grid)));
                    var wire = new SchematicPlannedWire(net.Name, pin.X, pin.Y, end.X, end.Y);
                    wires.Add(wire);
                    occupied.Add(wire);
                    labels.Add(new SchematicPlannedLabel(net.Name, end.X, end.Y));
                }
                continue;
            }

            var connected = new HashSet<(int X, int Y)> { ToGrid(net.Pins[0].X, net.Pins[0].Y) };
            foreach (var pin in net.Pins.Skip(1).OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var start = ToGrid(pin.X, pin.Y);
                var path = FindPath(
                    start,
                    connected,
                    obstacles,
                    occupied,
                    net.Name,
                    preferFeedback: feedback.Contains(net.Name));
                if (path is null)
                    return ToolResponse<SchematicRoutingPlan>.Fail(
                        $"Could not route schematic net {net.Name}.",
                        "SCHEMATIC_ROUTE_FAILED");
                foreach (var point in path)
                    connected.Add(point);
                foreach (var segment in Compress(path).Zip(Compress(path).Skip(1)))
                {
                    var from = FromGrid(segment.First);
                    var to = FromGrid(segment.Second);
                    var wire = new SchematicPlannedWire(net.Name, from.X, from.Y, to.X, to.Y);
                    if (!SamePoint(wire.X1, wire.Y1, wire.X2, wire.Y2))
                    {
                        wires.Add(wire);
                        occupied.Add(wire);
                    }
                }
            }
            var firstWire = wires.FirstOrDefault(wire => string.Equals(wire.Net, net.Name, StringComparison.OrdinalIgnoreCase));
            if (firstWire is not null)
                labels.Add(new SchematicPlannedLabel(net.Name, firstWire.X1, firstWire.Y1));
        }

        var distinctWires = wires
            .GroupBy(NormalizedSegmentKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static wire => wire.Net, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static wire => Math.Min(wire.X1, wire.X2))
            .ThenBy(static wire => Math.Min(wire.Y1, wire.Y2))
            .ToArray();
        var distinctLabels = labels
            .DistinctBy(static label => $"{label.Net}|{label.X:0.###}|{label.Y:0.###}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(static label => label.Net, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static label => label.X)
            .ThenBy(static label => label.Y)
            .ToArray();
        return ToolResponse<SchematicRoutingPlan>.Ok(
            "Routed schematic nets.",
            new SchematicRoutingPlan(distinctWires, distinctLabels));
    }

    private static IReadOnlySet<(int X, int Y)> BuildObstacles(IReadOnlyList<SchematicPresentationSymbol> symbols)
    {
        var result = new HashSet<(int X, int Y)>();
        foreach (var symbol in symbols)
        {
            var min = ToGrid(symbol.Bounds.Left + Grid, symbol.Bounds.Top + Grid);
            var max = ToGrid(symbol.Bounds.Right - Grid, symbol.Bounds.Bottom - Grid);
            for (var x = Math.Min(min.X, max.X); x <= Math.Max(min.X, max.X); x++)
            for (var y = Math.Min(min.Y, max.Y); y <= Math.Max(min.Y, max.Y); y++)
                result.Add((x, y));
        }
        foreach (var pin in symbols.SelectMany(static symbol => symbol.Pins))
            result.Add(ToGrid(pin.X, pin.Y));
        return result;
    }

    private static IReadOnlyList<(int X, int Y)>? FindPath(
        (int X, int Y) start,
        IReadOnlySet<(int X, int Y)> targets,
        IReadOnlySet<(int X, int Y)> obstacles,
        IReadOnlyList<SchematicPlannedWire> occupied,
        string net,
        bool preferFeedback)
    {
        var targetList = targets.ToArray();
        var minX = Math.Min(start.X, targetList.Min(static point => point.X)) - 30;
        var maxX = Math.Max(start.X, targetList.Max(static point => point.X)) + 30;
        var minY = Math.Min(start.Y, targetList.Min(static point => point.Y)) - 30;
        var maxY = Math.Max(start.Y, targetList.Max(static point => point.Y)) + 30;
        var queue = new PriorityQueue<RouteState, (int Cost, int Tie)>();
        var best = new Dictionary<RouteState, int>();
        var previous = new Dictionary<RouteState, RouteState>();
        var tie = 0;
        var initial = new RouteState(start.X, start.Y, 0, 0, false);
        best[initial] = 0;
        queue.Enqueue(initial, (0, tie++));

        RouteState? found = null;
        while (queue.TryDequeue(out var current, out _))
        {
            if (targets.Contains((current.X, current.Y)))
            {
                found = current;
                break;
            }
            var currentCost = best[current];
            foreach (var direction in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                if (current.MustContinueStraight
                    && (current.DirectionX != direction.Item1 || current.DirectionY != direction.Item2))
                    continue;
                var nextPoint = (X: current.X + direction.Item1, Y: current.Y + direction.Item2);
                if (nextPoint.X < minX || nextPoint.X > maxX || nextPoint.Y < minY || nextPoint.Y > maxY)
                    continue;
                if (obstacles.Contains(nextPoint) && !targets.Contains(nextPoint))
                    continue;

                var bend = (current.DirectionX != 0 || current.DirectionY != 0)
                    && (current.DirectionX != direction.Item1 || current.DirectionY != direction.Item2);
                var segmentStart = FromGrid((current.X, current.Y));
                var segmentEnd = FromGrid(nextPoint);
                var blocked = false;
                var mustContinueStraight = false;
                foreach (var wire in occupied.Where(wire =>
                             !string.Equals(wire.Net, net, StringComparison.OrdinalIgnoreCase)))
                {
                    var interaction = ClassifyInteraction(
                        segmentStart.X, segmentStart.Y, segmentEnd.X, segmentEnd.Y,
                        wire.X1, wire.Y1, wire.X2, wire.Y2);
                    if (interaction == SegmentInteraction.None)
                        continue;
                    if (interaction == SegmentInteraction.SharedConductorOrForeignEndpoint
                        || (interaction == SegmentInteraction.CrossingAtStart && !current.MustContinueStraight)
                        || (interaction == SegmentInteraction.CrossingAtEnd && targets.Contains(nextPoint)))
                    {
                        blocked = true;
                        break;
                    }
                    if (interaction == SegmentInteraction.CrossingAtEnd)
                        mustContinueStraight = true;
                }
                if (blocked)
                    continue;
                var crossings = occupied.Count(wire => SegmentsCross(
                    segmentStart.X, segmentStart.Y, segmentEnd.X, segmentEnd.Y,
                    wire.X1, wire.Y1, wire.X2, wire.Y2));
                var reversePenalty = !preferFeedback && direction.Item1 < 0 ? 2 : 0;
                var cost = currentCost + 10 + (bend ? 35 : 0) + (crossings * 250) + reversePenalty;
                var next = new RouteState(nextPoint.X, nextPoint.Y, direction.Item1, direction.Item2, mustContinueStraight);
                if (best.TryGetValue(next, out var known) && known <= cost)
                    continue;
                best[next] = cost;
                previous[next] = current;
                var heuristic = targetList.Min(target => Math.Abs(target.X - next.X) + Math.Abs(target.Y - next.Y)) * 10;
                queue.Enqueue(next, (cost + heuristic, tie++));
            }
        }

        if (found is null)
            return null;
        var path = new List<(int X, int Y)> { (found.Value.X, found.Value.Y) };
        var cursor = found.Value;
        while (previous.TryGetValue(cursor, out var prior))
        {
            cursor = prior;
            path.Add((cursor.X, cursor.Y));
        }
        path.Reverse();
        return path;
    }

    private static IReadOnlyList<(int X, int Y)> Compress(IReadOnlyList<(int X, int Y)> path)
    {
        if (path.Count < 3)
            return path;
        var result = new List<(int X, int Y)> { path[0] };
        for (var index = 1; index < path.Count - 1; index++)
        {
            var previous = result[^1];
            var current = path[index];
            var next = path[index + 1];
            if ((previous.X == current.X && current.X == next.X)
                || (previous.Y == current.Y && current.Y == next.Y))
                continue;
            result.Add(current);
        }
        result.Add(path[^1]);
        return result;
    }

    private static bool IsGlobalNet(string net) =>
        net.Equals("GND", StringComparison.OrdinalIgnoreCase)
        || net.StartsWith("+", StringComparison.Ordinal)
        || net.Contains("VCC", StringComparison.OrdinalIgnoreCase)
        || net.Contains("VDD", StringComparison.OrdinalIgnoreCase);

    private static string NormalizedSegmentKey(SchematicPlannedWire wire)
    {
        var first = $"{wire.X1:0.###},{wire.Y1:0.###}";
        var second = $"{wire.X2:0.###},{wire.Y2:0.###}";
        return string.Compare(first, second, StringComparison.Ordinal) <= 0
            ? $"{wire.Net}|{first}|{second}"
            : $"{wire.Net}|{second}|{first}";
    }

    internal static bool SegmentsCross(
        double x1, double y1, double x2, double y2,
        double x3, double y3, double x4, double y4)
    {
        var firstHorizontal = Math.Abs(y1 - y2) < 0.001;
        var secondHorizontal = Math.Abs(y3 - y4) < 0.001;
        if (firstHorizontal == secondHorizontal)
            return false;
        var hx1 = firstHorizontal ? x1 : x3;
        var hx2 = firstHorizontal ? x2 : x4;
        var hy = firstHorizontal ? y1 : y3;
        var vx = firstHorizontal ? x3 : x1;
        var vy1 = firstHorizontal ? y3 : y1;
        var vy2 = firstHorizontal ? y4 : y2;
        return vx > Math.Min(hx1, hx2) + 0.001
            && vx < Math.Max(hx1, hx2) - 0.001
            && hy > Math.Min(vy1, vy2) + 0.001
            && hy < Math.Max(vy1, vy2) - 0.001;
    }

    private static SegmentInteraction ClassifyInteraction(
        double x1, double y1, double x2, double y2,
        double x3, double y3, double x4, double y4)
    {
        var firstHorizontal = Math.Abs(y1 - y2) < 0.001;
        var secondHorizontal = Math.Abs(y3 - y4) < 0.001;
        if (firstHorizontal && secondHorizontal)
            return Math.Abs(y1 - y3) < 0.001
                && Math.Max(Math.Min(x1, x2), Math.Min(x3, x4))
                <= Math.Min(Math.Max(x1, x2), Math.Max(x3, x4)) + 0.001
                    ? SegmentInteraction.SharedConductorOrForeignEndpoint
                    : SegmentInteraction.None;
        if (!firstHorizontal && !secondHorizontal)
            return Math.Abs(x1 - x3) < 0.001
                && Math.Max(Math.Min(y1, y2), Math.Min(y3, y4))
                <= Math.Min(Math.Max(y1, y2), Math.Max(y3, y4)) + 0.001
                    ? SegmentInteraction.SharedConductorOrForeignEndpoint
                    : SegmentInteraction.None;

        var horizontalX1 = firstHorizontal ? x1 : x3;
        var horizontalX2 = firstHorizontal ? x2 : x4;
        var horizontalY = firstHorizontal ? y1 : y3;
        var verticalX = firstHorizontal ? x3 : x1;
        var verticalY1 = firstHorizontal ? y3 : y1;
        var verticalY2 = firstHorizontal ? y4 : y2;
        if (verticalX < Math.Min(horizontalX1, horizontalX2) - 0.001
            || verticalX > Math.Max(horizontalX1, horizontalX2) + 0.001
            || horizontalY < Math.Min(verticalY1, verticalY2) - 0.001
            || horizontalY > Math.Max(verticalY1, verticalY2) + 0.001)
            return SegmentInteraction.None;
        if (SamePoint(verticalX, horizontalY, x3, y3)
            || SamePoint(verticalX, horizontalY, x4, y4))
            return SegmentInteraction.SharedConductorOrForeignEndpoint;
        if (SamePoint(verticalX, horizontalY, x1, y1))
            return SegmentInteraction.CrossingAtStart;
        return SegmentInteraction.CrossingAtEnd;
    }

    private enum SegmentInteraction
    {
        None,
        CrossingAtStart,
        CrossingAtEnd,
        SharedConductorOrForeignEndpoint
    }

    private static (int X, int Y) ToGrid(double x, double y) =>
        ((int)Math.Round(x / Grid), (int)Math.Round(y / Grid));
    private static (double X, double Y) FromGrid((int X, int Y) point) =>
        (Snap(point.X * Grid), Snap(point.Y * Grid));
    private static double Snap(double value) =>
        Math.Round(value / Grid, MidpointRounding.AwayFromZero) * Grid;
    private static bool SamePoint(double x1, double y1, double x2, double y2) =>
        Math.Abs(x1 - x2) < 0.001 && Math.Abs(y1 - y2) < 0.001;
    private readonly record struct RouteState(int X, int Y, int DirectionX, int DirectionY, bool MustContinueStraight);
}

internal static class SchematicPresentationWriter
{
    public static string Rewrite(SchematicPresentationModel model, SchematicPresentationPlan plan)
    {
        var text = model.Document.Text;
        foreach (var symbol in model.Symbols.OrderByDescending(static item => item.Source.SourceStart))
        {
            var placement = plan.Symbols.Single(item =>
                string.Equals(item.Reference, symbol.Reference, StringComparison.OrdinalIgnoreCase)
                && item.Unit == symbol.Unit);
            var block = text.Substring(symbol.Source.SourceStart, symbol.Source.SourceLength);
            var rewritten = RewriteSymbolBlock(block, placement);
            text = text.Remove(symbol.Source.SourceStart, symbol.Source.SourceLength)
                .Insert(symbol.Source.SourceStart, rewritten);
        }

        text = RemoveBlocks(text, "wire", "label", "junction");
        var insertAt = FindRootClosingIndex(text);
        var additions = new StringBuilder();
        var seed = model.ConnectivitySignature;
        for (var index = 0; index < plan.Wires.Count; index++)
            additions.Append(FormatWire(plan.Wires[index], DeterministicUuid(seed, $"wire:{index}:{plan.Wires[index]}")));
        for (var index = 0; index < plan.Labels.Count; index++)
            additions.Append(FormatLabel(plan.Labels[index], DeterministicUuid(seed, $"label:{index}:{plan.Labels[index]}")));
        return text.Insert(insertAt, additions.ToString());
    }

    private static string RewriteSymbolBlock(string block, SchematicPlannedSymbol placement)
    {
        var at = Regex.Match(block, @"\(at\s+-?\d+(?:\.\d+)?\s+-?\d+(?:\.\d+)?(?:\s+-?\d+(?:\.\d+)?)?\)");
        if (!at.Success)
            throw new InvalidOperationException($"Schematic symbol {placement.Reference} has no placement.");
        var result = block.Remove(at.Index, at.Length).Insert(
            at.Index,
            $"(at {Format(placement.X)} {Format(placement.Y)} {Format(placement.RotationDegrees)})");

        var propertyIndex = 0;
        var search = 0;
        while ((search = result.IndexOf("(property", search, StringComparison.Ordinal)) >= 0)
        {
            var end = KiCadSchematicParser.FindMatchingParenthesis(result, search);
            if (end < 0)
                break;
            var property = result.Substring(search, end - search + 1);
            var propertyAt = Regex.Match(property, @"\(at\s+-?\d+(?:\.\d+)?\s+-?\d+(?:\.\d+)?(?:\s+-?\d+(?:\.\d+)?)?\)");
            if (propertyAt.Success)
            {
                var offset = propertyIndex switch { 0 => -2.54, 1 => 2.54, _ => 5.08 + ((propertyIndex - 2) * 2.54) };
                var replacement = $"(at {Format(placement.X)} {Format(placement.Y + offset)} 0)";
                property = property.Remove(propertyAt.Index, propertyAt.Length).Insert(propertyAt.Index, replacement);
                result = result.Remove(search, end - search + 1).Insert(search, property);
                end = search + property.Length - 1;
            }
            propertyIndex++;
            search = end + 1;
        }
        return result;
    }

    private static string RemoveBlocks(string text, params string[] keywords)
    {
        var ranges = new List<(int Start, int Length)>();
        foreach (var keyword in keywords)
        {
            var search = 0;
            while ((search = text.IndexOf($"({keyword}", search, StringComparison.Ordinal)) >= 0)
            {
                var after = search + keyword.Length + 1;
                if (after < text.Length && !char.IsWhiteSpace(text[after]))
                {
                    search = after;
                    continue;
                }
                var end = KiCadSchematicParser.FindMatchingParenthesis(text, search);
                if (end < 0)
                    break;
                var start = search;
                while (start > 0 && (text[start - 1] == ' ' || text[start - 1] == '\t'))
                    start--;
                if (start > 0 && text[start - 1] == '\n')
                    start--;
                ranges.Add((start, end - start + 1));
                search = end + 1;
            }
        }
        foreach (var range in ranges.OrderByDescending(static item => item.Start))
            text = text.Remove(range.Start, range.Length);
        return text;
    }

    private static int FindRootClosingIndex(string text)
    {
        var start = text.IndexOf("(kicad_sch", StringComparison.Ordinal);
        var end = KiCadSchematicParser.FindMatchingParenthesis(text, start);
        if (start < 0 || end < 0)
            throw new InvalidOperationException("Schematic root block is malformed.");
        return end;
    }

    private static string FormatWire(SchematicPlannedWire wire, string uuid) =>
        $"  (wire{Environment.NewLine}" +
        $"    (pts (xy {Format(wire.X1)} {Format(wire.Y1)}) (xy {Format(wire.X2)} {Format(wire.Y2)})){Environment.NewLine}" +
        $"    (stroke (width 0) (type default)){Environment.NewLine}" +
        $"    (uuid \"{uuid}\"){Environment.NewLine}" +
        $"  ){Environment.NewLine}";

    private static string FormatLabel(SchematicPlannedLabel label, string uuid) =>
        $"  (label \"{label.Net.Replace("\"", "\\\"", StringComparison.Ordinal)}\"{Environment.NewLine}" +
        $"    (at {Format(label.X)} {Format(label.Y)} 0){Environment.NewLine}" +
        $"    (effects (font (size 1.27 1.27))){Environment.NewLine}" +
        $"    (uuid \"{uuid}\"){Environment.NewLine}" +
        $"  ){Environment.NewLine}";

    private static string DeterministicUuid(string seed, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed + "|" + value));
        Span<byte> guid = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guid);
        guid[6] = (byte)((guid[6] & 0x0f) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
        return new Guid(guid).ToString();
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}

internal static class SchematicReadabilityAnalyzer
{
    public static SchematicReadabilityReport Analyze(
        SchematicPresentationModel model,
        int movedSymbolCount,
        double totalDisplacementMillimeters)
    {
        var crossings = 0;
        for (var first = 0; first < model.Document.Wires.Count; first++)
        for (var second = first + 1; second < model.Document.Wires.Count; second++)
        {
            var a = model.Document.Wires[first];
            var b = model.Document.Wires[second];
            if (SchematicOrthogonalRouter.SegmentsCross(
                    a.X1Millimeters, a.Y1Millimeters, a.X2Millimeters, a.Y2Millimeters,
                    b.X1Millimeters, b.Y1Millimeters, b.X2Millimeters, b.Y2Millimeters))
                crossings++;
        }

        var bends = CountBends(model.Document.Wires);
        var overlaps = 0;
        for (var first = 0; first < model.Symbols.Count; first++)
        for (var second = first + 1; second < model.Symbols.Count; second++)
            if (model.Symbols[first].Bounds.Intersects(model.Symbols[second].Bounds))
                overlaps++;
        var length = model.Document.Wires.Sum(static wire =>
            Math.Abs(wire.X2Millimeters - wire.X1Millimeters)
            + Math.Abs(wire.Y2Millimeters - wire.Y1Millimeters));
        var labelHops = model.Document.Labels
            .GroupBy(static label => label.Text, StringComparer.OrdinalIgnoreCase)
            .Sum(static group => Math.Max(0, group.Count() - 1));
        var congestion = CountCongestion(model.Document.Wires);
        var blockDispersion = BlockDispersion(model);
        var textBoxOverlaps = CountTextBoxOverlaps(model.Document.TextBoxes);
        var boxBoundaryCrossings = CountTextBoxBoundaryCrossings(model.Document.Wires, model.Document.TextBoxes);
        var visualBoxDispersion = VisualBoxDispersion(model);
        var flowViolations = CountFlowViolations(model);
        var penalty = (crossings * 15.0)
            + bends
            + (overlaps * 25.0)
            + (congestion * 5.0)
            + (flowViolations * 5.0)
            + labelHops
            + (textBoxOverlaps * 10.0)
            + (boxBoundaryCrossings * 0.5)
            + Math.Min(10, blockDispersion / 5000.0)
            + Math.Min(5, visualBoxDispersion / 10000.0);
        var score = Math.Max(0, 100 - Math.Min(100, penalty));
        return new SchematicReadabilityReport(
            "readability-v2",
            score,
            crossings,
            bends,
            overlaps,
            textBoxOverlaps,
            congestion,
            flowViolations,
            blockDispersion,
            labelHops,
            length,
            movedSymbolCount,
            totalDisplacementMillimeters,
            overlaps == 0 && textBoxOverlaps == 0,
            model.Document.TextBoxes.Count,
            textBoxOverlaps,
            boxBoundaryCrossings,
            visualBoxDispersion);
    }

    private static int CountTextBoxOverlaps(IReadOnlyList<KiCadSchematicTextBox> boxes)
    {
        var count = 0;
        for (var first = 0; first < boxes.Count; first++)
        for (var second = first + 1; second < boxes.Count; second++)
            if (TextBoxBounds(boxes[first]).Intersects(TextBoxBounds(boxes[second])))
                count++;
        return count;
    }

    private static int CountTextBoxBoundaryCrossings(
        IReadOnlyList<KiCadSchematicWire> wires,
        IReadOnlyList<KiCadSchematicTextBox> boxes)
    {
        var count = 0;
        foreach (var wire in wires)
        foreach (var box in boxes)
        {
            var bounds = TextBoxBounds(box);
            var firstInside = Contains(bounds, wire.X1Millimeters, wire.Y1Millimeters);
            var secondInside = Contains(bounds, wire.X2Millimeters, wire.Y2Millimeters);
            if (firstInside != secondInside)
                count++;
            else if (!firstInside && SegmentPassesThrough(bounds, wire))
                count += 2;
        }
        return count;
    }

    private static double VisualBoxDispersion(SchematicPresentationModel model)
    {
        var total = 0.0;
        foreach (var box in model.Document.TextBoxes)
        {
            var bounds = TextBoxBounds(box);
            var contained = model.Symbols.Where(symbol => Contains(bounds, symbol.X, symbol.Y)).ToArray();
            if (contained.Length < 2)
                continue;
            total += (contained.Max(static symbol => symbol.Bounds.Right) - contained.Min(static symbol => symbol.Bounds.Left))
                * (contained.Max(static symbol => symbol.Bounds.Bottom) - contained.Min(static symbol => symbol.Bounds.Top));
        }
        return total;
    }

    private static bool SegmentPassesThrough(SchematicRectangle bounds, KiCadSchematicWire wire)
    {
        var horizontal = Math.Abs(wire.Y1Millimeters - wire.Y2Millimeters) < 0.001;
        if (horizontal)
            return wire.Y1Millimeters > bounds.Top && wire.Y1Millimeters < bounds.Bottom
                && Math.Min(wire.X1Millimeters, wire.X2Millimeters) < bounds.Left
                && Math.Max(wire.X1Millimeters, wire.X2Millimeters) > bounds.Right;
        var vertical = Math.Abs(wire.X1Millimeters - wire.X2Millimeters) < 0.001;
        return vertical
            && wire.X1Millimeters > bounds.Left && wire.X1Millimeters < bounds.Right
            && Math.Min(wire.Y1Millimeters, wire.Y2Millimeters) < bounds.Top
            && Math.Max(wire.Y1Millimeters, wire.Y2Millimeters) > bounds.Bottom;
    }

    private static bool Contains(SchematicRectangle bounds, double x, double y) =>
        x >= bounds.Left && x <= bounds.Right && y >= bounds.Top && y <= bounds.Bottom;

    private static SchematicRectangle TextBoxBounds(KiCadSchematicTextBox box) =>
        new(
            box.XMillimeters,
            box.YMillimeters,
            box.XMillimeters + box.WidthMillimeters,
            box.YMillimeters + box.HeightMillimeters);

    private static int CountBends(IReadOnlyList<KiCadSchematicWire> wires)
    {
        var bends = 0;
        for (var first = 0; first < wires.Count; first++)
        for (var second = first + 1; second < wires.Count; second++)
        {
            var a = wires[first];
            var b = wires[second];
            var sharesEndpoint = SamePoint(a.X1Millimeters, a.Y1Millimeters, b.X1Millimeters, b.Y1Millimeters)
                || SamePoint(a.X1Millimeters, a.Y1Millimeters, b.X2Millimeters, b.Y2Millimeters)
                || SamePoint(a.X2Millimeters, a.Y2Millimeters, b.X1Millimeters, b.Y1Millimeters)
                || SamePoint(a.X2Millimeters, a.Y2Millimeters, b.X2Millimeters, b.Y2Millimeters);
            var perpendicular = Math.Abs(a.Y1Millimeters - a.Y2Millimeters) < 0.001
                != Math.Abs(b.Y1Millimeters - b.Y2Millimeters) < 0.001;
            if (sharesEndpoint && perpendicular)
                bends++;
        }
        return bends;
    }

    private static int CountCongestion(IReadOnlyList<KiCadSchematicWire> wires)
    {
        var congestion = 0;
        for (var first = 0; first < wires.Count; first++)
        for (var second = first + 1; second < wires.Count; second++)
        {
            var a = wires[first];
            var b = wires[second];
            var bothHorizontal = Math.Abs(a.Y1Millimeters - a.Y2Millimeters) < 0.001
                && Math.Abs(b.Y1Millimeters - b.Y2Millimeters) < 0.001;
            var bothVertical = Math.Abs(a.X1Millimeters - a.X2Millimeters) < 0.001
                && Math.Abs(b.X1Millimeters - b.X2Millimeters) < 0.001;
            if (bothHorizontal && Math.Abs(a.Y1Millimeters - b.Y1Millimeters) <= 2.54
                && RangesOverlap(a.X1Millimeters, a.X2Millimeters, b.X1Millimeters, b.X2Millimeters))
                congestion++;
            if (bothVertical && Math.Abs(a.X1Millimeters - b.X1Millimeters) <= 2.54
                && RangesOverlap(a.Y1Millimeters, a.Y2Millimeters, b.Y1Millimeters, b.Y2Millimeters))
                congestion++;
        }
        return congestion;
    }

    private static double BlockDispersion(SchematicPresentationModel model)
    {
        var total = 0.0;
        foreach (var block in model.Presentation.Blocks)
        {
            var symbols = model.Symbols.Where(symbol => block.References.Contains(symbol.Reference, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (symbols.Length < 2)
                continue;
            total += (symbols.Max(static symbol => symbol.Bounds.Right) - symbols.Min(static symbol => symbol.Bounds.Left))
                * (symbols.Max(static symbol => symbol.Bounds.Bottom) - symbols.Min(static symbol => symbol.Bounds.Top));
        }
        return total;
    }

    private static int CountFlowViolations(SchematicPresentationModel model)
    {
        var order = model.Presentation.Blocks
            .SelectMany(block => block.References.Select(reference => (Reference: reference, block.Order)))
            .ToDictionary(static item => item.Reference, static item => item.Order, StringComparer.OrdinalIgnoreCase);
        var violations = 0;
        foreach (var net in model.Nets)
        {
            var symbols = net.Pins.Select(pin => model.Symbols.First(symbol =>
                symbol.Pins.Any(candidate => string.Equals(candidate.Key, pin.Key, StringComparison.OrdinalIgnoreCase))))
                .DistinctBy(static symbol => symbol.Reference, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (var first = 0; first < symbols.Length; first++)
            for (var second = first + 1; second < symbols.Length; second++)
            {
                if (!order.TryGetValue(symbols[first].Reference, out var firstOrder)
                    || !order.TryGetValue(symbols[second].Reference, out var secondOrder))
                    continue;
                if (firstOrder < secondOrder && symbols[first].X > symbols[second].X)
                    violations++;
                if (secondOrder < firstOrder && symbols[second].X > symbols[first].X)
                    violations++;
            }
        }
        return violations;
    }

    private static bool RangesOverlap(double a1, double a2, double b1, double b2) =>
        Math.Max(Math.Min(a1, a2), Math.Min(b1, b2)) < Math.Min(Math.Max(a1, a2), Math.Max(b1, b2)) - 0.001;
    private static bool SamePoint(double x1, double y1, double x2, double y2) =>
        Math.Abs(x1 - x2) < 0.001 && Math.Abs(y1 - y2) < 0.001;
}

internal static class SchematicGeometry
{
    private const double Grid = 1.27;

    public static SchematicPinPoint TransformPin(
        KiCadSchematicSymbol symbol,
        SchematicPinDefinition pin)
    {
        var localX = pin.OffsetX;
        var localY = -pin.OffsetY;
        var rotation = ((symbol.RotationDegrees ?? 0) % 360 + 360) % 360;
        var transformed = rotation switch
        {
            >= 45 and < 135 => (-localY, localX),
            >= 135 and < 225 => (-localX, -localY),
            >= 225 and < 315 => (localY, -localX),
            _ => (localX, localY)
        };
        var directionX = Math.Sign(transformed.Item1);
        var directionY = Math.Sign(transformed.Item2);
        if (Math.Abs(transformed.Item1) >= Math.Abs(transformed.Item2))
            directionY = 0;
        else
            directionX = 0;
        return new SchematicPinPoint(
            Snap((symbol.XMillimeters ?? 0) + transformed.Item1),
            Snap((symbol.YMillimeters ?? 0) + transformed.Item2),
            directionX,
            directionY);
    }

    public static SchematicRectangle Bounds(
        KiCadSchematicSymbol symbol,
        SchematicSymbolCatalogEntry catalog)
    {
        var pins = catalog.Pins.Where(pin => pin.Unit == symbol.Unit)
            .Select(pin => TransformPin(symbol, pin))
            .ToArray();
        var x = symbol.XMillimeters ?? 0;
        var y = symbol.YMillimeters ?? 0;
        var halfWidth = Math.Max(5.08, pins.Length == 0 ? 5.08 : pins.Max(pin => Math.Abs(pin.X - x)) - (2 * Grid));
        var halfHeight = Math.Max(5.08, pins.Length == 0 ? 5.08 : pins.Max(pin => Math.Abs(pin.Y - y)) - (2 * Grid));
        return new SchematicRectangle(x - halfWidth, y - halfHeight, x + halfWidth, y + halfHeight);
    }

    private static double Snap(double value) =>
        Math.Round(value / Grid, MidpointRounding.AwayFromZero) * Grid;
}

public sealed record SchematicReadabilityReport(
    string ScoreVersion,
    double CompositeScore,
    int WireCrossings,
    int WireBends,
    int SymbolOverlaps,
    int TextOverlaps,
    int CongestedSegmentPairs,
    int FlowViolations,
    double BlockDispersionSquareMillimeters,
    int LabelHops,
    double TotalWireLengthMillimeters,
    int MovedSymbolCount,
    double TotalDisplacementMillimeters,
    bool HardConstraintsSatisfied,
    int TextBoxCount,
    int TextBoxOverlaps,
    int WireTextBoxBoundaryCrossings,
    double VisualBoxDispersionSquareMillimeters);

public sealed record SchematicConnectivityCertificate(
    string Version,
    string BeforeSignature,
    string AfterSignature,
    bool Equivalent);

public sealed record SchematicPresentationMutationResult(
    bool DryRun,
    SchematicReadabilityReport Before,
    SchematicReadabilityReport After,
    SchematicConnectivityCertificate Connectivity,
    IReadOnlyList<ChangeFileSnapshot> FileSnapshots);

public sealed class DesignIntentPresentation
{
    public IReadOnlyList<DesignIntentPresentationBlock> Blocks { get; init; } = Array.Empty<DesignIntentPresentationBlock>();
    public IReadOnlyList<string> FeedbackNets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LockedReferences { get; init; } = Array.Empty<string>();
}

public sealed record DesignIntentPresentationBlock(
    string Id,
    string Label,
    IReadOnlyList<string> References,
    int Order,
    string? TextBoxUuid = null);

internal sealed record SchematicPresentationSymbol(
    string Reference,
    int Unit,
    string SymbolId,
    double X,
    double Y,
    double RotationDegrees,
    SchematicRectangle Bounds,
    IReadOnlyList<SchematicPresentationPin> Pins,
    KiCadSchematicSymbol Source);

internal sealed record SchematicLogicalPin(
    string Reference,
    int Unit,
    string SymbolId,
    string Pin,
    double X,
    double Y,
    string? Net,
    bool HasNetConflict);

internal sealed record SchematicPresentationPin(
    string Key,
    string Pin,
    double X,
    double Y,
    int DirectionX,
    int DirectionY,
    string? Net);

internal sealed record SchematicPresentationNet(
    string Name,
    IReadOnlyList<SchematicPresentationPin> Pins);

internal sealed record SchematicPresentationPlan(
    IReadOnlyList<SchematicPlannedSymbol> Symbols,
    IReadOnlyList<SchematicPlannedWire> Wires,
    IReadOnlyList<SchematicPlannedLabel> Labels,
    int MovedSymbolCount,
    double TotalDisplacementMillimeters);

internal sealed record SchematicRoutingPlan(
    IReadOnlyList<SchematicPlannedWire> Wires,
    IReadOnlyList<SchematicPlannedLabel> Labels);

internal sealed record SchematicPlannedSymbol(
    string Reference,
    int Unit,
    double X,
    double Y,
    double RotationDegrees);

internal sealed record SchematicPlannedWire(
    string Net,
    double X1,
    double Y1,
    double X2,
    double Y2);

internal sealed record SchematicPlannedLabel(
    string Net,
    double X,
    double Y);

internal readonly record struct SchematicPinPoint(
    double X,
    double Y,
    int DirectionX,
    int DirectionY);

internal readonly record struct SchematicRectangle(
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public bool Intersects(SchematicRectangle other) =>
        Left < other.Right - 0.001
        && Right > other.Left + 0.001
        && Top < other.Bottom - 0.001
        && Bottom > other.Top + 0.001;
}
