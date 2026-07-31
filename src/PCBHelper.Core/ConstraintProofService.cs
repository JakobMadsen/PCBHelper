using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed class ConstraintProofService
{
    public const string DefaultRelativePath = ".pcbhelper/constraints-v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    private readonly ProjectDiscoveryService _projects;
    private readonly IProjectFileWriter _writer;

    public ConstraintProofService(ProjectDiscoveryService projects, IProjectFileWriter? writer = null)
    {
        _projects = projects;
        _writer = writer ?? new AtomicProjectFileWriter();
    }

    public ToolResponse<ConstraintValidationResult> Validate(string projectPath, string? constraintsPath = null)
    {
        var loaded = Load(projectPath, constraintsPath);
        if (!loaded.Success || loaded.Data is null)
            return ToolResponse<ConstraintValidationResult>.Fail(loaded.Summary, loaded.Error?.Code ?? "CONSTRAINTS_INVALID", loaded.Error?.Message);

        var errors = ValidateDocument(loaded.Data.Document).ToArray();
        var result = new ConstraintValidationResult(loaded.Data.Path, loaded.Data.Document.Constraints.Count, errors.Length == 0, errors);
        return errors.Length == 0
            ? ToolResponse<ConstraintValidationResult>.Ok($"Validated {result.ConstraintCount} constraint(s).", result)
            : ToolResponse<ConstraintValidationResult>.Fail("Constraint document is invalid.", "CONSTRAINTS_INVALID", errors[0], result);
    }

    public async Task<ToolResponse<ConstraintProofReport>> EvaluateAsync(
        string projectPath,
        string? constraintsPath = null,
        CancellationToken cancellationToken = default)
    {
        var loaded = Load(projectPath, constraintsPath);
        if (!loaded.Success || loaded.Data is null)
            return ToolResponse<ConstraintProofReport>.Fail(loaded.Summary, loaded.Error?.Code ?? "CONSTRAINTS_INVALID", loaded.Error?.Message);

        var errors = ValidateDocument(loaded.Data.Document).ToArray();
        if (errors.Length > 0)
            return ToolResponse<ConstraintProofReport>.Fail("Constraint document is invalid.", "CONSTRAINTS_INVALID", errors[0]);

        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data?.BoardFile is null)
            return ToolResponse<ConstraintProofReport>.Fail("Constraint proofs require a board file.", "BOARD_FILE_MISSING");

        var boardText = await File.ReadAllTextAsync(project.Data.BoardFile, cancellationToken);
        var constraintsText = await File.ReadAllTextAsync(loaded.Data.Path, cancellationToken);
        var inputHash = HashInputs(("board", boardText), ("constraints", constraintsText));
        var board = KiCadBoardParser.Parse(project.Data.BoardFile);
        var results = loaded.Data.Document.Constraints.Select(item => Evaluate(item, board, inputHash)).ToArray();
        var passed = results.All(static item => item.Outcome == ConstraintOutcome.Passed);
        var outputDirectory = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "reports", "constraints");
        var reportPath = Path.Combine(outputDirectory, $"{inputHash}.json");
        var report = new ConstraintProofReport(
            1,
            loaded.Data.Path,
            project.Data.BoardFile,
            inputHash,
            DateTimeOffset.UtcNow,
            passed,
            results,
            reportPath);
        Directory.CreateDirectory(outputDirectory);
        await _writer.WriteAtomicAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return ToolResponse<ConstraintProofReport>.Ok(
            passed ? "All layout constraints passed." : "One or more layout constraints failed or were unavailable.",
            report);
    }

    private static ConstraintResult Evaluate(ConstraintDefinition definition, KiCadBoardDocument board, string hash) => definition.Type switch
    {
        "allowed-layers" => AllowedLayers(definition, board, hash),
        "max-feature-distance" => FeatureDistance(definition, board, hash),
        "min-copper-area" => CopperArea(definition, board, hash),
        "max-zone-islands" => ZoneIslands(definition, board, hash),
        "max-trace-length" => TraceLength(definition, board, hash),
        "matched-trace-length" => MatchedTraceLength(definition, board, hash),
        "max-vias" => ViaCount(definition, board, hash),
        "footprint-region" => FootprintRegion(definition, board, hash),
        _ => Result(definition, ConstraintOutcome.Unavailable, null, null, "unsupported", [], hash, $"Unsupported constraint type: {definition.Type}")
    };

    private static ConstraintResult AllowedLayers(ConstraintDefinition definition, KiCadBoardDocument board, string hash)
    {
        if (!TrySegments(board, definition.Net, requireAny: definition.Net is not null, out var segments, out var unavailable))
            return Result(definition, ConstraintOutcome.Unavailable, null, 0, "violating-tracks", [], hash, unavailable!);
        var allowed = definition.AllowedLayers!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violating = segments.Where(item => item.Layer is null || !allowed.Contains(item.Layer)).ToArray();
        return Result(definition, violating.Length == 0 ? ConstraintOutcome.Passed : ConstraintOutcome.Failed,
            violating.Length, 0, "violating-tracks", violating.Select(static item => item.Uuid ?? item.Id).ToArray(), hash,
            violating.Length == 0 ? "All matching tracks use allowed layers." : $"{violating.Length} track(s) use forbidden layers.");
    }

    private static ConstraintResult FeatureDistance(ConstraintDefinition definition, KiCadBoardDocument board, string hash)
    {
        var from = ResolveFeature(board, definition.From);
        var to = ResolveFeature(board, definition.To);
        if (from is null || to is null)
            return Result(definition, ConstraintOutcome.Unavailable, null, definition.MaximumMm, "mm", [definition.From!, definition.To!], hash, "One or both features could not be resolved.");
        var distance = Math.Sqrt(Math.Pow(from.Value.X - to.Value.X, 2) + Math.Pow(from.Value.Y - to.Value.Y, 2));
        return Result(definition, distance <= definition.MaximumMm ? ConstraintOutcome.Passed : ConstraintOutcome.Failed,
            distance, definition.MaximumMm, "mm", [definition.From!, definition.To!], hash, $"Measured feature distance: {distance:0.###} mm.");
    }

    private static ConstraintResult CopperArea(ConstraintDefinition definition, KiCadBoardDocument board, string hash)
    {
        if (ResolveNet(board, definition.Net) is null)
            return Result(definition, ConstraintOutcome.Unavailable, null, definition.MinimumSquareMm, "mm2", [definition.Net!], hash, $"Net could not be resolved: {definition.Net}.");
        var polygons = FilledPolygons(board.Text, definition.Net, definition.Layer).ToArray();
        if (polygons.Length == 0)
            return Result(definition, ConstraintOutcome.Unavailable, null, definition.MinimumSquareMm, "mm2", [definition.Net!, definition.Layer!], hash, "No matching filled copper polygons were found; refill zones with KiCad first.");
        var area = polygons.Sum(PolygonArea);
        return Result(definition, area >= definition.MinimumSquareMm ? ConstraintOutcome.Passed : ConstraintOutcome.Failed,
            area, definition.MinimumSquareMm, "mm2", [definition.Net!, definition.Layer!], hash, $"Measured filled copper area: {area:0.###} mm².");
    }

    private static ConstraintResult ZoneIslands(ConstraintDefinition definition, KiCadBoardDocument board, string hash)
    {
        if (ResolveNet(board, definition.Net) is null)
            return Result(definition, ConstraintOutcome.Unavailable, null, definition.MaximumCount, "islands", [definition.Net!], hash, $"Net could not be resolved: {definition.Net}.");
        var polygons = FilledPolygons(board.Text, definition.Net, definition.Layer).ToArray();
        if (polygons.Length == 0)
            return Result(definition, ConstraintOutcome.Unavailable, null, definition.MaximumCount, "islands", [definition.Net!, definition.Layer!], hash, "No matching filled copper polygons were found; refill zones with KiCad first.");
        return Result(definition, polygons.Length <= definition.MaximumCount ? ConstraintOutcome.Passed : ConstraintOutcome.Failed,
            polygons.Length, definition.MaximumCount, "islands", [definition.Net!, definition.Layer!], hash, $"Found {polygons.Length} filled polygon island(s).");
    }

    private static ConstraintResult TraceLength(ConstraintDefinition definition, KiCadBoardDocument board, string hash)
    {
        if (!TrySegments(board, definition.Net, requireAny: true, out var segments, out var unavailable))
            return Result(definition, ConstraintOutcome.Unavailable, null, definition.MaximumMm, "mm", [definition.Net!], hash, unavailable!);
        var length = segments.Sum(SegmentLength);
        return Result(definition, length <= definition.MaximumMm ? ConstraintOutcome.Passed : ConstraintOutcome.Failed,
            length, definition.MaximumMm, "mm", segments.Select(static item => item.Uuid ?? item.Id).ToArray(), hash, $"Measured routed trace length: {length:0.###} mm.");
    }

    private static ConstraintResult MatchedTraceLength(ConstraintDefinition definition, KiCadBoardDocument board, string hash)
    {
        var lengths = new List<(string Net, double Length)>();
        foreach (var net in definition.Nets!)
        {
            if (!TrySegments(board, net, requireAny: true, out var segments, out var unavailable))
                return Result(definition, ConstraintOutcome.Unavailable, null, definition.MaximumDifferenceMm, "mm", definition.Nets!, hash, unavailable!);
            lengths.Add((net, segments.Sum(SegmentLength)));
        }
        var difference = lengths.Max(static item => item.Length) - lengths.Min(static item => item.Length);
        return Result(definition, difference <= definition.MaximumDifferenceMm ? ConstraintOutcome.Passed : ConstraintOutcome.Failed,
            difference, definition.MaximumDifferenceMm, "mm", definition.Nets!, hash, $"Measured maximum trace-length difference: {difference:0.###} mm.");
    }

    private static ConstraintResult ViaCount(ConstraintDefinition definition, KiCadBoardDocument board, string hash)
    {
        var net = definition.Net is null ? null : ResolveNet(board, definition.Net);
        if (definition.Net is not null && net is null)
            return Result(definition, ConstraintOutcome.Unavailable, null, definition.MaximumCount, "vias", [definition.Net], hash, $"Net could not be resolved: {definition.Net}.");
        var vias = net is null ? board.Vias : board.Vias.Where(item => NetMatches(item.NetCode, item.NetName, net)).ToArray();
        return Result(definition, vias.Count <= definition.MaximumCount ? ConstraintOutcome.Passed : ConstraintOutcome.Failed,
            vias.Count, definition.MaximumCount, "vias", vias.Select(static item => item.Uuid ?? item.Id).ToArray(), hash, $"Found {vias.Count} via(s).");
    }

    private static ConstraintResult FootprintRegion(ConstraintDefinition definition, KiCadBoardDocument board, string hash)
    {
        var footprint = board.Footprints.FirstOrDefault(item => string.Equals(item.Reference, definition.Feature, StringComparison.OrdinalIgnoreCase));
        if (footprint?.XMillimeters is null || footprint.YMillimeters is null)
            return Result(definition, ConstraintOutcome.Unavailable, null, null, "region", [definition.Feature!], hash, "Footprint could not be resolved.");
        var inside = footprint.XMillimeters >= definition.MinimumXmm && footprint.XMillimeters <= definition.MaximumXmm
            && footprint.YMillimeters >= definition.MinimumYmm && footprint.YMillimeters <= definition.MaximumYmm;
        return Result(definition, inside ? ConstraintOutcome.Passed : ConstraintOutcome.Failed, null, null, "region", [definition.Feature!], hash,
            $"Footprint anchor is ({footprint.XMillimeters:0.###}, {footprint.YMillimeters:0.###}) mm.");
    }

    private ToolResponse<LoadedConstraints> Load(string projectPath, string? constraintsPath)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<LoadedConstraints>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        var path = string.IsNullOrWhiteSpace(constraintsPath)
            ? Path.Combine(project.Data.ProjectRoot, DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar))
            : Path.GetFullPath(constraintsPath, project.Data.ProjectRoot);
        if (!ProjectScopePolicy.IsWithin(project.Data.ProjectRoot, path))
            return ToolResponse<LoadedConstraints>.Fail("Constraint path must remain inside the project.", "PROJECT_SCOPE_VIOLATION");
        if (!File.Exists(path)) return ToolResponse<LoadedConstraints>.Fail($"Constraint document was not found: {path}", "CONSTRAINTS_NOT_FOUND");
        try
        {
            var document = JsonSerializer.Deserialize<ConstraintDocument>(File.ReadAllText(path), JsonOptions);
            return document is null || document.Version != 1
                ? ToolResponse<LoadedConstraints>.Fail("Only constraint document version 1 is supported.", "CONSTRAINTS_INVALID")
                : ToolResponse<LoadedConstraints>.Ok("Loaded constraint document.", new(path, document));
        }
        catch (JsonException exception)
        {
            return ToolResponse<LoadedConstraints>.Fail("Constraint document is invalid JSON.", "CONSTRAINTS_INVALID", exception.Message);
        }
    }

    private static IEnumerable<string> ValidateDocument(ConstraintDocument document)
    {
        foreach (var duplicate in document.Constraints.Where(item => !string.IsNullOrWhiteSpace(item.Id)).GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            yield return $"Constraint id must be unique: {duplicate.Key}.";
        foreach (var definition in document.Constraints)
        {
            if (string.IsNullOrWhiteSpace(definition.Id)) yield return "Every constraint requires an id.";
            switch (definition.Type)
            {
                case "allowed-layers" when definition.AllowedLayers is null || definition.AllowedLayers.Count == 0: yield return $"{definition.Id}: allowed-layers requires allowedLayers."; break;
                case "max-feature-distance" when string.IsNullOrWhiteSpace(definition.From) || string.IsNullOrWhiteSpace(definition.To) || definition.MaximumMm is null or < 0: yield return $"{definition.Id}: max-feature-distance requires from, to, and non-negative maximumMm."; break;
                case "min-copper-area" when string.IsNullOrWhiteSpace(definition.Net) || string.IsNullOrWhiteSpace(definition.Layer) || definition.MinimumSquareMm is null or < 0: yield return $"{definition.Id}: min-copper-area requires net, layer, and non-negative minimumSquareMm."; break;
                case "max-zone-islands" when string.IsNullOrWhiteSpace(definition.Net) || string.IsNullOrWhiteSpace(definition.Layer) || definition.MaximumCount is null or < 0: yield return $"{definition.Id}: max-zone-islands requires net, layer, and non-negative maximumCount."; break;
                case "max-vias" when definition.MaximumCount is null or < 0: yield return $"{definition.Id}: max-vias requires non-negative maximumCount."; break;
                case "max-trace-length" when string.IsNullOrWhiteSpace(definition.Net) || definition.MaximumMm is null or < 0: yield return $"{definition.Id}: max-trace-length requires net and non-negative maximumMm."; break;
                case "matched-trace-length" when definition.Nets is null || definition.Nets.Count < 2 || definition.Nets.Any(string.IsNullOrWhiteSpace) || definition.MaximumDifferenceMm is null or < 0: yield return $"{definition.Id}: matched-trace-length requires at least two nets and non-negative maximumDifferenceMm."; break;
                case "footprint-region" when string.IsNullOrWhiteSpace(definition.Feature) || definition.MinimumXmm is null || definition.MaximumXmm is null || definition.MinimumYmm is null || definition.MaximumYmm is null: yield return $"{definition.Id}: footprint-region requires feature and all four region coordinates."; break;
                case "allowed-layers" or "max-feature-distance" or "min-copper-area" or "max-zone-islands" or "max-trace-length" or "matched-trace-length" or "max-vias" or "footprint-region": break;
                default: yield return $"{definition.Id}: unsupported constraint type '{definition.Type}'."; break;
            }
        }
    }

    private static bool TrySegments(KiCadBoardDocument board, string? netName, bool requireAny, out IReadOnlyList<KiCadSegment> segments, out string? unavailable)
    {
        if (netName is null)
        {
            segments = board.Segments;
            unavailable = null;
            return true;
        }
        var net = ResolveNet(board, netName);
        if (net is null)
        {
            segments = [];
            unavailable = $"Net could not be resolved: {netName}.";
            return false;
        }
        segments = board.Segments.Where(item => NetMatches(item.NetCode, item.NetName, net)).ToArray();
        if (requireAny && segments.Count == 0)
        {
            unavailable = $"No routed track segments were found for net: {netName}.";
            return false;
        }
        unavailable = null;
        return true;
    }

    private static KiCadNet? ResolveNet(KiCadBoardDocument board, string? value) => string.IsNullOrWhiteSpace(value) ? null
        : board.Nets.FirstOrDefault(item => string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase) || item.Code.ToString(CultureInfo.InvariantCulture) == value);
    private static bool NetMatches(int? code, string? name, KiCadNet net) => code == net.Code || string.Equals(name, net.Name, StringComparison.OrdinalIgnoreCase);
    private static double SegmentLength(KiCadSegment item) => item.StartXMillimeters is null || item.StartYMillimeters is null || item.EndXMillimeters is null || item.EndYMillimeters is null
        ? 0 : Math.Sqrt(Math.Pow(item.EndXMillimeters.Value - item.StartXMillimeters.Value, 2) + Math.Pow(item.EndYMillimeters.Value - item.StartYMillimeters.Value, 2));

    private static (double X, double Y)? ResolveFeature(KiCadBoardDocument board, string? feature)
    {
        if (string.IsNullOrWhiteSpace(feature)) return null;
        var separator = feature.IndexOf('.');
        var reference = separator < 0 ? feature : feature[..separator];
        var padName = separator < 0 ? null : feature[(separator + 1)..];
        var footprint = board.Footprints.FirstOrDefault(item => string.Equals(item.Reference, reference, StringComparison.OrdinalIgnoreCase));
        if (footprint?.XMillimeters is null || footprint.YMillimeters is null) return null;
        if (padName is null) return (footprint.XMillimeters.Value, footprint.YMillimeters.Value);
        var pad = footprint.Pads.FirstOrDefault(item => string.Equals(item.Name, padName, StringComparison.OrdinalIgnoreCase));
        if (pad?.XMillimeters is null || pad.YMillimeters is null) return null;
        var rotation = (footprint.RotationDegrees ?? 0) * Math.PI / 180;
        return (
            footprint.XMillimeters.Value + pad.XMillimeters.Value * Math.Cos(rotation) + pad.YMillimeters.Value * Math.Sin(rotation),
            footprint.YMillimeters.Value - pad.XMillimeters.Value * Math.Sin(rotation) + pad.YMillimeters.Value * Math.Cos(rotation));
    }

    private static IEnumerable<IReadOnlyList<(double X, double Y)>> FilledPolygons(string text, string? net, string? layer)
    {
        foreach (var zone in SExpressions(text, "zone"))
        {
            if (!Regex.IsMatch(zone, $"\\(net(?:_name)?\\s+\"{Regex.Escape(net!)}\"\\)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) continue;
            if (!Regex.IsMatch(zone, $"\\(layer\\s+\"{Regex.Escape(layer!)}\"\\)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) continue;
            foreach (var polygon in SExpressions(zone, "filled_polygon"))
            {
                var points = Regex.Matches(polygon, @"\(xy\s+(-?[\d.]+)\s+(-?[\d.]+)\)", RegexOptions.None, TimeSpan.FromSeconds(1))
                    .Select(match => (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))).ToArray();
                if (points.Length >= 3) yield return points;
            }
        }
    }

    private static IEnumerable<string> SExpressions(string text, string kind)
    {
        var pattern = $"({kind}";
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            var tokenEnd = index + pattern.Length;
            if (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]) && text[tokenEnd] != ')') { index = tokenEnd; continue; }
            var end = FindEnd(text, index);
            if (end < 0) yield break;
            yield return text[index..(end + 1)];
            index = end + 1;
        }
    }

    private static int FindEnd(string text, int start)
    {
        var depth = 0; var quoted = false;
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] == '"' && (index == 0 || text[index - 1] != '\\')) quoted = !quoted;
            if (quoted) continue;
            if (text[index] == '(') depth++;
            else if (text[index] == ')' && --depth == 0) return index;
        }
        return -1;
    }

    private static double PolygonArea(IReadOnlyList<(double X, double Y)> points)
    {
        var twiceArea = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            twiceArea += points[index].X * next.Y - next.X * points[index].Y;
        }
        return Math.Abs(twiceArea) / 2;
    }

    private static string HashInputs(params (string Name, string Content)[] inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var input in inputs.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(input.Name));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(input.Content));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static ConstraintResult Result(ConstraintDefinition definition, ConstraintOutcome outcome, double? measured, double? limit,
        string unit, IReadOnlyList<string> features, string hash, string summary) =>
        new(definition.Id, definition.Type, outcome, measured, limit, unit, features, hash, summary);

    private sealed record LoadedConstraints(string Path, ConstraintDocument Document);
}

