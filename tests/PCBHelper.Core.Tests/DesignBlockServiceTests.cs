using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class DesignBlockServiceTests
{
    [Fact]
    public void Create_Uses_Preview_Hash_Copies_Payload_And_Never_Overwrites()
    {
        using var fixture = new TempDirectory();
        var source = Path.Combine(fixture.Path, "source.kicad_sch");
        File.WriteAllText(source, "(kicad_sch (version 20250101) (generator eeschema))");
        var library = Path.Combine(fixture.Path, "company.kicad_blocks");
        var service = new DesignBlockService();
        var manifest = ManifestJson("clean-power-input", lifecycle: "draft");

        var preview = service.PreviewCreate(library, source, manifest);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Equal(64, preview.Data!.PlanHash.Length);
        Assert.Contains(preview.Data.Files, file => file.TargetFile == "clean-power-input.kicad_sch");

        var applied = service.ApplyCreate(library, source, manifest, preview.Data.PlanHash);

        Assert.True(applied.Success, applied.Error?.Message);
        Assert.True(applied.Data!.Validation!.Passed);
        var copied = Path.Combine(applied.Data.BlockDirectory, "clean-power-input.kicad_sch");
        Assert.Equal(Sha256(File.ReadAllBytes(source)), Sha256(File.ReadAllBytes(copied)));
        Assert.True(File.Exists(Path.Combine(applied.Data.BlockDirectory, "clean-power-input.json")));
        Assert.True(File.Exists(Path.Combine(applied.Data.BlockDirectory, DesignBlockService.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(applied.Data.BlockDirectory, "REVIEW.md")));

        var duplicate = service.ApplyCreate(library, source, manifest, preview.Data.PlanHash);

        Assert.False(duplicate.Success);
        Assert.Equal("DESIGN_BLOCK_EXISTS", duplicate.Error?.Code);
    }

    [Fact]
    public void Apply_Rejects_Changed_Source_After_Preview()
    {
        using var fixture = new TempDirectory();
        var source = Path.Combine(fixture.Path, "source.kicad_sch");
        File.WriteAllText(source, "(kicad_sch (version 20250101) (generator eeschema))");
        var library = Path.Combine(fixture.Path, "company.kicad_blocks");
        var service = new DesignBlockService();
        var manifest = ManifestJson("stable-source");
        var preview = service.PreviewCreate(library, source, manifest);
        Assert.True(preview.Success);
        File.AppendAllText(source, "\n; changed after review");

        var result = service.ApplyCreate(library, source, manifest, preview.Data!.PlanHash);

        Assert.False(result.Success);
        Assert.Equal("DESIGN_BLOCK_PLAN_HASH_MISMATCH", result.Error?.Code);
        Assert.False(Directory.Exists(Path.Combine(library, "stable-source.kicad_block")));
    }

    [Fact]
    public void Import_Preserves_Native_Default_Fields_And_Normalizes_Filenames()
    {
        using var fixture = new TempDirectory();
        var source = Path.Combine(fixture.Path, "vendor.kicad_block");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "vendor.kicad_sch"), "(kicad_sch (version 20250101) (generator eeschema))");
        File.WriteAllText(Path.Combine(source, "vendor.kicad_pcb"), "(kicad_pcb (version 20250101) (generator pcbnew))");
        File.WriteAllText(Path.Combine(source, "vendor.json"), """{"description":"Vendor text","keywords":"old","fields":{"Channel":"A"}}""");
        var library = Path.Combine(fixture.Path, "company.kicad_blocks");
        var service = new DesignBlockService();
        var manifest = ManifestJson("normalized-interface", lifecycle: "imported", layout: "anchored");
        var preview = service.PreviewImport(library, source, manifest);
        Assert.True(preview.Success, preview.Error?.Message);

        var result = service.ApplyImport(library, source, manifest, preview.Data!.PlanHash);

        Assert.True(result.Success, result.Error?.Message);
        var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Data!.BlockDirectory, "normalized-interface.json")));
        Assert.Equal("A", metadata.RootElement.GetProperty("fields").GetProperty("Channel").GetString());
        Assert.True(File.Exists(Path.Combine(result.Data.BlockDirectory, "normalized-interface.kicad_sch")));
        Assert.True(File.Exists(Path.Combine(result.Data.BlockDirectory, "normalized-interface.kicad_pcb")));
    }

    [Fact]
    public void Preview_Rejects_Missing_Provenance_And_Path_Like_Id()
    {
        using var fixture = new TempDirectory();
        var source = Path.Combine(fixture.Path, "source.kicad_sch");
        File.WriteAllText(source, "(kicad_sch (version 20250101) (generator eeschema))");
        var service = new DesignBlockService();
        var library = Path.Combine(fixture.Path, "company.kicad_blocks");

        var noSource = service.PreviewCreate(library, source, ManifestJson("missing-source", includeSource: false));
        var badId = service.PreviewCreate(library, source, ManifestJson("../escape"));

        Assert.False(noSource.Success);
        Assert.Equal("DESIGN_BLOCK_MANIFEST_INVALID", noSource.Error?.Code);
        Assert.False(badId.Success);
        Assert.Equal("DESIGN_BLOCK_MANIFEST_INVALID", badId.Error?.Code);
    }

    [Fact]
    public void Validation_Blocks_Unsupported_Maturity_Claims()
    {
        using var fixture = new TempDirectory();
        var source = Path.Combine(fixture.Path, "source.kicad_sch");
        File.WriteAllText(source, "(kicad_sch (version 20250101) (generator eeschema))");
        var library = Path.Combine(fixture.Path, "company.kicad_blocks");
        var service = new DesignBlockService();

        var preview = service.PreviewCreate(library, source, ManifestJson("unproven", maturity: "qualified"));

        Assert.False(preview.Success);
        Assert.Equal("DESIGN_BLOCK_MANIFEST_INVALID", preview.Error?.Code);
        Assert.Contains("simulation evidence", preview.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(library));
    }

    [Fact]
    public void List_Filters_By_Category_And_Maturity()
    {
        using var fixture = new TempDirectory();
        var source = Path.Combine(fixture.Path, "source.kicad_sch");
        File.WriteAllText(source, "(kicad_sch (version 20250101) (generator eeschema))");
        var library = Path.Combine(fixture.Path, "company.kicad_blocks");
        var service = new DesignBlockService();
        foreach (var id in new[] { "power-a", "power-b" })
        {
            var manifest = ManifestJson(id);
            var preview = service.PreviewCreate(library, source, manifest);
            Assert.True(preview.Success);
            Assert.True(service.ApplyCreate(library, source, manifest, preview.Data!.PlanHash).Success);
        }

        var result = service.List(library, "power", DesignBlockMaturity.Reference);

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Blocks.Count);
        Assert.All(result.Data.Blocks, block => Assert.Equal(DesignBlockLayoutPolicy.SchematicOnly, block.LayoutPolicy));
    }

    private static string ManifestJson(
        string id,
        string lifecycle = "draft",
        string maturity = "reference",
        string layout = "schematicOnly",
        bool includeSource = true)
    {
        const string validSource = """{"kind":"manufacturerReference","title":"Primary reference","uri":"https://example.com/reference","license":"Example license","attribution":"Example vendor","redistributionAllowed":true,"retrievedAtUtc":"2026-07-29T00:00:00Z"}""";
        var source = includeSource ? validSource : "null";
        return $$"""
        {
          "schemaVersion":1,
          "id":"{{id}}",
          "version":"1.0.0",
          "name":"{{id}}",
          "description":"A deterministic test block.",
          "category":"power",
          "lifecycleStage":"{{lifecycle}}",
          "maturity":"{{maturity}}",
          "source":{{source}},
          "ports":[
            {"name":"VIN","kind":"powerInput","net":"VIN","description":"Input supply.","electricalDomain":"5V","required":true},
            {"name":"GND","kind":"ground","net":"GND","description":"Return.","electricalDomain":"0V","required":true}
          ],
          "layout":{"policy":"{{layout}}","rationale":"Test policy.","constraints":[]},
          "evidence":[],
          "tags":["test"]
        }
        """;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
