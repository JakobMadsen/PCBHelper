namespace PCBHelper.Core;

public sealed class SchematicAuthoringService
{
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
                symbol.Properties.TryGetValue("Value", out var value) ? value.Value : null,
                symbol.Properties.TryGetValue("Footprint", out var footprint) ? footprint.Value : null,
                symbol.XMillimeters,
                symbol.YMillimeters,
                symbol.Properties.Select(static item => new SchematicFieldSummary(item.Key, item.Value.Value)).ToArray()))
            .ToArray();
        return ToolResponse<SchematicSymbolListResult>.Ok($"Found {symbols.Length} schematic symbol(s).", new SchematicSymbolListResult(schematic.Data.SchematicFile, symbols, schematic.Data.Wires.Count, schematic.Data.Labels.Count));
    }

    public ToolResponse<SchematicMutationResult> CreateSymbol(string projectPath, string symbol, string reference, double x, double y, string? value, string? footprint, bool dryRun)
    {
        var catalog = SchematicSymbolCatalog.Find(symbol);
        if (catalog is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Unsupported schematic symbol: {symbol}", "SCHEMATIC_SYMBOL_UNSUPPORTED");
        }

        var schematic = LoadSchematic(projectPath);
        if (!schematic.Success || schematic.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(schematic.Summary, schematic.Error?.Code ?? "SCHEMATIC_LOAD_FAILED", schematic.Error?.Message);
        }

        if (schematic.Data.Symbols.Any(existing => string.Equals(existing.Reference, reference, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic symbol already exists: {reference}", "SCHEMATIC_SYMBOL_EXISTS");
        }

        var footprintValue = footprint ?? catalog.DefaultFootprint;
        var text = FormatSymbol(catalog, reference, value ?? catalog.DefaultValue, footprintValue, x, y);
        var after = InsertBeforeSymbolInstances(schematic.Data.Text, text);
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

        var symbol = FindSymbol(schematic.Data, reference);
        if (symbol is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail($"Schematic symbol not found: {reference}", "SCHEMATIC_SYMBOL_NOT_FOUND");
        }

        var before = schematic.Data.Text;
        string after;
        if (symbol.Properties.TryGetValue(field, out var property))
        {
            after = before.Remove(property.ValueStart, property.ValueLength).Insert(property.ValueStart, value);
        }
        else
        {
            var propertyText = FormatProperty(field, value, symbol.XMillimeters ?? 0, (symbol.YMillimeters ?? 0) + 5);
            after = before.Insert(symbol.SourceStart + symbol.SourceLength - 1, propertyText);
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

        var cornerX = toPin.Data.X;
        var cornerY = fromPin.Data.Y;
        var addition = FormatWireIfNeeded(fromPin.Data.X, fromPin.Data.Y, cornerX, cornerY)
            + FormatWireIfNeeded(cornerX, cornerY, toPin.Data.X, toPin.Data.Y)
            + (string.IsNullOrWhiteSpace(net) ? string.Empty : FormatLabel(net, (fromPin.Data.X + toPin.Data.X) / 2, (fromPin.Data.Y + toPin.Data.Y) / 2));
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
        var symbols = schematic.Data.Symbols.Where(static symbol => symbol.Reference is not null).ToArray();
        var labelNames = schematic.Data.Labels.Select(static label => label.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var nets = labelNames.Select((name, index) => new KiCadNet(index + 1, name)).ToArray();
        var nextX = 45.0;
        var footprints = new List<string>();

        foreach (var symbol in symbols)
        {
            if (board.Footprints.Any(footprint => string.Equals(footprint.Reference, symbol.Reference, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

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
            footprints.Add(SchematicFootprintTemplates.Format(footprint, symbol.Reference!, value, nextX, catalog.DefaultBoardY, padNets));
            nextX += 20;
        }

        var after = RebuildBoard(boardBefore, nets, footprints);
        if (!dryRun)
        {
            File.WriteAllText(project.Data.BoardFile, after);
        }

        return Mutation("update-pcb-from-schematic", project.Data.ProjectName, dryRun, new[] { new ChangeFileSnapshot(project.Data.BoardFile, boardBefore, after) }, $"Created {footprints.Count} footprint(s).");
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

    private static KiCadSchematicSymbol? FindSymbol(KiCadSchematicDocument schematic, string reference)
    {
        return schematic.Symbols.FirstOrDefault(symbol => string.Equals(symbol.Reference, reference, StringComparison.OrdinalIgnoreCase));
    }

    private static ToolResponse<ResolvedSchematicPin> ResolvePin(KiCadSchematicDocument schematic, string pinReference)
    {
        var parts = pinReference.Split('.', 2);
        if (parts.Length != 2)
        {
            return ToolResponse<ResolvedSchematicPin>.Fail($"Pin reference must be <ref.pin>: {pinReference}", "SCHEMATIC_PIN_NOT_FOUND");
        }

        var symbol = FindSymbol(schematic, parts[0]);
        if (symbol is null || symbol.LibId is null || symbol.XMillimeters is null || symbol.YMillimeters is null)
        {
            return ToolResponse<ResolvedSchematicPin>.Fail($"Schematic symbol not found: {parts[0]}", "SCHEMATIC_PIN_NOT_FOUND");
        }

        var catalog = SchematicSymbolCatalog.Find(symbol.LibId);
        var pin = catalog?.Pins.FirstOrDefault(item => string.Equals(item.Name, parts[1], StringComparison.OrdinalIgnoreCase));
        if (pin is null)
        {
            return ToolResponse<ResolvedSchematicPin>.Fail($"Schematic pin not found: {pinReference}", "SCHEMATIC_PIN_NOT_FOUND");
        }

        return ToolResponse<ResolvedSchematicPin>.Ok("Resolved pin.", new ResolvedSchematicPin(parts[0], parts[1], symbol.XMillimeters.Value + pin.OffsetX, symbol.YMillimeters.Value + pin.OffsetY));
    }

    private static IReadOnlyDictionary<string, KiCadNet> AssignPadNets(string reference, SchematicSymbolCatalogEntry catalog, KiCadSchematicDocument schematic, IReadOnlyList<KiCadNet> nets)
    {
        var result = new Dictionary<string, KiCadNet>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in schematic.Labels)
        {
            var net = nets.First(item => string.Equals(item.Name, label.Text, StringComparison.OrdinalIgnoreCase));
            foreach (var pin in catalog.Pins)
            {
                var pinPoint = ResolvePin(schematic, $"{reference}.{pin.Name}");
                if (pinPoint.Data is null)
                {
                    continue;
                }

                if (schematic.Wires.Any(wire => PointOnWire(pinPoint.Data.X, pinPoint.Data.Y, wire) && PointOnWire(label.XMillimeters, label.YMillimeters, wire)))
                {
                    result[pin.Name] = net;
                }
            }
        }

        return result;
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

    private static string FormatSymbol(SchematicSymbolCatalogEntry catalog, string reference, string value, string footprint, double x, double y)
    {
        var uuid = Guid.NewGuid().ToString();
        return string.Join(Environment.NewLine, new[]
        {
            "  (symbol",
            $"    (lib_id \"{catalog.SymbolId}\")",
            $"    (at {KiCadSchematicParser.FormatNumber(x)} {KiCadSchematicParser.FormatNumber(y)} 0)",
            "    (unit 1)",
            "    (exclude_from_sim no)",
            "    (in_bom yes)",
            "    (on_board yes)",
            "    (dnp no)",
            $"    (uuid \"{uuid}\")",
            FormatProperty("Reference", reference, x, y - 3).TrimEnd(),
            FormatProperty("Value", value, x, y + 3).TrimEnd(),
            FormatProperty("Footprint", footprint, x, y + 5).TrimEnd(),
            "  )",
            string.Empty
        });
    }

    private static string FormatProperty(string name, string value, double x, double y)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"    (property \"{name}\" \"{value}\"",
            $"      (at {KiCadSchematicParser.FormatNumber(x)} {KiCadSchematicParser.FormatNumber(y)} 0)",
            "      (effects",
            "        (font",
            "          (size 1.27 1.27)",
            "        )",
            "      )",
            "    )",
            string.Empty
        });
    }

    private static string FormatWire(double x1, double y1, double x2, double y2)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "  (wire",
            $"    (pts (xy {KiCadSchematicParser.FormatNumber(x1)} {KiCadSchematicParser.FormatNumber(y1)}) (xy {KiCadSchematicParser.FormatNumber(x2)} {KiCadSchematicParser.FormatNumber(y2)}))",
            "    (stroke (width 0) (type default))",
            $"    (uuid \"{Guid.NewGuid()}\")",
            "  )",
            string.Empty
        });
    }

    private static string FormatWireIfNeeded(double x1, double y1, double x2, double y2)
    {
        return Math.Abs(x1 - x2) < 0.001 && Math.Abs(y1 - y2) < 0.001
            ? string.Empty
            : FormatWire(x1, y1, x2, y2);
    }

    private static string FormatLabel(string net, double x, double y)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"  (label \"{net}\"",
            $"    (at {KiCadSchematicParser.FormatNumber(x)} {KiCadSchematicParser.FormatNumber(y)} 0)",
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
        var withoutEmbedded = boardText
            .Replace("  (embedded_fonts no)\r\n", string.Empty, StringComparison.Ordinal)
            .Replace("  (embedded_fonts no)\n", string.Empty, StringComparison.Ordinal);
        var lastClose = withoutEmbedded.LastIndexOf(')');
        var prefix = lastClose >= 0 ? withoutEmbedded[..lastClose] : withoutEmbedded;
        var netText = string.Concat(nets.Select(net => $"  (net {net.Code} \"{net.Name}\"){Environment.NewLine}"));
        var footprintText = string.Concat(footprints);
        return prefix + netText + footprintText + "  (embedded_fonts no)" + Environment.NewLine + ")" + Environment.NewLine;
    }
}

