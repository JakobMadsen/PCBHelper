using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public interface ISimulationBackend
{
    SimulationCapabilities GetCapabilities();
    Task<ToolResponse<SimulationBackendResult>> RunAsync(string circuit, string analysisControl, string outputDirectory, CancellationToken cancellationToken);
}

public sealed class NgspiceLocator
{
    private readonly Func<string, string?> _environment;
    public NgspiceLocator() : this(Environment.GetEnvironmentVariable) { }
    public NgspiceLocator(Func<string, string?> environment) => _environment = environment;

    public SimulationCapabilities Locate()
    {
        var configured = _environment("NGSPICE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim('"')));
            return File.Exists(path)
                ? new(true, "ngspice", path, "NGSPICE", null)
                : new(false, "ngspice", null, "NGSPICE", $"NGSPICE points to a missing file: {path}");
        }

        var pathValue = _environment("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "ngspice.exe" : "ngspice");
            if (File.Exists(candidate)) return new(true, "ngspice", Path.GetFullPath(candidate), "PATH", null);
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in new[]
            {
                @"C:\Program Files\ngspice\bin\ngspice.exe", @"C:\Spice64\bin\ngspice.exe",
                @"D:\Program Files\ngspice\bin\ngspice.exe", @"D:\Spice64\bin\ngspice.exe"
            })
                if (File.Exists(candidate)) return new(true, "ngspice", candidate, "installation", null);
        }

        return new(false, "ngspice", null, "PATH", "Set NGSPICE or add ngspice to PATH.");
    }
}

public sealed class NgspiceBackend : ISimulationBackend
{
    private readonly NgspiceLocator _locator;
    private readonly ICommandRunner _runner;
    private readonly TimeSpan _timeout;
    public NgspiceBackend(NgspiceLocator locator, ICommandRunner runner, TimeSpan? timeout = null)
    { _locator = locator; _runner = runner; _timeout = timeout ?? TimeSpan.FromSeconds(60); }

    public SimulationCapabilities GetCapabilities() => _locator.Locate();

    public async Task<ToolResponse<SimulationBackendResult>> RunAsync(string circuit, string analysisControl, string outputDirectory, CancellationToken cancellationToken)
    {
        var capability = GetCapabilities();
        if (!capability.Available || capability.ExecutablePath is null)
            return ToolResponse<SimulationBackendResult>.Fail("ngspice is unavailable.", "SIMULATOR_NOT_FOUND", capability.Message);

        Directory.CreateDirectory(outputDirectory);
        var circuitPath = Path.Combine(outputDirectory, "circuit.cir");
        var logPath = Path.Combine(outputDirectory, "simulator.log");
        var cleanCircuit = Regex.Replace(circuit, @"(?im)^\s*\.end\s*$", string.Empty);
        await File.WriteAllTextAsync(circuitPath, cleanCircuit.TrimEnd() + Environment.NewLine + analysisControl + Environment.NewLine + ".end" + Environment.NewLine, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        CommandExecutionResult execution;
        try { execution = await _runner.RunAsync(capability.ExecutablePath, new[] { "-b", "-o", logPath, circuitPath }, outputDirectory, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return ToolResponse<SimulationBackendResult>.Fail("ngspice timed out.", "SIMULATION_TIMEOUT"); }

        var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, cancellationToken) : execution.StandardOutput + execution.StandardError;
        if (execution.ExitCode != 0)
        {
            var convergence = log.Contains("convergence", StringComparison.OrdinalIgnoreCase) || log.Contains("timestep too small", StringComparison.OrdinalIgnoreCase);
            return ToolResponse<SimulationBackendResult>.Fail("ngspice execution failed.", convergence ? "SIMULATION_CONVERGENCE_FAILED" : "SIMULATION_EXECUTION_FAILED", log);
        }
        return ToolResponse<SimulationBackendResult>.Ok("ngspice completed.", new(execution.ExitCode, circuitPath, logPath, Path.Combine(outputDirectory, "vectors.dat")));
    }
}

public sealed class SimulationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ProjectDiscoveryService _projects;
    private readonly TestSpecService _testSpecs;
    private readonly ISimulationBackend _backend;
    public SimulationService(ProjectDiscoveryService projects, TestSpecService testSpecs, ISimulationBackend backend)
    { _projects = projects; _testSpecs = testSpecs; _backend = backend; }

