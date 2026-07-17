using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed class PcbWayReleaseService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ProjectDiscoveryService _projects;
    private readonly ExportService _exports;
    private readonly AssemblyService _assembly;
    private readonly EngineeringGateService _gates;
    private readonly DesignIntentService _designIntent;
    public PcbWayReleaseService(ProjectDiscoveryService projects, ExportService exports, AssemblyService assembly, EngineeringGateService gates, DesignIntentService designIntent)
    { _projects=projects; _exports=exports; _assembly=assembly; _gates=gates; _designIntent=designIntent; }
    public PcbWayReleaseService(ProjectDiscoveryService projects, ExportService exports, AssemblyService assembly, EngineeringGateService gates)
        : this(projects, exports, assembly, gates, new DesignIntentService(projects, new BoardInspectionService(projects))) { }

    public ToolResponse<ReleaseRequirementsResult> ValidateRequirements(string projectPath)
    {
        var project=_projects.GetSummary(projectPath); if(!project.Success||project.Data?.BoardFile is null) return ToolResponse<ReleaseRequirementsResult>.Fail(project.Summary,project.Error?.Code??"PROJECT_NOT_FOUND",project.Error?.Message);
        var text=string.Join("\n",Directory.GetFiles(project.Data.ProjectRoot,"*",SearchOption.AllDirectories).Where(path=>!path.Contains(Path.DirectorySeparatorChar+".pcbhelper"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&Path.GetExtension(path) is ".md" or ".json" or ".txt").Select(File.ReadAllText));
        var board=KiCadBoardParser.Parse(project.Data.BoardFile);
        var checks=new List<ReleaseRequirementCheck>();
        Add("testpoints", @"(?i)test\s*points?\s*(?:are\s*)?(?:required|mandatory|needed)", board.Footprints.Any(f=>f.Reference?.StartsWith("TP",StringComparison.OrdinalIgnoreCase)==true), "No TP* footprint is present.");
        Add("mounting-holes", @"(?i)mounting\s*holes?\s*(?:are\s*)?(?:required|mandatory|needed)", board.Footprints.Any(f=>f.Reference?.StartsWith("H",StringComparison.OrdinalIgnoreCase)==true||f.FootprintName.Contains("MountingHole",StringComparison.OrdinalIgnoreCase)), "No mounting-hole footprint is present.");
        var hasZones=board.Text.Contains("(zone",StringComparison.Ordinal);checks.Add(new("filled-zones",hasZones,!hasZones||board.Text.Contains("(filled_polygon",StringComparison.Ordinal),hasZones&&!board.Text.Contains("(filled_polygon",StringComparison.Ordinal)?"Copper zones exist but are not filled. Refill and save in KiCad before release.":null));
        var result=new ReleaseRequirementsResult(checks.All(c=>!c.Required||c.Implemented),checks);
        return ToolResponse<ReleaseRequirementsResult>.Ok(result.Passed?"Release requirements are implemented.":"One or more documented release requirements are missing.",result);
        void Add(string id,string pattern,bool implemented,string message){var required=Regex.IsMatch(text,pattern);checks.Add(new(id,required,implemented,required&&!implemented?message:null));}
    }

    public async Task<ToolResponse<PcbWayReleaseResult>> GenerateAsync(string projectPath,CancellationToken cancellationToken=default)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<PcbWayReleaseResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);

        var requirements = ValidateRequirements(projectPath);
        if (requirements.Data is { Passed: false })
            return ToolResponse<PcbWayReleaseResult>.Fail("Release requirements are not implemented.", "RELEASE_REQUIREMENT_MISSING", data: null);

        var intent = _designIntent.Analyze(projectPath);
        var missingCriticalEvidence = intent.Data?.Findings.Any(finding =>
            finding.RuleId == "INTENT-COMPONENT-EVIDENCE-001"
            && finding.Severity == DesignIntentSeverity.Error
            && finding.Outcome == DesignIntentOutcome.NotProven) == true;
        if (missingCriticalEvidence)
            return ToolResponse<PcbWayReleaseResult>.Fail("Critical component evidence is unavailable.", "COMPONENT_EVIDENCE_UNAVAILABLE");
        if (intent.Data is null || !intent.Data.Passed)
            return ToolResponse<PcbWayReleaseResult>.Fail("Design-intent release gate did not pass.", intent.Error?.Code ?? "DESIGN_INTENT_GATE_FAILED", intent.Error?.Message ?? intent.Summary);

        var releaseRequirements = new EngineeringGateRequirements("required", "required", "required", "skip", "required");
        var gate = await _gates.RunAsync(projectPath, releaseRequirements, cancellationToken);
        if (!gate.Success || gate.Data?.Status != EngineeringGateStatus.Passed)
            return ToolResponse<PcbWayReleaseResult>.Fail("Engineering release gate did not pass.", "RELEASE_GATE_FAILED", gate.Error?.Message);
        var manufacturing=await _exports.ExportManufacturingFilesAsync(projectPath,cancellationToken); if(manufacturing.Data is null)return ToolResponse<PcbWayReleaseResult>.Fail(manufacturing.Summary,manufacturing.Error?.Code??"EXPORT_FAILED",manufacturing.Error?.Message);
        var bom=await _assembly.ExportAssemblyBomAsync(projectPath,cancellationToken);var cpl=await _assembly.ExportCplAsync(projectPath,cancellationToken);var validation=_assembly.ValidateAssemblyPackage(projectPath);
        if(bom.Data is null||cpl.Data is null||validation.Data is null||!validation.Data.Valid)return ToolResponse<PcbWayReleaseResult>.Fail("Assembly release validation failed.","ASSEMBLY_VALIDATION_FAILED");
        var missingFabricationFiles = MissingRequiredFabricationFiles(manufacturing.Data.GeneratedFiles);
        if (missingFabricationFiles.Count > 0)
            return ToolResponse<PcbWayReleaseResult>.Fail(
                $"Manufacturing export is incomplete: {string.Join(", ", missingFabricationFiles)}.",
                "FABRICATION_OUTPUT_INCOMPLETE");

        var root=Path.Combine(project.Data.ProjectRoot,".pcbhelper","releases",DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));Directory.CreateDirectory(root);
        var gerberZip=Path.Combine(root,$"{project.Data.ProjectName}-gerbers.zip");
        using(var archive=ZipFile.Open(gerberZip,ZipArchiveMode.Create))foreach(var file in manufacturing.Data.GeneratedFiles.Where(IsPcbWayFabricationFile))archive.CreateEntryFromFile(file,Path.GetFileName(file));
        var bomPath=Path.Combine(root,$"{project.Data.ProjectName}-bom.csv");var cplPath=Path.Combine(root,$"{project.Data.ProjectName}-cpl.csv");File.Copy(bom.Data.OutputFile,bomPath);File.Copy(cpl.Data.OutputFile,cplPath);
        var settingsPath=Path.Combine(root,"pcbway-order-settings.json");var settings=new PcbWayOrderSettings(2,"FR-4",1.6,"1 oz","green","white","HASL lead free","single pieces","top/bottom according to CPL","Do not approve substitutions without customer confirmation.");await File.WriteAllTextAsync(settingsPath,JsonSerializer.Serialize(settings,JsonOptions),cancellationToken);
        var reviewPath=Path.Combine(root,"release-review.json");var review=new PcbWayReleaseReview(project.Data.ProjectName,DateTimeOffset.UtcNow,gate.Data,requirements.Data!,validation.Data,new[]{"Verify polarized component orientation and pin 1.","Verify connector pinout and mechanical fit.","Confirm component stock, substitutions, shipping, tax, and final price before payment."});await File.WriteAllTextAsync(reviewPath,JsonSerializer.Serialize(review,JsonOptions),cancellationToken);
        var result=new PcbWayReleaseResult(root,gerberZip,bomPath,cplPath,settingsPath,reviewPath,gate.Data,requirements.Data!,validation.Data);
        return ToolResponse<PcbWayReleaseResult>.Ok("Generated a PCBWay release without placing an order.",result);
    }
    internal static IReadOnlyList<string> MissingRequiredFabricationFiles(IEnumerable<string> paths)
    {
        var extensions = paths.Select(path => Path.GetExtension(path).ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        Require("top copper (.gtl)", ".gtl");
        Require("bottom copper (.gbl)", ".gbl");
        Require("top solder mask (.gts)", ".gts");
        Require("bottom solder mask (.gbs)", ".gbs");
        Require("top silkscreen (.gto)", ".gto");
        Require("board outline (.gm1)", ".gm1");
        Require("drill file (.drl)", ".drl");
        return missing;

        void Require(string description, string extension)
        {
            if (!extensions.Contains(extension))
                missing.Add(description);
        }
    }

    internal static bool IsPcbWayFabricationFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".gtl" or ".gbl" or ".gts" or ".gbs" or ".gto" or ".gbo" or ".gm1" or ".drl" or ".gbrjob";
    }
}
public sealed record ReleaseRequirementCheck(string Id,bool Required,bool Implemented,string? Message);
public sealed record ReleaseRequirementsResult(bool Passed,IReadOnlyList<ReleaseRequirementCheck> Checks);
public sealed record PcbWayOrderSettings(int Layers,string Material,double ThicknessMm,string CopperWeight,string SolderMask,string Silkscreen,string SurfaceFinish,string BoardType,string AssemblySides,string SubstitutionPolicy);
public sealed record PcbWayReleaseReview(string ProjectName,DateTimeOffset CreatedAtUtc,EngineeringGateResult EngineeringGate,ReleaseRequirementsResult Requirements,AssemblyValidationResult Assembly,IReadOnlyList<string> HumanReview);
public sealed record PcbWayReleaseResult(string OutputDirectory,string GerberZip,string Bom,string Cpl,string OrderSettings,string ReviewReport,EngineeringGateResult EngineeringGate,ReleaseRequirementsResult Requirements,AssemblyValidationResult Assembly);
