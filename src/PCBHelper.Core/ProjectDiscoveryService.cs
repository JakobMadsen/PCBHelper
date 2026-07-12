namespace PCBHelper.Core;

public sealed class ProjectDiscoveryService
{
    private readonly ProjectScopePolicy _scope;

    public ProjectDiscoveryService()
        : this(ProjectScopePolicy.Unrestricted())
    {
    }

    public ProjectDiscoveryService(ProjectScopePolicy scope)
    {
        _scope = scope;
    }

    public ToolResponse<ProjectSummary> GetSummary(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return ToolResponse<ProjectSummary>.Fail("Project path is required.", "PROJECT_PATH_REQUIRED");
        }

        var authorized = _scope.Authorize(projectPath);
        if (!authorized.Success || authorized.Data is null)
        {
            return ToolResponse<ProjectSummary>.Fail(authorized.Summary, authorized.Error?.Code ?? "PROJECT_SCOPE_VIOLATION", authorized.Error?.Message);
        }

        var fullPath = authorized.Data;
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            return ToolResponse<ProjectSummary>.Fail($"Project path does not exist: {fullPath}", "PROJECT_PATH_NOT_FOUND");
        }

        var root = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        if (root is null)
        {
            return ToolResponse<ProjectSummary>.Fail($"Could not resolve project root for: {fullPath}", "PROJECT_ROOT_UNRESOLVED");
        }

        root = Path.GetFullPath(root);

        var projectFiles = Directory.GetFiles(root, "*.kicad_pro", SearchOption.TopDirectoryOnly).Order().ToArray();
        var schematicFiles = Directory.GetFiles(root, "*.kicad_sch", SearchOption.TopDirectoryOnly).Order().ToArray();
        var boardFiles = Directory.GetFiles(root, "*.kicad_pcb", SearchOption.TopDirectoryOnly).Order().ToArray();

        var warnings = new List<string>();
        if (projectFiles.Length > 1)
        {
            warnings.Add($"Multiple .kicad_pro files found; using {projectFiles[0]}.");
        }

        if (schematicFiles.Length > 1)
        {
            warnings.Add($"Multiple .kicad_sch files found; using {schematicFiles[0]}.");
        }

        if (boardFiles.Length > 1)
        {
            warnings.Add($"Multiple .kicad_pcb files found; using {boardFiles[0]}.");
        }

        var missing = new List<string>();
        if (projectFiles.Length == 0)
        {
            missing.Add(".kicad_pro");
        }

        if (schematicFiles.Length == 0)
        {
            missing.Add(".kicad_sch");
        }

        if (boardFiles.Length == 0)
        {
            missing.Add(".kicad_pcb");
        }

        var projectFile = projectFiles.FirstOrDefault();
        var projectName = projectFile is not null
            ? Path.GetFileNameWithoutExtension(projectFile)
            : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var summary = new ProjectSummary(
            projectName,
            root,
            projectFile,
            schematicFiles.FirstOrDefault(),
            boardFiles.FirstOrDefault(),
            missing,
            warnings);

        var text = missing.Count == 0
            ? $"Found KiCad project '{projectName}'."
            : $"Found project folder '{projectName}' with missing files: {string.Join(", ", missing)}.";

        return ToolResponse<ProjectSummary>.Ok(text, summary, warnings);
    }
}

public sealed record ProjectSummary(
    string ProjectName,
    string ProjectRoot,
    string? ProjectFile,
    string? SchematicFile,
    string? BoardFile,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> Warnings);
