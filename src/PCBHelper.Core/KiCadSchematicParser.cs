using System.Globalization;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

internal static partial class KiCadSchematicParser
{
    public static KiCadSchematicDocument Parse(string schematicFile)
    {
        var text = File.ReadAllText(schematicFile);
        return new KiCadSchematicDocument(
            schematicFile,
            text,
            ParseSymbols(text),
            ParseWires(text),
            ParseLabels(text),
            ParseJunctions(text));
    }

    public static string FormatNumber(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<KiCadSchematicSymbol> ParseSymbols(string text)
    {
        var symbols = new List<KiCadSchematicSymbol>();
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var symbolStart = text.IndexOf("(symbol", searchIndex, StringComparison.Ordinal);
            if (symbolStart < 0)
            {
                break;
            }

            var afterKeyword = symbolStart + "(symbol".Length;
            if (afterKeyword < text.Length && !char.IsWhiteSpace(text[afterKeyword]))
            {
                searchIndex = afterKeyword;
                continue;
            }

            var symbolEnd = FindMatchingParenthesis(text, symbolStart);
            if (symbolEnd < 0)
            {
                break;
            }

            var symbolText = text.Substring(symbolStart, symbolEnd - symbolStart + 1);
            var properties = ParseProperties(symbolText, symbolStart);
            properties.TryGetValue("Reference", out var reference);
            var libId = LibIdRegex().Match(symbolText);
            var at = AtRegex().Match(symbolText);

            symbols.Add(new KiCadSchematicSymbol(
                reference?.Value,
                libId.Success ? libId.Groups["libid"].Value : null,
                at.Success ? double.Parse(at.Groups["x"].Value, CultureInfo.InvariantCulture) : null,
                at.Success ? double.Parse(at.Groups["y"].Value, CultureInfo.InvariantCulture) : null,
                at.Success && at.Groups["rotation"].Success
                    ? double.Parse(at.Groups["rotation"].Value, CultureInfo.InvariantCulture)
                    : null,
                symbolStart,
                symbolEnd - symbolStart + 1,
                properties));

            searchIndex = symbolEnd + 1;
        }

        return symbols;
    }

    private static IReadOnlyList<KiCadSchematicWire> ParseWires(string text)
    {
        return WireRegex().Matches(text)
            .Select(static match => new KiCadSchematicWire(
                double.Parse(match.Groups["x1"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["y1"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["x2"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["y2"].Value, CultureInfo.InvariantCulture),
                match.Index,
                match.Length))
            .ToArray();
    }

    private static IReadOnlyList<KiCadSchematicLabel> ParseLabels(string text)
    {
        return LabelRegex().Matches(text)
            .Select(static match => new KiCadSchematicLabel(
                match.Groups["text"].Value,
                double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture),
                match.Index,
                match.Length))
            .ToArray();
    }

    private static IReadOnlyList<KiCadSchematicJunction> ParseJunctions(string text)
    {
        return JunctionRegex().Matches(text)
            .Select(static match => new KiCadSchematicJunction(
                double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture),
                match.Index,
                match.Length))
            .ToArray();
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

    internal static int FindMatchingParenthesis(string text, int openIndex)
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

    [GeneratedRegex(@"\(lib_id\s+""(?<libid>[^""]+)""\)")]
    private static partial Regex LibIdRegex();

    [GeneratedRegex(@"\(at\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)(?:\s+(?<rotation>-?\d+(?:\.\d+)?))?\)")]
    private static partial Regex AtRegex();

    [GeneratedRegex(@"\(property\s+""(?<name>[^""]+)""\s+""(?<value>(?:\\""|[^""])*)""")]
    private static partial Regex PropertyRegex();

    [GeneratedRegex(@"\(wire\s+\(pts\s+\(xy\s+(?<x1>-?\d+(?:\.\d+)?)\s+(?<y1>-?\d+(?:\.\d+)?)\)\s+\(xy\s+(?<x2>-?\d+(?:\.\d+)?)\s+(?<y2>-?\d+(?:\.\d+)?)\)\)[\s\S]*?\)")]
    private static partial Regex WireRegex();

    [GeneratedRegex(@"\(label\s+""(?<text>[^""]+)""[\s\S]*?\(at\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)(?:\s+-?\d+(?:\.\d+)?)?\)[\s\S]*?\)")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"\(junction\s+\(at\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)\)[\s\S]*?\)")]
    private static partial Regex JunctionRegex();
}

internal sealed record KiCadSchematicDocument(
    string SchematicFile,
    string Text,
    IReadOnlyList<KiCadSchematicSymbol> Symbols,
    IReadOnlyList<KiCadSchematicWire> Wires,
    IReadOnlyList<KiCadSchematicLabel> Labels,
    IReadOnlyList<KiCadSchematicJunction> Junctions);

internal sealed record KiCadSchematicSymbol(
    string? Reference,
    string? LibId,
    double? XMillimeters,
    double? YMillimeters,
    double? RotationDegrees,
    int SourceStart,
    int SourceLength,
    IReadOnlyDictionary<string, KiCadProperty> Properties);

internal sealed record KiCadSchematicWire(double X1Millimeters, double Y1Millimeters, double X2Millimeters, double Y2Millimeters, int SourceStart, int SourceLength);

internal sealed record KiCadSchematicLabel(string Text, double XMillimeters, double YMillimeters, int SourceStart, int SourceLength);

internal sealed record KiCadSchematicJunction(double XMillimeters, double YMillimeters, int SourceStart, int SourceLength);
