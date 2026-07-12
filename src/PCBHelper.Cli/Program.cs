using System.Text.Json;
using System.Globalization;
using PCBHelper.Core;

var projectDiscovery = new ProjectDiscoveryService();
var cliLocator = new KiCadCliLocator();
var runner = new ProcessCommandRunner();
var doctor = new KiCadDoctorService(cliLocator, runner, new NgspiceLocator());
var checkRunner = new CheckRunner(projectDiscovery, cliLocator, runner);
var exportService = new ExportService(projectDiscovery, cliLocator, runner);
var assemblyService = new AssemblyService(projectDiscovery, doctor, exportService);
var geometry = new GeometryService(projectDiscovery);
var changeReports = new ChangeReportService(projectDiscovery);
var geometryWorkflow = new GeometryWorkflowService(geometry, checkRunner, changeReports);
var componentService = new ComponentService(projectDiscovery);
var componentWorkflow = new ComponentValueWorkflowService(componentService, checkRunner, changeReports);
var routingService = new RoutingService(projectDiscovery);
var routingWorkflow = new RoutingWorkflowService(routingService, checkRunner, changeReports);
var autoroutingService = new AutoroutingService(projectDiscovery, cliLocator, runner);
var freeRoutingSetup = new FreeRoutingSetupService();
var schematicService = new SchematicAuthoringService(projectDiscovery);
var schematicWorkflow = new SchematicAuthoringWorkflowService(schematicService, checkRunner, changeReports);
var testSpecService = new TestSpecService(projectDiscovery);
var planRuntime = PCBHelperRuntime.ForCli();

var app = new CliApp(
    doctor,
    projectDiscovery,
    new BoardSummaryService(projectDiscovery),
    geometry,
    geometryWorkflow,
    componentWorkflow,
    routingWorkflow,
    autoroutingService,
    freeRoutingSetup,
    schematicWorkflow,
    testSpecService,
    new ChangeReviewService(projectDiscovery, changeReports, geometryWorkflow, componentWorkflow, routingWorkflow, schematicWorkflow),
    new BoardInspectionService(projectDiscovery),
    new CheckSummaryService(checkRunner),
    new GuiReviewService(cliLocator, new KiCadExecutableLocator(cliLocator), runner),
    checkRunner,
    exportService,
    new PackageService(
        projectDiscovery,
        doctor,
        exportService),
    assemblyService,
    new OpenKiCadService(projectDiscovery, new KiCadExecutableLocator(cliLocator), new ProcessStarter()),
    planRuntime.Plans,
    planRuntime.Transactions,
    planRuntime.Simulations,
    planRuntime.Releases,
    planRuntime.KiCadSimulationNetlists,
    planRuntime.DesignIntent);

return await app.RunAsync(args);

