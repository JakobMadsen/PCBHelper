namespace PCBHelper.Core;

public sealed class ComponentService
{
    private readonly ProjectDiscoveryService _projectDiscovery;

    public ComponentService(ProjectDiscoveryService projectDiscovery)
    {
        _projectDiscovery = projectDiscovery;
    }

    public ToolResponse<ComponentListResult> ListComponents(string projectPath)
    {
        var load = Load(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<ComponentListResult>.Fail(load.Summary, load.Error?.Code ?? "PROJECT_LOAD_FAILED", load.Error?.Message);
        }

        var components = BuildComponents(load.Data).ToArray();
        return ToolResponse<ComponentListResult>.Ok(
            $"Found {components.Length} component(s).",
            new ComponentListResult(load.Data.Project.ProjectRoot, components));
    }

    public ToolResponse<ComponentValueResult> GetValue(string projectPath, string reference)
    {
        var locations = FindValueLocations(projectPath, reference);
        if (!locations.Success || locations.Data is null)
        {
            return ToolResponse<ComponentValueResult>.Fail(locations.Summary, locations.Error?.Code ?? "VALUE_NOT_FOUND", locations.Error?.Message);
        }

        return ToolResponse<ComponentValueResult>.Ok(
            $"{reference} value: {locations.Data.Locations.First().Value}.",
            new ComponentValueResult(reference, locations.Data.Locations));
    }

    public ToolResponse<ComponentValueMutationResult> SetValue(
        string projectPath,
        string reference,
        string value,
        string? scope,
        bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return ToolResponse<ComponentValueMutationResult>.Fail("Reference is required.", "REFERENCE_REQUIRED");
        }

        if (value is null)
        {
            return ToolResponse<ComponentValueMutationResult>.Fail("Value is required.", "VALUE_REQUIRED");
        }

        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "available" : scope.ToLowerInvariant();
        if (normalizedScope is not "available" and not "schematic" and not "board" and not "both")
        {
            return ToolResponse<ComponentValueMutationResult>.Fail("Scope must be available, schematic, board, or both.", "INVALID_SCOPE");
        }

        var load = Load(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<ComponentValueMutationResult>.Fail(load.Summary, load.Error?.Code ?? "PROJECT_LOAD_FAILED", load.Error?.Message);
        }

        var locations = FindValueLocations(load.Data, reference);
        var selected = normalizedScope switch
        {
            "available" => locations,
            "board" => locations.Where(static location => location.Source == "board").ToArray(),
            "schematic" => locations.Where(static location => location.Source == "schematic").ToArray(),
            "both" => locations,
            _ => locations
        };

        if (selected.Length == 0)
        {
            return ToolResponse<ComponentValueMutationResult>.Fail($"Value not found for {reference} in scope {normalizedScope}.", "VALUE_NOT_FOUND");
        }

        if (normalizedScope == "both"
            && (!selected.Any(static location => location.Source == "board")
                || !selected.Any(static location => location.Source == "schematic")))
        {
            return ToolResponse<ComponentValueMutationResult>.Fail($"Both schematic and board values are required for {reference}.", "VALUE_SCOPE_INCOMPLETE");
        }

