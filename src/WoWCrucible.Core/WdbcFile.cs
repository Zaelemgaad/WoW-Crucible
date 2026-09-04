using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum ClientTableContainerKind { Wdbc, Wdb2, Wdc1 }
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
    private IReadOnlyList<DbcColumn>? _wdb2Columns;
    private DbcColumn? _wdb2Key;
    private bool _wdb2SideTablesDirty;
    private Wdc1TableData? _wdc1;

    private WdbcFile(string sourcePath, int rowCount, int fieldCount, int recordSize, byte[] records, byte[] strings,
        ClientTableContainerKind containerKind = ClientTableContainerKind.Wdbc, Wdb2Metadata? db2Metadata = null, byte[]? db2CopyTable = null, string? logicalTableName = null,
        Wdc1TableData? wdc1 = null)
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
        _wdc1 = wdc1;
        LogicalTableName = string.IsNullOrWhiteSpace(logicalTableName) ? Path.GetFileNameWithoutExtension(sourcePath) : logicalTableName;
    }

    public string SourcePath { get; private set; }
    public int RowCount { get; private set; }
    public int FieldCount { get; private set; }
    public int RecordSize { get; private set; }
    public int StringTableSize => _wdc1?.StringTableSize ?? _strings.Length;
    public bool IsDirty { get; private set; }
    public ClientTableContainerKind ContainerKind { get; }
    public Wdb2Metadata? Db2Metadata { get; private set; }
    public Wdc1Metadata? Wdc1Metadata => _wdc1?.Metadata;
    public int PhysicalHeaderSize => ContainerKind switch
    {
        ClientTableContainerKind.Wdb2 => 48 + (Db2Metadata?.IndexMap.Count ?? 0) * 6,
        ClientTableContainerKind.Wdc1 => _wdc1?.PhysicalHeaderSize ?? throw new InvalidDataException("WDC1 state is missing."),
        _ => HeaderSize
    };
    public bool AllowsStructuralMutation => _wdc1?.AllowsStructuralMutation ?? Db2Metadata switch
    {
        null => true,
        { CopyTableSize: > 0 } => false,
        { HasIndexMap: false } => true,
        _ => _wdb2Key is not null
    };
    public string LogicalTableName { get; }
    public string? LastBackupPath { get; private set; }

    public WdbcFile CloneInMemory()
    {
        if (_wdc1 is not null)
        {
            return new WdbcFile(SourcePath, RowCount, FieldCount, RecordSize, [], [], ClientTableContainerKind.Wdc1,
                logicalTableName: LogicalTableName, wdc1: _wdc1.Clone()) { IsDirty = IsDirty };
        }
        var metadata = Db2Metadata is null ? null : Db2Metadata with { IndexMap = Db2Metadata.IndexMap.ToArray(), StringLengths = Db2Metadata.StringLengths.ToArray() };
        var clone = new WdbcFile(SourcePath, RowCount, FieldCount, RecordSize,
            _records.AsSpan(0, checked(RowCount * RecordSize)).ToArray(), _strings.ToArray(), ContainerKind, metadata, _db2CopyTable.ToArray(), LogicalTableName)
        {
            IsDirty = IsDirty,
            _wdb2Columns = _wdb2Columns?.ToArray(),
            _wdb2Key = _wdb2Key,
            _wdb2SideTablesDirty = _wdb2SideTablesDirty
        };
        return clone;
    }

    public string ComputeContentSha256()
    {
        RebuildWdb2SideTablesIfNeeded();
        if (_wdc1 is not null) return _wdc1.ComputeContentSha256();
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
        if (_wdc1 is not null)
        {
            if (source._wdc1 is null) throw new InvalidDataException("The replacement WDC1 state is missing.");
            _wdc1.ReplaceFrom(source._wdc1); RowCount = _wdc1.RowCount; RecordSize = _wdc1.RecordSize; IsDirty = true; return;
        }
        RowCount = source.RowCount;
        _records = source._records.AsSpan(0, checked(source.RowCount * source.RecordSize)).ToArray();
        _strings = source._strings.ToArray();
        Db2Metadata = source.Db2Metadata is null ? null : source.Db2Metadata with { IndexMap = source.Db2Metadata.IndexMap.ToArray(), StringLengths = source.Db2Metadata.StringLengths.ToArray() };
        _db2CopyTable = source._db2CopyTable.ToArray();
        _wdb2Columns = source._wdb2Columns?.ToArray();
        _wdb2Key = source._wdb2Key;
        _wdb2SideTablesDirty = source._wdb2SideTablesDirty;
        _stringOffsets = null;
        IsDirty = true;
    }

    public static WdbcFile Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (header[..4].SequenceEqual("WDB2"u8)) return LoadWdb2(path, stream, header);
        if (header[..4].SequenceEqual("WDC1"u8))
        {
            var data = Wdc1TableData.Load(path, header);
            return new(path, data.RowCount, data.FieldCount, data.RecordSize, [], [], ClientTableContainerKind.Wdc1,
                logicalTableName: Path.GetFileNameWithoutExtension(path), wdc1: data);
        }
        if (!header[..4].SequenceEqual("WDBC"u8))
            throw new InvalidDataException("Unsupported client-table container. Crucible currently accepts WDBC, fixed-layout WDB2, and WDC1; WDB5/WDB6 and later WDC families require their matching adapter.");

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

    public void ConfigureWdc1Schema(IReadOnlyList<DbcColumn> columns)
    {
        if (_wdc1 is null) throw new InvalidOperationException("Only WDC1 tables require a WDC1 storage mapping.");
        _wdc1.ConfigureSchema(columns);
        RowCount = _wdc1.RowCount;
    }

    public void ConfigureWdb2Schema(IReadOnlyList<DbcColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (ContainerKind != ClientTableContainerKind.Wdb2 || Db2Metadata?.HasIndexMap != true) return;
        if (columns.Count == 0 || columns.Any(column => column.StorageKind != DbcColumnStorageKind.Record))
            throw new InvalidDataException("Indexed WDB2 structural mutation requires a complete fixed-record schema.");
        var key = columns.Where(column => column.IsIndex).ToArray();
        if (key.Length != 1 || key[0].Size is not (1 or 2 or 4) || key[0].Offset < 0 || key[0].Offset + key[0].Size > RecordSize)
            throw new InvalidDataException("Indexed WDB2 structural mutation requires exactly one physical 8/16/32-bit ID column.");
        foreach (var column in columns)
            if (column.Offset < 0 || column.Size <= 0 || column.Offset + column.Size > RecordSize)
                throw new InvalidDataException($"Schema field '{column.Name}' exceeds the {RecordSize:N0}-byte WDB2 record.");

        _wdb2Columns = columns.ToArray();
        _wdb2Key = key[0];
        if (Db2Metadata.CopyTableSize == 0) ValidateWdb2SideTables();
    }

    public ulong GetRaw64(int row, DbcColumn column)
    {
        if (_wdc1 is not null) return _wdc1.GetRaw64(row, column);
        ValidateCell(row, column);
        var cell = _records.AsSpan(row * RecordSize + column.Offset, column.Size);
        return column.Size switch
        {
            1 => cell[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(cell),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(cell),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(cell),
            _ => throw new NotSupportedException($"Field '{column.Name}' uses unsupported {column.Size}-byte scalar storage.")
        };
    }

    public uint GetRaw(int row, DbcColumn column)
    {
        var value = GetRaw64(row, column);
        return value <= uint.MaxValue ? (uint)value : throw new OverflowException($"Field '{column.Name}' contains a 64-bit value; use GetRaw64.");
    }

    public void SetRaw64(int row, DbcColumn column, ulong raw)
    {
        if (_wdc1 is not null)
        {
            _wdc1.SetRaw64(row, column, raw); IsDirty = true; return;
        }
        ValidateCell(row, column);
        if (ContainerKind == ClientTableContainerKind.Wdb2 && Db2Metadata?.HasIndexMap == true && (column.IsIndex || column.Type == DbcValueType.StringOffset))
        {
            if (_wdb2Key is null || _wdb2Columns is null)
                throw new InvalidOperationException("This WDB2 uses ID/string side tables. Resolve and configure its exact schema before editing indexed IDs or strings.");
            if (Db2Metadata.CopyTableSize > 0 && column.IsIndex)
                throw new InvalidOperationException("This WDB2 uses an ID index map plus a copy-record side table. Indexed ID edits remain blocked until copy references can be rebuilt.");
            _wdb2SideTablesDirty = true;
        }
        if (column.Size == 1 && raw > byte.MaxValue || column.Size == 2 && raw > ushort.MaxValue || column.Size == 4 && raw > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(raw), $"The value does not fit in this {column.Size}-byte field.");
        var cell = _records.AsSpan(row * RecordSize + column.Offset, column.Size);
        if (column.Size == 1) cell[0] = (byte)raw;
        else if (column.Size == 2) BinaryPrimitives.WriteUInt16LittleEndian(cell, (ushort)raw);
        else if (column.Size == 4) BinaryPrimitives.WriteUInt32LittleEndian(cell, (uint)raw);
        else if (column.Size == 8) BinaryPrimitives.WriteUInt64LittleEndian(cell, raw);
        else throw new NotSupportedException($"Field '{column.Name}' uses unsupported {column.Size}-byte scalar storage.");
        IsDirty = true;
    }

    public void SetRaw(int row, DbcColumn column, uint raw) => SetRaw64(row, column, raw);

    public object GetDisplayValue(int row, DbcColumn column) => column.Type switch
    {
        DbcValueType.Int32 => checked((int)SignExtend(GetRaw64(row, column), column.EffectiveBitWidth)),
        DbcValueType.UInt32 or DbcValueType.Byte => checked((uint)GetRaw64(row, column)),
        DbcValueType.Int64 => SignExtend(GetRaw64(row, column), column.EffectiveBitWidth),
        DbcValueType.UInt64 => GetRaw64(row, column),
        DbcValueType.Float32 => BitConverter.UInt32BitsToSingle(checked((uint)GetRaw64(row, column))),
        DbcValueType.StringOffset => GetString(GetRaw64(row, column)),
        _ => checked((uint)GetRaw64(row, column))
    };

    public void SetDisplayValue(int row, DbcColumn column, object? value)
    {
        if (column.Type == DbcValueType.StringOffset && ContainerKind == ClientTableContainerKind.Wdb2 && Db2Metadata?.HasIndexMap == true && _wdb2Columns is null)
            throw new InvalidOperationException("This WDB2 uses per-record string-length metadata. Resolve and configure its exact schema before editing strings.");
        var converted = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var text = column.Type == DbcValueType.StringOffset ? converted : converted.Trim();
        ulong raw = column.Type switch
        {
            DbcValueType.Int32 or DbcValueType.Int64 => ParseSigned(text, column.EffectiveBitWidth),
            DbcValueType.UInt32 or DbcValueType.Raw32 => ParseUInt(text),
            DbcValueType.UInt64 => ParseULong(text),
            DbcValueType.Byte => byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            DbcValueType.Float32 => BitConverter.SingleToUInt32Bits(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
            DbcValueType.StringOffset => GetOrAddString(text),
            _ => throw new ArgumentOutOfRangeException()
        };

        SetRaw64(row, column, raw);
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
        if (_wdc1 is not null)
        {
            var wdcTargetRow = _wdc1.CloneRows(sourceRow, 1, idColumn); _wdc1.SetRaw64(wdcTargetRow, idColumn, targetId);
            RowCount = _wdc1.RowCount; IsDirty = true; return wdcTargetRow;
        }
        var targetRow = RowCount; EnsureRowCapacity(1);
        _records.AsSpan(sourceRow * RecordSize, RecordSize).CopyTo(_records.AsSpan(targetRow * RecordSize, RecordSize));
        RowCount++; MarkWdb2StructuralMutation(); SetRaw(targetRow, idColumn, targetId); IsDirty = true; return targetRow;
    }

    public int AddBlankRows(int count, DbcColumn? idColumn = null)
    {
        RequireStructuralMutation();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (_wdc1 is not null)
        {
            var first = _wdc1.AddBlankRows(count, idColumn); RowCount = _wdc1.RowCount; IsDirty = true; return first;
        }
        var firstRow = RowCount;
        var firstId = idColumn is null ? 0u : NextId(idColumn);
        EnsureRowCapacity(count);
        for (var index = 0; index < count; index++)
        {
            var row = RowCount++;
            _records.AsSpan(row * RecordSize, RecordSize).Clear();
            if (idColumn is not null) SetRaw(row, idColumn, checked(firstId + (uint)index));
        }
        MarkWdb2StructuralMutation();
        IsDirty = true;
        return firstRow;
    }

    public int CloneRows(int sourceRow, int count, DbcColumn? idColumn = null)
    {
        RequireStructuralMutation();
        ArgumentOutOfRangeException.ThrowIfNegative(sourceRow);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sourceRow, RowCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (_wdc1 is not null)
        {
            var first = _wdc1.CloneRows(sourceRow, count, idColumn); RowCount = _wdc1.RowCount; IsDirty = true; return first;
        }
        var firstRow = RowCount;
        var firstId = idColumn is null ? 0u : NextId(idColumn);
        EnsureRowCapacity(count);
        for (var index = 0; index < count; index++)
        {
            var row = RowCount++;
            _records.AsSpan(sourceRow * RecordSize, RecordSize).CopyTo(_records.AsSpan(row * RecordSize, RecordSize));
            if (idColumn is not null) SetRaw(row, idColumn, checked(firstId + (uint)index));
        }
        MarkWdb2StructuralMutation();
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
        if (_wdc1 is not null)
        {
            _wdc1.DeleteRows(deleted); RowCount = _wdc1.RowCount; IsDirty = true; return;
        }

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
        MarkWdb2StructuralMutation();
        IsDirty = true;
    }

    public uint NextId(DbcColumn idColumn)
    {
        if (_wdc1 is not null) return _wdc1.NextId(idColumn);
        uint maximum = 0;
        for (var row = 0; row < RowCount; row++)
            maximum = Math.Max(maximum, GetRaw(row, idColumn));
        return checked(maximum + 1);
    }

    public string GetString(ulong offset)
    {
        if (_wdc1 is not null) return _wdc1.GetString(offset);
        if (offset > uint.MaxValue) return $"<invalid string offset {offset}>";
        if (offset >= (ulong)_strings.Length)
            return $"<invalid string offset {offset}>";
        var tail = _strings.AsSpan((int)offset);
        var length = tail.IndexOf((byte)0);
        if (length < 0) length = tail.Length;
        return Encoding.UTF8.GetString(tail[..length]);
    }

    private ulong GetOrAddString(string value)
    {
        if (_wdc1 is not null) return _wdc1.GetOrAddString(value);
        if (value.Length == 0)
        {
            // Offset zero is only an empty string when the first byte is NUL. Some
            // real custom WDBC files start their string block with live text and use
            // a later terminator byte for semantic empty values. Reusing raw zero in
            // those files silently turns an empty field into the first client path.
            var emptyOffset = Array.IndexOf(_strings, (byte)0);
            if (emptyOffset >= 0) return checked((uint)emptyOffset);
            var appended = checked((uint)_strings.Length);
            Array.Resize(ref _strings, checked(_strings.Length + 1));
            _strings[^1] = 0;
            _stringOffsets = null;
            return appended;
        }
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
        RebuildWdb2SideTablesIfNeeded();
        LastBackupPath = createBackup ? CrucibleBackupService.Create(fullPath, "ClientTables") : null;

        var temp = fullPath + ".tmp";
        if (_wdc1 is not null)
        {
            var bytes = _wdc1.GetPersistedBytes();
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temp, fullPath, true);
            _wdc1.Commit(bytes); RowCount = _wdc1.RowCount; FieldCount = _wdc1.FieldCount; RecordSize = _wdc1.RecordSize;
            IsDirty = false; return;
        }
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

    private static uint ParseUInt(string text)
    {
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalValue)) return decimalValue;
        var hexadecimal = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text.AsSpan();
        if (hexadecimal.Length is > 0 and <= 8 && uint.TryParse(hexadecimal, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rawValue)) return rawValue;
        throw new FormatException($"'{text}' is not an unsigned 32-bit decimal value (0..{uint.MaxValue}) or a 1-8 digit hexadecimal raw value (optional 0x prefix).");
    }

    private static ulong ParseULong(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static ulong ParseSigned(string text, int bitWidth)
    {
        if (bitWidth is <= 0 or > 64) throw new NotSupportedException($"Signed {bitWidth}-bit scalars are not supported.");
        var value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (bitWidth < 64)
        {
            var minimum = -(1L << (bitWidth - 1));
            var maximum = (1L << (bitWidth - 1)) - 1;
            if (value < minimum || value > maximum) throw new OverflowException($"Signed value {value} does not fit in {bitWidth} bits.");
            return unchecked((ulong)value) & ((1UL << bitWidth) - 1);
        }
        return unchecked((ulong)value);
    }

    private static long SignExtend(ulong value, int bitWidth)
    {
        if (bitWidth is <= 0 or > 64) throw new NotSupportedException($"Signed {bitWidth}-bit scalars are not supported.");
        if (bitWidth == 64) return unchecked((long)value);
        var mask = (1UL << bitWidth) - 1; value &= mask;
        return (value & (1UL << (bitWidth - 1))) == 0 ? (long)value : unchecked((long)(value | ~mask));
    }

    private void ValidateCell(int row, DbcColumn column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(column.Index);
        if (column.Offset < 0 || column.Offset + column.Size > RecordSize)
            throw new InvalidDataException($"Schema field '{column.Name}' exceeds the {RecordSize}-byte record.");
    }

    private void MarkWdb2StructuralMutation()
    {
        if (ContainerKind == ClientTableContainerKind.Wdb2 && Db2Metadata?.HasIndexMap == true)
            _wdb2SideTablesDirty = true;
    }

    private void ValidateWdb2SideTables()
    {
        var metadata = Db2Metadata ?? throw new InvalidDataException("WDB2 metadata is missing.");
        var key = _wdb2Key ?? throw new InvalidDataException("The indexed WDB2 schema has no physical ID column.");
        var columns = _wdb2Columns ?? throw new InvalidDataException("The indexed WDB2 schema is not configured.");
        if (metadata.MaxId < metadata.MinId)
            throw new InvalidDataException($"Invalid WDB2 ID range {metadata.MinId:N0}..{metadata.MaxId:N0}.");
        var expectedRange = checked((long)metadata.MaxId - metadata.MinId + 1);
        if (expectedRange != metadata.IndexMap.Count || metadata.StringLengths.Count != metadata.IndexMap.Count)
            throw new InvalidDataException("The indexed WDB2 ID and string-length side tables do not match the declared ID range.");

        var physicalIds = new HashSet<uint>();
        for (var row = 0; row < RowCount; row++)
        {
            var id = GetRaw(row, key);
            if (id == 0 || id < metadata.MinId || id > metadata.MaxId || !physicalIds.Add(id))
                throw new InvalidDataException($"Indexed WDB2 row {row + 1:N0} has invalid or duplicate physical ID {id:N0}.");
            var mapIndex = checked((int)(id - metadata.MinId));
            if (metadata.IndexMap[mapIndex] != row + 1)
                throw new InvalidDataException($"Indexed WDB2 ID {id:N0} maps to physical row {metadata.IndexMap[mapIndex]:N0}, expected {row + 1:N0}.");
            var expectedLength = Wdb2RowStringLength(row, columns);
            if (metadata.StringLengths[mapIndex] != expectedLength)
                throw new InvalidDataException($"Indexed WDB2 ID {id:N0} declares {metadata.StringLengths[mapIndex]:N0} string byte(s), expected {expectedLength:N0} from the selected schema.");
        }

        for (var index = 0; index < metadata.IndexMap.Count; index++)
        {
            var mappedRow = metadata.IndexMap[index];
            if (mappedRow == 0)
            {
                if (metadata.StringLengths[index] != 0)
                    throw new InvalidDataException($"Indexed WDB2 ID {metadata.MinId + (uint)index:N0} has string bytes but no physical row.");
                continue;
            }
            if (mappedRow < 1 || mappedRow > RowCount)
                throw new InvalidDataException($"Indexed WDB2 ID {metadata.MinId + (uint)index:N0} maps outside its {RowCount:N0} physical rows.");
            if (GetRaw(mappedRow - 1, key) != metadata.MinId + (uint)index)
                throw new InvalidDataException($"Indexed WDB2 side-table entry {metadata.MinId + (uint)index:N0} points at a row with a different physical ID.");
        }
    }

    private ushort Wdb2RowStringLength(int row, IReadOnlyList<DbcColumn> columns)
    {
        var total = 0;
        foreach (var column in columns.Where(column => column.Type == DbcValueType.StringOffset).OrderBy(column => column.Offset))
        {
            var offset = GetRaw64(row, column);
            if (offset >= (ulong)_strings.Length)
                throw new InvalidDataException($"WDB2 row {row + 1:N0}, field '{column.Name}' has invalid string offset {offset:N0}.");
            var value = GetString(offset);
            if (value.Length == 0) continue;
            total = checked(total + Encoding.UTF8.GetByteCount(value) + 1);
            if (total > ushort.MaxValue)
                throw new InvalidDataException($"WDB2 row {row + 1:N0} needs {total:N0} string bytes, exceeding the 16-bit side-table limit.");
        }
        return checked((ushort)total);
    }

    private void RebuildWdb2SideTablesIfNeeded()
    {
        if (!_wdb2SideTablesDirty) return;
        var metadata = Db2Metadata ?? throw new InvalidDataException("WDB2 metadata is missing.");
        if (!metadata.HasIndexMap) { _wdb2SideTablesDirty = false; return; }
        if (metadata.CopyTableSize > 0)
            throw new InvalidOperationException("This WDB2 has copy records; its ID/string side tables cannot be rebuilt without preserving those aliases.");
        var key = _wdb2Key ?? throw new InvalidOperationException("Resolve and configure the exact WDB2 schema before rebuilding its side tables.");
        var columns = _wdb2Columns ?? throw new InvalidOperationException("Resolve and configure the exact WDB2 schema before rebuilding its side tables.");
        if (RowCount == 0)
            throw new InvalidOperationException("Indexed WDB2 files cannot currently be persisted with zero physical rows because their empty ID-range encoding is not proven.");

        var ids = new uint[RowCount];
        var occupied = new HashSet<uint>();
        for (var row = 0; row < RowCount; row++)
        {
            var id = GetRaw(row, key);
            if (id == 0 || !occupied.Add(id))
                throw new InvalidDataException($"Indexed WDB2 row {row + 1:N0} has invalid or duplicate physical ID {id:N0}.");
            ids[row] = id;
        }
        var minId = ids.Min();
        var maxId = ids.Max();
        var range = checked((long)maxId - minId + 1);
        if (range > Array.MaxLength)
            throw new InvalidDataException($"Indexed WDB2 ID range {minId:N0}..{maxId:N0} is too large to materialize safely.");

        using var strings = new MemoryStream(Math.Max(_strings.Length, 2));
        strings.WriteByte(0);
        if (metadata.Build > 18273) strings.WriteByte(0);
        var rowLengths = new ushort[RowCount];
        var stringColumns = columns.Where(column => column.Type == DbcValueType.StringOffset).OrderBy(column => column.Offset).ToArray();
        for (var row = 0; row < RowCount; row++)
        {
            var total = 0;
            foreach (var column in stringColumns)
            {
                var oldOffset = GetRaw64(row, column);
                if (oldOffset >= (ulong)_strings.Length)
                    throw new InvalidDataException($"WDB2 row {row + 1:N0}, field '{column.Name}' has invalid string offset {oldOffset:N0}.");
                var value = GetString(oldOffset);
                uint newOffset = 0;
                if (value.Length > 0)
                {
                    var encoded = Encoding.UTF8.GetBytes(value);
                    if (strings.Position > uint.MaxValue)
                        throw new InvalidDataException("The rebuilt WDB2 string table exceeds the 32-bit offset range.");
                    newOffset = checked((uint)strings.Position);
                    strings.Write(encoded);
                    strings.WriteByte(0);
                    total = checked(total + encoded.Length + 1);
                    if (total > ushort.MaxValue)
                        throw new InvalidDataException($"WDB2 row {row + 1:N0} needs {total:N0} string bytes, exceeding the 16-bit side-table limit.");
                }
                WriteFixedRaw(row, column, newOffset);
            }
            rowLengths[row] = checked((ushort)total);
        }

        var indexMap = new int[(int)range];
        var stringLengths = new ushort[(int)range];
        for (var row = 0; row < RowCount; row++)
        {
            var index = checked((int)(ids[row] - minId));
            indexMap[index] = checked(row + 1);
            stringLengths[index] = rowLengths[row];
        }
        _strings = strings.ToArray();
        _stringOffsets = null;
        Db2Metadata = metadata with { MinId = minId, MaxId = maxId, IndexMap = indexMap, StringLengths = stringLengths, CopyTableSize = 0 };
        _wdb2SideTablesDirty = false;
        ValidateWdb2SideTables();
    }

    private void WriteFixedRaw(int row, DbcColumn column, ulong value)
    {
        var cell = _records.AsSpan(row * RecordSize + column.Offset, column.Size);
        if (column.Size == 1 && value <= byte.MaxValue) cell[0] = (byte)value;
        else if (column.Size == 2 && value <= ushort.MaxValue) BinaryPrimitives.WriteUInt16LittleEndian(cell, (ushort)value);
        else if (column.Size == 4 && value <= uint.MaxValue) BinaryPrimitives.WriteUInt32LittleEndian(cell, (uint)value);
        else if (column.Size == 8) BinaryPrimitives.WriteUInt64LittleEndian(cell, value);
        else throw new InvalidDataException($"Value {value:N0} does not fit WDB2 field '{column.Name}'.");
    }

    private void RequireStructuralMutation()
    {
        if (!AllowsStructuralMutation)
            throw new InvalidOperationException(ContainerKind == ClientTableContainerKind.Wdc1
                ? "This offset-map WDC1 must be paired with its exact DBD layout before records can be added, cloned, or deleted."
                : Db2Metadata?.CopyTableSize > 0
                    ? "This WDB2 contains a copy-record side table. Structural edits are blocked until its ID aliases can be rebuilt."
                    : "This WDB2 contains ID/string side tables. Resolve and configure its exact schema before adding, cloning, or deleting physical rows.");
    }

    private static string ReadIdentity(string path, Wdb2Metadata metadata)
    {
        var sidecar = IdentityPath(path);
        if (!File.Exists(sidecar))
        {
            var legacy = Path.GetFullPath(path) + ".crucible-table.json";
            if (!File.Exists(legacy)) return Path.GetFileNameWithoutExtension(path);
            sidecar = legacy;
        }
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
        var sidecar = IdentityPath(path); Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!); var temporary = sidecar + $".crucible-{Guid.NewGuid():N}.tmp";
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

    public static string IdentityPath(string tablePath)
    {
        var full = Path.GetFullPath(tablePath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16].ToLowerInvariant();
        var safeName = new string(Path.GetFileNameWithoutExtension(full).Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
        return Path.Combine(CruciblePaths.TableIdentityDirectory, $"{safeName}-{hash}.json");
    }
}
