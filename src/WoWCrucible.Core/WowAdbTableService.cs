using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record WowAdbHeader(
    string Signature,
    uint RecordCount,
    uint FieldCount,
    uint RecordSize,
    uint StringBlockSize,
    uint TableHash,
    int Build,
    int Timestamp,
    int MinId,
    int MaxId,
    int Locale,
    int CopyTableSize,
    long IndexBytes,
    long DataOffset);

public sealed record WowAdbRecord(int RowIndex, int? Id, IReadOnlyList<WowCacheValue> Values, int UnconsumedBytes, string? DecodeError, byte[] Payload)
{
    public bool Decoded => DecodeError is null && Values.Count > 0;
}

public sealed record WowAdbTable(
    string SourcePath,
    string Sha256,
    WowAdbHeader Header,
    WowCacheTableDefinition? Definition,
    IReadOnlyList<WowAdbRecord> Records,
    long StringBlockOffset,
    long CopyTableOffset,
    long TrailingBytes);

public static class WowAdbTableService
{
    private const int HeaderSize = 48;
    private const int MaximumRecords = 2_000_000;
    private const int MaximumFields = 4096;
    private const int MaximumRecordSize = 64 * 1024 * 1024;
    private const int MaximumStringBlockSize = 512 * 1024 * 1024;
    private const long MaximumRecordBytes = 2L * 1024 * 1024 * 1024;
    private static readonly Encoding StringEncoding = new UTF8Encoding(false, false);

