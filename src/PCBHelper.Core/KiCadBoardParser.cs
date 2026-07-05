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
            var properties = ParseProperties(footprintText, footprintStart);
            var pads = ParsePads(footprintText, footprintStart);

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
                atMatch.Success ? atMatch.Length : null,
                properties,
                pads));

            searchIndex = footprintEnd + 1;
        }

        return new KiCadBoardDocument(boardFile, text, ParseNets(text), footprints);
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

    private static IReadOnlyDictionary<string, KiCadProperty> ParseProperties(string blockText, int absoluteOffset)
    {
        var properties = new Dictionary<string, KiCadProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PropertyRegex().Matches(blockText))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value;
            properties[name] = new KiCadProperty(
                name,
                value,
                absoluteOffset + match.Index,
                match.Length,
                absoluteOffset + match.Groups["value"].Index,
                match.Groups["value"].Length);
        }

        return properties;
    }

    private static IReadOnlyList<KiCadPad> ParsePads(string footprintText, int absoluteOffset)
    {
        var pads = new List<KiCadPad>();
        var searchIndex = 0;
        while (searchIndex < footprintText.Length)
        {
            var padStart = footprintText.IndexOf("(pad", searchIndex, StringComparison.Ordinal);
            if (padStart < 0)
            {
                break;
            }

            var padEnd = FindMatchingParenthesis(footprintText, padStart);
            if (padEnd < 0)
            {
                break;
            }

            var padText = footprintText.Substring(padStart, padEnd - padStart + 1);
            var nameMatch = PadNameRegex().Match(padText);
            var typeMatch = PadTypeRegex().Match(padText);
            var atMatch = AtRegex().Match(padText);
            var netMatch = PadNetRegex().Match(padText);
            var pinFunctionMatch = PinFunctionRegex().Match(padText);
            var layersMatch = PadLayersRegex().Match(padText);

            pads.Add(new KiCadPad(
                nameMatch.Success ? nameMatch.Groups["name"].Value : string.Empty,
                typeMatch.Success ? typeMatch.Groups["type"].Value : null,
                atMatch.Success ? double.Parse(atMatch.Groups["x"].Value, CultureInfo.InvariantCulture) : null,
                atMatch.Success ? double.Parse(atMatch.Groups["y"].Value, CultureInfo.InvariantCulture) : null,
                layersMatch.Success
                    ? layersMatch.Groups["layers"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static layer => layer.Trim('"'))
                        .ToArray()
                    : Array.Empty<string>(),
                netMatch.Success ? int.Parse(netMatch.Groups["code"].Value, CultureInfo.InvariantCulture) : null,
                netMatch.Success ? netMatch.Groups["name"].Value : null,
                pinFunctionMatch.Success ? pinFunctionMatch.Groups["pinfunction"].Value : null,
                absoluteOffset + padStart,
                padEnd - padStart + 1));

            searchIndex = padEnd + 1;
        }

        return pads;
    }

    private static IReadOnlyList<KiCadNet> ParseNets(string text)
    {
        var firstFootprint = text.IndexOf("(footprint", StringComparison.Ordinal);
        var declarationText = firstFootprint >= 0 ? text[..firstFootprint] : text;
        return NetRegex().Matches(declarationText)
            .Select(static match => new KiCadNet(
                int.Parse(match.Groups["code"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value))
            .OrderBy(static net => net.Code)
            .ToArray();
    }

    [GeneratedRegex(@"\(footprint\s+""(?<name>[^""]+)""")]
    private static partial Regex FootprintNameRegex();

    [GeneratedRegex(@"\(property\s+""Reference""\s+""(?<reference>[^""]+)""")]
    private static partial Regex ReferenceRegex();

    [GeneratedRegex(@"\(property\s+""(?<name>[^""]+)""\s+""(?<value>(?:\\""|[^""])*)""")]
    private static partial Regex PropertyRegex();

    [GeneratedRegex(@"\(layer\s+""(?<layer>[^""]+)""\)")]
    private static partial Regex LayerRegex();

    [GeneratedRegex(@"\(at\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)(?:\s+(?<rotation>-?\d+(?:\.\d+)?))?\)")]
    private static partial Regex AtRegex();

    [GeneratedRegex(@"\(net\s+(?<code>\d+)\s+""(?<name>[^""]*)""\)")]
    private static partial Regex NetRegex();

    [GeneratedRegex(@"\(pad\s+""(?<name>[^""]*)""")]
    private static partial Regex PadNameRegex();

    [GeneratedRegex(@"\(pad\s+""[^""]*""\s+(?<type>\S+)")]
    private static partial Regex PadTypeRegex();

    [GeneratedRegex(@"\(layers\s+(?<layers>[^)]*)\)")]
    private static partial Regex PadLayersRegex();

    [GeneratedRegex(@"\(net\s+(?<code>\d+)\s+""(?<name>[^""]*)""\)")]
    private static partial Regex PadNetRegex();

    [GeneratedRegex(@"\(pinfunction\s+""(?<pinfunction>[^""]*)""\)")]
    private static partial Regex PinFunctionRegex();
}

internal sealed record KiCadBoardDocument(
    string BoardFile,
    string Text,
    IReadOnlyList<KiCadNet> Nets,
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
    int? AtLength,
    IReadOnlyDictionary<string, KiCadProperty> Properties,
    IReadOnlyList<KiCadPad> Pads);

internal sealed record KiCadProperty(
    string Name,
    string Value,
    int SourceStart,
    int SourceLength,
    int ValueStart,
    int ValueLength);

internal sealed record KiCadPad(
    string Name,
    string? Type,
    double? XMillimeters,
    double? YMillimeters,
    IReadOnlyList<string> Layers,
    int? NetCode,
    string? NetName,
    string? PinFunction,
    int SourceStart,
    int SourceLength);

internal sealed record KiCadNet(int Code, string Name);
