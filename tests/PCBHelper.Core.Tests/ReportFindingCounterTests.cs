using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class ReportFindingCounterTests
{
    [Fact]
    public void CountFindings_Counts_Known_Json_Finding_Arrays()
    {
        using var temp = new TempDirectory();
        var report = Path.Combine(temp.Path, "drc.json");
        File.WriteAllText(report, """
        {
          "violations": [
            { "description": "one" },
            { "description": "two" }
          ],
          "warnings": [
            { "description": "three" }
          ]
        }
        """);

        Assert.Equal(3, ReportFindingCounter.CountFindings(report));
    }
}
