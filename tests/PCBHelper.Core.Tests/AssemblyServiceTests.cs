using System.IO.Compression;
using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class AssemblyServiceTests
{
    [Fact]
    public void InspectAssembly_Groups_Bom_And_Exports_Cpl_For_Smd()
    {
        using var fixture = CreateAssemblyFixture();
        var service = CreateService();

        var result = service.InspectAssembly(fixture.Path);

        Assert.True(result.Success);
        Assert.Equal(5, result.Data!.Components.Count);
        var resistorRow = Assert.Single(result.Data.BomRows, row => row.Value == "10k");
        Assert.Equal(2, resistorRow.Quantity);
        Assert.Equal("R1, R2", resistorRow.Designators);
        Assert.Equal("Yageo", resistorRow.Manufacturer);
        Assert.Equal("RC0603FR-0710KL", resistorRow.Mpn);
        Assert.Contains(result.Data.CplRows, row => row.Designator == "R1" && row.XMillimeters == 10 && row.RotationDegrees == 90);
        Assert.Contains(result.Data.CplRows, row => row.Designator == "U1" && row.Side == "back");
        Assert.DoesNotContain(result.Data.CplRows, row => row.Designator == "J1");
        Assert.DoesNotContain(result.Data.BomRows.SelectMany(row => row.Designators.Split(',', StringSplitOptions.TrimEntries)), designator => designator == "DNP1");
    }

    [Fact]
    public async Task ExportAssemblyBom_And_Cpl_Write_Csv_Files()
    {
        using var fixture = CreateAssemblyFixture();
        var service = CreateService();

        var bom = await service.ExportAssemblyBomAsync(fixture.Path);
        var cpl = await service.ExportCplAsync(fixture.Path);

        Assert.True(bom.Success);
        Assert.True(File.Exists(bom.Data!.OutputFile));
        Assert.Contains("Designators,Quantity,Value,Package", File.ReadAllText(bom.Data.OutputFile));
        Assert.Contains("\"R1, R2\",2,10k", File.ReadAllText(bom.Data.OutputFile));
        Assert.True(cpl.Success);
        Assert.True(File.Exists(cpl.Data!.OutputFile));
        Assert.Contains("Designator,Mid X,Mid Y,Layer,Rotation,Value,Package", File.ReadAllText(cpl.Data.OutputFile));
        Assert.Contains("U1,40,20,back,180", File.ReadAllText(cpl.Data.OutputFile));
    }

    [Fact]
    public void ValidateAssemblyPackage_Reports_Dnp_Tht_MissingPart_And_Orientation_Warnings()
    {
        using var fixture = CreateAssemblyFixture();
        var service = CreateService();

        var result = service.ValidateAssemblyPackage(fixture.Path);

        Assert.True(result.Success);
        Assert.True(result.Data!.Valid);
        Assert.Equal(0, result.Data.ErrorCount);
        Assert.Contains(result.Data.Diagnostics, diagnostic => diagnostic.Code == "ASSEMBLY_COMPONENT_EXCLUDED" && diagnostic.Reference == "DNP1");
        Assert.Contains(result.Data.Diagnostics, diagnostic => diagnostic.Code == "ASSEMBLY_THT_CPL_EXCLUDED" && diagnostic.Reference == "J1");
        Assert.Contains(result.Data.Diagnostics, diagnostic => diagnostic.Code == "ASSEMBLY_PART_NUMBER_MISSING" && diagnostic.Reference == "J1");
        Assert.Contains(result.Data.Diagnostics, diagnostic => diagnostic.Code == "ASSEMBLY_ORIENTATION_REVIEW" && diagnostic.Reference == "U1");
    }

    [Fact]
    public void ValidateAssemblyPackage_Fails_On_Duplicate_Reference()
    {
        using var fixture = CreateAssemblyFixture(duplicateReference: true);
        var service = CreateService();

        var result = service.ValidateAssemblyPackage(fixture.Path);

        Assert.True(result.Success);
        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Diagnostics, diagnostic => diagnostic.Code == "DUPLICATE_REFERENCE" && diagnostic.Reference == "R1");
    }

    [Fact]
    public async Task CreatePcbWayAssemblyPackage_Includes_Manufacturing_Assembly_And_Validation_Files()
    {
        using var fixture = CreateAssemblyFixture();
        var service = CreateService();

        var result = await service.CreatePcbWayAssemblyPackageAsync(fixture.Path);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.Data!.ZipPath));
        using var archive = ZipFile.OpenRead(result.Data.ZipPath);
        var names = archive.Entries.Select(static entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("manifest.json", names);
        Assert.Contains("assembly-validation.json", names);
        Assert.Contains("assembly-board-assembly-bom.csv", names);
        Assert.Contains("assembly-board-cpl.csv", names);
        Assert.Contains("fabrication.gbr", names);
        Assert.Contains("fabrication.drl", names);
    }

    private static AssemblyService CreateService()
    {
        using var fakeCli = new TempFile("kicad-cli.exe", deleteOnDispose: false);
        var locator = new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli.Path : null);
        var runner = new FakeCommandRunner();
        var projectDiscovery = new ProjectDiscoveryService();
        var doctor = new KiCadDoctorService(locator, runner);
        var export = new ExportService(projectDiscovery, locator, runner);
        return new AssemblyService(projectDiscovery, doctor, export);
    }

    private static TempDirectory CreateAssemblyFixture(bool duplicateReference = false)
    {
        var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "assembly-board.kicad_pro"), "{}");
        File.WriteAllText(Path.Combine(temp.Path, "assembly-board.kicad_pcb"), CreateBoardText(duplicateReference));
        File.WriteAllText(Path.Combine(temp.Path, "assembly-board.kicad_sch"), CreateSchematicText());
        return temp;
    }

    private static string CreateBoardText(bool duplicateReference)
    {
        var secondReference = duplicateReference ? "R1" : "R2";
        return $$"""
(kicad_pcb
  (net 0 "")
  (net 1 "N1")
  (footprint "Resistor_SMD:R_0603_1608Metric"
    (layer "F.Cu")
    (at 10 20 90)
    (property "Reference" "R1")
    (property "Value" "10k")
    (pad "1" smd roundrect (at -0.8 0) (size 0.8 0.8) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "N1"))
    (pad "2" smd roundrect (at 0.8 0) (size 0.8 0.8) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "N1"))
  )
  (footprint "Resistor_SMD:R_0603_1608Metric"
    (layer "F.Cu")
    (at 20 20)
    (property "Reference" "{{secondReference}}")
    (property "Value" "10k")
    (pad "1" smd roundrect (at -0.8 0) (size 0.8 0.8) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "N1"))
    (pad "2" smd roundrect (at 0.8 0) (size 0.8 0.8) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "N1"))
  )
  (footprint "Package_SO:SOIC-8_3.9x4.9mm_P1.27mm"
    (layer "B.Cu")
    (at 40 20 180)
    (property "Reference" "U1")
    (property "Value" "OPA2325")
    (pad "1" smd rect (at -2 0) (size 0.6 1) (layers "B.Cu" "B.Paste" "B.Mask") (net 1 "N1"))
  )
  (footprint "Connector_PinHeader_2.54mm:PinHeader_1x02_P2.54mm_Vertical"
    (layer "F.Cu")
    (at 60 20)
    (property "Reference" "J1")
    (property "Value" "Conn_01x02")
    (pad "1" thru_hole circle (at 0 0) (size 1.7 1.7) (drill 1) (layers "*.Cu" "*.Mask") (net 1 "N1"))
  )
  (footprint "LED_SMD:LED_0603_1608Metric"
    (layer "F.Cu")
    (at 80 20)
    (property "Reference" "DNP1")
    (property "Value" "LED")
    (property "DNP" "true")
    (pad "1" smd rect (at -0.8 0) (size 0.8 0.8) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "N1"))
  )
)
""";
    }

    private static string CreateSchematicText()
    {
        return """
(kicad_sch
  (symbol
    (lib_id "Device:R")
    (at 0 0 0)
    (unit 1)
    (property "Reference" "R1")
    (property "Value" "10k")
    (property "Manufacturer" "Yageo")
    (property "MPN" "RC0603FR-0710KL")
  )
  (symbol
    (lib_id "Device:R")
    (at 0 0 0)
    (unit 1)
    (property "Reference" "R2")
    (property "Value" "10k")
    (property "Manufacturer" "Yageo")
    (property "MPN" "RC0603FR-0710KL")
  )
  (symbol
    (lib_id "Amplifier_Operational:OPA2325")
    (at 0 0 0)
    (unit 1)
    (property "Reference" "U1")
    (property "Value" "OPA2325")
    (property "MPN" "OPA2325AIDR")
  )
)
""";
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public async Task<CommandExecutionResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            if (arguments.Count > 0 && arguments[0] == "--version")
            {
                return new CommandExecutionResult(0, "KiCad Version 10.0.0", string.Empty);
            }

            var kind = arguments.Contains("drill") ? "drill" : "gerbers";
            var outputIndex = -1;
            for (var index = 0; index < arguments.Count; index++)
            {
                if (arguments[index] == "--output")
                {
                    outputIndex = index;
                    break;
                }
            }

            if (outputIndex >= 0 && outputIndex < arguments.Count - 1)
            {
                var output = arguments[outputIndex + 1];
                Directory.CreateDirectory(output);
                var file = Path.Combine(output, kind == "drill" ? "fabrication.drl" : "fabrication.gbr");
                await File.WriteAllTextAsync(file, kind, cancellationToken);
            }

            return new CommandExecutionResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class TempFile : IDisposable
    {
        private readonly bool _deleteOnDispose;

        public TempFile(string fileName, bool deleteOnDispose = true)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcbhelper-tests", Guid.NewGuid().ToString("N"), fileName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, string.Empty);
            _deleteOnDispose = deleteOnDispose;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (_deleteOnDispose && Directory.Exists(System.IO.Path.GetDirectoryName(Path)))
            {
                Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
            }
        }
    }
}