    public SimulationCapabilities GetCapabilities() => _backend.GetCapabilities();
    public ToolResponse<SimulationValidationResult> Validate(string projectPath, string? testId = null)
    {
        var loaded = _testSpecs.LoadSimulationTests(projectPath, testId);
        if (!loaded.Success || loaded.Data is null)
            return ToolResponse<SimulationValidationResult>.Fail(loaded.Summary, loaded.Error?.Code ?? "TEST_SPEC_INVALID", loaded.Error?.Message);
        if (loaded.Data.Tests.Count == 0)
            return ToolResponse<SimulationValidationResult>.Fail("No simulation tests were found.", "SIMULATION_TESTS_NOT_FOUND");
        var diagnostics = new List<TestSpecDiagnostic>();
        foreach (var item in loaded.Data.Tests)
            diagnostics.AddRange(ValidateTest(loaded.Data.ProjectRoot, item));
        var result = new SimulationValidationResult(loaded.Data.ProjectRoot, loaded.Data.Tests.Count, diagnostics.Count == 0, diagnostics);
        return diagnostics.Count == 0
            ? ToolResponse<SimulationValidationResult>.Ok($"Validated {loaded.Data.Tests.Count} simulation test(s).", result)
            : ToolResponse<SimulationValidationResult>.Fail("Simulation tests are invalid.", diagnostics[0].Code, diagnostics[0].Message, result);
    }

    public async Task<ToolResponse<SimulationRunResult>> RunAsync(string projectPath, string? testId = null, CancellationToken cancellationToken = default)
        => await RunParameterizedAsync(projectPath, testId, null, cancellationToken);

