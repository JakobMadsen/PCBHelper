using System.IO.Compression;
using System.Text.Json;

namespace PCBHelper.Core;

public sealed class PackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly KiCadDoctorService _doctor;
    private readonly ExportService _exportService;

    public PackageService(ProjectDiscoveryService projectDiscovery, KiCadDoctorService doctor, ExportService exportService)
    {
        _projectDiscovery = projectDiscovery;
        _doctor = doctor;
        _exportService = exportService;
    }

    public async Task<ToolResponse<ManufacturingPackageResult>> CreateManufacturingZipAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ManufacturingPackageResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var export = await _exportService.ExportManufacturingFilesAsync(projectPath, cancellationToken);
        if (export.Data is null)
        {
            return ToolResponse<ManufacturingPackageResult>.Fail(export.Summary, export.Error?.Code ?? "EXPORT_FAILED", export.Error?.Message);
        }

        var packageRoot = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "packages", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        Directory.CreateDirectory(packageRoot);

        var doctor = await _doctor.RunAsync(cancellationToken);
        var manifest = new ManufacturingPackageManifest(
            project.Data.ProjectName,
            DateTimeOffset.UtcNow,
            doctor.Data?.VersionOutput,
            export.Data.GeneratedFiles.Select(Path.GetFileName).Where(static name => name is not null).Cast<string>().ToArray());

        var manifestPath = Path.Combine(packageRoot, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);

        var zipPath = Path.Combine(packageRoot, $"{project.Data.ProjectName}-manufacturing.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(manifestPath, "manifest.json");
            foreach (var file in export.Data.GeneratedFiles.Where(File.Exists))
            {
                var relativeName = Path.GetFileName(file);
                if (!string.IsNullOrWhiteSpace(relativeName))
                {
                    archive.CreateEntryFromFile(file, relativeName);
                }
            }
        }

        var result = new ManufacturingPackageResult(zipPath, manifestPath, export.Data.GeneratedFiles);
        return ToolResponse<ManufacturingPackageResult>.Ok($"Created manufacturing zip: {zipPath}", result);
    }
}

public sealed record ManufacturingPackageManifest(
    string ProjectName,
    DateTimeOffset CreatedAtUtc,
    string? KiCadVersion,
    IReadOnlyList<string> IncludedFiles);

public sealed record ManufacturingPackageResult(
    string ZipPath,
    string ManifestPath,
    IReadOnlyList<string> IncludedFiles);
