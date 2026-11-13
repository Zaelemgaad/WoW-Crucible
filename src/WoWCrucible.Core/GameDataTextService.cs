using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace WoWCrucible.Core;

/// <summary>Tab-separated cells: \\, \t, \n, \r and \0 are escaped; \N is SQL NULL; \xHH is a raw byte of a DBC string.</summary>
public static class GameDataTsv
{
    public const string Null = "\\N";

    public static string Escape(string value)
    {
        if (value.AsSpan().IndexOfAny("\\\t\n\r\0") < 0) return value;
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
            builder.Append(c switch { '\\' => "\\\\", '\t' => "\\t", '\n' => "\\n", '\r' => "\\r", '\0' => "\\0", _ => c.ToString() });
        return builder.ToString();
    }

    public static string Unescape(string cell)
    {
        if (!cell.Contains('\\')) return cell;
        var builder = new StringBuilder(cell.Length);
        for (var i = 0; i < cell.Length; i++)
        {
            if (cell[i] != '\\') { builder.Append(cell[i]); continue; }
            if (++i >= cell.Length) throw new InvalidDataException("A cell ends in a lone backslash.");
            builder.Append(cell[i] switch { '\\' => '\\', 't' => '\t', 'n' => '\n', 'r' => '\r', '0' => '\0', _ => throw new InvalidDataException($"Unknown escape \\{cell[i]}.") });
        }
        return builder.ToString();
    }

    /// <summary>DBC strings are bytes: valid UTF-8 is written as text, anything else byte by byte as \xHH.</summary>
    public static string EncodeBytes(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (Encoding.UTF8.GetByteCount(text) == bytes.Length && Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(bytes)) return Escape(text);
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            builder.Append(b >= 0x80 ? $"\\x{b:X2}" : Escape(((char)b).ToString()));
        return builder.ToString();
    }

    public static byte[] DecodeBytes(string cell)
    {
        if (!cell.Contains('\\')) return Encoding.UTF8.GetBytes(cell);
        var result = new List<byte>(cell.Length);
        var run = new StringBuilder();
        void Flush() { if (run.Length > 0) { result.AddRange(Encoding.UTF8.GetBytes(run.ToString())); run.Clear(); } }
        for (var i = 0; i < cell.Length; i++)
        {
            if (cell[i] != '\\') { run.Append(cell[i]); continue; }
            if (++i >= cell.Length) throw new InvalidDataException("A cell ends in a lone backslash.");
            if (cell[i] == 'x')
            {
                if (i + 2 >= cell.Length) throw new InvalidDataException("A \\x escape needs two hex digits.");
                Flush(); result.Add(byte.Parse(cell.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 2; continue;
            }
            run.Append(cell[i] switch { '\\' => '\\', 't' => '\t', 'n' => '\n', 'r' => '\r', '0' => '\0', _ => throw new InvalidDataException($"Unknown escape \\{cell[i]}.") });
        }
        Flush();
        return result.ToArray();
    }

    public static void Write(string path, IReadOnlyList<string> header, IEnumerable<string[]> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20) { NewLine = "\n" };
        writer.WriteLine(string.Join('\t', header));
        foreach (var row in rows) writer.WriteLine(string.Join('\t', row));
    }

    public static (string[] Header, List<string[]> Rows) Read(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), false, 1 << 20);
        var header = (reader.ReadLine() ?? throw new InvalidDataException($"{path} is empty.")).Split('\t');
        var rows = new List<string[]>();
        string? line; var number = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            number++;
            var cells = line.Split('\t');
            if (cells.Length != header.Length) throw new InvalidDataException($"{path}:{number} has {cells.Length} cells; the header has {header.Length}.");
            rows.Add(cells);
        }
        return (header, rows);
    }
}

public sealed record GameDataTableResult(string Set, string Path, string Status, int Rows, string Message);

/// <summary>
/// DBC tables as TSV with WoWDBDefs column names (build 3.3.5.12340), and back to byte-identical DBCs.
/// The string block is rebuilt in row order; tables.tsv records which of the known layouts reproduces it.
/// </summary>
public static class GameDataDbcService
{
    public const int Build = 12340;
    public const string ManifestName = "tables.tsv";
    private static readonly string[] ManifestHeader = ["set", "path", "strings", "fields", "rows", "source_sha256"];

    // blizzard: one leading NUL, empty strings at 0, others in first-use order.
    // inline: every string, empty ones included, in first-use order (Ascension's custom tables).
    // raw: string columns stay offsets and the block is kept as its own .strings.tsv.
    public static readonly string[] Layouts = ["blizzard", "inline"];

    public sealed record Entry(string Set, string Path, string Strings, int Fields, int Rows, string SourceSha256);

    private sealed record Wdbc(int Rows, int Fields, int RecordSize, byte[] Records, byte[] Strings);

    private static Wdbc Parse(byte[] bytes, string name)
    {
        if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("WDBC"u8)) throw new InvalidDataException($"{name} is not a WDBC table.");
        int Word(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        var rows = Word(4); var fields = Word(8); var size = Word(12); var strings = Word(16);
        if (rows < 0 || fields <= 0 || size <= 0 || strings < 0 || 20L + (long)rows * size + strings != bytes.Length)
            throw new InvalidDataException($"{name}: the WDBC header does not match the file length.");
        return new(rows, fields, size, bytes.AsSpan(20, rows * size).ToArray(), bytes.AsSpan(20 + rows * size).ToArray());
    }

