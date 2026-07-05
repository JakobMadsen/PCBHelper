using System.Text.Json;

namespace PCBHelper.Core;

public static class ReportFindingCounter
{
    private static readonly HashSet<string> FindingArrayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "violations",
        "errors",
        "warnings",
        "exclusions"
    };

    public static int? CountFindings(string reportPath)
    {
        try
        {
            using var stream = File.OpenRead(reportPath);
            using var document = JsonDocument.Parse(stream);
            return CountElement(document.RootElement, null);
        }
        catch (JsonException)
        {
            return CountTextFindings(reportPath);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int CountElement(JsonElement element, string? propertyName)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var item in element.EnumerateArray())
            {
                count += CountElement(item, propertyName);
            }

            if (propertyName is not null && FindingArrayNames.Contains(propertyName))
            {
                return Math.Max(count, element.GetArrayLength());
            }

            return count;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var total = 0;
        foreach (var property in element.EnumerateObject())
        {
            total += CountElement(property.Value, property.Name);
        }

        return total;
    }

    private static int? CountTextFindings(string reportPath)
    {
        var text = File.ReadAllText(reportPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Violation", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }
}
