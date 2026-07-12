using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PCBHelper.Core;

public sealed class AssemblyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] ManufacturerFields =
    {
        "Manufacturer",
        "Mfr",
        "MFG"
    };

    private static readonly string[] MpnFields =
    {
        "MPN",
        "Manufacturer Part Number",
        "ManufacturerPartNumber",
        "Part Number",
        "PartNumber"
    };

    private static readonly string[] SupplierPartFields =
    {
        "SupplierPart",
        "Supplier Part",
        "Supplier Part Number",
        "LCSC",
        "LCSC Part",
        "PCBWay",
        "PCBWay Part",
        "PCBWay Part Number"
    };

    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly KiCadDoctorService _doctor;
    private readonly ExportService _exportService;

    public AssemblyService(ProjectDiscoveryService projectDiscovery, KiCadDoctorService doctor, ExportService exportService)
    {
        _projectDiscovery = projectDiscovery;
        _doctor = doctor;
        _exportService = exportService;
    }

    public ToolResponse<AssemblyInspectionResult> InspectAssembly(string projectPath)
    {
        var load = LoadAssembly(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<AssemblyInspectionResult>.Fail(load.Summary, load.Error?.Code ?? "ASSEMBLY_LOAD_FAILED", load.Error?.Message);
        }

        var result = new AssemblyInspectionResult(
            load.Data.Project.ProjectRoot,
            load.Data.Project.BoardFile!,
            load.Data.Project.SchematicFile,
            load.Data.Components,
            load.Data.BomRows,
            load.Data.CplRows);

        return ToolResponse<AssemblyInspectionResult>.Ok(
            $"Found {result.Components.Count} assembly component(s), {result.BomRows.Count} BOM row(s), and {result.CplRows.Count} CPL row(s).",
            result);
    }

    public async Task<ToolResponse<AssemblyExportResult>> ExportAssemblyBomAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var load = LoadAssembly(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<AssemblyExportResult>.Fail(load.Summary, load.Error?.Code ?? "ASSEMBLY_LOAD_FAILED", load.Error?.Message);
        }

        var root = ExportPathFactory.CreateExportDirectory(load.Data.Project.ProjectRoot);
        var outputDirectory = Path.Combine(root, "assembly-bom");
        Directory.CreateDirectory(outputDirectory);
        var outputFile = Path.Combine(outputDirectory, $"{load.Data.Project.ProjectName}-assembly-bom.csv");

        await File.WriteAllTextAsync(outputFile, FormatBomCsv(load.Data.BomRows), cancellationToken);

        var result = new AssemblyExportResult("assembly-bom", outputDirectory, outputFile, load.Data.BomRows.Count, [outputFile]);
        return ToolResponse<AssemblyExportResult>.Ok($"Exported PCBWay assembly BOM: {outputFile}", result);
    }

    public async Task<ToolResponse<AssemblyExportResult>> ExportCplAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var load = LoadAssembly(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<AssemblyExportResult>.Fail(load.Summary, load.Error?.Code ?? "ASSEMBLY_LOAD_FAILED", load.Error?.Message);
        }

        var root = ExportPathFactory.CreateExportDirectory(load.Data.Project.ProjectRoot);
        var outputDirectory = Path.Combine(root, "cpl");
        Directory.CreateDirectory(outputDirectory);
        var outputFile = Path.Combine(outputDirectory, $"{load.Data.Project.ProjectName}-cpl.csv");

        await File.WriteAllTextAsync(outputFile, FormatCplCsv(load.Data.CplRows), cancellationToken);

        var result = new AssemblyExportResult("cpl", outputDirectory, outputFile, load.Data.CplRows.Count, [outputFile]);
        return ToolResponse<AssemblyExportResult>.Ok($"Exported PCBWay CPL: {outputFile}", result);
    }

    public ToolResponse<AssemblyValidationResult> ValidateAssemblyPackage(string projectPath)
    {
        var load = LoadAssembly(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<AssemblyValidationResult>.Fail(load.Summary, load.Error?.Code ?? "ASSEMBLY_LOAD_FAILED", load.Error?.Message);
        }

        var diagnostics = Validate(load.Data);
        var errors = diagnostics.Count(static diagnostic => diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        var warnings = diagnostics.Count(static diagnostic => diagnostic.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));
        var result = new AssemblyValidationResult(
            load.Data.Project.ProjectRoot,
            errors == 0,
            errors,
            warnings,
            load.Data.Components.Count,
            load.Data.BomRows.Count,
            load.Data.CplRows.Count,
            diagnostics);

        return ToolResponse<AssemblyValidationResult>.Ok(
            errors == 0
                ? $"Assembly package validation passed with {warnings} warning(s)."
                : $"Assembly package validation found {errors} error(s) and {warnings} warning(s).",
            result,
            diagnostics.Where(static diagnostic => diagnostic.Severity == "warning").Select(static diagnostic => diagnostic.Message).ToArray());
    }

    public async Task<ToolResponse<AssemblyPackageResult>> CreatePcbWayAssemblyPackageAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var load = LoadAssembly(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<AssemblyPackageResult>.Fail(load.Summary, load.Error?.Code ?? "ASSEMBLY_LOAD_FAILED", load.Error?.Message);
        }

        var manufacturing = await _exportService.ExportManufacturingFilesAsync(projectPath, cancellationToken);
        if (manufacturing.Data is null)
        {
            return ToolResponse<AssemblyPackageResult>.Fail(manufacturing.Summary, manufacturing.Error?.Code ?? "EXPORT_FAILED", manufacturing.Error?.Message);
        }

        var packageRoot = Path.Combine(load.Data.Project.ProjectRoot, ".pcbhelper", "packages", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(packageRoot);

        var bomPath = Path.Combine(packageRoot, $"{load.Data.Project.ProjectName}-assembly-bom.csv");
        var cplPath = Path.Combine(packageRoot, $"{load.Data.Project.ProjectName}-cpl.csv");
        var validationPath = Path.Combine(packageRoot, "assembly-validation.json");
        var manifestPath = Path.Combine(packageRoot, "manifest.json");
        var zipPath = Path.Combine(packageRoot, $"{load.Data.Project.ProjectName}-pcbway-assembly.zip");

        await File.WriteAllTextAsync(bomPath, FormatBomCsv(load.Data.BomRows), cancellationToken);
        await File.WriteAllTextAsync(cplPath, FormatCplCsv(load.Data.CplRows), cancellationToken);

        var diagnostics = Validate(load.Data);
        var validation = new AssemblyValidationResult(
            load.Data.Project.ProjectRoot,
            diagnostics.All(static diagnostic => diagnostic.Severity != "error"),
            diagnostics.Count(static diagnostic => diagnostic.Severity == "error"),
            diagnostics.Count(static diagnostic => diagnostic.Severity == "warning"),
            load.Data.Components.Count,
            load.Data.BomRows.Count,
            load.Data.CplRows.Count,
            diagnostics);
        await File.WriteAllTextAsync(validationPath, JsonSerializer.Serialize(validation, JsonOptions), cancellationToken);

        var doctor = await _doctor.RunAsync(cancellationToken);
        var included = manufacturing.Data.GeneratedFiles
            .Concat([bomPath, cplPath, validationPath])
            .Where(File.Exists)
            .ToArray();
        var manifest = new PcbWayAssemblyPackageManifest(
            load.Data.Project.ProjectName,
            DateTimeOffset.UtcNow,
            doctor.Data?.VersionOutput,
            "pcbway-assembly-v1",
            included.Select(Path.GetFileName).Where(static name => name is not null).Cast<string>().ToArray());
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(manifestPath, "manifest.json");
            foreach (var file in included)
            {
                var name = Path.GetFileName(file);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    archive.CreateEntryFromFile(file, name);
                }
            }
        }

        var result = new AssemblyPackageResult(
            zipPath,
            manifestPath,
            bomPath,
            cplPath,
            validationPath,
            included,
            validation);
        return ToolResponse<AssemblyPackageResult>.Ok($"Created PCBWay assembly package: {zipPath}", result, validation.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == "warning")
            .Select(static diagnostic => diagnostic.Message)
            .ToArray());
    }

    private ToolResponse<LoadedAssembly> LoadAssembly(string projectPath)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<LoadedAssembly>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (project.Data.BoardFile is null)
        {
            return ToolResponse<LoadedAssembly>.Fail("Assembly export requires a .kicad_pcb file.", "BOARD_FILE_MISSING");
        }

        var board = KiCadBoardParser.Parse(project.Data.BoardFile);
        var schematicProperties = project.Data.SchematicFile is not null && File.Exists(project.Data.SchematicFile)
            ? LoadSchematicProperties(project.Data.SchematicFile)
            : new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        var components = board.Footprints
            .Select(footprint => CreateComponent(footprint, schematicProperties))
            .OrderBy(static component => component.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var assembled = components
            .Where(static component => component.Reference.Length > 0 && !component.IsExcludedFromAssembly && !component.IsUnannotated)
            .ToArray();
        var bomRows = CreateBomRows(assembled);
        var cplRows = assembled
            .Where(static component => component.MountType is "smd" or "mixed")
            .Where(static component => component.XMillimeters is not null && component.YMillimeters is not null)
            .Select(static component => new AssemblyCplRow(
                component.Reference,
                component.XMillimeters!.Value,
                component.YMillimeters!.Value,
                component.Side,
                component.RotationDegrees ?? 0,
                component.Value,
                component.Package))
            .ToArray();

        return ToolResponse<LoadedAssembly>.Ok(
            "Loaded assembly data.",
            new LoadedAssembly(project.Data, components, bomRows, cplRows));
    }

    private static Dictionary<string, Dictionary<string, string>> LoadSchematicProperties(string schematicFile)
    {
        var schematic = KiCadSchematicParser.Parse(schematicFile);
        var byReference = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in schematic.Symbols.Where(static symbol => !string.IsNullOrWhiteSpace(symbol.Reference)))
        {
            if (!byReference.TryGetValue(symbol.Reference!, out var properties))
            {
                properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                byReference[symbol.Reference!] = properties;
            }

            foreach (var property in symbol.Properties.Values)
            {
                if (!properties.ContainsKey(property.Name) && !string.IsNullOrWhiteSpace(property.Value))
                {
                    properties[property.Name] = property.Value;
                }
            }
        }

        return byReference;
    }

    private static AssemblyComponent CreateComponent(
        KiCadFootprint footprint,
        IReadOnlyDictionary<string, Dictionary<string, string>> schematicProperties)
    {
        var reference = footprint.Reference ?? string.Empty;
        schematicProperties.TryGetValue(reference, out var schematic);
        schematic ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var properties = new Dictionary<string, string>(schematic, StringComparer.OrdinalIgnoreCase);
        foreach (var property in footprint.Properties.Values)
        {
            if (!string.IsNullOrWhiteSpace(property.Value))
            {
                properties[property.Name] = property.Value;
            }
        }

        var value = GetProperty(properties, "Value") ?? string.Empty;
        var manufacturer = GetFirstProperty(properties, ManufacturerFields);
        var mpn = GetFirstProperty(properties, MpnFields);
        var supplierPart = GetFirstProperty(properties, SupplierPartFields);
        var notes = GetProperty(properties, "Notes") ?? GetProperty(properties, "AssemblyNotes") ?? string.Empty;
        var mountType = DetermineMountType(footprint);
        var isExcluded = IsExcluded(properties);
        var isUnannotated = string.IsNullOrWhiteSpace(reference)
            || reference.Contains("REF**", StringComparison.OrdinalIgnoreCase)
            || reference.EndsWith("?", StringComparison.Ordinal);

        return new AssemblyComponent(
            reference,
            value,
            footprint.FootprintName,
            footprint.Side,
            footprint.XMillimeters,
            footprint.YMillimeters,
            footprint.RotationDegrees,
            mountType,
            manufacturer ?? string.Empty,
            mpn ?? string.Empty,
            supplierPart ?? string.Empty,
            notes,
            isExcluded,
            isUnannotated);
    }

    private static IReadOnlyList<AssemblyBomRow> CreateBomRows(IReadOnlyList<AssemblyComponent> assembled)
    {
        return assembled
            .GroupBy(static component => new BomKey(
                component.Value,
                component.Package,
                component.Manufacturer,
                component.Mpn,
                component.SupplierPart,
                component.Side,
                component.MountType,
                component.Notes))
            .Select(static group => new AssemblyBomRow(
                string.Join(", ", group.Select(static item => item.Reference).Order(StringComparer.OrdinalIgnoreCase)),
                group.Count(),
                group.Key.Value,
                group.Key.Package,
                group.Key.Manufacturer,
                group.Key.Mpn,
                group.Key.SupplierPart,
                group.Key.Side,
                group.Key.MountType,
                group.Key.Notes))
            .OrderBy(static row => row.Designators, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<AssemblyDiagnostic> Validate(LoadedAssembly loaded)
    {
        var diagnostics = new List<AssemblyDiagnostic>();
        foreach (var duplicate in loaded.Components
            .Where(static component => !string.IsNullOrWhiteSpace(component.Reference))
            .GroupBy(static component => component.Reference, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1))
        {
            diagnostics.Add(Error("DUPLICATE_REFERENCE", duplicate.Key, $"Duplicate footprint reference: {duplicate.Key}."));
        }

        foreach (var component in loaded.Components)
        {
            if (component.IsUnannotated)
            {
                diagnostics.Add(Error("ASSEMBLY_REFERENCE_INVALID", component.Reference, "Footprint has missing or unannotated reference."));
                continue;
            }

            if (component.IsExcludedFromAssembly)
            {
                diagnostics.Add(Warning("ASSEMBLY_COMPONENT_EXCLUDED", component.Reference, $"{component.Reference} is marked DNP/excluded and will not be exported for assembly."));
                continue;
            }

            if (component.XMillimeters is null || component.YMillimeters is null || string.IsNullOrWhiteSpace(component.Side))
            {
                diagnostics.Add(Error("ASSEMBLY_PLACEMENT_MISSING", component.Reference, $"{component.Reference} is missing assembly placement data."));
            }

            if (component.MountType is "smd" or "mixed")
            {
                if (!loaded.CplRows.Any(row => row.Designator.Equals(component.Reference, StringComparison.OrdinalIgnoreCase)))
                {
                    diagnostics.Add(Error("ASSEMBLY_CPL_MISSING", component.Reference, $"{component.Reference} is an assembled SMD footprint but has no CPL row."));
                }
            }
            else if (component.MountType == "through-hole")
            {
                diagnostics.Add(Warning("ASSEMBLY_THT_CPL_EXCLUDED", component.Reference, $"{component.Reference} is through-hole and is excluded from the CPL by default."));
            }

            if (string.IsNullOrWhiteSpace(component.Mpn) && string.IsNullOrWhiteSpace(component.SupplierPart))
            {
                diagnostics.Add(Warning("ASSEMBLY_PART_NUMBER_MISSING", component.Reference, $"{component.Reference} has no MPN or supplier part number."));
            }

            if (HasOrientationRisk(component))
            {
                diagnostics.Add(Warning("ASSEMBLY_ORIENTATION_REVIEW", component.Reference, $"{component.Reference} may need polarity or pin-1 orientation review."));
            }
        }

        var bomDesignators = ExpandDesignators(loaded.BomRows.Select(static row => row.Designators));
        var cplDesignators = loaded.CplRows.Select(static row => row.Designator).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedCpl = loaded.Components
            .Where(static component => !component.IsExcludedFromAssembly && !component.IsUnannotated && component.MountType is "smd" or "mixed")
            .Select(static component => component.Reference)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var designator in expectedCpl.Where(designator => !cplDesignators.Contains(designator)))
        {
            diagnostics.Add(Error("ASSEMBLY_BOM_CPL_MISMATCH", designator, $"{designator} is expected in the CPL but was not exported."));
        }

        foreach (var designator in cplDesignators.Where(designator => !bomDesignators.Contains(designator)))
        {
            diagnostics.Add(Error("ASSEMBLY_BOM_CPL_MISMATCH", designator, $"{designator} is present in the CPL but missing from the assembly BOM."));
        }

        return diagnostics
            .OrderBy(static diagnostic => diagnostic.Severity == "error" ? 0 : 1)
            .ThenBy(static diagnostic => diagnostic.Reference, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatBomCsv(IReadOnlyList<AssemblyBomRow> rows)
    {
        var builder = new StringBuilder();
        AppendCsvLine(builder, "Designators", "Quantity", "Value", "Package", "Manufacturer", "MPN", "SupplierPart", "Side", "MountType", "Notes");
        foreach (var row in rows)
        {
            AppendCsvLine(
                builder,
                row.Designators,
                row.Quantity.ToString(CultureInfo.InvariantCulture),
                row.Value,
                row.Package,
                row.Manufacturer,
                row.Mpn,
                row.SupplierPart,
                row.Side,
                row.MountType,
                row.Notes);
        }

        return builder.ToString();
    }

    private static string FormatCplCsv(IReadOnlyList<AssemblyCplRow> rows)
    {
        var builder = new StringBuilder();
        AppendCsvLine(builder, "Designator", "Mid X", "Mid Y", "Layer", "Rotation", "Value", "Package");
        foreach (var row in rows)
        {
            AppendCsvLine(
                builder,
                row.Designator,
                row.XMillimeters.ToString("0.###", CultureInfo.InvariantCulture),
                row.YMillimeters.ToString("0.###", CultureInfo.InvariantCulture),
                row.Side,
                row.RotationDegrees.ToString("0.###", CultureInfo.InvariantCulture),
                row.Value,
                row.Package);
        }

        return builder.ToString();
    }

    private static void AppendCsvLine(StringBuilder builder, params string[] values)
    {
        builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string DetermineMountType(KiCadFootprint footprint)
    {
        var hasSmd = footprint.Pads.Any(static pad => pad.Type?.Equals("smd", StringComparison.OrdinalIgnoreCase) == true);
        var hasThroughHole = footprint.Pads.Any(static pad => pad.Type?.Contains("thru_hole", StringComparison.OrdinalIgnoreCase) == true
            || pad.Type?.Contains("through", StringComparison.OrdinalIgnoreCase) == true);

        return (hasSmd, hasThroughHole) switch
        {
            (true, true) => "mixed",
            (true, false) => "smd",
            (false, true) => "through-hole",
            _ => "unknown"
        };
    }

    private static bool IsExcluded(IReadOnlyDictionary<string, string> properties)
    {
        return IsTruthy(GetProperty(properties, "DNP"))
            || IsTruthy(GetProperty(properties, "DoNotPopulate"))
            || IsTruthy(GetProperty(properties, "Do Not Populate"))
            || IsTruthy(GetProperty(properties, "ExcludeFromBOM"))
            || IsTruthy(GetProperty(properties, "Exclude from BOM"))
            || IsTruthy(GetProperty(properties, "ExcludeFromPositionFiles"))
            || IsTruthy(GetProperty(properties, "Exclude from position files"))
            || IsTruthy(GetProperty(properties, "ExcludeFromPosFiles"));
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("y", StringComparison.OrdinalIgnoreCase)
                || value.Equals("dnp", StringComparison.OrdinalIgnoreCase)
                || value.Equals("exclude", StringComparison.OrdinalIgnoreCase)
                || value.Equals("excluded", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasOrientationRisk(AssemblyComponent component)
    {
        var reference = component.Reference;
        var value = component.Value;
        var package = component.Package;
        return reference.StartsWith("D", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("LED", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("U", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("Q", StringComparison.OrdinalIgnoreCase)
            || package.Contains("LED", StringComparison.OrdinalIgnoreCase)
            || package.Contains("Diode", StringComparison.OrdinalIgnoreCase)
            || value.Contains("polar", StringComparison.OrdinalIgnoreCase)
            || value.Contains("electro", StringComparison.OrdinalIgnoreCase)
            || value.Contains("tantal", StringComparison.OrdinalIgnoreCase)
            || package.Contains("CP", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ExpandDesignators(IEnumerable<string> values)
    {
        return values
            .SelectMany(static value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetFirstProperty(IReadOnlyDictionary<string, string> properties, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var value = GetProperty(properties, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetProperty(IReadOnlyDictionary<string, string> properties, string name)
    {
        return properties.TryGetValue(name, out var value) ? value : null;
    }

    private static AssemblyDiagnostic Error(string code, string reference, string message)
    {
        return new AssemblyDiagnostic("error", code, reference, message);
    }

    private static AssemblyDiagnostic Warning(string code, string reference, string message)
    {
        return new AssemblyDiagnostic("warning", code, reference, message);
    }

    private sealed record LoadedAssembly(
        ProjectSummary Project,
        IReadOnlyList<AssemblyComponent> Components,
        IReadOnlyList<AssemblyBomRow> BomRows,
        IReadOnlyList<AssemblyCplRow> CplRows);

    private sealed record BomKey(
        string Value,
        string Package,
        string Manufacturer,
        string Mpn,
        string SupplierPart,
        string Side,
        string MountType,
        string Notes);
}

public sealed record AssemblyInspectionResult(
    string ProjectRoot,
    string BoardFile,
    string? SchematicFile,
    IReadOnlyList<AssemblyComponent> Components,
    IReadOnlyList<AssemblyBomRow> BomRows,
    IReadOnlyList<AssemblyCplRow> CplRows);

public sealed record AssemblyComponent(
    string Reference,
    string Value,
    string Package,
    string Side,
    double? XMillimeters,
    double? YMillimeters,
    double? RotationDegrees,
    string MountType,
    string Manufacturer,
    string Mpn,
    string SupplierPart,
    string Notes,
    bool IsExcludedFromAssembly,
    bool IsUnannotated);

public sealed record AssemblyBomRow(
    string Designators,
    int Quantity,
    string Value,
    string Package,
    string Manufacturer,
    string Mpn,
    string SupplierPart,
    string Side,
    string MountType,
    string Notes);

public sealed record AssemblyCplRow(
    string Designator,
    double XMillimeters,
    double YMillimeters,
    string Side,
    double RotationDegrees,
    string Value,
    string Package);

public sealed record AssemblyExportResult(
    string Kind,
    string OutputDirectory,
    string OutputFile,
    int RowCount,
    IReadOnlyList<string> GeneratedFiles);

public sealed record AssemblyValidationResult(
    string ProjectRoot,
    bool Valid,
    int ErrorCount,
    int WarningCount,
    int ComponentCount,
    int BomRowCount,
    int CplRowCount,
    IReadOnlyList<AssemblyDiagnostic> Diagnostics);

public sealed record AssemblyDiagnostic(
    string Severity,
    string Code,
    string Reference,
    string Message);

public sealed record AssemblyPackageResult(
    string ZipPath,
    string ManifestPath,
    string BomPath,
    string CplPath,
    string ValidationPath,
    IReadOnlyList<string> IncludedFiles,
    AssemblyValidationResult Validation);

public sealed record PcbWayAssemblyPackageManifest(
    string ProjectName,
    DateTimeOffset CreatedAtUtc,
    string? KiCadVersion,
    string Profile,
    IReadOnlyList<string> IncludedFiles);
