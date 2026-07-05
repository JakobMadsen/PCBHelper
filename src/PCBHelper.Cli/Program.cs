using System.Text.Json;
using PCBHelper.Core;

var app = new CliApp(
    new KiCadDoctorService(new KiCadCliLocator(), new ProcessCommandRunner()),
    new ProjectDiscoveryService(),
    new BoardSummaryService(new ProjectDiscoveryService()),
    new CheckRunner(new ProjectDiscoveryService(), new KiCadCliLocator(), new ProcessCommandRunner()),
    new ExportService(new ProjectDiscoveryService(), new KiCadCliLocator(), new ProcessCommandRunner()),
    new PackageService(
        new ProjectDiscoveryService(),
        new KiCadDoctorService(new KiCadCliLocator(), new ProcessCommandRunner()),
        new ExportService(new ProjectDiscoveryService(), new KiCadCliLocator(), new ProcessCommandRunner())));

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
    private readonly CheckRunner _checkRunner;
    private readonly ExportService _exportService;
    private readonly PackageService _packageService;

    public CliApp(
        KiCadDoctorService doctor,
        ProjectDiscoveryService projectDiscovery,
        BoardSummaryService boardSummary,
        CheckRunner checkRunner,
        ExportService exportService,
        PackageService packageService)
    {
        _doctor = doctor;
        _projectDiscovery = projectDiscovery;
        _boardSummary = boardSummary;
        _checkRunner = checkRunner;
        _exportService = exportService;
        _packageService = packageService;
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
            "check" => await RunCheckAsync(positional, json, cancellationToken),
            "export" => await RunExportAsync(positional, json, cancellationToken),
            "package" => await RunPackageAsync(positional, json, cancellationToken),
            _ => UnknownCommand(positional[0])
        };
    }

    private async Task<int> RunDoctorAsync(bool json, CancellationToken cancellationToken)
    {
        var result = await _doctor.RunAsync(cancellationToken);
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

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 2;
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
        Console.WriteLine("  pcbhelper check <project-path> [--json]");
        Console.WriteLine("  pcbhelper export <project-path> [--json]");
        Console.WriteLine("  pcbhelper package <project-path> [--json]");
    }
}
