using System.Text;
using System.Text.Json;

namespace PCBHelper.Core;

public sealed class FootprintLibraryTransactionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ProjectDiscoveryService _projects;
    private readonly ProjectTransactionService _transactions;
    private readonly EngineeringGateService _gates;
    private readonly KiCadCliLocator _kiCad;
    private readonly FreeRoutingLocator _freeRouting;
    private readonly ICommandRunner _runner;

    public FootprintLibraryTransactionService(
        ProjectDiscoveryService projects,
        ProjectTransactionService transactions,
        EngineeringGateService gates,
        KiCadCliLocator kiCad,
        FreeRoutingLocator freeRouting,
        ICommandRunner runner)
    {
        _projects = projects;
        _transactions = transactions;
        _gates = gates;
        _kiCad = kiCad;
        _freeRouting = freeRouting;
        _runner = runner;
    }

    public async Task<ToolResponse<FootprintLibraryPreviewResult>> PreviewAsync(string projectPath, CancellationToken cancellationToken=default)
    {
        var project=_projects.GetSummary(projectPath);
        if(!project.Success||project.Data?.BoardFile is null||project.Data.SchematicFile is null)
            return ToolResponse<FootprintLibraryPreviewResult>.Fail(project.Summary,project.Error?.Code??"PROJECT_FILES_MISSING",project.Error?.Message);
        var cli=_kiCad.Locate();
        if(cli.ExecutablePath is null)return ToolResponse<FootprintLibraryPreviewResult>.Fail("kicad-cli is required.","KICAD_CLI_NOT_FOUND");
        var python=ResolvePython(cli.ExecutablePath);
        if(python is null)return ToolResponse<FootprintLibraryPreviewResult>.Fail("KiCad Python is required.","KICAD_PYTHON_NOT_FOUND");
        var sandbox=Path.Combine(Path.GetTempPath(),"pcbhelper-footprint-library",Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);
            foreach(var file in Directory.GetFiles(project.Data.ProjectRoot,"*",SearchOption.TopDirectoryOnly))File.Copy(file,Path.Combine(sandbox,Path.GetFileName(file)));
            var sandboxProjects=new ProjectDiscoveryService(ProjectScopePolicy.Unrestricted());
            var sandboxProject=sandboxProjects.GetSummary(sandbox).Data!;
            var board=KiCadBoardParser.Parse(sandboxProject.BoardFile!);
            var custom=board.Footprints.Where(f=>IsCustom(f.FootprintName)).ToArray();
            var symbolRefs=new SchematicAuthoringService(sandboxProjects).ListSymbols(sandbox).Data?.Symbols.Select(s=>s.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase)??new();
            var schematic=new SchematicAuthoringService(sandboxProjects);
            foreach(var footprint in custom.Where(f=>f.Reference is not null&&symbolRefs.Contains(f.Reference)))
            {
                var item=ItemName(footprint.FootprintName);
                var changed=schematic.SetSymbolField(sandbox,footprint.Reference!,"Footprint",$"PCBHelper:{item}",false);
                if(!changed.Success)return ToolResponse<FootprintLibraryPreviewResult>.Fail(changed.Summary,changed.Error?.Code??"FOOTPRINT_LIBRARY_SYNC_FAILED",changed.Error?.Message);
            }
            var pretty=Path.Combine(sandbox,"PCBHelper.pretty");Directory.CreateDirectory(pretty);
            var scriptPath=Path.Combine(sandbox,".pcbhelper","footprint-library","synchronize.py");Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            await File.WriteAllTextAsync(scriptPath,BuildPythonScript(sandboxProject.BoardFile!,pretty),cancellationToken);
            var autorouter=new AutoroutingService(sandboxProjects,_kiCad,_freeRouting,_runner);
            var run=await autorouter.RunKiCadPythonAsync(python,scriptPath,sandbox,"KiCad project footprint library synchronization",cancellationToken);
            if(run.ExitCode!=0)return ToolResponse<FootprintLibraryPreviewResult>.Fail("KiCad could not create the project footprint library.","FOOTPRINT_LIBRARY_SYNC_FAILED",run.StandardError);
            await File.WriteAllTextAsync(Path.Combine(sandbox,"fp-lib-table"),"(fp_lib_table\n  (lib (name \"PCBHelper\")(type \"KiCad\")(uri \"${KIPRJMOD}/PCBHelper.pretty\")(options \"\")(descr \"PCBHelper project footprints\"))\n)\n",cancellationToken);

            var before=Capture(project.Data.ProjectRoot);var after=Capture(sandbox);
            var relative=before.Keys.Union(after.Keys,StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var changes=relative.Where(p=>!string.Equals(before.GetValueOrDefault(p),after.GetValueOrDefault(p),StringComparison.Ordinal))
                .Select(p=>PreparedFileChange.Create(p,before.GetValueOrDefault(p),after.GetValueOrDefault(p))).ToArray();
            if(changes.Length==0)return ToolResponse<FootprintLibraryPreviewResult>.Fail("Footprint library synchronization produced no changes.","TRANSACTION_EMPTY");
            var previewId=Guid.NewGuid().ToString("N");var previewRoot=PreviewRoot(project.Data.ProjectRoot,previewId);Directory.CreateDirectory(previewRoot);
            foreach(var change in changes.Where(c=>c.AfterContent is not null))
            {var target=Path.Combine(previewRoot,"files",change.RelativePath);Directory.CreateDirectory(Path.GetDirectoryName(target)!);await File.WriteAllTextAsync(target,change.AfterContent!,cancellationToken);}
            var manifest=new FootprintLibraryPreviewManifest(previewId,DateTimeOffset.UtcNow,changes.Select(c=>new FootprintLibraryFileManifest(c.RelativePath,c.BeforeHash,c.AfterHash,c.AfterContent is not null)).ToArray());
            await File.WriteAllTextAsync(Path.Combine(previewRoot,"manifest.json"),JsonSerializer.Serialize(manifest,JsonOptions),cancellationToken);
            return ToolResponse<FootprintLibraryPreviewResult>.Ok("Prepared a project-local footprint library without modifying the design.",new(previewId,manifest.CreatedAtUtc,manifest.Files));
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {return ToolResponse<FootprintLibraryPreviewResult>.Fail("Could not prepare project footprint library.","FOOTPRINT_LIBRARY_PREVIEW_FAILED",ex.Message);}
        finally{if(Directory.Exists(sandbox))try{Directory.Delete(sandbox,true);}catch(IOException){}}
    }

    public async Task<ToolResponse<FootprintLibraryApplyResult>> ApplyAsync(string projectPath,string previewId,CancellationToken cancellationToken=default)
    {
        if(previewId.Length!=32||!previewId.All(Uri.IsHexDigit))return ToolResponse<FootprintLibraryApplyResult>.Fail("Preview id is invalid.","FOOTPRINT_LIBRARY_PREVIEW_INVALID");
        var project=_projects.GetSummary(projectPath);if(!project.Success||project.Data is null)return ToolResponse<FootprintLibraryApplyResult>.Fail(project.Summary,project.Error?.Code??"PROJECT_NOT_FOUND",project.Error?.Message);
        var root=PreviewRoot(project.Data.ProjectRoot,previewId);var manifestPath=Path.Combine(root,"manifest.json");if(!File.Exists(manifestPath))return ToolResponse<FootprintLibraryApplyResult>.Fail("Preview not found.","FOOTPRINT_LIBRARY_PREVIEW_NOT_FOUND");
        var manifest=JsonSerializer.Deserialize<FootprintLibraryPreviewManifest>(await File.ReadAllTextAsync(manifestPath,cancellationToken),JsonOptions);
        if(manifest is null||manifest.PreviewId!=previewId)return ToolResponse<FootprintLibraryApplyResult>.Fail("Preview manifest is invalid.","FOOTPRINT_LIBRARY_PREVIEW_INVALID");
        var changes=new List<PreparedFileChange>();
        foreach(var file in manifest.Files)
        {
            var target=Path.GetFullPath(Path.Combine(project.Data.ProjectRoot,file.RelativePath));if(!target.StartsWith(project.Data.ProjectRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return ToolResponse<FootprintLibraryApplyResult>.Fail("Preview path leaves project.","PROJECT_SCOPE_VIOLATION");
            var before=File.Exists(target)?await File.ReadAllTextAsync(target,cancellationToken):null;if(ProjectTransactionService.ContentHash(before)!=file.BeforeHash)return ToolResponse<FootprintLibraryApplyResult>.Fail($"Project file changed after preview: {file.RelativePath}","TRANSACTION_CONFLICT");
            var afterPath=Path.Combine(root,"files",file.RelativePath);var after=file.HasAfterContent?await File.ReadAllTextAsync(afterPath,cancellationToken):null;if(ProjectTransactionService.ContentHash(after)!=file.AfterHash)return ToolResponse<FootprintLibraryApplyResult>.Fail("Preview snapshot failed integrity validation.","FOOTPRINT_LIBRARY_PREVIEW_CORRUPT");
            changes.Add(PreparedFileChange.Create(file.RelativePath,before,after));
        }
        var applied=await _transactions.ApplyAsync(project.Data.ProjectRoot,"Synchronize project-local footprint library",previewId,new[]{new PreparedOperation("sync-footprint-library","sync-footprint-library","Create exact PCBHelper.pretty library and link board/schematic footprints.")},changes,cancellationToken:cancellationToken);
        if(!applied.Success||applied.Data is null)return ToolResponse<FootprintLibraryApplyResult>.Fail(applied.Summary,applied.Error?.Code??"TRANSACTION_APPLY_FAILED",applied.Error?.Message);
        var gate=await _gates.RunAsync(project.Data.ProjectRoot,EngineeringGateRequirements.Default,cancellationToken);
        if(!gate.Success||gate.Data is null||gate.Data.Status==EngineeringGateStatus.ExecutionFailed){await _transactions.RestoreAsync(project.Data.ProjectRoot,applied.Data.Transaction.TransactionId,CancellationToken.None);return ToolResponse<FootprintLibraryApplyResult>.Fail("Engineering gate execution failed; transaction was rolled back.","ENGINEERING_GATE_EXECUTION_FAILED",gate.Error?.Message);}
        var recorded=await _transactions.SetGateResultAsync(project.Data.ProjectRoot,applied.Data.Transaction.TransactionId,gate.Data,cancellationToken);
        return ToolResponse<FootprintLibraryApplyResult>.Ok(gate.Data.Status==EngineeringGateStatus.Passed?"Synchronized footprint library and passed engineering gates.":$"Synchronized footprint library; engineering gate status is {gate.Data.Status}.",new(previewId,recorded.Data??applied.Data,gate.Data));
    }

    private static bool IsCustom(string name)=>!name.Contains(':')||name.StartsWith("PCBHelper:",StringComparison.OrdinalIgnoreCase);
    private static string ItemName(string name)=>name.Contains(':')?name[(name.IndexOf(':')+1)..]:name;
    private static string PreviewRoot(string root,string id)=>Path.Combine(root,".pcbhelper","footprint-library-previews",id);
    private static string? ResolvePython(string cli){var d=Path.GetDirectoryName(cli);return new[]{"python.exe","pythonw.exe","python3","python"}.Select(n=>Path.Combine(d!,n)).FirstOrDefault(File.Exists);}
    private static Dictionary<string,string> Capture(string root)
    {var paths=Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly).Where(p=>Path.GetExtension(p) is ".kicad_pcb" or ".kicad_sch"||Path.GetFileName(p)=="fp-lib-table").Concat(Directory.Exists(Path.Combine(root,"PCBHelper.pretty"))?Directory.GetFiles(Path.Combine(root,"PCBHelper.pretty"),"*.kicad_mod",SearchOption.TopDirectoryOnly):Array.Empty<string>());return paths.ToDictionary(p=>Path.GetRelativePath(root,p),File.ReadAllText,StringComparer.OrdinalIgnoreCase);}
    private static string BuildPythonScript(string board,string pretty)
    {static string Q(string s)=>"r\""+s.Replace("\"","\\\"")+"\"";return $"import pcbnew,os,sys\np={Q(board)}\nout={Q(pretty)}\nb=pcbnew.LoadBoard(p)\nio=pcbnew.PCB_IO_KICAD_SEXPR()\nsaved=set()\nfor fp in b.GetFootprints():\n old=str(fp.GetFPID().GetUniStringLibId())\n if ':' not in old or old.lower().startswith('pcbhelper:'):\n  item=str(fp.GetFPID().GetLibItemName())\n  fp.SetFPID(pcbnew.LIB_ID('PCBHelper',item))\n  if item not in saved:\n   io.FootprintSave(out,fp)\n   saved.add(item)\npcbnew.SaveBoard(p,b)\nsys.stdout.flush();sys.stderr.flush();os._exit(0)\n";}
}

public sealed record FootprintLibraryFileManifest(string RelativePath,string BeforeHash,string AfterHash,bool HasAfterContent);
public sealed record FootprintLibraryPreviewManifest(string PreviewId,DateTimeOffset CreatedAtUtc,IReadOnlyList<FootprintLibraryFileManifest> Files);
public sealed record FootprintLibraryPreviewResult(string PreviewId,DateTimeOffset CreatedAtUtc,IReadOnlyList<FootprintLibraryFileManifest> Files);
public sealed record FootprintLibraryApplyResult(string PreviewId,ProjectTransactionResult Transaction,EngineeringGateResult EngineeringGate);