    /// <summary>The first definition root holding Table.dbd with a 3.3.5.12340 layout of this record size wins.</summary>
    public static IReadOnlyList<DbcColumn>? ResolveColumns(string table, int? recordSize, IReadOnlyList<string> definitionRoots)
    {
        foreach (var root in definitionRoots)
        {
            var path = Path.Combine(root, table + ".dbd");
            if (!File.Exists(path)) continue;
            var definition = DbdSchemaService.Load(path);
            var layout = definition.ForBuild(Build);
            if (layout is null) continue;
            var columns = DbdSchemaService.ResolveColumns(definition, layout, Build);
            var size = columns.Count == 0 ? 0 : columns.Max(column => column.Offset + column.Size);
            if (recordSize is null || size == recordSize) return columns;
        }
        return null;
    }

    private static IReadOnlyList<DbcColumn> RawColumns(int recordSize)
    {
        var columns = new List<DbcColumn>();
        var offset = 0;
        for (; offset + 4 <= recordSize; offset += 4) columns.Add(new(columns.Count, offset, 4, $"Field_{offset}", DbcValueType.UInt32));
        for (; offset < recordSize; offset++) columns.Add(new(columns.Count, offset, 1, $"Field_{offset}", DbcValueType.Byte));
        return columns;
    }