public sealed record SchematicSymbolListResult(string SchematicFile, IReadOnlyList<SchematicSymbolSummary> Symbols, int WireCount, int LabelCount);

public sealed record SchematicSymbolSummary(string Reference, string? SymbolId, string? Value, string? Footprint, double? XMillimeters, double? YMillimeters, IReadOnlyList<SchematicFieldSummary> Fields);

public sealed record SchematicFieldSummary(string Name, string Value);

public sealed record SchematicMutationResult(
    string Operation,
    string Reference,
    bool DryRun,
    IReadOnlyList<ChangeFileSnapshot> FileSnapshots,
    string? PreviewText,
    string? ChangeReportPath,
    IReadOnlyList<string> CheckReportPaths);

internal sealed record ResolvedSchematicPin(string Reference, string Pin, double X, double Y);

internal sealed record SchematicSymbolCatalogEntry(string SymbolId, string DefaultValue, string DefaultFootprint, double DefaultBoardY, IReadOnlyList<SchematicPinDefinition> Pins);

internal sealed record SchematicPinDefinition(string Name, double OffsetX, double OffsetY);

internal static class SchematicSymbolCatalog
{
    private static readonly SchematicSymbolCatalogEntry[] Entries =
    {
        new("Device:R", "R", "R_Axial_2Pad", 35, new[] { new SchematicPinDefinition("1", -5.08, 0), new SchematicPinDefinition("2", 5.08, 0) }),
        new("Device:LED", "LED", "LED_2Pad", 35, new[] { new SchematicPinDefinition("A", -1.27, 0), new SchematicPinDefinition("K", 1.27, 0) }),
        new("Device:D", "D", "LED_2Pad", 35, new[] { new SchematicPinDefinition("A", -1.27, 0), new SchematicPinDefinition("K", 1.27, 0) }),
        new("Device:Battery_Cell", "Battery_Cell", "BatteryHolder_2Pad_Back", 50, new[] { new SchematicPinDefinition("+", -4, 0), new SchematicPinDefinition("-", 4, 0) })
    };

