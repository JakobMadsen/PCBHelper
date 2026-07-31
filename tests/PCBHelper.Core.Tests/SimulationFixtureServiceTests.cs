using System.Text.Json;
using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class SimulationFixtureServiceTests
{
    [Fact]
    public void SetFixture_Writes_Only_Compiled_Circuit_And_Declarative_Test()
    {
        using var project = TestProject.Create();
        using var document = JsonDocument.Parse(Fixture());
        var service = new SimulationFixtureService(new ProjectDiscoveryService());

        var result = service.SetFixture(project.Path, document.RootElement, dryRun: false);

        Assert.True(result.Success, result.Error?.Message);
        var circuit = File.ReadAllText(Path.Combine(project.Path, "simulation", "filter.cir"));
        Assert.Contains("RR1 IN OUT 1000", circuit, StringComparison.Ordinal);
        Assert.Contains("CC1 OUT 0 9.9999999999999995E-07", circuit, StringComparison.Ordinal);
        var spec = File.ReadAllText(Path.Combine(project.Path, ".pcbhelper", "tests", "filter.json"));
        Assert.Contains("\"source\": \"spice-file\"", spec, StringComparison.Ordinal);
        Assert.Contains("\"path\": \"simulation/filter.cir\"", spec, StringComparison.Ordinal);
        Assert.True(new TestSpecService(new ProjectDiscoveryService()).ValidateTests(project.Path).Success);
    }

    [Fact]
    public void SetFixture_Rejects_Raw_And_Unknown_Circuit_Properties()
    {
        using var project = TestProject.Create();
        using var document = JsonDocument.Parse(Fixture().Replace(
            "\"resistanceOhms\":1000",
            "\"resistanceOhms\":1000,\"raw\":\".include arbitrary\"",
            StringComparison.Ordinal));

        var result = new SimulationFixtureService(new ProjectDiscoveryService())
            .SetFixture(project.Path, document.RootElement, dryRun: false);

        Assert.False(result.Success);
        Assert.Equal("SIMULATION_FIXTURE_INVALID", result.Error?.Code);
        Assert.False(Directory.Exists(Path.Combine(project.Path, "simulation")));
    }

    [Fact]
    public void SetFixture_Compiles_Supply_Limited_Ideal_Comparator()
    {
        using var project = TestProject.Create();
        using var document = JsonDocument.Parse("""
        {
          "id":"comparator",
          "elements":[
            {"kind":"ideal-comparator","reference":"U1","plusNet":"IN","minusNet":"THRESH","outputNet":"OUT",
             "positiveSupplyNet":"VCC","negativeSupplyNet":"0","openLoopGain":1000000,"outputHeadroomV":0.05}
          ],
          "tests":[
            {
              "id":"switches",
              "type":"simulation.op",
              "stimuli":[
                {"name":"VCC","kind":"dc-voltage","positiveNet":"VCC","negativeNet":"0","dcV":5},
                {"name":"IN","kind":"dc-voltage","positiveNet":"IN","negativeNet":"0","dcV":3},
                {"name":"THRESH","kind":"dc-voltage","positiveNet":"THRESH","negativeNet":"0","dcV":2.5}
              ],
              "measurements":[{"name":"out","kind":"nodeVoltage","net":"OUT","unit":"V"}],
              "asserts":[{"measurement":"out","greaterThan":4.9}]
            }
          ]
        }
        """);

        var result = new SimulationFixtureService(new ProjectDiscoveryService())
            .SetFixture(project.Path, document.RootElement, dryRun: false);

        Assert.True(result.Success, result.Error?.Message);
        var circuit = File.ReadAllText(Path.Combine(project.Path, "simulation", "comparator.cir"));
        Assert.Contains("BU1 OUT 0 V=(V(VCC)+V(0))/2+((V(VCC)-V(0))/2-0.050000000000000003)*tanh(1000000*(V(IN)-V(THRESH))/((V(VCC)-V(0))/2-0.050000000000000003))", circuit, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignPlan_Previews_Simulation_Fixture_As_Two_Transactional_Files()
    {
        using var project = TestProject.Create();
        var plan = $$"""
        {
          "version":1,
          "goal":"Add evidence",
          "operations":[
            {"id":"fixture","type":"set-simulation-fixture","fixture":{{Fixture()}}}
          ],
          "engineeringGate":{"erc":"skip","drc":"skip","manufacturingValidation":"skip","simulationAssertions":"skip"}
        }
        """;

        var preview = PCBHelperRuntime.ForCli().Plans.Preview(project.Path, plan);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Equal(2, preview.Data!.ChangedFiles.Count);
        Assert.Contains(preview.Data.ChangedFiles, item => item.RelativePath.Replace('\\', '/') == "simulation/filter.cir");
        Assert.Contains(preview.Data.ChangedFiles, item => item.RelativePath.Replace('\\', '/') == ".pcbhelper/tests/filter.json");
        Assert.False(Directory.Exists(Path.Combine(project.Path, "simulation")));
    }

    private static string Fixture() => """
    {
      "id":"filter",
      "elements":[
        {"kind":"resistor","reference":"R1","positiveNet":"IN","negativeNet":"OUT","resistanceOhms":1000},
        {"kind":"capacitor","reference":"C1","positiveNet":"OUT","negativeNet":"0","capacitanceFarads":0.000001}
      ],
      "tests":[
        {
          "id":"gain",
          "type":"simulation.ac",
          "analysis":{"startHz":10,"stopHz":1000,"pointsPerDecade":10},
          "stimuli":[{"name":"IN","kind":"ac-voltage","positiveNet":"IN","negativeNet":"0","amplitudeV":1}],
          "measurements":[{"name":"gain","kind":"gainDbAt","inputNet":"IN","outputNet":"OUT","frequencyHz":100,"unit":"dB"}],
          "asserts":[{"measurement":"gain","between":[-3,0]}]
        }
      ]
    }
    """;

    private sealed class TestProject : IDisposable
    {
        public string Path { get; }

        private TestProject(string path)
        {
            Path = path;
        }

        public static TestProject Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcbhelper-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(System.IO.Path.Combine(path, "test.kicad_pro"), "{}");
            return new TestProject(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