    private static ulong ReadRaw(byte[] records, int start, DbcColumn column) => column.Size switch
    {
        1 => records[start + column.Offset],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(records.AsSpan(start + column.Offset)),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(start + column.Offset)),
        8 => BinaryPrimitives.ReadUInt64LittleEndian(records.AsSpan(start + column.Offset)),
        _ => throw new NotSupportedException($"{column.Name} is {column.Size} bytes wide.")
    };

    private static void WriteRaw(byte[] records, int start, DbcColumn column, ulong value)
    {
        var cell = records.AsSpan(start + column.Offset, column.Size);
        if (column.Size == 1) cell[0] = checked((byte)value);
        else if (column.Size == 2) BinaryPrimitives.WriteUInt16LittleEndian(cell, checked((ushort)value));
        else if (column.Size == 4) BinaryPrimitives.WriteUInt32LittleEndian(cell, checked((uint)value));
        else BinaryPrimitives.WriteUInt64LittleEndian(cell, value);
    }

    private static string FormatValue(DbcColumn column, ulong raw)
    {
        switch (column.Type)
        {
            case DbcValueType.Float32:
                var bits = (uint)raw; var value = BitConverter.UInt32BitsToSingle(bits);
                if (float.IsFinite(value))
                {
                    var text = value.ToString("R", CultureInfo.InvariantCulture);
                    if (BitConverter.SingleToUInt32Bits(float.Parse(text, CultureInfo.InvariantCulture)) == bits) return text;
                }
                return $"0x{bits:X8}";
            case DbcValueType.Int32 or DbcValueType.Int64:
                var width = column.EffectiveBitWidth;
                var signed = width >= 64 ? (long)raw : (long)(raw << (64 - width)) >> (64 - width);
                return signed.ToString(CultureInfo.InvariantCulture);
            default:
                return raw.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static ulong ParseValue(DbcColumn column, string text)
    {
        if (column.Type == DbcValueType.Float32)
            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : BitConverter.SingleToUInt32Bits(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
        if (column.Type is DbcValueType.Int32 or DbcValueType.Int64)
        {
            var value = long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var width = column.EffectiveBitWidth;
            if (width < 64 && (value < -(1L << (width - 1)) || value >= 1L << width)) throw new OverflowException($"{column.Name} = {text} does not fit {width} bits.");
            return width >= 64 ? (ulong)value : (ulong)value & ((1UL << width) - 1);
        }
        var unsigned = ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        if (column.Size < 8 && unsigned >> (column.Size * 8) != 0) throw new OverflowException($"{column.Name} = {text} does not fit {column.Size} bytes.");
        return unsigned;
    }

    /// <summary>Writes string offsets into records and returns the string block for a layout.</summary>
    private static byte[] AssembleStrings(byte[] records, int rows, int recordSize, IReadOnlyList<DbcColumn> stringColumns, byte[]?[] cells, string layout)
    {
        using var block = new MemoryStream();
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var blizzard = layout == "blizzard";
        if (blizzard) { block.WriteByte(0); offsets[""] = 0; }
        for (var row = 0; row < rows; row++)
            for (var index = 0; index < stringColumns.Count; index++)
            {
                var value = cells[row * stringColumns.Count + index];
                if (value is null) continue; // an offset kept as a number
                var key = Encoding.Latin1.GetString(value);
                if (!offsets.TryGetValue(key, out var offset))
                {
                    offset = checked((uint)block.Length);
                    block.Write(value); block.WriteByte(0);
                    offsets[key] = offset;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(row * recordSize + stringColumns[index].Offset), offset);
            }
        return block.ToArray();
    }

    private static byte[] Container(int rows, int fields, int recordSize, byte[] records, byte[] strings)
    {
        var bytes = new byte[20 + records.Length + strings.Length];
        "WDBC"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), rows);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), fields);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), recordSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), strings.Length);
        records.CopyTo(bytes, 20); strings.CopyTo(bytes, 20 + records.Length);
        return bytes;
    }

    private static byte[]? StringAt(byte[] block, ulong offset)
    {
        if (offset >= (ulong)block.Length) return null;
        var end = Array.IndexOf(block, (byte)0, (int)offset);
        return end < 0 ? null : block.AsSpan((int)offset, end - (int)offset).ToArray();
    }

    private static string DescribeBlock(Wdbc dbc, IReadOnlyList<DbcColumn> stringColumns)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < dbc.Strings.Length - 1; i++) if (dbc.Strings[i] == 0) starts.Add(i + 1);
        var startSet = starts.ToHashSet();
        var referenced = new HashSet<int>();
        var order = new List<int>();
        for (var row = 0; row < dbc.Rows; row++)
            foreach (var column in stringColumns)
            {
                var offset = (int)ReadRaw(dbc.Records, row * dbc.RecordSize, column);
                if (referenced.Add(offset)) order.Add(offset);
            }
        var middle = referenced.Count(offset => !startSet.Contains(offset));
        var unreferenced = starts.Count(start => !referenced.Contains(start));
        var seen = new HashSet<string>(StringComparer.Ordinal); var duplicates = 0;
        foreach (var start in starts) if (!seen.Add(Encoding.Latin1.GetString(StringAt(dbc.Strings, (ulong)start) ?? []))) duplicates++;
        var ordered = order.Where(startSet.Contains).ToList();
        var outOfOrder = ordered.Zip(ordered.Skip(1)).Count(pair => pair.First > pair.Second);
        return $"{starts.Count:N0} strings, {middle:N0} references inside a string, {unreferenced:N0} unreferenced, {duplicates:N0} duplicates, {outOfOrder:N0} first uses out of order";
    }

    private static bool IsOtherLocale(DbcColumn column) =>
        column.Type == DbcValueType.StringOffset && column.Name.EndsWith(']') && !column.Name.EndsWith("[enUS]", StringComparison.Ordinal) && Regex.IsMatch(column.Name, @"\[(?:[a-z]{2}[A-Z]{2}|Locale\d+)\]$");

    /// <summary>Writes Table.tsv (and Table.strings.tsv for the raw layout) and returns the manifest entry.</summary>
    public static Entry ExportTable(byte[] source, string set, string relativePath, string tsvPath, IReadOnlyList<string> definitionRoots, out string message)
    {
        var table = Path.GetFileNameWithoutExtension(relativePath);
        var dbc = Parse(source, relativePath);
        var sha = Convert.ToHexString(SHA256.HashData(source));
        var columns = ResolveColumns(table, dbc.RecordSize, definitionRoots);
        var defined = columns is not null;
        columns ??= RawColumns(dbc.RecordSize);
        var stringColumns = columns.Where(column => column.Type == DbcValueType.StringOffset).ToArray();

        string layout = "raw";
        byte[]?[]? cells = null;
        message = defined ? "" : "no 3.3.5.12340 definition; columns are Field_<offset> and the string block is kept as it is";
        if (defined)
        {
            // A cell whose offset lies outside the block (garbage in an unused locale) stays that number: \o<offset>.
            cells = new byte[dbc.Rows * stringColumns.Length][];
            var invalid = 0;
            for (var row = 0; row < dbc.Rows; row++)
                for (var index = 0; index < stringColumns.Length; index++)
                    if ((cells[row * stringColumns.Length + index] = StringAt(dbc.Strings, ReadRaw(dbc.Records, row * dbc.RecordSize, stringColumns[index]))) is null) invalid++;
            foreach (var candidate in Layouts)
            {
                var records = dbc.Records.ToArray();
                var strings = AssembleStrings(records, dbc.Rows, dbc.RecordSize, stringColumns, cells, candidate);
                if (strings.AsSpan().SequenceEqual(dbc.Strings) && records.AsSpan().SequenceEqual(dbc.Records)) { layout = candidate; break; }
            }
            if (layout == "raw")
            {
                // The values are kept as text; the rebuilt block is in canonical order and verify proves the values equal.
                layout = "blizzard";
                message = "string block not reproducible byte for byte (" + DescribeBlock(dbc, stringColumns) + "); rebuilt in canonical order";
            }
            if (invalid > 0) message = (message.Length > 0 ? message + "; " : "") + $"{invalid:N0} string offset(s) outside the block kept as \\o<offset>";
        }

        var raw = layout == "raw";
        var kept = columns.Where(column => raw || !IsOtherLocale(column)).ToArray();
        var stringIndex = stringColumns.Select((column, index) => (column, index)).ToDictionary(pair => pair.column.Index, pair => pair.index);
        string StringCell(int row, DbcColumn column) => cells![row * stringColumns.Length + stringIndex[column.Index]] is { } text
            ? GameDataTsv.EncodeBytes(text) : "\\o" + ReadRaw(dbc.Records, row * dbc.RecordSize, column).ToString(CultureInfo.InvariantCulture);
        var localesPath = Path.ChangeExtension(tsvPath, ".locales.tsv");
        var locales = raw ? [] : columns.Where(IsOtherLocale).ToArray();
        if (locales.Length > 0)
        {
            // Locales other than enUS go to Table.locales.tsv: a "*" line per column's most common value, then each cell that differs.
            var keyName = LocaleKeyName(kept.Select(column => column.Name).ToArray(), Enumerable.Range(0, dbc.Rows).Select(row => FormatValue(kept[0], ReadRaw(dbc.Records, row * dbc.RecordSize, kept[0]))).ToArray());
            var defaults = locales.ToDictionary(column => column.Index, column => Enumerable.Range(0, dbc.Rows).Select(row => StringCell(row, column))
                .GroupBy(value => value, StringComparer.Ordinal).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Select(group => group.Key).FirstOrDefault() ?? "");
            var lines = new List<string[]>();
            foreach (var column in locales) if (defaults[column.Index].Length > 0) lines.Add(["*", column.Name, defaults[column.Index]]);
            for (var row = 0; row < dbc.Rows; row++)
                foreach (var column in locales)
                {
                    var value = StringCell(row, column);
                    if (value != defaults[column.Index])
                        lines.Add([keyName == "row" ? (row + 1).ToString(CultureInfo.InvariantCulture) : FormatValue(kept[0], ReadRaw(dbc.Records, row * dbc.RecordSize, kept[0])), column.Name, value]);
                }
            if (lines.Count > 0) GameDataTsv.Write(localesPath, [keyName, "column", "value"], lines);
            else if (File.Exists(localesPath)) File.Delete(localesPath);
        }
        else if (File.Exists(localesPath)) File.Delete(localesPath);
        IEnumerable<string[]> Rows()
        {
            for (var row = 0; row < dbc.Rows; row++)
            {
                var start = row * dbc.RecordSize;
                var line = new string[kept.Length];
                for (var i = 0; i < kept.Length; i++)
                {
                    var column = kept[i];
                    line[i] = !raw && column.Type == DbcValueType.StringOffset ? StringCell(row, column)
                        : raw && column.Type == DbcValueType.StringOffset ? ReadRaw(dbc.Records, start, column).ToString(CultureInfo.InvariantCulture)
                        : FormatValue(column, ReadRaw(dbc.Records, start, column));
                }
                yield return line;
            }
        }
        GameDataTsv.Write(tsvPath, kept.Select(column => column.Name).ToArray(), Rows());
        var stringsPath = Path.ChangeExtension(tsvPath, ".strings.tsv");
        if (raw)
        {
            var segments = new List<string[]>();
            var start = 0;
            for (var i = 0; i <= dbc.Strings.Length; i++)
                if (i == dbc.Strings.Length || dbc.Strings[i] == 0) { segments.Add([GameDataTsv.EncodeBytes(dbc.Strings.AsSpan(start, i - start))]); start = i + 1; }
            GameDataTsv.Write(stringsPath, ["string"], segments);
        }
        else if (File.Exists(stringsPath)) File.Delete(stringsPath);
        return new(set, relativePath.Replace('\\', '/'), layout, dbc.Fields, dbc.Rows, sha);
    }

    /// <summary>Builds one DBC from its TSV, the manifest entry and the same definitions.</summary>
    public static byte[] BuildTable(Entry entry, string tsvPath, IReadOnlyList<string> definitionRoots)
    {
        var table = Path.GetFileNameWithoutExtension(entry.Path);
        var (header, lines) = GameDataTsv.Read(tsvPath);
        if (lines.Count != entry.Rows) throw new InvalidDataException($"{tsvPath} has {lines.Count:N0} rows; tables.tsv says {entry.Rows:N0}.");
        var columns = ResolveColumns(table, null, definitionRoots);
        if (columns is null)
        {
            if (entry.Strings != "raw" || header.Any(name => !name.StartsWith("Field_", StringComparison.Ordinal)))
                throw new InvalidDataException($"{table} has no definition; only Field_<offset> columns in the raw layout build without one.");
            columns = RawColumns(RawWidth(header));
        }
        var recordSize = columns.Max(column => column.Offset + column.Size);
        var byName = columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var positions = new int[columns.Count];
        Array.Fill(positions, -1);
        for (var i = 0; i < header.Length; i++)
        {
            if (!byName.TryGetValue(header[i], out var column)) throw new InvalidDataException($"{tsvPath}: {header[i]} is not a {table} column.");
            if (positions[column.Index] >= 0) throw new InvalidDataException($"{tsvPath}: {header[i]} appears twice.");
            positions[column.Index] = i;
        }
        var raw = entry.Strings == "raw";
        foreach (var column in columns)
            if (positions[column.Index] < 0 && (raw || !IsOtherLocale(column)))
                throw new InvalidDataException($"{tsvPath} lacks the {column.Name} column.");

        // Locales other than enUS: per-column defaults ("*") and per-row values from Table.locales.tsv.
        var localeDefaults = new Dictionary<string, string>(StringComparer.Ordinal);
        var localeCells = new Dictionary<(string Key, string Column), string>();
        string? keyName = null;
        var localesPath = Path.ChangeExtension(tsvPath, ".locales.tsv");
        if (!raw && File.Exists(localesPath))
        {
            var (localeHeader, localeLines) = GameDataTsv.Read(localesPath);
            keyName = LocaleKeyName(header, lines.Select(line => line[0]).ToArray());
            if (localeHeader.Length != 3 || localeHeader[0] != keyName) throw new InvalidDataException($"{localesPath} must have the header {keyName}, column, value.");
            foreach (var line in localeLines)
            {
                if (!byName.TryGetValue(line[1], out var column) || !IsOtherLocale(column)) throw new InvalidDataException($"{localesPath}: {line[1]} is not a localized {table} column.");
                if (line[0] == "*") localeDefaults[line[1]] = line[2];
                else if (!localeCells.TryAdd((line[0], line[1]), line[2])) throw new InvalidDataException($"{localesPath}: {line[0]} {line[1]} appears twice.");
            }
        }

        var records = new byte[lines.Count * recordSize];
        var stringColumns = columns.Where(column => column.Type == DbcValueType.StringOffset).ToArray();
        var cells = new byte[]?[lines.Count * stringColumns.Length];
        for (var row = 0; row < lines.Count; row++)
        {
            var s = 0;
            var key = keyName == "row" ? (row + 1).ToString(CultureInfo.InvariantCulture) : lines[row][0];
            foreach (var column in columns)
            {
                var position = positions[column.Index];
                var text = position >= 0 ? lines[row][position]
                    : localeCells.TryGetValue((key, column.Name), out var cell) ? cell : localeDefaults.GetValueOrDefault(column.Name, "");
                if (!raw && column.Type == DbcValueType.StringOffset && !text.StartsWith("\\o", StringComparison.Ordinal))
                {
                    cells[row * stringColumns.Length + s++] = GameDataTsv.DecodeBytes(text);
                    continue;
                }
                if (column.Type == DbcValueType.StringOffset && !raw) { s++; text = text[2..]; }
                try { WriteRaw(records, row * recordSize, column, column.Type == DbcValueType.StringOffset ? uint.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture) : ParseValue(column, text)); }
                catch (Exception error) when (error is FormatException or OverflowException) { throw new InvalidDataException($"{tsvPath}:{row + 2} {column.Name} = '{text}': {error.Message}"); }
            }
        }
        byte[] strings;
        if (raw)
        {
            var stringsPath = Path.ChangeExtension(tsvPath, ".strings.tsv");
            var (_, segments) = GameDataTsv.Read(stringsPath);
            using var block = new MemoryStream();
            for (var i = 0; i < segments.Count; i++) { if (i > 0) block.WriteByte(0); block.Write(GameDataTsv.DecodeBytes(segments[i][0])); }
            strings = block.ToArray();
        }
        else strings = AssembleStrings(records, lines.Count, recordSize, stringColumns, cells, entry.Strings);
        return Container(lines.Count, entry.Fields, recordSize, records, strings);
    }

    /// <summary>Locale lines name their row by the first column when it is a unique ID, else by row number.</summary>
    private static string LocaleKeyName(IReadOnlyList<string> header, IReadOnlyList<string> firstColumn) =>
        header[0] == "ID" && firstColumn.Distinct(StringComparer.Ordinal).Count() == firstColumn.Count ? "ID" : "row";

    private static int RawWidth(string[] names)
    {
        var last = names[^1]; var offset = int.Parse(last["Field_".Length..], CultureInfo.InvariantCulture);
        var previous = names.Length > 1 ? int.Parse(names[^2]["Field_".Length..], CultureInfo.InvariantCulture) : -4;
        return offset + (offset - previous == 1 ? 1 : 4);
    }

    /// <summary>"identical", or "same values" when only the string block differs in order, else the first difference.</summary>
    public static string Compare(byte[] original, byte[] rebuilt, string table, IReadOnlyList<string> definitionRoots)
    {
        if (original.AsSpan().SequenceEqual(rebuilt)) return "identical";
        var a = Parse(original, table); var b = Parse(rebuilt, table);
        if (a.Rows != b.Rows || a.Fields != b.Fields || a.RecordSize != b.RecordSize) return $"header differs ({a.Rows}/{a.Fields}/{a.RecordSize} vs {b.Rows}/{b.Fields}/{b.RecordSize})";
        var columns = ResolveColumns(table, a.RecordSize, definitionRoots) ?? RawColumns(a.RecordSize);
        for (var row = 0; row < a.Rows; row++)
            foreach (var column in columns)
            {
                var x = ReadRaw(a.Records, row * a.RecordSize, column); var y = ReadRaw(b.Records, row * b.RecordSize, column);
                if (column.Type == DbcValueType.StringOffset)
                {
                    var left = StringAt(a.Strings, x); var right = StringAt(b.Strings, y);
                    if (left is null ? x != y : right is null || !left.AsSpan().SequenceEqual(right)) return $"row {row + 1} {column.Name} differs";
                }
                else if (x != y) return $"row {row + 1} {column.Name} differs ({x} vs {y})";
            }
        // Padding bytes between columns are compared too.
        var covered = new bool[a.RecordSize];
        foreach (var column in columns) for (var i = 0; i < column.Size; i++) covered[column.Offset + i] = true;
        for (var row = 0; row < a.Rows; row++)
            for (var i = 0; i < a.RecordSize; i++)
                if (!covered[i] && a.Records[row * a.RecordSize + i] != b.Records[row * b.RecordSize + i]) return $"row {row + 1} byte {i} differs";
        return "same values";
    }

    public static List<Entry> ReadManifest(string gamedata)
    {
        var path = Path.Combine(gamedata, "dbc", ManifestName);
        if (!File.Exists(path)) return [];
        var (header, rows) = GameDataTsv.Read(path);
        if (!header.SequenceEqual(ManifestHeader)) throw new InvalidDataException($"{path} has an unknown header.");
        return rows.Select(row => new Entry(row[0], row[1], row[2], int.Parse(row[3], CultureInfo.InvariantCulture), int.Parse(row[4], CultureInfo.InvariantCulture), row[5])).ToList();
    }

    public static void WriteManifest(string gamedata, IEnumerable<Entry> entries) =>
        GameDataTsv.Write(Path.Combine(gamedata, "dbc", ManifestName), ManifestHeader,
            entries.OrderBy(entry => entry.Set, StringComparer.Ordinal).ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new[] { entry.Set, entry.Path, entry.Strings, entry.Fields.ToString(CultureInfo.InvariantCulture), entry.Rows.ToString(CultureInfo.InvariantCulture), entry.SourceSha256 }));

    public static string TsvPath(string gamedata, Entry entry) => TsvPath(gamedata, entry.Set, entry.Path);
    public static string TsvPath(string gamedata, string set, string path) =>
        Path.Combine(gamedata, "dbc", set, Path.ChangeExtension(path.Replace('/', Path.DirectorySeparatorChar), ".tsv"));

    /// <summary>
    /// Rewrites gamedata/dbc from the given sets: a table byte-identical in both goes to shared/, a table that
    /// differs or exists on one side only to client/ or server/.
    /// </summary>
    public static List<GameDataTableResult> Export(string gamedata, IReadOnlyDictionary<string, string>? server, IReadOnlyDictionary<string, string>? client,
        IReadOnlyList<string> definitionRoots, Action<GameDataTableResult>? progress = null)
    {
        var dbcFolder = Path.Combine(gamedata, "dbc");
        foreach (var set in new[] { "shared", "server", "client" })
            if (Directory.Exists(Path.Combine(dbcFolder, set))) Directory.Delete(Path.Combine(dbcFolder, set), true);
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (server is not null) keys.UnionWith(server.Keys);
        if (client is not null) keys.UnionWith(client.Keys);
        var entries = new List<Entry>();
        var results = new List<GameDataTableResult>();
        foreach (var key in keys)
        {
            var serverBytes = server is not null && server.TryGetValue(key, out var serverPath) ? File.ReadAllBytes(serverPath) : null;
            var clientBytes = client is not null && client.TryGetValue(key, out var clientPath) ? File.ReadAllBytes(clientPath) : null;
            var sources = new List<(string Set, byte[] Bytes)>();
            if (serverBytes is not null && clientBytes is not null && serverBytes.AsSpan().SequenceEqual(clientBytes)) sources.Add(("shared", serverBytes));
            else
            {
                if (serverBytes is not null) sources.Add(("server", serverBytes));
                if (clientBytes is not null) sources.Add(("client", clientBytes));
            }
            foreach (var (set, bytes) in sources)
            {
                GameDataTableResult result;
                try
                {
                    var entry = ExportTable(bytes, set, key, TsvPath(gamedata, set, key), definitionRoots, out var message);
                    entries.Add(entry);
                    result = new(set, key, entry.Strings, entry.Rows, message);
                }
                catch (InvalidDataException error) { result = new(set, key, "failed", 0, error.Message); }
                results.Add(result);
                progress?.Invoke(result);
            }
        }
        WriteManifest(gamedata, entries);
        return results;
    }

    /// <summary>Builds every table of the manifest; shared tables go to both outputs.</summary>
    public static List<GameDataTableResult> BuildAll(string gamedata, string serverOut, string clientOut, IReadOnlyList<string> definitionRoots, Action<GameDataTableResult>? progress = null)
    {
        var results = new List<GameDataTableResult>();
        foreach (var entry in ReadManifest(gamedata))
        {
            var bytes = BuildTable(entry, TsvPath(gamedata, entry), definitionRoots);
            foreach (var root in entry.Set switch { "shared" => new[] { serverOut, clientOut }, "server" => [serverOut], _ => [clientOut] })
            {
                var target = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
            var result = new GameDataTableResult(entry.Set, entry.Path, "built", entry.Rows, "");
            results.Add(result); progress?.Invoke(result);
        }
        return results;
    }

    /// <summary>Builds each table from its TSV in memory and compares it with the given originals.</summary>
    public static List<GameDataTableResult> Verify(string gamedata, IReadOnlyDictionary<string, string>? server, IReadOnlyDictionary<string, string>? client,
        IReadOnlyList<string> definitionRoots, Action<GameDataTableResult>? progress = null)
    {
        var results = new List<GameDataTableResult>();
        foreach (var entry in ReadManifest(gamedata))
        {
            var originals = new List<(string Side, string Path)>();
            if (entry.Set is "shared" or "server" && server is not null && server.TryGetValue(entry.Path, out var serverPath)) originals.Add(("server", serverPath));
            if (entry.Set is "shared" or "client" && client is not null && client.TryGetValue(entry.Path, out var clientPath)) originals.Add(("client", clientPath));
            if (originals.Count == 0) continue;
            byte[] rebuilt;
            try { rebuilt = BuildTable(entry, TsvPath(gamedata, entry), definitionRoots); }
            catch (InvalidDataException error) { var failed = new GameDataTableResult(entry.Set, entry.Path, "failed", entry.Rows, error.Message); results.Add(failed); progress?.Invoke(failed); continue; }
            foreach (var (side, path) in originals)
            {
                var verdict = Compare(File.ReadAllBytes(path), rebuilt, Path.GetFileNameWithoutExtension(entry.Path), definitionRoots);
                var result = new GameDataTableResult(entry.Set, entry.Path + (entry.Set == "shared" ? $" ({side})" : ""), verdict, entry.Rows, "");
                results.Add(result); progress?.Invoke(result);
            }
        }
        return results;
    }

    /// <summary>Every *.dbc below a folder, keyed by its relative path. Folders named in skip are left out.</summary>
    public static SortedDictionary<string, string> FolderTables(string folder, IReadOnlyCollection<string> skip)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*.dbc", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (relative.Split('/')[..^1].Any(part => skip.Contains(part, StringComparer.OrdinalIgnoreCase))) continue;
            result[relative] = file;
        }
        return result;
    }

    /// <summary>Extracts the winning DBFilesClient\*.dbc of an archive list (lowest priority first) into a folder.</summary>
    public static SortedDictionary<string, string> ExtractClientTables(IReadOnlyList<string> archivesLowestFirst, string folder, Action<string>? progress = null)
    {
        var archives = new PatchArchiveService();
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(folder);
        for (var i = archivesLowestFirst.Count - 1; i >= 0; i--)
        {
            var archive = archivesLowestFirst[i];
            var entries = archives.ListFiles(archive, "*.dbc")
                .Where(entry => entry.ArchivePath.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase) && entry.Locale is 0 or 0x409)
                .GroupBy(entry => entry.ArchivePath["DBFilesClient\\".Length..], StringComparer.OrdinalIgnoreCase);
            var taken = 0;
            foreach (var group in entries)
            {
                var name = group.Key.Replace('\\', '/');
                if (name.Contains('/') || result.ContainsKey(name)) continue;
                var entry = group.OrderBy(candidate => candidate.Locale).First();
                var target = Path.Combine(folder, name);
                archives.ExtractFile(archive, target, entry);
                result[name] = target; taken++;
            }
            if (taken > 0) progress?.Invoke($"{Path.GetFileName(archive)}\t{taken}");
        }
        return result;
    }
}

