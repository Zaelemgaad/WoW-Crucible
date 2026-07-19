using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace WoWCrucible.Core;

public enum WowCacheDefinitionKind { Wdb, Adb }

public sealed record WowCacheFieldDefinition(
    string Name,
    string Type,
    bool IsKey,
    int MaximumCount,
    IReadOnlyList<WowCacheFieldDefinition> Children);

public sealed record WowCacheTableDefinition(
    string Name,
    WowCacheDefinitionKind Kind,
    IReadOnlyList<WowCacheFieldDefinition> Fields,
    string SourcePath,
    int? Build);

public sealed class WowCacheDefinitionCatalog
{
    private const int MaximumDefinitions = 4096;
    private const int MaximumFieldsPerDefinition = 4096;
    private readonly IReadOnlyList<WowCacheTableDefinition> _definitions;

    private WowCacheDefinitionCatalog(IReadOnlyList<WowCacheTableDefinition> definitions) => _definitions = definitions;

    public IReadOnlyList<WowCacheTableDefinition> Definitions => _definitions;

    public static WowCacheDefinitionCatalog Load(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var definitions = new List<WowCacheTableDefinition>();
        foreach (var suppliedPath in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var path = Path.GetFullPath(suppliedPath);
            if (!File.Exists(path)) throw new FileNotFoundException("Cache definition XML does not exist.", path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var root = document.Root ?? throw new InvalidDataException($"Definition XML has no root element: {path}");
            var wdbx = root.Name.LocalName.Equals("Definition", StringComparison.OrdinalIgnoreCase);
            var kind = root.Name.LocalName.Equals("wdbDef", StringComparison.OrdinalIgnoreCase) || wdbx ? WowCacheDefinitionKind.Wdb
                : root.Name.LocalName.Equals("adbDef", StringComparison.OrdinalIgnoreCase) ? WowCacheDefinitionKind.Adb
                : throw new InvalidDataException($"Unsupported cache definition root '{root.Name.LocalName}' in {path}.");
            var idName = wdbx ? "Table" : kind == WowCacheDefinitionKind.Wdb ? "wdbId" : "adbId";
            var fieldName = wdbx ? "Field" : kind == WowCacheDefinitionKind.Wdb ? "wdbElement" : "adbElement";
            foreach (var element in root.Elements().Where(element => element.Name.LocalName.Equals(idName, StringComparison.OrdinalIgnoreCase)))
            {
                if (definitions.Count >= MaximumDefinitions) throw new InvalidDataException($"Cache definition corpus exceeds the {MaximumDefinitions:N0}-table safety limit.");
                var name = Attribute(element, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"A {idName} in {path} has no name.");
                var fields = element.Elements().Where(child => child.Name.LocalName.Equals(fieldName, StringComparison.OrdinalIgnoreCase)).SelectMany((child, index) => ParseFields(child, index, wdbx)).ToArray();
                if (wdbx) fields = NormalizeWdbxCacheLayout(name, fields);
                if (fields.Length > MaximumFieldsPerDefinition) throw new InvalidDataException($"Definition {name} exceeds the {MaximumFieldsPerDefinition:N0}-field safety limit.");
                var build = int.TryParse(Attribute(element, "build"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBuild) ? parsedBuild : (int?)null;
                definitions.Add(new WowCacheTableDefinition(name, kind, fields, path, build));
            }
        }
        return new WowCacheDefinitionCatalog(definitions);
    }

    public WowCacheTableDefinition? Resolve(string cachePath, WowCacheDefinitionKind kind, string? explicitName = null)
    {
        var name = string.IsNullOrWhiteSpace(explicitName) ? Path.GetFileNameWithoutExtension(cachePath) : explicitName.Trim();
        var matches = _definitions.Where(definition => definition.Kind == kind && definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"More than one {kind} definition named '{name}' was loaded. Select one definition file explicitly.");
        return matches.SingleOrDefault();
    }

    public static IReadOnlyList<string> Discover(string? start = null)
    {
        var names = new[] { "WDB.xml", "WotLK 3.3.5 (12340).xml", "wdb-definitions.xml", "adb-definitions.xml", "definitions.xml" };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in new[] { start, Environment.CurrentDirectory, AppContext.BaseDirectory }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var initial = File.Exists(origin) ? new FileInfo(origin!).Directory : new DirectoryInfo(Path.GetFullPath(origin!));
            for (var directory = initial; directory is not null; directory = directory.Parent)
            {
                foreach (var relative in new[]
                {
                    Path.Combine("Tools", "Adb_Wdb_Parser 1.0.0"),
                    Path.Combine("Tools", "ADB_WDB_Parser for 4.3.x 1.0.0"),
                    Path.Combine("Tools", "WDB Converter 2.9"),
                    Path.Combine("Tools", "WDBXEditor", "Definitions")
                })
                foreach (var name in names)
                {
                    var candidate = Path.Combine(directory.FullName, relative, name);
                    if (File.Exists(candidate)) found.Add(candidate);
                }
            }
        }
        return found.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<WowCacheFieldDefinition> ParseFields(XElement element, int index, bool wdbx)
    {
        var type = Attribute(element, "type")?.Trim();
        if (string.IsNullOrWhiteSpace(type)) throw new InvalidDataException($"Cache field {index + 1} has no type.");
        var name = Attribute(element, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = $"Field_{index + 1}";
        var maximum = int.TryParse(Attribute(element, "maxcount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        if (maximum < 0 || maximum > 4096) throw new InvalidDataException($"Cache field {name} has an invalid maxcount of {maximum}.");
        var children = element.Elements().Where(child => child.Name.LocalName.Equals("structElement", StringComparison.OrdinalIgnoreCase)).SelectMany((child, childIndex) => ParseFields(child, childIndex, false)).ToArray();
        var isKey = string.Equals(Attribute(element, "key"), "yes", StringComparison.OrdinalIgnoreCase) || string.Equals(Attribute(element, "key"), "true", StringComparison.OrdinalIgnoreCase) || string.Equals(Attribute(element, "IsIndex"), "true", StringComparison.OrdinalIgnoreCase);
        var normalizedType = wdbx ? type.ToLowerInvariant() switch { "int" => "integer", "uint" => "uinteger", "float" => "single", "string" or "loc" => "varChar", _ => type } : type;
        var arraySize = wdbx && int.TryParse(Attribute(element, "ArraySize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedArray) ? parsedArray : 1;
        if (arraySize < 1 || arraySize > 4096) throw new InvalidDataException($"Cache field {name} has an invalid ArraySize of {arraySize}.");
        for (var item = 0; item < arraySize; item++) yield return new WowCacheFieldDefinition(arraySize == 1 ? name : $"{name}[{item + 1}]", normalizedType, isKey && item == 0, maximum, children);
    }

    private static string? Attribute(XElement element, string name) => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static WowCacheFieldDefinition[] NormalizeWdbxCacheLayout(string table, WowCacheFieldDefinition[] fields)
    {
        // WDBX's shared 12340 XML describes the logical row, while the wire cache
        // omits the old trailing expansion marker and compresses item stats behind
        // itemstatscount. Normalize those documented logical fields into the bytes
        // that are actually present; this is provider behavior, not an XML rewrite.
        var normalized = fields.ToList();
        if (table.Equals("CreatureCache", StringComparison.OrdinalIgnoreCase) || table.Equals("GameobjectCache", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Count > 0 && normalized[^1].Name.Equals("exp", StringComparison.OrdinalIgnoreCase)) normalized.RemoveAt(normalized.Count - 1);
        }
        if (table.Equals("ItemCache", StringComparison.OrdinalIgnoreCase))
        {
            var nameIndex = normalized.FindIndex(field => field.Name.Equals("Name1", StringComparison.OrdinalIgnoreCase));
            if (nameIndex >= 0)
            {
                normalized.InsertRange(nameIndex + 1,
                [
                    new WowCacheFieldDefinition("Name2", "varChar", false, 0, []),
                    new WowCacheFieldDefinition("Name3", "varChar", false, 0, []),
                    new WowCacheFieldDefinition("Name4", "varChar", false, 0, [])
                ]);
            }
            var countIndex = normalized.FindIndex(field => field.Name.Equals("itemstatscount", StringComparison.OrdinalIgnoreCase));
            if (countIndex >= 0)
            {
                var removable = Math.Min(21, normalized.Count - countIndex);
                normalized.RemoveRange(countIndex, removable);
                normalized.Insert(countIndex, new WowCacheFieldDefinition("itemstatscount", "struct", false, 10,
                [
                    new WowCacheFieldDefinition("stat_type", "integer", false, 0, []),
                    new WowCacheFieldDefinition("stat_value", "integer", false, 0, [])
                ]));
            }
        }
        return normalized.ToArray();
    }
}

public sealed record WowCacheHeader(string RawMagic, string Magic, uint Build, string RawLocale, string Locale, uint MaximumRecordSize, int RecordVersion, int? CacheVersion, int HeaderSize);

public sealed record WowCacheValue(string Name, string Type, object? Value, int Offset, int Length)
{
    public string DisplayValue => Value switch
    {
        null => "",
        float value => value.ToString("R", CultureInfo.InvariantCulture),
        double value => value.ToString("R", CultureInfo.InvariantCulture),
        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
        _ => Value.ToString() ?? string.Empty
    };
}

public sealed record WowCacheRecord(uint Id, uint PayloadSize, long FileOffset, IReadOnlyList<WowCacheValue> Values, int UnconsumedBytes, string? DecodeError, byte[] Payload)
{
    public bool Decoded => DecodeError is null && Values.Count > 0;
}

public sealed record WowCacheTable(
    string SourcePath,
    string Sha256,
    WowCacheHeader Header,
    WowCacheTableDefinition? Definition,
    IReadOnlyList<WowCacheRecord> Records,
    bool HasTerminator,
    long TrailingBytes);

public static class WowCacheTableService
{
    private const int MaximumRecords = 2_000_000;
    private const int MaximumPayloadSize = 64 * 1024 * 1024;
    private static readonly Encoding StringEncoding = new UTF8Encoding(false, false);

    public static WowCacheTable LoadWdb(string path, WowCacheTableDefinition? definition = null)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("WDB cache file does not exist.", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        if (stream.Length < 16) throw new InvalidDataException("WDB cache is shorter than the smallest supported header.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var rawMagic = ReadFourCc(reader); var build = reader.ReadUInt32(); var rawLocale = build >= 4500 ? ReadFourCc(reader) : string.Empty; var maximumRecordSize = reader.ReadUInt32(); var recordVersion = reader.ReadInt32(); int? cacheVersion = build >= 9506 ? reader.ReadInt32() : null; var headerSize = checked((int)stream.Position);
        if (rawMagic.Any(character => character is < ' ' or > '~')) throw new InvalidDataException("WDB cache magic is not a printable four-character code.");
        if (definition is not null && definition.Kind != WowCacheDefinitionKind.Wdb) throw new ArgumentException("A WDB file requires a WDB definition.", nameof(definition));
        if (definition?.Build is { } definitionBuild && definitionBuild != build) throw new InvalidDataException($"Definition {definition.Name} targets build {definitionBuild:N0}, but the cache header is build {build:N0}.");
        var header = new WowCacheHeader(rawMagic, Reverse(rawMagic), build, rawLocale, rawLocale.Length == 0 ? string.Empty : Reverse(rawLocale), maximumRecordSize, recordVersion, cacheVersion, headerSize);
        var records = new List<WowCacheRecord>(); var hasTerminator = false;
        while (stream.Position < stream.Length)
        {
            if (records.Count >= MaximumRecords) throw new InvalidDataException($"WDB cache exceeds the {MaximumRecords:N0}-record safety limit.");
            if (stream.Length - stream.Position < 8) break;
            var recordOffset = stream.Position; var id = reader.ReadUInt32(); var payloadSize = reader.ReadUInt32();
            if (id == 0 && payloadSize == 0) { hasTerminator = true; break; }
            if (payloadSize > MaximumPayloadSize) throw new InvalidDataException($"WDB record {id:N0} declares {payloadSize:N0} bytes, above the {MaximumPayloadSize:N0}-byte safety limit.");
            if (payloadSize > stream.Length - stream.Position) throw new InvalidDataException($"WDB record {id:N0} declares {payloadSize:N0} bytes but only {stream.Length - stream.Position:N0} remain.");
            var payload = reader.ReadBytes(checked((int)payloadSize));
            records.Add(Decode(id, payloadSize, recordOffset, payload, definition));
        }
        var trailing = stream.Length - stream.Position;
        stream.Position = 0; var sha = Convert.ToHexString(SHA256.HashData(stream));
        return new WowCacheTable(path, sha, header, definition, records, hasTerminator, trailing);
    }

    public static void Export(WowCacheTable table, string outputPath, string format, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(table); outputPath = Path.GetFullPath(outputPath); format = format.Trim().ToLowerInvariant();
        if (format is not ("csv" or "jsonl")) throw new ArgumentException("Cache export format must be csv or jsonl.", nameof(format));
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output already exists: {outputPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); var temporary = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
            {
                if (format == "jsonl")
                {
                    foreach (var record in table.Records)
                    {
                        var values = record.Values.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
                        writer.WriteLine(JsonSerializer.Serialize(new { record.Id, record.PayloadSize, record.FileOffset, Values = values, record.UnconsumedBytes, record.DecodeError }));
                    }
                }
                else
                {
                    var columns = table.Records.SelectMany(record => record.Values.Select(value => value.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    writer.WriteLine(string.Join(',', new[] { "Id", "PayloadSize", "FileOffset", "UnconsumedBytes", "DecodeError" }.Concat(columns).Select(Csv)));
                    foreach (var record in table.Records)
                    {
                        var values = record.Values.ToDictionary(value => value.Name, value => value.DisplayValue, StringComparer.OrdinalIgnoreCase);
                        writer.WriteLine(string.Join(',', new[] { record.Id.ToString(CultureInfo.InvariantCulture), record.PayloadSize.ToString(CultureInfo.InvariantCulture), record.FileOffset.ToString(CultureInfo.InvariantCulture), record.UnconsumedBytes.ToString(CultureInfo.InvariantCulture), record.DecodeError ?? string.Empty }.Concat(columns.Select(column => values.GetValueOrDefault(column) ?? string.Empty)).Select(Csv)));
                    }
                }
            }
            File.Move(temporary, outputPath, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static WowCacheRecord Decode(uint id, uint payloadSize, long fileOffset, byte[] payload, WowCacheTableDefinition? definition)
    {
        if (definition is null) return new WowCacheRecord(id, payloadSize, fileOffset, [], payload.Length, null, payload);
        var values = new List<WowCacheValue>(); var offset = 0;
        try
        {
            foreach (var field in definition.Fields)
            {
                var type = Normalize(field.Type);
                if (type == "size") continue;
                if (field.IsKey) { AddUnique(values, new WowCacheValue(field.Name, field.Type, id, 0, 4)); continue; }
                if (type == "struct")
                {
                    var start = offset; var count = ReadUInt32(payload, ref offset); var maximum = field.MaximumCount > 0 ? field.MaximumCount : 4096;
                    if (count > maximum) throw new InvalidDataException($"{field.Name} count {count:N0} exceeds its maximum of {maximum:N0}.");
                    AddUnique(values, new WowCacheValue(field.Name + ".Count", "uinteger", count, start, 4));
                    for (var item = 0; item < count; item++) foreach (var child in field.Children) AddUnique(values, ReadValue(payload, ref offset, child, $"{child.Name}[{item + 1}]"));
                    continue;
                }
                AddUnique(values, ReadValue(payload, ref offset, field, field.Name));
            }
            return new WowCacheRecord(id, payloadSize, fileOffset, values, payload.Length - offset, null, payload);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            return new WowCacheRecord(id, payloadSize, fileOffset, values, payload.Length - Math.Min(offset, payload.Length), exception.Message, payload);
        }
    }

    private static WowCacheValue ReadValue(byte[] payload, ref int offset, WowCacheFieldDefinition field, string name)
    {
        var start = offset; object value;
        switch (Normalize(field.Type))
        {
            case "integer": value = ReadInt32(payload, ref offset); break;
            case "uinteger": value = ReadUInt32(payload, ref offset); break;
            case "single": value = BitConverter.Int32BitsToSingle(ReadInt32(payload, ref offset)); break;
            case "smallint": value = ReadInt16(payload, ref offset); break;
            case "usmallint": value = ReadUInt16(payload, ref offset); break;
            case "tinyint": Ensure(payload, offset, 1, name); value = unchecked((sbyte)payload[offset++]); break;
            case "utinyint": case "byte": Ensure(payload, offset, 1, name); value = payload[offset++]; break;
            case "integer64": value = ReadInt64(payload, ref offset); break;
            case "uinteger64": value = ReadUInt64(payload, ref offset); break;
            case "varchar":
                var end = Array.IndexOf(payload, (byte)0, offset); if (end < 0) throw new InvalidDataException($"{name} has no null terminator inside the record payload.");
                value = StringEncoding.GetString(payload, offset, end - offset); offset = end + 1; break;
            default: throw new InvalidDataException($"Unsupported cache field type '{field.Type}' at {name}.");
        }
        return new WowCacheValue(name, field.Type, value, start, offset - start);
    }

    private static void AddUnique(List<WowCacheValue> values, WowCacheValue value)
    {
        if (!values.Any(existing => existing.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase))) { values.Add(value); return; }
        var suffix = 2; string candidate; do candidate = $"{value.Name}#{suffix++}"; while (values.Any(existing => existing.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)));
        values.Add(value with { Name = candidate });
    }

    private static uint ReadUInt32(byte[] bytes, ref int offset) { Ensure(bytes, offset, 4, "UInt32"); var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)); offset += 4; return value; }
    private static int ReadInt32(byte[] bytes, ref int offset) => unchecked((int)ReadUInt32(bytes, ref offset));
    private static ushort ReadUInt16(byte[] bytes, ref int offset) { Ensure(bytes, offset, 2, "UInt16"); var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)); offset += 2; return value; }
    private static short ReadInt16(byte[] bytes, ref int offset) => unchecked((short)ReadUInt16(bytes, ref offset));
    private static ulong ReadUInt64(byte[] bytes, ref int offset) { Ensure(bytes, offset, 8, "UInt64"); var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)); offset += 8; return value; }
    private static long ReadInt64(byte[] bytes, ref int offset) => unchecked((long)ReadUInt64(bytes, ref offset));
    private static void Ensure(byte[] bytes, int offset, int length, string field) { if (offset < 0 || length < 0 || offset > bytes.Length - length) throw new InvalidDataException($"Record ended while reading {field} at payload offset {offset:N0}."); }
    private static string Normalize(string type) => type.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    private static string ReadFourCc(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
    private static string Reverse(string value) => new(value.Reverse().ToArray());
    private static string Csv(string? value) { value ??= string.Empty; return value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\""; }
}
