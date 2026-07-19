using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum ClientTableContainerKind { Wdbc, Wdb2 }
public sealed record Wdb2Metadata(uint TableHash, int Build, uint Timestamp, uint MinId, uint MaxId, uint Locale,
    IReadOnlyList<int> IndexMap, IReadOnlyList<ushort> StringLengths, int CopyTableSize)
{
    public bool HasIndexMap => IndexMap.Count > 0;
    public int CopyRows => CopyTableSize / 8;
}
public sealed record ClientTableIdentity(string TableName, ClientTableContainerKind Container, int Build, uint TableHash);

public sealed class WdbcFile
{
    public const int HeaderSize = 20;
    private byte[] _records;
    private byte[] _strings;
    private Dictionary<string, uint>? _stringOffsets;
    private byte[] _db2CopyTable;

    private WdbcFile(string sourcePath, int rowCount, int fieldCount, int recordSize, byte[] records, byte[] strings,
        ClientTableContainerKind containerKind = ClientTableContainerKind.Wdbc, Wdb2Metadata? db2Metadata = null, byte[]? db2CopyTable = null, string? logicalTableName = null)
    {
        SourcePath = sourcePath;
        RowCount = rowCount;
        FieldCount = fieldCount;
        RecordSize = recordSize;
        _records = records;
        _strings = strings;
        ContainerKind = containerKind;
        Db2Metadata = db2Metadata;
        _db2CopyTable = db2CopyTable ?? [];
        LogicalTableName = string.IsNullOrWhiteSpace(logicalTableName) ? Path.GetFileNameWithoutExtension(sourcePath) : logicalTableName;
    }

    public string SourcePath { get; private set; }
    public int RowCount { get; private set; }
    public int FieldCount { get; }
    public int RecordSize { get; }
    public int StringTableSize => _strings.Length;
    public bool IsDirty { get; private set; }
    public ClientTableContainerKind ContainerKind { get; }
    public Wdb2Metadata? Db2Metadata { get; private set; }
    public int PhysicalHeaderSize => ContainerKind == ClientTableContainerKind.Wdb2 ? 48 + (Db2Metadata?.IndexMap.Count ?? 0) * 6 : HeaderSize;
    public bool AllowsStructuralMutation => Db2Metadata is null || !Db2Metadata.HasIndexMap && Db2Metadata.CopyTableSize == 0;
    public string LogicalTableName { get; }

    public WdbcFile CloneInMemory()
    {
        var metadata = Db2Metadata is null ? null : Db2Metadata with { IndexMap = Db2Metadata.IndexMap.ToArray(), StringLengths = Db2Metadata.StringLengths.ToArray() };
        var clone = new WdbcFile(SourcePath, RowCount, FieldCount, RecordSize,
            _records.AsSpan(0, checked(RowCount * RecordSize)).ToArray(), _strings.ToArray(), ContainerKind, metadata, _db2CopyTable.ToArray(), LogicalTableName)
        {
            IsDirty = IsDirty
        };
        return clone;
    }