/// <summary>World database tables as TSV plus their CREATE TABLE statements, and back to one SQL file.</summary>
public static partial class GameDataWorldService
{
    public const string SchemaName = "schema.sql";

    public static string ConnectionString(string host, int port, string user, string password, string database) =>
        new MySqlConnectionStringBuilder
        {
            Server = host, Port = (uint)port, UserID = user, Password = password, Database = database,
            CharacterSet = "utf8mb4", TreatTinyAsBoolean = false, AllowZeroDateTime = true, ConvertZeroDateTime = false,
            DefaultCommandTimeout = 0, ConnectionTimeout = 10, SslMode = MySqlSslMode.Preferred
        }.ConnectionString;

    private static readonly string[] Temporal = ["date", "datetime", "timestamp", "time", "year"];

    /// <summary>Reads every base table, ordered by its primary key (or every column), into world/Table.tsv and world/schema.sql.</summary>
    public static List<GameDataTableResult> Export(string connectionString, string worldFolder, IReadOnlyCollection<string>? only, Action<string>? progress = null)
    {
        Directory.CreateDirectory(worldFolder);
        using var connection = new MySqlConnection(connectionString);
        connection.Open();
        var database = connection.Database;
        var tables = new List<string>();
        using (var command = new MySqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema=@db AND table_type='BASE TABLE' ORDER BY table_name", connection))
        {
            command.Parameters.AddWithValue("@db", database);
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        if (only is { Count: > 0 }) tables = tables.Where(table => only.Contains(table, StringComparer.OrdinalIgnoreCase)).ToList();
        var schema = new StringBuilder();
        var results = new List<GameDataTableResult>();
        foreach (var table in tables)
        {
            string create;
            using (var command = new MySqlCommand($"SHOW CREATE TABLE `{table}`", connection))
            using (var reader = command.ExecuteReader()) { reader.Read(); create = reader.GetString(1); }
            create = AutoIncrement().Replace(create, "");
            schema.Append(create.Replace("\r\n", "\n")).Append(";\n\n");

            var columns = new List<(string Name, string Type)>();
            var key = new List<string>();
            using (var command = new MySqlCommand("SELECT column_name, data_type, column_key FROM information_schema.columns WHERE table_schema=@db AND table_name=@t ORDER BY ordinal_position", connection))
            {
                command.Parameters.AddWithValue("@db", database); command.Parameters.AddWithValue("@t", table);
                using var reader = command.ExecuteReader();
                while (reader.Read()) columns.Add((reader.GetString(0), reader.GetString(1).ToLowerInvariant()));
            }
            using (var command = new MySqlCommand("SELECT column_name FROM information_schema.key_column_usage WHERE table_schema=@db AND table_name=@t AND constraint_name='PRIMARY' ORDER BY ordinal_position", connection))
            {
                command.Parameters.AddWithValue("@db", database); command.Parameters.AddWithValue("@t", table);
                using var reader = command.ExecuteReader();
                while (reader.Read()) key.Add(reader.GetString(0));
            }
            var order = (key.Count > 0 ? key : columns.Select(column => column.Name)).Select(name => $"`{name}`");
            var select = string.Join(", ", columns.Select(column => Temporal.Contains(column.Type) ? $"CAST(`{column.Name}` AS CHAR)" : $"`{column.Name}`"));
            var path = Path.Combine(worldFolder, table + ".tsv");
            var rows = 0;
            using (var command = new MySqlCommand($"SELECT {select} FROM `{table}` ORDER BY {string.Join(", ", order)}", connection))
            using (var reader = command.ExecuteReader())
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20) { NewLine = "\n" })
            {
                writer.WriteLine(string.Join('\t', columns.Select(column => column.Name)));
                var line = new string[columns.Count];
                while (reader.Read())
                {
                    for (var i = 0; i < line.Length; i++) line[i] = Format(reader, i);
                    writer.WriteLine(string.Join('\t', line));
                    rows++;
                }
            }
            results.Add(new("world", table, "exported", rows, key.Count > 0 ? "" : "no primary key; ordered by every column"));
            progress?.Invoke($"{table}\t{rows}");
        }
        if (only is not { Count: > 0 }) File.WriteAllText(Path.Combine(worldFolder, SchemaName), schema.ToString(), new UTF8Encoding(false));
        return results;
    }

