using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class SimulationServiceTests
{
    [Fact]
    public void Locator_Reports_Configured_Missing_Executable()
    {
        var locator = new NgspiceLocator(name => name == "NGSPICE" ? Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe") : null);
        var result = locator.Locate();
        Assert.False(result.Available);
        Assert.Equal("NGSPICE", result.Source);
    }

    [Fact]
    public void Validation_Rejects_Path_Traversal()
    {
        using var fixture = TestProject.Create();
        fixture.WriteSpec(Spec("../outside.cir"));
        var service = CreateService(fixture.Path, new FakeBackend());
        var result = service.Validate(fixture.Path);
        Assert.False(result.Success);
        Assert.Equal("PROJECT_SCOPE_VIOLATION", result.Error?.Code);
    }

    [Fact]
    public async Task Run_Parses_Measurements_And_Evaluates_Assertions()
    {
        using var fixture = TestProject.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Path, "simulation"));
        File.WriteAllText(Path.Combine(fixture.Path, "simulation", "filter.cir"), "R1 IN OUT 1k\nC1 OUT 0 1u\n");
        fixture.WriteSpec(Spec("simulation/filter.cir"));
        var service = CreateService(fixture.Path, new FakeBackend());
        var result = await service.RunAsync(fixture.Path);
        Assert.True(result.Success);
        Assert.True(result.Data!.Passed);
        Assert.Equal(-0.5, result.Data.Tests[0].Measurements[0].Value, 3);
        Assert.True(File.Exists(Path.Combine(result.Data.OutputDirectory, "report.json")));
    }

    [Fact]
    public async Task Sweep_Runs_Each_Constrained_Battery_Scenario()
    {
        using var fixture = TestProject.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Path, "simulation"));
        File.WriteAllText(Path.Combine(fixture.Path, "simulation", "filter.cir"), "VBAT VCC 0 {{BATTERY_V}}\nR1 IN OUT 1k\n");
        fixture.WriteSpec(Spec("simulation/filter.cir"));
        var result = await CreateService(fixture.Path, new FakeBackend()).RunSweepAsync(fixture.Path, "gain", "battery", new[] { 3.0, 4.5, 5.0 });
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.Data!.Scenarios.Count);
        Assert.All(result.Data.Scenarios, scenario => Assert.True(scenario.Passed));
    }

    private static SimulationService CreateService(string path, ISimulationBackend backend)
    {
        var projects = new ProjectDiscoveryService();
        return new SimulationService(projects, new TestSpecService(projects), backend);
    }

    private static string Spec(string circuit) => $$"""
    { "version": 1, "tests": [{ "id": "gain", "type": "simulation.ac",
      "circuit": { "source": "spice-file", "path": "{{circuit}}" },
      "analysis": { "startHz": 10, "stopHz": 1000, "pointsPerDecade": 10 },
      "stimuli": [{ "name": "IN", "kind": "ac-voltage", "positiveNet": "IN", "negativeNet": "0", "amplitudeV": 1 }],
      "measurements": [{ "name": "gain", "kind": "gainDbAt", "inputNet": "IN", "outputNet": "OUT", "frequencyHz": 100, "unit": "dB" }],
      "asserts": [{ "measurement": "gain", "between": [-1, 0] }] }] }
    """;

    private sealed class FakeBackend : ISimulationBackend
    {
        public SimulationCapabilities GetCapabilities() => new(true, "fake", "fake", "test", null);
        public Task<ToolResponse<SimulationBackendResult>> RunAsync(string circuit, string analysisControl, string outputDirectory, CancellationToken cancellationToken)
        {
            var vectors = Path.Combine(outputDirectory, "vectors.dat");
            File.WriteAllText(vectors, "frequency gain\n10 -0.1\n100 -0.5\n1000 -10\n");
            var circuitPath = Path.Combine(outputDirectory, "circuit.cir"); File.WriteAllText(circuitPath, circuit + analysisControl);
            var log = Path.Combine(outputDirectory, "simulator.log"); File.WriteAllText(log, "ok");
            return Task.FromResult(ToolResponse<SimulationBackendResult>.Ok("ok", new(0, circuitPath, log, vectors)));
        }
    }

    private sealed class TestProject : IDisposable
    {
        public string Path { get; }
        private TestProject(string path) => Path = path;
        public static TestProject Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcbhelper-sim-" + Guid.NewGuid());
            Directory.CreateDirectory(path); File.WriteAllText(System.IO.Path.Combine(path, "test.kicad_pro"), "{}"); return new(path);
        }
        public void WriteSpec(string json)
        { var dir = System.IO.Path.Combine(Path, ".pcbhelper", "tests"); Directory.CreateDirectory(dir); File.WriteAllText(System.IO.Path.Combine(dir, "test.json"), json); }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
