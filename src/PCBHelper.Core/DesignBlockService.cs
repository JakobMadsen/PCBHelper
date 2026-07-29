using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed class DesignBlockService
{
    public const int ManifestSchemaVersion = 1;
    public const string ManifestFileName = "pcbhelper-block.json";
    private static readonly Regex IdPattern = new("^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public ToolResponse<DesignBlockListResult> List(
        string libraryPath,
        string? category = null,
        DesignBlockMaturity? maturity = null)
    {
        var library = ResolveLibrary(libraryPath, mustExist: true);
        if (!library.Success || library.Data is null)
            return ToolResponse<DesignBlockListResult>.Fail(library.Summary, library.Error!.Code, library.Error.Message);

        var blocks = new List<DesignBlockSummary>();
        var warnings = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(library.Data, "*.kicad_block", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var loaded = LoadManifest(directory);
            if (!loaded.Success || loaded.Data is null)
            {
                warnings.Add($"{Path.GetFileName(directory)}: {loaded.Error?.Message ?? loaded.Summary}");
                continue;
            }

            var manifest = loaded.Data;
            if (category is not null && !manifest.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
            if (maturity is not null && manifest.Maturity != maturity) continue;
            blocks.Add(ToSummary(directory, manifest));
        }

        return ToolResponse<DesignBlockListResult>.Ok(
            $"Found {blocks.Count} PCBHelper design block(s).",
            new DesignBlockListResult(library.Data, blocks),
            warnings);
    }

    public ToolResponse<DesignBlockInspectionResult> Inspect(string libraryPath, string id)
    {
        var target = ResolveBlock(libraryPath, id, mustExist: true);
        if (!target.Success || target.Data is null)
            return ToolResponse<DesignBlockInspectionResult>.Fail(target.Summary, target.Error!.Code, target.Error.Message);
        var loaded = LoadManifest(target.Data);
        if (!loaded.Success || loaded.Data is null)
            return ToolResponse<DesignBlockInspectionResult>.Fail(loaded.Summary, loaded.Error!.Code, loaded.Error.Message);
        var validation = ValidateBlockDirectory(target.Data, loaded.Data);
        return ToolResponse<DesignBlockInspectionResult>.Ok(
            $"Loaded design block {loaded.Data.Id}.",
            new DesignBlockInspectionResult(ToSummary(target.Data, loaded.Data), loaded.Data, validation));
    }

    public ToolResponse<DesignBlockValidationResult> Validate(string libraryPath, string? id = null)
    {
        var library = ResolveLibrary(libraryPath, mustExist: true);
        if (!library.Success || library.Data is null)
            return ToolResponse<DesignBlockValidationResult>.Fail(library.Summary, library.Error!.Code, library.Error.Message);

        var directories = id is null
            ? Directory.EnumerateDirectories(library.Data, "*.kicad_block", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.Ordinal).ToArray()
            : new[] { ResolveBlock(library.Data, id, mustExist: true).Data ?? string.Empty };
        if (directories.Any(string.IsNullOrWhiteSpace))
            return ToolResponse<DesignBlockValidationResult>.Fail($"Design block was not found: {id}", "DESIGN_BLOCK_NOT_FOUND");

        var results = new List<DesignBlockValidation>();
        foreach (var directory in directories)
        {
            var loaded = LoadManifest(directory);
            if (!loaded.Success || loaded.Data is null)
            {
                results.Add(new DesignBlockValidation(
                    Path.GetFileNameWithoutExtension(directory),
                    directory,
                    false,
                    new[] { new DesignBlockFinding("error", loaded.Error?.Code ?? "MANIFEST_INVALID", loaded.Error?.Message ?? loaded.Summary) }));
                continue;
            }
            results.Add(ValidateBlockDirectory(directory, loaded.Data));
        }

        var duplicateIds = results.GroupBy(static result => result.Id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (duplicateIds.Count > 0)
        {
            results = results.Select(result => duplicateIds.Contains(result.Id)
                ? result with
                {
                    Passed = false,
                    Findings = result.Findings.Append(new DesignBlockFinding(
                        "error", "DESIGN_BLOCK_ID_DUPLICATE", $"The id '{result.Id}' occurs more than once in the library.")).ToArray()
                }
                : result).ToList();
        }

        var passed = results.All(static result => result.Passed);
        return ToolResponse<DesignBlockValidationResult>.Ok(
            passed ? "Design block validation passed." : "Design block validation found blocking errors.",
            new DesignBlockValidationResult(library.Data, passed, results));
    }

    public ToolResponse<DesignBlockAuthoringPreview> PreviewCreate(
        string libraryPath,
        string sourcePath,
        string manifestJson,
        string? boardPath = null) =>
        Preview(DesignBlockAuthoringOperation.Create, libraryPath, sourcePath, boardPath, manifestJson);

    public ToolResponse<DesignBlockAuthoringPreview> PreviewImport(
        string libraryPath,
        string sourceBlockPath,
        string manifestJson) =>
        Preview(DesignBlockAuthoringOperation.Import, libraryPath, sourceBlockPath, null, manifestJson);

    public ToolResponse<DesignBlockAuthoringResult> ApplyCreate(
        string libraryPath,
        string sourcePath,
        string manifestJson,
        string expectedPlanHash,
        string? boardPath = null) =>
        Apply(DesignBlockAuthoringOperation.Create, libraryPath, sourcePath, boardPath, manifestJson, expectedPlanHash);

    public ToolResponse<DesignBlockAuthoringResult> ApplyImport(
        string libraryPath,
        string sourceBlockPath,
        string manifestJson,
        string expectedPlanHash) =>
        Apply(DesignBlockAuthoringOperation.Import, libraryPath, sourceBlockPath, null, manifestJson, expectedPlanHash);

    private ToolResponse<DesignBlockAuthoringPreview> Preview(
        DesignBlockAuthoringOperation operation,
        string libraryPath,
        string sourcePath,
        string? boardPath,
        string manifestJson)
    {
        var library = ResolveLibrary(libraryPath, mustExist: false);
        if (!library.Success || library.Data is null)
            return ToolResponse<DesignBlockAuthoringPreview>.Fail(library.Summary, library.Error!.Code, library.Error.Message);
        var manifest = ParseManifest(manifestJson);
        if (!manifest.Success || manifest.Data is null)
            return ToolResponse<DesignBlockAuthoringPreview>.Fail(manifest.Summary, manifest.Error!.Code, manifest.Error.Message);
        var manifestFindings = ValidateManifest(manifest.Data, requirePayload: false);
        var manifestErrors = manifestFindings.Where(static finding => finding.Severity == "error").ToArray();
        if (manifestErrors.Length > 0)
            return ToolResponse<DesignBlockAuthoringPreview>.Fail(
                "The design block manifest is invalid.",
                "DESIGN_BLOCK_MANIFEST_INVALID",
                string.Join(" ", manifestErrors.Select(static finding => finding.Message)));

        var target = ResolveBlock(library.Data, manifest.Data.Id, mustExist: false);
        if (!target.Success || target.Data is null)
            return ToolResponse<DesignBlockAuthoringPreview>.Fail(target.Summary, target.Error!.Code, target.Error.Message);
        if (Directory.Exists(target.Data) || File.Exists(target.Data))
            return ToolResponse<DesignBlockAuthoringPreview>.Fail(
                $"Design block already exists: {target.Data}",
                "DESIGN_BLOCK_EXISTS",
                "Create a new semantic version or choose a different id; PCBHelper never overwrites a block.");

        var payload = ResolvePayload(operation, sourcePath, boardPath);
        if (!payload.Success || payload.Data is null)
            return ToolResponse<DesignBlockAuthoringPreview>.Fail(payload.Summary, payload.Error!.Code, payload.Error.Message);

        var filePlans = new List<DesignBlockFilePlan>();
        if (payload.Data.SchematicPath is not null)
            filePlans.Add(CreateFilePlan(payload.Data.SchematicPath, $"{manifest.Data.Id}.kicad_sch"));
        if (payload.Data.BoardPath is not null)
            filePlans.Add(CreateFilePlan(payload.Data.BoardPath, $"{manifest.Data.Id}.kicad_pcb"));
        filePlans.Add(new DesignBlockFilePlan(
            $"{manifest.Data.Id}.json",
            null,
            Sha256(Encoding.UTF8.GetBytes(CreateKiCadMetadata(manifest.Data, payload.Data.SourceMetadata))),
            "generated-kicad-metadata"));

        var finalized = manifest.Data with
        {
            NativeKiCad = new DesignBlockNativeKiCad(
                10,
                payload.Data.SchematicPath is null ? null : $"{manifest.Data.Id}.kicad_sch",
                payload.Data.BoardPath is null ? null : $"{manifest.Data.Id}.kicad_pcb",
                $"{manifest.Data.Id}.json",
                filePlans.Where(static file => file.SourcePath is not null)
                    .ToDictionary(static file => file.TargetFile, static file => file.Sha256, StringComparer.Ordinal))
        };
        var finalizedJson = JsonSerializer.Serialize(finalized, JsonOptions);
        filePlans.Add(new DesignBlockFilePlan(ManifestFileName, null, Sha256(Encoding.UTF8.GetBytes(finalizedJson)), "generated-pcbhelper-manifest"));
        var reviewMarkdown = CreateReviewTemplate(finalized);
        filePlans.Add(new DesignBlockFilePlan("REVIEW.md", null, Sha256(Encoding.UTF8.GetBytes(reviewMarkdown)), "generated-review-checklist"));

        var canonical = JsonSerializer.Serialize(new
        {
            operation = operation.ToString().ToLowerInvariant(),
            library = library.Data,
            target = target.Data,
            manifest = finalized,
            files = filePlans.Select(static file => new { file.TargetFile, file.Sha256, file.Kind }).OrderBy(static file => file.TargetFile)
        }, JsonOptions);
        var planHash = Sha256(Encoding.UTF8.GetBytes(canonical));
        var warnings = manifestFindings.Where(static finding => finding.Severity == "warning")
            .Select(static finding => finding.Message).ToArray();
        return ToolResponse<DesignBlockAuthoringPreview>.Ok(
            $"Prepared {operation.ToString().ToLowerInvariant()} preview for design block {manifest.Data.Id}.",
            new DesignBlockAuthoringPreview(
                operation,
                library.Data,
                target.Data,
                planHash,
                finalized,
                filePlans,
                new[]
                {
                    "Review the source, license, port contract, layout policy, and generated target paths.",
                    "Apply only this exact plan hash. PCBHelper will refuse an existing target."
                }),
            warnings);
    }

    private ToolResponse<DesignBlockAuthoringResult> Apply(
        DesignBlockAuthoringOperation operation,
        string libraryPath,
        string sourcePath,
        string? boardPath,
        string manifestJson,
        string expectedPlanHash)
    {
        if (string.IsNullOrWhiteSpace(expectedPlanHash))
            return ToolResponse<DesignBlockAuthoringResult>.Fail(
                "An expected plan hash from preview is required.",
                "DESIGN_BLOCK_PLAN_HASH_REQUIRED");
        var preview = Preview(operation, libraryPath, sourcePath, boardPath, manifestJson);
        if (!preview.Success || preview.Data is null)
            return ToolResponse<DesignBlockAuthoringResult>.Fail(preview.Summary, preview.Error!.Code, preview.Error.Message);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(preview.Data.PlanHash.ToUpperInvariant()),
                Encoding.ASCII.GetBytes(expectedPlanHash.ToUpperInvariant())))
            return ToolResponse<DesignBlockAuthoringResult>.Fail(
                "The design block plan changed after preview.",
                "DESIGN_BLOCK_PLAN_HASH_MISMATCH",
                $"Expected {expectedPlanHash}; current preview is {preview.Data.PlanHash}.");

        var library = preview.Data.LibraryPath;
        var target = preview.Data.TargetDirectory;
        if (Directory.Exists(target) || File.Exists(target))
            return ToolResponse<DesignBlockAuthoringResult>.Fail(
                $"Design block already exists: {target}",
                "DESIGN_BLOCK_EXISTS");

        Directory.CreateDirectory(library);
        var staging = Path.Combine(library, $".{preview.Data.Manifest.Id}.{Guid.NewGuid():N}.staging");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in preview.Data.Files.Where(static file => file.SourcePath is not null))
                File.Copy(file.SourcePath!, Path.Combine(staging, file.TargetFile), overwrite: false);

            var sourceMetadata = ResolvePayload(operation, sourcePath, boardPath).Data?.SourceMetadata;
            File.WriteAllText(
                Path.Combine(staging, $"{preview.Data.Manifest.Id}.json"),
                CreateKiCadMetadata(preview.Data.Manifest, sourceMetadata),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(staging, ManifestFileName),
                JsonSerializer.Serialize(preview.Data.Manifest, JsonOptions),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(staging, "REVIEW.md"),
                CreateReviewTemplate(preview.Data.Manifest),
                new UTF8Encoding(false));
            Directory.Move(staging, target);
        }
        catch (Exception exception)
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            return ToolResponse<DesignBlockAuthoringResult>.Fail(
                "Could not apply the design block authoring plan.",
                "DESIGN_BLOCK_APPLY_FAILED",
                exception.Message);
        }

        var validation = Validate(library, preview.Data.Manifest.Id);
        var blockValidation = validation.Data?.Blocks.SingleOrDefault();
        return ToolResponse<DesignBlockAuthoringResult>.Ok(
            $"Created immutable design block {preview.Data.Manifest.Id} {preview.Data.Manifest.Version}.",
            new DesignBlockAuthoringResult(
                operation,
                preview.Data.PlanHash,
                target,
                preview.Data.Manifest,
                blockValidation),
            blockValidation?.Findings.Where(static finding => finding.Severity == "warning")
                .Select(static finding => finding.Message).ToArray());
    }

    private static ToolResponse<ResolvedPayload> ResolvePayload(
        DesignBlockAuthoringOperation operation,
        string sourcePath,
        string? boardPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return ToolResponse<ResolvedPayload>.Fail("A source path is required.", "DESIGN_BLOCK_SOURCE_REQUIRED");
        var fullSource = Path.GetFullPath(sourcePath);
        if (operation == DesignBlockAuthoringOperation.Import)
        {
            if (!Directory.Exists(fullSource) || !fullSource.EndsWith(".kicad_block", StringComparison.OrdinalIgnoreCase))
                return ToolResponse<ResolvedPayload>.Fail(
                    "Import requires an existing native KiCad .kicad_block directory.",
                    "DESIGN_BLOCK_IMPORT_SOURCE_INVALID");
            var schematic = FindSinglePayload(fullSource, "*.kicad_sch");
            if (!schematic.Success) return ToolResponse<ResolvedPayload>.Fail(schematic.Summary, schematic.Error!.Code, schematic.Error.Message);
            var board = FindSinglePayload(fullSource, "*.kicad_pcb");
            if (!board.Success) return ToolResponse<ResolvedPayload>.Fail(board.Summary, board.Error!.Code, board.Error.Message);
            if (schematic.Data is null && board.Data is null)
                return ToolResponse<ResolvedPayload>.Fail("The source block has no schematic or board payload.", "DESIGN_BLOCK_PAYLOAD_MISSING");
            return ToolResponse<ResolvedPayload>.Ok(
                "Resolved native KiCad design block payload.",
                new ResolvedPayload(schematic.Data, board.Data, LoadSourceMetadata(fullSource)));
        }

        string? schematicPath = null;
        string? resolvedBoardPath = null;
        if (Directory.Exists(fullSource))
        {
            var schematic = FindSinglePayload(fullSource, "*.kicad_sch");
            if (!schematic.Success) return ToolResponse<ResolvedPayload>.Fail(schematic.Summary, schematic.Error!.Code, schematic.Error.Message);
            schematicPath = schematic.Data;
            var board = FindSinglePayload(fullSource, "*.kicad_pcb");
            if (!board.Success) return ToolResponse<ResolvedPayload>.Fail(board.Summary, board.Error!.Code, board.Error.Message);
            resolvedBoardPath = board.Data;
        }
        else if (File.Exists(fullSource) && fullSource.EndsWith(".kicad_sch", StringComparison.OrdinalIgnoreCase))
        {
            schematicPath = fullSource;
        }
        else if (File.Exists(fullSource) && fullSource.EndsWith(".kicad_pcb", StringComparison.OrdinalIgnoreCase))
        {
            resolvedBoardPath = fullSource;
        }
        else
        {
            return ToolResponse<ResolvedPayload>.Fail(
                "Create requires a KiCad schematic, board, or directory containing one of each.",
                "DESIGN_BLOCK_CREATE_SOURCE_INVALID");
        }

        if (boardPath is not null)
        {
            var explicitBoard = Path.GetFullPath(boardPath);
            if (!File.Exists(explicitBoard) || !explicitBoard.EndsWith(".kicad_pcb", StringComparison.OrdinalIgnoreCase))
                return ToolResponse<ResolvedPayload>.Fail("The explicit board payload is invalid.", "DESIGN_BLOCK_BOARD_SOURCE_INVALID");
            resolvedBoardPath = explicitBoard;
        }
        if (schematicPath is null && resolvedBoardPath is null)
            return ToolResponse<ResolvedPayload>.Fail("No KiCad payload was found.", "DESIGN_BLOCK_PAYLOAD_MISSING");
        return ToolResponse<ResolvedPayload>.Ok(
            "Resolved new design block payload.",
            new ResolvedPayload(schematicPath, resolvedBoardPath, null));
    }

    private static ToolResponse<string?> FindSinglePayload(string directory, string pattern)
    {
        var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        return files.Length switch
        {
            0 => ToolResponse<string?>.Ok("No matching payload.", null),
            1 => ToolResponse<string?>.Ok("Resolved payload.", Path.GetFullPath(files[0])),
            _ => ToolResponse<string?>.Fail(
                $"Source contains more than one {pattern} payload.",
                "DESIGN_BLOCK_PAYLOAD_AMBIGUOUS",
                "Use a source directory containing at most one schematic and one board.")
        };
    }

    private static JsonElement? LoadSourceMetadata(string directory)
    {
        var metadata = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .SingleOrDefault();
        if (metadata is null) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadata));
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static ToolResponse<string> ResolveLibrary(string libraryPath, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
            return ToolResponse<string>.Fail("A design block library path is required.", "DESIGN_BLOCK_LIBRARY_REQUIRED");
        var full = Path.GetFullPath(libraryPath);
        if (!full.EndsWith(".kicad_blocks", StringComparison.OrdinalIgnoreCase))
            return ToolResponse<string>.Fail(
                "A KiCad design block library directory must end in .kicad_blocks.",
                "DESIGN_BLOCK_LIBRARY_INVALID");
        if (mustExist && !Directory.Exists(full))
            return ToolResponse<string>.Fail($"Design block library was not found: {full}", "DESIGN_BLOCK_LIBRARY_NOT_FOUND");
        return ToolResponse<string>.Ok("Resolved design block library.", full);
    }

    private static ToolResponse<string> ResolveBlock(string libraryPath, string id, bool mustExist)
    {
        if (!IdPattern.IsMatch(id ?? string.Empty))
            return ToolResponse<string>.Fail(
                "A block id must use lowercase letters, digits, dots, underscores, or hyphens.",
                "DESIGN_BLOCK_ID_INVALID");
        var library = Path.GetFullPath(libraryPath);
        var target = Path.GetFullPath(Path.Combine(library, $"{id}.kicad_block"));
        var prefix = library.EndsWith(Path.DirectorySeparatorChar) ? library : library + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return ToolResponse<string>.Fail("Design block target escaped the library root.", "DESIGN_BLOCK_PATH_INVALID");
        if (mustExist && !Directory.Exists(target))
            return ToolResponse<string>.Fail($"Design block was not found: {id}", "DESIGN_BLOCK_NOT_FOUND");
        return ToolResponse<string>.Ok("Resolved design block path.", target);
    }

    private static ToolResponse<DesignBlockManifest> LoadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(path))
            return ToolResponse<DesignBlockManifest>.Fail(
                $"Missing {ManifestFileName}.",
                "DESIGN_BLOCK_MANIFEST_MISSING");
        return ParseManifest(File.ReadAllText(path));
    }

    private static ToolResponse<DesignBlockManifest> ParseManifest(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<DesignBlockManifest>(json, JsonOptions);
            return manifest is null
                ? ToolResponse<DesignBlockManifest>.Fail("The design block manifest is empty.", "DESIGN_BLOCK_MANIFEST_INVALID")
                : ToolResponse<DesignBlockManifest>.Ok("Parsed design block manifest.", manifest);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ToolResponse<DesignBlockManifest>.Fail(
                "The design block manifest is not valid PCBHelper JSON.",
                "DESIGN_BLOCK_MANIFEST_INVALID",
                exception.Message);
        }
    }

    private static DesignBlockValidation ValidateBlockDirectory(string directory, DesignBlockManifest manifest)
    {
        var findings = ValidateManifest(manifest, requirePayload: true).ToList();
        var directoryId = Path.GetFileNameWithoutExtension(directory);
        if (!manifest.Id.Equals(directoryId, StringComparison.Ordinal))
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_DIRECTORY_MISMATCH", $"Manifest id '{manifest.Id}' does not match directory '{directoryId}'."));

        if (manifest.NativeKiCad is not null)
        {
            var payloadHashes = manifest.NativeKiCad.PayloadSha256 ?? new Dictionary<string, string>();
            ValidatePayloadFile(directory, manifest.NativeKiCad.SchematicFile, ".kicad_sch", "(kicad_sch", payloadHashes, findings);
            ValidatePayloadFile(directory, manifest.NativeKiCad.BoardFile, ".kicad_pcb", "(kicad_pcb", payloadHashes, findings);
            var metadataPath = SafeChildPath(directory, manifest.NativeKiCad.MetadataFile);
            if (metadataPath is null || !File.Exists(metadataPath))
            {
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_METADATA_MISSING", "Native KiCad metadata is missing."));
            }
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    var root = document.RootElement;
                    if (!root.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("keywords", out var keywords) || keywords.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                        findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_METADATA_INVALID", "KiCad metadata must contain string description, string keywords, and object fields."));
                }
                catch (JsonException exception)
                {
                    findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_METADATA_INVALID", exception.Message));
                }
            }
        }

        foreach (var evidence in manifest.Evidence ?? Array.Empty<DesignBlockEvidence>())
        {
            if (string.IsNullOrWhiteSpace(evidence.Uri)) continue;
            if (Uri.TryCreate(evidence.Uri, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") continue;
            var evidencePath = SafeChildPath(directory, evidence.Uri);
            if (evidencePath is null || !File.Exists(evidencePath))
            {
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_EVIDENCE_MISSING", $"Evidence does not exist inside the block: {evidence.Uri}"));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(evidence.Sha256) &&
                !Sha256(File.ReadAllBytes(evidencePath)).Equals(evidence.Sha256, StringComparison.OrdinalIgnoreCase))
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_EVIDENCE_HASH_MISMATCH", $"Evidence hash does not match: {evidence.Uri}"));
        }

        return new DesignBlockValidation(
            manifest.Id,
            directory,
            findings.All(static finding => finding.Severity != "error"),
            findings);
    }

    private static IReadOnlyList<DesignBlockFinding> ValidateManifest(DesignBlockManifest manifest, bool requirePayload)
    {
        var findings = new List<DesignBlockFinding>();
        if (manifest.SchemaVersion != ManifestSchemaVersion)
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_SCHEMA_UNSUPPORTED", $"schemaVersion must be {ManifestSchemaVersion}."));
        if (!IdPattern.IsMatch(manifest.Id ?? string.Empty))
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_ID_INVALID", "id must use lowercase letters, digits, dots, underscores, or hyphens."));
        if (!VersionPattern.IsMatch(manifest.Version ?? string.Empty))
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_VERSION_INVALID", "version must be semantic version form such as 1.0.0."));
        Required(manifest.Name, "name", findings);
        Required(manifest.Description, "description", findings);
        Required(manifest.Category, "category", findings);
        if (manifest.Source is null)
        {
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PROVENANCE_REQUIRED", "source provenance is required."));
        }
        else
        {
            Required(manifest.Source.Title, "source.title", findings);
            Required(manifest.Source.License, "source.license", findings);
            Required(manifest.Source.Attribution, "source.attribution", findings);
            if (manifest.Source.RetrievedAtUtc == default)
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_RETRIEVAL_DATE_REQUIRED", "source.retrievedAtUtc is required."));
            if (!Uri.TryCreate(manifest.Source.Uri, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "internal"))
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_SOURCE_URI_INVALID", "source.uri must be an absolute http, https, or internal URI."));
            if (manifest.LifecycleStage == DesignBlockLifecycleStage.Published && !manifest.Source.RedistributionAllowed)
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_REDISTRIBUTION_BLOCKED", "A published block must have redistribution permission."));
        }
        if (manifest.Ports is null || manifest.Ports.Count == 0)
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PORTS_REQUIRED", "At least one explicit interface port is required."));
        foreach (var duplicate in (manifest.Ports ?? Array.Empty<DesignBlockPort>()).GroupBy(static port => port.Name, StringComparer.OrdinalIgnoreCase).Where(static group => group.Count() > 1))
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PORT_DUPLICATE", $"Port name is duplicated: {duplicate.Key}."));
        foreach (var port in manifest.Ports ?? Array.Empty<DesignBlockPort>())
        {
            Required(port.Name, "ports[].name", findings);
            Required(port.Net, "ports[].net", findings);
            Required(port.Description, "ports[].description", findings);
        }
        if (manifest.Layout is null)
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_LAYOUT_REQUIRED", "An explicit layout policy is required."));
        if (requirePayload && manifest.NativeKiCad is null)
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_NATIVE_PAYLOAD_REQUIRED", "Native KiCad payload metadata is required."));
        if (manifest.NativeKiCad is not null)
        {
            if (manifest.NativeKiCad.MinimumKiCadMajor < 10)
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_KICAD_VERSION_INVALID", "Native block format requires KiCad 10 or newer."));
            if (manifest.NativeKiCad.SchematicFile is null && manifest.NativeKiCad.BoardFile is null)
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PAYLOAD_MISSING", "A schematic or board payload is required."));
            if (manifest.Layout?.Policy == DesignBlockLayoutPolicy.Locked && manifest.NativeKiCad.BoardFile is null)
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_LOCKED_LAYOUT_MISSING", "A locked layout policy requires a board payload."));
        }

        RequireEvidenceForMaturity(manifest, findings);
        if (manifest.LifecycleStage == DesignBlockLifecycleStage.Published)
        {
            if (manifest.NativeKiCad?.SchematicFile is null)
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PUBLISH_SCHEMATIC_REQUIRED", "Published blocks require a schematic payload."));
            if (!HasPassedEvidence(manifest, DesignBlockEvidenceKind.Review))
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_REVIEW_REQUIRED", "Published blocks require passed review evidence."));
        }
        else if (manifest.LifecycleStage == DesignBlockLifecycleStage.Reviewed &&
                 !HasPassedEvidence(manifest, DesignBlockEvidenceKind.Review))
        {
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_REVIEW_REQUIRED", "Reviewed lifecycle stage requires passed review evidence."));
        }
        else
        {
            findings.Add(new DesignBlockFinding("warning", "DESIGN_BLOCK_NOT_PUBLISHED", $"Block lifecycle stage is {manifest.LifecycleStage}."));
        }
        return findings;
    }

    private static void RequireEvidenceForMaturity(DesignBlockManifest manifest, List<DesignBlockFinding> findings)
    {
        void Require(DesignBlockEvidenceKind kind, string message)
        {
            if (!HasPassedEvidence(manifest, kind))
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_MATURITY_EVIDENCE_REQUIRED", message));
        }

        if (manifest.Maturity >= DesignBlockMaturity.Simulated) Require(DesignBlockEvidenceKind.Simulation, "Simulated maturity requires passed simulation evidence.");
        if (manifest.Maturity >= DesignBlockMaturity.PrototypeTested) Require(DesignBlockEvidenceKind.BenchTest, "Prototype-tested maturity requires passed bench-test evidence.");
        if (manifest.Maturity >= DesignBlockMaturity.Qualified) Require(DesignBlockEvidenceKind.Review, "Qualified maturity requires passed review evidence.");
        if (manifest.Maturity >= DesignBlockMaturity.ProductionProven) Require(DesignBlockEvidenceKind.ProductionRun, "Production-proven maturity requires passed production-run evidence.");
    }

    private static bool HasPassedEvidence(DesignBlockManifest manifest, DesignBlockEvidenceKind kind) =>
        (manifest.Evidence ?? Array.Empty<DesignBlockEvidence>()).Any(evidence => evidence.Kind == kind && evidence.Result == DesignBlockEvidenceResult.Passed);

    private static void ValidatePayloadFile(
        string directory,
        string? relativePath,
        string extension,
        string header,
        IReadOnlyDictionary<string, string> hashes,
        List<DesignBlockFinding> findings)
    {
        if (relativePath is null) return;
        if (!relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PAYLOAD_EXTENSION_INVALID", $"Payload must end with {extension}: {relativePath}"));
            return;
        }
        var path = SafeChildPath(directory, relativePath);
        if (path is null || !File.Exists(path))
        {
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PAYLOAD_MISSING", $"Payload file is missing: {relativePath}"));
            return;
        }
        using (var reader = new StreamReader(path))
        {
            var first = reader.ReadLine() ?? string.Empty;
            if (!first.TrimStart().StartsWith(header, StringComparison.Ordinal))
                findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PAYLOAD_INVALID", $"Payload does not look like a KiCad {extension} file: {relativePath}"));
        }
        if (!hashes.TryGetValue(relativePath, out var expected))
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PAYLOAD_HASH_MISSING", $"Manifest has no payload hash for {relativePath}."));
        else if (!Sha256(File.ReadAllBytes(path)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_PAYLOAD_HASH_MISMATCH", $"Payload hash does not match: {relativePath}"));
    }

    private static string? SafeChildPath(string directory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) return null;
        var root = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static DesignBlockSummary ToSummary(string directory, DesignBlockManifest manifest) =>
        new(manifest.Id, manifest.Version, manifest.Name, manifest.Category, manifest.LifecycleStage, manifest.Maturity,
            manifest.Ports ?? Array.Empty<DesignBlockPort>(), manifest.Layout?.Policy ?? DesignBlockLayoutPolicy.SchematicOnly, directory);

    private static DesignBlockFilePlan CreateFilePlan(string sourcePath, string targetFile) =>
        new(targetFile, sourcePath, Sha256(File.ReadAllBytes(sourcePath)), "copied-native-payload");

    private static string CreateKiCadMetadata(DesignBlockManifest manifest, JsonElement? sourceMetadata)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sourceMetadata is { ValueKind: JsonValueKind.Object } metadata &&
            metadata.TryGetProperty("fields", out var existingFields) && existingFields.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in existingFields.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String) fields[property.Name] = property.Value.GetString()!;
        }
        var keywords = string.Join(' ', (manifest.Tags ?? Array.Empty<string>())
            .Append(manifest.Category)
            .Append(manifest.Maturity.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Replace(' ', '-').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal));
        return JsonSerializer.Serialize(new { description = manifest.Description, keywords, fields }, JsonOptions);
    }

    private static string CreateReviewTemplate(DesignBlockManifest manifest) =>
        $"""
        # Review: {manifest.Name} {manifest.Version}

        This block is not accepted merely because it exists. Complete the fixed PCBHelper block process:

        - [ ] Source, attribution, license, and redistribution permission reviewed
        - [ ] Schematic redrawn or normalized to the PCBHelper house style
        - [ ] Port names, directions, electrical domains, and net names reviewed
        - [ ] Component ratings, tolerances, footprints, and sourcing reviewed
        - [ ] Layout policy and any keep-outs or placement constraints reviewed
        - [ ] ERC/DRC and relevant simulation evidence attached
        - [ ] Human schematic review passed
        - [ ] Human PCB review passed when a layout payload is present
        - [ ] Prototype/production evidence attached before increasing maturity
        - [ ] Version and immutable payload hashes reviewed before publication

        Lifecycle: {manifest.LifecycleStage}
        Maturity: {manifest.Maturity}
        Source: {manifest.Source?.Uri}
        """;

    private static void Required(string? value, string field, List<DesignBlockFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(value))
            findings.Add(new DesignBlockFinding("error", "DESIGN_BLOCK_FIELD_REQUIRED", $"{field} is required."));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record ResolvedPayload(string? SchematicPath, string? BoardPath, JsonElement? SourceMetadata);
}

