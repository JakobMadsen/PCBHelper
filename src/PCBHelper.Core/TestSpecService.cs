using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCBHelper.Core;

public sealed class TestSpecService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> SupportedTestTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "simulation.op",
        "simulation.ac",
        "simulation.tran",
        "topology",
        "geometry",
        "manufacturing"
    };

    private readonly ProjectDiscoveryService _projectDiscovery;

    public TestSpecService(ProjectDiscoveryService projectDiscovery)
    {
        _projectDiscovery = projectDiscovery;
    }

    public ToolResponse<TestSpecListResult> ListTests(string projectPath)
    {
        var load = Load(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<TestSpecListResult>.Fail(load.Summary, load.Error?.Code ?? "TEST_SPEC_INVALID", load.Error?.Message);
        }

        return ToolResponse<TestSpecListResult>.Ok(
            $"Found {load.Data.TestCount} PCBHelper test(s) in {load.Data.Files.Count} file(s).",
            new TestSpecListResult(load.Data.ProjectRoot, load.Data.TestsDirectory, load.Data.Files));
    }

    public ToolResponse<TestSpecValidationResult> ValidateTests(string projectPath)
    {
        var load = Load(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<TestSpecValidationResult>.Fail(
                load.Summary,
                load.Error?.Code ?? "TEST_SPEC_INVALID",
                load.Error?.Message,
                load.Data?.Validation);
        }

        return ToolResponse<TestSpecValidationResult>.Ok(
            $"Validated {load.Data.TestCount} PCBHelper test(s).",
            load.Data.Validation);
    }

    public ToolResponse<TestEvaluationResult> EvaluateResults(string projectPath, string resultsPath)
    {
        if (string.IsNullOrWhiteSpace(resultsPath))
        {
            return ToolResponse<TestEvaluationResult>.Fail("evaluate-test-results requires --results.", "TEST_RESULTS_REQUIRED");
        }

        var load = Load(projectPath);
        if (!load.Success || load.Data is null)
        {
            return ToolResponse<TestEvaluationResult>.Fail(load.Summary, load.Error?.Code ?? "TEST_SPEC_INVALID", load.Error?.Message);
        }

        var results = ReadMeasurementResults(resultsPath);
        if (!results.Success || results.Data is null)
        {
            return ToolResponse<TestEvaluationResult>.Fail(results.Summary, results.Error?.Code ?? "TEST_RESULTS_INVALID", results.Error?.Message);
        }

        var measurementMap = results.Data.Measurements.ToDictionary(static item => item.Name, StringComparer.OrdinalIgnoreCase);
        var evaluations = new List<TestCaseEvaluation>();
        var missingMeasurements = new List<string>();

        foreach (var loadedTest in load.Data.Tests)
        {
            var assertions = new List<TestAssertionEvaluation>();
            foreach (var assertion in loadedTest.Test.Asserts)
            {
                if (!measurementMap.TryGetValue(assertion.Measurement, out var measurement))
                {
                    missingMeasurements.Add(assertion.Measurement);
                    assertions.Add(TestAssertionEvaluation.Missing(assertion.Measurement));
                    continue;
                }

                assertions.Add(EvaluateAssertion(assertion, measurement));
            }

            evaluations.Add(new TestCaseEvaluation(
                loadedTest.Test.Id,
                loadedTest.Test.Type,
                loadedTest.File,
                assertions.All(static item => item.Passed),
                assertions));
        }

        var assertionCount = evaluations.Sum(static item => item.Assertions.Count);
        var passedAssertionCount = evaluations.Sum(static item => item.Assertions.Count(assertion => assertion.Passed));
        var failedAssertionCount = assertionCount - passedAssertionCount;
        var result = new TestEvaluationResult(
            load.Data.ProjectRoot,
            Path.GetFullPath(resultsPath),
            failedAssertionCount == 0,
            evaluations.Count,
            evaluations.Count(static item => item.Passed),
            evaluations.Count(static item => !item.Passed),
            assertionCount,
            passedAssertionCount,
            failedAssertionCount,
            results.Data.Measurements,
            evaluations);

        if (missingMeasurements.Count > 0)
        {
            return ToolResponse<TestEvaluationResult>.Fail(
                $"Missing measurement result(s): {string.Join(", ", missingMeasurements.Distinct(StringComparer.OrdinalIgnoreCase))}.",
                "TEST_MEASUREMENT_NOT_FOUND",
                data: result);
        }

        if (!result.Passed)
        {
            return ToolResponse<TestEvaluationResult>.Fail(
                $"{failedAssertionCount} of {assertionCount} PCBHelper test assertion(s) failed.",
                "TEST_ASSERTIONS_FAILED",
                data: result);
        }

        return ToolResponse<TestEvaluationResult>.Ok(
            $"All {assertionCount} PCBHelper test assertion(s) passed.",
            result);
    }

    private ToolResponse<LoadedTestSpecs> Load(string projectPath)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<LoadedTestSpecs>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var testsDirectory = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "tests");
        if (!Directory.Exists(testsDirectory))
        {
            var emptyValidation = new TestSpecValidationResult(project.Data.ProjectRoot, testsDirectory, true, 0, 0, Array.Empty<TestSpecDiagnostic>());
            return ToolResponse<LoadedTestSpecs>.Ok(
                $"No PCBHelper test directory found: {testsDirectory}",
                new LoadedTestSpecs(project.Data.ProjectRoot, testsDirectory, Array.Empty<TestSpecFileSummary>(), Array.Empty<LoadedTestCase>(), emptyValidation));
        }

        var files = Directory.GetFiles(testsDirectory, "*.json", SearchOption.TopDirectoryOnly).Order().ToArray();
        var summaries = new List<TestSpecFileSummary>();
        var tests = new List<LoadedTestCase>();
        var diagnostics = new List<TestSpecDiagnostic>();

        foreach (var file in files)
        {
            var document = ReadSpecDocument(file);
            if (!document.Success || document.Data is null)
            {
                diagnostics.Add(new TestSpecDiagnostic(file, document.Error?.Code ?? "TEST_SPEC_INVALID", document.Error?.Message ?? document.Summary, null));
                summaries.Add(new TestSpecFileSummary(file, false, 0, Array.Empty<string>()));
                continue;
            }

            var fileDiagnostics = ValidateDocument(file, document.Data);
            diagnostics.AddRange(fileDiagnostics);
            var valid = fileDiagnostics.Count == 0;
            var ids = document.Data.Tests.Select(static test => test.Id).Where(static id => !string.IsNullOrWhiteSpace(id)).ToArray();
            summaries.Add(new TestSpecFileSummary(file, valid, document.Data.Tests.Count, ids));
            tests.AddRange(document.Data.Tests.Select(test => new LoadedTestCase(file, test)));
        }

        var validation = new TestSpecValidationResult(project.Data.ProjectRoot, testsDirectory, diagnostics.Count == 0, files.Length, tests.Count, diagnostics);
        var loaded = new LoadedTestSpecs(project.Data.ProjectRoot, testsDirectory, summaries, tests, validation);
        return diagnostics.Count == 0
            ? ToolResponse<LoadedTestSpecs>.Ok($"Loaded {tests.Count} PCBHelper test(s).", loaded)
            : ToolResponse<LoadedTestSpecs>.Fail("One or more PCBHelper test specs are invalid.", diagnostics[0].Code, diagnostics[0].Message, loaded);
    }

    private static ToolResponse<TestSpecDocument> ReadSpecDocument(string file)
    {
        try
        {
            var document = JsonSerializer.Deserialize<TestSpecDocument>(File.ReadAllText(file), JsonOptions);
            return document is null
                ? ToolResponse<TestSpecDocument>.Fail($"Test spec is empty: {file}", "TEST_SPEC_INVALID")
                : ToolResponse<TestSpecDocument>.Ok($"Read test spec: {file}", document);
        }
        catch (JsonException exception)
        {
            return ToolResponse<TestSpecDocument>.Fail($"Test spec is invalid JSON: {file}", "TEST_SPEC_INVALID", exception.Message);
        }
    }

    private static IReadOnlyList<TestSpecDiagnostic> ValidateDocument(string file, TestSpecDocument document)
    {
        var diagnostics = new List<TestSpecDiagnostic>();
        if (document.Version != 1)
        {
            diagnostics.Add(new TestSpecDiagnostic(file, "TEST_SPEC_INVALID", $"Unsupported test spec version: {document.Version}.", null));
        }

        var testIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var test in document.Tests)
        {
            if (string.IsNullOrWhiteSpace(test.Id))
            {
                diagnostics.Add(new TestSpecDiagnostic(file, "TEST_SPEC_INVALID", "Test id is required.", null));
                continue;
            }

            if (!testIds.Add(test.Id))
            {
                diagnostics.Add(new TestSpecDiagnostic(file, "TEST_SPEC_INVALID", $"Duplicate test id: {test.Id}.", test.Id));
            }

            if (!SupportedTestTypes.Contains(test.Type))
            {
                diagnostics.Add(new TestSpecDiagnostic(file, "TEST_TYPE_UNSUPPORTED", $"Unsupported test type: {test.Type}.", test.Id));
            }

            var measurements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var measurement in test.Measurements)
            {
                if (string.IsNullOrWhiteSpace(measurement.Name))
                {
                    diagnostics.Add(new TestSpecDiagnostic(file, "TEST_SPEC_INVALID", "Measurement name is required.", test.Id));
                    continue;
                }

                if (!measurements.Add(measurement.Name))
                {
                    diagnostics.Add(new TestSpecDiagnostic(file, "TEST_SPEC_INVALID", $"Duplicate measurement: {measurement.Name}.", test.Id));
                }

                if (string.IsNullOrWhiteSpace(measurement.Kind))
                {
                    diagnostics.Add(new TestSpecDiagnostic(file, "TEST_SPEC_INVALID", $"Measurement kind is required: {measurement.Name}.", test.Id));
                }
            }

            foreach (var assertion in test.Asserts)
            {
                if (string.IsNullOrWhiteSpace(assertion.Measurement) || !measurements.Contains(assertion.Measurement))
                {
                    diagnostics.Add(new TestSpecDiagnostic(file, "TEST_MEASUREMENT_NOT_FOUND", $"Assertion references unknown measurement: {assertion.Measurement}.", test.Id));
                }

                var operatorCount = CountOperators(assertion);
                if (operatorCount != 1)
                {
                    diagnostics.Add(new TestSpecDiagnostic(file, "TEST_SPEC_INVALID", $"Assertion for {assertion.Measurement} must specify exactly one operator.", test.Id));
                }

                if (assertion.Between is not null && (assertion.Between.Count != 2 || assertion.Between[0] > assertion.Between[1]))
                {
                    diagnostics.Add(new TestSpecDiagnostic(file, "TEST_SPEC_INVALID", $"Assertion between range is invalid for {assertion.Measurement}.", test.Id));
                }

                if ((assertion.EqualsValue is not null || assertion.Approximately is not null)
                    && assertion.Tolerance is null or < 0)
                {
                    diagnostics.Add(new TestSpecDiagnostic(file, "TEST_SPEC_INVALID", $"Assertion for {assertion.Measurement} requires non-negative tolerance.", test.Id));
                }
            }
        }

        return diagnostics;
    }

    private static int CountOperators(TestAssertionSpec assertion)
    {
        var count = 0;
        if (assertion.Between is not null) count++;
        if (assertion.LessThan is not null) count++;
        if (assertion.GreaterThan is not null) count++;
        if (assertion.EqualsValue is not null) count++;
        if (assertion.Approximately is not null) count++;
        return count;
    }

    private static ToolResponse<TestMeasurementResultDocument> ReadMeasurementResults(string resultsPath)
    {
        var fullPath = Path.GetFullPath(resultsPath);
        if (!File.Exists(fullPath))
        {
            return ToolResponse<TestMeasurementResultDocument>.Fail($"Measurement results file was not found: {fullPath}", "TEST_RESULTS_NOT_FOUND");
        }

        try
        {
            var document = JsonSerializer.Deserialize<TestMeasurementResultDocument>(File.ReadAllText(fullPath), JsonOptions);
            if (document is null)
            {
                return ToolResponse<TestMeasurementResultDocument>.Fail($"Measurement results file is empty: {fullPath}", "TEST_RESULTS_INVALID");
            }

            if (document.Measurements.Any(static measurement => string.IsNullOrWhiteSpace(measurement.Name)))
            {
                return ToolResponse<TestMeasurementResultDocument>.Fail($"Measurement results contain an unnamed measurement: {fullPath}", "TEST_RESULTS_INVALID");
            }

            return ToolResponse<TestMeasurementResultDocument>.Ok($"Read measurement results: {fullPath}", document);
        }
        catch (JsonException exception)
        {
            return ToolResponse<TestMeasurementResultDocument>.Fail($"Measurement results file is invalid JSON: {fullPath}", "TEST_RESULTS_INVALID", exception.Message);
        }
    }

    private static TestAssertionEvaluation EvaluateAssertion(TestAssertionSpec assertion, TestMeasurementResult measurement)
    {
        if (assertion.Between is not null)
        {
            var passed = measurement.Value >= assertion.Between[0] && measurement.Value <= assertion.Between[1];
            return new TestAssertionEvaluation(assertion.Measurement, "between", assertion.Between[0], assertion.Between[1], null, measurement.Value, passed);
        }

        if (assertion.LessThan is not null)
        {
            var passed = measurement.Value < assertion.LessThan.Value;
            return new TestAssertionEvaluation(assertion.Measurement, "lessThan", null, assertion.LessThan, null, measurement.Value, passed);
        }

        if (assertion.GreaterThan is not null)
        {
            var passed = measurement.Value > assertion.GreaterThan.Value;
            return new TestAssertionEvaluation(assertion.Measurement, "greaterThan", assertion.GreaterThan, null, null, measurement.Value, passed);
        }

        if (assertion.EqualsValue is not null)
        {
            var passed = Math.Abs(measurement.Value - assertion.EqualsValue.Value) <= assertion.Tolerance!.Value;
            return new TestAssertionEvaluation(assertion.Measurement, "equals", assertion.EqualsValue, assertion.EqualsValue, assertion.Tolerance, measurement.Value, passed);
        }

        var approximately = assertion.Approximately!.Value;
        var approximatelyPassed = Math.Abs(measurement.Value - approximately) <= assertion.Tolerance!.Value;
        return new TestAssertionEvaluation(assertion.Measurement, "approximately", approximately, approximately, assertion.Tolerance, measurement.Value, approximatelyPassed);
    }
}

