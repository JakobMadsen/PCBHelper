using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed partial class BoardSummaryService
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

        var text = File.ReadAllText(project.Data.BoardFile);
        var footprints = ParseFootprints(text);
        var board = new BoardSummary(project.Data.BoardFile, footprints);
        return ToolResponse<BoardSummary>.Ok($"Found {footprints.Count} footprint(s) in {project.Data.ProjectName}.", board);
    }

    private static IReadOnlyList<FootprintSummary> ParseFootprints(string boardText)
    {
        var footprints = new List<FootprintSummary>();
        foreach (Match match in FootprintRegex().Matches(boardText))
        {
            var body = match.Groups["body"].Value;
            var referenceMatch = ReferenceRegex().Match(body);
            var layerMatch = LayerRegex().Match(body);
            var atMatch = AtRegex().Match(body);

            footprints.Add(new FootprintSummary(
                referenceMatch.Success ? referenceMatch.Groups["reference"].Value : null,
                match.Groups["name"].Value,
                layerMatch.Success ? layerMatch.Groups["layer"].Value : null,
                layerMatch.Success && layerMatch.Groups["layer"].Value.StartsWith("B.", StringComparison.OrdinalIgnoreCase) ? "back" : "front",
                atMatch.Success ? double.Parse(atMatch.Groups["x"].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                atMatch.Success ? double.Parse(atMatch.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture) : null));
        }

        return footprints;
    }

    [GeneratedRegex(@"\(footprint\s+""(?<name>[^""]+)""(?<body>.*?)(?=\n\s*\(footprint|\n\s*\(gr_|\n\s*\(segment|\n\s*\(embedded_fonts|\n\))", RegexOptions.Singleline)]
    private static partial Regex FootprintRegex();

    [GeneratedRegex(@"\(property\s+""Reference""\s+""(?<reference>[^""]+)""")]
    private static partial Regex ReferenceRegex();

    [GeneratedRegex(@"\(layer\s+""(?<layer>[^""]+)""\)")]
    private static partial Regex LayerRegex();

    [GeneratedRegex(@"\(at\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)")]
    private static partial Regex AtRegex();
}

public sealed record BoardSummary(string BoardFile, IReadOnlyList<FootprintSummary> Footprints);

public sealed record FootprintSummary(
    string? Reference,
    string FootprintName,
    string? Layer,
    string Side,
    double? XMillimeters,
    double? YMillimeters);