    public async Task<ToolResponse<SimulationSweepResult>> RunSweepAsync(string projectPath, string testId, string kind, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
    {
        var key = kind switch { "battery" => "BATTERY_V", "tolerance" => "TOLERANCE_SCALE", "noise" => "NOISE_V", _ => null };
        if (key is null || values.Count == 0 || values.Any(value => !double.IsFinite(value)))
            return ToolResponse<SimulationSweepResult>.Fail("Sweep kind or values are invalid.", "SIMULATION_SWEEP_INVALID");
        var scenarios = new List<SimulationSweepScenario>();
        foreach (var value in values)
        {
            var run = await RunParameterizedAsync(projectPath, testId, new Dictionary<string,double> { [key] = value }, cancellationToken);
            if (!run.Success || run.Data is null) return ToolResponse<SimulationSweepResult>.Fail(run.Summary, run.Error?.Code ?? "SIMULATION_EXECUTION_FAILED", run.Error?.Message);
            scenarios.Add(new(value, run.Data.Passed, run.Data.RunId, run.Data.OutputDirectory));
        }
        return ToolResponse<SimulationSweepResult>.Ok("Completed deterministic simulation sweep.", new(kind, key, scenarios.All(s=>s.Passed), scenarios));
    }

    private async Task<ToolResponse<SimulationRunResult>> RunParameterizedAsync(string projectPath, string? testId, IReadOnlyDictionary<string,double>? parameters, CancellationToken cancellationToken)
    {
        var validation = Validate(projectPath, testId);
        if (!validation.Success) return ToolResponse<SimulationRunResult>.Fail(validation.Summary, validation.Error?.Code ?? "TEST_SPEC_INVALID", validation.Error?.Message);
        var loaded = _testSpecs.LoadSimulationTests(projectPath, testId).Data!;
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
        var runRoot = Path.Combine(loaded.ProjectRoot, ".pcbhelper", "simulations", runId);
        Directory.CreateDirectory(runRoot);
        var results = new List<SimulationTestResult>();
        foreach (var item in loaded.Tests)
        {
            var directory = Path.Combine(runRoot, SafeName(item.Test.Id));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "normalized-test.json"), JsonSerializer.Serialize(item.Test, JsonOptions), cancellationToken);
            var circuitPath = ResolveContained(loaded.ProjectRoot, item.Test.Circuit!.Path!);
            var circuit = await File.ReadAllTextAsync(circuitPath, cancellationToken);
            if (parameters is not null)
                foreach (var parameter in parameters)
                    circuit = circuit.Replace("{{" + parameter.Key + "}}", parameter.Value.ToString("G17", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (Regex.IsMatch(circuit, @"\{\{[A-Z0-9_]+\}\}"))
                return ToolResponse<SimulationRunResult>.Fail("Simulation circuit contains unresolved sweep parameters.", "SIMULATION_SWEEP_PARAMETER_MISSING");
            var vectors = BuildVectors(item.Test);
            var control = BuildControl(item.Test, vectors);
            var execution = await _backend.RunAsync(circuit, control, directory, cancellationToken);
            if (!execution.Success || execution.Data is null)
                return ToolResponse<SimulationRunResult>.Fail(execution.Summary, execution.Error?.Code ?? "SIMULATION_EXECUTION_FAILED", execution.Error?.Message);
            var parsed = ParseVectors(execution.Data.VectorPath, vectors.Count);
            if (parsed is null)
                return ToolResponse<SimulationRunResult>.Fail("ngspice did not produce valid numeric vectors.", "SIMULATION_OUTPUT_INVALID", execution.Data.VectorPath);
            ToolResponse<IReadOnlyList<TestMeasurementResult>> measurements;
            try { measurements = Measure(item.Test, parsed.Value.X, parsed.Value.Series); }
            catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException)
            { return ToolResponse<SimulationRunResult>.Fail("Simulation measurements could not be derived.", "SIMULATION_OUTPUT_INVALID", exception.Message); }
            if (!measurements.Success || measurements.Data is null)
                return ToolResponse<SimulationRunResult>.Fail(measurements.Summary, measurements.Error?.Code ?? "SIMULATION_OUTPUT_INVALID", measurements.Error?.Message);
            var evaluation = _testSpecs.EvaluateTest(item, measurements.Data);
            await File.WriteAllTextAsync(Path.Combine(directory, "measurements.json"), JsonSerializer.Serialize(new TestMeasurementResultDocument(measurements.Data), JsonOptions), cancellationToken);
            results.Add(new(item.Test.Id, evaluation.Passed, measurements.Data, evaluation.Assertions, execution.Data.CircuitPath, execution.Data.LogPath));
        }
        var passed = results.All(static result => result.Passed);
        var report = new SimulationRunResult(runId, runRoot, passed, results.Count, results.Count(static x => x.Passed), results.Count(static x => !x.Passed), results);
        var reportPath = Path.Combine(runRoot, "report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return ToolResponse<SimulationRunResult>.Ok(passed ? "All simulation assertions passed." : "One or more simulation assertions failed.", report);
    }

    public ToolResponse<SimulationRunResult> GetReport(string projectPath, string runId)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null) return ToolResponse<SimulationRunResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        if (!Regex.IsMatch(runId, "^[A-Za-z0-9_-]+$")) return ToolResponse<SimulationRunResult>.Fail("Invalid simulation run id.", "SIMULATION_RUN_NOT_FOUND");
        var path = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "simulations", runId, "report.json");
        if (!File.Exists(path)) return ToolResponse<SimulationRunResult>.Fail("Simulation report was not found.", "SIMULATION_RUN_NOT_FOUND");
        var report = JsonSerializer.Deserialize<SimulationRunResult>(File.ReadAllText(path), JsonOptions);
        return report is null ? ToolResponse<SimulationRunResult>.Fail("Simulation report is invalid.", "SIMULATION_OUTPUT_INVALID") : ToolResponse<SimulationRunResult>.Ok("Loaded simulation report.", report);
    }

    private static IReadOnlyList<TestSpecDiagnostic> ValidateTest(string root, LoadedTestCase item)
    {
        var t = item.Test; var errors = new List<TestSpecDiagnostic>();
        if (t.Circuit?.Source != "spice-file" || string.IsNullOrWhiteSpace(t.Circuit.Path)) errors.Add(D("TEST_SPEC_INVALID", "Simulation circuit must use spice-file with a path."));
        else try { var path = ResolveContained(root, t.Circuit.Path); if (!File.Exists(path)) errors.Add(D("SIMULATION_MODEL_UNAVAILABLE", $"Circuit file was not found: {t.Circuit.Path}")); } catch { errors.Add(D("PROJECT_SCOPE_VIOLATION", "Circuit path must remain inside the project.")); }
        if (t.Analysis is null) errors.Add(D("TEST_SPEC_INVALID", "Simulation analysis is required."));
        else if (t.Type == "simulation.ac" && (!(t.Analysis.StartHz > 0) || !(t.Analysis.StopHz > t.Analysis.StartHz) || t.Analysis.PointsPerDecade is < 1)) errors.Add(D("TEST_SPEC_INVALID", "AC analysis requires valid startHz, stopHz, and pointsPerDecade."));
        else if (t.Type == "simulation.tran" && (!(t.Analysis.StepSeconds > 0) || !(t.Analysis.StopSeconds > t.Analysis.StepSeconds))) errors.Add(D("TEST_SPEC_INVALID", "Transient analysis requires valid stepSeconds and stopSeconds."));
        foreach (var s in t.Stimuli) if (!new[] { "ac-voltage", "dc-voltage", "pulse-voltage" }.Contains(s.Kind) || !SafeToken(s.PositiveNet) || !SafeToken(s.NegativeNet) || !SafeToken(s.Name)) errors.Add(D("TEST_SPEC_INVALID", $"Invalid stimulus: {s.Name}."));
        foreach (var m in t.Measurements)
        {
            if (!MeasurementUnits.TryGetValue(m.Kind, out var unit)) errors.Add(D("TEST_MEASUREMENT_UNSUPPORTED", $"Unsupported measurement: {m.Kind}."));
            else if (!string.IsNullOrWhiteSpace(m.Unit) && !m.Unit.Equals(unit, StringComparison.OrdinalIgnoreCase)) errors.Add(D("TEST_SPEC_INVALID", $"{m.Kind} requires unit {unit}."));
            if (new[] { m.Net, m.InputNet, m.OutputNet, m.Source }.Where(static x => x is not null).Any(x => !SafeToken(x!))) errors.Add(D("TEST_SPEC_INVALID", $"Unsafe net or source in measurement {m.Name}."));
        }
        return errors;
        TestSpecDiagnostic D(string code, string message) => new(item.File, code, message, t.Id);
    }

    private static readonly Dictionary<string, string> MeasurementUnits = new(StringComparer.OrdinalIgnoreCase)
    { ["nodeVoltage"]="V", ["branchCurrent"]="A", ["gainDbAt"]="dB", ["peakFrequency"]="Hz", ["cutoffFrequency"]="Hz", ["minVoltage"]="V", ["maxVoltage"]="V", ["peakToPeak"]="V", ["settlingTime"]="s" };
    private static bool SafeToken(string value) => value == "0" || Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_.:+-]*$");
    private static string SafeName(string value) => Regex.Replace(value, "[^A-Za-z0-9_-]", "_");
    private static string ResolveContained(string root, string relative)
    { if (Path.IsPathRooted(relative)) throw new InvalidOperationException(); var full = Path.GetFullPath(Path.Combine(root, relative)); var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException(); return full; }

    private static List<string> BuildVectors(TestCaseSpec test)
    {
        var list = new List<string>();
        foreach (var m in test.Measurements)
        {
            string vector = m.Kind switch
            {
                "branchCurrent" => $"i({m.Source})",
                "gainDbAt" => $"db(v({m.OutputNet})/v({m.InputNet}))",
                "peakFrequency" or "cutoffFrequency" => $"db(v({m.Net}))",
                _ => $"v({m.Net})"
            };
            if (!list.Contains(vector, StringComparer.OrdinalIgnoreCase)) list.Add(vector);
        }
        return list;
    }

    private static string BuildControl(TestCaseSpec t, IReadOnlyList<string> vectors)
    {
        var sb = new StringBuilder();
        foreach (var s in t.Stimuli)
        {
            sb.Append('V').Append(s.Name).Append(' ').Append(s.PositiveNet).Append(' ').Append(s.NegativeNet).Append(' ');
            sb.Append(s.Kind switch { "ac-voltage" => $"AC {F(s.AmplitudeV ?? 1)}", "dc-voltage" => $"DC {F(s.DcV ?? 0)}", _ => $"PULSE({F(s.InitialV ?? 0)} {F(s.PulsedV ?? 1)} 0 1n 1n {F(s.PulseWidthSeconds ?? 0.001)} {F(s.PeriodSeconds ?? 0.002)})" }).AppendLine();
        }
        sb.AppendLine(".control").AppendLine("set wr_vecnames").AppendLine("set wr_singlescale");
        if (t.Type == "simulation.ac") sb.AppendLine($"ac dec {t.Analysis!.PointsPerDecade} {F(t.Analysis.StartHz!.Value)} {F(t.Analysis.StopHz!.Value)}");
        else if (t.Type == "simulation.tran") sb.AppendLine($"tran {F(t.Analysis!.StepSeconds!.Value)} {F(t.Analysis.StopSeconds!.Value)}");
        else sb.AppendLine("op");
        sb.Append("wrdata vectors.dat"); foreach (var v in vectors) sb.Append(' ').Append(v); sb.AppendLine().AppendLine("quit").AppendLine(".endc");
        return sb.ToString();
        static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static (double[] X, double[][] Series)? ParseVectors(string path, int count)
    {
        if (!File.Exists(path)) return null; var rows = new List<double[]>();
        foreach (var line in File.ReadLines(path))
        {
            var values = Regex.Matches(line, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length >= count + 1) rows.Add(values);
        }
        if (rows.Count == 0) return null;
        return (rows.Select(r => r[0]).ToArray(), Enumerable.Range(0, count).Select(i => rows.Select(r => r[i + 1]).ToArray()).ToArray());
    }

    private static ToolResponse<IReadOnlyList<TestMeasurementResult>> Measure(TestCaseSpec test, double[] x, double[][] series)
    {
        var vectors = BuildVectors(test); var output = new List<TestMeasurementResult>();
        foreach (var m in test.Measurements)
        {
            var expression = m.Kind == "branchCurrent" ? $"i({m.Source})" : m.Kind == "gainDbAt" ? $"db(v({m.OutputNet})/v({m.InputNet}))"
                : m.Kind is "peakFrequency" or "cutoffFrequency" ? $"db(v({m.Net}))" : $"v({m.Net})";
            var y = series[vectors.FindIndex(v => v.Equals(expression, StringComparison.OrdinalIgnoreCase))]; double value;
            switch (m.Kind)
            {
                case "nodeVoltage": value = y[^1]; break;
                case "branchCurrent": value = y[^1]; break;
                case "gainDbAt": value = Interpolate(x, y, m.FrequencyHz ?? 0); break;
                case "peakFrequency": value = x[Array.IndexOf(y, y.Max())]; break;
                case "cutoffFrequency": var target = y.Max() - 3; value = x[Enumerable.Range(0, y.Length).MinBy(i => Math.Abs(y[i] - target))]; break;
                case "minVoltage": value = y.Min(); break;
                case "maxVoltage": value = y.Max(); break;
                case "peakToPeak": value = y.Max() - y.Min(); break;
                case "settlingTime":
                    var final = m.TargetValue ?? y[^1]; var tolerance = m.MeasurementTolerance ?? Math.Max(Math.Abs(final) * .02, 1e-9); var index = Enumerable.Range(0, y.Length).FirstOrDefault(i => Enumerable.Range(i, y.Length-i).All(j => Math.Abs(y[j]-final) <= tolerance), -1);
                    if (index < 0) return ToolResponse<IReadOnlyList<TestMeasurementResult>>.Fail($"{m.Name} did not settle.", "SIMULATION_OUTPUT_INVALID"); value = x[index]; break;
                default: return ToolResponse<IReadOnlyList<TestMeasurementResult>>.Fail($"Unsupported measurement {m.Kind}.", "TEST_MEASUREMENT_UNSUPPORTED");
            }
            output.Add(new(m.Name, value, MeasurementUnits[m.Kind], "ngspice"));
        }
        return ToolResponse<IReadOnlyList<TestMeasurementResult>>.Ok("Measured simulation outputs.", output);
    }
    private static double Interpolate(double[] x, double[] y, double at)
    { if (at < x[0] || at > x[^1]) throw new InvalidOperationException("Measurement point is outside the analysis range."); var hi = Array.FindIndex(x, value => value >= at); if (hi <= 0) return y[0]; var ratio = (at-x[hi-1])/(x[hi]-x[hi-1]); return y[hi-1]+ratio*(y[hi]-y[hi-1]); }
}

public sealed record SimulationCapabilities(bool Available, string Backend, string? ExecutablePath, string Source, string? Message);
public sealed record SimulationBackendResult(int ExitCode, string CircuitPath, string LogPath, string VectorPath);
public sealed record SimulationValidationResult(string ProjectRoot, int TestCount, bool Valid, IReadOnlyList<TestSpecDiagnostic> Diagnostics);
public sealed record SimulationTestResult(string TestId, bool Passed, IReadOnlyList<TestMeasurementResult> Measurements, IReadOnlyList<TestAssertionEvaluation> Assertions, string CircuitPath, string LogPath);
public sealed record SimulationRunResult(string RunId, string OutputDirectory, bool Passed, int TestCount, int PassedTestCount, int FailedTestCount, IReadOnlyList<SimulationTestResult> Tests);
public sealed record SimulationSweepScenario(double Value,bool Passed,string RunId,string OutputDirectory);
public sealed record SimulationSweepResult(string Kind,string Parameter,bool Passed,IReadOnlyList<SimulationSweepScenario> Scenarios);
