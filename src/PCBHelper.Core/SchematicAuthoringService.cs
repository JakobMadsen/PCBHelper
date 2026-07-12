using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed class SchematicAuthoringService
{
    private const double SchematicGridMillimeters = 1.27;
    private const string KiCad10BoardFormatVersion = "20260206";
    private const string KiCad10GeneratorVersion = "10.0";
    private static readonly object LibrarySymbolCacheLock = new();
    private static readonly Dictionary<string, string?> LibrarySymbolDefinitionCache = new(StringComparer.OrdinalIgnoreCase);
    private static string[]? SymbolLibraryDirectories;

    private readonly ProjectDiscoveryService _projectDiscovery;

    public SchematicAuthoringService(ProjectDiscoveryService projectDiscovery)
    {
        _projectDiscovery = projectDiscovery;
    }

    public ToolResponse<SchematicSymbolListResult> ListSymbols(string projectPath)
    {
        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicSymbolListResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var symbols = schematic.Data.Symbols
            .Where(static symbol => symbol.Reference is not null)
            .Select(static symbol => new SchematicSymbolSummary(
                symbol.Reference!,
                symbol.LibId,
                symbol.Unit,
                symbol.Properties.TryGetValue("Value", out var value) ? value.Value : null,
                symbol.Properties.TryGetValue("Footprint", out var footprint) ? footprint.Value : null,
                symbol.XMillimeters,
                symbol.YMillimeters,
                symbol.Properties.Select(static item => new SchematicFieldSummary(item.Key, item.Value.Value)).ToArray()))
            .ToArray();
        var wires = schematic.Data.Wires
            .Select(static wire => new SchematicWireSummary(
                wire.Uuid,
                wire.X1Millimeters,
                wire.Y1Millimeters,
                wire.X2Millimeters,
                wire.Y2Millimeters))
            .ToArray();
        var labels = schematic.Data.Labels
            .Select(static label => new SchematicLabelSummary(
                label.Uuid,
                label.Text,
                label.XMillimeters,
                label.YMillimeters))
            .ToArray();

        return ToolResponse<SchematicSymbolListResult>.Ok(
            $"Found {symbols.Length} schematic symbol(s).",
            new SchematicSymbolListResult(schematic.Data.SchematicFile, symbols, schematic.Data.Wires.Count, schematic.Data.Labels.Count, wires, labels));
    }

    public ToolResponse<SchematicMutationResult> CreateSymbol(string projectPath, string symbol, string reference, double x, double y, string? value, string? footprint, bool dryRun)
    {
        return CreateSymbol(projectPath, symbol, reference, x, y, value, footprint, unit: 1, dryRun);
    }

    public ToolResponse<SchematicMutationResult> CreateSymbol(string projectPath, string symbol, string reference, double x, double y, string? value, string? footprint, int unit, bool dryRun)
    {
        var catalog = SchematicSymbolCatalog.Find(symbol);
        if (catalog is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Unsupported schematic symbol: {symbol}", "SCHEMATIC_SYMBOL_UNSUPPORTED");
        }

        if (!catalog.Units.Contains(unit))
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Unsupported schematic symbol unit {unit} for {symbol}.", "SCHEMATIC_SYMBOL_UNIT_UNSUPPORTED");
        }

        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var matchingReference = schematic.Data.Symbols
            .Where(existing => string.Equals(existing.Reference, reference, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingReference.Any(existing => !string.Equals(existing.LibId, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic symbol reference already exists with another symbol id: {reference}", "SCHEMATIC_SYMBOL_EXISTS");
        }

        if (matchingReference.Any(existing => existing.Unit == unit))
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic symbol already exists: {reference} unit {unit}", "SCHEMATIC_SYMBOL_EXISTS");
        }

        var footprintValue = footprint ?? catalog.DefaultFootprint;
        var text = FormatSymbol(catalog, reference, value ?? catalog.DefaultValue, footprintValue, x, y, unit);
        var withLibrarySymbol = EnsureLibSymbolDefinition(schematic.Data.Text, catalog);
        var after = InsertBeforeSymbolInstances(withLibrarySymbol, text);
        if (!dryRun)
        {
            File.WriteAllText(schematic.Data.SchematicFile, after);
        }

        return Mutation("create-schematic-symbol", reference, dryRun, new[] { new ChangeFileSnapshot(schematic.Data.SchematicFile, schematic.Data.Text, after) }, text);
    }

    public ToolResponse<SchematicMutationResult> SetSymbolField(string projectPath, string reference, string field, string value, bool dryRun)
    {
        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var symbols = FindSymbols(schematic.Data, reference).ToArray();
        if (symbols.Length == 0)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic symbol not found: {reference}", "SCHEMATIC_SYMBOL_NOT_FOUND");
        }

        var before = schematic.Data.Text;
        var after = before;
        foreach (var symbol in symbols.OrderByDescending(static item => item.SourceStart))
        {
            if (symbol.Properties.TryGetValue(field, out var property))
            {
                after = after.Remove(property.ValueStart, property.ValueLength).Insert(property.ValueStart, value);
            }
            else
            {
                var propertyText = FormatProperty(field, value, symbol.XMillimeters ?? 0, (symbol.YMillimeters ?? 0) + 5);
                after = after.Insert(GetSymbolClosingLineStart(after, symbol), propertyText);
            }
        }

        if (!dryRun)
        {
            File.WriteAllText(schematic.Data.SchematicFile, after);
        }

        return Mutation("set-symbol-field", reference, dryRun, new[] { new ChangeFileSnapshot(schematic.Data.SchematicFile, before, after) }, $"{field}={value}");
    }

    public ToolResponse<SchematicMutationResult> ConnectPins(string projectPath, string from, string to, string? net, bool dryRun)
    {
        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var fromPin = ResolvePin(schematic.Data, from);
        var toPin = ResolvePin(schematic.Data, to);
        if (!fromPin.Success || fromPin.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(fromPin.Summary, fromPin.Error?.Code ?? "SCHEMATIC_PIN_NOT_FOUND", fromPin.Error?.Message);
        }

        if (!toPin.Success || toPin.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(toPin.Summary, toPin.Error?.Code ?? "SCHEMATIC_PIN_NOT_FOUND", toPin.Error?.Message);
        }

        if (string.IsNullOrWhiteSpace(net))
        {
            return ToolResponse<SchematicMutationResult>.Fail("A net name is required to connect schematic pins.", "SCHEMATIC_NET_REQUIRED");
        }

        var connectivity = SchematicConnectivity.Build(schematic.Data);
        var pins = new[] { fromPin.Data, toPin.Data };
        foreach (var pin in pins)
        {
            var conflictingNet = connectivity.NetNamesAtPoint(pin.X, pin.Y)
                .FirstOrDefault(item => !string.Equals(item, net, StringComparison.OrdinalIgnoreCase));
            if (conflictingNet is not null)
            {
                return ToolResponse<SchematicMutationResult>.Fail(
                    $"Schematic pin {pin.Reference}.{pin.Pin} is already connected to net {conflictingNet}.",
                    "SCHEMATIC_NET_CONFLICT");
            }
        }

        var fromConnected = connectivity.NetNamesAtPoint(fromPin.Data.X, fromPin.Data.Y)
            .Any(item => string.Equals(item, net, StringComparison.OrdinalIgnoreCase));
        var toConnected = connectivity.NetNamesAtPoint(toPin.Data.X, toPin.Data.Y)
            .Any(item => string.Equals(item, net, StringComparison.OrdinalIgnoreCase));

        if (fromConnected && toConnected)
        {
            return Mutation(
                "connect-schematic-pins",
                $"{from}-{to}",
                dryRun,
                new[] { new ChangeFileSnapshot(schematic.Data.SchematicFile, schematic.Data.Text, schematic.Data.Text) },
                string.Empty);
        }

        var fromStub = fromConnected ? null : FindStubEndpoint(connectivity, fromPin.Data, net);
        var toStub = toConnected ? null : FindStubEndpoint(connectivity, toPin.Data, net);
        if ((!fromConnected && fromStub is null) || (!toConnected && toStub is null))
        {
            return ToolResponse<SchematicMutationResult>.Fail(
                $"Could not place a schematic connection for {from}-{to} without crossing an existing net.",
                "SCHEMATIC_STUB_BLOCKED");
        }

        var addition = string.Empty;
        if (fromStub is not null)
        {
            addition += FormatWire(fromPin.Data.X, fromPin.Data.Y, fromStub.Value.X, fromStub.Value.Y);
        }

        if (toStub is not null)
        {
            addition += FormatWire(toPin.Data.X, toPin.Data.Y, toStub.Value.X, toStub.Value.Y);
        }

        double labelX;
        double labelY;
        if (fromStub is not null && toStub is not null)
        {
            var path = FindConnectionPath(connectivity, fromStub.Value, toStub.Value);
            if (path is null)
            {
                return ToolResponse<SchematicMutationResult>.Fail(
                    $"Could not route a schematic connection for {from}-{to} without touching another conductor.",
                    "SCHEMATIC_WIRE_BLOCKED");
            }

            for (var index = 0; index < path.Count - 1; index++)
            {
                addition += FormatWireIfNeeded(path[index].X, path[index].Y, path[index + 1].X, path[index + 1].Y);
            }

            var first = path[0];
            var second = path[1];
            labelX = SnapToSchematicGrid((first.X + second.X) / 2);
            labelY = SnapToSchematicGrid((first.Y + second.Y) / 2);
        }
        else
        {
            var endpoint = fromStub ?? toStub!.Value;
            labelX = endpoint.X;
            labelY = endpoint.Y;
        }

        addition += FormatLabel(net, labelX, labelY);
        var after = InsertBeforeSymbolInstances(schematic.Data.Text, addition);
        if (!dryRun)
        {
            File.WriteAllText(schematic.Data.SchematicFile, after);
        }

        return Mutation("connect-schematic-pins", $"{from}-{to}", dryRun, new[] { new ChangeFileSnapshot(schematic.Data.SchematicFile, schematic.Data.Text, after) }, addition);
    }

    public ToolResponse<SchematicMutationResult> AddNetLabel(string projectPath, string net, double x, double y, bool dryRun)
    {
        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var addition = FormatLabel(net, x, y);
        var after = InsertBeforeSymbolInstances(schematic.Data.Text, addition);
        if (!dryRun)
        {
            File.WriteAllText(schematic.Data.SchematicFile, after);
        }

        return Mutation("add-net-label", net, dryRun, new[] { new ChangeFileSnapshot(schematic.Data.SchematicFile, schematic.Data.Text, after) }, addition);
    }

    public ToolResponse<SchematicMutationResult> DeleteNetLabelByUuid(string projectPath, string uuid, bool dryRun)
    {
        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var label = schematic.Data.Labels.FirstOrDefault(item => string.Equals(item.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
        if (label is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic net label not found: {uuid}", "SCHEMATIC_LABEL_NOT_FOUND");
        }

        return DeleteSchematicBlock(schematic.Data, "delete-net-label", label.Text, label.SourceStart, label.SourceLength, dryRun);
    }

    public ToolResponse<SchematicMutationResult> DeleteNetLabel(string projectPath, string net, double x, double y, double? toleranceMillimeters, bool dryRun)
    {
        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var tolerance = NormalizeTolerance(toleranceMillimeters);
        if (tolerance is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail("Tolerance must be zero or greater.", "INVALID_TOLERANCE");
        }

        var matches = schematic.Data.Labels
            .Where(label => string.Equals(label.Text, net, StringComparison.OrdinalIgnoreCase)
                && Distance(label.XMillimeters, label.YMillimeters, x, y) <= tolerance.Value)
            .ToArray();
        if (matches.Length == 0)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic net label not found near ({x:0.###}, {y:0.###}): {net}", "SCHEMATIC_LABEL_NOT_FOUND");
        }

        if (matches.Length > 1)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Multiple schematic net labels matched near ({x:0.###}, {y:0.###}); delete by uuid instead.", "SCHEMATIC_LABEL_AMBIGUOUS");
        }

        var label = matches[0];
        return DeleteSchematicBlock(schematic.Data, "delete-net-label", label.Text, label.SourceStart, label.SourceLength, dryRun);
    }

    public ToolResponse<SchematicMutationResult> DeleteSchematicWireByUuid(string projectPath, string uuid, bool dryRun)
    {
        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var wire = schematic.Data.Wires.FirstOrDefault(item => string.Equals(item.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
        if (wire is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic wire not found: {uuid}", "SCHEMATIC_WIRE_NOT_FOUND");
        }

        return DeleteSchematicBlock(schematic.Data, "delete-schematic-wire", uuid, wire.SourceStart, wire.SourceLength, dryRun);
    }

    public ToolResponse<SchematicMutationResult> DeleteSchematicWire(string projectPath, double x1, double y1, double x2, double y2, double? toleranceMillimeters, bool dryRun)
    {
        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var tolerance = NormalizeTolerance(toleranceMillimeters);
        if (tolerance is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail("Tolerance must be zero or greater.", "INVALID_TOLERANCE");
        }

        var matches = schematic.Data.Wires
            .Where(wire => WireMatches(wire, x1, y1, x2, y2, tolerance.Value))
            .ToArray();
        if (matches.Length == 0)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic wire not found near ({x1:0.###}, {y1:0.###}) to ({x2:0.###}, {y2:0.###}).", "SCHEMATIC_WIRE_NOT_FOUND");
        }

        if (matches.Length > 1)
        {
            return ToolResponse<SchematicMutationResult>.Fail("Multiple schematic wires matched; delete by uuid instead.", "SCHEMATIC_WIRE_AMBIGUOUS");
        }

        var wire = matches[0];
        return DeleteSchematicBlock(schematic.Data, "delete-schematic-wire", wire.Uuid ?? $"{x1:0.###},{y1:0.###}-{x2:0.###},{y2:0.###}", wire.SourceStart, wire.SourceLength, dryRun);
    }

    public ToolResponse<SchematicMutationResult> UpdatePcbFromSchematic(string projectPath, bool dryRun)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (project.Data.BoardFile is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail("update-pcb-from-schematic requires a .kicad_pcb file.", "BOARD_FILE_MISSING");
        }

        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var boardBefore = File.ReadAllText(project.Data.BoardFile);
        var board = KiCadBoardParser.Parse(project.Data.BoardFile);
        var symbols = schematic.Data.Symbols
            .Where(static symbol => IsBoardSymbol(symbol.Reference))
            .GroupBy(static symbol => symbol.Reference!, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(static symbol => symbol.Unit).First())
            .ToArray();
        var labelNames = schematic.Data.Labels.Select(static label => label.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var nets = BuildCanonicalNets(labelNames, board);
        var nextX = NextGeneratedFootprintX(board);
        var footprints = new List<string>();
        var existingFootprintUpdates = new List<(KiCadFootprint Footprint, IReadOnlyDictionary<string, KiCadNet> PadNets)>();

        foreach (var symbol in symbols)
        {
            var libId = symbol.LibId ?? string.Empty;
            var catalog = SchematicSymbolCatalog.Find(libId);
            if (catalog is null)
            {
                return ToolResponse<SchematicMutationResult>.Fail($"Unsupported schematic symbol: {libId}", "SCHEMATIC_SYMBOL_UNSUPPORTED");
            }

            var footprint = symbol.Properties.TryGetValue("Footprint", out var footprintProperty) ? footprintProperty.Value : catalog.DefaultFootprint;
            if (!SchematicFootprintTemplates.IsSupported(footprint))
            {
                return ToolResponse<SchematicMutationResult>.Fail($"Missing footprint template: {footprint}", "FOOTPRINT_TEMPLATE_NOT_FOUND");
            }

            var value = symbol.Properties.TryGetValue("Value", out var valueProperty) ? valueProperty.Value : catalog.DefaultValue;
            var padNets = AssignPadNets(symbol.Reference!, catalog, schematic.Data, nets);
            var existing = board.Footprints.FirstOrDefault(footprint => string.Equals(footprint.Reference, symbol.Reference, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existingFootprintUpdates.Add((existing, MergePadNets(padNets, PreserveExistingPadNets(existing))));
                continue;
            }

            footprints.Add(SchematicFootprintTemplates.Format(footprint, symbol.Reference!, value, nextX, catalog.DefaultBoardY, padNets));
            nextX += 20;
        }

        var boardWithUpdatedPads = ApplyFootprintPadNetUpdates(boardBefore, existingFootprintUpdates);
        var after = RebuildBoard(boardWithUpdatedPads, nets, footprints);
        if (!dryRun)
        {
            File.WriteAllText(project.Data.BoardFile, after);
        }

        return Mutation("update-pcb-from-schematic", project.Data.ProjectName, dryRun, new[] { new ChangeFileSnapshot(project.Data.BoardFile, boardBefore, after) }, $"Created {footprints.Count} footprint(s).");
    }

    public ToolResponse<SchematicMutationResult> RegenerateBoardFootprint(string projectPath, string reference, bool dryRun)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (project.Data.BoardFile is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail("regenerate-board-footprint requires a .kicad_pcb file.", "BOARD_FILE_MISSING");
        }

        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        var symbol = FindSymbol(schematic.Data, reference);
        if (symbol is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic symbol not found: {reference}", "SCHEMATIC_SYMBOL_NOT_FOUND");
        }

        var libId = symbol.LibId ?? string.Empty;
        var catalog = SchematicSymbolCatalog.Find(libId);
        if (catalog is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Unsupported schematic symbol: {libId}", "SCHEMATIC_SYMBOL_UNSUPPORTED");
        }

        var board = KiCadBoardParser.Parse(project.Data.BoardFile);
        var existing = board.Footprints.FirstOrDefault(footprint => string.Equals(footprint.Reference, reference, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Board footprint not found: {reference}", "FOOTPRINT_NOT_FOUND");
        }

        if (existing.XMillimeters is null || existing.YMillimeters is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Footprint has no top-level position: {reference}", "FOOTPRINT_POSITION_MISSING");
        }

        var footprint = symbol.Properties.TryGetValue("Footprint", out var footprintProperty) ? footprintProperty.Value : catalog.DefaultFootprint;
        if (!SchematicFootprintTemplates.IsSupported(footprint))
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Missing footprint template: {footprint}", "FOOTPRINT_TEMPLATE_NOT_FOUND");
        }

        var value = symbol.Properties.TryGetValue("Value", out var valueProperty) ? valueProperty.Value : catalog.DefaultValue;
        var padNets = MergePadNets(
            AssignPadNets(reference, catalog, schematic.Data, board.Nets),
            PreserveExistingPadNets(existing));
        var regenerated = SchematicFootprintTemplates.Format(
            footprint,
            reference,
            value,
            existing.XMillimeters.Value,
            existing.YMillimeters.Value,
            padNets,
            existing.RotationDegrees);
        var removal = ExpandRemovalRange(board.Text, existing.SourceStart, existing.SourceLength);
        var after = board.Text.Remove(removal.Start, removal.Length).Insert(removal.Start, regenerated);
        if (!dryRun)
        {
            File.WriteAllText(project.Data.BoardFile, after);
        }

        return Mutation(
            "regenerate-board-footprint",
            reference,
            dryRun,
            new[] { new ChangeFileSnapshot(project.Data.BoardFile, board.Text, after) },
            regenerated);
    }

    internal ToolResponse<SchematicMutationResult> RestoreFileSnapshots(ChangeReport report, bool dryRun)
    {
        if (report.FileSnapshots is null || report.FileSnapshots.Count == 0)
        {
            return ToolResponse<SchematicMutationResult>.Fail("Change report does not contain file snapshots.", "FILE_SNAPSHOT_MISSING");
        }

        if (!dryRun)
        {
            foreach (var snapshot in report.FileSnapshots)
            {
                if (snapshot.BeforeText is not null)
                {
                    File.WriteAllText(snapshot.File, snapshot.BeforeText);
                }
            }
        }

        return ToolResponse<SchematicMutationResult>.Ok(
            $"{(dryRun ? "Previewed restore" : "Restored")} file snapshot change {report.ChangeId}.",
            new SchematicMutationResult("restore-change", report.Reference, dryRun, report.FileSnapshots, null, null, Array.Empty<string>()));
    }

    private ToolResponse<KiCadSchematicDocument> LoadSchematic(string projectPath)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<KiCadSchematicDocument>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (project.Data.SchematicFile is null)
        {
            return ToolResponse<KiCadSchematicDocument>.Fail("Operation requires a .kicad_sch file.", "SCHEMATIC_FILE_MISSING");
        }

        return ToolResponse<KiCadSchematicDocument>.Ok("Loaded schematic.", KiCadSchematicParser.Parse(project.Data.SchematicFile));
    }

    private static bool IsBoardSymbol(string? reference)
    {
        return !string.IsNullOrWhiteSpace(reference) && !reference.StartsWith("#", StringComparison.Ordinal);
    }

    private static IReadOnlyList<KiCadSchematicSymbol> FindSymbols(KiCadSchematicDocument schematic, string reference)
    {
        return schematic.Symbols
            .Where(symbol => string.Equals(symbol.Reference, reference, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static KiCadSchematicSymbol? FindSymbol(KiCadSchematicDocument schematic, string reference)
    {
        return FindSymbols(schematic, reference).FirstOrDefault();
    }

    private static ToolResponse<ResolvedSchematicPin> ResolvePin(KiCadSchematicDocument schematic, string pinReference)
    {
        var parts = pinReference.Split(new[] { '.', ':' }, 2);
        if (parts.Length != 2)
        {
            return ToolResponse<ResolvedSchematicPin>.Fail($"Pin reference must be <ref.pin> or <ref:pin>: {pinReference}", "SCHEMATIC_PIN_NOT_FOUND");
        }

        var symbols = FindSymbols(schematic, parts[0]);
        if (symbols.Count == 0)
        {
            return ToolResponse<ResolvedSchematicPin>.Fail($"Schematic symbol not found: {parts[0]}", "SCHEMATIC_PIN_NOT_FOUND");
        }

        var catalog = symbols
            .Select(symbol => symbol.LibId)
            .Where(static libId => libId is not null)
            .Select(libId => SchematicSymbolCatalog.Find(libId!))
            .FirstOrDefault(catalog => catalog is not null);
        var pin = catalog?.Pins.FirstOrDefault(item => string.Equals(item.Name, parts[1], StringComparison.OrdinalIgnoreCase));
        if (pin is null)
        {
            return ToolResponse<ResolvedSchematicPin>.Fail($"Schematic pin not found: {pinReference}", "SCHEMATIC_PIN_NOT_FOUND");
        }

        var symbol = symbols.FirstOrDefault(item => item.Unit == pin.Unit);
        if (symbol is null)
        {
            return ToolResponse<ResolvedSchematicPin>.Fail($"Schematic symbol unit {pin.Unit} is not placed for {parts[0]}.{parts[1]}.", "SCHEMATIC_SYMBOL_UNIT_NOT_PLACED");
        }

        if (symbol.LibId is null || symbol.XMillimeters is null || symbol.YMillimeters is null)
        {
            return ToolResponse<ResolvedSchematicPin>.Fail($"Schematic symbol not found: {parts[0]}", "SCHEMATIC_PIN_NOT_FOUND");
        }

        var x = SnapToSchematicGrid(SnapToSchematicGrid(symbol.XMillimeters.Value) + pin.OffsetX);
        var y = SnapToSchematicGrid(SnapToSchematicGrid(symbol.YMillimeters.Value) + pin.OffsetY);
        return ToolResponse<ResolvedSchematicPin>.Ok("Resolved pin.", new ResolvedSchematicPin(parts[0], parts[1], pin.Unit, x, y, pin.OffsetX, pin.OffsetY));
    }

    private static IReadOnlyDictionary<string, KiCadNet> AssignPadNets(string reference, SchematicSymbolCatalogEntry catalog, KiCadSchematicDocument schematic, IReadOnlyList<KiCadNet> nets)
    {
        var result = new Dictionary<string, KiCadNet>(StringComparer.OrdinalIgnoreCase);
        var connectivity = SchematicConnectivity.Build(schematic);
        foreach (var pin in catalog.Pins)
        {
            var pinPoint = ResolvePin(schematic, $"{reference}.{pin.Name}");
            if (pinPoint.Data is null)
            {
                continue;
            }

            var netName = connectivity.NetNamesAtPoint(pinPoint.Data.X, pinPoint.Data.Y).FirstOrDefault();
            var net = nets.FirstOrDefault(item => string.Equals(item.Name, netName, StringComparison.OrdinalIgnoreCase));
            if (net is not null)
            {
                result[pin.Name] = net;
                foreach (var alias in PadAliasesFor(catalog.SymbolId, pin.Name))
                {
                    result[alias] = net;
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> PadAliasesFor(string symbolId, string pinName)
    {
        if (symbolId is "Device:LED" or "Device:D" or "Device:D_Photo")
        {
            if (string.Equals(pinName, "K", StringComparison.OrdinalIgnoreCase))
            {
                yield return "1";
            }
            else if (string.Equals(pinName, "A", StringComparison.OrdinalIgnoreCase))
            {
                yield return "2";
            }
        }

        if (string.Equals(symbolId, "Connector_Generic:Conn_01x02", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(pinName, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(pinName, "Pin_1", StringComparison.OrdinalIgnoreCase))
            {
                yield return "1";
            }
            else if (string.Equals(pinName, "2", StringComparison.OrdinalIgnoreCase) || string.Equals(pinName, "Pin_2", StringComparison.OrdinalIgnoreCase))
            {
                yield return "2";
            }
        }
    }

    private static IReadOnlyDictionary<string, KiCadNet> PreserveExistingPadNets(KiCadFootprint footprint)
    {
        var result = new Dictionary<string, KiCadNet>(StringComparer.OrdinalIgnoreCase);
        foreach (var pad in footprint.Pads)
        {
            if (pad.NetCode is null || string.IsNullOrWhiteSpace(pad.NetName))
            {
                continue;
            }

            var key = string.IsNullOrWhiteSpace(pad.PinFunction) ? pad.Name : pad.PinFunction;
            result[key] = new KiCadNet(pad.NetCode.Value, pad.NetName);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, KiCadNet> MergePadNets(IReadOnlyDictionary<string, KiCadNet> primary, IReadOnlyDictionary<string, KiCadNet> fallback)
    {
        var result = new Dictionary<string, KiCadNet>(fallback, StringComparer.OrdinalIgnoreCase);
        foreach (var item in primary)
        {
            result[item.Key] = item.Value;
        }

        return result;
    }

    private static double NextGeneratedFootprintX(KiCadBoardDocument board)
    {
        var maxX = board.Footprints
            .Select(static footprint => footprint.XMillimeters)
            .Where(static x => x is not null)
            .Select(static x => x!.Value)
            .DefaultIfEmpty(25.0)
            .Max();

        return Math.Max(45.0, maxX + 20.0);
    }

    private static IReadOnlyList<KiCadNet> BuildCanonicalNets(IReadOnlyList<string> schematicNetNames, KiCadBoardDocument board)
    {
        var byName = new Dictionary<string, KiCadNet>(StringComparer.OrdinalIgnoreCase);
        var usedCodes = new HashSet<int>();

        foreach (var net in board.Nets.OrderBy(static net => net.Code))
        {
            AddKnownNet(net.Name, net.Code);
        }

        foreach (var pad in board.Footprints.SelectMany(static footprint => footprint.Pads))
        {
            if (pad.NetCode is not null && !string.IsNullOrWhiteSpace(pad.NetName))
            {
                AddKnownNet(pad.NetName, pad.NetCode.Value);
            }
        }

        foreach (var name in schematicNetNames.Where(static name => !string.IsNullOrWhiteSpace(name)))
        {
            if (!byName.ContainsKey(name))
            {
                byName[name] = new KiCadNet(NextAvailableCode(), name);
                usedCodes.Add(byName[name].Code);
            }
        }

        return byName.Values.OrderBy(static net => net.Code).ToArray();

        void AddKnownNet(string name, int code)
        {
            if (string.IsNullOrWhiteSpace(name) || byName.ContainsKey(name))
            {
                return;
            }

            var assignedCode = usedCodes.Contains(code) ? NextAvailableCode() : code;
            byName[name] = new KiCadNet(assignedCode, name);
            usedCodes.Add(assignedCode);
        }

        int NextAvailableCode()
        {
            var code = 1;
            while (usedCodes.Contains(code))
            {
                code++;
            }

            return code;
        }
    }

    private static string ApplyFootprintPadNetUpdates(string boardText, IReadOnlyList<(KiCadFootprint Footprint, IReadOnlyDictionary<string, KiCadNet> PadNets)> updates)
    {
        var result = boardText;
        foreach (var update in updates.OrderByDescending(static item => item.Footprint.SourceStart))
        {
            result = UpdateFootprintPadNets(result, update.Footprint, update.PadNets);
        }

        return result;
    }

    private static string UpdateFootprintPadNets(string boardText, KiCadFootprint footprint, IReadOnlyDictionary<string, KiCadNet> padNets)
    {
        var footprintText = boardText.Substring(footprint.SourceStart, footprint.SourceLength);
        foreach (var pad in footprint.Pads.OrderByDescending(static item => item.SourceStart))
        {
            var key = string.IsNullOrWhiteSpace(pad.PinFunction) ? pad.Name : pad.PinFunction;
            if (!padNets.TryGetValue(key, out var net))
            {
                continue;
            }

            var relativePadStart = pad.SourceStart - footprint.SourceStart;
            var padText = footprintText.Substring(relativePadStart, pad.SourceLength);
            var updatedPad = ReplacePadNet(padText, net);
            footprintText = footprintText.Remove(relativePadStart, pad.SourceLength).Insert(relativePadStart, updatedPad);
        }

        return boardText.Remove(footprint.SourceStart, footprint.SourceLength).Insert(footprint.SourceStart, footprintText);
    }

    private static string ReplacePadNet(string padText, KiCadNet net)
    {
        var netStart = padText.IndexOf("(net ", StringComparison.Ordinal);
        if (netStart >= 0)
        {
            var netEnd = KiCadSchematicParser.FindMatchingParenthesis(padText, netStart);
            if (netEnd >= 0)
            {
                return padText.Remove(netStart, netEnd - netStart + 1).Insert(netStart, FormatPadNet(net));
            }
        }

        var closeIndex = padText.LastIndexOf(')');
        if (closeIndex < 0)
        {
            return padText;
        }

        var newline = DetectNewline(padText);
        return padText.Insert(closeIndex, $"{newline}      {FormatPadNet(net)}");
    }

    private static string FormatPadNet(KiCadNet net)
    {
        return $"(net {net.Code} \"{EscapeKiCadString(net.Name)}\")";
    }

    private static bool PointOnWire(double x, double y, KiCadSchematicWire wire)
    {
        var horizontal = Math.Abs(wire.Y1Millimeters - wire.Y2Millimeters) < 0.001 && Math.Abs(y - wire.Y1Millimeters) < 0.001
            && x >= Math.Min(wire.X1Millimeters, wire.X2Millimeters) - 0.001
            && x <= Math.Max(wire.X1Millimeters, wire.X2Millimeters) + 0.001;
        var vertical = Math.Abs(wire.X1Millimeters - wire.X2Millimeters) < 0.001 && Math.Abs(x - wire.X1Millimeters) < 0.001
            && y >= Math.Min(wire.Y1Millimeters, wire.Y2Millimeters) - 0.001
            && y <= Math.Max(wire.Y1Millimeters, wire.Y2Millimeters) + 0.001;
        return horizontal || vertical;
    }

    private static ToolResponse<SchematicMutationResult> DeleteSchematicBlock(KiCadSchematicDocument schematic, string operation, string reference, int sourceStart, int sourceLength, bool dryRun)
    {
        var before = schematic.Text;
        var removal = ExpandRemovalRange(before, sourceStart, sourceLength);
        var removedText = before.Substring(removal.Start, removal.Length);
        var after = before.Remove(removal.Start, removal.Length);
        if (!dryRun)
        {
            File.WriteAllText(schematic.SchematicFile, after);
        }

        return Mutation(operation, reference, dryRun, new[] { new ChangeFileSnapshot(schematic.SchematicFile, before, after) }, removedText);
    }

    private static (int Start, int Length) ExpandRemovalRange(string text, int sourceStart, int sourceLength)
    {
        var start = sourceStart;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, sourceStart - 1));
        var candidateStart = lineStart < 0 ? 0 : lineStart + 1;
        if (text[candidateStart..sourceStart].All(char.IsWhiteSpace))
        {
            start = candidateStart;
        }

        var end = sourceStart + sourceLength;
        if (end < text.Length && text[end] == '\r')
        {
            end++;
        }

        if (end < text.Length && text[end] == '\n')
        {
            end++;
        }

        return (start, end - start);
    }

    private static double? NormalizeTolerance(double? toleranceMillimeters)
    {
        var tolerance = toleranceMillimeters ?? 0.05;
        return tolerance < 0 ? null : tolerance;
    }

    private static bool WireMatches(KiCadSchematicWire wire, double x1, double y1, double x2, double y2, double tolerance)
    {
        var forward = Distance(wire.X1Millimeters, wire.Y1Millimeters, x1, y1) <= tolerance
            && Distance(wire.X2Millimeters, wire.Y2Millimeters, x2, y2) <= tolerance;
        var reversed = Distance(wire.X1Millimeters, wire.Y1Millimeters, x2, y2) <= tolerance
            && Distance(wire.X2Millimeters, wire.Y2Millimeters, x1, y1) <= tolerance;
        return forward || reversed;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double SnapToSchematicGrid(double value)
    {
        return Math.Round(value / SchematicGridMillimeters) * SchematicGridMillimeters;
    }

    private static bool SameSchematicPoint(double x1, double y1, double x2, double y2)
    {
        return Math.Abs(SnapToSchematicGrid(x1) - SnapToSchematicGrid(x2)) < 0.001
            && Math.Abs(SnapToSchematicGrid(y1) - SnapToSchematicGrid(y2)) < 0.001;
    }

    private static (double X, double Y)? FindStubEndpoint(SchematicConnectivity connectivity, ResolvedSchematicPin pin, string net)
    {
        foreach (var direction in StubDirections(pin))
        {
            for (var length = 1; length <= 4; length++)
            {
                var x = SnapToSchematicGrid(pin.X + (direction.X * SchematicGridMillimeters * length));
                var y = SnapToSchematicGrid(pin.Y + (direction.Y * SchematicGridMillimeters * length));
                var endpointNets = connectivity.NetNamesAtPoint(x, y).ToArray();
                if (endpointNets.Any(item => !string.Equals(item, net, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (connectivity.SegmentTouchesConductorAfterStart(pin.X, pin.Y, x, y))
                {
                    continue;
                }

                return (x, y);
            }
        }

        return null;
    }

    private static IReadOnlyList<(double X, double Y)> StubDirections(ResolvedSchematicPin pin)
    {
        var primary = Math.Abs(pin.OffsetX) >= Math.Abs(pin.OffsetY)
            ? (X: pin.OffsetX < 0 ? -1.0 : 1.0, Y: 0.0)
            : (X: 0.0, Y: pin.OffsetY < 0 ? -1.0 : 1.0);
        var directions = new List<(double X, double Y)> { primary };
        foreach (var candidate in new[] { (-1.0, 0.0), (1.0, 0.0), (0.0, -1.0), (0.0, 1.0) })
        {
            if (!directions.Any(item => Math.Abs(item.X - candidate.Item1) < 0.001 && Math.Abs(item.Y - candidate.Item2) < 0.001))
            {
                directions.Add((candidate.Item1, candidate.Item2));
            }
        }

        return directions;
    }

    private static IReadOnlyList<(double X, double Y)>? FindConnectionPath(
        SchematicConnectivity connectivity,
        (double X, double Y) from,
        (double X, double Y) to)
    {
        var candidates = new List<IReadOnlyList<(double X, double Y)>>
        {
            NormalizePath(new[] { from, (to.X, from.Y), to }),
            NormalizePath(new[] { from, (from.X, to.Y), to })
        };

        for (var steps = 1; steps <= 20; steps++)
        {
            var offset = SchematicGridMillimeters * steps;
            foreach (var y in new[] { Math.Min(from.Y, to.Y) - offset, Math.Max(from.Y, to.Y) + offset })
            {
                candidates.Add(NormalizePath(new[] { from, (from.X, y), (to.X, y), to }));
            }

            foreach (var x in new[] { Math.Min(from.X, to.X) - offset, Math.Max(from.X, to.X) + offset })
            {
                candidates.Add(NormalizePath(new[] { from, (x, from.Y), (x, to.Y), to }));
            }
        }

        return candidates.FirstOrDefault(path => PathIsClear(connectivity, path))
            ?? FindGridConnectionPath(connectivity, from, to);
    }

    private static IReadOnlyList<(double X, double Y)>? FindGridConnectionPath(
        SchematicConnectivity connectivity,
        (double X, double Y) from,
        (double X, double Y) to)
    {
        var start = ToGridPoint(from);
        var target = ToGridPoint(to);
        const int margin = 40;
        var minX = Math.Min(start.X, target.X) - margin;
        var maxX = Math.Max(start.X, target.X) + margin;
        var minY = Math.Min(start.Y, target.Y) - margin;
        var maxY = Math.Max(start.Y, target.Y) + margin;
        var queue = new Queue<(int X, int Y)>();
        var previous = new Dictionary<(int X, int Y), (int X, int Y)>();
        var visited = new HashSet<(int X, int Y)> { start };
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == target)
            {
                var gridPath = new List<(int X, int Y)> { current };
                while (current != start)
                {
                    current = previous[current];
                    gridPath.Add(current);
                }

                gridPath.Reverse();
                return CompressPath(gridPath.Select(FromGridPoint).ToArray());
            }

            foreach (var next in new[]
            {
                (current.X - 1, current.Y),
                (current.X + 1, current.Y),
                (current.X, current.Y - 1),
                (current.X, current.Y + 1)
            })
            {
                if (next.Item1 < minX || next.Item1 > maxX || next.Item2 < minY || next.Item2 > maxY || !visited.Add(next))
                {
                    continue;
                }

                var currentPoint = FromGridPoint(current);
                var nextPoint = FromGridPoint(next);
                if (connectivity.SegmentCreatesUnintendedConnection(currentPoint.X, currentPoint.Y, nextPoint.X, nextPoint.Y))
                {
                    continue;
                }

                previous[next] = current;
                queue.Enqueue(next);
            }
        }

        return null;
    }

    private static (int X, int Y) ToGridPoint((double X, double Y) point)
    {
        return ((int)Math.Round(point.X / SchematicGridMillimeters), (int)Math.Round(point.Y / SchematicGridMillimeters));
    }

    private static (double X, double Y) FromGridPoint((int X, int Y) point)
    {
        return (SnapToSchematicGrid(point.X * SchematicGridMillimeters), SnapToSchematicGrid(point.Y * SchematicGridMillimeters));
    }

    private static IReadOnlyList<(double X, double Y)> CompressPath(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count <= 2)
        {
            return path;
        }

        var result = new List<(double X, double Y)> { path[0] };
        for (var index = 1; index < path.Count - 1; index++)
        {
            var previous = result[^1];
            var current = path[index];
            var next = path[index + 1];
            var sameDirection = (Math.Abs(previous.X - current.X) < 0.001 && Math.Abs(current.X - next.X) < 0.001)
                || (Math.Abs(previous.Y - current.Y) < 0.001 && Math.Abs(current.Y - next.Y) < 0.001);
            if (!sameDirection)
            {
                result.Add(current);
            }
        }

        result.Add(path[^1]);
        return result;
    }

    private static IReadOnlyList<(double X, double Y)> NormalizePath(IEnumerable<(double X, double Y)> points)
    {
        var normalized = new List<(double X, double Y)>();
        foreach (var point in points.Select(point => (SnapToSchematicGrid(point.X), SnapToSchematicGrid(point.Y))))
        {
            if (normalized.Count == 0 || !SameSchematicPoint(normalized[^1].X, normalized[^1].Y, point.Item1, point.Item2))
            {
                normalized.Add(point);
            }
        }

        return normalized;
    }

    private static bool PathIsClear(SchematicConnectivity connectivity, IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count < 2)
        {
            return true;
        }

        for (var index = 0; index < path.Count - 1; index++)
        {
            if (connectivity.SegmentCreatesUnintendedConnection(path[index].X, path[index].Y, path[index + 1].X, path[index + 1].Y))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetSymbolClosingLineStart(string text, KiCadSchematicSymbol symbol)
    {
        var closingParenthesis = symbol.SourceStart + symbol.SourceLength - 1;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, closingParenthesis - 1));
        return lineStart < 0 ? closingParenthesis : lineStart + 1;
    }

    private static ToolResponse<SchematicMutationResult> Mutation(string operation, string reference, bool dryRun, IReadOnlyList<ChangeFileSnapshot> snapshots, string? previewText)
    {
        return ToolResponse<SchematicMutationResult>.Ok(
            $"{(dryRun ? "Previewed" : "Applied")} {operation}.",
            new SchematicMutationResult(operation, reference, dryRun, snapshots, previewText, null, Array.Empty<string>()));
    }

    private static string InsertBeforeSymbolInstances(string text, string addition)
    {
        var marker = "  (symbol_instances)";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            return text.Insert(index, addition);
        }

        var lastClose = text.LastIndexOf(')');
        return lastClose >= 0 ? text.Insert(lastClose, addition) : text + addition;
    }

    private static string EnsureLibSymbolDefinition(string text, SchematicSymbolCatalogEntry catalog)
    {
        var libSymbolsStart = text.IndexOf("  (lib_symbols", StringComparison.Ordinal);
        if (libSymbolsStart < 0)
        {
            return text;
        }

        var libSymbolsEnd = KiCadSchematicParser.FindMatchingParenthesis(text, libSymbolsStart);
        if (libSymbolsEnd < 0)
        {
            return text;
        }

        var libSymbolsText = text.Substring(libSymbolsStart, libSymbolsEnd - libSymbolsStart + 1);
        if (libSymbolsText.Contains($"(symbol \"{catalog.SymbolId}\"", StringComparison.Ordinal))
        {
            return text;
        }

        var definition = TryFormatLibraryExactLibSymbolDefinition(catalog) ?? FormatLibSymbolDefinition(catalog);
        if (string.Equals(libSymbolsText, "  (lib_symbols)", StringComparison.Ordinal))
        {
            return text.Remove(libSymbolsStart, libSymbolsText.Length)
                .Insert(libSymbolsStart, $"  (lib_symbols{Environment.NewLine}{definition}  )");
        }

        return text.Insert(libSymbolsEnd, definition);
    }

    private static string FormatSymbol(SchematicSymbolCatalogEntry catalog, string reference, string value, string footprint, double x, double y, int unit)
    {
        var uuid = Guid.NewGuid().ToString();
        var snappedX = SnapToSchematicGrid(x);
        var snappedY = SnapToSchematicGrid(y);
        return string.Join(Environment.NewLine, new[]
        {
            "  (symbol",
            $"    (lib_id \"{catalog.SymbolId}\")",
            $"    (at {KiCadSchematicParser.FormatNumber(snappedX)} {KiCadSchematicParser.FormatNumber(snappedY)} 0)",
            $"    (unit {unit})",
            "    (exclude_from_sim no)",
            "    (in_bom yes)",
            "    (on_board yes)",
            "    (dnp no)",
            $"    (uuid \"{uuid}\")",
            FormatProperty("Reference", reference, snappedX, snappedY - (2 * SchematicGridMillimeters)).TrimEnd(),
            FormatProperty("Value", value, snappedX, snappedY + (2 * SchematicGridMillimeters)).TrimEnd(),
            FormatProperty("Footprint", footprint, snappedX, snappedY + (4 * SchematicGridMillimeters), hidden: true).TrimEnd(),
            "  )",
            string.Empty
        });
    }

    private static string FormatLibSymbolDefinition(SchematicSymbolCatalogEntry catalog)
    {
        var symbolName = catalog.SymbolId.Split(':').Last();
        var unitBlocks = string.Concat(catalog.Units.Select(unit =>
        {
            var pins = string.Concat(catalog.Pins.Where(pin => pin.Unit == unit).Select(FormatLibSymbolPin));
            return string.Join(Environment.NewLine, new[]
            {
                $"      (symbol \"{symbolName}_{unit}_1\"",
                pins.TrimEnd(),
                "      )",
                string.Empty
            });
        }));
        return string.Join(Environment.NewLine, new[]
        {
            $"    (symbol \"{catalog.SymbolId}\"",
            "      (pin_names (offset 0))",
            "      (exclude_from_sim no)",
            "      (in_bom yes)",
            "      (on_board yes)",
            $"      (property \"Reference\" \"{catalog.DefaultValue}\"",
            "        (at 0 0 0)",
            "        (effects (font (size 1.27 1.27)))",
            "      )",
            $"      (property \"Value\" \"{catalog.DefaultValue}\"",
            "        (at 0 0 0)",
            "        (effects (font (size 1.27 1.27)))",
            "      )",
            unitBlocks.TrimEnd(),
            "    )",
            string.Empty
        });
    }

    private static string? TryFormatLibraryExactLibSymbolDefinition(SchematicSymbolCatalogEntry catalog)
    {
        lock (LibrarySymbolCacheLock)
        {
            if (LibrarySymbolDefinitionCache.TryGetValue(catalog.SymbolId, out var cached))
            {
                return cached;
            }
        }

        var definition = TryLoadLibraryExactLibSymbolDefinition(catalog);
        lock (LibrarySymbolCacheLock)
        {
            LibrarySymbolDefinitionCache[catalog.SymbolId] = definition;
        }

        return definition;
    }

    private static string? TryLoadLibraryExactLibSymbolDefinition(SchematicSymbolCatalogEntry catalog)
    {
        var parts = catalog.SymbolId.Split(':', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        foreach (var libraryFile in GetKiCadSymbolLibraryCandidates(parts[0]))
        {
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            var text = File.ReadAllText(libraryFile);
            var block = ExtractLibrarySymbolBlock(text, parts[1]);
            if (block is null)
            {
                continue;
            }

            var renamed = RenameTopLevelSymbol(block, catalog.SymbolId);
            return IndentBlock(renamed, 4) + Environment.NewLine;
        }

        return null;
    }

    private static IEnumerable<string> GetKiCadSymbolLibraryCandidates(string libraryName)
    {
        foreach (var directory in GetKiCadSymbolLibraryDirectories())
        {
            yield return Path.Combine(directory, $"{libraryName}.kicad_sym");
        }
    }

    private static IReadOnlyList<string> GetKiCadSymbolLibraryDirectories()
    {
        if (SymbolLibraryDirectories is not null)
        {
            return SymbolLibraryDirectories;
        }

        var directories = new List<string>();
        var configuredSymbolDir = Environment.GetEnvironmentVariable("KICAD_SYMBOL_DIR");
        if (!string.IsNullOrWhiteSpace(configuredSymbolDir))
        {
            directories.Add(configuredSymbolDir);
        }

        foreach (var root in KiCadInstallRootDiscovery.GetInstallRoots())
        {
            directories.Add(Path.Combine(root, "share", "kicad", "symbols"));
        }

        var cli = new KiCadCliLocator().Locate();
        if (cli.Found && cli.ExecutablePath is not null)
        {
            var bin = Path.GetDirectoryName(cli.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(bin))
            {
                directories.Add(Path.GetFullPath(Path.Combine(bin, "..", "share", "kicad", "symbols")));
            }
        }

        SymbolLibraryDirectories = directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return SymbolLibraryDirectories;
    }

    private static string? ExtractLibrarySymbolBlock(string text, string symbolName)
    {
        var marker = $"(symbol \"{symbolName}\"";
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var symbolStart = text.IndexOf(marker, searchIndex, StringComparison.Ordinal);
            if (symbolStart < 0)
            {
                return null;
            }

            var symbolEnd = KiCadSchematicParser.FindMatchingParenthesis(text, symbolStart);
            if (symbolEnd < 0)
            {
                return null;
            }

            return text.Substring(symbolStart, symbolEnd - symbolStart + 1);
        }

        return null;
    }

    private static string RenameTopLevelSymbol(string block, string symbolId)
    {
        var nameStart = block.IndexOf('"', StringComparison.Ordinal);
        if (nameStart < 0)
        {
            return block;
        }

        var nameEnd = block.IndexOf('"', nameStart + 1);
        if (nameEnd < 0)
        {
            return block;
        }

        return block.Remove(nameStart + 1, nameEnd - nameStart - 1).Insert(nameStart + 1, symbolId);
    }

    private static string IndentBlock(string block, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join(Environment.NewLine, block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => prefix + line.TrimEnd()));
    }

    private static string FormatLibSymbolPin(SchematicPinDefinition pin)
    {
        var pinAtX = pin.OffsetX;
        var pinAtY = Math.Abs(pin.OffsetY) > Math.Abs(pin.OffsetX) ? -pin.OffsetY : pin.OffsetY;
        var rotation = PinRotation(pinAtX, pinAtY);
        return string.Join(Environment.NewLine, new[]
        {
            "        (pin passive line",
            $"          (at {KiCadSchematicParser.FormatNumber(pinAtX)} {KiCadSchematicParser.FormatNumber(pinAtY)} {KiCadSchematicParser.FormatNumber(rotation)})",
            "          (length 2.54)",
            $"          (name \"{pin.Name}\" (effects (font (size 1.27 1.27))))",
            $"          (number \"{pin.Name}\" (effects (font (size 1.27 1.27))))",
            "        )",
            string.Empty
        });
    }

    private static double PinRotation(double offsetX, double offsetY)
    {
        if (Math.Abs(offsetX) >= Math.Abs(offsetY))
        {
            return offsetX < 0 ? 0 : 180;
        }

        return offsetY < 0 ? 90 : 270;
    }

    private static string FormatProperty(string name, string value, double x, double y, bool hidden = false)
    {
        var snappedX = SnapToSchematicGrid(x);
        var snappedY = SnapToSchematicGrid(y);
        var lines = new List<string>
        {
            $"    (property \"{name}\" \"{value}\"",
            $"      (at {KiCadSchematicParser.FormatNumber(snappedX)} {KiCadSchematicParser.FormatNumber(snappedY)} 0)",
        };
        if (hidden)
        {
            lines.Add("      (hide yes)");
        }

        lines.AddRange(new[]
        {
            "      (effects",
            "        (font",
            "          (size 1.27 1.27)",
            "        )",
            "      )",
            "    )",
            string.Empty
        });
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatWire(double x1, double y1, double x2, double y2)
    {
        var snappedX1 = SnapToSchematicGrid(x1);
        var snappedY1 = SnapToSchematicGrid(y1);
        var snappedX2 = SnapToSchematicGrid(x2);
        var snappedY2 = SnapToSchematicGrid(y2);
        return string.Join(Environment.NewLine, new[]
        {
            "  (wire",
            $"    (pts (xy {KiCadSchematicParser.FormatNumber(snappedX1)} {KiCadSchematicParser.FormatNumber(snappedY1)}) (xy {KiCadSchematicParser.FormatNumber(snappedX2)} {KiCadSchematicParser.FormatNumber(snappedY2)}))",
            "    (stroke (width 0) (type default))",
            $"    (uuid \"{Guid.NewGuid()}\")",
            "  )",
            string.Empty
        });
    }

    private static string FormatWireIfNeeded(double x1, double y1, double x2, double y2)
    {
        return SameSchematicPoint(x1, y1, x2, y2)
            ? string.Empty
            : FormatWire(x1, y1, x2, y2);
    }

    private static string FormatLabel(string net, double x, double y)
    {
        var snappedX = SnapToSchematicGrid(x);
        var snappedY = SnapToSchematicGrid(y);
        return string.Join(Environment.NewLine, new[]
        {
            $"  (label \"{net}\"",
            $"    (at {KiCadSchematicParser.FormatNumber(snappedX)} {KiCadSchematicParser.FormatNumber(snappedY)} 0)",
            "    (effects",
            "      (font (size 1.27 1.27))",
            "    )",
            $"    (uuid \"{Guid.NewGuid()}\")",
            "  )",
            string.Empty
        });
    }

    private static string RebuildBoard(string boardText, IReadOnlyList<KiCadNet> nets, IReadOnlyList<string> footprints)
    {
        var newline = DetectNewline(boardText);
        var withoutEmbedded = RemoveTopLevelBlocks(boardText, "embedded_fonts");
        var withoutNets = RemoveTopLevelBlocks(withoutEmbedded, "net");
        var netText = string.Concat(nets.Select(net => $"  (net {net.Code} \"{EscapeKiCadString(net.Name)}\"){newline}"));
        var firstFootprint = FindTopLevelBlock(withoutNets, "footprint");
        var netInsert = firstFootprint >= 0 ? firstFootprint : FindBoardClosingIndex(withoutNets);
        var withNets = withoutNets.Insert(netInsert, netText);
        var lastClose = FindBoardClosingIndex(withNets);
        var prefix = lastClose >= 0 ? withNets[..lastClose] : withNets;
        var footprintText = string.Concat(footprints);
        var rebuilt = prefix + footprintText + $"  (embedded_fonts no){newline}){newline}";
        return NormalizeBoardHeaderForKiCad10(rebuilt, newline);
    }

    private static string NormalizeBoardHeaderForKiCad10(string boardText, string newline)
    {
        var unixText = boardText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = unixText.Split('\n').ToList();

        var generatorIndex = -1;
        var generatorVersionIndex = -1;
        for (var i = 0; i < Math.Min(lines.Count, 40); i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("(version ", StringComparison.Ordinal))
            {
                var indent = lines[i][..(lines[i].Length - trimmed.Length)];
                lines[i] = $"{indent}(version {KiCad10BoardFormatVersion})";
            }
            else if (trimmed.StartsWith("(generator ", StringComparison.Ordinal))
            {
                generatorIndex = i;
            }
            else if (trimmed.StartsWith("(generator_version ", StringComparison.Ordinal))
            {
                var indent = lines[i][..(lines[i].Length - trimmed.Length)];
                lines[i] = $"{indent}(generator_version \"{KiCad10GeneratorVersion}\")";
                generatorVersionIndex = i;
            }
        }

        if (generatorVersionIndex < 0 && generatorIndex >= 0)
        {
            var generatorLine = lines[generatorIndex];
            var generatorTrimmed = generatorLine.TrimStart();
            var indent = generatorLine[..(generatorLine.Length - generatorTrimmed.Length)];
            lines.Insert(generatorIndex + 1, $"{indent}(generator_version \"{KiCad10GeneratorVersion}\")");
        }

        var normalized = string.Join("\n", lines);
        return newline == "\r\n"
            ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal)
            : normalized;
    }

    private static string RemoveTopLevelBlocks(string text, string keyword)
    {
        var ranges = new List<(int Start, int Length)>();
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '(')
            {
                if (depth == 1 && IsBlockKeyword(text, index, keyword))
                {
                    var end = KiCadSchematicParser.FindMatchingParenthesis(text, index);
                    if (end >= 0)
                    {
                        var removal = ExpandRemovalRange(text, index, end - index + 1);
                        ranges.Add(removal);
                        index = end;
                        continue;
                    }
                }

                depth++;
            }
            else if (current == ')')
            {
                depth--;
            }
        }

        var result = text;
        foreach (var range in ranges.OrderByDescending(static range => range.Start))
        {
            result = result.Remove(range.Start, range.Length);
        }

        return result;
    }

    private static int FindTopLevelBlock(string text, string keyword)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '(')
            {
                if (depth == 1 && IsBlockKeyword(text, index, keyword))
                {
                    return index;
                }

                depth++;
            }
            else if (current == ')')
            {
                depth--;
            }
        }

        return -1;
    }

    private static bool IsBlockKeyword(string text, int openIndex, string keyword)
    {
        var marker = $"({keyword}";
        if (openIndex + marker.Length > text.Length || !text.AsSpan(openIndex, marker.Length).SequenceEqual(marker))
        {
            return false;
        }

        var afterKeyword = openIndex + marker.Length;
        return afterKeyword >= text.Length || char.IsWhiteSpace(text[afterKeyword]);
    }

    private static int FindBoardClosingIndex(string text)
    {
        return text.LastIndexOf(')');
    }

    private static string DetectNewline(string text)
    {
        return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static string EscapeKiCadString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

public sealed record SchematicSymbolListResult(
    string SchematicFile,
    IReadOnlyList<SchematicSymbolSummary> Symbols,
    int WireCount,
    int LabelCount,
    IReadOnlyList<SchematicWireSummary> Wires,
    IReadOnlyList<SchematicLabelSummary> Labels);

public sealed record SchematicSymbolSummary(string Reference, string? SymbolId, int Unit, string? Value, string? Footprint, double? XMillimeters, double? YMillimeters, IReadOnlyList<SchematicFieldSummary> Fields);

public sealed record SchematicFieldSummary(string Name, string Value);

public sealed record SchematicWireSummary(string? Uuid, double X1Millimeters, double Y1Millimeters, double X2Millimeters, double Y2Millimeters);

public sealed record SchematicLabelSummary(string? Uuid, string Text, double XMillimeters, double YMillimeters);

public sealed record SchematicMutationResult(
    string Operation,
    string Reference,
    bool DryRun,
    IReadOnlyList<ChangeFileSnapshot> FileSnapshots,
    string? PreviewText,
    string? ChangeReportPath,
    IReadOnlyList<string> CheckReportPaths);

internal sealed record ResolvedSchematicPin(string Reference, string Pin, int Unit, double X, double Y, double OffsetX, double OffsetY);

internal sealed class SchematicConnectivity
{
    private const double Epsilon = 0.001;
    private const double GridMillimeters = 1.27;

    private readonly KiCadSchematicDocument _schematic;
    private readonly IReadOnlyList<SchematicWireIsland> _islands;
    private readonly IReadOnlyList<SchematicPoint> _pinPoints;

    private SchematicConnectivity(KiCadSchematicDocument schematic, IReadOnlyList<SchematicWireIsland> islands, IReadOnlyList<SchematicPoint> pinPoints)
    {
        _schematic = schematic;
        _islands = islands;
        _pinPoints = pinPoints;
    }

    public static SchematicConnectivity Build(KiCadSchematicDocument schematic)
    {
        var wires = schematic.Wires;
        var parent = Enumerable.Range(0, wires.Count).ToArray();
        for (var first = 0; first < wires.Count; first++)
        {
            for (var second = first + 1; second < wires.Count; second++)
            {
                if (SegmentsConnect(wires[first], wires[second], schematic.Junctions))
                {
                    Union(parent, first, second);
                }
            }
        }

        var grouped = new Dictionary<int, List<KiCadSchematicWire>>();
        for (var index = 0; index < wires.Count; index++)
        {
            var root = Find(parent, index);
            if (!grouped.TryGetValue(root, out var islandWires))
            {
                islandWires = new List<KiCadSchematicWire>();
                grouped[root] = islandWires;
            }

            islandWires.Add(wires[index]);
        }

        var islands = grouped.Values
            .Select(wireGroup =>
                new SchematicWireIsland(
                    wireGroup,
                    schematic.Labels
                        .Where(label => wireGroup.Any(wire => PointOnSegment(label.XMillimeters, label.YMillimeters, wire)))
                        .ToArray()))
            .ToArray();
        var pinPoints = schematic.Symbols
            .Where(static symbol => symbol.LibId is not null && symbol.XMillimeters is not null && symbol.YMillimeters is not null)
            .SelectMany(static symbol =>
            {
                var catalog = SchematicSymbolCatalog.Find(symbol.LibId!);
                if (catalog is null)
                {
                    return Array.Empty<SchematicPoint>();
                }

                var originX = Snap(symbol.XMillimeters!.Value);
                var originY = Snap(symbol.YMillimeters!.Value);
                return catalog.Pins
                    .Where(pin => pin.Unit == symbol.Unit)
                    .Select(pin => new SchematicPoint(Snap(originX + pin.OffsetX), Snap(originY + pin.OffsetY)))
                    .ToArray();
            })
            .ToArray();

        return new SchematicConnectivity(schematic, islands, pinPoints);
    }

    public IReadOnlyList<string> NetNamesAtPoint(double x, double y)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in _schematic.Labels.Where(label => SamePoint(label.XMillimeters, label.YMillimeters, x, y)))
        {
            names.Add(label.Text);
        }

        foreach (var island in _islands.Where(island => island.Wires.Any(wire => PointOnSegment(x, y, wire))))
        {
            foreach (var label in island.Labels)
            {
                names.Add(label.Text);
            }
        }

        return names.ToArray();
    }

    public bool SegmentTouchesConductorAfterStart(double x1, double y1, double x2, double y2)
    {
        return _schematic.Wires.Any(wire => SegmentTouchesWireAfterStart(x1, y1, x2, y2, wire))
            || _schematic.Labels.Any(label => PointOnSegment(label.XMillimeters, label.YMillimeters, x1, y1, x2, y2)
                && !SamePoint(label.XMillimeters, label.YMillimeters, x1, y1))
            || _pinPoints.Any(point => PointOnSegment(point.X, point.Y, x1, y1, x2, y2)
                && !SamePoint(point.X, point.Y, x1, y1));
    }

    public bool SegmentCreatesUnintendedConnection(double x1, double y1, double x2, double y2)
    {
        var candidate = new KiCadSchematicWire(x1, y1, x2, y2, null, 0, 0);
        return _schematic.Wires.Any(wire => SegmentsConnect(candidate, wire, Array.Empty<KiCadSchematicJunction>()))
            || _schematic.Labels.Any(label => PointOnSegment(label.XMillimeters, label.YMillimeters, candidate))
            || _pinPoints.Any(point => PointOnSegment(point.X, point.Y, candidate));
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Union(int[] parent, int first, int second)
    {
        var firstRoot = Find(parent, first);
        var secondRoot = Find(parent, second);
        if (firstRoot != secondRoot)
        {
            parent[secondRoot] = firstRoot;
        }
    }

    private static bool SegmentsConnect(KiCadSchematicWire first, KiCadSchematicWire second, IReadOnlyList<KiCadSchematicJunction> junctions)
    {
        var firstHorizontal = Math.Abs(first.Y1Millimeters - first.Y2Millimeters) < Epsilon;
        var secondHorizontal = Math.Abs(second.Y1Millimeters - second.Y2Millimeters) < Epsilon;
        if (firstHorizontal != secondHorizontal)
        {
            var horizontal = firstHorizontal ? first : second;
            var vertical = firstHorizontal ? second : first;
            var x = vertical.X1Millimeters;
            var y = horizontal.Y1Millimeters;
            var crosses = x >= Math.Min(horizontal.X1Millimeters, horizontal.X2Millimeters) - Epsilon
                && x <= Math.Max(horizontal.X1Millimeters, horizontal.X2Millimeters) + Epsilon
                && y >= Math.Min(vertical.Y1Millimeters, vertical.Y2Millimeters) - Epsilon
                && y <= Math.Max(vertical.Y1Millimeters, vertical.Y2Millimeters) + Epsilon;
            if (!crosses)
            {
                return false;
            }

            return IsWireEndpoint(first, x, y)
                || IsWireEndpoint(second, x, y)
                || junctions.Any(junction => SamePoint(junction.XMillimeters, junction.YMillimeters, x, y));
        }

        return SegmentTouchesWireAfterStart(first.X1Millimeters, first.Y1Millimeters, first.X2Millimeters, first.Y2Millimeters, second, ignoreStart: false);
    }

    private static bool IsWireEndpoint(KiCadSchematicWire wire, double x, double y)
    {
        return SamePoint(wire.X1Millimeters, wire.Y1Millimeters, x, y)
            || SamePoint(wire.X2Millimeters, wire.Y2Millimeters, x, y);
    }

    private static bool SegmentTouchesWireAfterStart(double x1, double y1, double x2, double y2, KiCadSchematicWire wire, bool ignoreStart = true)
    {
        var horizontal = Math.Abs(y1 - y2) < Epsilon;
        var wireHorizontal = Math.Abs(wire.Y1Millimeters - wire.Y2Millimeters) < Epsilon;
        if (horizontal && wireHorizontal)
        {
            if (Math.Abs(y1 - wire.Y1Millimeters) >= Epsilon)
            {
                return false;
            }

            var overlapStart = Math.Max(Math.Min(x1, x2), Math.Min(wire.X1Millimeters, wire.X2Millimeters));
            var overlapEnd = Math.Min(Math.Max(x1, x2), Math.Max(wire.X1Millimeters, wire.X2Millimeters));
            return OverlapTouchesBeyondStart(overlapStart, overlapEnd, x1, y1, true, ignoreStart);
        }

        if (!horizontal && !wireHorizontal)
        {
            if (Math.Abs(x1 - wire.X1Millimeters) >= Epsilon)
            {
                return false;
            }

            var overlapStart = Math.Max(Math.Min(y1, y2), Math.Min(wire.Y1Millimeters, wire.Y2Millimeters));
            var overlapEnd = Math.Min(Math.Max(y1, y2), Math.Max(wire.Y1Millimeters, wire.Y2Millimeters));
            return OverlapTouchesBeyondStart(overlapStart, overlapEnd, x1, y1, false, ignoreStart);
        }

        var verticalX = horizontal ? wire.X1Millimeters : x1;
        var horizontalY = horizontal ? y1 : wire.Y1Millimeters;
        var verticalY1 = horizontal ? wire.Y1Millimeters : y1;
        var verticalY2 = horizontal ? wire.Y2Millimeters : y2;
        var horizontalX1 = horizontal ? x1 : wire.X1Millimeters;
        var horizontalX2 = horizontal ? x2 : wire.X2Millimeters;
        var touches = verticalX >= Math.Min(horizontalX1, horizontalX2) - Epsilon
            && verticalX <= Math.Max(horizontalX1, horizontalX2) + Epsilon
            && horizontalY >= Math.Min(verticalY1, verticalY2) - Epsilon
            && horizontalY <= Math.Max(verticalY1, verticalY2) + Epsilon;
        return touches && (!ignoreStart || !SamePoint(verticalX, horizontalY, x1, y1));
    }

    private static bool OverlapTouchesBeyondStart(double overlapStart, double overlapEnd, double startX, double startY, bool horizontal, bool ignoreStart)
    {
        if (overlapStart > overlapEnd + Epsilon)
        {
            return false;
        }

        if (!ignoreStart)
        {
            return true;
        }

        var onlyStart = Math.Abs(overlapStart - overlapEnd) < Epsilon
            && (horizontal
                ? SamePoint(overlapStart, startY, startX, startY)
                : SamePoint(startX, overlapStart, startX, startY));
        return !onlyStart;
    }

    private static bool PointOnSegment(double x, double y, KiCadSchematicWire wire)
    {
        return PointOnSegment(x, y, wire.X1Millimeters, wire.Y1Millimeters, wire.X2Millimeters, wire.Y2Millimeters);
    }

    private static bool PointOnSegment(double x, double y, double x1, double y1, double x2, double y2)
    {
        var horizontal = Math.Abs(y1 - y2) < Epsilon && Math.Abs(y - y1) < Epsilon
            && x >= Math.Min(x1, x2) - Epsilon
            && x <= Math.Max(x1, x2) + Epsilon;
        var vertical = Math.Abs(x1 - x2) < Epsilon && Math.Abs(x - x1) < Epsilon
            && y >= Math.Min(y1, y2) - Epsilon
            && y <= Math.Max(y1, y2) + Epsilon;
        return horizontal || vertical;
    }

    private static bool SamePoint(double x1, double y1, double x2, double y2)
    {
        return Math.Abs(x1 - x2) < Epsilon && Math.Abs(y1 - y2) < Epsilon;
    }

    private static double Snap(double value)
    {
        return Math.Round(value / GridMillimeters) * GridMillimeters;
    }
}

internal sealed record SchematicWireIsland(IReadOnlyList<KiCadSchematicWire> Wires, IReadOnlyList<KiCadSchematicLabel> Labels);

internal sealed record SchematicPoint(double X, double Y);

internal sealed record SchematicSymbolCatalogEntry(string SymbolId, string DefaultValue, string DefaultFootprint, double DefaultBoardY, IReadOnlyList<SchematicPinDefinition> Pins)
{
    public IReadOnlyList<int> Units { get; } = Pins.Select(static pin => pin.Unit).Distinct().OrderBy(static unit => unit).ToArray();
}

internal sealed record SchematicPinDefinition(string Name, double OffsetX, double OffsetY, int Unit = 1);

internal static class SchematicSymbolCatalog
{
    private static readonly SchematicSymbolCatalogEntry[] Entries =
    {
        new("Device:R", "R", "R_Axial_2Pad", 35, new[] { new SchematicPinDefinition("1", 0, -3.81), new SchematicPinDefinition("2", 0, 3.81) }),
        new("Device:C", "C", "C_Disc_2Pad", 42, new[] { new SchematicPinDefinition("1", 0, -3.81), new SchematicPinDefinition("2", 0, 3.81) }),
        new("Device:LED", "LED", "LED_2Pad", 35, new[] { new SchematicPinDefinition("A", 3.81, 0), new SchematicPinDefinition("K", -3.81, 0) }),
        new("Device:D", "D", "LED_2Pad", 35, new[] { new SchematicPinDefinition("A", 3.81, 0), new SchematicPinDefinition("K", -3.81, 0) }),
        new("Device:D_Photo", "D_Photo", "Photodiode_2Pad", 28, new[] { new SchematicPinDefinition("A", 2.54, 0), new SchematicPinDefinition("K", -5.08, 0) }),
        new("Device:Battery_Cell", "Battery_Cell", "BatteryHolder_2Pad_Back", 50, new[] { new SchematicPinDefinition("+", 0, -5.08), new SchematicPinDefinition("-", 0, 2.54) }),
        new("power:PWR_FLAG", "PWR_FLAG", "", 50, new[] { new SchematicPinDefinition("1", 0, 0) }),
        new("Connector_Generic:Conn_01x02", "Conn_01x02", "Connector_PinHeader_2.54mm:PinHeader_1x02_P2.54mm_Vertical", 50, new[] { new SchematicPinDefinition("1", -5.08, 0), new SchematicPinDefinition("2", -5.08, 2.54) }),
        new("Connector_Generic:Conn_01x04", "Conn_01x04", "Connector_PinHeader_2.54mm:PinHeader_1x04_P2.54mm_Vertical", 50, new[]
        {
            new SchematicPinDefinition("1", -5.08, -2.54), new SchematicPinDefinition("2", -5.08, 0),
            new SchematicPinDefinition("3", -5.08, 2.54), new SchematicPinDefinition("4", -5.08, 5.08)
        }),
        new("Transistor_BJT:Q_NPN_BEC", "Q_NPN_BEC", "Package_TO_SOT_SMD:SOT-23", 50, new[]
        {
            new SchematicPinDefinition("1", -5.08, 0), new SchematicPinDefinition("2", 2.54, 5.08), new SchematicPinDefinition("3", 2.54, -5.08)
        }),
        new("74xGxx:74LVC1G3157", "74LVC1G3157", "Package_TO_SOT_SMD:SOT-23-6", 50, new[]
        {
            new SchematicPinDefinition("1", 10.16, -5.08, 1), new SchematicPinDefinition("3", 10.16, 5.08, 1),
            new SchematicPinDefinition("4", -10.16, 0, 1), new SchematicPinDefinition("6", -10.16, 10.16, 1),
            new SchematicPinDefinition("2", 0, 10.16, 2), new SchematicPinDefinition("5", 0, -10.16, 2)
        }),
        new(
            "Amplifier_Operational:OPA2325",
            "OPA2325",
            "DIP8_300mil",
            45,
            new[]
            {
                new SchematicPinDefinition("1", 7.62, 0, 1),
                new SchematicPinDefinition("2", -7.62, -2.54, 1),
                new SchematicPinDefinition("3", -7.62, 2.54, 1),
                new SchematicPinDefinition("4", -2.54, 7.62, 3),
                new SchematicPinDefinition("5", -7.62, 2.54, 2),
                new SchematicPinDefinition("6", -7.62, -2.54, 2),
                new SchematicPinDefinition("7", 7.62, 0, 2),
                new SchematicPinDefinition("8", -2.54, -7.62, 3)
            }),
        new(
            "Amplifier_Operational:OPA2388",
            "OPA2388",
            "DIP8_300mil",
            45,
            new[]
            {
                new SchematicPinDefinition("1", 7.62, 0, 1),
                new SchematicPinDefinition("2", -7.62, -2.54, 1),
                new SchematicPinDefinition("3", -7.62, 2.54, 1),
                new SchematicPinDefinition("4", -2.54, 7.62, 3),
                new SchematicPinDefinition("5", -7.62, 2.54, 2),
                new SchematicPinDefinition("6", -7.62, -2.54, 2),
                new SchematicPinDefinition("7", 7.62, 0, 2),
                new SchematicPinDefinition("8", -2.54, -7.62, 3)
            })
    };

    public static SchematicSymbolCatalogEntry? Find(string symbolId)
    {
        return Entries.FirstOrDefault(entry => string.Equals(entry.SymbolId, symbolId, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class SchematicFootprintTemplates
{
    private static readonly string[] KiCadFootprintLibraryRoots =
    {
        @"D:\Program Files\KiCad\10.0\share\kicad\footprints",
        @"C:\Program Files\KiCad\10.0\share\kicad\footprints"
    };

    public static bool IsSupported(string footprint)
    {
        return footprint is "R_Axial_2Pad" or "C_Disc_2Pad" or "LED_2Pad" or "Photodiode_2Pad" or "BatteryHolder_2Pad_Back" or "DIP8_300mil"
            || ResolveKiCadFootprintPath(footprint) is not null;
    }

    public static string Format(string footprint, string reference, string value, double x, double y, IReadOnlyDictionary<string, KiCadNet> padNets, double? rotationDegrees = null)
    {
        return footprint switch
        {
            "R_Axial_2Pad" => FormatTwoPad("R_Axial_2Pad", reference, value, x, y, rotationDegrees, "F.Cu", new[] { ("1", 0.0, "1"), ("2", 10.16, "2") }, padNets),
            "C_Disc_2Pad" => FormatTwoPad("C_Disc_2Pad", reference, value, x, y, rotationDegrees, "F.Cu", new[] { ("1", 0.0, "1"), ("2", 5.08, "2") }, padNets),
            "LED_2Pad" => FormatTwoPad("LED_2Pad", reference, value, x, y, rotationDegrees, "F.Cu", new[] { ("1", -2.54, "K"), ("2", 2.54, "A") }, padNets),
            "Photodiode_2Pad" => FormatTwoPad("Photodiode_2Pad", reference, value, x, y, rotationDegrees, "F.Cu", new[] { ("1", -2.54, "A"), ("2", 2.54, "K") }, padNets),
            "BatteryHolder_2Pad_Back" => FormatTwoPad("BatteryHolder_2Pad_Back", reference, value, x, y, rotationDegrees, "B.Cu", new[] { ("1", -4.0, "+"), ("2", 4.0, "-") }, padNets),
            "DIP8_300mil" => FormatDip8(reference, value, x, y, rotationDegrees, padNets),
            _ => FormatKiCadLibraryFootprint(footprint, reference, value, x, y, rotationDegrees, padNets)
        };
    }

    private static string FormatKiCadLibraryFootprint(string footprint, string reference, string value, double x, double y, double? rotationDegrees, IReadOnlyDictionary<string, KiCadNet> padNets)
    {
        var path = ResolveKiCadFootprintPath(footprint);
        if (path is null)
        {
            return string.Empty;
        }

        var text = File.ReadAllText(path);
        var footprintName = footprint.Contains(':', StringComparison.Ordinal) ? footprint : Path.GetFileNameWithoutExtension(path);
        text = Regex.Replace(text, "^\\(footprint\\s+\"[^\"]+\"", $"(footprint \"{footprintName}\"");
        text = ReplaceFirstTopLevelLine(text, "uuid", $"\t(uuid \"{Guid.NewGuid()}\")", insertAfterHead: "layer");
        var atText = rotationDegrees is null
            ? $"\t(at {KiCadBoardParser.FormatNumber(x)} {KiCadBoardParser.FormatNumber(y)})"
            : $"\t(at {KiCadBoardParser.FormatNumber(x)} {KiCadBoardParser.FormatNumber(y)} {KiCadBoardParser.FormatNumber(rotationDegrees.Value)})";
        text = ReplaceFirstTopLevelLine(text, "at", atText, insertAfterHead: "uuid");
        text = ReplacePropertyValue(text, "Reference", reference);
        text = ReplacePropertyValue(text, "Value", value);
        text = AddPadNets(text, padNets);
        return NormalizeIndentForBoard(text).TrimEnd() + Environment.NewLine;
    }

    private static string? ResolveKiCadFootprintPath(string footprint)
    {
        var separator = footprint.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == footprint.Length - 1)
        {
            return null;
        }

        var library = footprint[..separator];
        var name = footprint[(separator + 1)..];
        foreach (var root in KiCadFootprintLibraryRoots)
        {
            var candidate = Path.Combine(root, $"{library}.pretty", $"{name}.kicad_mod");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ReplaceFirstTopLevelLine(string text, string head, string replacement, string insertAfterHead)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var targetPrefix = $"\t({head} ";
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(targetPrefix, StringComparison.Ordinal))
            {
                lines[i] = replacement;
                return string.Join(Environment.NewLine, lines);
            }
        }

        var insertPrefix = $"\t({insertAfterHead} ";
        var insertAt = lines.FindIndex(1, line => line.StartsWith(insertPrefix, StringComparison.Ordinal));
        lines.Insert(insertAt < 0 ? 1 : insertAt + 1, replacement);
        return string.Join(Environment.NewLine, lines);
    }

    private static string ReplacePropertyValue(string text, string propertyName, string value)
    {
        return Regex.Replace(
            text,
            $"(\\(property\\s+\"{Regex.Escape(propertyName)}\"\\s+)\"[^\"]*\"",
            match => match.Groups[1].Value + $"\"{EscapeKiCadString(value)}\"",
            RegexOptions.Singleline);
    }

    private static string AddPadNets(string text, IReadOnlyDictionary<string, KiCadNet> padNets)
    {
        return Regex.Replace(text, "(?ms)^\\t\\(pad\\s+\"(?<pad>[^\"]*)\".*?^\\t\\)", match =>
        {
            var pad = match.Groups["pad"].Value;
            if (string.IsNullOrWhiteSpace(pad) || !padNets.TryGetValue(pad, out var net))
            {
                return match.Value;
            }

            var withoutNet = Regex.Replace(match.Value, "(?ms)^\\t\\t\\(net\\s+\\d+\\s+\"[^\"]*\"\\)\\r?\\n?", string.Empty);
            var insertAt = withoutNet.LastIndexOf("\n\t)", StringComparison.Ordinal);
            if (insertAt < 0)
            {
                return withoutNet;
            }

            return withoutNet.Insert(insertAt, $"\n\t\t(net {net.Code} \"{EscapeKiCadString(net.Name)}\")");
        });
    }

    private static string NormalizeIndentForBoard(string text)
    {
        return "  " + text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", Environment.NewLine + "  ", StringComparison.Ordinal);
    }

    private static string EscapeKiCadString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string FormatDip8(string reference, string value, double x, double y, double? rotationDegrees, IReadOnlyDictionary<string, KiCadNet> padNets)
    {
        var atText = rotationDegrees is null
            ? $"    (at {KiCadBoardParser.FormatNumber(x)} {KiCadBoardParser.FormatNumber(y)})"
            : $"    (at {KiCadBoardParser.FormatNumber(x)} {KiCadBoardParser.FormatNumber(y)} {KiCadBoardParser.FormatNumber(rotationDegrees.Value)})";

        return string.Join(Environment.NewLine, new[]
        {
            "  (footprint \"DIP8_300mil\"",
            "    (layer \"F.Cu\")",
            $"    (uuid \"{Guid.NewGuid()}\")",
            atText,
            $"    (property \"Reference\" \"{reference}\" (at 3.81 -3.5 0) (layer \"F.SilkS\") (effects (font (size 1 1) (thickness 0.1))))",
            $"    (property \"Value\" \"{value}\" (at 3.81 11.2 0) (layer \"F.Fab\") (effects (font (size 1 1) (thickness 0.1))))",
            FormatDipPad("1", 0, 0, "1", padNets),
            FormatDipPad("2", 0, 2.54, "2", padNets),
            FormatDipPad("3", 0, 5.08, "3", padNets),
            FormatDipPad("4", 0, 7.62, "4", padNets),
            FormatDipPad("5", 7.62, 7.62, "5", padNets),
            FormatDipPad("6", 7.62, 5.08, "6", padNets),
            FormatDipPad("7", 7.62, 2.54, "7", padNets),
            FormatDipPad("8", 7.62, 0, "8", padNets),
            "  )",
            string.Empty
        });
    }

    private static string FormatTwoPad(string name, string reference, string value, double x, double y, double? rotationDegrees, string layer, IReadOnlyList<(string Pad, double X, string Pin)> pads, IReadOnlyDictionary<string, KiCadNet> padNets)
    {
        var padText = string.Concat(pads.Select(pad =>
        {
            padNets.TryGetValue(pad.Pin, out var net);
            var netText = net is null ? string.Empty : $"{Environment.NewLine}      (net {net.Code} \"{net.Name}\")";
            return string.Join(Environment.NewLine, new[]
            {
                $"    (pad \"{pad.Pad}\" thru_hole circle",
                $"      (at {KiCadBoardParser.FormatNumber(pad.X)} 0)",
                "      (size 2.54 2.54)",
                "      (drill 0.8)",
                "      (layers \"*.Cu\" \"*.Mask\")" + netText,
                $"      (pinfunction \"{pad.Pin}\")",
                "    )",
                string.Empty
            });
        }));
        var atText = rotationDegrees is null
            ? $"    (at {KiCadBoardParser.FormatNumber(x)} {KiCadBoardParser.FormatNumber(y)})"
            : $"    (at {KiCadBoardParser.FormatNumber(x)} {KiCadBoardParser.FormatNumber(y)} {KiCadBoardParser.FormatNumber(rotationDegrees.Value)})";

        return string.Join(Environment.NewLine, new[]
        {
            $"  (footprint \"{name}\"",
            $"    (layer \"{layer}\")",
            $"    (uuid \"{Guid.NewGuid()}\")",
            atText,
            $"    (property \"Reference\" \"{reference}\" (at 0 -3 0) (layer \"F.SilkS\") (effects (font (size 1 1) (thickness 0.1))))",
            $"    (property \"Value\" \"{value}\" (at 0 3 0) (layer \"F.Fab\") (effects (font (size 1 1) (thickness 0.1))))",
            padText.TrimEnd(),
            "  )",
            string.Empty
        });
    }

    private static string FormatDipPad(string padName, double x, double y, string pinFunction, IReadOnlyDictionary<string, KiCadNet> padNets)
    {
        padNets.TryGetValue(pinFunction, out var net);
        var netText = net is null ? string.Empty : $"{Environment.NewLine}      (net {net.Code} \"{net.Name}\")";
        return string.Join(Environment.NewLine, new[]
        {
            $"    (pad \"{padName}\" thru_hole oval",
            $"      (at {KiCadBoardParser.FormatNumber(x)} {KiCadBoardParser.FormatNumber(y)})",
            "      (size 1.6 2.2)",
            "      (drill 0.8)",
            "      (layers \"*.Cu\" \"*.Mask\")" + netText,
            $"      (pinfunction \"{pinFunction}\")",
            "    )"
        });
    }
}
