using System.Text.Json;

namespace PCBHelper.Core;

public sealed class PCBHelperRuntime
{
    private PCBHelperRuntime(ProjectScopePolicy scope)
    {
        Projects = new ProjectDiscoveryService(scope);
        var locator = new KiCadCliLocator();
        var runner = new ProcessCommandRunner();
        var ngspice = new NgspiceLocator();
        Doctor = new KiCadDoctorService(locator, runner, ngspice);
        Checks = new CheckRunner(Projects, locator, runner);
        CheckSummary = new CheckSummaryService(Checks);
        Exports = new ExportService(Projects, locator, runner);
        Assembly = new AssemblyService(Projects, Doctor, Exports);
        TestSpecs = new TestSpecService(Projects);
        Simulations = new SimulationService(Projects, TestSpecs, new NgspiceBackend(ngspice, runner));
        KiCadSimulationNetlists = new KiCadSimulationNetlistService(Projects, locator, runner);
        AgentGuidance = new AgentGuidanceService();
        Components = new ComponentService(Projects);
        BoardSummary = new BoardSummaryService(Projects);
        BoardInspection = new BoardInspectionService(Projects);
        BoardFinishing = new BoardFinishingService(Projects);
        Gui = new GuiReviewService(locator, new KiCadExecutableLocator(locator), runner);
        TransactionStore = new ProjectTransactionStore(Projects);
        Transactions = new ProjectTransactionService(Projects, TransactionStore, new AtomicProjectFileWriter(), () => DateTimeOffset.UtcNow);
        Gates = new EngineeringGateService(CheckSummary, Assembly, Simulations);
        Releases = new PcbWayReleaseService(Projects, Exports, Assembly, Gates);
        Plans = new DesignPlanService(Projects, Transactions, Gates);
        Workflows = new ProjectWorkflowService(Projects, BoardSummary, BoardInspection, Components, Gui, TransactionStore, Gates, Assembly, Simulations, locator, runner);
    }

    public static PCBHelperRuntime ForCli() => new(ProjectScopePolicy.Unrestricted());
    public static PCBHelperRuntime ForMcp() => new(ProjectScopePolicy.FromEnvironment());

    public ProjectDiscoveryService Projects { get; }
    public KiCadDoctorService Doctor { get; }
    public CheckRunner Checks { get; }
    public CheckSummaryService CheckSummary { get; }
    public ExportService Exports { get; }
    public AssemblyService Assembly { get; }
    public TestSpecService TestSpecs { get; }
    public SimulationService Simulations { get; }
    public KiCadSimulationNetlistService KiCadSimulationNetlists { get; }
    public AgentGuidanceService AgentGuidance { get; }
    public ComponentService Components { get; }
    public BoardSummaryService BoardSummary { get; }
    public BoardInspectionService BoardInspection { get; }
    public BoardFinishingService BoardFinishing { get; }
    public GuiReviewService Gui { get; }
    public ProjectTransactionStore TransactionStore { get; }
    public ProjectTransactionService Transactions { get; }
    public EngineeringGateService Gates { get; }
    public PcbWayReleaseService Releases { get; }
    public DesignPlanService Plans { get; }
    public ProjectWorkflowService Workflows { get; }
}

