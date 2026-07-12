using System.Text.Json;

namespace PCBHelper.Core;

public sealed class DesignIntentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ProjectDiscoveryService _projects;
    private readonly BoardInspectionService _boards;

    public DesignIntentService(ProjectDiscoveryService projects, BoardInspectionService boards)
    {
        _projects = projects;
        _boards = boards;
    }

    public ToolResponse<DesignIntentValidationResult> Validate(string projectPath)
    {
        var loaded = Load(projectPath);
        if (!loaded.Success || loaded.Data is null)
            return ToolResponse<DesignIntentValidationResult>.Fail(loaded.Summary, loaded.Error?.Code ?? "DESIGN_INTENT_UNAVAILABLE", loaded.Error?.Message);

        var errors = ValidateDocument(loaded.Data.Intent).ToArray();
        return ToolResponse<DesignIntentValidationResult>.Ok(
            errors.Length == 0 ? "Design intent is valid." : $"Design intent contains {errors.Length} validation error(s).",
            new DesignIntentValidationResult(loaded.Data.IntentPath, errors.Length == 0, errors));
    }

    public ToolResponse<DesignIntentReport> Analyze(string projectPath)
    {
        var loaded = Load(projectPath);
        if (!loaded.Success || loaded.Data is null)
            return ToolResponse<DesignIntentReport>.Fail(loaded.Summary, loaded.Error?.Code ?? "DESIGN_INTENT_UNAVAILABLE", loaded.Error?.Message);

        var validationErrors = ValidateDocument(loaded.Data.Intent).ToArray();
        if (validationErrors.Length > 0)
            return ToolResponse<DesignIntentReport>.Fail("Design intent is invalid.", "DESIGN_INTENT_INVALID", string.Join(" ", validationErrors));

        var graph = BuildGraph(loaded.Data.Schematic);
        var findings = new List<DesignIntentFinding>();
        CheckDeclaredNets(graph, loaded.Data.Intent, findings);
        CheckKnownSymbols(graph, findings);
        CheckLeds(graph, findings);
        CheckIcDecoupling(graph, loaded.Data.Intent, findings);
        CheckOpAmps(graph, findings);
        CheckI2c(graph, loaded.Data.Intent, findings);
        CheckAdcRanges(loaded.Data.Intent, findings);
        CheckConnectors(graph, loaded.Data.Intent, findings);
        CheckEvidence(graph, loaded.Data.Intent, findings);
        CheckTestAccess(projectPath, loaded.Data.Intent, findings);

        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
        var output = Path.Combine(loaded.Data.ProjectRoot, ".pcbhelper", "intent-runs", runId);
        Directory.CreateDirectory(output);
        var normalized = Path.Combine(output, "normalized-intent.json");
        var findingsPath = Path.Combine(output, "findings.json");
        var reportPath = Path.Combine(output, "report.json");
        File.WriteAllText(normalized, JsonSerializer.Serialize(loaded.Data.Intent, JsonOptions));
        File.WriteAllText(findingsPath, JsonSerializer.Serialize(findings, JsonOptions));

        var proven = findings.Count(item => item.Outcome == DesignIntentOutcome.Proven);
        var notProven = findings.Count(item => item.Outcome == DesignIntentOutcome.NotProven);
        var errors = findings.Count(item => item.Severity == DesignIntentSeverity.Error && item.Outcome == DesignIntentOutcome.NotProven);
        var report = new DesignIntentReport(runId, loaded.Data.IntentPath, reportPath, graph, findings, errors == 0, proven, notProven,
            PlainLanguageSummary(errors, notProven), new[] { normalized, findingsPath, reportPath });
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
        return ToolResponse<DesignIntentReport>.Ok(report.Passed ? "Design intent verification passed." : $"Design intent verification found {errors} blocking error(s).", report);
    }

    public ToolResponse<DesignIntentReport> GetReport(string projectPath, string runId)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<DesignIntentReport>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        if (string.IsNullOrWhiteSpace(runId) || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || runId.Contains("..", StringComparison.Ordinal))
            return ToolResponse<DesignIntentReport>.Fail("Invalid design-intent run id.", "DESIGN_INTENT_REPORT_NOT_FOUND");
        var path = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "intent-runs", runId, "report.json");
        if (!File.Exists(path)) return ToolResponse<DesignIntentReport>.Fail("Design-intent report was not found.", "DESIGN_INTENT_REPORT_NOT_FOUND");
        try
        {
            var report = JsonSerializer.Deserialize<DesignIntentReport>(File.ReadAllText(path), JsonOptions);
            return report is null ? ToolResponse<DesignIntentReport>.Fail("Design-intent report is invalid.", "DESIGN_INTENT_REPORT_INVALID")
                : ToolResponse<DesignIntentReport>.Ok("Loaded design-intent report.", report);
        }
        catch (JsonException exception) { return ToolResponse<DesignIntentReport>.Fail("Design-intent report is invalid.", "DESIGN_INTENT_REPORT_INVALID", exception.Message); }
    }

    public ToolResponse<DesignIntentMutationResult> SetIntent(string projectPath, JsonElement intent, bool dryRun)
    {
        DesignIntentDocument? parsed;
        try { parsed = JsonSerializer.Deserialize<DesignIntentDocument>(intent.GetRawText(), JsonOptions); }
        catch (JsonException exception) { return ToolResponse<DesignIntentMutationResult>.Fail("Design intent JSON is invalid.", "DESIGN_INTENT_INVALID", exception.Message); }
        if (parsed is null) return ToolResponse<DesignIntentMutationResult>.Fail("Design intent JSON is invalid.", "DESIGN_INTENT_INVALID");
        var errors = ValidateDocument(parsed).ToArray();
        if (errors.Length > 0) return ToolResponse<DesignIntentMutationResult>.Fail("Design intent is invalid.", "DESIGN_INTENT_INVALID", string.Join(" ", errors));
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null) return ToolResponse<DesignIntentMutationResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        var directory = Path.Combine(project.Data.ProjectRoot, ".pcbhelper");
        var path = Path.Combine(directory, "design-intent.json");
        var normalized = JsonSerializer.Serialize(parsed, JsonOptions) + Environment.NewLine;
        if (!dryRun) { Directory.CreateDirectory(directory); File.WriteAllText(path, normalized); }
        return ToolResponse<DesignIntentMutationResult>.Ok($"{(dryRun ? "Previewed" : "Updated")} project design intent.", new(path, dryRun, normalized));
    }

    private ToolResponse<LoadedIntent> Load(string projectPath)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null) return ToolResponse<LoadedIntent>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        if (project.Data.SchematicFile is null) return ToolResponse<LoadedIntent>.Fail("A schematic is required for design-intent analysis.", "SCHEMATIC_NOT_FOUND");
        var path = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "design-intent.json");
        if (!File.Exists(path)) return ToolResponse<LoadedIntent>.Fail("No .pcbhelper/design-intent.json exists. Intent-dependent checks are unavailable, not passed.", "DESIGN_INTENT_UNAVAILABLE");
        try
        {
            var intent = JsonSerializer.Deserialize<DesignIntentDocument>(File.ReadAllText(path), JsonOptions);
            return intent is null ? ToolResponse<LoadedIntent>.Fail("Design intent JSON is empty.", "DESIGN_INTENT_INVALID")
                : ToolResponse<LoadedIntent>.Ok("Loaded design intent.", new(project.Data.ProjectRoot, path, intent, KiCadSchematicParser.Parse(project.Data.SchematicFile)));
        }
        catch (JsonException exception) { return ToolResponse<LoadedIntent>.Fail("Design intent JSON is invalid.", "DESIGN_INTENT_INVALID", exception.Message); }
    }

    private static IEnumerable<string> ValidateDocument(DesignIntentDocument intent)
    {
        if (intent.Version != 1) yield return "Only design-intent version 1 is supported.";
        if (intent.Supplies.Count + intent.Signals.Count + intent.Connectors.Count + intent.Components.Count == 0)
            yield return "Design intent must declare at least one supply, signal, connector, or component evidence entry.";
        foreach (var signal in intent.Signals)
        {
            if (string.IsNullOrWhiteSpace(signal.Net) || string.IsNullOrWhiteSpace(signal.Role)) yield return "Every signal requires net and role.";
            if (signal.MinVoltage is not null && signal.MaxVoltage is not null && signal.MinVoltage > signal.MaxVoltage) yield return $"Signal {signal.Net} has minVoltage above maxVoltage.";
        }
        foreach (var supply in intent.Supplies)
            if (string.IsNullOrWhiteSpace(supply.Net) || supply.MinVoltage > supply.NominalVoltage || supply.NominalVoltage > supply.MaxVoltage) yield return $"Supply {supply.Net} has invalid voltage limits.";
        foreach (var component in intent.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Reference)) yield return "Every component evidence entry requires reference.";
            foreach (var rating in component.Ratings)
                if (rating.Maximum <= 0 || string.IsNullOrWhiteSpace(rating.Kind) || string.IsNullOrWhiteSpace(rating.Unit)) yield return $"Component {component.Reference} has an invalid rating.";
        }
    }

    private static DesignNetGraph BuildGraph(KiCadSchematicDocument schematic)
    {
        var connectivity = SchematicConnectivity.Build(schematic);
        var components = new List<DesignGraphComponent>();
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in schematic.Symbols.Where(s => !string.IsNullOrWhiteSpace(s.Reference)).GroupBy(s => s.Reference!, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var catalog = first.LibId is null ? null : SchematicSymbolCatalog.Find(first.LibId);
            if (catalog is null) { unknown.Add(first.Reference!); components.Add(new(first.Reference!, first.LibId ?? "", Value(first), Array.Empty<DesignGraphPin>())); continue; }
            var pins = new List<DesignGraphPin>();
            foreach (var definition in catalog.Pins)
            {
                var symbol = group.FirstOrDefault(item => item.Unit == definition.Unit);
                if (symbol?.XMillimeters is null || symbol.YMillimeters is null) continue;
                var x = Snap(symbol.XMillimeters.Value + definition.OffsetX);
                var y = Snap(symbol.YMillimeters.Value + definition.OffsetY);
                pins.Add(new(definition.Name, connectivity.NetNamesAtPoint(x, y).FirstOrDefault(), x, y));
            }
            components.Add(new(first.Reference!, first.LibId!, Value(first), pins));
        }
        var nets = components.SelectMany(c => c.Pins.Where(p => !string.IsNullOrWhiteSpace(p.Net)).Select(p => p.Net!)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new DesignNetGraph(components, nets, unknown.ToArray());
        static string? Value(KiCadSchematicSymbol symbol) => symbol.Properties.TryGetValue("Value", out var value) ? value.Value : null;
        static double Snap(double value) => Math.Round(value / 1.27, MidpointRounding.AwayFromZero) * 1.27;
    }

    private static void CheckKnownSymbols(DesignNetGraph graph, ICollection<DesignIntentFinding> findings)
    {
        foreach (var reference in graph.UnknownSymbolReferences)
            findings.Add(Finding("INTENT-SYMBOL-001", DesignIntentSeverity.Warning, DesignIntentOutcome.NotProven,
                $"PCBHelper does not know the pin map for {reference}.", new[] { reference }, Array.Empty<string>(), "A known symbol and verified pin map.", "Pin connectivity cannot be proven."));
    }

    private static void CheckDeclaredNets(DesignNetGraph graph, DesignIntentDocument intent, ICollection<DesignIntentFinding> findings)
    {
        foreach (var net in intent.Supplies.Select(s => s.Net).Concat(intent.Signals.Select(s => s.Net)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = graph.Nets.Contains(net, StringComparer.OrdinalIgnoreCase);
            findings.Add(Finding("INTENT-NET-001", found ? DesignIntentSeverity.Info : DesignIntentSeverity.Error,
                found ? DesignIntentOutcome.Proven : DesignIntentOutcome.NotProven,
                found ? $"Declared net {net} exists in the schematic graph." : $"Declared net {net} was not found in the schematic graph.",
                Array.Empty<string>(), new[] { net }, "A declared net connected to at least one known schematic pin.", found ? "The net was found." : "No connected known pin uses this net."));
        }
    }

    private static void CheckLeds(DesignNetGraph graph, ICollection<DesignIntentFinding> findings)
    {
        foreach (var led in graph.Components.Where(c => c.SymbolId is "Device:LED" or "Device:D"))
        {
            var nets = led.Pins.Select(p => p.Net).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var resistor = graph.Components.FirstOrDefault(c => c.SymbolId == "Device:R" && c.Pins.Any(p => p.Net is not null && nets.Contains(p.Net)));
            findings.Add(Finding("INTENT-LED-001", resistor is null ? DesignIntentSeverity.Error : DesignIntentSeverity.Info,
                resistor is null ? DesignIntentOutcome.NotProven : DesignIntentOutcome.Proven,
                resistor is null ? $"{led.Reference} has no resistor in either connected net." : $"{led.Reference} is connected through resistor {resistor.Reference}.",
                resistor is null ? new[] { led.Reference } : new[] { led.Reference, resistor.Reference }, nets.Cast<string>().ToArray(), "A current-limiting resistor in the LED current path.", resistor is null ? "No adjacent resistor was found." : "A resistor shares an LED net."));
        }
    }

    private static void CheckIcDecoupling(DesignNetGraph graph, DesignIntentDocument intent, ICollection<DesignIntentFinding> findings)
    {
        var ground = GroundNets(intent);
        var supplies = intent.Supplies.Select(s => s.Net).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ic in graph.Components.Where(c => IsIc(c.SymbolId)))
        {
            var icNets = ic.Pins.Select(p => p.Net).Where(n => n is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var supply = icNets.FirstOrDefault(supplies.Contains);
            if (supply is null) { findings.Add(Finding("INTENT-IC-POWER-001", DesignIntentSeverity.Error, DesignIntentOutcome.NotProven, $"{ic.Reference} has no declared supply net.", new[] { ic.Reference }, icNets.ToArray(), "IC connected to a declared supply.", "No supply connection was found.")); continue; }
            var capacitor = graph.Components.FirstOrDefault(c => c.SymbolId == "Device:C" && Touches(c, supply) && c.Pins.Any(p => p.Net is not null && ground.Contains(p.Net)));
            findings.Add(Finding("INTENT-DECOUPLING-001", capacitor is null ? DesignIntentSeverity.Error : DesignIntentSeverity.Info, capacitor is null ? DesignIntentOutcome.NotProven : DesignIntentOutcome.Proven,
                capacitor is null ? $"{ic.Reference} has no visible decoupling capacitor between {supply} and ground." : $"{ic.Reference} is decoupled by {capacitor.Reference}.",
                capacitor is null ? new[] { ic.Reference } : new[] { ic.Reference, capacitor.Reference }, new[] { supply }, "A capacitor between IC supply and ground.", capacitor is null ? "No matching capacitor was found." : "A matching capacitor was found."));
        }
    }

    private static void CheckOpAmps(DesignNetGraph graph, ICollection<DesignIntentFinding> findings)
    {
        foreach (var opamp in graph.Components.Where(c => c.SymbolId.Contains("Amplifier_Operational", StringComparison.OrdinalIgnoreCase)))
        foreach (var pin in opamp.Pins.Where(p => p.Name is "2" or "3" or "5" or "6"))
            findings.Add(Finding("INTENT-OPAMP-INPUT-001", pin.Net is null ? DesignIntentSeverity.Error : DesignIntentSeverity.Info, pin.Net is null ? DesignIntentOutcome.NotProven : DesignIntentOutcome.Proven,
                pin.Net is null ? $"{opamp.Reference} input pin {pin.Name} is floating." : $"{opamp.Reference} input pin {pin.Name} is connected to {pin.Net}.", new[] { opamp.Reference }, pin.Net is null ? Array.Empty<string>() : new[] { pin.Net }, "Every placed op-amp input is connected.", pin.Net is null ? "No named connected net was found." : "A named net was found."));
    }

    private static void CheckI2c(DesignNetGraph graph, DesignIntentDocument intent, ICollection<DesignIntentFinding> findings)
    {
        foreach (var signal in intent.Signals.Where(s => s.Role is "i2c-sda" or "i2c-scl"))
        {
            var pullup = graph.Components.FirstOrDefault(c => c.SymbolId == "Device:R" && Touches(c, signal.Net) && c.Pins.Any(p => p.Net is not null && intent.Supplies.Any(s => s.Net.Equals(p.Net, StringComparison.OrdinalIgnoreCase))));
            findings.Add(Finding("INTENT-I2C-PULLUP-001", pullup is null ? DesignIntentSeverity.Error : DesignIntentSeverity.Info, pullup is null ? DesignIntentOutcome.NotProven : DesignIntentOutcome.Proven,
                pullup is null ? $"{signal.Net} has no pull-up resistor to a declared supply." : $"{signal.Net} has pull-up {pullup.Reference}.", pullup is null ? Array.Empty<string>() : new[] { pullup.Reference }, new[] { signal.Net }, "An I2C pull-up resistor to a declared supply.", pullup is null ? "No matching resistor was found." : "A matching resistor was found."));
        }
    }

    private static void CheckAdcRanges(DesignIntentDocument intent, ICollection<DesignIntentFinding> findings)
    {
        foreach (var signal in intent.Signals.Where(s => s.Role == "adc-input"))
        {
            var proven = signal.MinVoltage is not null && signal.MaxVoltage is not null && signal.AdcMinVoltage is not null && signal.AdcMaxVoltage is not null && signal.MinVoltage >= signal.AdcMinVoltage && signal.MaxVoltage <= signal.AdcMaxVoltage;
            findings.Add(Finding("INTENT-ADC-RANGE-001", proven ? DesignIntentSeverity.Info : DesignIntentSeverity.Error, proven ? DesignIntentOutcome.Proven : DesignIntentOutcome.NotProven,
                proven ? $"{signal.Net} stays inside the declared ADC range." : $"{signal.Net} is outside, or lacks evidence for, the declared ADC range.", Array.Empty<string>(), new[] { signal.Net }, "Signal min/max inside ADC min/max.", $"Signal [{signal.MinVoltage}, {signal.MaxVoltage}] V; ADC [{signal.AdcMinVoltage}, {signal.AdcMaxVoltage}] V."));
        }
    }

    private static void CheckConnectors(DesignNetGraph graph, DesignIntentDocument intent, ICollection<DesignIntentFinding> findings)
    {
        foreach (var expected in intent.Connectors)
        {
            var connector = graph.Components.FirstOrDefault(c => c.Reference.Equals(expected.Reference, StringComparison.OrdinalIgnoreCase));
            var actual = connector?.Pins.Select(p => p.Net).Where(n => n is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
            var missing = expected.RequiredNets.Where(net => !actual.Contains(net)).ToArray();
            findings.Add(Finding("INTENT-CONNECTOR-001", missing.Length == 0 ? DesignIntentSeverity.Info : DesignIntentSeverity.Error, missing.Length == 0 ? DesignIntentOutcome.Proven : DesignIntentOutcome.NotProven,
                missing.Length == 0 ? $"Connector {expected.Reference} exposes all required nets." : $"Connector {expected.Reference} is missing: {string.Join(", ", missing)}.", new[] { expected.Reference }, expected.RequiredNets, "All declared connector nets present.", missing.Length == 0 ? "All were found." : "Required nets are absent."));
        }
    }

    private static void CheckEvidence(DesignNetGraph graph, DesignIntentDocument intent, ICollection<DesignIntentFinding> findings)
    {
        foreach (var component in intent.Components)
        {
            var isSemiconductor = component.Semiconductor || graph.Components.FirstOrDefault(c => c.Reference.Equals(component.Reference, StringComparison.OrdinalIgnoreCase))?.SymbolId is string id && (id.Contains("Amplifier", StringComparison.OrdinalIgnoreCase) || id.Contains("Transistor", StringComparison.OrdinalIgnoreCase) || id.Contains("74", StringComparison.OrdinalIgnoreCase));
            var identity = !string.IsNullOrWhiteSpace(component.Mpn) && !string.IsNullOrWhiteSpace(component.Manufacturer)
                && !string.IsNullOrWhiteSpace(component.DatasheetRevision) && Uri.TryCreate(component.DatasheetUrl, UriKind.Absolute, out _);
            var evidenceOk = identity && (!isSemiconductor || component.PinMapVerified);
            findings.Add(Finding("INTENT-COMPONENT-EVIDENCE-001", evidenceOk ? DesignIntentSeverity.Info : component.Critical ? DesignIntentSeverity.Error : DesignIntentSeverity.Warning, evidenceOk ? DesignIntentOutcome.Proven : DesignIntentOutcome.NotProven,
                evidenceOk ? $"{component.Reference} has sourced identity and pin-map evidence." : $"{component.Reference} lacks complete identity, datasheet, or pin-map evidence.", new[] { component.Reference }, Array.Empty<string>(), "Manufacturer, MPN, datasheet and semiconductor pin-map evidence.", evidenceOk ? "Evidence is present." : "Evidence is incomplete."));
            foreach (var rating in component.Ratings)
            {
                var marginLimit = rating.Maximum * (1 - rating.MarginPercent / 100.0);
                var proven = !string.IsNullOrWhiteSpace(rating.Source) && rating.ObservedMaximum is not null && rating.ObservedMaximum <= marginLimit;
                findings.Add(Finding("INTENT-RATING-001", proven ? DesignIntentSeverity.Info : component.Critical ? DesignIntentSeverity.Error : DesignIntentSeverity.Warning, proven ? DesignIntentOutcome.Proven : DesignIntentOutcome.NotProven,
                    proven ? $"{component.Reference} {rating.Kind} stays within its derated limit." : $"{component.Reference} {rating.Kind} exceeds or lacks evidence for its derated limit.", new[] { component.Reference }, rating.Net is null ? Array.Empty<string>() : new[] { rating.Net }, $"Observed maximum <= {marginLimit:g4} {rating.Unit} with {rating.MarginPercent:g3}% margin.", $"Observed {rating.ObservedMaximum?.ToString("g4") ?? "unknown"} {rating.Unit}; source {rating.Source ?? "missing"}."));
            }
        }
    }

    private void CheckTestAccess(string projectPath, DesignIntentDocument intent, ICollection<DesignIntentFinding> findings)
    {
        var nets = _boards.ListNets(projectPath);
        if (!nets.Success || nets.Data is null) { findings.Add(Finding("INTENT-TESTPOINT-001", DesignIntentSeverity.Error, DesignIntentOutcome.NotProven, "Board net data is unavailable, so test access cannot be proven.", Array.Empty<string>(), Array.Empty<string>(), "Readable board pads and nets.", nets.Summary)); return; }
        var groundNames = GroundNets(intent);
        var groundPads = nets.Data.Nets.Where(n => groundNames.Contains(n.Name)).SelectMany(n => n.Pads).Where(IsTestPoint).ToArray();
        foreach (var signal in intent.Signals.Where(s => s.RequiredTestpoint))
        {
            var net = nets.Data.Nets.FirstOrDefault(n => n.Name.Equals(signal.Net, StringComparison.OrdinalIgnoreCase));
            var candidates = net?.Pads.Where(IsTestPoint).ToArray() ?? Array.Empty<NetPadSummary>();
            var sideMatches = candidates.Where(p => signal.TestpointSide is null || p.FootprintSide.Equals(signal.TestpointSide, StringComparison.OrdinalIgnoreCase)).ToArray();
            var exposed = sideMatches.Where(p => p.PadLayers.Any(layer => layer.Contains("Mask", StringComparison.OrdinalIgnoreCase) || layer == "*.Mask"))
                .Where(tp => HasPadClearance(tp, nets.Data.Nets, signal.Net)).ToArray();
            var maxDistance = signal.MaxGroundDistanceMm ?? intent.TestAccess.MaxGroundDistanceMm;
            var nearGround = exposed.Any(tp => groundPads.Any(g => Distance(tp, g) <= maxDistance));
            var passed = exposed.Length > 0 && (!intent.TestAccess.RequireGroundCompanion || nearGround);
            var suggestion = passed ? null : JsonSerializer.Serialize(new { type = "add-testpoint", net = signal.Net, reference = "TP?", diameterMm = intent.TestAccess.DefaultDiameterMm });
            findings.Add(new("INTENT-TESTPOINT-001", passed ? DesignIntentSeverity.Info : DesignIntentSeverity.Error, passed ? DesignIntentOutcome.Proven : DesignIntentOutcome.NotProven,
                passed ? $"{signal.Net} has exposed test access and nearby ground." : $"{signal.Net} lacks an exposed testpoint or nearby ground companion.", exposed.Select(p => p.FootprintReference).ToArray(), new[] { signal.Net }, "An exposed testpoint on the requested side with nearby ground.", $"Found {exposed.Length}; ground companion within {maxDistance:g3} mm: {nearGround}.", Array.Empty<string>(), suggestion));
        }
        static bool IsTestPoint(NetPadSummary pad) => pad.FootprintReference.StartsWith("TP", StringComparison.OrdinalIgnoreCase);
        static double Distance(NetPadSummary a, NetPadSummary b) => a.AbsoluteXMillimeters is null || a.AbsoluteYMillimeters is null || b.AbsoluteXMillimeters is null || b.AbsoluteYMillimeters is null ? double.PositiveInfinity : Math.Sqrt(Math.Pow(a.AbsoluteXMillimeters.Value - b.AbsoluteXMillimeters.Value, 2) + Math.Pow(a.AbsoluteYMillimeters.Value - b.AbsoluteYMillimeters.Value, 2));
        static bool HasPadClearance(NetPadSummary testpoint, IReadOnlyList<NetSummary> allNets, string ownNet) => allNets
            .Where(net => !net.Name.Equals(ownNet, StringComparison.OrdinalIgnoreCase))
            .SelectMany(net => net.Pads)
            .All(other => Distance(testpoint, other) >= Radius(testpoint) + Radius(other) + 0.2);
        static double Radius(NetPadSummary pad) => Math.Max(pad.PadSizeXMillimeters ?? 0, pad.PadSizeYMillimeters ?? 0) / 2;
    }

    private static HashSet<string> GroundNets(DesignIntentDocument intent) => intent.Signals.Where(s => s.Role == "ground").Select(s => s.Net).Append("GND").ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static bool Touches(DesignGraphComponent component, string net) => component.Pins.Any(p => string.Equals(p.Net, net, StringComparison.OrdinalIgnoreCase));
    private static bool IsIc(string symbol) => symbol.Contains("Amplifier", StringComparison.OrdinalIgnoreCase) || symbol.StartsWith("74", StringComparison.OrdinalIgnoreCase);
    private static string PlainLanguageSummary(int errors, int notProven) => errors == 0 ? $"No blocking design-intent errors were found. {notProven} item(s) remain unproven and should be reviewed." : $"The design does not yet match all declared requirements: {errors} blocking error(s), {notProven} unproven item(s).";
    private static DesignIntentFinding Finding(string id, DesignIntentSeverity severity, DesignIntentOutcome outcome, string message, IReadOnlyList<string> refs, IReadOnlyList<string> nets, string expected, string observed) => new(id, severity, outcome, message, refs, nets, expected, observed, Array.Empty<string>(), null);
    private sealed record LoadedIntent(string ProjectRoot, string IntentPath, DesignIntentDocument Intent, KiCadSchematicDocument Schematic);
}

public sealed class DesignIntentDocument
{
    public int Version { get; init; }
    public IReadOnlyList<DesignIntentSupply> Supplies { get; init; } = Array.Empty<DesignIntentSupply>();
    public IReadOnlyList<DesignIntentSignal> Signals { get; init; } = Array.Empty<DesignIntentSignal>();
    public IReadOnlyList<DesignIntentConnector> Connectors { get; init; } = Array.Empty<DesignIntentConnector>();
    public IReadOnlyList<DesignIntentComponentEvidence> Components { get; init; } = Array.Empty<DesignIntentComponentEvidence>();
    public DesignIntentTestAccess TestAccess { get; init; } = new();
}
public sealed record DesignIntentSupply(string Net, double MinVoltage, double NominalVoltage, double MaxVoltage);
public sealed record DesignIntentSignal(string Net, string Role, double? MinVoltage = null, double? MaxVoltage = null, double? AdcMinVoltage = null, double? AdcMaxVoltage = null, bool RequiredTestpoint = false, string? TestpointSide = null, double? MaxGroundDistanceMm = null);
public sealed record DesignIntentConnector(string Reference, IReadOnlyList<string> RequiredNets);
public sealed class DesignIntentComponentEvidence
{
    public string Reference { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string? Mpn { get; init; }
    public string? DatasheetUrl { get; init; }
    public string? DatasheetRevision { get; init; }
    public bool PinMapVerified { get; init; }
    public bool Semiconductor { get; init; }
    public bool Critical { get; init; }
    public IReadOnlyList<DesignIntentRating> Ratings { get; init; } = Array.Empty<DesignIntentRating>();
}
public sealed record DesignIntentRating(string Kind, double Maximum, string Unit, string? Source = null, double MarginPercent = 20, double? ObservedMaximum = null, string? Net = null);
public sealed record DesignIntentTestAccess(bool RequireGroundCompanion = true, double MaxGroundDistanceMm = 15, double DefaultDiameterMm = 2);
public sealed record DesignIntentValidationResult(string IntentPath, bool Valid, IReadOnlyList<string> Errors);
public sealed record DesignIntentMutationResult(string IntentPath, bool DryRun, string NormalizedJson);
public sealed record DesignNetGraph(IReadOnlyList<DesignGraphComponent> Components, IReadOnlyList<string> Nets, IReadOnlyList<string> UnknownSymbolReferences);
public sealed record DesignGraphComponent(string Reference, string SymbolId, string? Value, IReadOnlyList<DesignGraphPin> Pins);
public sealed record DesignGraphPin(string Name, string? Net, double XMillimeters, double YMillimeters);
public enum DesignIntentSeverity { Error, Warning, Info }
public enum DesignIntentOutcome { Proven, NotProven, NotApplicable }
public sealed record DesignIntentFinding(string RuleId, DesignIntentSeverity Severity, DesignIntentOutcome Outcome, string Message, IReadOnlyList<string> References, IReadOnlyList<string> Nets, string Expected, string Observed, IReadOnlyList<string> Evidence, string? SuggestedOperationJson);
public sealed record DesignIntentReport(string RunId, string IntentPath, string ReportPath, DesignNetGraph Graph, IReadOnlyList<DesignIntentFinding> Findings, bool Passed, int ProvenCount, int NotProvenCount, string PlainLanguageSummary, IReadOnlyList<string> ArtifactPaths);
