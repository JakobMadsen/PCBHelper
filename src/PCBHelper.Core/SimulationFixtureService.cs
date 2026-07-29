using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed class SimulationFixtureService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ProjectDiscoveryService _projects;

    public SimulationFixtureService(ProjectDiscoveryService projects)
    {
        _projects = projects;
    }

    public ToolResponse<SimulationFixtureResult> SetFixture(string projectPath, JsonElement fixture, bool dryRun)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<SimulationFixtureResult>.Fail(
                project.Summary,
                project.Error?.Code ?? "PROJECT_NOT_FOUND",
                project.Error?.Message);
        }

        try
        {
            var id = RequiredSafeToken(fixture, "id");
            var elements = RequiredArray(fixture, "elements");
            var tests = RequiredArray(fixture, "tests");
            if (elements.GetArrayLength() == 0)
            {
                return Invalid("Simulation fixture requires at least one circuit element.");
            }
            if (tests.GetArrayLength() == 0)
            {
                return Invalid("Simulation fixture requires at least one test.");
            }

            var circuit = CompileCircuit(id, elements);
            var circuitRelativePath = Path.Combine("simulation", id + ".cir").Replace('\\', '/');
            var document = CompileTestDocument(tests, circuitRelativePath);
            var testJson = document.ToJsonString(JsonOptions) + Environment.NewLine;
            var circuitRelativeFile = Path.Combine("simulation", id + ".cir");
            var testRelativeFile = Path.Combine(".pcbhelper", "tests", id + ".json");

            if (!dryRun)
            {
                WriteProjectFile(project.Data.ProjectRoot, circuitRelativeFile, circuit);
                WriteProjectFile(project.Data.ProjectRoot, testRelativeFile, testJson);

                var validation = new TestSpecService(_projects).ValidateTests(project.Data.ProjectRoot);
                if (!validation.Success)
                {
                    return ToolResponse<SimulationFixtureResult>.Fail(
                        "Compiled simulation fixture did not pass declarative test validation.",
                        validation.Error?.Code ?? "TEST_SPEC_INVALID",
                        validation.Error?.Message);
                }
            }

            return ToolResponse<SimulationFixtureResult>.Ok(
                dryRun ? $"Validated structured simulation fixture {id}." : $"Wrote structured simulation fixture {id}.",
                new SimulationFixtureResult(id, circuitRelativePath, testRelativeFile.Replace('\\', '/'), elements.GetArrayLength(), tests.GetArrayLength()));
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception.Message);
        }
        catch (JsonException exception)
        {
            return Invalid(exception.Message);
        }
        catch (IOException exception)
        {
            return ToolResponse<SimulationFixtureResult>.Fail("Could not write simulation fixture.", "SIMULATION_FIXTURE_WRITE_FAILED", exception.Message);
        }
    }

    private static string CompileCircuit(string id, JsonElement elements)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder()
            .Append("* PCBHelper structured simulation fixture: ").AppendLine(id);

        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Every simulation element must be an object.");
            }

            var kind = RequiredString(element, "kind");
            var reference = RequiredSafeToken(element, "reference");
            if (!references.Add(reference))
            {
                throw new InvalidOperationException($"Duplicate simulation element reference: {reference}.");
            }

            switch (kind)
            {
                case "resistor":
                    RequireOnly(element, "kind", "reference", "positiveNet", "negativeNet", "resistanceOhms");
                    output.Append('R').Append(reference).Append(' ')
                        .Append(RequiredNet(element, "positiveNet")).Append(' ')
                        .Append(RequiredNet(element, "negativeNet")).Append(' ')
                        .AppendLine(PositiveNumber(element, "resistanceOhms"));
                    break;
                case "capacitor":
                    RequireOnly(element, "kind", "reference", "positiveNet", "negativeNet", "capacitanceFarads");
                    output.Append('C').Append(reference).Append(' ')
                        .Append(RequiredNet(element, "positiveNet")).Append(' ')
                        .Append(RequiredNet(element, "negativeNet")).Append(' ')
                        .AppendLine(PositiveNumber(element, "capacitanceFarads"));
                    break;
                case "ideal-opamp":
                case "ideal-comparator":
                    RequireOnly(element, "kind", "reference", "plusNet", "minusNet", "outputNet", "positiveSupplyNet", "negativeSupplyNet", "openLoopGain", "outputHeadroomV");
                    var gain = PositiveNumber(element, "openLoopGain");
                    var headroom = NonNegativeNumber(element, "outputHeadroomV");
                    var plus = RequiredNet(element, "plusNet");
                    var minus = RequiredNet(element, "minusNet");
                    var outputNet = RequiredNet(element, "outputNet");
                    var positiveSupply = RequiredNet(element, "positiveSupplyNet");
                    var negativeSupply = RequiredNet(element, "negativeSupplyNet");
                    output.Append("B").Append(reference).Append(' ')
                        .Append(outputNet).Append(" 0 V=(V(").Append(positiveSupply).Append(")+V(")
                        .Append(negativeSupply).Append("))/2+((V(").Append(positiveSupply).Append(")-V(")
                        .Append(negativeSupply).Append("))/2-").Append(headroom).Append(")*tanh(")
                        .Append(gain).Append("*(V(").Append(plus).Append(")-V(").Append(minus)
                        .Append("))/((V(").Append(positiveSupply).Append(")-V(").Append(negativeSupply)
                        .Append("))/2-").Append(headroom).AppendLine("))");
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported simulation element kind: {kind}.");
            }
        }

        return output.ToString();
    }

    private static JsonObject CompileTestDocument(JsonElement tests, string circuitRelativePath)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var array = new JsonArray();
        foreach (var test in tests.EnumerateArray())
        {
            if (test.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Every simulation test must be an object.");
            }

            var id = RequiredSafeToken(test, "id");
            if (!ids.Add(id))
            {
                throw new InvalidOperationException($"Duplicate simulation test id: {id}.");
            }
            var type = RequiredString(test, "type");
            if (type is not ("simulation.op" or "simulation.ac" or "simulation.tran"))
            {
                throw new InvalidOperationException($"Unsupported structured simulation test type: {type}.");
            }

            var node = JsonNode.Parse(test.GetRawText())?.AsObject()
                ?? throw new InvalidOperationException($"Simulation test {id} is invalid.");
            node["circuit"] = new JsonObject
            {
                ["source"] = "spice-file",
                ["path"] = circuitRelativePath
            };
            array.Add(node);
        }

        return new JsonObject
        {
            ["version"] = 1,
            ["tests"] = array
        };
    }

    private static void WriteProjectFile(string root, string relativePath, string content)
    {
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulation fixture target must remain inside the project.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }

    private static JsonElement RequiredArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{name} must be an array.");
        }
        return value;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"{name} is required.");
        }
        return value.GetString()!;
    }

    private static string RequiredSafeToken(JsonElement element, string name)
    {
        var value = RequiredString(element, name);
        if (!Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_-]*$"))
        {
            throw new InvalidOperationException($"{name} must be a safe identifier.");
        }
        return value;
    }

    private static string RequiredNet(JsonElement element, string name)
    {
        var value = RequiredString(element, name);
        if (value != "0" && !Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_.:+-]*$"))
        {
            throw new InvalidOperationException($"{name} must be a safe net name.");
        }
        return value;
    }

    private static string PositiveNumber(JsonElement element, string name)
    {
        var value = Number(element, name);
        if (!(value > 0))
        {
            throw new InvalidOperationException($"{name} must be greater than zero.");
        }
        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static string NonNegativeNumber(JsonElement element, string name)
    {
        var value = Number(element, name);
        if (value < 0)
        {
            throw new InvalidOperationException($"{name} must be zero or greater.");
        }
        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || !property.TryGetDouble(out var value) || !double.IsFinite(value))
        {
            throw new InvalidOperationException($"{name} must be a finite number.");
        }
        return value;
    }

    private static void RequireOnly(JsonElement element, params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidOperationException($"Unsupported {RequiredString(element, "kind")} element property: {property.Name}.");
            }
        }
    }

    private static ToolResponse<SimulationFixtureResult> Invalid(string message) =>
        ToolResponse<SimulationFixtureResult>.Fail("Structured simulation fixture is invalid.", "SIMULATION_FIXTURE_INVALID", message);
}

public sealed record SimulationFixtureResult(
    string FixtureId,
    string CircuitFile,
    string TestSpecFile,
    int ElementCount,
    int TestCount);
