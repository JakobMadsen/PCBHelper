namespace PCBHelper.Core;

public sealed class BoardInspectionService
{
    private readonly ProjectDiscoveryService _projectDiscovery;

    public BoardInspectionService(ProjectDiscoveryService projectDiscovery)
    {
        _projectDiscovery = projectDiscovery;
    }

    public ToolResponse<NetListResult> ListNets(string projectPath)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<NetListResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var nets = board.Data.Nets.Select(net => ToSummary(board.Data, net)).ToArray();
        return ToolResponse<NetListResult>.Ok($"Found {nets.Length} net(s).", new NetListResult(board.Data.BoardFile, nets));
    }

    public ToolResponse<NetSummary> GetNet(string projectPath, string net)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<NetSummary>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var match = board.Data.Nets.FirstOrDefault(item =>
            string.Equals(item.Name, net, StringComparison.OrdinalIgnoreCase)
            || item.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) == net);
        if (match is null)
        {
            return ToolResponse<NetSummary>.Fail($"Net not found: {net}", "NET_NOT_FOUND");
        }

        return ToolResponse<NetSummary>.Ok($"Found net {match.Name}.", ToSummary(board.Data, match));
    }

    public ToolResponse<FootprintPadsResult> ListFootprintPads(string projectPath, string reference)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<FootprintPadsResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var footprint = board.Data.Footprints.FirstOrDefault(item => string.Equals(item.Reference, reference, StringComparison.OrdinalIgnoreCase));
        if (footprint is null)
        {
            return ToolResponse<FootprintPadsResult>.Fail($"Footprint not found: {reference}", "FOOTPRINT_NOT_FOUND");
        }

        var pads = footprint.Pads.Select(pad =>
        {
            var absolute = CalculateAbsolutePadPosition(footprint, pad);
            return new PadSummary(
            pad.Name,
            pad.Type,
            pad.XMillimeters,
            pad.YMillimeters,
            absolute.X,
            absolute.Y,
            pad.Layers,
            pad.NetCode,
            pad.NetName,
            pad.PinFunction);
        }).ToArray();
        return ToolResponse<FootprintPadsResult>.Ok($"Found {pads.Length} pad(s) for {reference}.", new FootprintPadsResult(reference, pads));
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

    private static NetSummary ToSummary(KiCadBoardDocument board, KiCadNet net)
    {
        var pads = board.Footprints
            .Where(static footprint => footprint.Reference is not null)
            .SelectMany(footprint => footprint.Pads
                .Where(pad => pad.NetCode == net.Code)
                .Select(pad =>
                {
                    var absolute = CalculateAbsolutePadPosition(footprint, pad);
                    return new NetPadSummary(
                        footprint.Reference!,
                        pad.Name,
                        pad.PinFunction,
                        footprint.Side,
                        pad.XMillimeters,
                        pad.YMillimeters,
                        absolute.X,
                        absolute.Y,
                        pad.Layers,
                        footprint.XMillimeters,
                        footprint.YMillimeters);
                }))
            .ToArray();

        return new NetSummary(net.Code, net.Name, pads);
    }

    internal static (double? X, double? Y) CalculateAbsolutePadPosition(KiCadFootprint footprint, KiCadPad pad)
    {
        if (footprint.XMillimeters is null || footprint.YMillimeters is null || pad.XMillimeters is null || pad.YMillimeters is null)
        {
            return (null, null);
        }

        var rotation = footprint.RotationDegrees ?? 0;
        var radians = rotation * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var x = footprint.XMillimeters.Value + pad.XMillimeters.Value * cos - pad.YMillimeters.Value * sin;
        var y = footprint.YMillimeters.Value + pad.XMillimeters.Value * sin + pad.YMillimeters.Value * cos;
        return (x, y);
    }
}

public sealed record NetListResult(string BoardFile, IReadOnlyList<NetSummary> Nets);

public sealed record NetSummary(int Code, string Name, IReadOnlyList<NetPadSummary> Pads);

public sealed record NetPadSummary(
    string FootprintReference,
    string PadName,
    string? PinFunction,
    string FootprintSide,
    double? PadXMillimeters,
    double? PadYMillimeters,
    double? AbsoluteXMillimeters,
    double? AbsoluteYMillimeters,
    IReadOnlyList<string> PadLayers,
    double? FootprintXMillimeters,
    double? FootprintYMillimeters);

public sealed record FootprintPadsResult(string Reference, IReadOnlyList<PadSummary> Pads);

public sealed record PadSummary(
    string Name,
    string? Type,
    double? XMillimeters,
    double? YMillimeters,
    double? AbsoluteXMillimeters,
    double? AbsoluteYMillimeters,
    IReadOnlyList<string> Layers,
    int? NetCode,
    string? NetName,
    string? PinFunction);