public sealed record TestSpecListResult(string ProjectRoot, string TestsDirectory, IReadOnlyList<TestSpecFileSummary> Files);

public sealed record TestSpecFileSummary(string File, bool Valid, int TestCount, IReadOnlyList<string> TestIds);

public sealed record TestSpecValidationResult(string ProjectRoot, string TestsDirectory, bool Valid, int FileCount, int TestCount, IReadOnlyList<TestSpecDiagnostic> Diagnostics);

public sealed record TestSpecDiagnostic(string File, string Code, string Message, string? TestId);

public sealed record TestEvaluationResult(
    string ProjectRoot,
    string ResultsFile,
    bool Passed,
    int TestCount,
    int PassedTestCount,
    int FailedTestCount,
    int AssertionCount,
    int PassedAssertionCount,
    int FailedAssertionCount,
    IReadOnlyList<TestMeasurementResult> Measurements,
    IReadOnlyList<TestCaseEvaluation> Tests);

public sealed record TestCaseEvaluation(string Id, string Type, string File, bool Passed, IReadOnlyList<TestAssertionEvaluation> Assertions);

public sealed record TestAssertionEvaluation(
    string Measurement,
    string Operator,
    double? Min,
    double? Max,
    double? Tolerance,
    double? Actual,
    bool Passed)
{
    public static TestAssertionEvaluation Missing(string measurement)
    {
        return new TestAssertionEvaluation(measurement, "missing", null, null, null, null, false);
    }
}