public sealed record ConstraintDocument(int Version, IReadOnlyList<ConstraintDefinition> Constraints)
{
    public IReadOnlyList<ConstraintDefinition> Constraints { get; init; } = Constraints ?? [];
}

public sealed record ConstraintDefinition(string Id, string Type, string? Net = null, IReadOnlyList<string>? Nets = null,
    IReadOnlyList<string>? AllowedLayers = null, string? Layer = null, string? From = null, string? To = null,
    string? Feature = null, double? MaximumMm = null, double? MaximumDifferenceMm = null, double? MinimumSquareMm = null,
    int? MaximumCount = null, double? MinimumXmm = null, double? MaximumXmm = null, double? MinimumYmm = null, double? MaximumYmm = null);

public enum ConstraintOutcome { Passed, Failed, Unavailable }
public sealed record ConstraintResult(string Id, string Type, ConstraintOutcome Outcome, double? Measured, double? Limit,
    string Unit, IReadOnlyList<string> Features, string InputHash, string Summary);
public sealed record ConstraintValidationResult(string ConstraintsPath, int ConstraintCount, bool Valid, IReadOnlyList<string> Errors);
public sealed record ConstraintProofReport(int SchemaVersion, string ConstraintsPath, string BoardPath, string InputHash,
    DateTimeOffset CreatedAtUtc, bool Passed, IReadOnlyList<ConstraintResult> Results, string ReportPath);