    private static string Format(MySqlDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return GameDataTsv.Null;
        return reader.GetValue(i) switch
        {
            string text => GameDataTsv.Escape(text),
            float value => value.ToString("R", CultureInfo.InvariantCulture),
            double value => value.ToString("R", CultureInfo.InvariantCulture),
            decimal value => value.ToString(CultureInfo.InvariantCulture),
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            var value => GameDataTsv.Escape(value.ToString() ?? "")
        };
    }

    /// <summary>Column name to SQL data type, per table, from schema.sql.</summary>
    public static Dictionary<string, (string Create, Dictionary<string, string> Types)> ReadSchema(string worldFolder)
    {
        var result = new Dictionary<string, (string, Dictionary<string, string>)>(StringComparer.OrdinalIgnoreCase);
        var text = File.ReadAllText(Path.Combine(worldFolder, SchemaName));
        foreach (var statement in text.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = CreateName().Match(statement);
            if (!name.Success) continue;
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match column in ColumnLine().Matches(statement)) types[column.Groups[1].Value] = column.Groups[2].Value.ToLowerInvariant();
            result[name.Groups[1].Value] = (statement, types);
        }
        return result;
    }

    private static readonly string[] Numeric = ["tinyint", "smallint", "mediumint", "int", "integer", "bigint", "float", "double", "decimal", "real", "bit"];
    private static readonly string[] Binary = ["binary", "varbinary", "tinyblob", "blob", "mediumblob", "longblob"];

    /// <summary>Writes DROP/CREATE and batched INSERTs for every table that has both a statement and a TSV.</summary>
    public static List<GameDataTableResult> BuildSql(string worldFolder, string outputSql, IReadOnlyCollection<string>? only)
    {
        var schema = ReadSchema(worldFolder);
        var results = new List<GameDataTableResult>();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputSql))!);
        using var writer = new StreamWriter(outputSql, false, new UTF8Encoding(false), 1 << 20) { NewLine = "\n" };
        writer.WriteLine("SET NAMES utf8mb4;\nSET FOREIGN_KEY_CHECKS=0;\nSET UNIQUE_CHECKS=0;\nSET SESSION sql_mode='NO_AUTO_VALUE_ON_ZERO';\n");
        foreach (var (table, (create, types)) in schema.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (only is { Count: > 0 } && !only.Contains(table, StringComparer.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(worldFolder, table + ".tsv");
            if (!File.Exists(path)) throw new FileNotFoundException($"schema.sql names {table}, but {path} is missing.");
            writer.WriteLine($"DROP TABLE IF EXISTS `{table}`;\n{create};");
            using var reader = new StreamReader(path, new UTF8Encoding(false), false, 1 << 20);
            var header = (reader.ReadLine() ?? throw new InvalidDataException($"{path} is empty.")).Split('\t');
            var kinds = header.Select(name => types.TryGetValue(name, out var type) ? type : throw new InvalidDataException($"{path}: {name} is not a column of {table}.")).ToArray();
            var prefix = $"INSERT INTO `{table}` ({string.Join(",", header.Select(name => $"`{name}`"))}) VALUES\n";
            var batch = new StringBuilder();
            var rows = 0; var number = 1;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                number++;
                var cells = line.Split('\t');
                if (cells.Length != header.Length) throw new InvalidDataException($"{path}:{number} has {cells.Length} cells; the header has {header.Length}.");
                batch.Append(batch.Length == 0 ? prefix : ",\n").Append('(');
                for (var i = 0; i < cells.Length; i++)
                {
                    if (i > 0) batch.Append(',');
                    batch.Append(Literal(cells[i], kinds[i], path, number));
                }
                batch.Append(')');
                rows++;
                if (batch.Length > 1 << 20) { writer.Write(batch); writer.WriteLine(";"); batch.Clear(); }
            }
            if (batch.Length > 0) { writer.Write(batch); writer.WriteLine(";"); }
            writer.WriteLine();
            results.Add(new("world", table, "built", rows, ""));
        }
        writer.WriteLine("SET FOREIGN_KEY_CHECKS=1;\nSET UNIQUE_CHECKS=1;");
        return results;
    }

    private static string Literal(string cell, string type, string path, int line)
    {
        if (cell == GameDataTsv.Null) return "NULL";
        if (Numeric.Contains(type))
            return NumberCell().IsMatch(cell) ? cell : throw new InvalidDataException($"{path}:{line} '{cell}' is not a {type}.");
        if (Binary.Contains(type))
            return cell.StartsWith("0x", StringComparison.Ordinal) ? "X'" + cell[2..] + "'" : throw new InvalidDataException($"{path}:{line} a {type} cell is written 0x<hex>.");
        var text = GameDataTsv.Unescape(cell);
        var builder = new StringBuilder(text.Length + 2).Append('\'');
        foreach (var c in text)
            builder.Append(c switch { '\\' => "\\\\", '\'' => "\\'", '\0' => "\\0", '\n' => "\\n", '\r' => "\\r", '\x1a' => "\\Z", _ => c.ToString() });
        return builder.Append('\'').ToString();
    }

    [GeneratedRegex(@" AUTO_INCREMENT=\d+")] private static partial Regex AutoIncrement();
    [GeneratedRegex(@"^CREATE TABLE `([^`]+)`")] private static partial Regex CreateName();
    [GeneratedRegex(@"^\s+`([^`]+)` ([a-zA-Z]+)", RegexOptions.Multiline)] private static partial Regex ColumnLine();
    [GeneratedRegex(@"^-?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$")] private static partial Regex NumberCell();
}
