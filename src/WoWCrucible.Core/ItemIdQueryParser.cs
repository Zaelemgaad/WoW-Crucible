namespace WoWCrucible.Core;

/// <summary>
/// Parses the item-ID forms accepted by the desktop catalog.
/// Commas are thousands separators only when every following group contains
/// exactly three digits; spaces alone always separate a batch. Human-readable
/// batches such as "17 and 17802" are accepted as exact IDs too.
/// </summary>
public static class ItemIdQueryParser
{
    public static bool TryParseSingle(string? text, out uint entry)
    {
        var entries = Parse(text);
        if (entries.Count == 1)
        {
            entry = entries[0];
            return true;
        }

        entry = 0;
        return false;
    }

    public static IReadOnlyList<uint> Parse(string? text)
    {
        var candidate = NormalizeConjunctions(text?.Trim() ?? string.Empty);
        if (candidate.Length == 0 || candidate.Any(character =>
                !char.IsDigit(character) && character is not ',' and not '_' and not ' ' and not '#' and not ';' and not '\r' and not '\n' and not '\t'))
            return [];

        var hardBatch = candidate.IndexOfAny([';', '\r', '\n']) >= 0 || candidate.Count(character => character == '#') > 1;
        var commaGrouped = !hardBatch && candidate.Contains(',');
        var pieces = candidate.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(piece => piece.TrimStart('#').Replace("_", string.Empty, StringComparison.Ordinal))
            .Where(piece => piece.Length > 0)
            .ToArray();
        if (pieces.Length == 0 || pieces.Any(piece => piece.Any(character => !char.IsDigit(character)))) return [];

        var looksGrouped = commaGrouped && pieces.Length > 1 && pieces[0].Length is >= 1 and <= 3 && pieces.Skip(1).All(piece => piece.Length == 3);
        if (looksGrouped && uint.TryParse(string.Concat(pieces), out var grouped) && grouped > 0) return [grouped];

        var result = new List<uint>();
        foreach (var piece in pieces)
        {
            if (!uint.TryParse(piece, out var entry) || entry == 0) return [];
            if (!result.Contains(entry)) result.Add(entry);
        }
        return result;
    }

    private static string NormalizeConjunctions(string candidate)
    {
        if (candidate.Length == 0) return candidate;
        var words = candidate.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return string.Empty;
        return string.Join(' ', words.Select(word => word.Equals("and", StringComparison.OrdinalIgnoreCase) || word == "&" ? ";" : word));
    }
}
