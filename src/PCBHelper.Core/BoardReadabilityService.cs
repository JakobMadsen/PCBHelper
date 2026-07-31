using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed class BoardReadabilityService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ProjectDiscoveryService _projects;

    public BoardReadabilityService(ProjectDiscoveryService projects) => _projects = projects;

    public ToolResponse<BoardReadabilityReport> Analyze(string projectPath, string? outputDirectory = null)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data?.BoardFile is null || !File.Exists(project.Data.BoardFile))
            return ToolResponse<BoardReadabilityReport>.Fail(
                project.Summary,
                project.Error?.Code ?? "BOARD_FILE_MISSING",
                project.Error?.Message);

        try
        {
            var board = KiCadBoardParser.Parse(project.Data.BoardFile);
            var model = BoardVisualModel.Parse(board);
            var findings = Analyze(model);
            string? jsonPath = null;
            string? markdownPath = null;
            var report = new BoardReadabilityReport(
                "board-readability-v1",
                board.BoardFile,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(board.BoardFile))),
                new BoardReadabilitySummary(
                    findings.Count(static item => item.Severity == "error"),
                    findings.Count(static item => item.Severity == "warning"),
                    findings.Count(static item => item.Severity == "info")),
                findings,
                null,
                null);

            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                var authorized = _projects.AuthorizePath(Path.GetFullPath(outputDirectory));
                if (!authorized.Success || authorized.Data is null)
                    return ToolResponse<BoardReadabilityReport>.Fail(
                        authorized.Summary,
                        authorized.Error?.Code ?? "PROJECT_SCOPE_VIOLATION",
                        authorized.Error?.Message);
                Directory.CreateDirectory(authorized.Data);
                jsonPath = Path.Combine(authorized.Data, "board-readability.json");
                markdownPath = Path.Combine(authorized.Data, "board-readability.md");
                report = report with { JsonReportPath = jsonPath, MarkdownReportPath = markdownPath };
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
                File.WriteAllText(markdownPath, RenderMarkdown(report));
            }

            return ToolResponse<BoardReadabilityReport>.Ok(
                $"Board readability analysis found {findings.Count} finding(s).",
                report);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return ToolResponse<BoardReadabilityReport>.Fail(
                "Board readability analysis failed.",
                "BOARD_READABILITY_FAILED",
                exception.Message);
        }
    }

    private static IReadOnlyList<BoardReadabilityFinding> Analyze(BoardVisualModel model)
    {
        var findings = new List<BoardReadabilityFinding>();
        CheckLabels(model, findings);
        CheckOrientation(model, findings);
        CheckSilkscreenGeometry(model, findings);
        return findings
            .OrderBy(static item => item.Code, StringComparer.Ordinal)
            .ThenBy(static item => item.Reference, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Layer, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void CheckLabels(BoardVisualModel model, ICollection<BoardReadabilityFinding> findings)
    {
        foreach (var footprint in model.Footprints)
        {
            var nearby = model.Texts.Where(text => text.Visible
                && text.Layer.EndsWith("SilkS", StringComparison.OrdinalIgnoreCase)
                && (text.OwnerReference is null
                    || text.OwnerReference.Equals(footprint.Reference, StringComparison.OrdinalIgnoreCase))
                && Distance(text.Bounds.CenterX, text.Bounds.CenterY, footprint.X, footprint.Y) <= 7.5)
                .ToArray();
            var functional = nearby.Where(text =>
                    !text.Text.Equals(footprint.Reference, StringComparison.OrdinalIgnoreCase)
                    && !text.Text.Equals(footprint.Value, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (footprint.Reference.StartsWith("TP", StringComparison.OrdinalIgnoreCase))
            {
                var net = footprint.Pads.Select(static pad => pad.NetName).FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));
                if (net is not null && !functional.Any(text => LabelMatches(text.Text, net)))
                    findings.Add(Finding(
                        "BOARD_TP_LABEL_MISSING",
                        "warning",
                        footprint.Reference,
                        footprint.Side == "back" ? "B.SilkS" : "F.SilkS",
                        $"{footprint.Reference} has no visible functional label for net {net}.",
                        footprint.Bounds));
            }

            if (footprint.Reference.StartsWith("JP", StringComparison.OrdinalIgnoreCase)
                && functional.All(static text => text.Text.Length <= 2 || IsElectricalMarker(text.Text)))
                findings.Add(Finding(
                    "BOARD_JUMPER_PURPOSE_MISSING",
                    "warning",
                    footprint.Reference,
                    footprint.Side == "back" ? "B.SilkS" : "F.SilkS",
                    $"{footprint.Reference} has no visible purpose label.",
                    footprint.Bounds));

            if (footprint.Reference.StartsWith("J", StringComparison.OrdinalIgnoreCase)
                && !footprint.Reference.StartsWith("JP", StringComparison.OrdinalIgnoreCase)
                && footprint.Pads.Count > 1)
            {
                var nets = footprint.Pads.Select(static pad => pad.NetName)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var missing = nets.Where(net => !functional.Any(text => LabelMatches(text.Text, net!))).ToArray();
                if (missing.Length > 0)
                    findings.Add(Finding(
                        "BOARD_CONNECTOR_LABEL_MISSING",
                        "warning",
                        footprint.Reference,
                        footprint.Side == "back" ? "B.SilkS" : "F.SilkS",
                        $"{footprint.Reference} lacks visible pin labels for: {string.Join(", ", missing)}.",
                        footprint.Bounds));
            }
        }
    }

    private static void CheckOrientation(BoardVisualModel model, ICollection<BoardReadabilityFinding> findings)
    {
        foreach (var footprint in model.Footprints.Where(static item => RequiresOrientationMark(item)))
        {
            var pinOne = footprint.Pads.FirstOrDefault(static pad => pad.Name == "1") ?? footprint.Pads.FirstOrDefault();
            if (pinOne is null)
                continue;
            var hasTextMarker = model.Texts.Any(text => text.Visible
                && IsElectricalMarker(text.Text)
                && Distance(text.Bounds.CenterX, text.Bounds.CenterY, pinOne.X, pinOne.Y) <= 3.5);
            // Generic footprint outline segments are not reliable orientation evidence. In v1 only
            // an explicit visible textual polarity/pin marker is accepted automatically.
            if (!hasTextMarker)
                findings.Add(Finding(
                    "BOARD_ORIENTATION_MARK_MISSING",
                    "warning",
                    footprint.Reference,
                    footprint.Side == "back" ? "B.SilkS" : "F.SilkS",
                    $"{footprint.Reference} has no verifiable pin-1, cathode, or polarity mark near its orientation-sensitive pad.",
                    pinOne.Bounds));
        }
    }

    private static void CheckSilkscreenGeometry(BoardVisualModel model, ICollection<BoardReadabilityFinding> findings)
    {
        var visible = model.Texts.Where(static item => item.Visible && item.Layer.EndsWith("SilkS", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var footprint in model.Footprints)
        foreach (var primitive in footprint.SilkscreenPrimitives)
        foreach (var pad in model.Footprints.SelectMany(static item => item.Pads))
            if (SameSide(primitive.Layer, pad.Layer) && primitive.Bounds.Intersects(pad.Bounds))
                findings.Add(new BoardReadabilityFinding(
                    "BOARD_SILK_PAD_OVERLAP", "warning", footprint.Reference, primitive.Layer,
                    $"{footprint.Reference} silkscreen {primitive.Kind} overlaps {pad.Reference} pad {pad.Name} or its mask opening.",
                    new
                    {
                        silkscreen = new BoardReadabilityGeometry(primitive.Bounds.Left, primitive.Bounds.Top, primitive.Bounds.Right, primitive.Bounds.Bottom),
                        padReference = pad.Reference,
                        padName = pad.Name,
                        pad.NetName,
                        pad = new BoardReadabilityGeometry(pad.Bounds.Left, pad.Bounds.Top, pad.Bounds.Right, pad.Bounds.Bottom)
                    }));

        foreach (var text in visible)
        {
            foreach (var pad in model.Footprints.SelectMany(static item => item.Pads))
                if (SameSide(text.Layer, pad.Layer) && text.Bounds.Intersects(pad.Bounds))
                    findings.Add(Finding(
                        "BOARD_SILK_PAD_OVERLAP", "warning", text.OwnerReference, text.Layer,
                        $"Silkscreen text '{text.Text}' overlaps a pad or mask opening.", text.Bounds));

            foreach (var footprint in model.Footprints)
                if (SameSide(text.Layer, footprint.Side == "back" ? "B.SilkS" : "F.SilkS")
                    && text.Bounds.Intersects(footprint.Bounds)
                    && !IsReferenceOutsideOwnBody(text, footprint))
                    findings.Add(Finding(
                        "BOARD_SILK_OCCLUDED_AFTER_ASSEMBLY", "warning", text.OwnerReference ?? footprint.Reference, text.Layer,
                        $"Silkscreen text '{text.Text}' may be hidden by {footprint.Reference} after assembly.", text.Bounds));

            if (model.BoardOutline is not null && !model.BoardOutline.Value.Contains(text.Bounds))
                findings.Add(Finding(
                    "BOARD_SILK_OUTSIDE_BOARD", "warning", text.OwnerReference, text.Layer,
                    $"Silkscreen text '{text.Text}' extends outside the board outline.", text.Bounds));
        }

        for (var first = 0; first < visible.Length; first++)
        for (var second = first + 1; second < visible.Length; second++)
            if (visible[first].Layer.Equals(visible[second].Layer, StringComparison.OrdinalIgnoreCase)
                && visible[first].Bounds.Intersects(visible[second].Bounds))
                findings.Add(Finding(
                    "BOARD_SILK_TEXT_OVERLAP", "warning", visible[first].OwnerReference, visible[first].Layer,
                    $"Silkscreen texts '{visible[first].Text}' and '{visible[second].Text}' overlap.",
                    visible[first].Bounds));

        var incomplete = model.Footprints.Where(static item => !item.HasDocumentedEnvelope).Select(static item => item.Reference).ToArray();
        if (incomplete.Length > 0)
            findings.Add(new BoardReadabilityFinding(
                "BOARD_GEOMETRY_INCOMPLETE",
                "info",
                null,
                null,
                $"{incomplete.Length} footprint(s) use a conservative pad-envelope fallback.",
                new { references = incomplete }));
    }

    private static bool IsReferenceOutsideOwnBody(BoardVisualText text, BoardVisualFootprint footprint) =>
        text.OwnerReference is not null
        && text.OwnerReference.Equals(footprint.Reference, StringComparison.OrdinalIgnoreCase)
        && !footprint.Bounds.Contains(text.Bounds);

    private static bool RequiresOrientationMark(BoardVisualFootprint footprint) =>
        footprint.Reference.StartsWith("D", StringComparison.OrdinalIgnoreCase)
        || footprint.Reference.StartsWith("U", StringComparison.OrdinalIgnoreCase)
        || footprint.Reference.StartsWith("Q", StringComparison.OrdinalIgnoreCase)
        || footprint.Value.Contains("polar", StringComparison.OrdinalIgnoreCase)
        || footprint.Value.Contains("electro", StringComparison.OrdinalIgnoreCase)
        || footprint.Name.Contains("CP_", StringComparison.OrdinalIgnoreCase);

    private static bool LabelMatches(string text, string net)
    {
        static string Normalize(string value) => Regex.Replace(value.Trim().TrimStart('/').ToUpperInvariant(), "[^A-Z0-9]+", string.Empty);
        return Normalize(text) == Normalize(net);
    }

    private static bool IsElectricalMarker(string value) =>
        value.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("+", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("-", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("K", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("A", StringComparison.OrdinalIgnoreCase);

    private static bool SameSide(string first, string second) =>
        first.StartsWith("B.", StringComparison.OrdinalIgnoreCase) == second.StartsWith("B.", StringComparison.OrdinalIgnoreCase);
    private static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
    private static BoardReadabilityFinding Finding(string code, string severity, string? reference, string? layer, string message, BoardVisualRectangle bounds) =>
        new(code, severity, reference, layer, message, new BoardReadabilityGeometry(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));

    private static string RenderMarkdown(BoardReadabilityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Board Readability Report").AppendLine();
        builder.AppendLine($"- Version: `{report.Version}`");
        builder.AppendLine($"- Board SHA-256: `{report.BoardSha256}`");
        builder.AppendLine($"- Findings: {report.Findings.Count}").AppendLine();
        builder.AppendLine("| Severity | Code | Reference | Message |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var finding in report.Findings)
            builder.AppendLine($"| {finding.Severity} | `{finding.Code}` | {finding.Reference ?? "-"} | {finding.Message.Replace("|", "\\|", StringComparison.Ordinal)} |");
        return builder.ToString();
    }
}

public sealed record BoardReadabilityReport(
    string Version,
    string BoardFile,
    string BoardSha256,
    BoardReadabilitySummary Summary,
    IReadOnlyList<BoardReadabilityFinding> Findings,
    string? JsonReportPath,
    string? MarkdownReportPath);
public sealed record BoardReadabilitySummary(int Error, int Warning, int Info);
public sealed record BoardReadabilityFinding(string Code, string Severity, string? Reference, string? Layer, string Message, object Evidence);
public sealed record BoardReadabilityGeometry(double Left, double Top, double Right, double Bottom);

internal sealed class BoardVisualModel
{
    private BoardVisualModel(IReadOnlyList<BoardVisualFootprint> footprints, IReadOnlyList<BoardVisualText> texts, BoardVisualRectangle? boardOutline)
    {
        Footprints = footprints; Texts = texts; BoardOutline = boardOutline;
    }
    public IReadOnlyList<BoardVisualFootprint> Footprints { get; }
    public IReadOnlyList<BoardVisualText> Texts { get; }
    public BoardVisualRectangle? BoardOutline { get; }

    public static BoardVisualModel Parse(KiCadBoardDocument board)
    {
        var texts = new List<BoardVisualText>();
        foreach (var block in Blocks(board.Text, "gr_text"))
            if (TryParseText(block, null, 0, 0, 0, out var text)) texts.Add(text);

        var footprints = new List<BoardVisualFootprint>();
        foreach (var source in board.Footprints)
        {
            var reference = source.Reference ?? string.Empty;
            var value = source.Properties.TryGetValue("Value", out var valueProperty) ? valueProperty.Value : string.Empty;
            var x = source.XMillimeters ?? 0;
            var y = source.YMillimeters ?? 0;
            var rotation = source.RotationDegrees ?? 0;
            var sourceText = board.Text.Substring(source.SourceStart, source.SourceLength);
            foreach (var keyword in new[] { "property", "fp_text" })
            foreach (var block in Blocks(sourceText, keyword))
                if (TryParseText(block, reference, x, y, rotation, out var text)) texts.Add(text);

            var pads = source.Pads.Select(pad =>
            {
                var point = BoardInspectionService.CalculateAbsolutePadPosition(source, pad);
                var px = point.X ?? x; var py = point.Y ?? y;
                var width = pad.SizeXMillimeters ?? 0; var height = pad.SizeYMillimeters ?? 0;
                return new BoardVisualPad(reference, pad.Name, pad.NetName, px, py,
                    source.Side == "back" ? "B.Mask" : "F.Mask",
                    new BoardVisualRectangle(px - width / 2, py - height / 2, px + width / 2, py + height / 2));
            }).ToArray();
            var courtyard = PrimitiveBounds(sourceText, x, y, rotation, "CrtYd");
            var fab = PrimitiveBounds(sourceText, x, y, rotation, ".Fab");
            var documented = courtyard ?? fab;
            var fallback = Union(pads.Select(static pad => pad.Bounds).ToArray()) ?? new BoardVisualRectangle(x - 1, y - 1, x + 1, y + 1);
            var silk = ParsePrimitives(sourceText, x, y, rotation, "SilkS");
            footprints.Add(new BoardVisualFootprint(reference, source.FootprintName, value, source.Side, x, y, documented ?? fallback, documented is not null, pads, silk));
        }

        var edge = PrimitiveBounds(board.Text, 0, 0, 0, "Edge.Cuts");
        return new BoardVisualModel(footprints, texts, edge);
    }

    private static bool TryParseText(string block, string? owner, double originX, double originY, double rotation, out BoardVisualText text)
    {
        var property = Regex.Match(block, "^\\(property\\s+\"(?<kind>[^\"]+)\"\\s+\"(?<text>[^\"]*)\"");
        var graphic = Regex.Match(block, "^\\((?:gr_text|fp_text)\\s+(?:(?:user|reference|value)\\s+)?\"(?<text>[^\"]*)\"");
        var match = property.Success ? property : graphic;
        if (!match.Success) { text = null!; return false; }
        var at = Regex.Match(block, @"\(at\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)(?:\s+(?<r>-?\d+(?:\.\d+)?))?\)");
        var layer = Regex.Match(block, "\\(layer\\s+\"(?<layer>[^\"]+)\"\\)");
        if (!at.Success || !layer.Success) { text = null!; return false; }
        var localX = double.Parse(at.Groups["x"].Value, CultureInfo.InvariantCulture);
        var localY = double.Parse(at.Groups["y"].Value, CultureInfo.InvariantCulture);
        var point = owner is null ? (localX, localY) : Transform(localX, localY, originX, originY, rotation);
        var font = Regex.Match(block, @"\(size\s+(?<x>\d+(?:\.\d+)?)\s+(?<y>\d+(?:\.\d+)?)\)");
        var fontX = font.Success ? double.Parse(font.Groups["x"].Value, CultureInfo.InvariantCulture) : 1.27;
        var fontY = font.Success ? double.Parse(font.Groups["y"].Value, CultureInfo.InvariantCulture) : 1.27;
        var value = match.Groups["text"].Value;
        var width = Math.Max(fontX, value.Length * fontX * 0.62);
        var height = fontY;
        var visible = !Regex.IsMatch(block, @"\(hide\s+yes\)|\(hide\)", RegexOptions.IgnoreCase);
        text = new BoardVisualText(value, owner, layer.Groups["layer"].Value, visible,
            new BoardVisualRectangle(point.Item1 - width / 2, point.Item2 - height / 2, point.Item1 + width / 2, point.Item2 + height / 2));
        return true;
    }

    private static IReadOnlyList<BoardVisualPrimitive> ParsePrimitives(string text, double x, double y, double rotation, string layerFragment)
    {
        var result = new List<BoardVisualPrimitive>();
        foreach (var keyword in new[] { "fp_line", "fp_rect", "fp_circle", "fp_arc", "fp_poly", "gr_line", "gr_rect", "gr_circle", "gr_arc", "gr_poly" })
        foreach (var block in Blocks(text, keyword))
        {
            var layer = Regex.Match(block, "\\(layer\\s+\"(?<layer>[^\"]+)\"\\)");
            if (!layer.Success || !layer.Groups["layer"].Value.Contains(layerFragment, StringComparison.OrdinalIgnoreCase)) continue;
            var points = Regex.Matches(block, @"\((?:start|end|mid|center|xy)\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)\)")
                .Select(match => Transform(double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture), x, y, rotation))
                .ToArray();
            var bounds = Bounds(points);
            if (bounds is not null) result.Add(new BoardVisualPrimitive(keyword, layer.Groups["layer"].Value, bounds.Value));
        }
        return result;
    }

    private static BoardVisualRectangle? PrimitiveBounds(string text, double x, double y, double rotation, string layerFragment) =>
        Union(ParsePrimitives(text, x, y, rotation, layerFragment).Select(static item => item.Bounds).ToArray());
    private static BoardVisualRectangle? Bounds(IReadOnlyList<(double X, double Y)> points) => points.Count == 0 ? null : new(points.Min(static p => p.X), points.Min(static p => p.Y), points.Max(static p => p.X), points.Max(static p => p.Y));
    private static BoardVisualRectangle? Union(IReadOnlyList<BoardVisualRectangle> values) => values.Count == 0 ? null : new(values.Min(static p => p.Left), values.Min(static p => p.Top), values.Max(static p => p.Right), values.Max(static p => p.Bottom));
    private static (double, double) Transform(double localX, double localY, double x, double y, double degrees)
    {
        var radians = degrees * Math.PI / 180; var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        return (x + localX * cos - localY * sin, y + localX * sin + localY * cos);
    }

    private static IEnumerable<string> Blocks(string text, string keyword)
    {
        var search = 0;
        while ((search = text.IndexOf($"({keyword}", search, StringComparison.Ordinal)) >= 0)
        {
            var boundary = search + keyword.Length + 1;
            if (boundary < text.Length && !char.IsWhiteSpace(text[boundary])) { search = boundary; continue; }
            var end = KiCadSchematicParser.FindMatchingParenthesis(text, search);
            if (end < 0) yield break;
            yield return text.Substring(search, end - search + 1);
            search = end + 1;
        }
    }
}

internal sealed record BoardVisualFootprint(string Reference, string Name, string Value, string Side, double X, double Y, BoardVisualRectangle Bounds, bool HasDocumentedEnvelope, IReadOnlyList<BoardVisualPad> Pads, IReadOnlyList<BoardVisualPrimitive> SilkscreenPrimitives);
internal sealed record BoardVisualPad(string Reference, string Name, string? NetName, double X, double Y, string Layer, BoardVisualRectangle Bounds);
internal sealed record BoardVisualText(string Text, string? OwnerReference, string Layer, bool Visible, BoardVisualRectangle Bounds);
internal sealed record BoardVisualPrimitive(string Kind, string Layer, BoardVisualRectangle Bounds);
internal readonly record struct BoardVisualRectangle(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left; public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2; public double CenterY => (Top + Bottom) / 2;
    public bool Intersects(BoardVisualRectangle other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
    public bool Contains(BoardVisualRectangle other) => other.Left >= Left && other.Right <= Right && other.Top >= Top && other.Bottom <= Bottom;
}
