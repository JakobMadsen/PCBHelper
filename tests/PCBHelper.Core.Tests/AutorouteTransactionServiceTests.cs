using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class AutorouteTransactionServiceTests
{
    [Fact]
    public async Task FindDisallowedTracks_Reports_Bottom_Track_For_Front_Only_Constraint()
    {
        using var fixture = new TemporaryBoard(
            """
            (kicad_pcb (version 20240108) (generator pcbnew)
              (general (thickness 1.6))
              (paper "A4")
              (layers (0 "F.Cu" signal) (31 "B.Cu" signal))
              (setup (pad_to_mask_clearance 0))
              (net 0 "")
              (net 1 "SIG")
              (segment (start 10 10) (end 20 10) (width 0.2) (layer "F.Cu") (net 1) (uuid "11111111-1111-1111-1111-111111111111"))
              (segment (start 20 10) (end 20 20) (width 0.2) (layer "B.Cu") (net 1) (uuid "22222222-2222-2222-2222-222222222222"))
            )
            """);

        var findings = AutorouteTransactionService.FindDisallowedTracks(
            fixture.BoardFile,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "F.Cu" });

        var finding = Assert.Single(findings);
        Assert.Equal("B.Cu", finding.Layer);
        Assert.Equal("22222222-2222-2222-2222-222222222222", finding.Uuid);
    }

    private sealed class TemporaryBoard : IDisposable
    {
        private readonly string _directory;

        public TemporaryBoard(string content)
        {
            _directory = Path.Combine(Path.GetTempPath(), "pcbhelper-layer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            BoardFile = Path.Combine(_directory, "board.kicad_pcb");
            File.WriteAllText(BoardFile, content);
        }

        public string BoardFile { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
    }
}