    public static WowAdbTable LoadWch2(string path, WowCacheTableDefinition? definition = null)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("ADB cache file does not exist.", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        if (stream.Length < HeaderSize) throw new InvalidDataException("ADB cache is shorter than its 48-byte WCH2 header.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var signature = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (!signature.Equals("WCH2", StringComparison.Ordinal)) throw new NotSupportedException($"ADB signature {signature} is not the Cataclysm WCH2 layout. WCH5/WCH7/WCH8 require their matching DB2 layout metadata and are not guessed.");
        var recordCount = reader.ReadUInt32(); var fieldCount = reader.ReadUInt32(); var recordSize = reader.ReadUInt32(); var stringSize = reader.ReadUInt32(); var tableHash = reader.ReadUInt32(); var build = reader.ReadInt32(); var timestamp = reader.ReadInt32(); var minId = reader.ReadInt32(); var maxId = reader.ReadInt32(); var locale = reader.ReadInt32(); var copySize = reader.ReadInt32();
        if (recordCount > MaximumRecords) throw new InvalidDataException($"ADB cache declares {recordCount:N0} records, above the {MaximumRecords:N0}-record safety limit.");
        if (fieldCount > MaximumFields) throw new InvalidDataException($"ADB cache declares {fieldCount:N0} fields, above the {MaximumFields:N0}-field safety limit.");
        if (recordSize > MaximumRecordSize) throw new InvalidDataException($"ADB record size {recordSize:N0} exceeds the {MaximumRecordSize:N0}-byte safety limit.");
        if (stringSize > MaximumStringBlockSize) throw new InvalidDataException($"ADB string block {stringSize:N0} exceeds the {MaximumStringBlockSize:N0}-byte safety limit.");
        if (copySize < 0) throw new InvalidDataException("ADB copy-table size cannot be negative.");
        if (definition is not null && definition.Kind != WowCacheDefinitionKind.Adb) throw new ArgumentException("An ADB file requires an ADB definition.", nameof(definition));
        if (definition?.Build is { } definitionBuild && definitionBuild != build) throw new InvalidDataException($"Definition {definition.Name} targets build {definitionBuild:N0}, but the ADB header is build {build:N0}.");
        long indexBytes = 0;
        if (maxId != 0 && build > 12880)
        {
            var identities = checked((long)maxId - minId + 1); if (identities <= 0 || identities > 100_000_000) throw new InvalidDataException($"ADB index range {minId:N0}..{maxId:N0} is invalid or exceeds the safety limit.");
            indexBytes = checked(identities * 6);
        }
        var dataOffset = checked((long)HeaderSize + indexBytes); var recordBytes = checked((long)recordCount * recordSize); if (recordBytes > MaximumRecordBytes) throw new InvalidDataException($"ADB fixed records exceed the {MaximumRecordBytes:N0}-byte in-memory safety limit."); var stringOffset = checked(dataOffset + recordBytes); var copyOffset = checked(stringOffset + stringSize); var declaredEnd = checked(copyOffset + copySize);
        if (declaredEnd > stream.Length) throw new InvalidDataException($"ADB header requires {declaredEnd:N0} bytes, but the file contains only {stream.Length:N0}.");
        stream.Position = stringOffset; var strings = reader.ReadBytes(checked((int)stringSize));
        stream.Position = dataOffset; var records = new List<WowAdbRecord>(checked((int)recordCount));
        for (var row = 0; row < recordCount; row++) records.Add(Decode(checked((int)row), reader.ReadBytes(checked((int)recordSize)), strings, definition));
        stream.Position = 0; var sha = Convert.ToHexString(SHA256.HashData(stream));
        return new WowAdbTable(path, sha, new WowAdbHeader(signature, recordCount, fieldCount, recordSize, stringSize, tableHash, build, timestamp, minId, maxId, locale, copySize, indexBytes, dataOffset), definition, records, stringOffset, copyOffset, stream.Length - declaredEnd);
    }

    public static void Export(WowAdbTable table, string outputPath, string format, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(table); outputPath = Path.GetFullPath(outputPath); format = format.Trim().ToLowerInvariant();
        if (format is not ("csv" or "jsonl")) throw new ArgumentException("ADB export format must be csv or jsonl.", nameof(format));
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output already exists: {outputPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); var temporary = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
            {
                if (format == "jsonl") foreach (var record in table.Records) writer.WriteLine(JsonSerializer.Serialize(new { record.RowIndex, record.Id, Values = record.Values.ToDictionary(value => value.Name, value => value.Value), record.UnconsumedBytes, record.DecodeError }));
                else
                {
                    var columns = table.Records.SelectMany(record => record.Values.Select(value => value.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); writer.WriteLine(string.Join(',', new[] { "RowIndex", "Id", "UnconsumedBytes", "DecodeError" }.Concat(columns).Select(Csv)));
                    foreach (var record in table.Records)
                    {
                        var values = record.Values.ToDictionary(value => value.Name, value => value.DisplayValue, StringComparer.OrdinalIgnoreCase);
                        writer.WriteLine(string.Join(',', new[] { record.RowIndex.ToString(CultureInfo.InvariantCulture), record.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, record.UnconsumedBytes.ToString(CultureInfo.InvariantCulture), record.DecodeError ?? string.Empty }.Concat(columns.Select(column => values.GetValueOrDefault(column) ?? string.Empty)).Select(Csv)));
                    }
                }
            }
            File.Move(temporary, outputPath, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static WowAdbRecord Decode(int row, byte[] payload, byte[] strings, WowCacheTableDefinition? definition)
    {
        if (definition is null) return new WowAdbRecord(row, null, [], payload.Length, null, payload);
        var values = new List<WowCacheValue>(); var offset = 0; int? id = null;
        try
        {
            foreach (var field in definition.Fields)
            {
                if (field.Type.Equals("struct", StringComparison.OrdinalIgnoreCase) || field.Type.Equals("size", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"ADB fixed rows do not support schema field type {field.Type} at {field.Name}.");
                var value = ReadValue(payload, strings, ref offset, field); AddUnique(values, value);
                if (id is null && (field.IsKey || field.Name.Equals("id", StringComparison.OrdinalIgnoreCase) || field.Name.Equals("entry", StringComparison.OrdinalIgnoreCase))) id = value.Value switch { int signed => signed, uint unsigned when unsigned <= int.MaxValue => (int)unsigned, _ => null };
            }
            return new WowAdbRecord(row, id, values, payload.Length - offset, null, payload);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            return new WowAdbRecord(row, id, values, payload.Length - Math.Min(offset, payload.Length), exception.Message, payload);
        }
    }

    private static WowCacheValue ReadValue(byte[] payload, byte[] strings, ref int offset, WowCacheFieldDefinition field)
    {
        var start = offset; object value;
        switch (Normalize(field.Type))
        {
            case "integer": value = ReadInt32(payload, ref offset); break;
            case "uinteger": value = ReadUInt32(payload, ref offset); break;
            case "single": value = BitConverter.Int32BitsToSingle(ReadInt32(payload, ref offset)); break;
            case "smallint": value = ReadInt16(payload, ref offset); break;
            case "usmallint": value = ReadUInt16(payload, ref offset); break;
            case "tinyint": Ensure(payload, offset, 1, field.Name); value = unchecked((sbyte)payload[offset++]); break;
            case "utinyint": case "byte": Ensure(payload, offset, 1, field.Name); value = payload[offset++]; break;
            case "integer64": value = ReadInt64(payload, ref offset); break;
            case "uinteger64": value = ReadUInt64(payload, ref offset); break;
            case "varchar":
                var stringOffset = ReadUInt32(payload, ref offset); value = ReadString(strings, stringOffset, field.Name); break;
            default: throw new InvalidDataException($"Unsupported ADB field type '{field.Type}' at {field.Name}.");
        }
        return new WowCacheValue(field.Name, field.Type, value, start, offset - start);
    }

    private static string ReadString(byte[] strings, uint offset, string name)
    {
        if (offset == 0) return string.Empty; if (offset >= strings.Length) throw new InvalidDataException($"{name} points to string offset {offset:N0}, outside the {strings.Length:N0}-byte string block.");
        var end = Array.IndexOf(strings, (byte)0, checked((int)offset)); if (end < 0) throw new InvalidDataException($"{name} string at offset {offset:N0} has no terminator."); return StringEncoding.GetString(strings, checked((int)offset), end - checked((int)offset));
    }
    private static void AddUnique(List<WowCacheValue> values, WowCacheValue value) { if (!values.Any(existing => existing.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase))) { values.Add(value); return; } var suffix = 2; string candidate; do candidate = $"{value.Name}#{suffix++}"; while (values.Any(existing => existing.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))); values.Add(value with { Name = candidate }); }
    private static uint ReadUInt32(byte[] bytes, ref int offset) { Ensure(bytes, offset, 4, "UInt32"); var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)); offset += 4; return value; }
    private static int ReadInt32(byte[] bytes, ref int offset) => unchecked((int)ReadUInt32(bytes, ref offset));
    private static ushort ReadUInt16(byte[] bytes, ref int offset) { Ensure(bytes, offset, 2, "UInt16"); var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)); offset += 2; return value; }
    private static short ReadInt16(byte[] bytes, ref int offset) => unchecked((short)ReadUInt16(bytes, ref offset));
    private static ulong ReadUInt64(byte[] bytes, ref int offset) { Ensure(bytes, offset, 8, "UInt64"); var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)); offset += 8; return value; }
    private static long ReadInt64(byte[] bytes, ref int offset) => unchecked((long)ReadUInt64(bytes, ref offset));
    private static void Ensure(byte[] bytes, int offset, int length, string field) { if (offset < 0 || length < 0 || offset > bytes.Length - length) throw new InvalidDataException($"Record ended while reading {field} at offset {offset:N0}."); }
    private static string Normalize(string type) => type.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    private static string Csv(string? value) { value ??= string.Empty; return value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\""; }
}
