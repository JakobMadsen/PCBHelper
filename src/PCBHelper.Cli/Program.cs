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

var app = new CliApp(
    doctor,
    projectDiscovery,
    new BoardSummaryService(projectDiscovery),
    geometry,
    new GeometryWorkflowService(geometry, checkRunner, new ChangeReportService(projectDiscovery)),
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
            "check" => await RunCheckAsync(positional, json, cancellationToken),
            "export" => await RunExportAsync(positional, json, cancellationToken),
            "package" => await RunPackageAsync(positional, json, cancellationToken),
            "open" => RunOpen(positional, json),
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

        var result = await _geometryWorkflow.RestoreChangeAsync(args[1], change, HasFlag(args, "--dry-run"), cancellationToken);
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
        Console.WriteLine("  pcbhelper check <project-path> [--json]");
        Console.WriteLine("  pcbhelper export <project-path> [--json]");
        Console.WriteLine("  pcbhelper package <project-path> [--json]");
        Console.WriteLine("  pcbhelper open <project-path> [--dry-run] [--json]");
    }
}
