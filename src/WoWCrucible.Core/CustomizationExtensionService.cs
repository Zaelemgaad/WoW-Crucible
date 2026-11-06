using System.Globalization;
using System.Text;

namespace WoWCrucible.Core;

/// <summary>One thing wrong with a row, or an advisory about it.</summary>
public sealed record CustomizationProblem(string Severity, string File, int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"{Severity}\t{File}:{Line}\t{Message}" : $"{Severity}\t{File}\t{Message}";
}

/// <summary>One data row of a user file: its line and its cells by column.</summary>
public sealed record CustomizationRow(string File, int Line, IReadOnlyDictionary<string, string> Cells)
{
    public string this[string column] => Cells.TryGetValue(column, out var value) ? value : "";
}

/// <summary>
/// The user's customization rows for benilla and the server: tab-separated files in
/// <c>&lt;customization&gt;/user/</c>, layered over the merged catalog, which regenerating the merge never touches.
/// Ids spell as <c>u12</c> (a user row), <c>r966</c> (retail), <c>e966</c> (Epsilon) or a whole merged id,
/// <c>(source &lt;&lt; 28) | native</c>. The client's reader is benilla's <c>look_extensions.rs</c>; the two agree on the
/// format in <c>docs/wotlk/CUSTOMIZATION-EXTENSIONS.md</c>. This one is stricter, so a bad row stops here.
/// </summary>
public static class CustomizationExtensionService
{
    public const uint SourceUser = 15;
    public static readonly IReadOnlyDictionary<string, string[]> Columns = new Dictionary<string, string[]>
    {
        ["subjects.tsv"] = ["subject", "name", "category", "native_display", "match", "body"],
        ["options.tsv"] = ["option", "body", "name", "category", "order"],
        ["choices.tsv"] = ["choice", "option", "name", "order", "display", "model", "texture1", "texture2", "texture3", "scale", "geosets", "materials", "swatch"],
        ["items.tsv"] = ["appearance", "category", "item", "name"],
    };