    public string ComputeContentSha256()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> contentMetadata = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(contentMetadata[0..4], RowCount);
        BinaryPrimitives.WriteInt32LittleEndian(contentMetadata[4..8], FieldCount);
        BinaryPrimitives.WriteInt32LittleEndian(contentMetadata[8..12], RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(contentMetadata[12..16], _strings.Length);
        hash.AppendData(contentMetadata);
        hash.AppendData(_records.AsSpan(0, checked(RowCount * RecordSize)));
        hash.AppendData(_strings);
        hash.AppendData([(byte)ContainerKind]);
        if (Db2Metadata is { } db2Metadata)
        {
            Span<byte> db2 = stackalloc byte[28];
            BinaryPrimitives.WriteUInt32LittleEndian(db2[0..4], db2Metadata.TableHash); BinaryPrimitives.WriteInt32LittleEndian(db2[4..8], db2Metadata.Build);
            BinaryPrimitives.WriteUInt32LittleEndian(db2[8..12], db2Metadata.Timestamp); BinaryPrimitives.WriteUInt32LittleEndian(db2[12..16], db2Metadata.MinId);
            BinaryPrimitives.WriteUInt32LittleEndian(db2[16..20], db2Metadata.MaxId); BinaryPrimitives.WriteUInt32LittleEndian(db2[20..24], db2Metadata.Locale);
            BinaryPrimitives.WriteInt32LittleEndian(db2[24..28], db2Metadata.CopyTableSize); hash.AppendData(db2);
            Span<byte> mapBytes = stackalloc byte[4]; foreach (var value in db2Metadata.IndexMap) { BinaryPrimitives.WriteInt32LittleEndian(mapBytes, value); hash.AppendData(mapBytes); }
            Span<byte> lengthBytes = stackalloc byte[2]; foreach (var value in db2Metadata.StringLengths) { BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, value); hash.AppendData(lengthBytes); }
            hash.AppendData(_db2CopyTable);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void ReplaceContentFrom(WdbcFile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.FieldCount != FieldCount || source.RecordSize != RecordSize || source.ContainerKind != ContainerKind)
            throw new InvalidDataException("The replacement client-table layout or container differs from the open table.");
        RowCount = source.RowCount;
        _records = source._records.AsSpan(0, checked(source.RowCount * source.RecordSize)).ToArray();
        _strings = source._strings.ToArray();
        Db2Metadata = source.Db2Metadata is null ? null : source.Db2Metadata with { IndexMap = source.Db2Metadata.IndexMap.ToArray(), StringLengths = source.Db2Metadata.StringLengths.ToArray() };
        _db2CopyTable = source._db2CopyTable.ToArray();
        _stringOffsets = null;
        IsDirty = true;
    }

    public static WdbcFile Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (header[..4].SequenceEqual("WDB2"u8)) return LoadWdb2(path, stream, header);
        if (!header[..4].SequenceEqual("WDBC"u8))
            throw new InvalidDataException("Unsupported client-table container. Crucible currently accepts WDBC and fixed-layout WDB2; later WDB5/WDB6/WDC families require their matching adapter.");

        var rows = ReadNonNegative(header[4..8], "row count");
        var fields = ReadPositive(header[8..12], "field count");
        var recordSize = ReadPositive(header[12..16], "record size");
        var stringSize = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
        if (stringSize < 0 || (long)rows * recordSize + stringSize != stream.Length - HeaderSize)
            throw new InvalidDataException("The WDBC header sizes do not match the file length.");