    public static SchematicSymbolCatalogEntry? Find(string symbolId)
    {
        return Entries.FirstOrDefault(entry => string.Equals(entry.SymbolId, symbolId, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class SchematicFootprintTemplates
{
    public static bool IsSupported(string footprint)
    {
        return footprint is "R_Axial_2Pad" or "LED_2Pad" or "BatteryHolder_2Pad_Back";
    }

    public static string Format(string footprint, string reference, string value, double x, double y, IReadOnlyDictionary<string, KiCadNet> padNets)
    {
        return footprint switch
        {
            "R_Axial_2Pad" => FormatTwoPad("R_Axial_2Pad", reference, value, x, y, "F.Cu", new[] { ("1", 0.0, "1"), ("2", 10.16, "2") }, padNets),
            "LED_2Pad" => FormatTwoPad("LED_2Pad", reference, value, x, y, "F.Cu", new[] { ("1", -1.27, "A"), ("2", 1.27, "K") }, padNets),
            "BatteryHolder_2Pad_Back" => FormatTwoPad("BatteryHolder_2Pad_Back", reference, value, x, y, "B.Cu", new[] { ("1", -4.0, "+"), ("2", 4.0, "-") }, padNets),
            _ => string.Empty
        };
    }

    private static string FormatTwoPad(string name, string reference, string value, double x, double y, string layer, IReadOnlyList<(string Pad, double X, string Pin)> pads, IReadOnlyDictionary<string, KiCadNet> padNets)
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

        return string.Join(Environment.NewLine, new[]
        {
            $"  (footprint \"{name}\"",
            $"    (layer \"{layer}\")",
            $"    (uuid \"{Guid.NewGuid()}\")",
            $"    (at {KiCadBoardParser.FormatNumber(x)} {KiCadBoardParser.FormatNumber(y)})",
            $"    (property \"Reference\" \"{reference}\" (at 0 -3 0) (layer \"F.SilkS\") (effects (font (size 1 1) (thickness 0.1))))",
            $"    (property \"Value\" \"{value}\" (at 0 3 0) (layer \"F.Fab\") (effects (font (size 1 1) (thickness 0.1))))",
            padText.TrimEnd(),
            "  )",
            string.Empty
        });
    }
}
