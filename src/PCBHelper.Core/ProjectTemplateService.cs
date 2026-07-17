using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed partial class ProjectTemplateService
{
    public const string CircularTemplateId = "blank-two-layer-circle";
    public const string RectangularTemplateId = "blank-two-layer-rectangle";

    private const double MinimumDimensionMillimeters = 10;
    private const double MaximumDimensionMillimeters = 500;
    private readonly ProjectScopePolicy _scope;
    private readonly ProjectDiscoveryService _projects;

    public ProjectTemplateService(ProjectScopePolicy scope, ProjectDiscoveryService projects)
    {
        _scope = scope;
        _projects = projects;
    }

    public ToolResponse<ProjectCreationResult> CreateProjectFromTemplate(
        string templateId,
        string projectName,
        string destinationDirectory,
        double? boardWidthMm = null,
        double? boardHeightMm = null,
        double? boardDiameterMm = null)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return Fail("Template id is required.", "PROJECT_TEMPLATE_REQUIRED");
        if (string.IsNullOrWhiteSpace(projectName) || !ProjectNameRegex().IsMatch(projectName))
            return Fail("Project name must start with a letter or digit and contain only letters, digits, dot, underscore, or hyphen (maximum 64 characters).", "PROJECT_NAME_INVALID");
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            return Fail("Destination directory is required.", "PROJECT_DESTINATION_REQUIRED");

        var destination = _scope.Authorize(destinationDirectory);
        if (!destination.Success || destination.Data is null)
            return Fail(destination.Summary, destination.Error?.Code ?? "PROJECT_SCOPE_VIOLATION", destination.Error?.Message);
        if (!Directory.Exists(destination.Data))
            return Fail($"Destination directory does not exist: {destination.Data}", "PROJECT_DESTINATION_NOT_FOUND");

        var projectRoot = Path.Combine(destination.Data, projectName);
        var target = _scope.Authorize(projectRoot);
        if (!target.Success || target.Data is null)
            return Fail(target.Summary, target.Error?.Code ?? "PROJECT_SCOPE_VIOLATION", target.Error?.Message);
        if (Directory.Exists(target.Data) || File.Exists(target.Data))
            return Fail($"Project destination already exists: {target.Data}", "PROJECT_DESTINATION_EXISTS");

        var dimensions = ResolveDimensions(templateId, boardWidthMm, boardHeightMm, boardDiameterMm);
        if (!dimensions.Success || dimensions.Data is null)
            return Fail(dimensions.Summary, dimensions.Error?.Code ?? "PROJECT_TEMPLATE_INVALID", dimensions.Error?.Message);

        var staging = Path.Combine(destination.Data, $".{projectName}.pcbhelper-create-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            var projectFile = Path.Combine(staging, $"{projectName}.kicad_pro");
            var schematicFile = Path.Combine(staging, $"{projectName}.kicad_sch");
            var boardFile = Path.Combine(staging, $"{projectName}.kicad_pcb");
            File.WriteAllText(projectFile, FormatProject(projectName), Utf8NoBom);
            File.WriteAllText(schematicFile, FormatSchematic(projectName), Utf8NoBom);
            File.WriteAllText(boardFile, FormatBoard(projectName, dimensions.Data), Utf8NoBom);
            Directory.Move(staging, target.Data);

            var summary = _projects.GetSummary(target.Data);
            if (!summary.Success || summary.Data is null || summary.Data.MissingFiles.Count != 0)
                return Fail("Created project files, but the project could not be rediscovered as a complete KiCad project.", "PROJECT_CREATION_VALIDATION_FAILED", summary.Error?.Message);

            var files = new[]
            {
                Path.Combine(target.Data, $"{projectName}.kicad_pro"),
                Path.Combine(target.Data, $"{projectName}.kicad_sch"),
                Path.Combine(target.Data, $"{projectName}.kicad_pcb")
            };
            return ToolResponse<ProjectCreationResult>.Ok(
                $"Created KiCad project '{projectName}' from template '{templateId}'.",
                new ProjectCreationResult(templateId, target.Data, files, dimensions.Data.Shape, dimensions.Data.WidthMm, dimensions.Data.HeightMm));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not create the KiCad project atomically.", "PROJECT_CREATION_FAILED", exception.Message);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); } catch (IOException) { }
            }
        }
    }

    private static ToolResponse<ProjectTemplateDimensions> ResolveDimensions(
        string templateId, double? width, double? height, double? diameter)
    {
        if (string.Equals(templateId, CircularTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            if (width is not null || height is not null)
                return ToolResponse<ProjectTemplateDimensions>.Fail("The circular template accepts boardDiameterMm, not width or height.", "PROJECT_TEMPLATE_DIMENSIONS_INVALID");
            var value = diameter ?? 100;
            return ValidDimension(value)
                ? ToolResponse<ProjectTemplateDimensions>.Ok("Resolved circular board dimensions.", new ProjectTemplateDimensions("circle", value, value))
                : InvalidDimensions();
        }

        if (string.Equals(templateId, RectangularTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            if (diameter is not null)
                return ToolResponse<ProjectTemplateDimensions>.Fail("The rectangular template accepts boardWidthMm and boardHeightMm, not diameter.", "PROJECT_TEMPLATE_DIMENSIONS_INVALID");
            var resolvedWidth = width ?? 100;
            var resolvedHeight = height ?? 80;
            return ValidDimension(resolvedWidth) && ValidDimension(resolvedHeight)
                ? ToolResponse<ProjectTemplateDimensions>.Ok("Resolved rectangular board dimensions.", new ProjectTemplateDimensions("rectangle", resolvedWidth, resolvedHeight))
                : InvalidDimensions();
        }

        return ToolResponse<ProjectTemplateDimensions>.Fail(
            $"Unsupported project template: {templateId}. Supported templates are {CircularTemplateId} and {RectangularTemplateId}.",
            "PROJECT_TEMPLATE_UNSUPPORTED");
    }

    private static bool ValidDimension(double value) =>
        double.IsFinite(value) && value >= MinimumDimensionMillimeters && value <= MaximumDimensionMillimeters;

    private static ToolResponse<ProjectTemplateDimensions> InvalidDimensions() =>
        ToolResponse<ProjectTemplateDimensions>.Fail(
            $"Board dimensions must be finite and between {MinimumDimensionMillimeters} mm and {MaximumDimensionMillimeters} mm.",
            "PROJECT_TEMPLATE_DIMENSIONS_INVALID");

    private static string FormatProject(string projectName) => $$"""
        {
          "meta": {
            "filename": "{{projectName}}.kicad_pro",
            "version": 1
          }
        }
        """;

    private static string FormatSchematic(string projectName) => $$"""
        (kicad_sch
          (version 20241229)
          (generator "PCBHelper")
          (generator_version "0.1")
          (uuid "{{Guid.NewGuid()}}")
          (paper "A4")
          (title_block
            (title "{{projectName}}")
            (comment 1 "Created by PCBHelper from an approved blank two-layer template.")
          )
          (lib_symbols)
          (symbol_instances)
        )
        """;

    private static string FormatBoard(string projectName, ProjectTemplateDimensions dimensions)
    {
        const double centerX = 100;
        const double centerY = 100;
        var outline = dimensions.Shape == "circle"
            ? FormatCircle(centerX, centerY, dimensions.WidthMm)
            : FormatRectangle(centerX, centerY, dimensions.WidthMm, dimensions.HeightMm);
        return $$"""
            (kicad_pcb
              (version 20260206)
              (generator "PCBHelper")
              (generator_version "0.1")
              (general
                (thickness 1.6)
                (legacy_teardrops no)
              )
              (paper "A4")
              (title_block
                (title "{{projectName}}")
                (comment 1 "Created by PCBHelper from an approved blank two-layer template.")
              )
              (layers
                (0 "F.Cu" signal)
                (2 "B.Cu" signal)
                (9 "F.Adhes" user "F.Adhesive")
                (11 "B.Adhes" user "B.Adhesive")
                (13 "F.Paste" user)
                (15 "B.Paste" user)
                (5 "F.SilkS" user "F.Silkscreen")
                (7 "B.SilkS" user "B.Silkscreen")
                (1 "F.Mask" user)
                (3 "B.Mask" user)
                (17 "Dwgs.User" user "User.Drawings")
                (19 "Cmts.User" user "User.Comments")
                (21 "Eco1.User" user "User.Eco1")
                (23 "Eco2.User" user "User.Eco2")
                (25 "Edge.Cuts" user)
                (27 "Margin" user)
                (31 "F.CrtYd" user "F.Courtyard")
                (29 "B.CrtYd" user "B.Courtyard")
                (35 "F.Fab" user)
                (33 "B.Fab" user)
              )
              (setup
                (pad_to_mask_clearance 0)
                (allow_soldermask_bridges_in_footprints no)
              )
              (net 0 "")
            {{outline}}
            )
            """;
    }

    private static string FormatCircle(double centerX, double centerY, double diameter)
    {
        var radius = diameter / 2;
        return $$"""
          (gr_circle
            (center {{Number(centerX)}} {{Number(centerY)}})
            (end {{Number(centerX + radius)}} {{Number(centerY)}})
            (stroke (width 0.1) (type default))
            (fill none)
            (layer "Edge.Cuts")
            (uuid "{{Guid.NewGuid()}}")
          )
        """;
    }

    private static string FormatRectangle(double centerX, double centerY, double width, double height)
    {
        var left = centerX - width / 2;
        var right = centerX + width / 2;
        var top = centerY - height / 2;
        var bottom = centerY + height / 2;
        return $$"""
          (gr_rect
            (start {{Number(left)}} {{Number(top)}})
            (end {{Number(right)}} {{Number(bottom)}})
            (stroke (width 0.1) (type default))
            (fill none)
            (layer "Edge.Cuts")
            (uuid "{{Guid.NewGuid()}}")
          )
        """;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static ToolResponse<ProjectCreationResult> Fail(string summary, string code, string? message = null) =>
        ToolResponse<ProjectCreationResult>.Fail(summary, code, message);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNameRegex();
}

public sealed record ProjectCreationResult(
    string TemplateId,
    string ProjectPath,
    IReadOnlyList<string> CreatedFiles,
    string BoardShape,
    double BoardWidthMm,
    double BoardHeightMm);

internal sealed record ProjectTemplateDimensions(string Shape, double WidthMm, double HeightMm);