public enum DesignBlockAuthoringOperation { Create, Import }
public enum DesignBlockLifecycleStage { Draft, Imported, Reviewed, Published, Deprecated }
public enum DesignBlockMaturity { Reference, Simulated, PrototypeTested, Qualified, ProductionProven }
public enum DesignBlockSourceKind { ManufacturerReference, OfficialKiCad, OpenHardware, Internal, ProjectExtraction }
public enum DesignBlockPortKind { PowerInput, PowerOutput, Ground, AnalogInput, AnalogOutput, DigitalInput, DigitalOutput, Reference, Sense, Shield, TestAccess }
public enum DesignBlockLayoutPolicy { SchematicOnly, Grouped, Anchored, Locked }
public enum DesignBlockEvidenceKind { Review, Simulation, BenchTest, ProductionRun, Erc, Drc, Datasheet }
public enum DesignBlockEvidenceResult { Passed, Failed, Informational }

public sealed record DesignBlockManifest(
    int SchemaVersion,
    string Id,
    string Version,
    string Name,
    string Description,
    string Category,
    DesignBlockLifecycleStage LifecycleStage,
    DesignBlockMaturity Maturity,
    DesignBlockSource? Source,
    IReadOnlyList<DesignBlockPort> Ports,
    DesignBlockLayoutContract? Layout,
    IReadOnlyList<DesignBlockEvidence> Evidence,
    IReadOnlyList<string> Tags,
    DesignBlockNativeKiCad? NativeKiCad = null);

