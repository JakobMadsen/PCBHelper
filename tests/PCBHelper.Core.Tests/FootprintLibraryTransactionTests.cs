using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class FootprintLibraryTransactionTests
{
    [Fact]
    public void EnsurePcbHelperLibraryEntry_Preserves_Existing_Libraries()
    {
        const string original="(fp_lib_table\n  (lib (name \"Resistor_SMD\")(type \"KiCad\")(uri \"${KICAD10_FOOTPRINT_DIR}/Resistor_SMD.pretty\"))\n)\n";

        var updated=FootprintLibraryTransactionService.EnsurePcbHelperLibraryEntry(original);

        Assert.Contains("(name \"Resistor_SMD\")",updated);
        Assert.Contains("(name \"PCBHelper\")",updated);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(updated,"\\(name \\\"PCBHelper\\\"\\)").Cast<System.Text.RegularExpressions.Match>());
        Assert.Equal(updated,FootprintLibraryTransactionService.EnsurePcbHelperLibraryEntry(updated));
    }
}
