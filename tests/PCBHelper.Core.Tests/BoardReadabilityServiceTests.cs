namespace PCBHelper.Core.Tests;

public sealed class BoardReadabilityServiceTests
{
    [Fact]
    public void Analyze_Finds_Unlabeled_Testpoint_Without_Changing_Board()
    {
        using var fixture = CopyBlankFixture();
        var boardPath = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");
        var board = File.ReadAllText(boardPath);
        var footprint = """

  (footprint "PCBHelper:TestPoint"
    (layer "F.Cu")
    (at 20 20)
    (property "Reference" "TP1" (at 0 -2 0) (layer "F.SilkS") (hide yes) (effects (font (size 1.27 1.27))))
    (property "Value" "TestPoint" (at 0 2 0) (layer "F.Fab") (hide yes) (effects (font (size 1.27 1.27))))
    (pad "1" thru_hole circle (at 0 0) (size 2 2) (drill 1) (layers "*.Cu" "*.Mask") (net 1 "GND"))
  )
""";
        File.WriteAllText(boardPath, board.Insert(board.LastIndexOf(')'), footprint));
        var before = File.ReadAllText(boardPath);

        var result = new BoardReadabilityService(new ProjectDiscoveryService()).Analyze(fixture.Path);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("board-readability-v1", result.Data!.Version);
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(boardPath))),
            result.Data.BoardSha256);
        Assert.Contains(result.Data.Findings, finding =>
            finding.Code == "BOARD_TP_LABEL_MISSING" && finding.Reference == "TP1");
        Assert.Equal(before, File.ReadAllText(boardPath));
    }

    [Fact]
    public void Analyze_Finds_Missing_Connector_Jumper_And_Orientation_Marks()
    {
        using var fixture = CopyBlankFixture();
        var boardPath = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");
        var board = File.ReadAllText(boardPath);
        var footprints = """

  (footprint "Connector:Header" (layer "F.Cu") (at 20 20)
    (property "Reference" "J1" (at 0 -3 0) (layer "F.SilkS") (effects (font (size 1 1))))
    (property "Value" "POWER" (at 0 3 0) (layer "F.Fab") (hide yes) (effects (font (size 1 1))))
    (pad "1" thru_hole circle (at -1 0) (size 2 2) (drill 1) (layers "*.Cu" "*.Mask") (net 1 "5V"))
    (pad "2" thru_hole circle (at 1 0) (size 2 2) (drill 1) (layers "*.Cu" "*.Mask") (net 2 "GND")))
  (footprint "Jumper:SolderJumper" (layer "F.Cu") (at 40 20)
    (property "Reference" "JP1" (at 0 -2 0) (layer "F.SilkS") (effects (font (size 1 1))))
    (property "Value" "Jumper" (at 0 2 0) (layer "F.Fab") (hide yes) (effects (font (size 1 1))))
    (pad "1" smd rect (at -1 0) (size 1 1) (layers "F.Cu" "F.Mask") (net 3 "A"))
    (pad "2" smd rect (at 1 0) (size 1 1) (layers "F.Cu" "F.Mask") (net 4 "B")))
  (footprint "Diode:SOD" (layer "F.Cu") (at 60 20)
    (property "Reference" "D1" (at 0 -2 0) (layer "F.SilkS") (effects (font (size 1 1))))
    (property "Value" "DIODE" (at 0 2 0) (layer "F.Fab") (hide yes) (effects (font (size 1 1))))
    (pad "1" smd rect (at -1 0) (size 1 1) (layers "F.Cu" "F.Mask") (net 3 "A"))
    (pad "2" smd rect (at 1 0) (size 1 1) (layers "F.Cu" "F.Mask") (net 4 "B")))
""";
        File.WriteAllText(boardPath, board.Insert(board.LastIndexOf(')'), footprints));

        var result = new BoardReadabilityService(new ProjectDiscoveryService()).Analyze(fixture.Path);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(result.Data!.Findings, item => item.Code == "BOARD_CONNECTOR_LABEL_MISSING" && item.Reference == "J1");
        Assert.Contains(result.Data.Findings, item => item.Code == "BOARD_JUMPER_PURPOSE_MISSING" && item.Reference == "JP1");
        Assert.Contains(result.Data.Findings, item => item.Code == "BOARD_ORIENTATION_MARK_MISSING" && item.Reference == "D1");
    }

    [Fact]
    public void Analyze_Finds_Silkscreen_Geometry_Problems()
    {
        using var fixture = CopyBlankFixture();
        var boardPath = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");
        var board = File.ReadAllText(boardPath);
        var additions = """

  (footprint "Device:R" (layer "F.Cu") (at 40 40)
    (property "Reference" "R1" (at 0 -4 0) (layer "F.SilkS") (effects (font (size 1 1))))
    (property "Value" "1k" (at 0 4 0) (layer "F.Fab") (hide yes) (effects (font (size 1 1))))
    (fp_rect (start -3 -3) (end 3 3) (stroke (width 0.1) (type default)) (fill none) (layer "F.Fab"))
    (fp_line (start -1 0) (end 1 0) (stroke (width 0.2) (type default)) (layer "F.SilkS"))
    (pad "1" smd rect (at 0 0) (size 2 2) (layers "F.Cu" "F.Mask") (net 1 "N1")))
  (gr_text "PAD" (at 40 40) (layer "F.SilkS") (effects (font (size 1 1))))
  (gr_text "OVER" (at 40 40) (layer "F.SilkS") (effects (font (size 1 1))))
  (gr_text "OUTSIDE" (at 10 10) (layer "F.SilkS") (effects (font (size 1 1))))
""";
        File.WriteAllText(boardPath, board.Insert(board.LastIndexOf(')'), additions));

        var result = new BoardReadabilityService(new ProjectDiscoveryService()).Analyze(fixture.Path);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(result.Data!.Findings, item => item.Code == "BOARD_SILK_PAD_OVERLAP");
        Assert.Contains(result.Data.Findings, item => item.Code == "BOARD_SILK_PAD_OVERLAP" && item.Reference == "R1");
        Assert.Contains(result.Data.Findings, item => item.Code == "BOARD_SILK_TEXT_OVERLAP");
        Assert.Contains(result.Data.Findings, item => item.Code == "BOARD_SILK_OCCLUDED_AFTER_ASSEMBLY");
        Assert.Contains(result.Data.Findings, item => item.Code == "BOARD_SILK_OUTSIDE_BOARD");
    }

    private static TempDirectory CopyBlankFixture()
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", "blank-authoring");
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        return temp;
    }
}