public sealed class CliApp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly KiCadDoctorService _doctor;
    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly BoardSummaryService _boardSummary;
    private readonly GeometryService _geometry;
    private readonly GeometryWorkflowService _geometryWorkflow;
    private readonly ComponentValueWorkflowService _componentWorkflow;
    private readonly RoutingWorkflowService _routingWorkflow;
    private readonly AutoroutingService _autorouting;
    private readonly FreeRoutingSetupService _freeRoutingSetup;
    private readonly SchematicAuthoringWorkflowService _schematicWorkflow;
    private readonly TestSpecService _testSpecService;
    private readonly ChangeReviewService _changeReview;
    private readonly BoardInspectionService _boardInspection;
    private readonly CheckSummaryService _checkSummary;
    private readonly GuiReviewService _guiReview;
    private readonly CheckRunner _checkRunner;
    private readonly ExportService _exportService;
    private readonly PackageService _packageService;
    private readonly AssemblyService _assemblyService;
    private readonly OpenKiCadService _openKiCad;
    private readonly DesignPlanService _designPlans;
    private readonly ProjectTransactionService _transactions;
    private readonly SimulationService _simulations;
    private readonly PcbWayReleaseService _releases;
    private readonly KiCadSimulationNetlistService _kicadSimulationNetlists;
    private readonly DesignIntentService _designIntent;

    public CliApp(
        KiCadDoctorService doctor,
        ProjectDiscoveryService projectDiscovery,
        BoardSummaryService boardSummary,
        GeometryService geometry,
        GeometryWorkflowService geometryWorkflow,
        ComponentValueWorkflowService componentWorkflow,
        RoutingWorkflowService routingWorkflow,
        AutoroutingService autorouting,
        FreeRoutingSetupService freeRoutingSetup,
        SchematicAuthoringWorkflowService schematicWorkflow,
        TestSpecService testSpecService,
        ChangeReviewService changeReview,
        BoardInspectionService boardInspection,
        CheckSummaryService checkSummary,
        GuiReviewService guiReview,
        CheckRunner checkRunner,
        ExportService exportService,
        PackageService packageService,
        AssemblyService assemblyService,
        OpenKiCadService openKiCad,
        DesignPlanService designPlans,
        ProjectTransactionService transactions,
        SimulationService simulations,
        PcbWayReleaseService releases,
        KiCadSimulationNetlistService kicadSimulationNetlists,
        DesignIntentService designIntent)
    {
        _doctor = doctor;
        _projectDiscovery = projectDiscovery;
        _boardSummary = boardSummary;
        _geometry = geometry;
        _geometryWorkflow = geometryWorkflow;
        _componentWorkflow = componentWorkflow;
        _routingWorkflow = routingWorkflow;
        _autorouting = autorouting;
        _freeRoutingSetup = freeRoutingSetup;
        _schematicWorkflow = schematicWorkflow;
        _testSpecService = testSpecService;
        _changeReview = changeReview;
        _boardInspection = boardInspection;
        _checkSummary = checkSummary;
        _guiReview = guiReview;
        _checkRunner = checkRunner;
        _exportService = exportService;
        _packageService = packageService;
        _assemblyService = assemblyService;
        _openKiCad = openKiCad;
        _designPlans = designPlans;
        _transactions = transactions;
        _simulations = simulations;
        _releases = releases;
        _kicadSimulationNetlists = kicadSimulationNetlists;
        _designIntent = designIntent;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var positional = args.Where(static arg => !arg.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray();

        return positional[0] switch
        {
            "doctor" => await RunDoctorAsync(json, cancellationToken),
            "summary" => RunSummary(positional, json),
            "board-summary" => RunBoardSummary(positional, json),
            "measure" => RunMeasure(positional, json),
            "move" => await RunMoveAsync(positional, json, cancellationToken),
            "set-spacing" => await RunSetSpacingAsync(positional, json, cancellationToken),
            "restore-change" => await RunRestoreChangeAsync(positional, json, cancellationToken),
            "list-changes" => RunListChanges(positional, json),
            "show-change" => RunShowChange(positional, json),
            "list-components" => RunListComponents(positional, json),
            "get-value" => RunGetValue(positional, json),
            "set-value" => await RunSetValueAsync(positional, json, cancellationToken),
            "list-nets" => RunListNets(positional, json),
            "get-net" => RunGetNet(positional, json),
            "list-footprint-pads" => RunListFootprintPads(positional, json),
            "list-tracks" => RunListTracks(positional, json),
            "list-vias" => RunListVias(positional, json),
            "get-net-routing" => RunGetNetRouting(positional, json),
            "list-unrouted-connections" => RunListUnroutedConnections(positional, json),
            "validate-track-clearance" => RunValidateTrackClearance(positional, json),
            "add-track" => await RunAddTrackAsync(positional, json, cancellationToken),
            "add-track-polyline" => await RunAddTrackPolylineAsync(positional, json, cancellationToken),
            "delete-track" => await RunDeleteTrackAsync(positional, json, cancellationToken),
            "add-via" => await RunAddViaAsync(positional, json, cancellationToken),
            "delete-via" => await RunDeleteViaAsync(positional, json, cancellationToken),
            "setup-freerouting" => await RunSetupFreeRoutingAsync(positional, json, cancellationToken),
            "autoroute-board" => await RunAutorouteBoardAsync(positional, json, cancellationToken),
            "list-schematic-symbols" => RunListSchematicSymbols(positional, json),
            "create-schematic-symbol" => await RunCreateSchematicSymbolAsync(positional, json, cancellationToken),
            "set-symbol-field" => await RunSetSymbolFieldAsync(positional, json, cancellationToken),
            "connect-schematic-pins" => await RunConnectSchematicPinsAsync(positional, json, cancellationToken),
            "add-net-label" => await RunAddNetLabelAsync(positional, json, cancellationToken),
            "delete-net-label-by-uuid" => await RunDeleteNetLabelByUuidAsync(positional, json, cancellationToken),
            "delete-net-label" => await RunDeleteNetLabelAsync(positional, json, cancellationToken),
            "delete-schematic-wire-by-uuid" => await RunDeleteSchematicWireByUuidAsync(positional, json, cancellationToken),
            "delete-schematic-wire" => await RunDeleteSchematicWireAsync(positional, json, cancellationToken),
            "update-pcb-from-schematic" => await RunUpdatePcbFromSchematicAsync(positional, json, cancellationToken),
            "regenerate-board-footprint" => await RunRegenerateBoardFootprintAsync(positional, json, cancellationToken),
            "list-tests" => RunListTests(positional, json),
            "validate-tests" => RunValidateTests(positional, json),
            "evaluate-test-results" => RunEvaluateTestResults(positional, json),
            "simulation" => await RunSimulationAsync(positional, json, cancellationToken),
            "intent" => RunIntent(positional, json),
            "check" => await RunCheckAsync(positional, json, cancellationToken),
            "check-summary" => await RunCheckSummaryAsync(positional, json, cancellationToken),
            "export" => await RunExportAsync(positional, json, cancellationToken),
            "export-bom" => await RunExportBomAsync(positional, json, cancellationToken),
            "export-position-files" => await RunExportPositionFilesAsync(positional, json, cancellationToken),
            "package" => await RunPackageAsync(positional, json, cancellationToken),
            "export-assembly-bom" => await RunExportAssemblyBomAsync(positional, json, cancellationToken),
            "export-cpl" => await RunExportCplAsync(positional, json, cancellationToken),
            "validate-assembly-package" => RunValidateAssemblyPackage(positional, json),
            "package-assembly" => await RunPackageAssemblyAsync(positional, json, cancellationToken),
            "generate-pcbway-release" => await RunPcbWayReleaseAsync(positional, json, cancellationToken),
            "validate-release-requirements" => RunReleaseRequirements(positional, json),
            "export-kicad-spice-netlist" => await RunKiCadSpiceNetlistAsync(positional, json, cancellationToken),
            "open" => RunOpen(positional, json),
            "kicad-gui-status" => await RunGuiStatusAsync(positional, json, cancellationToken),
            "refresh-gui" => await RunRefreshGuiAsync(positional, json, cancellationToken),
            "focus-component" => await RunFocusComponentAsync(positional, json, cancellationToken),
            "plan" => await RunPlanAsync(positional, json, cancellationToken),
            "transaction" => await RunTransactionAsync(positional, json, cancellationToken),
            _ => UnknownCommand(positional[0])
        };
    }

    private async Task<int> RunPcbWayReleaseAsync(IReadOnlyList<string> args,bool json,CancellationToken cancellationToken)
    { if(args.Count<2){Write(ToolResponse<object>.Fail("generate-pcbway-release requires <project-path>.","PROJECT_PATH_REQUIRED"),json);return 2;}var result=await _releases.GenerateAsync(args[1],cancellationToken);Write(result,json);return result.Success?0:1; }
    private int RunReleaseRequirements(IReadOnlyList<string> args,bool json)
    { if(args.Count<2){Write(ToolResponse<object>.Fail("validate-release-requirements requires <project-path>.","PROJECT_PATH_REQUIRED"),json);return 2;}var result=_releases.ValidateRequirements(args[1]);Write(result,json);return result.Data?.Passed==true?0:1; }
    private int RunIntent(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 3 || args[1] is not ("validate" or "analyze" or "report"))
        { Write(ToolResponse<object>.Fail("Usage: pcbhelper intent validate|analyze <project-path> OR pcbhelper intent report <project-path> --run <id>", "DESIGN_INTENT_ARGS_REQUIRED"), json); return 2; }
        if (args[1] == "validate") { var result = _designIntent.Validate(args[2]); Write(result, json); return result.Data?.Valid == true ? 0 : 1; }
        if (args[1] == "analyze") { var result = _designIntent.Analyze(args[2]); Write(result, json); return result.Data?.Passed == true ? 0 : 1; }
        var run = GetOption(args, "--run");
        if (run is null) { Write(ToolResponse<object>.Fail("intent report requires --run <id>.", "DESIGN_INTENT_RUN_REQUIRED"), json); return 2; }
        var report = _designIntent.GetReport(args[2], run); Write(report, json); return report.Success ? 0 : 1;
    }
    private async Task<int> RunKiCadSpiceNetlistAsync(IReadOnlyList<string> args,bool json,CancellationToken cancellationToken)
    { if(args.Count<2){Write(ToolResponse<object>.Fail("export-kicad-spice-netlist requires <project-path>.","PROJECT_PATH_REQUIRED"),json);return 2;}var result=await _kicadSimulationNetlists.ExportAsync(args[1],cancellationToken);Write(result,json);return result.Success?0:1; }

    private async Task<int> RunSimulationAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2 || args[1] is not ("status" or "validate" or "run" or "report"))
        {
            Write(ToolResponse<object>.Fail("Usage: pcbhelper simulation status|validate|run|report [<project>]", "SIMULATION_ARGS_REQUIRED"), json);
            return 2;
        }
        if (args[1] == "status")
        {
            Write(ToolResponse<SimulationCapabilities>.Ok("Simulation capability status.", _simulations.GetCapabilities()), json);
            return _simulations.GetCapabilities().Available ? 0 : 1;
        }
        if (args.Count < 3)
        {
            Write(ToolResponse<object>.Fail("Simulation command requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }
        if (args[1] == "validate")
        {
            var result = _simulations.Validate(args[2], GetOption(args, "--test")); Write(result, json); return result.Success ? 0 : 1;
        }
        if (args[1] == "run")
        {
            var result = await _simulations.RunAsync(args[2], GetOption(args, "--test"), cancellationToken); Write(result, json);
            return result.Success && result.Data?.Passed == true ? 0 : 1;
        }
        var run = GetOption(args, "--run");
        if (run is null) { Write(ToolResponse<object>.Fail("simulation report requires --run.", "SIMULATION_RUN_REQUIRED"), json); return 2; }
        var report = _simulations.GetReport(args[2], run); Write(report, json); return report.Success ? 0 : 1;
    }

    private async Task<int> RunPlanAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 3 || args[1] is not ("validate" or "preview" or "apply"))
        {
            Write(ToolResponse<object>.Fail("Usage: pcbhelper plan validate|preview|apply <project> --file <plan.json>", "PLAN_ARGS_REQUIRED"), json);
            return 2;
        }

        var file = GetOption(args, "--file");
        if (file is null || !File.Exists(file))
        {
            Write(ToolResponse<object>.Fail("A readable --file <plan.json> is required.", "PLAN_FILE_NOT_FOUND"), json);
            return 2;
        }

        var plan = await File.ReadAllTextAsync(file, cancellationToken);
        if (args[1] == "validate")
        {
            var result = _designPlans.Validate(args[2], plan); Write(result, json); return result.Success ? 0 : 1;
        }
        if (args[1] == "preview")
        {
            var result = _designPlans.Preview(args[2], plan); Write(result, json); return result.Success ? 0 : 1;
        }

        var expectedHash = GetOption(args, "--expected-hash");
        if (expectedHash is null)
        {
            Write(ToolResponse<object>.Fail("plan apply requires --expected-hash from preview.", "PLAN_HASH_REQUIRED"), json);
            return 2;
        }
        var decisions = GetOption(args, "--acknowledged-decisions")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var applied = await _designPlans.ApplyAsync(args[2], plan, expectedHash, decisions, cancellationToken);
        Write(applied, json); return applied.Success ? 0 : 1;
    }

    private async Task<int> RunTransactionAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 3 || args[1] is not ("show" or "restore"))
        {
            Write(ToolResponse<object>.Fail("Usage: pcbhelper transaction show|restore <project> --id <transaction-id>", "TRANSACTION_ARGS_REQUIRED"), json);
            return 2;
        }
        var id = GetOption(args, "--id");
        if (id is null)
        {
            Write(ToolResponse<object>.Fail("transaction requires --id.", "TRANSACTION_ID_REQUIRED"), json);
            return 2;
        }
        if (args[1] == "show")
        {
            var result = _transactions.Get(args[2], id); Write(result, json); return result.Success ? 0 : 1;
        }
        var restored = await _transactions.RestoreAsync(args[2], id, cancellationToken);
        Write(restored, json); return restored.Success ? 0 : 1;
    }

    private async Task<int> RunDoctorAsync(bool json, CancellationToken cancellationToken)
    {
        var result = await _doctor.RunAsync(cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunMeasure(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<MeasurementResult>.Fail("measure requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var from = GetOption(args, "--from");
        var to = GetOption(args, "--to");
        if (from is null || to is null)
        {
            Write(ToolResponse<MeasurementResult>.Fail("measure requires --from and --to.", "MEASURE_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = _geometry.MeasureDistance(args[1], from, to);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunMoveAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ComponentMutationResult>.Fail("move requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var reference = GetOption(args, "--ref");
        if (reference is null
            || !TryGetDoubleOption(args, "--x", out var x)
            || !TryGetDoubleOption(args, "--y", out var y))
        {
            Write(ToolResponse<ComponentMutationResult>.Fail("move requires --ref, --x, and --y.", "MOVE_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _geometryWorkflow.MoveComponentAsync(args[1], reference, x, y, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunSetSpacingAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ComponentSpacingMutationResult>.Fail("set-spacing requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var fixedReference = GetOption(args, "--fixed");
        var movingReference = GetOption(args, "--moving");
        if (fixedReference is null
            || movingReference is null
            || !TryGetDoubleOption(args, "--distance", out var distance))
        {
            Write(ToolResponse<ComponentSpacingMutationResult>.Fail("set-spacing requires --fixed, --moving, and --distance.", "SPACING_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _geometryWorkflow.SetComponentSpacingAsync(
            args[1],
            fixedReference,
            movingReference,
            distance,
            GetOption(args, "--axis"),
            HasFlag(args, "--dry-run"),
            cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunRestoreChangeAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ComponentRestoreResult>.Fail("restore-change requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var change = GetOption(args, "--change");
        if (change is null)
        {
            Write(ToolResponse<ComponentRestoreResult>.Fail("restore-change requires --change.", "CHANGE_REQUIRED"), json);
            return 2;
        }

        var result = await _changeReview.RestoreChangeAsync(args[1], change, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunListChanges(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ChangeListResult>.Fail("list-changes requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _changeReview.ListChanges(args[1]);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunShowChange(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ChangeReport>.Fail("show-change requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var change = GetOption(args, "--change");
        if (change is null)
        {
            Write(ToolResponse<ChangeReport>.Fail("show-change requires --change.", "CHANGE_REQUIRED"), json);
            return 2;
        }

        var result = _changeReview.GetChange(args[1], change);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunListComponents(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ComponentListResult>.Fail("list-components requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _componentWorkflow.ListComponents(args[1]);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunGetValue(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ComponentValueResult>.Fail("get-value requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var reference = GetOption(args, "--ref");
        if (reference is null)
        {
            Write(ToolResponse<ComponentValueResult>.Fail("get-value requires --ref.", "REFERENCE_REQUIRED"), json);
            return 2;
        }

        var result = _componentWorkflow.GetValue(args[1], reference);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunSetValueAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ComponentValueMutationResult>.Fail("set-value requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var reference = GetOption(args, "--ref");
        var value = GetOption(args, "--value");
        if (reference is null || value is null)
        {
            Write(ToolResponse<ComponentValueMutationResult>.Fail("set-value requires --ref and --value.", "VALUE_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _componentWorkflow.SetValueAsync(args[1], reference, value, GetOption(args, "--scope"), HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunListNets(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<NetListResult>.Fail("list-nets requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _boardInspection.ListNets(args[1]);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunGetNet(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<NetSummary>.Fail("get-net requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var net = GetOption(args, "--net");
        if (net is null)
        {
            Write(ToolResponse<NetSummary>.Fail("get-net requires --net.", "NET_REQUIRED"), json);
            return 2;
        }

        var result = _boardInspection.GetNet(args[1], net);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunListFootprintPads(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<FootprintPadsResult>.Fail("list-footprint-pads requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var reference = GetOption(args, "--ref");
        if (reference is null)
        {
            Write(ToolResponse<FootprintPadsResult>.Fail("list-footprint-pads requires --ref.", "REFERENCE_REQUIRED"), json);
            return 2;
        }

        var result = _boardInspection.ListFootprintPads(args[1], reference);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunListTracks(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<TrackListResult>.Fail("list-tracks requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _routingWorkflow.ListTracks(args[1], GetOption(args, "--net"));
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunListVias(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ViaListResult>.Fail("list-vias requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _routingWorkflow.ListVias(args[1], GetOption(args, "--net"));
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunGetNetRouting(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<NetRoutingResult>.Fail("get-net-routing requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var net = GetOption(args, "--net");
        if (net is null)
        {
            Write(ToolResponse<NetRoutingResult>.Fail("get-net-routing requires --net.", "NET_REQUIRED"), json);
            return 2;
        }

        var result = _routingWorkflow.GetNetRouting(args[1], net);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunListUnroutedConnections(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<UnroutedConnectionListResult>.Fail("list-unrouted-connections requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _routingWorkflow.ListUnroutedConnections(args[1], GetOption(args, "--net"));
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunValidateTrackClearance(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<RoutingClearanceValidationResult>.Fail("validate-track-clearance requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var net = GetOption(args, "--net");
        var points = GetOption(args, "--points");
        var layer = GetOption(args, "--layer");
        if (net is null || points is null || layer is null || !TryGetDoubleOption(args, "--width", out var width))
        {
            Write(ToolResponse<RoutingClearanceValidationResult>.Fail("validate-track-clearance requires --net, --points, --layer, and --width.", "ROUTING_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = _routingWorkflow.ValidateTrackClearance(args[1], net, points, layer, width);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunAddTrackAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("add-track requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var net = GetOption(args, "--net");
        var layer = GetOption(args, "--layer");
        if (net is null || layer is null
            || !TryGetDoubleOption(args, "--start-x", out var startX)
            || !TryGetDoubleOption(args, "--start-y", out var startY)
            || !TryGetDoubleOption(args, "--end-x", out var endX)
            || !TryGetDoubleOption(args, "--end-y", out var endY)
            || !TryGetDoubleOption(args, "--width", out var width))
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("add-track requires --net, --start-x, --start-y, --end-x, --end-y, --layer, and --width.", "ROUTING_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _routingWorkflow.AddTrackAsync(args[1], net, startX, startY, endX, endY, layer, width, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunAddTrackPolylineAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("add-track-polyline requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var net = GetOption(args, "--net");
        var points = GetOption(args, "--points");
        var layer = GetOption(args, "--layer");
        if (net is null || points is null || layer is null || !TryGetDoubleOption(args, "--width", out var width))
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("add-track-polyline requires --net, --points, --layer, and --width.", "ROUTING_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _routingWorkflow.AddTrackPolylineAsync(args[1], net, points, layer, width, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunDeleteTrackAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("delete-track requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var track = GetOption(args, "--track");
        if (track is null)
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("delete-track requires --track.", "ROUTING_ITEM_REQUIRED"), json);
            return 2;
        }

        var result = await _routingWorkflow.DeleteTrackAsync(args[1], track, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunAddViaAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("add-via requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var net = GetOption(args, "--net");
        var layers = GetOption(args, "--layers");
        if (net is null || layers is null
            || !TryGetDoubleOption(args, "--x", out var x)
            || !TryGetDoubleOption(args, "--y", out var y)
            || !TryGetDoubleOption(args, "--size", out var size)
            || !TryGetDoubleOption(args, "--drill", out var drill))
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("add-via requires --net, --x, --y, --size, --drill, and --layers.", "ROUTING_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _routingWorkflow.AddViaAsync(args[1], net, x, y, size, drill, layers, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunDeleteViaAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("delete-via requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var via = GetOption(args, "--via");
        if (via is null)
        {
            Write(ToolResponse<RoutingMutationResult>.Fail("delete-via requires --via.", "ROUTING_ITEM_REQUIRED"), json);
            return 2;
        }

        var result = await _routingWorkflow.DeleteViaAsync(args[1], via, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunSetupFreeRoutingAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        var result = await _freeRoutingSetup.SetupAsync(HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunAutorouteBoardAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<AutorouteBoardResult>.Fail("autoroute-board requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _autorouting.AutorouteBoardAsync(args[1], HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunListSchematicSymbols(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicSymbolListResult>.Fail("list-schematic-symbols requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _schematicWorkflow.ListSymbols(args[1]);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunCreateSchematicSymbolAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("create-schematic-symbol requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var symbol = GetOption(args, "--symbol");
        var reference = GetOption(args, "--ref");
        if (symbol is null || reference is null || !TryGetDoubleOption(args, "--x", out var x) || !TryGetDoubleOption(args, "--y", out var y))
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("create-schematic-symbol requires --symbol, --ref, --x, and --y.", "SCHEMATIC_ARGS_REQUIRED"), json);
            return 2;
        }

        var unit = TryGetIntOption(args, "--unit", out var parsedUnit) ? parsedUnit : 1;
        var result = await _schematicWorkflow.CreateSymbolAsync(args[1], symbol, reference, x, y, GetOption(args, "--value"), GetOption(args, "--footprint"), unit, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunSetSymbolFieldAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("set-symbol-field requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var reference = GetOption(args, "--ref");
        var field = GetOption(args, "--field");
        var value = GetOption(args, "--value");
        if (reference is null || field is null || value is null)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("set-symbol-field requires --ref, --field, and --value.", "SCHEMATIC_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _schematicWorkflow.SetSymbolFieldAsync(args[1], reference, field, value, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunConnectSchematicPinsAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("connect-schematic-pins requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var from = GetOption(args, "--from");
        var to = GetOption(args, "--to");
        if (from is null || to is null)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("connect-schematic-pins requires --from and --to.", "SCHEMATIC_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _schematicWorkflow.ConnectPinsAsync(args[1], from, to, GetOption(args, "--net"), HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunAddNetLabelAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("add-net-label requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var net = GetOption(args, "--net");
        if (net is null || !TryGetDoubleOption(args, "--x", out var x) || !TryGetDoubleOption(args, "--y", out var y))
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("add-net-label requires --net, --x, and --y.", "SCHEMATIC_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _schematicWorkflow.AddNetLabelAsync(args[1], net, x, y, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunDeleteNetLabelByUuidAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("delete-net-label-by-uuid requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var uuid = GetOption(args, "--uuid");
        if (uuid is null)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("delete-net-label-by-uuid requires --uuid.", "SCHEMATIC_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _schematicWorkflow.DeleteNetLabelByUuidAsync(args[1], uuid, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunDeleteNetLabelAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("delete-net-label requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var net = GetOption(args, "--net");
        if (net is null || !TryGetDoubleOption(args, "--x", out var x) || !TryGetDoubleOption(args, "--y", out var y))
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("delete-net-label requires --net, --x, and --y.", "SCHEMATIC_ARGS_REQUIRED"), json);
            return 2;
        }

        var tolerance = TryGetDoubleOption(args, "--tolerance", out var parsedTolerance) ? parsedTolerance : (double?)null;
        var result = await _schematicWorkflow.DeleteNetLabelAsync(args[1], net, x, y, tolerance, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunDeleteSchematicWireByUuidAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("delete-schematic-wire-by-uuid requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var uuid = GetOption(args, "--uuid");
        if (uuid is null)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("delete-schematic-wire-by-uuid requires --uuid.", "SCHEMATIC_ARGS_REQUIRED"), json);
            return 2;
        }

        var result = await _schematicWorkflow.DeleteSchematicWireByUuidAsync(args[1], uuid, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunDeleteSchematicWireAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("delete-schematic-wire requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        if (!TryGetDoubleOption(args, "--x1", out var x1)
            || !TryGetDoubleOption(args, "--y1", out var y1)
            || !TryGetDoubleOption(args, "--x2", out var x2)
            || !TryGetDoubleOption(args, "--y2", out var y2))
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("delete-schematic-wire requires --x1, --y1, --x2, and --y2.", "SCHEMATIC_ARGS_REQUIRED"), json);
            return 2;
        }

        var tolerance = TryGetDoubleOption(args, "--tolerance", out var parsedTolerance) ? parsedTolerance : (double?)null;
        var result = await _schematicWorkflow.DeleteSchematicWireAsync(args[1], x1, y1, x2, y2, tolerance, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunUpdatePcbFromSchematicAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("update-pcb-from-schematic requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _schematicWorkflow.UpdatePcbFromSchematicAsync(args[1], HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunRegenerateBoardFootprintAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("regenerate-board-footprint requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var reference = GetOption(args, "--ref");
        if (reference is null)
        {
            Write(ToolResponse<SchematicMutationResult>.Fail("regenerate-board-footprint requires --ref.", "REFERENCE_REQUIRED"), json);
            return 2;
        }

        var result = await _schematicWorkflow.RegenerateBoardFootprintAsync(args[1], reference, HasFlag(args, "--dry-run"), cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunListTests(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<TestSpecListResult>.Fail("list-tests requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _testSpecService.ListTests(args[1]);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunValidateTests(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<TestSpecValidationResult>.Fail("validate-tests requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _testSpecService.ValidateTests(args[1]);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunEvaluateTestResults(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<TestEvaluationResult>.Fail("evaluate-test-results requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var results = GetOption(args, "--results");
        if (results is null)
        {
            Write(ToolResponse<TestEvaluationResult>.Fail("evaluate-test-results requires --results.", "TEST_RESULTS_REQUIRED"), json);
            return 2;
        }

        var result = _testSpecService.EvaluateResults(args[1], results);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunSummary(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ProjectSummary>.Fail("summary requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _projectDiscovery.GetSummary(args[1]);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunBoardSummary(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<BoardSummary>.Fail("board-summary requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _boardSummary.GetSummary(args[1]);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunCheckAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<CheckRunResult>.Fail("check requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _checkRunner.RunChecksAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunCheckSummaryAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<CheckSummaryResult>.Fail("check-summary requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _checkSummary.RunAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunExportAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ManufacturingExportResult>.Fail("export requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _exportService.ExportManufacturingFilesAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunExportBomAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SingleExportResult>.Fail("export-bom requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _exportService.ExportBomAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunExportPositionFilesAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<SingleExportResult>.Fail("export-position-files requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _exportService.ExportPositionFilesAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunPackageAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<ManufacturingPackageResult>.Fail("package requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _packageService.CreateManufacturingZipAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunExportAssemblyBomAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<AssemblyExportResult>.Fail("export-assembly-bom requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _assemblyService.ExportAssemblyBomAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunExportCplAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<AssemblyExportResult>.Fail("export-cpl requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _assemblyService.ExportCplAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunValidateAssemblyPackage(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<AssemblyValidationResult>.Fail("validate-assembly-package requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _assemblyService.ValidateAssemblyPackage(args[1]);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunPackageAssemblyAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<AssemblyPackageResult>.Fail("package-assembly requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _assemblyService.CreatePcbWayAssemblyPackageAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private int RunOpen(IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<OpenProjectResult>.Fail("open requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = _openKiCad.OpenProject(args[1], HasFlag(args, "--dry-run"));
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunGuiStatusAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<KiCadGuiCapabilities>.Fail("kicad-gui-status requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _guiReview.GetCapabilitiesAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunRefreshGuiAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<KiCadGuiActionResult>.Fail("refresh-gui requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var result = await _guiReview.RefreshProjectAsync(args[1], cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private async Task<int> RunFocusComponentAsync(IReadOnlyList<string> args, bool json, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            Write(ToolResponse<KiCadGuiActionResult>.Fail("focus-component requires <project-path>.", "PROJECT_PATH_REQUIRED"), json);
            return 2;
        }

        var reference = GetOption(args, "--ref");
        if (reference is null)
        {
            Write(ToolResponse<KiCadGuiActionResult>.Fail("focus-component requires --ref.", "REFERENCE_REQUIRED"), json);
            return 2;
        }

        var result = await _guiReview.FocusComponentAsync(args[1], reference, cancellationToken);
        Write(result, json);
        return result.Success ? 0 : 1;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 2;
    }

    private static string? GetOption(IReadOnlyList<string> args, string optionName)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool TryGetDoubleOption(IReadOnlyList<string> args, string optionName, out double value)
    {
        var raw = GetOption(args, optionName);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetIntOption(IReadOnlyList<string> args, string optionName, out int value)
    {
        var raw = GetOption(args, optionName);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flagName)
    {
        return args.Any(arg => arg.Equals(flagName, StringComparison.OrdinalIgnoreCase));
    }

    private static void Write<T>(ToolResponse<T> response, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            return;
        }

        var stream = response.Success ? Console.Out : Console.Error;
        stream.WriteLine(response.Summary);
        foreach (var warning in response.Warnings)
        {
            stream.WriteLine($"warning: {warning}");
        }

        if (response.Error is not null)
        {
            stream.WriteLine($"{response.Error.Code}: {response.Error.Message}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("PCBHelper");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  pcbhelper doctor [--json]");
        Console.WriteLine("  pcbhelper summary <project-path> [--json]");
        Console.WriteLine("  pcbhelper board-summary <project-path> [--json]");
        Console.WriteLine("  pcbhelper measure <project-path> --from <ref> --to <ref> [--json]");
        Console.WriteLine("  pcbhelper move <project-path> --ref <ref> --x <mm> --y <mm> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper set-spacing <project-path> --fixed <ref> --moving <ref> --distance <mm> [--axis x|y] [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper plan validate|preview <project-path> --file <plan.json> [--json]");
        Console.WriteLine("  pcbhelper plan apply <project-path> --file <plan.json> --expected-hash <hash> [--acknowledged-decisions <ids>] [--json]");
        Console.WriteLine("  pcbhelper transaction show|restore <project-path> --id <transaction-id> [--json]");
        Console.WriteLine("  pcbhelper restore-change <project-path> --change <change-id-or-path> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper list-changes <project-path> [--json]");
        Console.WriteLine("  pcbhelper show-change <project-path> --change <change-id-or-path> [--json]");
        Console.WriteLine("  pcbhelper list-components <project-path> [--json]");
        Console.WriteLine("  pcbhelper get-value <project-path> --ref <ref> [--json]");
        Console.WriteLine("  pcbhelper set-value <project-path> --ref <ref> --value <value> [--scope available|schematic|board|both] [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper list-nets <project-path> [--json]");
        Console.WriteLine("  pcbhelper get-net <project-path> --net <name-or-code> [--json]");
        Console.WriteLine("  pcbhelper list-footprint-pads <project-path> --ref <ref> [--json]");
        Console.WriteLine("  pcbhelper list-tracks <project-path> [--net <name-or-code>] [--json]");
        Console.WriteLine("  pcbhelper list-vias <project-path> [--net <name-or-code>] [--json]");
        Console.WriteLine("  pcbhelper get-net-routing <project-path> --net <name-or-code> [--json]");
        Console.WriteLine("  pcbhelper list-unrouted-connections <project-path> [--net <name-or-code>] [--json]");
        Console.WriteLine("  pcbhelper validate-track-clearance <project-path> --net <name-or-code> --points \"x1,y1;x2,y2;...\" --layer F.Cu|B.Cu --width <mm> [--json]");
        Console.WriteLine("  pcbhelper add-track <project-path> --net <name-or-code> --start-x <mm> --start-y <mm> --end-x <mm> --end-y <mm> --layer F.Cu|B.Cu --width <mm> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper add-track-polyline <project-path> --net <name-or-code> --points \"x1,y1;x2,y2;...\" --layer F.Cu|B.Cu --width <mm> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper delete-track <project-path> --track <uuid-or-id> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper add-via <project-path> --net <name-or-code> --x <mm> --y <mm> --size <mm> --drill <mm> --layers F.Cu,B.Cu [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper delete-via <project-path> --via <uuid-or-id> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper setup-freerouting [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper autoroute-board <project-path> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper list-schematic-symbols <project-path> [--json]");
        Console.WriteLine("  pcbhelper create-schematic-symbol <project-path> --symbol <catalog-id> --ref <ref> --x <mm> --y <mm> [--unit <n>] [--value <value>] [--footprint <id>] [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper set-symbol-field <project-path> --ref <ref> --field <name> --value <value> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper connect-schematic-pins <project-path> --from <ref.pin|ref:pin> --to <ref.pin|ref:pin> [--net <name>] [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper add-net-label <project-path> --net <name> --x <mm> --y <mm> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper delete-net-label-by-uuid <project-path> --uuid <uuid> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper delete-net-label <project-path> --net <name> --x <mm> --y <mm> [--tolerance <mm>] [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper delete-schematic-wire-by-uuid <project-path> --uuid <uuid> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper delete-schematic-wire <project-path> --x1 <mm> --y1 <mm> --x2 <mm> --y2 <mm> [--tolerance <mm>] [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper update-pcb-from-schematic <project-path> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper regenerate-board-footprint <project-path> --ref <ref> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper list-tests <project-path> [--json]");
        Console.WriteLine("  pcbhelper validate-tests <project-path> [--json]");
        Console.WriteLine("  pcbhelper evaluate-test-results <project-path> --results <path> [--json]");
        Console.WriteLine("  pcbhelper simulation status|validate|run|report [<project-path>] [--test <id>] [--run <id>] [--json]");
        Console.WriteLine("  pcbhelper intent validate|analyze <project-path> [--json]");
        Console.WriteLine("  pcbhelper intent report <project-path> --run <id> [--json]");
        Console.WriteLine("  pcbhelper check <project-path> [--json]");
        Console.WriteLine("  pcbhelper check-summary <project-path> [--json]");
        Console.WriteLine("  pcbhelper export <project-path> [--json]");
        Console.WriteLine("  pcbhelper export-bom <project-path> [--json]");
        Console.WriteLine("  pcbhelper export-position-files <project-path> [--json]");
        Console.WriteLine("  pcbhelper package <project-path> [--json]");
        Console.WriteLine("  pcbhelper export-assembly-bom <project-path> [--json]");
        Console.WriteLine("  pcbhelper export-cpl <project-path> [--json]");
        Console.WriteLine("  pcbhelper validate-assembly-package <project-path> [--json]");
        Console.WriteLine("  pcbhelper package-assembly <project-path> [--json]");
        Console.WriteLine("  pcbhelper open <project-path> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper kicad-gui-status <project-path> [--json]");
        Console.WriteLine("  pcbhelper refresh-gui <project-path> [--json]");
        Console.WriteLine("  pcbhelper focus-component <project-path> --ref <ref> [--json]");
    }
}
