using System.Globalization;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

internal static partial class KiCadBoardParser
{
    public static KiCadBoardDocument Parse(string boardFile)
    {
        var text = File.ReadAllText(boardFile);
        var footprints = new List<KiCadFootprint>();

        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var footprintStart = text.IndexOf("(footprint", searchIndex, StringComparison.Ordinal);
            if (footprintStart < 0)
            {
                break;
            }

            var footprintEnd = FindMatchingParenthesis(text, footprintStart);
            if (footprintEnd < 0)
            {
                break;
            }

            var footprintText = text.Substring(footprintStart, footprintEnd - footprintStart + 1);
            var nameMatch = FootprintNameRegex().Match(footprintText);
            var referenceMatch = ReferenceRegex().Match(footprintText);
            var layerMatch = LayerRegex().Match(footprintText);
            var atMatch = AtRegex().Match(footprintText);

            footprints.Add(new KiCadFootprint(
                referenceMatch.Success ? referenceMatch.Groups["reference"].Value : null,
                nameMatch.Success ? nameMatch.Groups["name"].Value : string.Empty,
                layerMatch.Success ? layerMatch.Groups["layer"].Value : null,
                layerMatch.Success && layerMatch.Groups["layer"].Value.StartsWith("B.", StringComparison.OrdinalIgnoreCase) ? "back" : "front",
                atMatch.Success ? double.Parse(atMatch.Groups["x"].Value, CultureInfo.InvariantCulture) : null,
                atMatch.Success ? double.Parse(atMatch.Groups["y"].Value, CultureInfo.InvariantCulture) : null,
                atMatch.Success && atMatch.Groups["rotation"].Success
                    ? double.Parse(atMatch.Groups["rotation"].Value, CultureInfo.InvariantCulture)
                    : null,
                footprintStart,
                footprintEnd - footprintStart + 1,
                atMatch.Success ? footprintStart + atMatch.Index : null,
                atMatch.Success ? atMatch.Length : null));

            searchIndex = footprintEnd + 1;
        }

        return new KiCadBoardDocument(boardFile, text, footprints);
    }

    public static string FormatTopLevelAt(Placement placement)
    {
        var x = placement.XMillimeters.ToString("0.###", CultureInfo.InvariantCulture);
        var y = placement.YMillimeters.ToString("0.###", CultureInfo.InvariantCulture);
        if (placement.RotationDegrees is null)
        {
            return $"(at {x} {y})";
        }

        var rotation = placement.RotationDegrees.Value.ToString("0.###", CultureInfo.InvariantCulture);
        return $"(at {x} {y} {rotation})";
    }

    private static int FindMatchingParenthesis(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = openIndex; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    [GeneratedRegex(@"\(footprint\s+""(?<name>[^""]+)""")]
    private static partial Regex FootprintNameRegex();

    [GeneratedRegex(@"\(property\s+""Reference""\s+""(?<reference>[^""]+)""")]
    private static partial Regex ReferenceRegex();

    [GeneratedRegex(@"\(layer\s+""(?<layer>[^""]+)""\)")]
    private static partial Regex LayerRegex();

    [GeneratedRegex(@"\(at\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)(?:\s+(?<rotation>-?\d+(?:\.\d+)?))?\)")]
    private static partial Regex AtRegex();
}

internal sealed record KiCadBoardDocument(
    string BoardFile,
    string Text,
    IReadOnlyList<KiCadFootprint> Footprints);

internal sealed record KiCadFootprint(
    string? Reference,
    string FootprintName,
    string? Layer,
    string Side,
    double? XMillimeters,
    double? YMillimeters,
    double? RotationDegrees,
    int SourceStart,
    int SourceLength,
    int? AtStart,
    int? AtLength);
