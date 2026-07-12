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

        return new KiCadBoardDocument(boardFile, text, ParseNets(text), footprints, ParseSegments(text), ParseVias(text));
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

    public static string FormatNumber(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
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
            var shapeMatch = PadShapeRegex().Match(padText);
            var atMatch = AtRegex().Match(padText);
            var sizeMatch = PadSizeRegex().Match(padText);
            var net = ParseNetReference(padText);
            var pinFunctionMatch = PinFunctionRegex().Match(padText);
            var layersMatch = PadLayersRegex().Match(padText);

            pads.Add(new KiCadPad(
                nameMatch.Success ? nameMatch.Groups["name"].Value : string.Empty,
                typeMatch.Success ? typeMatch.Groups["type"].Value : null,
                shapeMatch.Success ? shapeMatch.Groups["shape"].Value : null,
                atMatch.Success ? double.Parse(atMatch.Groups["x"].Value, CultureInfo.InvariantCulture) : null,
                atMatch.Success ? double.Parse(atMatch.Groups["y"].Value, CultureInfo.InvariantCulture) : null,
                sizeMatch.Success ? double.Parse(sizeMatch.Groups["x"].Value, CultureInfo.InvariantCulture) : null,
                sizeMatch.Success ? double.Parse(sizeMatch.Groups["y"].Value, CultureInfo.InvariantCulture) : null,
                layersMatch.Success
                    ? layersMatch.Groups["layers"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static layer => layer.Trim('"'))
                        .ToArray()
                    : Array.Empty<string>(),
                net.Code,
                net.Name,
                pinFunctionMatch.Success ? pinFunctionMatch.Groups["pinfunction"].Value : null,
                absoluteOffset + padStart,
                padEnd - padStart + 1));

            searchIndex = padEnd + 1;
        }

        return pads;
    }

    private static IReadOnlyList<KiCadNet> ParseNets(string text)
    {
        var netsByName = new Dictionary<string, KiCadNet>(StringComparer.OrdinalIgnoreCase);
        var usedCodes = new HashSet<int>();

        foreach (Match match in NetRegex().Matches(text))
        {
            var name = NormalizeNetName(match.Groups["name"].Value);
            if (name is null)
            {
                continue;
            }

            var code = int.Parse(match.Groups["code"].Value, CultureInfo.InvariantCulture);
            usedCodes.Add(code);
            if (!netsByName.ContainsKey(name))
            {
                netsByName[name] = new KiCadNet(code, name);
            }
        }

        foreach (Match match in NamedNetReferenceRegex().Matches(text))
        {
            var name = NormalizeNetName(match.Groups["name"].Value);
            if (name is null || netsByName.ContainsKey(name))
            {
                continue;
            }

            var code = NextAvailableNetCode(usedCodes);
            usedCodes.Add(code);
            netsByName[name] = new KiCadNet(code, name);
        }

        return netsByName.Values
            .OrderBy(static net => net.Code)
            .ThenBy(static net => net.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<KiCadSegment> ParseSegments(string text)
    {
        var segments = new List<KiCadSegment>();
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var start = text.IndexOf("(segment", searchIndex, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = FindMatchingParenthesis(text, start);
            if (end < 0)
            {
                break;
            }

            var block = text.Substring(start, end - start + 1);
            var startMatch = StartRegex().Match(block);
            var endMatch = EndRegex().Match(block);
            var widthMatch = WidthRegex().Match(block);
            var layerMatch = LayerRegex().Match(block);
            var net = ParseNetReference(block);
            var uuidMatch = UuidRegex().Match(block);

            segments.Add(new KiCadSegment(
                uuidMatch.Success ? uuidMatch.Groups["uuid"].Value : $"segment-{segments.Count + 1}",
                uuidMatch.Success ? uuidMatch.Groups["uuid"].Value : null,
                startMatch.Success ? double.Parse(startMatch.Groups["x"].Value, CultureInfo.InvariantCulture) : null,
                startMatch.Success ? double.Parse(startMatch.Groups["y"].Value, CultureInfo.InvariantCulture) : null,
                endMatch.Success ? double.Parse(endMatch.Groups["x"].Value, CultureInfo.InvariantCulture) : null,
                endMatch.Success ? double.Parse(endMatch.Groups["y"].Value, CultureInfo.InvariantCulture) : null,
                widthMatch.Success ? double.Parse(widthMatch.Groups["width"].Value, CultureInfo.InvariantCulture) : null,
                layerMatch.Success ? layerMatch.Groups["layer"].Value : null,
                net.Code,
                net.Name,
                start,
                end - start + 1,
                block));

            searchIndex = end + 1;
        }

        return segments;
    }

    private static IReadOnlyList<KiCadVia> ParseVias(string text)
    {
        var vias = new List<KiCadVia>();
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var start = text.IndexOf("(via", searchIndex, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = FindMatchingParenthesis(text, start);
            if (end < 0)
            {
                break;
            }

            var block = text.Substring(start, end - start + 1);
            var atMatch = AtRegex().Match(block);
            var sizeMatch = SizeRegex().Match(block);
            var drillMatch = DrillRegex().Match(block);
            var layersMatch = PadLayersRegex().Match(block);
            var net = ParseNetReference(block);
            var uuidMatch = UuidRegex().Match(block);

            vias.Add(new KiCadVia(
                uuidMatch.Success ? uuidMatch.Groups["uuid"].Value : $"via-{vias.Count + 1}",
                uuidMatch.Success ? uuidMatch.Groups["uuid"].Value : null,
                atMatch.Success ? double.Parse(atMatch.Groups["x"].Value, CultureInfo.InvariantCulture) : null,
                atMatch.Success ? double.Parse(atMatch.Groups["y"].Value, CultureInfo.InvariantCulture) : null,
                sizeMatch.Success ? double.Parse(sizeMatch.Groups["size"].Value, CultureInfo.InvariantCulture) : null,
                drillMatch.Success ? double.Parse(drillMatch.Groups["drill"].Value, CultureInfo.InvariantCulture) : null,
                layersMatch.Success
                    ? layersMatch.Groups["layers"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static layer => layer.Trim('"'))
                        .ToArray()
                    : Array.Empty<string>(),
                net.Code,
                net.Name,
                start,
                end - start + 1,
                block));

            searchIndex = end + 1;
        }

        return vias;
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

    [GeneratedRegex(@"\(start\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)\)")]
    private static partial Regex StartRegex();

    [GeneratedRegex(@"\(end\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)\)")]
    private static partial Regex EndRegex();

    [GeneratedRegex(@"\(width\s+(?<width>-?\d+(?:\.\d+)?)\)")]
    private static partial Regex WidthRegex();

    [GeneratedRegex(@"\(size\s+(?<size>-?\d+(?:\.\d+)?)\)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\(size\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)\)")]
    private static partial Regex PadSizeRegex();

    [GeneratedRegex(@"\(drill\s+(?<drill>-?\d+(?:\.\d+)?)\)")]
    private static partial Regex DrillRegex();

    [GeneratedRegex(@"\(net\s+(?<code>\d+)\s+""(?<name>[^""]*)""\)")]
    private static partial Regex NetRegex();

    [GeneratedRegex(@"\(net\s+(?<code>\d+)\)")]
    private static partial Regex NetCodeRegex();

    [GeneratedRegex(@"\(net\s+""(?<name>[^""]*)""\)")]
    private static partial Regex NamedNetReferenceRegex();

    [GeneratedRegex(@"\(pad\s+""(?<name>[^""]*)""")]
    private static partial Regex PadNameRegex();

    [GeneratedRegex(@"\(pad\s+""[^""]*""\s+(?<type>\S+)")]
    private static partial Regex PadTypeRegex();

    [GeneratedRegex(@"\(pad\s+""[^""]*""\s+\S+\s+(?<shape>\S+)")]
    private static partial Regex PadShapeRegex();

    [GeneratedRegex(@"\(layers\s+(?<layers>[^)]*)\)")]
    private static partial Regex PadLayersRegex();

    [GeneratedRegex(@"\(pinfunction\s+""(?<pinfunction>[^""]*)""\)")]
    private static partial Regex PinFunctionRegex();

    [GeneratedRegex(@"\(uuid\s+""?(?<uuid>[0-9a-fA-F-]+)""?\)")]
    private static partial Regex UuidRegex();

    private static ParsedNetReference ParseNetReference(string block)
    {
        var numericNamedMatch = NetRegex().Match(block);
        if (numericNamedMatch.Success)
        {
            return new ParsedNetReference(
                int.Parse(numericNamedMatch.Groups["code"].Value, CultureInfo.InvariantCulture),
                NormalizeNetName(numericNamedMatch.Groups["name"].Value));
        }

        var namedMatch = NamedNetReferenceRegex().Match(block);
        if (namedMatch.Success)
        {
            return new ParsedNetReference(null, NormalizeNetName(namedMatch.Groups["name"].Value));
        }

        var numericMatch = NetCodeRegex().Match(block);
        return numericMatch.Success
            ? new ParsedNetReference(int.Parse(numericMatch.Groups["code"].Value, CultureInfo.InvariantCulture), null)
            : new ParsedNetReference(null, null);
    }

    private static string? NormalizeNetName(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int NextAvailableNetCode(ISet<int> usedCodes)
    {
        var code = usedCodes.Count == 0 ? 1 : usedCodes.Max() + 1;
        while (usedCodes.Contains(code))
        {
            code++;
        }

        return code;
    }
}

internal sealed record ParsedNetReference(int? Code, string? Name);

internal sealed record KiCadBoardDocument(
    string BoardFile,
    string Text,
    IReadOnlyList<KiCadNet> Nets,
    IReadOnlyList<KiCadFootprint> Footprints,
    IReadOnlyList<KiCadSegment> Segments,
    IReadOnlyList<KiCadVia> Vias);

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
    string? Shape,
    double? XMillimeters,
    double? YMillimeters,
    double? SizeXMillimeters,
    double? SizeYMillimeters,
    IReadOnlyList<string> Layers,
    int? NetCode,
    string? NetName,
    string? PinFunction,
    int SourceStart,
    int SourceLength);

internal sealed record KiCadNet(int Code, string Name);

internal sealed record KiCadSegment(
    string Id,
    string? Uuid,
    double? StartXMillimeters,
    double? StartYMillimeters,
    double? EndXMillimeters,
    double? EndYMillimeters,
    double? WidthMillimeters,
    string? Layer,
    int? NetCode,
    string? NetName,
    int SourceStart,
    int SourceLength,
    string SourceText);

internal sealed record KiCadVia(
    string Id,
    string? Uuid,
    double? XMillimeters,
    double? YMillimeters,
    double? SizeMillimeters,
    double? DrillMillimeters,
    IReadOnlyList<string> Layers,
    int? NetCode,
    string? NetName,
    int SourceStart,
    int SourceLength,
    string SourceText);