        var changedFiles = selected.Select(static location => location.File).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!dryRun)
        {
            foreach (var group in selected.GroupBy(static location => location.File, StringComparer.OrdinalIgnoreCase))
            {
                var text = File.ReadAllText(group.Key);
                foreach (var location in group.OrderByDescending(static item => item.ValueStart))
                {
                    text = text.Remove(location.ValueStart, location.ValueLength).Insert(location.ValueStart, value);
                }

                File.WriteAllText(group.Key, text);
            }
        }

        var result = new ComponentValueMutationResult(
            reference,
            normalizedScope,
            dryRun,
            selected,
            selected.Select(location => location with { Value = value }).ToArray(),
            changedFiles,
            null,
            null,
            Array.Empty<string>());

        return ToolResponse<ComponentValueMutationResult>.Ok(
            $"{(dryRun ? "Previewed" : "Set")} {reference} value to {value}.",
            result);
    }

    internal ToolResponse<ComponentValueLocations> FindValueLocations(string projectPath, string reference)
    {
        var load = Load(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<ComponentValueLocations>.Fail(load.Summary, load.Error?.Code ?? "PROJECT_LOAD_FAILED", load.Error?.Message);
        }

        var locations = FindValueLocations(load.Data, reference);
        return locations.Length == 0
            ? ToolResponse<ComponentValueLocations>.Fail($"Value not found for {reference}.", "VALUE_NOT_FOUND")
            : ToolResponse<ComponentValueLocations>.Ok($"Found {locations.Length} value location(s) for {reference}.", new ComponentValueLocations(reference, locations));
    }

    private ToolResponse<ComponentLoadContext> Load(string projectPath)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ComponentLoadContext>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var board = project.Data.BoardFile is not null && File.Exists(project.Data.BoardFile)
            ? KiCadBoardParser.Parse(project.Data.BoardFile)
            : null;
        var schematic = project.Data.SchematicFile is not null && File.Exists(project.Data.SchematicFile)
            ? KiCadSchematicParser.Parse(project.Data.SchematicFile)
            : null;

        return ToolResponse<ComponentLoadContext>.Ok("Loaded component context.", new ComponentLoadContext(project.Data, board, schematic));
    }

    private static IReadOnlyList<ComponentSummary> BuildComponents(ComponentLoadContext context)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in context.Board?.Footprints.Select(static item => item.Reference).Where(static item => item is not null).Cast<string>() ?? Array.Empty<string>())
        {
            references.Add(reference);
        }

        foreach (var reference in context.Schematic?.Symbols.Select(static item => item.Reference).Where(static item => item is not null).Cast<string>() ?? Array.Empty<string>())
        {
            references.Add(reference);
        }

        return references.Order(StringComparer.OrdinalIgnoreCase)
            .Select(reference =>
            {
                var footprint = context.Board?.Footprints.FirstOrDefault(item => string.Equals(item.Reference, reference, StringComparison.OrdinalIgnoreCase));
                var symbol = context.Schematic?.Symbols.FirstOrDefault(item => string.Equals(item.Reference, reference, StringComparison.OrdinalIgnoreCase));
                var boardValue = footprint?.Properties.TryGetValue("Value", out var boardProperty) == true ? boardProperty.Value : null;
                var schematicValue = symbol?.Properties.TryGetValue("Value", out var schematicProperty) == true ? schematicProperty.Value : null;

                return new ComponentSummary(
                    reference,
                    schematicValue ?? boardValue,
                    boardValue,
                    schematicValue,
                    footprint?.FootprintName,
                    footprint?.Side,
                    footprint?.XMillimeters,
                    footprint?.YMillimeters,
                    footprint?.Pads.Count ?? 0);
            })
            .ToArray();
    }

    private static ComponentValueLocation[] FindValueLocations(ComponentLoadContext context, string reference)
    {
        var locations = new List<ComponentValueLocation>();
        var footprint = context.Board?.Footprints.FirstOrDefault(item => string.Equals(item.Reference, reference, StringComparison.OrdinalIgnoreCase));
        if (footprint?.Properties.TryGetValue("Value", out var boardValue) == true && context.Board is not null)
        {
            locations.Add(new ComponentValueLocation("board", context.Board.BoardFile, reference, boardValue.Value, boardValue.ValueStart, boardValue.ValueLength));
        }

        var symbol = context.Schematic?.Symbols.FirstOrDefault(item => string.Equals(item.Reference, reference, StringComparison.OrdinalIgnoreCase));
        if (symbol?.Properties.TryGetValue("Value", out var schematicValue) == true && context.Schematic is not null)
        {
            locations.Add(new ComponentValueLocation("schematic", context.Schematic.SchematicFile, reference, schematicValue.Value, schematicValue.ValueStart, schematicValue.ValueLength));
        }

        return locations.ToArray();
    }
}

internal sealed record ComponentLoadContext(ProjectSummary Project, KiCadBoardDocument? Board, KiCadSchematicDocument? Schematic);

internal sealed record ComponentValueLocations(string Reference, IReadOnlyList<ComponentValueLocation> Locations);

public sealed record ComponentListResult(string ProjectRoot, IReadOnlyList<ComponentSummary> Components);

public sealed record ComponentSummary(
    string Reference,
    string? Value,
    string? BoardValue,
    string? SchematicValue,
    string? FootprintName,
    string? Side,
    double? XMillimeters,
    double? YMillimeters,
    int PadCount);

public sealed record ComponentValueResult(string Reference, IReadOnlyList<ComponentValueLocation> Locations);

public sealed record ComponentValueLocation(
    string Source,
    string File,
    string Reference,
    string Value,
    int ValueStart,
    int ValueLength);

public sealed record ComponentValueMutationResult(
    string Reference,
    string Scope,
    bool DryRun,
    IReadOnlyList<ComponentValueLocation> Before,
    IReadOnlyList<ComponentValueLocation> After,
    IReadOnlyList<string> ChangedFiles,
    string? ChangeReportPath,
    string? CheckSummary,
    IReadOnlyList<string> CheckReportPaths);
