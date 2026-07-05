namespace PCBHelper.Core;

public sealed class GeometryService
{
    private readonly ProjectDiscoveryService _projectDiscovery;

    public GeometryService(ProjectDiscoveryService projectDiscovery)
    {
        _projectDiscovery = projectDiscovery;
    }

    public ToolResponse<MeasurementResult> MeasureDistance(string projectPath, string fromReference, string toReference)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<MeasurementResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var from = FindFootprint(board.Data, fromReference);
        if (from is null)
        {
            return ToolResponse<MeasurementResult>.Fail($"Footprint not found: {fromReference}", "FOOTPRINT_NOT_FOUND");
        }

        var to = FindFootprint(board.Data, toReference);
        if (to is null)
        {
            return ToolResponse<MeasurementResult>.Fail($"Footprint not found: {toReference}", "FOOTPRINT_NOT_FOUND");
        }

        var fromPlacement = ToPlacement(from);
        var toPlacement = ToPlacement(to);
        if (fromPlacement is null || toPlacement is null)
        {
            return ToolResponse<MeasurementResult>.Fail("Both footprints must have top-level positions.", "FOOTPRINT_POSITION_MISSING");
        }

        var dx = toPlacement.XMillimeters - fromPlacement.XMillimeters;
        var dy = toPlacement.YMillimeters - fromPlacement.YMillimeters;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var result = new MeasurementResult(from.Reference!, to.Reference!, fromPlacement, toPlacement, dx, dy, distance);

        return ToolResponse<MeasurementResult>.Ok(
            $"{from.Reference} to {to.Reference}: {distance:0.###} mm.",
            result);
    }

    public ToolResponse<ComponentMoveResult> MoveComponent(
        string projectPath,
        string reference,
        double xMillimeters,
        double yMillimeters,
        bool dryRun)
    {
        var board = LoadBoard(projectPath);
        if (!board.Success || board.Data is null)
        {
            return ToolResponse<ComponentMoveResult>.Fail(board.Summary, board.Error?.Code ?? "BOARD_LOAD_FAILED", board.Error?.Message);
        }

        var footprint = FindFootprint(board.Data, reference);
        if (footprint is null)
        {
            return ToolResponse<ComponentMoveResult>.Fail($"Footprint not found: {reference}", "FOOTPRINT_NOT_FOUND");
        }

        if (footprint.AtStart is null || footprint.AtLength is null)
        {
            return ToolResponse<ComponentMoveResult>.Fail($"Footprint has no top-level position: {reference}", "FOOTPRINT_POSITION_MISSING");
        }

        var before = ToPlacement(footprint)!;
        var after = before with { XMillimeters = xMillimeters, YMillimeters = yMillimeters };
        if (!dryRun)
        {
            var replacement = KiCadBoardParser.FormatTopLevelAt(after);
            var updated = board.Data.Text.Remove(footprint.AtStart.Value, footprint.AtLength.Value)
                .Insert(footprint.AtStart.Value, replacement);
            File.WriteAllText(board.Data.BoardFile, updated);
        }

        var result = new ComponentMoveResult(reference, board.Data.BoardFile, dryRun, before, after);
        var mode = dryRun ? "Previewed" : "Moved";
        return ToolResponse<ComponentMoveResult>.Ok(
            $"{mode} {reference} from ({before.XMillimeters:0.###}, {before.YMillimeters:0.###}) to ({after.XMillimeters:0.###}, {after.YMillimeters:0.###}).",
            result);
    }

    public ToolResponse<ComponentSpacingResult> SetComponentSpacing(
        string projectPath,
        string fixedReference,
        string movingReference,
        double distanceMillimeters,
        string? axis,
        bool dryRun)
    {
        var normalizedAxis = string.IsNullOrWhiteSpace(axis) ? "x" : axis.ToLowerInvariant();
        if (normalizedAxis is not "x" and not "y")
        {
            return ToolResponse<ComponentSpacingResult>.Fail("Axis must be 'x' or 'y'.", "INVALID_AXIS");
        }

        if (distanceMillimeters < 0)
        {
            return ToolResponse<ComponentSpacingResult>.Fail("Distance must be zero or greater.", "INVALID_DISTANCE");
        }

        var measurement = MeasureDistance(projectPath, fixedReference, movingReference);
        if (!measurement.Success || measurement.Data is null)
        {
            return ToolResponse<ComponentSpacingResult>.Fail(measurement.Summary, measurement.Error?.Code ?? "MEASURE_FAILED", measurement.Error?.Message);
        }

        var fixedPlacement = measurement.Data.FromPlacement;
        var movingPlacement = measurement.Data.ToPlacement;
        var direction = normalizedAxis == "x"
            ? Math.Sign(movingPlacement.XMillimeters - fixedPlacement.XMillimeters)
            : Math.Sign(movingPlacement.YMillimeters - fixedPlacement.YMillimeters);
        if (direction == 0)
        {
            direction = 1;
        }

        var targetX = normalizedAxis == "x"
            ? fixedPlacement.XMillimeters + (direction * distanceMillimeters)
            : movingPlacement.XMillimeters;
        var targetY = normalizedAxis == "y"
            ? fixedPlacement.YMillimeters + (direction * distanceMillimeters)
            : movingPlacement.YMillimeters;

        var move = MoveComponent(projectPath, movingReference, targetX, targetY, dryRun);
        if (!move.Success || move.Data is null)
        {
            return ToolResponse<ComponentSpacingResult>.Fail(move.Summary, move.Error?.Code ?? "MOVE_FAILED", move.Error?.Message);
        }

        var afterDx = move.Data.After.XMillimeters - fixedPlacement.XMillimeters;
        var afterDy = move.Data.After.YMillimeters - fixedPlacement.YMillimeters;
        var afterDistance = Math.Sqrt((afterDx * afterDx) + (afterDy * afterDy));
        var result = new ComponentSpacingResult(
            fixedReference,
            movingReference,
            normalizedAxis,
            dryRun,
            measurement.Data.DistanceMillimeters,
            afterDistance,
            move.Data.Before,
            move.Data.After,
            move.Data.ChangedFile);

        return ToolResponse<ComponentSpacingResult>.Ok(
            $"{(dryRun ? "Previewed" : "Set")} {movingReference} spacing from {fixedReference} to {afterDistance:0.###} mm on {normalizedAxis}-axis.",
            result);
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

    private static KiCadFootprint? FindFootprint(KiCadBoardDocument board, string reference)
    {
        return board.Footprints.FirstOrDefault(footprint =>
            string.Equals(footprint.Reference, reference, StringComparison.OrdinalIgnoreCase));
    }

    private static Placement? ToPlacement(KiCadFootprint footprint)
    {
        if (footprint.XMillimeters is null || footprint.YMillimeters is null)
        {
            return null;
        }

        return new Placement(footprint.XMillimeters.Value, footprint.YMillimeters.Value, footprint.RotationDegrees);
    }
}

public sealed record Placement(double XMillimeters, double YMillimeters, double? RotationDegrees);

public sealed record MeasurementResult(
    string FromReference,
    string ToReference,
    Placement FromPlacement,
    Placement ToPlacement,
    double DxMillimeters,
    double DyMillimeters,
    double DistanceMillimeters);

public sealed record ComponentMoveResult(
    string Reference,
    string ChangedFile,
    bool DryRun,
    Placement Before,
    Placement After);

public sealed record ComponentSpacingResult(
    string FixedReference,
    string MovingReference,
    string Axis,
    bool DryRun,
    double BeforeDistanceMillimeters,
    double AfterDistanceMillimeters,
    Placement Before,
    Placement After,
    string ChangedFile);