public sealed record TestMeasurementResultDocument(IReadOnlyList<TestMeasurementResult> Measurements);

public sealed record TestMeasurementResult(string Name, double Value, string? Unit, string? Source);

public sealed record TestSpecDocument(int Version, IReadOnlyList<TestCaseSpec> Tests)
{
    public IReadOnlyList<TestCaseSpec> Tests { get; init; } = Tests ?? Array.Empty<TestCaseSpec>();
}

public sealed record TestCaseSpec(
    string Id,
    string Type,
    string? Description,
    IReadOnlyList<TestMeasurementSpec> Measurements,
    IReadOnlyList<TestAssertionSpec> Asserts)
{
    public IReadOnlyList<TestMeasurementSpec> Measurements { get; init; } = Measurements ?? Array.Empty<TestMeasurementSpec>();
    public IReadOnlyList<TestAssertionSpec> Asserts { get; init; } = Asserts ?? Array.Empty<TestAssertionSpec>();
}

public sealed record TestMeasurementSpec(string Name, string Kind, string? Unit);

public sealed record TestAssertionSpec(
    string Measurement,
    IReadOnlyList<double>? Between,
    double? LessThan,
    double? GreaterThan,
    [property: JsonPropertyName("equals")] double? EqualsValue,
    double? Approximately,
    double? Tolerance);

internal sealed record LoadedTestSpecs(
    string ProjectRoot,
    string TestsDirectory,
    IReadOnlyList<TestSpecFileSummary> Files,
    IReadOnlyList<LoadedTestCase> Tests,
    TestSpecValidationResult Validation)
{
    public int TestCount => Tests.Count;
}

internal sealed record LoadedTestCase(string File, TestCaseSpec Test);
