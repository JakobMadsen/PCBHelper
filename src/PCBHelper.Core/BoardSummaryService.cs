namespace PCBHelper.Core;

public sealed class BoardSummaryService
{
    private readonly ProjectDiscoveryService _projectDiscovery;

    public BoardSummaryService(ProjectDiscoveryService projectDiscovery)
    {
        _projectDiscovery = projectDiscovery;
    }

    public ToolResponse<BoardSummary> GetSummary(string projectPath)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<BoardSummary>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (project.Data.BoardFile is null)
        {
            return ToolResponse<BoardSummary>.Fail("Board summary requires a .kicad_pcb file.", "BOARD_FILE_MISSING");
        }

        var document = KiCadBoardParser.Parse(project.Data.BoardFile);
        var footprints = document.Footprints
            .Select(static footprint => new FootprintSummary(
                footprint.Reference,
                footprint.FootprintName,
                footprint.Layer,
                footprint.Side,
                footprint.XMillimeters,
                footprint.YMillimeters,
                footprint.RotationDegrees))
            .ToArray();
        var board = new BoardSummary(project.Data.BoardFile, footprints);
        return ToolResponse<BoardSummary>.Ok($"Found {footprints.Length} footprint(s) in {project.Data.ProjectName}.", board);
    }
}

public sealed record BoardSummary(string BoardFile, IReadOnlyList<FootprintSummary> Footprints);

public sealed record FootprintSummary(
    string? Reference,
    string FootprintName,
    string? Layer,
    string Side,
    double? XMillimeters,
    double? YMillimeters,
    double? RotationDegrees);
