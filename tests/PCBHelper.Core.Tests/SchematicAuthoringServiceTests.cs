using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class SchematicAuthoringServiceTests
{
    private const double SchematicGridMillimeters = 1.27;

    [Fact]
    public void UpdatePcbFromSchematic_Omits_Optional_Tpsm861253_ThermalVias()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "PCBHelper:TPSM861253", "U1", 80, 50, null, null, dryRun: false).Success);

        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);
        var board = File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_pcb"));

        Assert.True(update.Success, update.Error?.Message);
        Assert.Contains("PCBHelper:TPSM861253_RDX_NoThermalVias", board, StringComparison.Ordinal);
        Assert.DoesNotContain("np_thru_hole", board, StringComparison.Ordinal);
        Assert.Contains("(pad \"1\" smd", board, StringComparison.Ordinal);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(board, @"\(clearance 0\.15\)").Count);
    }

    [Theory]
    [InlineData("Amplifier_Operational:OPA1612AxD", "U1", 1)]
    [InlineData("Amplifier_Operational:OPA1612AxD", "U1", 2)]
    [InlineData("Amplifier_Operational:OPA1612AxD", "U1", 3)]
    [InlineData("Regulator_Linear:LM1117-5.0", "U2", 1)]
    [InlineData("Comparator:TLV7011", "U3", 1)]
    [InlineData("Comparator:TLV7031DBV", "U4", 1)]
    [InlineData("Comparator:MCP6561-OT", "U5", 1)]
    [InlineData("Device:R_Potentiometer", "RV1", 1)]
    [InlineData("Connector_Generic:Conn_01x03", "J3", 1)]
    [InlineData("Connector_Generic:Conn_02x07_Odd_Even", "J4", 1)]
    [InlineData("Connector_Generic:Conn_02x10_Odd_Even", "J4", 1)]
    [InlineData("Switch:SW_SPDT", "SW1", 1)]
    [InlineData("Switch:SW_Push", "SW2", 1)]
    [InlineData("74xx:74LS08", "U5", 1)]
    [InlineData("74xx:74LS08", "U5", 5)]
    [InlineData("PCBHelper:TPS2553-1", "U6", 1)]
    [InlineData("PCBHelper:TPS2113A", "U7", 1)]
    [InlineData("PCBHelper:TPSM861253", "U8", 1)]
    [InlineData("PCBHelper:SN74CB3Q3257", "U9", 1)]
    [InlineData("PCBHelper:LSF0204", "U10", 1)]
    [InlineData("74xGxx:74LVC1G86", "U11", 1)]
    [InlineData("74xGxx:74LVC1G08", "U12", 1)]
    [InlineData("PCBHelper:Arduino_UNO_R4_Shield", "J7", 1)]
    public void CreateSymbol_Supports_Radar_Approved_Catalog(string symbol, string reference, int unit)
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        var created = service.CreateSymbol(fixture.Path, symbol, reference, 80, 50, null, null, unit, dryRun: false);

        Assert.True(created.Success, created.Error?.Message);
        Assert.Contains(service.ListSymbols(fixture.Path).Data!.Symbols,
            item => item.Reference == reference && item.SymbolId == symbol && item.Unit == unit);
    }

    [Fact]
    public void CreateSymbol_Writes_Project_Local_Library_For_Custom_Tps2553()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        var created = service.CreateSymbol(fixture.Path, "PCBHelper:TPS2553-1", "U1", 80, 50, null, null, dryRun: false);

        Assert.True(created.Success, created.Error?.Message);
        var library = File.ReadAllText(Path.Combine(fixture.Path, "PCBHelper.kicad_sym"));
        var table = File.ReadAllText(Path.Combine(fixture.Path, "sym-lib-table"));
        Assert.Contains("(symbol \"TPS2553-1\"", library, StringComparison.Ordinal);
        Assert.Contains("${KIPRJMOD}/PCBHelper.kicad_sym", table, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PCBHelper:TPS2113A", "TPS2113A", "STAT", "8", "IN1")]
    [InlineData("PCBHelper:TPSM861253", "TPSM861253", "VIN", "7", "VOS")]
    [InlineData("PCBHelper:SN74CB3Q3257", "SN74CB3Q3257", "S", "16", "VCC")]
    [InlineData("PCBHelper:LSF0204", "LSF0204", "Vref_A", "14", "Vref_B")]
    [InlineData("PCBHelper:Arduino_UNO_R4_Shield", "Arduino_UNO_R4_Shield", "NC", "32", "SCL")]
    public void CreateSymbol_Writes_Datasheet_Pin_Names_For_Project_Local_Power_Parts(
        string symbolId,
        string symbolName,
        string firstPinName,
        string lastPinNumber,
        string lastPinName)
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        var created = service.CreateSymbol(fixture.Path, symbolId, "U1", 80, 50, null, null, dryRun: false);

        Assert.True(created.Success, created.Error?.Message);
        var library = File.ReadAllText(Path.Combine(fixture.Path, "PCBHelper.kicad_sym"));
        Assert.Contains($"(symbol \"{symbolName}\"", library, StringComparison.Ordinal);
        Assert.Contains($"(name \"{firstPinName}\"", library, StringComparison.Ordinal);
        Assert.Contains($"(name \"{lastPinName}\"", library, StringComparison.Ordinal);
        Assert.Contains($"(number \"{lastPinNumber}\"", library, StringComparison.Ordinal);
    }

    [Fact]
    public void ListSymbols_Returns_Connector_Pin_Numbers_Coordinates_And_Nets()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "Connector_Generic:Conn_02x10_Odd_Even", "J1", 110, 70, null, null, dryRun: false).Success);
        Assert.True(service.AddNetLabel(fixture.Path, "PIN_1", 105.41, 59.69, dryRun: false).Success);

        var symbol = Assert.Single(service.ListSymbols(fixture.Path).Data!.Symbols);

        Assert.Equal(20, symbol.Pins.Count);
        var pin1 = Assert.Single(symbol.Pins, pin => pin.Pin == "1");
        Assert.Equal(105.41, pin1.XMillimeters, 2);
        Assert.Equal(59.69, pin1.YMillimeters, 2);
        Assert.Contains("PIN_1", pin1.Nets);
        var pin20 = Assert.Single(symbol.Pins, pin => pin.Pin == "20");
        Assert.Equal(118.11, pin20.XMillimeters, 2);
        Assert.Equal(82.55, pin20.YMillimeters, 2);
    }

    [Fact]
    public void ConnectPins_Resolves_Lm358_Power_Pins_To_Their_Displayed_Positions()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:LM358", "U1", 80, 50, null, null, unit: 1, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:LM358", "U1", 80, 65, null, null, unit: 2, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:LM358", "U1", 80, 80, null, null, unit: 3, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "C1", 110, 65, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "C2", 110, 95, null, null, dryRun: false).Success);

        Assert.True(service.ConnectPins(fixture.Path, "U1.8", "C1.1", "+5V", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.4", "C2.2", "GND", dryRun: false).Success);

        var result = service.ListSymbols(fixture.Path).Data!;
        var powerUnit = Assert.Single(result.Symbols, symbol => symbol.Reference == "U1" && symbol.Unit == 3);
        var pinX = powerUnit.XMillimeters!.Value - 2.54;
        var pin8Y = powerUnit.YMillimeters!.Value - 7.62;
        var pin4Y = powerUnit.YMillimeters.Value + 7.62;
        Assert.Contains(result.Wires, wire => Touches(wire, pinX, pin8Y));
        Assert.Contains(result.Wires, wire => Touches(wire, pinX, pin4Y));

        static bool Touches(SchematicWireSummary wire, double x, double y) =>
            (Math.Abs(wire.X1Millimeters - x) < 0.001 && Math.Abs(wire.Y1Millimeters - y) < 0.001) ||
            (Math.Abs(wire.X2Millimeters - x) < 0.001 && Math.Abs(wire.Y2Millimeters - y) < 0.001);
    }

    [Fact]
    public void MarkPinNoConnect_Adds_An_Idempotent_Marker_At_The_Resolved_Pin()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "74xx:74LS08", "U1", 80, 50, null, null, unit: 3, dryRun: false).Success);

        var first = service.MarkPinNoConnect(fixture.Path, "U1.8", dryRun: false);
        var second = service.MarkPinNoConnect(fixture.Path, "U1.8", dryRun: false);
        var schematic = File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_sch"));
        var pin = Assert.Single(Assert.Single(service.ListSymbols(fixture.Path).Data!.Symbols).Pins, item => item.Pin == "8");
        var marker = Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            schematic,
            @"\(no_connect\s+\(at\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)\)").Cast<System.Text.RegularExpressions.Match>());

        Assert.True(first.Success, first.Error?.Message);
        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(pin.XMillimeters, double.Parse(marker.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Equal(pin.YMillimeters, double.Parse(marker.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 3);
    }

    [Fact]
    public void DeleteSchematicNoConnectByUuid_Removes_Only_The_Targeted_Marker()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.MarkPinNoConnect(fixture.Path, "R1.1", dryRun: false).Success);
        Assert.True(service.MarkPinNoConnect(fixture.Path, "R1.2", dryRun: false).Success);
        var schematicPath = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");
        var markers = service.ListSymbols(fixture.Path).Data!.NoConnects.ToArray();
        Assert.Equal(2, markers.Length);

        var result = service.DeleteSchematicNoConnectByUuid(fixture.Path, markers[0].Uuid!, dryRun: false);
        var after = File.ReadAllText(schematicPath);

        Assert.True(result.Success, result.Error?.Message);
        Assert.DoesNotContain(markers[0].Uuid!, after, StringComparison.Ordinal);
        Assert.Contains(markers[1].Uuid!, after, StringComparison.Ordinal);
        Assert.Single(service.ListSymbols(fixture.Path).Data!.NoConnects);
    }

    [Fact]
    public void MarkPinNoConnect_Rejects_An_Electrically_Connected_Pin()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R2", 90, 50, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.1", "R2.1", "CONNECTED", dryRun: false).Success);

        var result = service.MarkPinNoConnect(fixture.Path, "R1.1", dryRun: false);

        Assert.False(result.Success);
        Assert.Equal("SCHEMATIC_PIN_CONNECTED", result.Error?.Code);
    }

    [Fact]
    public void ConnectPins_Rejects_A_Pin_Marked_NoConnect()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R2", 90, 50, null, null, dryRun: false).Success);
        Assert.True(service.MarkPinNoConnect(fixture.Path, "R1.1", dryRun: false).Success);

        var result = service.ConnectPins(fixture.Path, "R1.1", "R2.1", "INVALID", dryRun: false);

        Assert.False(result.Success);
        Assert.Equal("SCHEMATIC_PIN_NO_CONNECT", result.Error?.Code);
    }

    [Fact]
    public void CreateSymbol_Embeds_SelfContained_Graphics_For_Inherited_Lm358()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        var created = service.CreateSymbol(
            fixture.Path,
            "Amplifier_Operational:LM358",
            "U1",
            80,
            60,
            "LM358",
            null,
            dryRun: false);

        Assert.True(created.Success, created.Error?.Message);
        var schematicText = File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_sch"));
        var definitionStart = schematicText.IndexOf("(symbol \"Amplifier_Operational:LM358\"", StringComparison.Ordinal);
        Assert.True(definitionStart >= 0);
        var definitionEnd = FindMatchingParenthesis(schematicText, definitionStart);
        var definition = schematicText.Substring(definitionStart, definitionEnd - definitionStart + 1);
        Assert.DoesNotContain("(extends ", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("(symbol \"LM2904_", definition, StringComparison.Ordinal);
        Assert.Contains("(symbol \"LM358_1_1\"", definition, StringComparison.Ordinal);
        Assert.Contains("(polyline", definition, StringComparison.Ordinal);
        Assert.Contains("(pin input", definition, StringComparison.Ordinal);
        Assert.Contains("(pin power_in", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceSymbol_Preserves_Reference_Value_Position_And_Wires_For_Compatible_Pins()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "C1", 70, 50, "10uF", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 70, 70, "10k", null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "C1.2", "R1.1", "VMID", dryRun: false).Success);
        var before = service.ListSymbols(fixture.Path).Data!;

        var replaced = service.ReplaceSymbol(fixture.Path, "C1", "Device:C_Polarized", dryRun: false);
        var after = service.ListSymbols(fixture.Path).Data!;

        Assert.True(replaced.Success, replaced.Error?.Message);
        var capacitor = Assert.Single(after.Symbols, item => item.Reference == "C1");
        Assert.Equal("Device:C_Polarized", capacitor.SymbolId);
        Assert.Equal("10uF", capacitor.Value);
        Assert.Equal(before.WireCount, after.WireCount);
        var schematicText = File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_sch"));
        Assert.Contains("(instances", schematicText, StringComparison.Ordinal);
        Assert.Contains("(reference \"C1\")", schematicText, StringComparison.Ordinal);
        Assert.Contains("(pin \"1\"", schematicText, StringComparison.Ordinal);
        Assert.Contains("(pin \"2\"", schematicText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceSymbol_Migrates_A_Legacy_Symbol_Id_To_Its_Project_Local_Catalog_Entry()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "PCBHelper:TPS2553-1", "U2", 110, 115, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 130, 115, "66.5k", null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U2.5", "R1.1", "ILIM", dryRun: false).Success);

        var schematicPath = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");
        var legacyText = File.ReadAllText(schematicPath).Replace(
            "(lib_id \"PCBHelper:TPS2553-1\")",
            "(lib_id \"Power_Management:TPS2553-1\")",
            StringComparison.Ordinal);
        File.WriteAllText(schematicPath, legacyText);
        var before = service.ListSymbols(fixture.Path).Data!;

        var replaced = service.ReplaceSymbol(fixture.Path, "U2", "PCBHelper:TPS2553-1", dryRun: false);
        var after = service.ListSymbols(fixture.Path).Data!;

        Assert.True(replaced.Success, replaced.Error?.Message);
        var migrated = Assert.Single(after.Symbols, item => item.Reference == "U2");
        Assert.Equal("PCBHelper:TPS2553-1", migrated.SymbolId);
        Assert.Equal(before.WireCount, after.WireCount);
        Assert.Contains("(lib_id \"PCBHelper:TPS2553-1\")", File.ReadAllText(schematicPath), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteSymbol_Removes_Only_The_Selected_Symbol()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "C1", 70, 50, "10uF", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 70, 70, "10k", null, dryRun: false).Success);

        var deleted = service.DeleteSymbol(fixture.Path, "C1", dryRun: false);
        var symbols = service.ListSymbols(fixture.Path).Data!.Symbols;

        Assert.True(deleted.Success, deleted.Error?.Message);
        Assert.DoesNotContain(symbols, item => item.Reference == "C1");
        Assert.Contains(symbols, item => item.Reference == "R1");
    }

    [Fact]
    public void Parser_Reads_Symbols_Wires_And_Labels()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false);
        service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false);
        service.ConnectPins(fixture.Path, "R1.2", "D1.A", "LED_A", dryRun: false);
        service.AddNetLabel(fixture.Path, "VCC", 40, 50, dryRun: false);

        var result = service.ListSymbols(fixture.Path);

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Symbols.Count);
        Assert.True(result.Data.WireCount >= 1);
        Assert.Equal(2, result.Data.LabelCount);
        Assert.Contains(result.Data.Symbols, symbol => symbol.Reference == "R1" && symbol.Value == "330R");
    }

    [Fact]
    public void AddSchematicBlockBox_Creates_A_Titled_NonFilled_Human_Review_Boundary()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        var result = service.AddSchematicBlockBox(fixture.Path, "POWER / PROTECTION", 15, 12, 65, 45, dryRun: false);
        var schematic = File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_sch"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("(text_box \"POWER / PROTECTION\"", schematic, StringComparison.Ordinal);
        Assert.Contains("(fill (type none))", schematic, StringComparison.Ordinal);
        Assert.Contains("(bold yes)", schematic, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_Mutations_Do_Not_Change_Files()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var schematicFile = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");
        var boardFile = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");
        var schematicBefore = File.ReadAllText(schematicFile);
        var boardBefore = File.ReadAllText(boardFile);

        var symbol = service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: true);
        var label = service.AddNetLabel(fixture.Path, "VCC", 40, 50, dryRun: true);
        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: true);

        Assert.True(symbol.Success);
        Assert.True(label.Success);
        Assert.True(update.Success);
        Assert.Equal(schematicBefore, File.ReadAllText(schematicFile));
        Assert.Equal(boardBefore, File.ReadAllText(boardFile));
    }

    [Fact]
    public void Real_Mutations_Create_Symbol_Field_Wire_Label_And_Board_Footprints()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:Battery_Cell", "BT1", 30, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.SetSymbolField(fixture.Path, "R1", "Datasheet", "local", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "BT1.+", "R1.1", "VCC", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.2", "D1.A", "LED_A", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "D1.K", "BT1.-", "GND", dryRun: false).Success);
        Assert.True(service.AddNetLabel(fixture.Path, "GND", 60, 54, dryRun: false).Success);

        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);
        var board = new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path);
        var nets = new BoardInspectionService(new ProjectDiscoveryService()).ListNets(fixture.Path);

        Assert.True(update.Success);
        Assert.Contains(board.Data!.Footprints, footprint => footprint.Reference == "BT1");
        Assert.Contains(board.Data.Footprints, footprint => footprint.Reference == "R1");
        Assert.Contains(board.Data.Footprints, footprint => footprint.Reference == "D1");
        Assert.Contains(nets.Data!.Nets, net => net.Name == "LED_A");
    }

    [Fact]
    public void ConnectPins_Accepts_Dot_And_Colon_Pin_References()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);

        var dot = service.ConnectPins(fixture.Path, "R1.1", "R1.2", "LOOP", dryRun: true);
        var colon = service.ConnectPins(fixture.Path, "R1:2", "D1:A", "LED_A", dryRun: false);

        Assert.True(dot.Success);
        Assert.True(colon.Success);
        Assert.True(service.ListSymbols(fixture.Path).Data!.WireCount >= 1);
    }

    [Fact]
    public void ListSymbols_Returns_Wire_And_Label_Details_For_Cleanup()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.2", "D1.A", "SIG", dryRun: false).Success);

        var list = service.ListSymbols(fixture.Path);

        Assert.True(list.Success);
        Assert.Contains(list.Data!.Labels, label => label.Text == "SIG" && !string.IsNullOrWhiteSpace(label.Uuid));
        Assert.Contains(list.Data.Wires, wire => !string.IsNullOrWhiteSpace(wire.Uuid));
    }

    [Fact]
    public void Parser_Reads_KiCad_Multiline_Wire_Point_Lists()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.2", "D1.A", "SIG", dryRun: false).Success);

        var schematicFile = Directory.GetFiles(fixture.Path, "*.kicad_sch").Single();
        var text = File.ReadAllText(schematicFile);
        var compactClose = "))" + Environment.NewLine + "    (stroke";
        var multilineClose = ")" + Environment.NewLine + "    )" + Environment.NewLine + "    (stroke";
        Assert.Contains(compactClose, text, StringComparison.Ordinal);
        File.WriteAllText(schematicFile, text.Replace(compactClose, multilineClose, StringComparison.Ordinal));

        var parsed = service.ListSymbols(fixture.Path);

        Assert.True(parsed.Success, parsed.Error?.Message);
        Assert.NotEmpty(parsed.Data!.Wires);
    }

    [Fact]
    public void ConnectPins_Places_Label_On_A_Created_Wire_Segment()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:D_Photo", "PD1", 40, 90, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 80, 100, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "PD1.A", "U1.2", "TIA_IN", dryRun: false).Success);

        var list = service.ListSymbols(fixture.Path);
        var labels = list.Data!.Labels.Where(item => item.Text == "TIA_IN").ToArray();

        Assert.Single(labels);
        AssertLabelIsOnWire(labels[0], list.Data.Wires);
    }

    [Fact]
    public void Catalog_Pin_Offsets_Are_On_Schematic_Grid()
    {
        var catalogType = typeof(SchematicAuthoringService).Assembly.GetType("PCBHelper.Core.SchematicSymbolCatalog")!;
        var entries = (Array)catalogType
            .GetField("Entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        foreach (var entry in entries)
        {
            var symbolId = (string)entry.GetType().GetProperty("SymbolId")!.GetValue(entry)!;
            var pins = (System.Collections.IEnumerable)entry.GetType().GetProperty("Pins")!.GetValue(entry)!;
            foreach (var pin in pins)
            {
                var name = (string)pin.GetType().GetProperty("Name")!.GetValue(pin)!;
                var offsetX = (double)pin.GetType().GetProperty("OffsetX")!.GetValue(pin)!;
                var offsetY = (double)pin.GetType().GetProperty("OffsetY")!.GetValue(pin)!;

                Assert.True(IsOnSchematicGrid(offsetX), $"{symbolId} pin {name} OffsetX is off grid: {offsetX}");
                Assert.True(IsOnSchematicGrid(offsetY), $"{symbolId} pin {name} OffsetY is off grid: {offsetY}");
            }
        }
    }

    [Fact]
    public void Catalog_Tlv7011_Pin_Map_Matches_Ti_Sot23_5_Datasheet()
    {
        var catalogType = typeof(SchematicAuthoringService).Assembly.GetType("PCBHelper.Core.SchematicSymbolCatalog")!;
        var find = catalogType.GetMethod("Find", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var entry = find.Invoke(null, new object[] { "Comparator:TLV7011" });

        Assert.NotNull(entry);
        var pins = ((System.Collections.IEnumerable)entry!.GetType().GetProperty("Pins")!.GetValue(entry)!)
            .Cast<object>()
            .ToDictionary(
                pin => (string)pin.GetType().GetProperty("Name")!.GetValue(pin)!,
                pin => (
                    X: (double)pin.GetType().GetProperty("OffsetX")!.GetValue(pin)!,
                    Y: (double)pin.GetType().GetProperty("OffsetY")!.GetValue(pin)!));

        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, pins.Keys.Order().ToArray());
        Assert.Equal((7.62, 0), pins["1"]);
        Assert.Equal((-2.54, -7.62), pins["2"]);
        Assert.Equal((-7.62, 2.54), pins["3"]);
        Assert.Equal((-7.62, -2.54), pins["4"]);
        Assert.Equal((-2.54, 7.62), pins["5"]);
    }

    [Theory]
    [InlineData("Connector_Generic:Conn_01x02", "1", 0)]
    [InlineData("Connector_Generic:Conn_01x02", "2", -2.54)]
    [InlineData("Connector_Generic:Conn_01x03", "1", 2.54)]
    [InlineData("Connector_Generic:Conn_01x03", "3", -2.54)]
    [InlineData("Connector_Generic:Conn_01x05", "1", 5.08)]
    [InlineData("Connector_Generic:Conn_01x05", "5", -5.08)]
    [InlineData("Connector_Generic:Conn_02x07_Odd_Even", "1", 7.62)]
    [InlineData("Connector_Generic:Conn_02x07_Odd_Even", "14", -7.62)]
    [InlineData("Connector_Generic:Conn_02x10_Odd_Even", "1", 10.16)]
    [InlineData("Connector_Generic:Conn_02x10_Odd_Even", "20", -12.7)]
    public void Catalog_Connector_Pin_Offsets_Match_KiCad_Standard_Library(string symbolId, string pinName, double expectedY)
    {
        var catalogType = typeof(SchematicAuthoringService).Assembly.GetType("PCBHelper.Core.SchematicSymbolCatalog")!;
        var find = catalogType.GetMethod("Find", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var entry = find.Invoke(null, new object[] { symbolId })!;
        var pins = (System.Collections.IEnumerable)entry.GetType().GetProperty("Pins")!.GetValue(entry)!;
        var pin = pins.Cast<object>().Single(item => (string)item.GetType().GetProperty("Name")!.GetValue(item)! == pinName);

        Assert.Equal(expectedY, (double)pin.GetType().GetProperty("OffsetY")!.GetValue(pin)!, 3);
    }

    [Fact]
    public void CreateSymbol_Snaps_OffGrid_Placement_To_Schematic_Grid()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 40.4, 90.3, "330R", null, dryRun: false).Success);

        var symbol = Assert.Single(service.ListSymbols(fixture.Path).Data!.Symbols);
        Assert.Equal(40.64, symbol.XMillimeters!.Value, precision: 3);
        Assert.Equal(90.17, symbol.YMillimeters!.Value, precision: 3);
        AssertSymbolIsOnSchematicGrid(symbol);
    }

    [Fact]
    public void CreateSymbol_Embeds_LibSymbol_For_Erc_Pin_Connectivity()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var schematicFile = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");

        Assert.True(service.CreateSymbol(fixture.Path, "Device:D_Photo", "PD1", 40, 90, null, null, dryRun: false).Success);

        var text = File.ReadAllText(schematicFile);
        Assert.Contains("(lib_symbols", text);
        Assert.Contains("(symbol \"Device:D_Photo\"", text);
        Assert.Contains("(lib_id \"Device:D_Photo\")", text);
        Assert.Single(service.ListSymbols(fixture.Path).Data!.Symbols);
    }

    [Fact]
    public void CreateSymbol_Allows_Same_Reference_For_Different_Units()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        var unit1 = service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 60, 50, null, null, unit: 1, dryRun: false);
        var unit2 = service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 85, 50, null, null, unit: 2, dryRun: false);
        var duplicateUnit2 = service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 100, 50, null, null, unit: 2, dryRun: true);
        var list = service.ListSymbols(fixture.Path);

        Assert.True(unit1.Success);
        Assert.True(unit2.Success);
        Assert.False(duplicateUnit2.Success);
        Assert.Equal("SCHEMATIC_SYMBOL_EXISTS", duplicateUnit2.Error?.Code);
        Assert.Contains(list.Data!.Symbols, symbol => symbol.Reference == "U1" && symbol.Unit == 1);
        Assert.Contains(list.Data.Symbols, symbol => symbol.Reference == "U1" && symbol.Unit == 2);
    }

    [Fact]
    public void CreateSymbol_And_UpdateBoard_Supports_FivePin_Header_And_ThroughHole_4053()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Connector_Generic:Conn_01x05", "J1", 40, 50, "CONTROL", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "4xxx:4053", "U3", 80, 80, "CD4053BE", null, dryRun: false).Success);
        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);

        Assert.True(update.Success, update.Error?.Message ?? update.Summary);
        var board = new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path);
        var header = Assert.Single(board.Data!.Footprints, item => item.Reference == "J1" && item.FootprintName.Contains("PinHeader_1x05", StringComparison.Ordinal));
        var demodulator = Assert.Single(board.Data.Footprints, item => item.Reference == "U3" && item.FootprintName.Contains("DIP-16_W7.62mm", StringComparison.Ordinal));
        var inspection = new BoardInspectionService(new ProjectDiscoveryService());
        Assert.Equal(5, inspection.ListFootprintPads(fixture.Path, header.Reference!).Data!.Pads.Count);
        Assert.Equal(16, inspection.ListFootprintPads(fixture.Path, demodulator.Reference!).Data!.Pads.Count);
        var boardText = File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_pcb"));
        Assert.Contains("(layer \"F.CrtYd\")", boardText, StringComparison.Ordinal);
        Assert.Contains("PinHeader_1x05_P2.54mm_Vertical.step", boardText, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSymbol_And_UpdateBoard_Supports_Full_TwoByTen_Test_Header_Footprint()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Connector_Generic:Conn_02x10_Odd_Even", "JTEST", 80, 80, "LAB_TEST", null, dryRun: false).Success);
        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);

        Assert.True(update.Success, update.Error?.Message ?? update.Summary);
        var board = new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path);
        var header = Assert.Single(board.Data!.Footprints, item => item.Reference == "JTEST" && item.FootprintName.Contains("PinHeader_2x10", StringComparison.Ordinal));
        Assert.Equal(20, new BoardInspectionService(new ProjectDiscoveryService()).ListFootprintPads(fixture.Path, header.Reference!).Data!.Pads.Count);
        var boardText = File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_pcb"));
        Assert.Contains("(layer \"F.CrtYd\")", boardText, StringComparison.Ordinal);
        Assert.Contains("PinHeader_2x10_P2.54mm_Vertical.step", boardText, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSymbol_And_UpdateBoard_Supports_TestBoard_ProtectedSignalParts()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var pads = new BoardInspectionService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "PCBHelper:SN74CB3Q3257", "U1", 50, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "PCBHelper:LSF0204", "U2", 80, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "74xGxx:74LVC1G86", "U3", 110, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "74xGxx:74LVC1G08", "U4", 130, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "PCBHelper:Arduino_UNO_R4_Shield", "J1", 160, 80, null, null, dryRun: false).Success);

        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);

        Assert.True(update.Success, update.Error?.Message ?? update.Summary);
        Assert.Equal(16, pads.ListFootprintPads(fixture.Path, "U1").Data!.Pads.Count);
        Assert.Equal(14, pads.ListFootprintPads(fixture.Path, "U2").Data!.Pads.Count);
        Assert.Equal(5, pads.ListFootprintPads(fixture.Path, "U3").Data!.Pads.Count);
        Assert.Equal(5, pads.ListFootprintPads(fixture.Path, "U4").Data!.Pads.Count);
        Assert.Equal(32, pads.ListFootprintPads(fixture.Path, "J1").Data!.Pads.Count);
    }

    [Fact]
    public void ProjectLocalSymbol_VerticalPins_Preserve_Catalog_Nets_On_The_Board()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var pads = new BoardInspectionService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "PCBHelper:LSF0204", "U1", 80, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 55, 70, "0R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R2", 105, 70, "0R", null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.7", "R1.1", "LSF_GND", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.8", "R2.1", "LSF_ENABLE", dryRun: false).Success);

        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);
        var lsfPads = pads.ListFootprintPads(fixture.Path, "U1");

        Assert.True(update.Success, update.Error?.Message ?? update.Summary);
        Assert.True(lsfPads.Success, lsfPads.Error?.Message ?? lsfPads.Summary);
        Assert.Contains(lsfPads.Data!.Pads, pad => pad.Name == "7" && pad.NetName == "LSF_GND");
        Assert.Contains(lsfPads.Data.Pads, pad => pad.Name == "8" && pad.NetName == "LSF_ENABLE");
    }

    [Fact]
    public void ProjectLocalSymbol_Embeds_Vertical_Pins_At_The_Catalog_Y_Coordinate()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "PCBHelper:LSF0204", "U1", 80, 50, null, null, dryRun: false).Success);

        var schematic = File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_sch"));
        var definitionStart = schematic.IndexOf("(symbol \"PCBHelper:LSF0204\"", StringComparison.Ordinal);
        var instance = System.Text.RegularExpressions.Regex.Match(
            schematic,
            @"\(symbol\s+\(lib_id ""PCBHelper:LSF0204""\)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.True(instance.Success);
        var definition = schematic.Substring(definitionStart, instance.Index - definitionStart);
        Assert.Matches(
            @"(?s)\(pin passive line\s+\(at 0 -12\.7 90\).*?\(number ""7""",
            definition);
        Assert.Matches(
            @"(?s)\(pin passive line\s+\(at 5\.08 -12\.7 90\).*?\(number ""8""",
            definition);
    }

    [Fact]
    public void ReplaceSymbol_Refreshes_Stale_ProjectLocal_Library_Definitions()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.CreateSymbol(fixture.Path, "PCBHelper:LSF0204", "U1", 80, 50, null, null, dryRun: false).Success);
        var schematicPath = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");
        var libraryPath = Path.Combine(fixture.Path, "PCBHelper.kicad_sym");
        File.WriteAllText(
            schematicPath,
            File.ReadAllText(schematicPath).Replace("(at 0 -12.7 90)", "(at 0 12.7 270)", StringComparison.Ordinal));
        File.WriteAllText(
            libraryPath,
            File.ReadAllText(libraryPath).Replace("(at 0 -12.7 90)", "(at 0 12.7 270)", StringComparison.Ordinal));

        var result = service.ReplaceSymbol(fixture.Path, "U1", "PCBHelper:LSF0204", dryRun: false);
        var embedded = ExtractSymbolDefinition(File.ReadAllText(schematicPath), "PCBHelper:LSF0204");
        var projectLocal = ExtractSymbolDefinition(File.ReadAllText(libraryPath), "LSF0204");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("(at 0 -12.7 90)", embedded, StringComparison.Ordinal);
        Assert.DoesNotContain("(at 0 12.7 270)", embedded, StringComparison.Ordinal);
        Assert.Contains("(at 0 -12.7 90)", projectLocal, StringComparison.Ordinal);
        Assert.DoesNotContain("(at 0 12.7 270)", projectLocal, StringComparison.Ordinal);

        static string ExtractSymbolDefinition(string text, string symbolId)
        {
            var start = text.IndexOf($"(symbol \"{symbolId}\"", StringComparison.Ordinal);
            Assert.True(start >= 0);
            var depth = 0;
            var end = -1;
            for (var index = start; index < text.Length; index++)
            {
                if (text[index] == '(') depth++;
                else if (text[index] == ')' && --depth == 0)
                {
                    end = index;
                    break;
                }
            }
            Assert.True(end >= start);
            return text.Substring(start, end - start + 1);
        }
    }

    [Fact]
    public void ListSymbols_Uses_Embedded_KiCad_Pin_Geometry_For_74xGxx_SingleGate()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "74xGxx:74LVC1G86", "U1", 110, 50, null, null, dryRun: false).Success);

        var symbol = Assert.Single(service.ListSymbols(fixture.Path).Data!.Symbols);
        var pins = symbol.Pins.ToDictionary(pin => pin.Pin);
        Assert.Equal(symbol.XMillimeters!.Value - 15.24, pins["1"].XMillimeters, 2);
        Assert.Equal(symbol.YMillimeters!.Value - 2.54, pins["1"].YMillimeters, 2);
        Assert.Equal(symbol.XMillimeters.Value - 15.24, pins["2"].XMillimeters, 2);
        Assert.Equal(symbol.YMillimeters.Value + 2.54, pins["2"].YMillimeters, 2);
        Assert.Equal(symbol.XMillimeters.Value, pins["3"].XMillimeters, 2);
        Assert.Equal(symbol.YMillimeters.Value + 10.16, pins["3"].YMillimeters, 2);
        Assert.Equal(symbol.XMillimeters.Value + 12.70, pins["4"].XMillimeters, 2);
        Assert.Equal(symbol.YMillimeters.Value, pins["4"].YMillimeters, 2);
        Assert.Equal(symbol.XMillimeters.Value, pins["5"].XMillimeters, 2);
        Assert.Equal(symbol.YMillimeters.Value - 10.16, pins["5"].YMillimeters, 2);
    }

    [Fact]
    public void ConnectPins_Mirrors_Library_Y_Offset_Into_Schematic_Coordinates()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "4xxx:4053", "U3", 80, 80, "CD4053BE", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 40, 80, "10k", null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U3.1", "R1.1", "Y1_TEST", dryRun: false).Success);

        var schematic = service.ListSymbols(fixture.Path).Data!;
        var symbol = Assert.Single(schematic.Symbols, item => item.Reference == "U3");
        Assert.Contains(schematic.Wires, wire => IsWireEndpoint(
            symbol.XMillimeters!.Value - 12.7,
            symbol.YMillimeters!.Value - 5.08,
            wire));
    }

    [Fact]
    public void ConnectPins_Fails_When_Required_MultiUnit_Symbol_Is_Not_Placed()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "CAC1", 70, 35, "100n", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 60, 50, null, null, unit: 1, dryRun: false).Success);

        var result = service.ConnectPins(fixture.Path, "CAC1.2", "U1.6", "AC_OUT", dryRun: true);

        Assert.False(result.Success);
        Assert.Equal("SCHEMATIC_SYMBOL_UNIT_NOT_PLACED", result.Error?.Code);
    }

    [Fact]
    public void ConnectPins_Resolves_MultiUnit_Pins_To_Different_Islands()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:D_Photo", "PD1", 35, 35, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "CAC1", 70, 35, "100n", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 60, 50, null, null, unit: 1, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 85, 50, null, null, unit: 2, dryRun: false).Success);

        var tiaIn = service.ConnectPins(fixture.Path, "PD1.A", "U1.2", "TIA_IN", dryRun: false);
        var acOut = service.ConnectPins(fixture.Path, "CAC1.2", "U1.6", "AC_OUT", dryRun: false);
        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);
        var pads = new BoardInspectionService(new ProjectDiscoveryService()).ListFootprintPads(fixture.Path, "U1");

        Assert.True(tiaIn.Success);
        Assert.True(acOut.Success);
        Assert.True(update.Success);
        Assert.Contains(pads.Data!.Pads, pad => pad.Name == "2" && pad.NetName == "TIA_IN");
        Assert.Contains(pads.Data.Pads, pad => pad.Name == "6" && pad.NetName == "AC_OUT");
        Assert.Single(new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path).Data!.Footprints, footprint => footprint.Reference == "U1");
    }

    [Fact]
    public void ConnectPins_Generates_Grid_Safe_Wires_And_Labels()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:D_Photo", "PD1", 40, 90, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 80, 100, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "PD1.A", "U1.2", "TIA_IN", dryRun: false).Success);

        var list = service.ListSymbols(fixture.Path).Data!;
        foreach (var symbol in list.Symbols)
        {
            AssertSymbolIsOnSchematicGrid(symbol);
        }

        foreach (var wire in list.Wires)
        {
            AssertWireIsOnSchematicGrid(wire);
        }

        var labels = list.Labels.Where(item => item.Text == "TIA_IN").ToArray();
        Assert.Single(labels);
        Assert.True(list.Wires.Count >= 3);
        foreach (var label in labels)
        {
            AssertLabelIsOnSchematicGrid(label);
            AssertLabelIsOnWire(label, list.Wires);
        }
    }

    [Fact]
    public void ConnectPins_Starts_Stubs_At_KiCad_Symbol_Pin_Points()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:D_Photo", "PD1", 40, 90, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 80, 100, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "PD1.A", "U1.2", "TIA_IN", dryRun: false).Success);

        var wires = service.ListSymbols(fixture.Path).Data!.Wires;

        Assert.Contains(wires, wire => IsWireEndpoint(41.91, 90.17, wire));
        Assert.Contains(wires, wire => IsWireEndpoint(72.39, 102.87, wire));
    }

    [Fact]
    public void ConnectPins_Uses_Local_Labels_Per_Pin_Island_Without_Redundant_Pin_Stubs()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:D_Photo", "PD1", 40, 90, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:D_Photo", "PD2", 40, 105, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 80, 100, null, null, dryRun: false).Success);

        Assert.True(service.ConnectPins(fixture.Path, "PD1.A", "U1.2", "TIA_IN", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "PD2.K", "U1.2", "TIA_IN", dryRun: false).Success);

        var labels = service.ListSymbols(fixture.Path).Data!.Labels.Where(label => label.Text == "TIA_IN").ToArray();

        Assert.Equal(2, labels.Length);
    }

    [Fact]
    public void ConnectPins_Fails_When_Pin_Island_Already_Has_Another_Net()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.1", "R1.2", "GND", dryRun: false).Success);

        var conflict = service.ConnectPins(fixture.Path, "R1.1", "D1.A", "TIA_IN", dryRun: true);

        Assert.False(conflict.Success);
        Assert.Equal("SCHEMATIC_NET_CONFLICT", conflict.Error?.Code);
    }

    [Fact]
    public void DeleteNetLabel_And_DeleteSchematicWire_Remove_Targeted_Blocks()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.2", "D1.A", "SIG", dryRun: false).Success);
        Assert.True(service.AddNetLabel(fixture.Path, "EXTRA", 80, 50, dryRun: false).Success);

        var before = service.ListSymbols(fixture.Path).Data!;
        var extra = before.Labels.Single(label => label.Text == "EXTRA");
        var wire = before.Wires.First();

        var dryRun = service.DeleteNetLabelByUuid(fixture.Path, extra.Uuid!, dryRun: true);
        Assert.True(dryRun.Success);
        Assert.Equal(before.LabelCount, service.ListSymbols(fixture.Path).Data!.LabelCount);

        var labelDelete = service.DeleteNetLabelByUuid(fixture.Path, extra.Uuid!, dryRun: false);
        var wireDelete = service.DeleteSchematicWire(fixture.Path, wire.X1Millimeters, wire.Y1Millimeters, wire.X2Millimeters, wire.Y2Millimeters, 0.001, dryRun: false);

        Assert.True(labelDelete.Success);
        Assert.True(wireDelete.Success);
        var after = service.ListSymbols(fixture.Path).Data!;
        Assert.DoesNotContain(after.Labels, label => label.Uuid == extra.Uuid);
        Assert.DoesNotContain(after.Wires, item => item.Uuid == wire.Uuid);
    }

    [Fact]
    public void ReplaceNetLabel_Changes_Only_The_Label_At_The_Selected_Location()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(service.AddNetLabel(fixture.Path, "GND", 20.32, 50.8, dryRun: false).Success);
        Assert.True(service.AddNetLabel(fixture.Path, "GND", 40.64, 50.8, dryRun: false).Success);

        var result = service.ReplaceNetLabel(fixture.Path, "GND", "VMID", 20.32, 50.8, 0.001, dryRun: false);
        var labels = service.ListSymbols(fixture.Path).Data!.Labels;

        Assert.True(result.Success);
        Assert.Contains(labels, label => label.Text == "VMID" && label.XMillimeters == 20.32);
        Assert.Contains(labels, label => label.Text == "GND" && label.XMillimeters == 40.64);
        Assert.DoesNotContain(labels, label => label.Text == "GND" && label.XMillimeters == 20.32);
    }

    [Fact]
    public void UpdatePcbFromSchematic_Uses_Led_Pad_Pitch_With_Clearance()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var pads = new BoardInspectionService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);

        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);
        var d1Pads = pads.ListFootprintPads(fixture.Path, "D1");

        Assert.True(update.Success);
        Assert.True(d1Pads.Success);
        Assert.Contains(d1Pads.Data!.Pads, pad => pad.Name == "1" && pad.XMillimeters == -2.54);
        Assert.Contains(d1Pads.Data.Pads, pad => pad.Name == "2" && pad.XMillimeters == 2.54);
    }

    [Fact]
    public void UpdatePcbFromSchematic_Replaces_Net_Section_Idempotently()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var boardFile = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.2", "D1.A", "SIG", dryRun: false).Success);

        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);

        var boardText = File.ReadAllText(boardFile);
        var netLines = TopLevelNetLines(boardText);

        Assert.Single(netLines, line => line.Contains("\"SIG\"", StringComparison.Ordinal));
        Assert.Equal(netLines.Length, netLines.Distinct(StringComparer.Ordinal).Count());
        Assert.All(netLines, line => Assert.True(boardText.IndexOf(line, StringComparison.Ordinal) < boardText.IndexOf("  (footprint", StringComparison.Ordinal)));
    }

    [Fact]
    public void UpdatePcbFromSchematic_Updates_Existing_Footprint_Pad_Nets()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var board = new BoardInspectionService(new ProjectDiscoveryService());
        var boardFile = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");

        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 85, 50, null, null, unit: 2, dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);
        var u1Before = board.ListFootprintPads(fixture.Path, "U1");
        Assert.Contains(u1Before.Data!.Pads, pad => pad.Name == "7" && pad.NetName is null);

        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "CFB2", 70, 35, "10p", null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.7", "CFB2.2", "FILTER_OUT", dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);

        var u1After = board.ListFootprintPads(fixture.Path, "U1");
        var cfb2After = board.ListFootprintPads(fixture.Path, "CFB2");
        var nets = board.ListNets(fixture.Path);
        var boardText = File.ReadAllText(boardFile);

        Assert.Contains(u1After.Data!.Pads, pad => pad.Name == "7" && pad.NetName == "FILTER_OUT");
        Assert.Contains(cfb2After.Data!.Pads, pad => pad.Name == "2" && pad.NetName == "FILTER_OUT");
        Assert.Contains(nets.Data!.Nets, net => net.Name == "FILTER_OUT");
        Assert.Single(TopLevelNetLines(boardText), line => line.Contains("\"FILTER_OUT\"", StringComparison.Ordinal));
    }

    [Fact]
    public void UpdatePcbFromSchematic_Overwrites_Stale_Existing_Pad_Nets_For_Filter_Section()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var board = new BoardInspectionService(new ProjectDiscoveryService());
        var boardFile = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");

        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "CAC1", 70, 35, "100n", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "RFB2", 90, 40, "100k", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "CFB2", 90, 50, "10p", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:D", "DDEM1", 110, 45, "D", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "RDEM1", 130, 40, "100k", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "CDEM1", 130, 50, "100n", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 85, 50, null, null, unit: 2, dryRun: false).Success);

        Assert.True(service.ConnectPins(fixture.Path, "CAC1.2", "U1.6", "AC_OUT", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.6", "RFB2.2", "AC_OUT", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.6", "CFB2.2", "AC_OUT", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.7", "RFB2.1", "FILTER_OUT", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.7", "CFB2.1", "FILTER_OUT", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.7", "DDEM1.A", "FILTER_OUT", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "DDEM1.K", "RDEM1.1", "DEMOD_OUT", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "DDEM1.K", "CDEM1.1", "DEMOD_OUT", dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);

        ForceFootprintPadsToNet(boardFile, "RFB2", "AC_OUT");
        ForceFootprintPadsToNet(boardFile, "CFB2", "AC_OUT");
        ForceFootprintPadsToNet(boardFile, "DDEM1", "AC_OUT");
        Assert.Contains(board.ListFootprintPads(fixture.Path, "RFB2").Data!.Pads, pad => pad.Name == "1" && pad.NetName == "AC_OUT");
        Assert.Contains(board.ListFootprintPads(fixture.Path, "CFB2").Data!.Pads, pad => pad.Name == "1" && pad.NetName == "AC_OUT");

        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);
        var rfb2 = board.ListFootprintPads(fixture.Path, "RFB2");
        var cfb2 = board.ListFootprintPads(fixture.Path, "CFB2");
        var u1 = board.ListFootprintPads(fixture.Path, "U1");
        var ddem1 = board.ListFootprintPads(fixture.Path, "DDEM1");
        var nets = board.ListNets(fixture.Path);
        var filterOut = board.GetNet(fixture.Path, "FILTER_OUT");
        var acOut = board.GetNet(fixture.Path, "AC_OUT");
        var demodOut = board.GetNet(fixture.Path, "DEMOD_OUT");

        Assert.True(update.Success);
        Assert.Contains(rfb2.Data!.Pads, pad => pad.Name == "1" && pad.NetName == "FILTER_OUT");
        Assert.Contains(rfb2.Data.Pads, pad => pad.Name == "2" && pad.NetName == "AC_OUT");
        Assert.Contains(cfb2.Data!.Pads, pad => pad.Name == "1" && pad.NetName == "FILTER_OUT");
        Assert.Contains(cfb2.Data.Pads, pad => pad.Name == "2" && pad.NetName == "AC_OUT");
        Assert.Contains(u1.Data!.Pads, pad => pad.Name == "6" && pad.NetName == "AC_OUT");
        Assert.Contains(u1.Data.Pads, pad => pad.Name == "7" && pad.NetName == "FILTER_OUT");
        Assert.Contains(ddem1.Data!.Pads, pad => pad.Name == "2" && pad.NetName == "FILTER_OUT");
        Assert.Contains(ddem1.Data.Pads, pad => pad.Name == "1" && pad.NetName == "DEMOD_OUT");
        Assert.Contains(nets.Data!.Nets, net => net.Name == "FILTER_OUT" && net.PadCount > 0);
        Assert.Contains(filterOut.Data!.Pads, pad => pad.FootprintReference == "U1" && pad.PadName == "7");
        Assert.Contains(filterOut.Data.Pads, pad => pad.FootprintReference == "RFB2" && pad.PadName == "1");
        Assert.Contains(filterOut.Data.Pads, pad => pad.FootprintReference == "CFB2" && pad.PadName == "1");
        Assert.Contains(acOut.Data!.Pads, pad => pad.FootprintReference == "U1" && pad.PadName == "6");
        Assert.Contains(acOut.Data.Pads, pad => pad.FootprintReference == "RFB2" && pad.PadName == "2");
        Assert.Contains(acOut.Data.Pads, pad => pad.FootprintReference == "CFB2" && pad.PadName == "2");
        Assert.Contains(demodOut.Data!.Pads, pad => pad.FootprintReference == "DDEM1" && pad.PadName == "1");
        Assert.Contains(demodOut.Data.Pads, pad => pad.FootprintReference == "RDEM1" && pad.PadName == "1");
        Assert.Contains(demodOut.Data.Pads, pad => pad.FootprintReference == "CDEM1" && pad.PadName == "1");
    }

    [Fact]
    public void UpdatePcbFromSchematic_Places_New_Footprints_After_Existing_Board_Content()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var summary = new BoardSummaryService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "RV1", 90, 50, "100k", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "RV2", 110, 50, "100k", null, dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);

        var footprints = summary.GetSummary(fixture.Path).Data!.Footprints;
        var r1 = footprints.Single(footprint => footprint.Reference == "R1");
        var d1 = footprints.Single(footprint => footprint.Reference == "D1");
        var rv1 = footprints.Single(footprint => footprint.Reference == "RV1");
        var rv2 = footprints.Single(footprint => footprint.Reference == "RV2");

        Assert.Equal(45, r1.XMillimeters);
        Assert.Equal(65, d1.XMillimeters);
        Assert.True(rv1.XMillimeters > d1.XMillimeters);
        Assert.True(rv2.XMillimeters > rv1.XMillimeters);
    }

    [Fact]
    public void RegenerateBoardFootprint_Replaces_Existing_Template_And_Preserves_Placement()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var pads = new BoardInspectionService(new ProjectDiscoveryService());
        var boardFile = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");

        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);
        File.WriteAllText(
            boardFile,
            File.ReadAllText(boardFile)
                .Replace("(at -2.54 0)", "(at -1.27 0)")
                .Replace("(at 2.54 0)", "(at 1.27 0)"));

        var before = pads.ListFootprintPads(fixture.Path, "D1");
        Assert.Contains(before.Data!.Pads, pad => pad.Name == "1" && pad.XMillimeters == -1.27);
        Assert.Contains(before.Data.Pads, pad => pad.Name == "2" && pad.XMillimeters == 1.27);

        var preview = service.RegenerateBoardFootprint(fixture.Path, "D1", dryRun: true);
        Assert.True(preview.Success);
        Assert.Contains(pads.ListFootprintPads(fixture.Path, "D1").Data!.Pads, pad => pad.Name == "1" && pad.XMillimeters == -1.27);

        var result = service.RegenerateBoardFootprint(fixture.Path, "D1", dryRun: false);
        var after = pads.ListFootprintPads(fixture.Path, "D1");
        var summary = new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path);
        var d1 = summary.Data!.Footprints.Single(footprint => footprint.Reference == "D1");

        Assert.True(result.Success);
        Assert.Contains(after.Data!.Pads, pad => pad.Name == "1" && pad.XMillimeters == -2.54);
        Assert.Contains(after.Data.Pads, pad => pad.Name == "2" && pad.XMillimeters == 2.54);
        Assert.Equal(45, d1.XMillimeters);
        Assert.Equal(35, d1.YMillimeters);
    }

    [Fact]
    public void UpdatePcbFromSchematic_Generates_Loadable_Hb100_Footprint_With_All_Duplicate_Pad_Nets()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var pads = new BoardInspectionService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Connector_Generic:Conn_01x03", "J2", 70, 50, "HB100", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 100, 45, "1k", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R2", 100, 55, "1k", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R3", 100, 65, "1k", null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "J2.1", "R1.1", "SENSOR_5V", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "J2.2", "R2.1", "GND", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "J2.3", "R3.1", "RAW_SENSOR", dryRun: false).Success);
        Assert.True(service.SetSymbolField(fixture.Path, "J2", "Footprint", "PCBHelper:HB100_Module", dryRun: false).Success);

        var updated = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);
        var result = pads.ListFootprintPads(fixture.Path, "J2");

        Assert.True(updated.Success, updated.Error?.Message);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(8, result.Data!.Pads.Count);
        Assert.All(result.Data.Pads.Where(pad => pad.Name == "1"), pad => Assert.Equal("SENSOR_5V", pad.NetName));
        Assert.All(result.Data.Pads.Where(pad => pad.Name == "2"), pad => Assert.Equal("GND", pad.NetName));
        Assert.All(result.Data.Pads.Where(pad => pad.Name == "3"), pad => Assert.Equal("RAW_SENSOR", pad.NetName));

        var boardText = File.ReadAllText(Directory.GetFiles(fixture.Path, "*.kicad_pcb").Single());
        var hb100Start = boardText.IndexOf("(footprint \"PCBHelper:HB100_Module\"", StringComparison.Ordinal);
        var hb100End = boardText.IndexOf("(embedded_fonts no)", hb100Start, StringComparison.Ordinal);
        var hb100Text = boardText[hb100Start..hb100End];
        Assert.DoesNotContain("(version ", hb100Text);
        Assert.DoesNotContain("(generator ", hb100Text);
        Assert.DoesNotContain("(generator_version ", hb100Text);
        Assert.DoesNotContain("(fill (type none))", hb100Text);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(hb100Text, "\\(fill no\\)").Count);
        var padBlocks = System.Text.RegularExpressions.Regex.Matches(hb100Text, "(?ms)^\\s*\\(pad\\s+.*?^\\s*\\)");
        Assert.Equal(8, padBlocks.Count);
        Assert.All(
            padBlocks.Cast<System.Text.RegularExpressions.Match>(),
            pad => Assert.True(
                pad.Value.IndexOf("(net ", StringComparison.Ordinal) < pad.Value.IndexOf("(uuid ", StringComparison.Ordinal),
                "KiCad requires a board pad's net before its UUID."));
        Assert.DoesNotMatch("\\(net\\s+\\d+", hb100Text);
    }

    [Fact]
    public void RegenerateBoardFootprint_Preserves_Existing_Pad_Nets_When_Schematic_Is_Cleaned()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var pads = new BoardInspectionService(new ProjectDiscoveryService());
        var boardFile = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.2", "D1.A", "SIG", dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);

        var listed = service.ListSymbols(fixture.Path).Data!;
        foreach (var label in listed.Labels)
        {
            Assert.True(service.DeleteNetLabelByUuid(fixture.Path, label.Uuid!, dryRun: false).Success);
        }

        foreach (var wire in listed.Wires)
        {
            Assert.True(service.DeleteSchematicWireByUuid(fixture.Path, wire.Uuid!, dryRun: false).Success);
        }

        File.WriteAllText(
            boardFile,
            File.ReadAllText(boardFile)
                .Replace("(at -2.54 0)", "(at -1.27 0)")
                .Replace("(at 2.54 0)", "(at 1.27 0)"));

        var result = service.RegenerateBoardFootprint(fixture.Path, "D1", dryRun: false);
        var after = pads.ListFootprintPads(fixture.Path, "D1");

        Assert.True(result.Success);
        Assert.Contains(after.Data!.Pads, pad => pad.Name == "2" && pad.XMillimeters == 2.54 && pad.NetName == "SIG");
    }

    [Fact]
    public void RegenerateBoardFootprint_Preserves_KiCad10_Named_Pad_Nets()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var pads = new BoardInspectionService(new ProjectDiscoveryService());
        var boardFile = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "0R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.2", "D1.A", "SIG", dryRun: false).Success);
        Assert.True(service.UpdatePcbFromSchematic(fixture.Path, dryRun: false).Success);
        Assert.True(service.SetSymbolField(fixture.Path, "R1", "Footprint", "R_Axial_2Pad", dryRun: false).Success);

        var listed = service.ListSymbols(fixture.Path).Data!;
        foreach (var label in listed.Labels)
        {
            Assert.True(service.DeleteNetLabelByUuid(fixture.Path, label.Uuid!, dryRun: false).Success);
        }

        foreach (var wire in listed.Wires)
        {
            Assert.True(service.DeleteSchematicWireByUuid(fixture.Path, wire.Uuid!, dryRun: false).Success);
        }

        var boardText = File.ReadAllText(boardFile);
        boardText = System.Text.RegularExpressions.Regex.Replace(
            boardText,
            @"\(net\s+\d+\s+""SIG""\)",
            "(net \"SIG\")");
        File.WriteAllText(boardFile, boardText);

        var result = service.RegenerateBoardFootprint(fixture.Path, "R1", dryRun: false);
        var after = pads.ListFootprintPads(fixture.Path, "R1");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(after.Data!.Pads, pad => pad.Name == "2" && pad.NetName == "SIG");
    }

    [Fact]
    public void Real_Mutations_Support_Receiver_Core_Symbols_And_Footprints()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var board = new BoardSummaryService(new ProjectDiscoveryService());
        var pads = new BoardInspectionService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:D_Photo", "PD1", 35, 35, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:D_Photo", "PD2", 35, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "RF1", 50, 35, "100k", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "CF1", 50, 45, "10p", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:C", "CAC1", 70, 35, "100n", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 60, 50, null, null, unit: 1, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 85, 50, null, null, unit: 2, dryRun: false).Success);

        Assert.True(service.ConnectPins(fixture.Path, "PD1.A", "U1.2", "TIA_IN", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "PD2.K", "U1.2", "TIA_IN", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "RF1.1", "U1.2", "TIA_IN", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "RF1.2", "U1.1", "TIA_OUT", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "CF1.1", "U1.2", "TIA_IN", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "CF1.2", "U1.1", "TIA_OUT", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "U1.1", "CAC1.1", "TIA_OUT", dryRun: false).Success);
        var groundConnection = service.ConnectPins(fixture.Path, "PD1.K", "PD2.A", "GND", dryRun: false);
        Assert.True(groundConnection.Success, $"{groundConnection.Error?.Code}: {groundConnection.Error?.Message}");
        Assert.True(service.ConnectPins(fixture.Path, "U1.3", "U1.5", "VREF", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "CAC1.2", "U1.6", "AC_OUT", dryRun: false).Success);

        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);
        var schematic = service.ListSymbols(fixture.Path).Data!;
        var summary = board.GetSummary(fixture.Path);
        var u1Pads = pads.ListFootprintPads(fixture.Path, "U1");
        var pd1Pads = pads.ListFootprintPads(fixture.Path, "PD1");
        var c1Pads = pads.ListFootprintPads(fixture.Path, "CF1");

        Assert.True(update.Success);
        foreach (var symbol in schematic.Symbols)
        {
            AssertSymbolIsOnSchematicGrid(symbol);
        }

        foreach (var wire in schematic.Wires)
        {
            AssertWireIsOnSchematicGrid(wire);
        }

        foreach (var label in schematic.Labels)
        {
            AssertLabelIsOnSchematicGrid(label);
            AssertLabelIsOnWire(label, schematic.Wires);
        }

        Assert.Contains(summary.Data!.Footprints, footprint => footprint.Reference == "U1");
        Assert.Contains(summary.Data.Footprints, footprint => footprint.Reference == "PD1");
        Assert.Contains(summary.Data.Footprints, footprint => footprint.Reference == "CF1");
        Assert.True(u1Pads.Success);
        Assert.Contains(u1Pads.Data!.Pads, pad => pad.Name == "1");
        Assert.Contains(u1Pads.Data.Pads, pad => pad.Name == "1" && pad.NetName == "TIA_OUT");
        Assert.Contains(u1Pads.Data.Pads, pad => pad.Name == "2" && pad.NetName == "TIA_IN");
        Assert.Contains(u1Pads.Data.Pads, pad => pad.Name == "3" && pad.NetName == "VREF");
        Assert.Contains(u1Pads.Data.Pads, pad => pad.Name == "5" && pad.NetName == "VREF");
        Assert.Contains(u1Pads.Data.Pads, pad => pad.Name == "6" && pad.NetName == "AC_OUT");
        Assert.Contains(u1Pads.Data.Pads, pad => pad.Name == "8");
        Assert.True(pd1Pads.Success);
        Assert.Contains(pd1Pads.Data!.Pads, pad => pad.Name == "1" && pad.PinFunction == "A" && pad.NetName == "TIA_IN");
        Assert.Contains(pd1Pads.Data.Pads, pad => pad.Name == "2" && pad.PinFunction == "K" && pad.NetName == "GND");
        Assert.True(c1Pads.Success);
        Assert.Equal(2, c1Pads.Data!.Pads.Count);
    }

    [Fact]
    public void SetSymbolField_Inserts_New_Property_Before_Symbol_Closing_Line()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var schematicFile = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.SetSymbolField(fixture.Path, "R1", "MPN", "ABC-123", dryRun: false).Success);

        var text = File.ReadAllText(schematicFile);
        Assert.Contains($"{Environment.NewLine}    (property \"MPN\" \"ABC-123\"{Environment.NewLine}", text);
        Assert.DoesNotContain("      (property \"MPN\"", text);
        var propertyStart = text.IndexOf("(property \"MPN\" \"ABC-123\"", StringComparison.Ordinal);
        Assert.True(propertyStart >= 0);
        var propertyExcerpt = text.Substring(propertyStart, Math.Min(300, text.Length - propertyStart));
        Assert.Contains("(hide yes)", propertyExcerpt);
        Assert.Contains(service.ListSymbols(fixture.Path).Data!.Symbols.Single().Fields, field => field.Name == "MPN" && field.Value == "ABC-123");
    }

    [Fact]
    public void SetSymbolField_Updates_All_Placed_Units_For_A_Reference()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 60, 50, null, null, unit: 1, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Amplifier_Operational:OPA2325", "U1", 85, 50, null, null, unit: 2, dryRun: false).Success);

        var result = service.SetSymbolField(fixture.Path, "U1", "Value", "OPA2325IDR", dryRun: false);
        var symbols = service.ListSymbols(fixture.Path).Data!.Symbols.Where(symbol => symbol.Reference == "U1").ToArray();

        Assert.True(result.Success);
        Assert.Equal(2, symbols.Length);
        Assert.All(symbols, symbol => Assert.Equal("OPA2325IDR", symbol.Value));
    }

    [Fact]
    public void HideSymbolField_Preserves_Value_And_Adds_Hidden_Attribute()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var schematicFile = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");

        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.SetSymbolField(fixture.Path, "R1", "MPN", "ABC-123", dryRun: false).Success);

        var result = service.HideSymbolField(fixture.Path, "R1", "MPN", dryRun: false);
        var text = File.ReadAllText(schematicFile);

        Assert.True(result.Success, result.Error?.Message);
        var propertyStart = text.IndexOf("(property \"MPN\" \"ABC-123\"", StringComparison.Ordinal);
        Assert.True(propertyStart >= 0);
        var propertyExcerpt = text.Substring(propertyStart, Math.Min(300, text.Length - propertyStart));
        Assert.Contains("(hide yes)", propertyExcerpt);
        Assert.Contains(service.ListSymbols(fixture.Path).Data!.Symbols.Single().Fields, field => field.Name == "MPN" && field.Value == "ABC-123");
    }

    [Fact]
    public async Task RestoreChange_Restores_Schematic_File_Snapshot()
    {
        using var fixture = CopyBlankFixture();
        var projectDiscovery = new ProjectDiscoveryService();
        var reports = new ChangeReportService(projectDiscovery);
        var checks = CreateCheckRunner(projectDiscovery);
        var schematic = new SchematicAuthoringWorkflowService(new SchematicAuthoringService(projectDiscovery), checks, reports);
        var review = new ChangeReviewService(
            projectDiscovery,
            reports,
            new GeometryWorkflowService(new GeometryService(projectDiscovery), checks, reports),
            new ComponentValueWorkflowService(new ComponentService(projectDiscovery), checks, reports),
            new RoutingWorkflowService(new RoutingService(projectDiscovery), checks, reports),
            schematic);

        var create = await schematic.CreateSymbolAsync(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false);
        Assert.True(create.Success);
        Assert.Single(schematic.ListSymbols(fixture.Path).Data!.Symbols);

        var restore = await review.RestoreChangeAsync(fixture.Path, create.Data!.ChangeReportPath!, dryRun: false);

        Assert.True(restore.Success);
        Assert.Empty(schematic.ListSymbols(fixture.Path).Data!.Symbols);
    }

    [Fact]
    public void Stable_Errors_For_Unsupported_Symbol_Pin_And_Footprint()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        var unsupported = service.CreateSymbol(fixture.Path, "Device:OpAmp", "U1", 50, 50, null, null, dryRun: true);
        service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false);
        var missingPin = service.ConnectPins(fixture.Path, "R1.9", "R1.1", null, dryRun: true);
        service.SetSymbolField(fixture.Path, "R1", "Footprint", "UnknownFootprint", dryRun: false);
        var missingFootprint = service.UpdatePcbFromSchematic(fixture.Path, dryRun: true);

        Assert.Equal("SCHEMATIC_SYMBOL_UNSUPPORTED", unsupported.Error?.Code);
        Assert.Equal("SCHEMATIC_PIN_NOT_FOUND", missingPin.Error?.Code);
        Assert.Equal("FOOTPRINT_TEMPLATE_NOT_FOUND", missingFootprint.Error?.Code);
    }

    [Fact]
    public void UpdatePcbFromSchematic_MissingFootprint_IdentifiesReferenceAndEmptyValue()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Switch:SW_SPDT", "SW1", 50, 50, null, null, dryRun: false).Success);

        var result = service.UpdatePcbFromSchematic(fixture.Path, dryRun: true);

        Assert.False(result.Success);
        Assert.Equal("FOOTPRINT_TEMPLATE_NOT_FOUND", result.Error?.Code);
        Assert.Contains("SW1", result.Summary, StringComparison.Ordinal);
        Assert.Contains("<empty>", result.Summary, StringComparison.Ordinal);
    }

    private static CheckRunner CreateCheckRunner(ProjectDiscoveryService projectDiscovery)
    {
        using var fakeCli = new TempFile("kicad-cli.exe", deleteOnDispose: false);
        return new CheckRunner(
            projectDiscovery,
            new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli.Path : null),
            new FakeCommandRunner());
    }

    private static TempDirectory CopyBlankFixture()
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", "blank-authoring");
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        }

        return temp;
    }

    private static void AssertSymbolIsOnSchematicGrid(SchematicSymbolSummary symbol)
    {
        Assert.True(symbol.XMillimeters is not null, $"{symbol.Reference} has no X coordinate.");
        Assert.True(symbol.YMillimeters is not null, $"{symbol.Reference} has no Y coordinate.");
        Assert.True(IsOnSchematicGrid(symbol.XMillimeters.Value), $"{symbol.Reference} X is off grid: {symbol.XMillimeters}");
        Assert.True(IsOnSchematicGrid(symbol.YMillimeters.Value), $"{symbol.Reference} Y is off grid: {symbol.YMillimeters}");
    }

    private static void AssertWireIsOnSchematicGrid(SchematicWireSummary wire)
    {
        Assert.True(IsOnSchematicGrid(wire.X1Millimeters), $"Wire {wire.Uuid} X1 is off grid: {wire.X1Millimeters}");
        Assert.True(IsOnSchematicGrid(wire.Y1Millimeters), $"Wire {wire.Uuid} Y1 is off grid: {wire.Y1Millimeters}");
        Assert.True(IsOnSchematicGrid(wire.X2Millimeters), $"Wire {wire.Uuid} X2 is off grid: {wire.X2Millimeters}");
        Assert.True(IsOnSchematicGrid(wire.Y2Millimeters), $"Wire {wire.Uuid} Y2 is off grid: {wire.Y2Millimeters}");
    }

    private static void AssertLabelIsOnSchematicGrid(SchematicLabelSummary label)
    {
        Assert.True(IsOnSchematicGrid(label.XMillimeters), $"Label {label.Text} X is off grid: {label.XMillimeters}");
        Assert.True(IsOnSchematicGrid(label.YMillimeters), $"Label {label.Text} Y is off grid: {label.YMillimeters}");
    }

    private static void AssertLabelIsOnSingleWireEndpoint(SchematicLabelSummary label, IReadOnlyList<SchematicWireSummary> wires)
    {
        var touching = wires.Where(wire => ApproximatelyOnWire(label.XMillimeters, label.YMillimeters, wire)).ToArray();
        var wire = Assert.Single(touching);
        Assert.True(
            IsWireEndpoint(label.XMillimeters, label.YMillimeters, wire),
            $"Label {label.Text} is on a wire but not on exactly one endpoint.");
    }

    private static void AssertLabelIsOnWire(SchematicLabelSummary label, IReadOnlyList<SchematicWireSummary> wires)
    {
        Assert.Contains(wires, wire => ApproximatelyOnWire(label.XMillimeters, label.YMillimeters, wire));
    }

    private static string[] TopLevelNetLines(string boardText)
    {
        return boardText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(static line => line.StartsWith("  (net ", StringComparison.Ordinal))
            .ToArray();
    }

    private static void ForceFootprintPadsToNet(string boardFile, string reference, string netName)
    {
        var text = File.ReadAllText(boardFile);
        var netMatch = System.Text.RegularExpressions.Regex.Match(
            text,
            @$"(?m)^\s+\(net\s+(?<code>\d+)\s+""{System.Text.RegularExpressions.Regex.Escape(netName)}""\)");
        if (!netMatch.Success)
        {
            throw new InvalidOperationException($"Net not found in board: {netName}");
        }

        var netText = $"(net {netMatch.Groups["code"].Value} \"{netName}\")";
        var referenceIndex = text.IndexOf($"(property \"Reference\" \"{reference}\"", StringComparison.Ordinal);
        if (referenceIndex < 0)
        {
            throw new InvalidOperationException($"Footprint not found in board: {reference}");
        }

        var footprintStart = text.LastIndexOf("  (footprint", referenceIndex, StringComparison.Ordinal);
        var footprintEnd = FindMatchingParenthesis(text, footprintStart);
        var footprintText = text.Substring(footprintStart, footprintEnd - footprintStart + 1);
        var padRanges = new List<(int Start, int Length)>();
        var searchIndex = 0;
        while (searchIndex < footprintText.Length)
        {
            var padStart = footprintText.IndexOf("(pad ", searchIndex, StringComparison.Ordinal);
            if (padStart < 0)
            {
                break;
            }

            var padEnd = FindMatchingParenthesis(footprintText, padStart);
            padRanges.Add((padStart, padEnd - padStart + 1));
            searchIndex = padEnd + 1;
        }

        foreach (var range in padRanges.OrderByDescending(static item => item.Start))
        {
            var padText = footprintText.Substring(range.Start, range.Length);
            var netStart = padText.IndexOf("(net ", StringComparison.Ordinal);
            if (netStart >= 0)
            {
                var netEnd = FindMatchingParenthesis(padText, netStart);
                padText = padText.Remove(netStart, netEnd - netStart + 1).Insert(netStart, netText);
            }
            else
            {
                var closeIndex = padText.LastIndexOf(')');
                padText = padText.Insert(closeIndex, $"{Environment.NewLine}      {netText}");
            }

            footprintText = footprintText.Remove(range.Start, range.Length).Insert(range.Start, padText);
        }

        File.WriteAllText(boardFile, text.Remove(footprintStart, footprintEnd - footprintStart + 1).Insert(footprintStart, footprintText));
    }

    private static int FindMatchingParenthesis(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = openIndex; index < text.Length; index++)
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
                depth++;
            }
            else if (current == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        throw new InvalidOperationException("Could not find matching parenthesis.");
    }

    private static bool IsOnSchematicGrid(double value)
    {
        var scaled = value / SchematicGridMillimeters;
        return Math.Abs(scaled - Math.Round(scaled)) < 0.001;
    }

    private static bool IsWireEndpoint(double x, double y, SchematicWireSummary wire)
    {
        return (Math.Abs(x - wire.X1Millimeters) < 0.001 && Math.Abs(y - wire.Y1Millimeters) < 0.001)
            || (Math.Abs(x - wire.X2Millimeters) < 0.001 && Math.Abs(y - wire.Y2Millimeters) < 0.001);
    }

    private static bool ApproximatelyOnWire(double x, double y, SchematicWireSummary wire)
    {
        var horizontal = Math.Abs(wire.Y1Millimeters - wire.Y2Millimeters) < 0.001
            && Math.Abs(y - wire.Y1Millimeters) < 0.001
            && x >= Math.Min(wire.X1Millimeters, wire.X2Millimeters) - 0.001
            && x <= Math.Max(wire.X1Millimeters, wire.X2Millimeters) + 0.001;
        var vertical = Math.Abs(wire.X1Millimeters - wire.X2Millimeters) < 0.001
            && Math.Abs(x - wire.X1Millimeters) < 0.001
            && y >= Math.Min(wire.Y1Millimeters, wire.Y2Millimeters) - 0.001
            && y <= Math.Max(wire.Y1Millimeters, wire.Y2Millimeters) + 0.001;
        return horizontal || vertical;
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public async Task<CommandExecutionResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index] == "--output")
                {
                    await File.WriteAllTextAsync(arguments[index + 1], "[]", cancellationToken);
                }
            }

            return new CommandExecutionResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class TempFile : IDisposable
    {
        private readonly bool _deleteOnDispose;

        public TempFile(string fileName, bool deleteOnDispose = true)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcbhelper-tests", Guid.NewGuid().ToString("N"), fileName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, string.Empty);
            _deleteOnDispose = deleteOnDispose;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (_deleteOnDispose && Directory.Exists(System.IO.Path.GetDirectoryName(Path)))
            {
                Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
            }
        }
    }
}