public sealed class ProjectWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ProjectDiscoveryService _projects;
    private readonly BoardSummaryService _boards;
    private readonly BoardInspectionService _inspection;
    private readonly ComponentService _components;
    private readonly GuiReviewService _gui;
    private readonly ProjectTransactionStore _transactions;
    private readonly EngineeringGateService _gates;
    private readonly AssemblyService _assembly;
    private readonly SimulationService _simulations;
    private readonly KiCadCliLocator _cliLocator;
    private readonly ICommandRunner _runner;

    public ProjectWorkflowService(ProjectDiscoveryService projects, BoardSummaryService boards, BoardInspectionService inspection,
        ComponentService components, GuiReviewService gui, ProjectTransactionStore transactions, EngineeringGateService gates, AssemblyService assembly, SimulationService simulations,
        KiCadCliLocator cliLocator, ICommandRunner runner)
    {
        _projects = projects; _boards = boards; _inspection = inspection; _components = components; _gui = gui;
        _transactions = transactions; _gates = gates; _assembly = assembly; _simulations = simulations; _cliLocator = cliLocator; _runner = runner;
    }

    public async Task<ToolResponse<ProjectContextResult>> GetProjectContextAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<ProjectContextResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        var board = _boards.GetSummary(projectPath);
        var components = _components.ListComponents(projectPath);
        var nets = _inspection.ListNets(projectPath);
        var gui = await _gui.GetCapabilitiesAsync(projectPath, cancellationToken);
        var latest = _transactions.List(project.Data.ProjectRoot).Data?.Transactions.OrderByDescending(static item => item.CreatedAtUtc).FirstOrDefault();
        return ToolResponse<ProjectContextResult>.Ok("Collected project context without running engineering checks.",
            new ProjectContextResult(project.Data, board.Data, components.Data, nets.Data, gui.Data, latest, _simulations.GetCapabilities(),
                new AgentContractReference(AgentGuidanceService.CapabilityVersion, AgentGuidanceService.GuideVersion,
                    AgentGuidanceService.GuideUri, AgentGuidanceService.DesignPlanSchemaUri)),
            project.Warnings.Concat(board.Warnings).Concat(components.Warnings).Concat(nets.Warnings).ToArray());
    }

    public async Task<ToolResponse<ReviewPackageResult>> GenerateReviewPackageAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var context = await GetProjectContextAsync(projectPath, cancellationToken);
        if (!context.Success || context.Data is null)
            return ToolResponse<ReviewPackageResult>.Fail(context.Summary, context.Error?.Code ?? "PROJECT_NOT_FOUND", context.Error?.Message);
        var validation = _assembly.ValidateAssemblyPackage(projectPath);
        var root = context.Data.Project.ProjectRoot;
        var directory = Path.Combine(root, ".pcbhelper", "review", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        Directory.CreateDirectory(directory);
        var renderFiles = new List<string>();
        var uncertainty = new List<string>();
        var cli = _cliLocator.Locate();
        if (cli.Found && cli.ExecutablePath is not null)
        {
            if (context.Data.Project.BoardFile is not null)
            {
                var boardRender = Path.Combine(directory, "board-top.png");
                var rendered = await _runner.RunAsync(cli.ExecutablePath,
                    new[] { "pcb", "render", "--output", boardRender, "--side", "top", context.Data.Project.BoardFile }, root, cancellationToken);
                if (rendered.ExitCode == 0 && File.Exists(boardRender)) renderFiles.Add(boardRender);
                else uncertainty.Add("KiCad could not render the board preview.");
            }
            if (context.Data.Project.SchematicFile is not null)
            {
                var schematicDirectory = Path.Combine(directory, "schematic");
                Directory.CreateDirectory(schematicDirectory);
                var rendered = await _runner.RunAsync(cli.ExecutablePath,
                    new[] { "sch", "export", "svg", "--output", schematicDirectory, context.Data.Project.SchematicFile }, root, cancellationToken);
                if (rendered.ExitCode == 0) renderFiles.AddRange(Directory.GetFiles(schematicDirectory, "*.svg"));
                else uncertainty.Add("KiCad could not render the schematic preview.");
            }
        }
        else
        {
            uncertainty.Add("KiCad CLI is unavailable, so visual renders were not generated.");
        }
        var report = Path.Combine(directory, "review.json");
        var result = new ReviewPackageResult(report, context.Data, validation.Data, renderFiles, uncertainty);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result, JsonOptions), cancellationToken);
        return ToolResponse<ReviewPackageResult>.Ok("Generated project review package.", result, result.UnresolvedUncertainty);
    }

    public async Task<ToolResponse<AssemblyPackageResult>> GeneratePcbWayPackageAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<AssemblyPackageResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        var latest = _transactions.List(project.Data.ProjectRoot).Data?.Transactions.OrderByDescending(static item => item.CreatedAtUtc).FirstOrDefault();
        if (latest is null || latest.Status != ProjectTransactionStatus.GatePassed)
            return ToolResponse<AssemblyPackageResult>.Fail("PCBWay package requires a latest gate-passed design transaction.", "RELEASE_GATE_NOT_PASSED");
        return await _assembly.CreatePcbWayAssemblyPackageAsync(projectPath, cancellationToken);
    }
}

public sealed record ProjectContextResult(ProjectSummary Project, BoardSummary? Board, ComponentListResult? Components, NetListResult? Nets, KiCadGuiCapabilities? KiCadCapabilities, ProjectTransactionRecord? LatestTransaction, SimulationCapabilities? SimulationCapabilities = null, AgentContractReference? AgentContract = null);
public sealed record AgentContractReference(int CapabilityVersion, int GuideVersion, string GuideUri, string DesignPlanSchemaUri);
public sealed record ReviewPackageResult(string ReportPath, ProjectContextResult Context, AssemblyValidationResult? ManufacturingValidation, IReadOnlyList<string> RenderFiles, IReadOnlyList<string> UnresolvedUncertainty);
