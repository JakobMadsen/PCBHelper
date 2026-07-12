namespace PCBHelper.Core;

public sealed class KiCadSimulationNetlistService
{
    private readonly ProjectDiscoveryService _projects; private readonly KiCadCliLocator _locator; private readonly ICommandRunner _runner;
    public KiCadSimulationNetlistService(ProjectDiscoveryService projects,KiCadCliLocator locator,ICommandRunner runner){_projects=projects;_locator=locator;_runner=runner;}
    public ToolResponse<KiCadSimulationModelValidation> ValidateModels(string projectPath)
    {
        var project=_projects.GetSummary(projectPath);if(!project.Success||project.Data?.SchematicFile is null)return ToolResponse<KiCadSimulationModelValidation>.Fail(project.Summary,project.Error?.Code??"SCHEMATIC_NOT_FOUND",project.Error?.Message);
        var schematic=KiCadSchematicParser.Parse(project.Data.SchematicFile);var diagnostics=new List<KiCadSimulationModelDiagnostic>();
        foreach(var symbol in schematic.Symbols.Where(s=>s.Reference is not null&&!s.Reference.StartsWith('#')))
        {
            var passive=symbol.LibId is not null&&(symbol.LibId.StartsWith("Device:R",StringComparison.OrdinalIgnoreCase)||symbol.LibId.StartsWith("Device:C",StringComparison.OrdinalIgnoreCase)||symbol.LibId.StartsWith("Device:L",StringComparison.OrdinalIgnoreCase));
            var model=symbol.Properties.Keys.Any(k=>k.Contains("Spice_Model",StringComparison.OrdinalIgnoreCase)||k.Contains("Sim.Model",StringComparison.OrdinalIgnoreCase));
            var pinMap=symbol.Properties.Keys.Any(k=>k.Contains("Spice_Node_Sequence",StringComparison.OrdinalIgnoreCase)||k.Contains("Sim.Pins",StringComparison.OrdinalIgnoreCase));
            if(!passive&&!model)diagnostics.Add(new(symbol.Reference!,"SIMULATION_MODEL_UNAVAILABLE","A non-passive symbol requires an explicit SPICE model."));
            if(!passive&&model&&!pinMap)diagnostics.Add(new(symbol.Reference!,"SIMULATION_PIN_MAP_UNVERIFIED","The SPICE model requires an explicit verified pin map."));
        }
        var result=new KiCadSimulationModelValidation(diagnostics.Count==0,diagnostics);
        return ToolResponse<KiCadSimulationModelValidation>.Ok(result.Valid?"KiCad simulation models and pin maps are ready.":"KiCad simulation model validation found blockers.",result);
    }
    public async Task<ToolResponse<KiCadSpiceNetlistResult>> ExportAsync(string projectPath,CancellationToken cancellationToken=default)
    {
        var validation=ValidateModels(projectPath);if(validation.Data is null)return ToolResponse<KiCadSpiceNetlistResult>.Fail(validation.Summary,validation.Error?.Code??"SIMULATION_MODEL_UNAVAILABLE",validation.Error?.Message);if(!validation.Data.Valid)return ToolResponse<KiCadSpiceNetlistResult>.Fail("KiCad model or pin-map validation failed.",validation.Data.Diagnostics[0].Code,validation.Data.Diagnostics[0].Message);
        var project=_projects.GetSummary(projectPath).Data!;var cli=_locator.Locate();if(!cli.Found||cli.ExecutablePath is null)return ToolResponse<KiCadSpiceNetlistResult>.Fail("kicad-cli is unavailable.","KICAD_CLI_NOT_FOUND");
        var dir=Path.Combine(project.ProjectRoot,".pcbhelper","simulations","netlists",DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));Directory.CreateDirectory(dir);var output=Path.Combine(dir,project.ProjectName+".cir");
        var run=await _runner.RunAsync(cli.ExecutablePath,new[]{"sch","export","netlist","--format","spice","-o",output,project.SchematicFile!},project.ProjectRoot,cancellationToken);
        if(run.ExitCode!=0||!File.Exists(output))return ToolResponse<KiCadSpiceNetlistResult>.Fail("KiCad SPICE netlist export failed.","SIMULATION_NETLIST_EXPORT_FAILED",run.StandardError);
        return ToolResponse<KiCadSpiceNetlistResult>.Ok("Exported validated KiCad SPICE netlist.",new(output,validation.Data));
    }
}
public sealed record KiCadSimulationModelDiagnostic(string Reference,string Code,string Message);
public sealed record KiCadSimulationModelValidation(bool Valid,IReadOnlyList<KiCadSimulationModelDiagnostic> Diagnostics);
public sealed record KiCadSpiceNetlistResult(string NetlistPath,KiCadSimulationModelValidation Validation);
