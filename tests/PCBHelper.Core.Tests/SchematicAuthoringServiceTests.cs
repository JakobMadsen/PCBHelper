using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class SchematicAuthoringServiceTests
{
    private const double SchematicGridMillimeters = 1.27;

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