    /// <summary>An id in any spelling, or null. A native id fits 28 bits.</summary>
    public static uint? ParseId(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        uint? Native(uint source)
        {
            if (!uint.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var native) || native == 0 || native >= 1u << 28) return null;
            return (source << 28) | native;
        }
        switch (char.ToLowerInvariant(text[0]))
        {
            case 'u': return Native(SourceUser);
            case 'r': return Native(2);
            case 'e': return Native(1);
        }
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) && whole != 0 ? whole : null;
    }

    public static string FormatId(uint id) => (id >> 28) switch
    {
        SourceUser => $"u{id & 0x0FFFFFFF}",
        2 => $"r{id & 0x0FFFFFFF}",
        1 => $"e{id & 0x0FFFFFFF}",
        _ => id.ToString(CultureInfo.InvariantCulture),
    };

    public static string UserFolder(string customization) => Path.Combine(customization, "user");

    /// <summary>The rows of one user file. A missing file is no rows; <c>#</c> starts a comment line.</summary>
    public static IReadOnlyList<CustomizationRow> ReadRows(string customization, string file)
    {
        var path = Path.Combine(UserFolder(customization), file);
        if (!File.Exists(path)) return [];
        string[]? header = null;
        var rows = new List<CustomizationRow>();
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.StartsWith('#')) continue;
            var cells = line.TrimStart('﻿').Split('\t').Select(cell => cell.Trim()).ToArray();
            if (header is null) { header = cells; continue; }
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < header.Length && c < cells.Length; c++) map[header[c]] = cells[c];
            rows.Add(new CustomizationRow(file, i + 1, map));
        }
        return rows;
    }

    /// <summary>What the generated catalog holds, so a row's parent can be checked.</summary>
    private sealed class Catalog
    {
        public HashSet<uint> Models { get; } = [];
        public HashSet<uint> Options { get; } = [];
        public HashSet<uint> Choices { get; } = [];
        public bool Loaded { get; set; }

        public static Catalog Load(string customization)
        {
            var catalog = new Catalog();
            void Read(string file, HashSet<uint> into)
            {
                var path = Path.Combine(customization, file);
                if (!File.Exists(path)) return;
                catalog.Loaded = true;
                foreach (var line in File.ReadLines(path).Skip(1))
                {
                    var cell = line.Split('\t')[0];
                    if (uint.TryParse(cell, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) into.Add(id);
                }
            }
            Read("merged_model.tsv", catalog.Models); Read("wardrobe_subject.tsv", catalog.Models);
            Read("merged_option.tsv", catalog.Options); Read("wardrobe_option.tsv", catalog.Options);
            Read("merged_choice.tsv", catalog.Choices); Read("wardrobe_choice.tsv", catalog.Choices);
            return catalog;
        }
    }

    /// <summary>Everything wrong with the folder. Errors would be left out by the game; warnings are for a person.</summary>
    /// <param name="customization">The folder holding <c>merged_*.tsv</c> and <c>user/</c>.</param>
    /// <param name="assets">Loose folders to look for named files in; none skips the check.</param>
    public static IReadOnlyList<CustomizationProblem> Validate(string customization, IReadOnlyList<string>? assets = null)
    {
        var problems = new List<CustomizationProblem>();
        var catalog = Catalog.Load(customization);
        void Error(CustomizationRow row, string message) => problems.Add(new("ERROR", row.File, row.Line, message));
        void Warn(CustomizationRow row, string message) => problems.Add(new("WARN", row.File, row.Line, message));
        if (!catalog.Loaded) problems.Add(new("WARN", "(catalog)", 0, "No merged_*.tsv here: a row's parent cannot be checked."));

        foreach (var (file, columns) in Columns)
        {
            var path = Path.Combine(UserFolder(customization), file);
            if (!File.Exists(path)) continue;
            var header = File.ReadLines(path).FirstOrDefault(line => line.Trim().Length > 0 && !line.StartsWith('#'))?.TrimStart('﻿').Split('\t').Select(c => c.Trim()).ToArray() ?? [];
            foreach (var unknown in header.Except(columns)) problems.Add(new("ERROR", file, 0, $"Unknown column `{unknown}`. This file has: {string.Join(", ", columns)}."));
            if (header.Length > 0 && !header.Contains(columns[0])) problems.Add(new("ERROR", file, 0, $"The first column is `{columns[0]}`."));
        }

        uint? UserId(CustomizationRow row, string column, HashSet<uint> seen)
        {
            var id = ParseId(row[column]);
            if (id is null) { Error(row, $"{column} `{row[column]}` is not an id."); return null; }
            if (id >> 28 != SourceUser) { Error(row, $"{column} `{row[column]}` must be a user id, such as u1."); return null; }
            if (!seen.Add(id.Value)) Error(row, $"{column} {FormatId(id.Value)} is used twice.");
            return id;
        }
        bool Number(CustomizationRow row, string column)
        {
            if (row[column].Length == 0 || uint.TryParse(row[column], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return true;
            Error(row, $"{column} `{row[column]}` is not a number.");
            return false;
        }
        void Asset(CustomizationRow row, string column, string value)
        {
            if (assets is null || assets.Count == 0 || value.Length == 0) return;
            var relative = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (!assets.Any(folder => File.Exists(Path.Combine(folder, relative))))
                Warn(row, $"{column} `{value}` is not in a loose folder given. It must then be in the client's archives or a retail source.");
        }

        var userModels = new HashSet<uint>();
        var seenSubjects = new HashSet<uint>();
        foreach (var row in ReadRows(customization, "subjects.tsv"))
        {
            var id = ParseId(row["subject"]);
            if (id is null) { Error(row, $"subject `{row["subject"]}` is not an id."); continue; }
            if (!seenSubjects.Add(id.Value)) Error(row, $"subject {FormatId(id.Value)} is used twice.");
            userModels.Add(id.Value);
            if (row["body"].Length == 0 && !catalog.Models.Contains(id.Value) && catalog.Loaded)
                Error(row, $"subject {FormatId(id.Value)} has no body file and no row in the catalog.");
            if (row["category"].Length == 0) Error(row, "category names no AppearanceCategories row, so no wardrobe grid would show it.");
            Number(row, "category"); Number(row, "native_display");
            if (row["native_display"] is "" or "0") Error(row, "native_display is required: the look keeps that display's scale, sounds and blood.");
            var matches = row["match"].Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (matches.Length == 0) Warn(row, "match is empty: it shows in the wardrobe and nothing in the world wears it.");
            foreach (var match in matches)
            {
                var parts = match.Trim().Split(':');
                if (parts.Length != 2 || parts[0] is not ("form" or "creature" or "family") || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    Error(row, $"match `{match}` is not form:<n>, creature:<entry> or family:<n>.");
            }
            Asset(row, "body", row["body"]);
        }

        var userOptions = new HashSet<uint>();
        var seenOptions = new HashSet<uint>();
        foreach (var row in ReadRows(customization, "options.tsv"))
        {
            if (UserId(row, "option", seenOptions) is { } id) userOptions.Add(id);
            var body = ParseId(row["body"]);
            if (body is null) Error(row, $"body `{row["body"]}` is not an id.");
            else if (catalog.Loaded && !catalog.Models.Contains(body.Value) && !userModels.Contains(body.Value))
                Error(row, $"body {FormatId(body.Value)} is in neither the catalog nor subjects.tsv.");
            if (row["name"].Length == 0) Error(row, "name is empty, and the screen shows it.");
            if (row["category"].Length == 0) Error(row, "category is empty: name a tab of the body, or a new one.");
            if (row["order"].Length > 0 && !int.TryParse(row["order"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) Error(row, $"order `{row["order"]}` is not a number.");
        }

        var seenChoices = new HashSet<uint>();
        foreach (var row in ReadRows(customization, "choices.tsv"))
        {
            UserId(row, "choice", seenChoices);
            var option = ParseId(row["option"]);
            if (option is null) Error(row, $"option `{row["option"]}` is not an id.");
            else if (catalog.Loaded && !catalog.Options.Contains(option.Value) && !userOptions.Contains(option.Value))
                Error(row, $"option {FormatId(option.Value)} is in neither the catalog nor options.tsv.");
            if (row["name"].Length == 0) Error(row, "name is empty, and the screen shows it.");
            Number(row, "order"); Number(row, "display");
            if (row["scale"].Length > 0 && (!float.TryParse(row["scale"], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) || scale <= 0)) Error(row, $"scale `{row["scale"]}` is not a positive number.");
            var names = new[] { "display", "model", "texture1", "texture2", "texture3", "geosets", "materials" };
            if (names.All(column => row[column].Length == 0)) Error(row, "names no display, model, texture, geoset or material, so it would draw nothing new.");
            if (row["display"] is not ("" or "0") && (row["model"].Length > 0 || row["texture1"].Length > 0)) Error(row, "names a display and a model or texture. The display would win; choose one.");
            foreach (var geoset in row["geosets"].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (!ushort.TryParse(geoset.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)) Error(row, $"geoset `{geoset}` is not xxyy.");
            foreach (var material in row["materials"].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = material.Split('=', 2);
                if (parts.Length != 2 || !uint.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _) || parts[1].Trim().Length == 0)
                { Error(row, $"material `{material}` is not target=file."); continue; }
                Asset(row, "materials", parts[1].Trim());
            }
            foreach (var swatch in row["swatch"].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (!uint.TryParse(swatch.Trim().TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) Error(row, $"swatch `{swatch}` is not RRGGBB.");
            Asset(row, "model", row["model"]);
            foreach (var column in new[] { "texture1", "texture2", "texture3" })
            {
                Asset(row, column, row[column]);
                if (row[column].Length > 0 && !row[column].EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) Warn(row, $"{column} `{row[column]}` does not end in .blp.");
            }
            if (row["model"].Length > 0 && !row["model"].EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) Warn(row, $"model `{row["model"]}` does not end in .m2.");
        }

        var seenItems = new HashSet<uint>();
        foreach (var row in ReadRows(customization, "items.tsv"))
        {
            UserId(row, "appearance", seenItems);
            Number(row, "category"); Number(row, "item");
            if (row["category"] is "" or "0") Error(row, "category names no AppearanceCategories row.");
            if (row["item"] is "" or "0") Error(row, "item names no item.");
        }
        return problems;
    }

    /// <summary>The next free user id of a file's first column.</summary>
    public static string NextUserId(string customization, string file)
    {
        var column = Columns[file][0];
        var used = ReadRows(customization, file).Select(row => ParseId(row[column])).Where(id => id >> 28 == SourceUser).Select(id => id!.Value & 0x0FFFFFFF).DefaultIfEmpty(0u).Max();
        return $"u{used + 1}";
    }

    /// <summary>
    /// Appends a row, first proving the folder with it is valid, so a bad row never reaches the game. Nothing is
    /// written on failure. The first column's id is assigned when none is given. Returns the row's id.
    /// </summary>
    public static string AddRow(string customization, string file, IReadOnlyDictionary<string, string> cells, out IReadOnlyList<CustomizationProblem> problems)
    {
        if (!Columns.TryGetValue(file, out var columns)) throw new ArgumentException($"No user file {file}.");
        var unknown = cells.Keys.Except(columns).FirstOrDefault();
        if (unknown is not null) throw new ArgumentException($"{file} has no column `{unknown}`. It has: {string.Join(", ", columns)}.");
        var folder = UserFolder(customization);
        var path = Path.Combine(folder, file);
        var values = new Dictionary<string, string>(cells, StringComparer.Ordinal);
        if (!values.ContainsKey(columns[0]) || values[columns[0]].Length == 0) values[columns[0]] = NextUserId(customization, file);
        foreach (var value in values.Values)
            if (value.Contains('\t') || value.Contains('\n') || value.Contains('\r')) throw new ArgumentException("A cell may not hold a tab or a line break.");

        var old = File.Exists(path) ? File.ReadAllText(path) : null;
        var header = old is null ? string.Join('\t', columns) + "\n" : "";
        var separator = old is { Length: > 0 } && !old.EndsWith('\n') ? "\n" : "";
        // Written in the order the file's own header has, which may be a subset of the columns.
        var order = old is null ? columns : (old.Split('\n').FirstOrDefault(line => line.Trim().Length > 0 && !line.StartsWith('#'))?.TrimEnd('\r').TrimStart('﻿').Split('\t').Select(c => c.Trim()).ToArray() ?? columns);
        var missing = values.Where(pair => pair.Value.Length > 0).Select(pair => pair.Key).Except(order).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"{file}'s header lacks {string.Join(", ", missing)}. Add the column first.");
        var line = string.Join('\t', order.Select(column => values.TryGetValue(column, out var value) ? value : "")) + "\n";

        Directory.CreateDirectory(folder);
        File.WriteAllText(path, (old ?? "") + separator + header + line, new UTF8Encoding(false));
        problems = Validate(customization);
        if (problems.Any(problem => problem.Severity == "ERROR"))
        {
            if (old is null) File.Delete(path); else File.WriteAllText(path, old, new UTF8Encoding(false));
            return "";
        }
        return values[columns[0]];
    }

    /// <summary>Copies a valid folder's files where the server reads them. Refuses on any error.</summary>
    public static IReadOnlyList<string> Export(string customization, string destinationCustomization, out IReadOnlyList<CustomizationProblem> problems)
    {
        problems = Validate(customization);
        if (problems.Any(problem => problem.Severity == "ERROR")) return [];
        var target = UserFolder(destinationCustomization);
        Directory.CreateDirectory(target);
        var written = new List<string>();
        foreach (var file in Columns.Keys)
        {
            var source = Path.Combine(UserFolder(customization), file);
            if (!File.Exists(source)) continue;
            var destination = Path.Combine(target, file);
            var temporary = destination + ".tmp";
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
            written.Add(destination);
        }
        return written;
    }

    /// <summary>The generated subjects and each one's options, for finding the id a row hangs on.</summary>
    public static IReadOnlyList<string> DescribeCatalog(string customization)
    {
        var lines = new List<string>();
        var subjects = Path.Combine(customization, "wardrobe_subject.tsv");
        if (!File.Exists(subjects)) return lines;
        var options = Path.Combine(customization, "wardrobe_option.tsv");
        var byModel = File.Exists(options)
            ? File.ReadLines(options).Skip(1).Select(line => line.Split('\t')).Where(cells => cells.Length >= 2).GroupBy(cells => cells[1]).ToDictionary(group => group.Key, group => group.Select(cells => cells[0]).ToArray())
            : [];
        foreach (var line in File.ReadLines(subjects).Skip(1))
        {
            var cells = line.Split('\t');
            if (cells.Length < 5 || ParseId(cells[0]) is not { } id) continue;
            var ids = byModel.TryGetValue(cells[0], out var list) ? string.Join(' ', list.Select(option => ParseId(option) is { } o ? FormatId(o) : option)) : "";
            lines.Add($"{FormatId(id)}\t{cells[1]}\tcategory {cells[2]}\t{cells[4]}\toptions: {ids}");
        }
        return lines;
    }
}