public sealed record DesignBlockSource(
    DesignBlockSourceKind Kind,
    string Title,
    string Uri,
    string License,
    string? Attribution,
    bool RedistributionAllowed,
    DateTimeOffset RetrievedAtUtc);

public sealed record DesignBlockPort(
    string Name,
    DesignBlockPortKind Kind,
    string Net,
    string Description,
    string? ElectricalDomain = null,
    bool Required = true);

public sealed record DesignBlockLayoutContract(
    DesignBlockLayoutPolicy Policy,
    string Rationale,
    IReadOnlyList<string> Constraints);

public sealed record DesignBlockEvidence(
    DesignBlockEvidenceKind Kind,
    DesignBlockEvidenceResult Result,
    string Uri,
    string? Sha256,
    DateTimeOffset RecordedAtUtc,
    string? Reviewer = null);

public sealed record DesignBlockNativeKiCad(
    int MinimumKiCadMajor,
    string? SchematicFile,
    string? BoardFile,
    string MetadataFile,
    IReadOnlyDictionary<string, string> PayloadSha256);

public sealed record DesignBlockFilePlan(string TargetFile, string? SourcePath, string Sha256, string Kind);
public sealed record DesignBlockSummary(
    string Id,
    string Version,
    string Name,
    string Category,
    DesignBlockLifecycleStage LifecycleStage,
    DesignBlockMaturity Maturity,
    IReadOnlyList<DesignBlockPort> Ports,
    DesignBlockLayoutPolicy LayoutPolicy,
    string Directory);
public sealed record DesignBlockListResult(string LibraryPath, IReadOnlyList<DesignBlockSummary> Blocks);
public sealed record DesignBlockInspectionResult(DesignBlockSummary Summary, DesignBlockManifest Manifest, DesignBlockValidation Validation);
public sealed record DesignBlockFinding(string Severity, string Code, string Message);
public sealed record DesignBlockValidation(string Id, string Directory, bool Passed, IReadOnlyList<DesignBlockFinding> Findings);
public sealed record DesignBlockValidationResult(string LibraryPath, bool Passed, IReadOnlyList<DesignBlockValidation> Blocks);
public sealed record DesignBlockAuthoringPreview(
    DesignBlockAuthoringOperation Operation,
    string LibraryPath,
    string TargetDirectory,
    string PlanHash,
    DesignBlockManifest Manifest,
    IReadOnlyList<DesignBlockFilePlan> Files,
    IReadOnlyList<string> RequiredReview);
public sealed record DesignBlockAuthoringResult(
    DesignBlockAuthoringOperation Operation,
    string PlanHash,
    string BlockDirectory,
    DesignBlockManifest Manifest,
    DesignBlockValidation? Validation);
