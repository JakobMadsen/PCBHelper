using PCBHelper.Core;
using System.Globalization;

namespace PCBHelper.Core.Tests;

public sealed class TestSpecServiceTests
{
    [Fact]
    public void Missing_Test_Directory_Returns_Empty_List()
    {
        using var fixture = CreateProject();
        var service = new TestSpecService(new ProjectDiscoveryService());

        var result = service.ListTests(fixture.Path);

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Files);
    }

    [Fact]
    public void Valid_Spec_Parses_And_Validates()
    {
        using var fixture = CreateProject();
        WriteSpec(fixture.Path, ValidSpec());
        var service = new TestSpecService(new ProjectDiscoveryService());

        var list = service.ListTests(fixture.Path);
        var validation = service.ValidateTests(fixture.Path);

        Assert.True(list.Success);
        Assert.Single(list.Data!.Files);
        Assert.True(validation.Success);
        Assert.Equal(2, validation.Data!.TestCount);
    }

    [Fact]
    public void Invalid_Json_Returns_Stable_Error()
    {
        using var fixture = CreateProject();
        WriteSpec(fixture.Path, "{ not json");
        var service = new TestSpecService(new ProjectDiscoveryService());

        var result = service.ValidateTests(fixture.Path);

        Assert.False(result.Success);
        Assert.Equal("TEST_SPEC_INVALID", result.Error?.Code);
    }

    [Fact]
    public void Unknown_Measurement_In_Spec_Returns_Stable_Error()
    {
        using var fixture = CreateProject();
        WriteSpec(fixture.Path, """
        {
          "version": 1,
          "tests": [
            {
              "id": "bad",
              "type": "simulation.ac",
              "measurements": [],
              "asserts": [
                {
                  "measurement": "missing",
                  "lessThan": 1
                }
              ]
            }
          ]
        }
        """);
        var service = new TestSpecService(new ProjectDiscoveryService());

        var result = service.ValidateTests(fixture.Path);

        Assert.False(result.Success);
        Assert.Equal("TEST_MEASUREMENT_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public void Assertion_Operators_Pass_And_Fail_Deterministically()
    {
        using var fixture = CreateProject();
        WriteSpec(fixture.Path, ValidSpec());
        var passResults = WriteResults(fixture.Path, "pass.json", gain100: -0.5, gain10k: -24.2, bias: 1.67, exact: 2.02, approx: 4.95);
        var failResults = WriteResults(fixture.Path, "fail.json", gain100: -4.0, gain10k: -12.0, bias: 1.5, exact: 2.2, approx: 4.7);
        var service = new TestSpecService(new ProjectDiscoveryService());

        var pass = service.EvaluateResults(fixture.Path, passResults);
        var fail = service.EvaluateResults(fixture.Path, failResults);

        Assert.True(pass.Success);
        Assert.True(pass.Data!.Passed);
        Assert.Equal(5, pass.Data.PassedAssertionCount);
        Assert.False(fail.Success);
        Assert.Equal("TEST_ASSERTIONS_FAILED", fail.Error?.Code);
        Assert.False(fail.Data!.Passed);
        Assert.Equal(5, fail.Data.FailedAssertionCount);
    }

    [Fact]
    public void Missing_Result_Measurement_Returns_Stable_Error()
    {
        using var fixture = CreateProject();
        WriteSpec(fixture.Path, ValidSpec());
        var results = Path.Combine(fixture.Path, "missing-result.json");
        File.WriteAllText(results, """
        {
          "measurements": []
        }
        """);
        var service = new TestSpecService(new ProjectDiscoveryService());

        var result = service.EvaluateResults(fixture.Path, results);

        Assert.False(result.Success);
        Assert.Equal("TEST_MEASUREMENT_NOT_FOUND", result.Error?.Code);
    }

    private static TempDirectory CreateProject()
    {
        var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "test-project.kicad_pro"), """
        {
          "project": {
            "name": "test-project"
          }
        }
        """);
        File.WriteAllText(Path.Combine(temp.Path, "test-project.kicad_sch"), "(kicad_sch)");
        File.WriteAllText(Path.Combine(temp.Path, "test-project.kicad_pcb"), "(kicad_pcb)");
        return temp;
    }

    private static void WriteSpec(string projectPath, string text)
    {
        var testsDirectory = Path.Combine(projectPath, ".pcbhelper", "tests");
        Directory.CreateDirectory(testsDirectory);
        File.WriteAllText(Path.Combine(testsDirectory, "filter.json"), text);
    }

    private static string WriteResults(string projectPath, string fileName, double gain100, double gain10k, double bias, double exact, double approx)
    {
        var path = Path.Combine(projectPath, fileName);
        File.WriteAllText(path, $$"""
        {
          "measurements": [
            { "name": "gain_at_100hz_db", "value": {{Format(gain100)}}, "unit": "dB", "source": "test" },
            { "name": "gain_at_10khz_db", "value": {{Format(gain10k)}}, "unit": "dB", "source": "test" },
            { "name": "bias_v", "value": {{Format(bias)}}, "unit": "V", "source": "test" },
            { "name": "exact_v", "value": {{Format(exact)}}, "unit": "V", "source": "test" },
            { "name": "approx_v", "value": {{Format(approx)}}, "unit": "V", "source": "test" }
          ]
        }
        """);
        return path;

        static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    private static string ValidSpec()
    {
        return """
        {
          "version": 1,
          "tests": [
            {
              "id": "filter_response",
              "type": "simulation.ac",
              "measurements": [
                { "name": "gain_at_100hz_db", "kind": "gainDb", "unit": "dB" },
                { "name": "gain_at_10khz_db", "kind": "gainDb", "unit": "dB" },
                { "name": "bias_v", "kind": "voltage", "unit": "V" }
              ],
              "asserts": [
                { "measurement": "gain_at_100hz_db", "between": [ -3.0, 0.5 ] },
                { "measurement": "gain_at_10khz_db", "lessThan": -20.0 },
                { "measurement": "bias_v", "greaterThan": 1.6 }
              ]
            },
            {
              "id": "tolerances",
              "type": "simulation.op",
              "measurements": [
                { "name": "exact_v", "kind": "voltage", "unit": "V" },
                { "name": "approx_v", "kind": "voltage", "unit": "V" }
              ],
              "asserts": [
                { "measurement": "exact_v", "equals": 2.0, "tolerance": 0.05 },
                { "measurement": "approx_v", "approximately": 5.0, "tolerance": 0.1 }
              ]
            }
          ]
        }
        """;
    }
}