        var recordBytes = GC.AllocateUninitializedArray<byte>(checked(rows * recordSize));
        stream.ReadExactly(recordBytes);
        var strings = GC.AllocateUninitializedArray<byte>(stringSize);
        stream.ReadExactly(strings);
        return new(path, rows, fields, recordSize, recordBytes, strings);
    }

    private static WdbcFile LoadWdb2(string path, FileStream stream, ReadOnlySpan<byte> commonHeader)
    {
        Span<byte> extension = stackalloc byte[28]; stream.ReadExactly(extension);
        var rows = ReadNonNegative(commonHeader[4..8], "row count"); var fields = ReadPositive(commonHeader[8..12], "field count");
        var recordSize = ReadPositive(commonHeader[12..16], "record size"); var stringSize = ReadNonNegative(commonHeader[16..20], "string table size");
        var tableHash = BinaryPrimitives.ReadUInt32LittleEndian(extension[0..4]); var build = BinaryPrimitives.ReadInt32LittleEndian(extension[4..8]);
        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(extension[8..12]); var minId = BinaryPrimitives.ReadUInt32LittleEndian(extension[12..16]);
        var maxId = BinaryPrimitives.ReadUInt32LittleEndian(extension[16..20]); var locale = BinaryPrimitives.ReadUInt32LittleEndian(extension[20..24]);
        var copySize = BinaryPrimitives.ReadInt32LittleEndian(extension[24..28]);
        if (build <= 0) throw new InvalidDataException($"Invalid WDB2 client build: {build}.");
        if (copySize < 0 || copySize % 8 != 0) throw new InvalidDataException($"Invalid WDB2 copy-table size: {copySize}.");
        var indexMap = Array.Empty<int>(); var stringLengths = Array.Empty<ushort>();
        if (maxId != 0 && build > 12880)
        {
            if (maxId < minId) throw new InvalidDataException($"Invalid WDB2 ID range {minId}..{maxId}.");
            var range = checked((long)maxId - minId + 1); if (range > int.MaxValue) throw new InvalidDataException("The WDB2 ID map is too large to address.");
            var mapBytes = checked(range * 6); if (48 + mapBytes > stream.Length) throw new InvalidDataException("The WDB2 ID/string-length maps exceed the file length.");
            indexMap = new int[(int)range]; Span<byte> value = stackalloc byte[4];
            for (var index = 0; index < indexMap.Length; index++) { stream.ReadExactly(value); indexMap[index] = BinaryPrimitives.ReadInt32LittleEndian(value); }
            stringLengths = new ushort[(int)range]; Span<byte> length = stackalloc byte[2];
            for (var index = 0; index < stringLengths.Length; index++) { stream.ReadExactly(length); stringLengths[index] = BinaryPrimitives.ReadUInt16LittleEndian(length); }
        }
        var expected = checked(stream.Position + (long)rows * recordSize + stringSize + copySize);
        if (expected != stream.Length) throw new InvalidDataException($"The WDB2 header sizes resolve to {expected:N0} bytes, but the file contains {stream.Length:N0} bytes.");
        var recordBytes = GC.AllocateUninitializedArray<byte>(checked(rows * recordSize)); stream.ReadExactly(recordBytes);
        var strings = GC.AllocateUninitializedArray<byte>(stringSize); stream.ReadExactly(strings);
        var copyTable = GC.AllocateUninitializedArray<byte>(copySize); stream.ReadExactly(copyTable);
        var metadata = new Wdb2Metadata(tableHash, build, timestamp, minId, maxId, locale, indexMap, stringLengths, copySize);
        var logicalTableName = ReadIdentity(path, metadata);
        return new(path, rows, fields, recordSize, recordBytes, strings, ClientTableContainerKind.Wdb2, metadata, copyTable, logicalTableName);
    }

    public uint GetRaw(int row, DbcColumn column)
    {
        ValidateCell(row, column);
        var cell = _records.AsSpan(row * RecordSize + column.Offset, column.Size);
        return column.Size switch { 1 => cell[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(cell), 4 => BinaryPrimitives.ReadUInt32LittleEndian(cell), _ => throw new NotSupportedException($"Field '{column.Name}' uses unsupported {column.Size}-byte scalar storage.") };
    }

    public void SetRaw(int row, DbcColumn column, uint raw)
    {
        ValidateCell(row, column);
        if (ContainerKind == ClientTableContainerKind.Wdb2 && Db2Metadata?.HasIndexMap == true && column.IsIndex)
            throw new InvalidOperationException("This WDB2 uses an ID index map. Editing its indexed ID is blocked until the map can be rebuilt from the selected schema.");
        if (column.Size == 1 && raw > byte.MaxValue || column.Size == 2 && raw > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(raw), $"The value does not fit in this {column.Size}-byte field.");
        var cell = _records.AsSpan(row * RecordSize + column.Offset, column.Size);
        if (column.Size == 1) cell[0] = (byte)raw;
        else if (column.Size == 2) BinaryPrimitives.WriteUInt16LittleEndian(cell, (ushort)raw);
        else if (column.Size == 4) BinaryPrimitives.WriteUInt32LittleEndian(cell, raw);
        else throw new NotSupportedException($"Field '{column.Name}' uses unsupported {column.Size}-byte scalar storage.");
        IsDirty = true;
    }

    public object GetDisplayValue(int row, DbcColumn column) => column.Type switch
    {
        DbcValueType.Int32 => column.Size switch { 1 => (int)unchecked((sbyte)GetRaw(row, column)), 2 => (int)unchecked((short)GetRaw(row, column)), _ => unchecked((int)GetRaw(row, column)) },
        DbcValueType.UInt32 or DbcValueType.Byte => GetRaw(row, column),
        DbcValueType.Float32 => BitConverter.UInt32BitsToSingle(GetRaw(row, column)),
        DbcValueType.StringOffset => GetString(GetRaw(row, column)),
        _ => GetRaw(row, column)
    };

    public void SetDisplayValue(int row, DbcColumn column, object? value)
    {
        var converted = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var text = column.Type == DbcValueType.StringOffset ? converted : converted.Trim();
        uint raw = column.Type switch
        {
            DbcValueType.Int32 => ParseSigned(text, column.Size),
            DbcValueType.UInt32 or DbcValueType.Raw32 => ParseUInt(text),
            DbcValueType.Byte => byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            DbcValueType.Float32 => BitConverter.SingleToUInt32Bits(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
            DbcValueType.StringOffset => GetOrAddString(text),
            _ => throw new ArgumentOutOfRangeException()
        };

        SetRaw(row, column, raw);
    }

    public int AddBlankRow(DbcColumn? idColumn = null)
    {
        return AddBlankRows(1, idColumn);
    }

    public int CloneRow(int sourceRow, DbcColumn? idColumn = null)
    {
        return CloneRows(sourceRow, 1, idColumn);
    }

    public int CloneRowWithId(int sourceRow, DbcColumn idColumn, uint targetId)
    {
        RequireStructuralMutation();
        ArgumentOutOfRangeException.ThrowIfNegative(sourceRow);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sourceRow, RowCount);
        if (targetId == 0) throw new ArgumentOutOfRangeException(nameof(targetId), "A physical DBC identity must be positive.");
        for (var row = 0; row < RowCount; row++)
            if (GetRaw(row, idColumn) == targetId) throw new InvalidOperationException($"DBC identity {targetId:N0} already exists at row {row + 1:N0}.");
        var targetRow = RowCount; EnsureRowCapacity(1);
        _records.AsSpan(sourceRow * RecordSize, RecordSize).CopyTo(_records.AsSpan(targetRow * RecordSize, RecordSize));
        RowCount++; SetRaw(targetRow, idColumn, targetId); IsDirty = true; return targetRow;
    }

    public int AddBlankRows(int count, DbcColumn? idColumn = null)
    {
        RequireStructuralMutation();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var firstRow = RowCount;
        var firstId = idColumn is null ? 0u : NextId(idColumn);
        EnsureRowCapacity(count);
        for (var index = 0; index < count; index++)
        {
            var row = RowCount++;
            _records.AsSpan(row * RecordSize, RecordSize).Clear();
            if (idColumn is not null) SetRaw(row, idColumn, checked(firstId + (uint)index));
        }
        IsDirty = true;
        return firstRow;
    }

    public int CloneRows(int sourceRow, int count, DbcColumn? idColumn = null)
    {
        RequireStructuralMutation();
        ArgumentOutOfRangeException.ThrowIfNegative(sourceRow);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sourceRow, RowCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var firstRow = RowCount;
        var firstId = idColumn is null ? 0u : NextId(idColumn);
        EnsureRowCapacity(count);
        for (var index = 0; index < count; index++)
        {
            var row = RowCount++;
            _records.AsSpan(sourceRow * RecordSize, RecordSize).CopyTo(_records.AsSpan(row * RecordSize, RecordSize));
            if (idColumn is not null) SetRaw(row, idColumn, checked(firstId + (uint)index));
        }
        IsDirty = true;
        return firstRow;
    }

    public void DeleteRows(IEnumerable<int> rows)
    {
        RequireStructuralMutation();
        var deleted = rows.Distinct().OrderDescending().ToArray();
        if (deleted.Length == 0) return;
        if (deleted.Any(row => row < 0 || row >= RowCount))
            throw new ArgumentOutOfRangeException(nameof(rows));

        var remove = deleted.ToHashSet();
        var newRecords = GC.AllocateUninitializedArray<byte>(checked((RowCount - deleted.Length) * RecordSize));
        var destination = 0;
        for (var source = 0; source < RowCount; source++)
        {
            if (remove.Contains(source)) continue;
            _records.AsSpan(source * RecordSize, RecordSize).CopyTo(newRecords.AsSpan(destination * RecordSize, RecordSize));
            destination++;
        }
        _records = newRecords;
        RowCount -= deleted.Length;
        IsDirty = true;
    }

    public uint NextId(DbcColumn idColumn)
    {
        uint maximum = 0;
        for (var row = 0; row < RowCount; row++)
            maximum = Math.Max(maximum, GetRaw(row, idColumn));
        return checked(maximum + 1);
    }

    public string GetString(uint offset)
    {
        if (offset >= _strings.Length)
            return $"<invalid string offset {offset}>";
        var tail = _strings.AsSpan((int)offset);
        var length = tail.IndexOf((byte)0);
        if (length < 0) length = tail.Length;
        return Encoding.UTF8.GetString(tail[..length]);
    }

    private uint GetOrAddString(string value)
    {
        if (value.Length == 0) return 0;
        EnsureStringIndex();
        if (_stringOffsets!.TryGetValue(value, out var existing)) return existing;

        var encoded = Encoding.UTF8.GetBytes(value);
        var offset = checked((uint)_strings.Length);
        Array.Resize(ref _strings, checked(_strings.Length + encoded.Length + 1));
        encoded.CopyTo(_strings.AsSpan((int)offset));
        _strings[^1] = 0;
        _stringOffsets[value] = offset;
        return offset;
    }

    private void EnsureStringIndex()
    {
        if (_stringOffsets is not null) return;
        _stringOffsets = new(StringComparer.Ordinal);
        var offset = 0;
        while (offset < _strings.Length)
        {
            var tail = _strings.AsSpan(offset);
            var length = tail.IndexOf((byte)0);
            if (length < 0) length = tail.Length;
            var value = Encoding.UTF8.GetString(tail[..length]);
            _stringOffsets.TryAdd(value, (uint)offset);
            offset += length + 1;
        }
    }

    public bool RowContains(int row, string query, IReadOnlyList<DbcColumn> columns)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var column in columns)
        {
            var value = GetDisplayValue(row, column);
            if (Convert.ToString(value, CultureInfo.InvariantCulture)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        return false;
    }

    public void Save(string path, bool createBackup = true)
    {
        var fullPath = Path.GetFullPath(path);
        if (createBackup && File.Exists(fullPath))
            File.Copy(fullPath, fullPath + ".bak", true);

        var temp = fullPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Span<byte> header = stackalloc byte[ContainerKind == ClientTableContainerKind.Wdb2 ? 48 : HeaderSize];
            (ContainerKind == ClientTableContainerKind.Wdb2 ? "WDB2"u8 : "WDBC"u8).CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..8], RowCount);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..12], FieldCount);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..16], RecordSize);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..20], _strings.Length);
            if (ContainerKind == ClientTableContainerKind.Wdb2)
            {
                var metadata = Db2Metadata ?? throw new InvalidDataException("WDB2 metadata is missing.");
                BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], metadata.TableHash); BinaryPrimitives.WriteInt32LittleEndian(header[24..28], metadata.Build);
                BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], metadata.Timestamp); BinaryPrimitives.WriteUInt32LittleEndian(header[32..36], metadata.MinId);
                BinaryPrimitives.WriteUInt32LittleEndian(header[36..40], metadata.MaxId); BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], metadata.Locale);
                BinaryPrimitives.WriteInt32LittleEndian(header[44..48], _db2CopyTable.Length);
            }
            stream.Write(header);
            if (ContainerKind == ClientTableContainerKind.Wdb2 && Db2Metadata is { HasIndexMap: true } db2)
            {
                Span<byte> mapBytes = stackalloc byte[4]; foreach (var entry in db2.IndexMap) { BinaryPrimitives.WriteInt32LittleEndian(mapBytes, entry); stream.Write(mapBytes); }
                Span<byte> lengthBytes = stackalloc byte[2]; foreach (var entry in db2.StringLengths) { BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, entry); stream.Write(lengthBytes); }
            }
            stream.Write(_records.AsSpan(0, checked(RowCount * RecordSize)));
            stream.Write(_strings);
            if (ContainerKind == ClientTableContainerKind.Wdb2) stream.Write(_db2CopyTable);
            stream.Flush(true);
        }
        File.Move(temp, fullPath, true);
        WriteIdentity(fullPath);
        IsDirty = false;
    }

    public void SaveAs(string path, bool createBackup = true)
    {
        Save(path, createBackup);
        SourcePath = Path.GetFullPath(path);
    }

    private static int ReadPositive(ReadOnlySpan<byte> bytes, string name)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return value > 0 ? value : throw new InvalidDataException($"Invalid {name}: {value}.");
    }

    private void EnsureRowCapacity(int additionalRows)
    {
        var requiredRows = checked(RowCount + additionalRows);
        var requiredBytes = checked(requiredRows * RecordSize);
        if (requiredBytes <= _records.Length) return;

        var currentRows = _records.Length / RecordSize;
        var grownRows = Math.Max(requiredRows, Math.Max(16, checked(currentRows * 2)));
        var expanded = GC.AllocateUninitializedArray<byte>(checked(grownRows * RecordSize));
        _records.AsSpan(0, RowCount * RecordSize).CopyTo(expanded);
        _records = expanded;
    }

    private static int ReadNonNegative(ReadOnlySpan<byte> bytes, string name)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return value >= 0 ? value : throw new InvalidDataException($"Invalid {name}: {value}.");
    }

    private static uint ParseUInt(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static uint ParseSigned(string text, int size)
    {
        var value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return size switch
        {
            1 when value is >= sbyte.MinValue and <= sbyte.MaxValue => unchecked((byte)(sbyte)value),
            2 when value is >= short.MinValue and <= short.MaxValue => unchecked((ushort)(short)value),
            4 => unchecked((uint)value),
            1 or 2 => throw new OverflowException($"Signed value {value} does not fit in {size * 8} bits."),
            _ => throw new NotSupportedException($"Signed {size}-byte scalars are not supported.")
        };
    }

    private void ValidateCell(int row, DbcColumn column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(column.Index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column.Index, FieldCount);
        if (column.Offset < 0 || column.Offset + column.Size > RecordSize)
            throw new InvalidDataException($"Schema field '{column.Name}' exceeds the {RecordSize}-byte record.");
    }

    private void RequireStructuralMutation()
    {
        if (!AllowsStructuralMutation)
            throw new InvalidOperationException("This WDB2 contains an ID index map or copy table. Cell edits are supported, but adding, cloning, or deleting physical rows is blocked until Crucible can rebuild every dependent side table.");
    }

    private static string ReadIdentity(string path, Wdb2Metadata metadata)
    {
        var sidecar = IdentityPath(path); if (!File.Exists(sidecar)) return Path.GetFileNameWithoutExtension(path);
        try
        {
            var identity = JsonSerializer.Deserialize<ClientTableIdentity>(File.ReadAllText(sidecar), IdentityJsonOptions) ?? throw new InvalidDataException("The identity sidecar is empty.");
            if (identity.Container != ClientTableContainerKind.Wdb2 || identity.Build != metadata.Build || identity.TableHash != metadata.TableHash || string.IsNullOrWhiteSpace(identity.TableName))
                throw new InvalidDataException("The identity sidecar does not match this WDB2 header.");
            return identity.TableName;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Invalid WDB2 identity sidecar '{sidecar}': {exception.Message}", exception);
        }
    }

    private void WriteIdentity(string path)
    {
        if (ContainerKind != ClientTableContainerKind.Wdb2 || Db2Metadata is not { } metadata || Path.GetFileNameWithoutExtension(path).Equals(LogicalTableName, StringComparison.OrdinalIgnoreCase)) return;
        var sidecar = IdentityPath(path); var temporary = sidecar + $".crucible-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ClientTableIdentity(LogicalTableName, ContainerKind, metadata.Build, metadata.TableHash), IdentityJsonOptions));
            File.Move(temporary, sidecar, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static readonly JsonSerializerOptions IdentityJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static string IdentityPath(string tablePath) => Path.GetFullPath(tablePath) + ".crucible-table.json";
}
