using System.Text.RegularExpressions;

namespace PCBHelper.Core;

internal static partial class KiCadSchematicParser
{
    public static KiCadSchematicDocument Parse(string schematicFile)
    {
        var text = File.ReadAllText(schematicFile);
        var symbols = new List<KiCadSchematicSymbol>();

        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var symbolStart = text.IndexOf("(symbol", searchIndex, StringComparison.Ordinal);
            if (symbolStart < 0)
            {
                break;
            }

            var symbolEnd = FindMatchingParenthesis(text, symbolStart);
            if (symbolEnd < 0)
            {
                break;
            }

            var symbolText = text.Substring(symbolStart, symbolEnd - symbolStart + 1);
            var properties = ParseProperties(symbolText, symbolStart);
            properties.TryGetValue("Reference", out var reference);

            symbols.Add(new KiCadSchematicSymbol(
                reference?.Value,
                symbolStart,
                symbolEnd - symbolStart + 1,
                properties));

            searchIndex = symbolEnd + 1;
        }

        return new KiCadSchematicDocument(schematicFile, text, symbols);
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

    [GeneratedRegex(@"\(property\s+""(?<name>[^""]+)""\s+""(?<value>(?:\\""|[^""])*)""")]
    private static partial Regex PropertyRegex();
}

internal sealed record KiCadSchematicDocument(
    string SchematicFile,
    string Text,
    IReadOnlyList<KiCadSchematicSymbol> Symbols);

internal sealed record KiCadSchematicSymbol(
    string? Reference,
    int SourceStart,
    int SourceLength,
    IReadOnlyDictionary<string, KiCadProperty> Properties);
