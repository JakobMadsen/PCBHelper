using System.Text.Json;
using System.Globalization;
using PCBHelper.Core;

var projectDiscovery = new ProjectDiscoveryService();
var cliLocator = new KiCadCliLocator();
var runner = new ProcessCommandRunner();
var doctor = new KiCadDoctorService(cliLocator, runner);
var checkRunner = new CheckRunner(projectDiscovery, cliLocator, runner);
var exportService = new ExportService(projectDiscovery, cliLocator, runner);
var geometry = new GeometryService(projectDiscovery);
var changeReports = new ChangeReportService(projectDiscovery);
var geometryWorkflow = new GeometryWorkflowService(geometry, checkRunner, changeReports);
var componentService = new ComponentService(projectDiscovery);
var componentWorkflow = new ComponentValueWorkflowService(componentService, checkRunner, changeReports);

var app = new CliApp(
    doctor,
    projectDiscovery,
    new BoardSummaryService(projectDiscovery),
    geometry,
    geometryWorkflow,
    componentWorkflow,
    new ChangeReviewService(projectDiscovery, changeReports, geometryWorkflow, componentWorkflow),
    new BoardInspectionService(projectDiscovery),
    new CheckSummaryService(checkRunner),
    new GuiReviewService(cliLocator, new KiCadExecutableLocator(cliLocator), runner),
    checkRunner,
    exportService,
    new PackageService(
        projectDiscovery,
        doctor,
        exportService),
    new OpenKiCadService(projectDiscovery, new KiCadExecutableLocator(cliLocator), new ProcessStarter()));

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
    private readonly ChangeReviewService _changeReview;
    private readonly BoardInspectionService _boardInspection;
    private readonly CheckSummaryService _checkSummary;
    private readonly GuiReviewService _guiReview;
    private readonly CheckRunner _checkRunner;
    private readonly ExportService _exportService;
    private readonly PackageService _packageService;
    private readonly OpenKiCadService _openKiCad;

    public CliApp(
        KiCadDoctorService doctor,
        ProjectDiscoveryService projectDiscovery,
        BoardSummaryService boardSummary,
        GeometryService geometry,
        GeometryWorkflowService geometryWorkflow,
        ComponentValueWorkflowService componentWorkflow,
        ChangeReviewService changeReview,
        BoardInspectionService boardInspection,
        CheckSummaryService checkSummary,
        GuiReviewService guiReview,
        CheckRunner checkRunner,
        ExportService exportService,
        PackageService packageService,
        OpenKiCadService openKiCad)
    {
        _doctor = doctor;
        _projectDiscovery = projectDiscovery;
        _boardSummary = boardSummary;
        _geometry = geometry;
        _geometryWorkflow = geometryWorkflow;
        _componentWorkflow = componentWorkflow;
        _changeReview = changeReview;
        _boardInspection = boardInspection;
        _checkSummary = checkSummary;
        _guiReview = guiReview;
        _checkRunner = checkRunner;
        _exportService = exportService;
        _packageService = packageService;
        _openKiCad = openKiCad;
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
            "check" => await RunCheckAsync(positional, json, cancellationToken),
            "check-summary" => await RunCheckSummaryAsync(positional, json, cancellationToken),
            "export" => await RunExportAsync(positional, json, cancellationToken),
            "export-bom" => await RunExportBomAsync(positional, json, cancellationToken),
            "export-position-files" => await RunExportPositionFilesAsync(positional, json, cancellationToken),
            "package" => await RunPackageAsync(positional, json, cancellationToken),
            "open" => RunOpen(positional, json),
            "kicad-gui-status" => await RunGuiStatusAsync(positional, json, cancellationToken),
            "refresh-gui" => await RunRefreshGuiAsync(positional, json, cancellationToken),
            "focus-component" => await RunFocusComponentAsync(positional, json, cancellationToken),
            _ => UnknownCommand(positional[0])
        };
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
        Console.WriteLine("  pcbhelper restore-change <project-path> --change <change-id-or-path> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper list-changes <project-path> [--json]");
        Console.WriteLine("  pcbhelper show-change <project-path> --change <change-id-or-path> [--json]");
        Console.WriteLine("  pcbhelper list-components <project-path> [--json]");
        Console.WriteLine("  pcbhelper get-value <project-path> --ref <ref> [--json]");
        Console.WriteLine("  pcbhelper set-value <project-path> --ref <ref> --value <value> [--scope available|schematic|board|both] [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper list-nets <project-path> [--json]");
        Console.WriteLine("  pcbhelper get-net <project-path> --net <name-or-code> [--json]");
        Console.WriteLine("  pcbhelper list-footprint-pads <project-path> --ref <ref> [--json]");
        Console.WriteLine("  pcbhelper check <project-path> [--json]");
        Console.WriteLine("  pcbhelper check-summary <project-path> [--json]");
        Console.WriteLine("  pcbhelper export <project-path> [--json]");
        Console.WriteLine("  pcbhelper export-bom <project-path> [--json]");
        Console.WriteLine("  pcbhelper export-position-files <project-path> [--json]");
        Console.WriteLine("  pcbhelper package <project-path> [--json]");
        Console.WriteLine("  pcbhelper open <project-path> [--dry-run] [--json]");
        Console.WriteLine("  pcbhelper kicad-gui-status <project-path> [--json]");
        Console.WriteLine("  pcbhelper refresh-gui <project-path> [--json]");
        Console.WriteLine("  pcbhelper focus-component <project-path> --ref <ref> [--json]");
    }
}
