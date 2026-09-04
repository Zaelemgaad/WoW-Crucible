using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

public enum Wdc1StorageMode
{
    None,
    Immediate,
    Common,
    Pallet,
    PalletArray
}

public sealed record Wdc1FieldStorageMetadata(
    int Index,
    Wdc1StorageMode Mode,
    int FieldOffsetBits,
    int FieldSizeBits,
    int ElementBitWidth,
    int ArrayCount,
    int AdditionalDataSize,
    bool Signed);

public sealed record Wdc1Metadata(
    uint TableHash,
    uint LayoutHash,
    uint MinId,
    uint MaxId,
    uint Locale,
    ushort Flags,
    ushort IdIndex,
    int PhysicalRecordCount,
    int CopyRecordCount,
    int TotalFieldCount,
    int BitpackedDataOffset,
    int LookupColumnCount,
    int OffsetMapOffset,
    int IdListSize,
    int FieldStorageInfoSize,
    int CommonDataSize,
    int PalletDataSize,
    int RelationshipDataSize)
{
    public IReadOnlyList<Wdc1FieldStorageMetadata> FieldStorage { get; init; } = [];
    public bool HasOffsetMap => (Flags & 0x1) != 0;
    public bool HasExternalIds => (Flags & 0x4) != 0;
    public bool HasCopyTable => CopyRecordCount > 0;
    public bool HasRelationshipData => RelationshipDataSize > 0;
}

internal enum Wdc1StorageType : uint
{
    None = 0,
    Immediate = 1,
    Common = 2,
    Pallet = 3,
    PalletArray = 4
}

internal readonly record struct Wdc1FieldStructure(short SizeBitsDelta, ushort Position)
{
    public int ElementBitWidth => checked(32 - SizeBitsDelta);
}

internal readonly record struct Wdc1FieldStorageInfo(
    ushort FieldOffsetBits,
    ushort FieldSizeBits,
    uint AdditionalDataSize,
    Wdc1StorageType StorageType,
    uint BitpackingOffsetBits,
    uint BitpackingSizeBits,
    uint Flags)
{
    public uint DefaultValue => BitpackingOffsetBits;
    public int PalletArrayCount => StorageType == Wdc1StorageType.PalletArray ? checked((int)Flags) : 1;
    public bool IsSigned => StorageType == Wdc1StorageType.Immediate && (Flags & 0x1) != 0;
}

internal sealed class Wdc1TableData
{
    private const int HeaderSize = 84;
    private const int FieldStructureSize = 4;
    private const int StorageInfoSize = 24;

    private sealed class Row(uint id, ulong[][] values, uint? relationship)
    {
        public uint Id { get; set; } = id;
        public ulong[][] Values { get; } = values;
        public uint? Relationship { get; set; } = relationship;

        public Row Clone(uint? id = null) => new(id ?? Id, Values.Select(values => values.ToArray()).ToArray(), Relationship);
    }

    private sealed record ParsedHeader(
        int RecordCount,
        int FieldCount,
        int RecordSize,
        int StringTableSize,
        uint TableHash,
        uint LayoutHash,
        uint MinId,
        uint MaxId,
        uint Locale,
        int CopyTableSize,
        ushort Flags,
        ushort IdIndex,
        int TotalFieldCount,
        int BitpackedDataOffset,
        int LookupColumnCount,
        int OffsetMapOffset,
        int IdListSize,
        int FieldStorageInfoSize,
        int CommonDataSize,
        int PalletDataSize,
        int RelationshipDataSize);

    private byte[] _originalBytes;
    private ParsedHeader _header;
    private Wdc1FieldStructure[] _fieldStructures;
    private Wdc1FieldStorageInfo[] _storage;
    private byte[][] _additionalData;
    private byte[] _recordData;
    private byte[] _offsetMap;
    private byte[] _idList;
    private byte[] _copyTable;
    private byte[] _strings;
    private uint[] _relationshipValues;
    private List<Row>? _rows;
    private IReadOnlyList<DbcColumn>? _configuredColumns;
    private bool _dirty;

    private Wdc1TableData(
        byte[] originalBytes,
        ParsedHeader header,
        Wdc1FieldStructure[] fieldStructures,
        Wdc1FieldStorageInfo[] storage,
        byte[][] additionalData,
        byte[] recordData,
        byte[] offsetMap,
        byte[] idList,
        byte[] copyTable,
        byte[] strings,
        uint[] relationshipValues)
    {
        _originalBytes = originalBytes;
        _header = header;
        _fieldStructures = fieldStructures;
        _storage = storage;
        _additionalData = additionalData;
        _recordData = recordData;
        _offsetMap = offsetMap;
        _idList = idList;
        _copyTable = copyTable;
        _strings = strings;
        _relationshipValues = relationshipValues;
        if (!Metadata.HasOffsetMap) _rows = DecodeRegularRows();
    }

    public Wdc1Metadata Metadata => new(
        _header.TableHash, _header.LayoutHash, _header.MinId, _header.MaxId, _header.Locale, _header.Flags, _header.IdIndex,
        _header.RecordCount, _header.CopyTableSize / 8, _header.TotalFieldCount, _header.BitpackedDataOffset,
        _header.LookupColumnCount, _header.OffsetMapOffset, _header.IdListSize, _header.FieldStorageInfoSize,
        _header.CommonDataSize, _header.PalletDataSize, _header.RelationshipDataSize)
    {
        FieldStorage = this._storage.Select((storageInfo, index) => new Wdc1FieldStorageMetadata(
            index,
            (Wdc1StorageMode)storageInfo.StorageType,
            storageInfo.FieldOffsetBits,
            storageInfo.FieldSizeBits,
            SourceElementBitWidth(index),
            ArrayCount(index),
            checked((int)storageInfo.AdditionalDataSize),
            storageInfo.IsSigned)).ToArray()
    };

    public int RowCount => checked(_header.RecordCount + _header.CopyTableSize / 8);
    public int FieldCount => _header.FieldCount;
    public int RecordSize => _header.RecordSize;
    public int StringTableSize => _strings.Length;
    public int PhysicalHeaderSize => checked(HeaderSize + _header.TotalFieldCount * FieldStructureSize);
    public bool IsDirty => _dirty;
    public bool AllowsStructuralMutation => !Metadata.HasOffsetMap || _configuredColumns is not null;

    public static Wdc1TableData Load(string path, ReadOnlySpan<byte> initialHeader)
    {
        var bytes = File.ReadAllBytes(path);
        if (initialHeader.Length >= 4 && !bytes.AsSpan(0, 4).SequenceEqual(initialHeader[..4]))
            throw new InvalidDataException("The WDC1 file changed while it was being opened.");
        return Parse(bytes);
    }

    private static Wdc1TableData Parse(byte[] bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual("WDC1"u8))
            throw new InvalidDataException("The client table is not a complete WDC1 file.");

        var span = bytes.AsSpan();
        var header = new ParsedHeader(
            PositiveOrZero(U32(span, 4), "record count"),
            Positive(U32(span, 8), "field count"),
            PositiveOrZero(U32(span, 12), "record size"),
            PositiveOrZero(U32(span, 16), "string table size"),
            U32(span, 20), U32(span, 24), U32(span, 28), U32(span, 32), U32(span, 36),
            PositiveOrZero(U32(span, 40), "copy table size"), U16(span, 44), U16(span, 46),
            Positive(U32(span, 48), "total field count"), PositiveOrZero(U32(span, 52), "bitpacked data offset"),
            PositiveOrZero(U32(span, 56), "lookup column count"), PositiveOrZero(U32(span, 60), "offset map offset"),
            PositiveOrZero(U32(span, 64), "ID list size"), PositiveOrZero(U32(span, 68), "field storage info size"),
            PositiveOrZero(U32(span, 72), "common data size"), PositiveOrZero(U32(span, 76), "pallet data size"),
            PositiveOrZero(U32(span, 80), "relationship data size"));

        if (header.CopyTableSize % 8 != 0) throw new InvalidDataException("The WDC1 copy table is not made of 8-byte ID pairs.");
        if (header.FieldStorageInfoSize % StorageInfoSize != 0) throw new InvalidDataException("The WDC1 field-storage block is not aligned to 24-byte entries.");
        var hasImplicitEmptyStorage = header.RecordCount == 0 && header.CopyTableSize == 0 && header.FieldStorageInfoSize == 0;
        if (!hasImplicitEmptyStorage && header.FieldStorageInfoSize / StorageInfoSize != header.TotalFieldCount)
            throw new InvalidDataException($"WDC1 declares {header.TotalFieldCount:N0} fields but {header.FieldStorageInfoSize / StorageInfoSize:N0} field-storage entries.");
        if ((header.Flags & 0x4) != 0 && header.IdListSize != checked(header.RecordCount * 4))
            throw new InvalidDataException("The WDC1 external ID list does not contain one ID per physical record.");

        var position = checked(HeaderSize + header.TotalFieldCount * FieldStructureSize);
        RequireRange(bytes, HeaderSize, checked(header.TotalFieldCount * FieldStructureSize), "field structures");
        var structures = new Wdc1FieldStructure[header.TotalFieldCount];
        for (var index = 0; index < structures.Length; index++)
        {
            var offset = HeaderSize + index * FieldStructureSize;
            structures[index] = new(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(offset, 2)), U16(span, offset + 2));
            if (structures[index].ElementBitWidth is < 0 or > 64)
                throw new InvalidDataException($"WDC1 field {index:N0} declares unsupported {structures[index].ElementBitWidth:N0}-bit elements.");
        }

        byte[] recordData;
        byte[] offsetMap;
        byte[] strings;
        if ((header.Flags & 0x1) != 0)
        {
            if (header.OffsetMapOffset < position || header.OffsetMapOffset > bytes.Length)
                throw new InvalidDataException("The WDC1 offset-map position is outside the file.");
            recordData = Slice(bytes, position, header.OffsetMapOffset - position, "variable record data");
            var range = IdRange(header.MinId, header.MaxId);
            var mapLength = checked(range * 6);
            offsetMap = Slice(bytes, header.OffsetMapOffset, mapLength, "offset map");
            strings = [0];
            position = checked(header.OffsetMapOffset + mapLength);
        }
        else
        {
            var recordBytes = checked(header.RecordCount * header.RecordSize);
            recordData = Slice(bytes, position, recordBytes, "record data");
            position = checked(position + recordBytes);
            strings = Slice(bytes, position, header.StringTableSize, "string table");
            position = checked(position + header.StringTableSize);
            offsetMap = [];
        }

        var idList = Slice(bytes, position, header.IdListSize, "ID list"); position = checked(position + header.IdListSize);
        var copyTable = Slice(bytes, position, header.CopyTableSize, "copy table"); position = checked(position + header.CopyTableSize);
        var storageBytes = Slice(bytes, position, header.FieldStorageInfoSize, "field storage info"); position = checked(position + header.FieldStorageInfoSize);
        var storage = new Wdc1FieldStorageInfo[header.TotalFieldCount];
        for (var index = 0; index < storage.Length; index++)
        {
            if (hasImplicitEmptyStorage)
            {
                storage[index] = new(
                    checked((ushort)(structures[index].Position * 8)),
                    checked((ushort)structures[index].ElementBitWidth),
                    0,
                    Wdc1StorageType.None,
                    0,
                    0,
                    0);
                continue;
            }
            var offset = index * StorageInfoSize;
            var type = (Wdc1StorageType)U32(storageBytes, offset + 8);
            if (!Enum.IsDefined(type)) throw new InvalidDataException($"WDC1 field {index:N0} uses unknown storage type {(uint)type:N0}.");
            storage[index] = new(U16(storageBytes, offset), U16(storageBytes, offset + 2), U32(storageBytes, offset + 4), type,
                U32(storageBytes, offset + 12), U32(storageBytes, offset + 16), U32(storageBytes, offset + 20));
        }

        var additional = new byte[storage.Length][];
        var palletStart = position;
        for (var index = 0; index < storage.Length; index++)
        {
            if (storage[index].StorageType is not (Wdc1StorageType.Pallet or Wdc1StorageType.PalletArray)) { additional[index] = []; continue; }
            additional[index] = Slice(bytes, position, checked((int)storage[index].AdditionalDataSize), $"field {index} pallet data");
            position = checked(position + additional[index].Length);
        }
        if (position - palletStart != header.PalletDataSize) throw new InvalidDataException("The WDC1 pallet blocks do not match PalletDataSize.");

        var commonStart = position;
        for (var index = 0; index < storage.Length; index++)
        {
            if (storage[index].StorageType != Wdc1StorageType.Common) continue;
            additional[index] = Slice(bytes, position, checked((int)storage[index].AdditionalDataSize), $"field {index} common data");
            position = checked(position + additional[index].Length);
        }
        if (position - commonStart != header.CommonDataSize) throw new InvalidDataException("The WDC1 common-data blocks do not match CommonDataSize.");

        uint[] relationshipValues = [];
        if (header.RelationshipDataSize > 0)
        {
            var relationshipBytes = Slice(bytes, position, header.RelationshipDataSize, "relationship data");
            if (relationshipBytes.Length < 12) throw new InvalidDataException("The WDC1 relationship block is shorter than its header.");
            var count = PositiveOrZero(U32(relationshipBytes, 0), "relationship count");
            if (checked(12 + count * 8) != relationshipBytes.Length) throw new InvalidDataException("The WDC1 relationship count does not match its block size.");
            if (count > checked(header.RecordCount + header.CopyTableSize / 8)) throw new InvalidDataException("The WDC1 relationship block has more entries than physical and copy records.");
            relationshipValues = new uint[count];
            for (var index = 0; index < count; index++)
            {
                var offset = 12 + index * 8;
                var foreignId = U32(relationshipBytes, offset);
                var recordIndex = PositiveOrZero(U32(relationshipBytes, offset + 4), "relationship record index");
                var validRecord = (header.Flags & 0x1) != 0
                    ? recordIndex >= header.MinId && recordIndex <= header.MaxId
                    : recordIndex < header.RecordCount;
                if (!validRecord) throw new InvalidDataException($"The WDC1 relationship block contains invalid physical row {recordIndex:N0}.");
                relationshipValues[index] = foreignId;
            }
            position = checked(position + header.RelationshipDataSize);
        }
        if (position != bytes.Length) throw new InvalidDataException($"WDC1 section sizes resolve to {position:N0} bytes, but the file contains {bytes.Length:N0} bytes.");

        return new(bytes.ToArray(), header, structures, storage, additional, recordData, offsetMap, idList, copyTable, strings, relationshipValues);
    }

    public void ConfigureSchema(IReadOnlyList<DbcColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ValidateMappings(columns);
        _configuredColumns = columns.ToArray();
        if (Metadata.HasOffsetMap && _rows is null) _rows = DecodeOffsetRows(columns);
    }

    public ulong GetRaw64(int row, DbcColumn column)
    {
        var current = GetRow(row);
        return column.StorageKind switch
        {
            DbcColumnStorageKind.ExternalId => current.Id,
            DbcColumnStorageKind.Relationship => current.Relationship ?? 0,
            _ => GetRecordValue(current, column)
        };
    }

    public void SetRaw64(int row, DbcColumn column, ulong value)
    {
        ValidateColumnValue(column, value);
        var current = GetRow(row);
        switch (column.StorageKind)
        {
            case DbcColumnStorageKind.ExternalId:
                if (value > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(value), "WDC1 record IDs are unsigned 32-bit values.");
                current.Id = (uint)value;
                break;
            case DbcColumnStorageKind.Relationship:
                if (value > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(value), "WDC1 relationship IDs are unsigned 32-bit values.");
                current.Relationship = (uint)value;
                break;
            default:
                var storageIndex = StorageIndex(column);
                if (column.ArrayIndex < 0 || column.ArrayIndex >= current.Values[storageIndex].Length)
                    throw new InvalidDataException($"Schema field '{column.Name}' addresses array element {column.ArrayIndex:N0}, but WDC1 storage field {storageIndex:N0} has {current.Values[storageIndex].Length:N0} element(s).");
                current.Values[storageIndex][column.ArrayIndex] = value;
                if (column.IsIndex && value <= uint.MaxValue && !Metadata.HasExternalIds) current.Id = (uint)value;
                break;
        }
        _dirty = true;
    }

    public string GetString(ulong offset)
    {
        if (offset > int.MaxValue || offset >= (ulong)_strings.Length) return $"<invalid string offset {offset}>";
        var tail = _strings.AsSpan((int)offset);
        var length = tail.IndexOf((byte)0);
        if (length < 0) length = tail.Length;
        return Encoding.UTF8.GetString(tail[..length]);
    }

    public ulong GetOrAddString(string value)
    {
        value ??= string.Empty;
        var offset = 0;
        while (offset < _strings.Length)
        {
            var tail = _strings.AsSpan(offset);
            var length = tail.IndexOf((byte)0);
            if (length < 0) length = tail.Length;
            if (Encoding.UTF8.GetString(tail[..length]).Equals(value, StringComparison.Ordinal)) return (ulong)offset;
            offset += length + 1;
        }
        var encoded = Encoding.UTF8.GetBytes(value);
        var result = _strings.Length;
        Array.Resize(ref _strings, checked(_strings.Length + encoded.Length + 1));
        encoded.CopyTo(_strings.AsSpan(result));
        _strings[^1] = 0;
        _dirty = true;
        return checked((ulong)result);
    }

    public int AddBlankRows(int count, DbcColumn? idColumn)
    {
        RequireRows();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var first = _rows!.Count;
        var nextId = idColumn is null ? 0u : NextId(idColumn);
        var shape = _rows.Count > 0 ? _rows[0].Values.Select(values => values.Length).ToArray() : StorageShape();
        for (var index = 0; index < count; index++)
        {
            var values = shape.Select(length => new ulong[length]).ToArray();
            var row = new Row(idColumn is null ? 0 : checked(nextId + (uint)index), values, Metadata.HasRelationshipData ? 0u : null);
            _rows.Add(row);
            if (idColumn is not null) SetRaw64(_rows.Count - 1, idColumn, row.Id);
        }
        _dirty = true;
        return first;
    }

    public int CloneRows(int sourceRow, int count, DbcColumn? idColumn)
    {
        RequireRows();
        ArgumentOutOfRangeException.ThrowIfNegative(sourceRow);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sourceRow, _rows!.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var first = _rows.Count;
        var nextId = idColumn is null ? 0u : NextId(idColumn);
        for (var index = 0; index < count; index++)
        {
            var clone = _rows[sourceRow].Clone(idColumn is null ? null : checked(nextId + (uint)index));
            _rows.Add(clone);
            if (idColumn is not null) SetRaw64(_rows.Count - 1, idColumn, clone.Id);
        }
        _dirty = true;
        return first;
    }

    public void DeleteRows(IEnumerable<int> rows)
    {
        RequireRows();
        var targets = rows.Distinct().OrderDescending().ToArray();
        if (targets.Any(row => row < 0 || row >= _rows!.Count)) throw new ArgumentOutOfRangeException(nameof(rows));
        foreach (var row in targets) _rows!.RemoveAt(row);
        if (targets.Length > 0) _dirty = true;
    }

    public uint NextId(DbcColumn idColumn)
    {
        RequireRows();
        uint maximum = 0;
        for (var row = 0; row < _rows!.Count; row++)
        {
            var value = GetRaw64(row, idColumn);
            if (value > uint.MaxValue) throw new InvalidDataException("A WDC1 ID exceeds its unsigned 32-bit storage.");
            maximum = Math.Max(maximum, (uint)value);
        }
        return checked(maximum + 1);
    }

    public void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!_dirty) destination.Write(_originalBytes);
        else destination.Write(EncodeCanonical());
    }

    public byte[] GetPersistedBytes() => _dirty ? EncodeCanonical() : _originalBytes.ToArray();

    public void Commit(byte[] bytes)
    {
        var columns = _configuredColumns;
        var parsed = Parse(bytes);
        if (columns is not null) parsed.ConfigureSchema(columns);
        CopyFrom(parsed);
        _dirty = false;
    }

    public Wdc1TableData Clone()
    {
        var clone = Parse(_originalBytes.ToArray());
        if (_configuredColumns is not null) clone.ConfigureSchema(_configuredColumns);
        if (_dirty)
        {
            clone._rows = _rows?.Select(row => row.Clone()).ToList();
            clone._strings = _strings.ToArray();
            clone._dirty = true;
        }
        return clone;
    }

    public void ReplaceFrom(Wdc1TableData source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.FieldCount != FieldCount || source.Metadata.LayoutHash != Metadata.LayoutHash)
            throw new InvalidDataException("The replacement WDC1 field count or layout hash differs from the open table.");
        var clone = source.Clone();
        CopyFrom(clone);
        _dirty = true;
    }

    public string ComputeContentSha256() => Convert.ToHexString(SHA256.HashData(GetPersistedBytes()));

    private List<Row> DecodeRegularRows()
    {
        var physical = new List<Row>(_header.RecordCount + _header.CopyTableSize / 8);
        var common = BuildCommonLookups();
        for (var index = 0; index < _header.RecordCount; index++)
        {
            var record = _recordData.AsSpan(index * _header.RecordSize, _header.RecordSize);
            var id = Metadata.HasExternalIds ? U32(_idList, index * 4) : ReadInlineId(record);
            var values = new ulong[_storage.Length][];
            for (var field = 0; field < values.Length; field++) values[field] = ReadRegularField(record, field, id, common[field]);
            physical.Add(new(id, values, index < _relationshipValues.Length ? _relationshipValues[index] : null));
        }

        var byId = physical.ToDictionary(row => row.Id);
        for (var offset = 0; offset < _copyTable.Length; offset += 8)
        {
            var newId = U32(_copyTable, offset); var sourceId = U32(_copyTable, offset + 4);
            if (!byId.TryGetValue(sourceId, out var source)) throw new InvalidDataException($"WDC1 copy row {newId:N0} references missing record {sourceId:N0}.");
            var clone = source.Clone(newId);
            var relationshipIndex = checked(_header.RecordCount + offset / 8);
            if (relationshipIndex < _relationshipValues.Length) clone.Relationship = _relationshipValues[relationshipIndex];
            if (!Metadata.HasExternalIds && _header.IdIndex < clone.Values.Length && clone.Values[_header.IdIndex].Length > 0)
                clone.Values[_header.IdIndex][0] = newId;
            if (!byId.TryAdd(newId, clone)) throw new InvalidDataException($"WDC1 copy row duplicates ID {newId:N0}.");
            physical.Add(clone);
        }
        return physical;
    }

    private List<Row> DecodeOffsetRows(IReadOnlyList<DbcColumn> columns)
    {
        if (!Metadata.HasExternalIds) throw new InvalidDataException("Variable-record WDC1 decoding requires the file's external ID list.");
        var groups = RecordGroups(columns);
        var common = BuildCommonLookups();
        var result = new List<Row>(_header.RecordCount);
        var stringPool = new List<byte> { 0 };
        var strings = new Dictionary<string, ulong>(StringComparer.Ordinal) { [string.Empty] = 0 };
        var dataStart = checked(HeaderSize + _header.TotalFieldCount * FieldStructureSize);

        for (var rowIndex = 0; rowIndex < _header.RecordCount; rowIndex++)
        {
            var id = U32(_idList, rowIndex * 4);
            if (id < _header.MinId || id > _header.MaxId) throw new InvalidDataException($"WDC1 ID {id:N0} is outside its offset-map range.");
            var mapIndex = checked((int)(id - _header.MinId));
            var absoluteOffset = PositiveOrZero(U32(_offsetMap, mapIndex * 6), "record offset");
            var size = U16(_offsetMap, mapIndex * 6 + 4);
            if (absoluteOffset == 0 || size == 0) throw new InvalidDataException($"WDC1 offset map has no record body for listed ID {id:N0}.");
            var relative = checked(absoluteOffset - dataStart);
            var record = Slice(_recordData, relative, size, $"variable record {id}").AsSpan();
            var values = new ulong[_storage.Length][];
            var bitOffset = 0;
            for (var field = 0; field < _storage.Length; field++)
            {
                var shape = groups.GetValueOrDefault(field);
                var arrayCount = shape?.Count ?? ArrayCount(field);
                if (shape?.Any(column => column.Type == DbcValueType.StringOffset) == true)
                {
                    values[field] = new ulong[arrayCount];
                    if ((bitOffset & 7) != 0) throw new InvalidDataException($"Variable WDC1 string field {field:N0} is not byte-aligned in record {id:N0}.");
                    for (var element = 0; element < arrayCount; element++)
                    {
                        var byteOffset = bitOffset / 8;
                        if (byteOffset >= record.Length) throw new InvalidDataException($"Variable WDC1 string field {field:N0} exceeds record {id:N0}.");
                        var tail = record[byteOffset..]; var length = tail.IndexOf((byte)0);
                        if (length < 0) throw new InvalidDataException($"Variable WDC1 string field {field:N0} in record {id:N0} is unterminated.");
                        var text = Encoding.UTF8.GetString(tail[..length]);
                        if (!strings.TryGetValue(text, out var stringOffset))
                        {
                            stringOffset = checked((ulong)stringPool.Count); var encoded = Encoding.UTF8.GetBytes(text);
                            stringPool.AddRange(encoded); stringPool.Add(0); strings[text] = stringOffset;
                        }
                        values[field][element] = stringOffset;
                        bitOffset = checked(bitOffset + (length + 1) * 8);
                    }
                    continue;
                }

                values[field] = ReadVariableField(record, field, id, common[field], bitOffset, arrayCount);
                bitOffset = checked(bitOffset + _storage[field].FieldSizeBits);
            }
            if ((bitOffset + 7) / 8 > record.Length) throw new InvalidDataException($"Variable WDC1 schema consumed beyond record {id:N0}.");
            result.Add(new(id, values, rowIndex < _relationshipValues.Length ? _relationshipValues[rowIndex] : null));
        }
        _strings = stringPool.ToArray();
        return result;
    }

    private uint ReadInlineId(ReadOnlySpan<byte> record)
    {
        if (_header.IdIndex >= _storage.Length) throw new InvalidDataException($"WDC1 inline ID index {_header.IdIndex:N0} exceeds its field count.");
        var info = _storage[_header.IdIndex];
        if (info.StorageType == Wdc1StorageType.Common) throw new InvalidDataException("WDC1 stores its inline record ID in common data, which is not independently addressable.");
        var value = ReadStoredScalar(record, _header.IdIndex, 0, 1, null, 0);
        if (value > uint.MaxValue) throw new InvalidDataException("A WDC1 inline record ID exceeds 32 bits.");
        return (uint)value;
    }

    private ulong[] ReadRegularField(ReadOnlySpan<byte> record, int field, uint id, IReadOnlyDictionary<uint, ulong>? common)
    {
        var count = ArrayCount(field);
        var result = new ulong[count];
        for (var element = 0; element < count; element++) result[element] = ReadStoredScalar(record, field, element, count, common, id);
        return result;
    }

    private ulong[] ReadVariableField(ReadOnlySpan<byte> record, int field, uint id, IReadOnlyDictionary<uint, ulong>? common, int bitOffset, int arrayCount)
    {
        var result = new ulong[arrayCount];
        var info = _storage[field];
        if (info.StorageType == Wdc1StorageType.Common)
        {
            result[0] = common is not null && common.TryGetValue(id, out var value) ? value : info.DefaultValue;
            return result;
        }
        if (info.StorageType is Wdc1StorageType.Pallet or Wdc1StorageType.PalletArray)
        {
            var index = ReadBits(record, bitOffset, info.FieldSizeBits);
            ReadPallet(field, index, result);
            return result;
        }
        var elementBits = ElementBitWidth(field, arrayCount);
        for (var element = 0; element < arrayCount; element++)
            result[element] = NormalizeSigned(ReadBits(record, checked(bitOffset + element * elementBits), elementBits), elementBits, info.IsSigned);
        return result;
    }

    private ulong ReadStoredScalar(ReadOnlySpan<byte> record, int field, int element, int arrayCount, IReadOnlyDictionary<uint, ulong>? common, uint id)
    {
        var info = _storage[field];
        if (info.StorageType == Wdc1StorageType.Common)
            return common is not null && common.TryGetValue(id, out var commonValue) ? commonValue : info.DefaultValue;
        if (info.StorageType is Wdc1StorageType.Pallet or Wdc1StorageType.PalletArray)
        {
            var index = ReadBits(record, info.FieldOffsetBits, info.FieldSizeBits);
            var values = new ulong[Math.Max(arrayCount, info.PalletArrayCount)];
            ReadPallet(field, index, values);
            return values[element];
        }
        var elementBits = ElementBitWidth(field, arrayCount);
        var value = ReadBits(record, checked(info.FieldOffsetBits + element * elementBits), elementBits);
        return NormalizeSigned(value, elementBits, info.IsSigned);
    }

    private void ReadPallet(int field, ulong index, Span<ulong> destination)
    {
        var info = _storage[field];
        var count = info.StorageType == Wdc1StorageType.PalletArray ? info.PalletArrayCount : 1;
        var baseOffset = checked((int)index * count * 4);
        if (baseOffset < 0 || baseOffset + count * 4 > _additionalData[field].Length)
            throw new InvalidDataException($"WDC1 field {field:N0} pallet index {index:N0} is outside its data block.");
        for (var element = 0; element < count && element < destination.Length; element++)
            destination[element] = U32(_additionalData[field], baseOffset + element * 4);
    }

    private IReadOnlyDictionary<uint, ulong>?[] BuildCommonLookups()
    {
        var result = new IReadOnlyDictionary<uint, ulong>?[_storage.Length];
        for (var field = 0; field < _storage.Length; field++)
        {
            if (_storage[field].StorageType != Wdc1StorageType.Common) continue;
            var bytes = _additionalData[field];
            if (bytes.Length % 8 != 0) throw new InvalidDataException($"WDC1 field {field:N0} common data is not made of 8-byte ID/value pairs.");
            var values = new Dictionary<uint, ulong>();
            for (var offset = 0; offset < bytes.Length; offset += 8)
                if (!values.TryAdd(U32(bytes, offset), U32(bytes, offset + 4))) throw new InvalidDataException($"WDC1 field {field:N0} common data repeats a record ID.");
            result[field] = values;
        }
        return result;
    }

    private byte[] EncodeCanonical()
    {
        RequireRows();
        var groups = _configuredColumns is null ? new Dictionary<int, List<DbcColumn>>() : RecordGroups(_configuredColumns);
        var shapes = StorageShape(groups);
        var widths = new int[_storage.Length];
        var offsets = new int[_storage.Length];
        var bitOffset = 0;
        for (var field = 0; field < widths.Length; field++)
        {
            var declared = groups.GetValueOrDefault(field)?.Select(column => column.EffectiveBitWidth).DefaultIfEmpty(0).Max() ?? 0;
            var source = Math.Max(SourceElementBitWidth(field), declared);
            widths[field] = CanonicalWidth(source);
            offsets[field] = bitOffset;
            bitOffset = checked(bitOffset + widths[field] * shapes[field]);
        }
        var recordSize = checked((bitOffset + 7) / 8);
        var recordData = new byte[checked(_rows!.Count * recordSize)];
        for (var rowIndex = 0; rowIndex < _rows.Count; rowIndex++)
        {
            var record = recordData.AsSpan(rowIndex * recordSize, recordSize);
            for (var field = 0; field < _storage.Length; field++)
            {
                var values = _rows[rowIndex].Values[field];
                for (var element = 0; element < shapes[field]; element++)
                    WriteBits(record, checked(offsets[field] + element * widths[field]), widths[field], element < values.Length ? values[element] : 0);
            }
        }

        var externalIds = Metadata.HasExternalIds || _configuredColumns?.Any(column => column.StorageKind == DbcColumnStorageKind.ExternalId) == true;
        var relationshipRows = _rows.Select((row, index) => (row, index)).Where(value => value.row.Relationship.HasValue).ToArray();
        var idListSize = externalIds ? checked(_rows.Count * 4) : 0;
        var relationshipSize = relationshipRows.Length == 0 ? 0 : checked(12 + relationshipRows.Length * 8);
        var flags = (ushort)((_header.Flags & ~0x1) | 0x10);
        if (externalIds) flags |= 0x4; else flags = (ushort)(flags & ~0x4);
        var minId = _rows.Count == 0 ? 0u : _rows.Min(row => row.Id);
        var maxId = _rows.Count == 0 ? 0u : _rows.Max(row => row.Id);
        var fieldStorageSize = checked(_storage.Length * StorageInfoSize);
        var totalSize = checked(HeaderSize + _storage.Length * FieldStructureSize + recordData.Length + _strings.Length + idListSize + fieldStorageSize + relationshipSize);
        var output = new byte[totalSize];
        "WDC1"u8.CopyTo(output);
        W32(output, 4, checked((uint)_rows.Count)); W32(output, 8, checked((uint)_storage.Length)); W32(output, 12, checked((uint)recordSize)); W32(output, 16, checked((uint)_strings.Length));
        W32(output, 20, _header.TableHash); W32(output, 24, _header.LayoutHash); W32(output, 28, minId); W32(output, 32, maxId); W32(output, 36, _header.Locale);
        W32(output, 40, 0); W16(output, 44, flags); W16(output, 46, _header.IdIndex); W32(output, 48, checked((uint)_storage.Length));
        W32(output, 52, checked((uint)recordSize)); W32(output, 56, relationshipRows.Length == 0 ? 0u : 1u); W32(output, 60, 0); W32(output, 64, checked((uint)idListSize));
        W32(output, 68, checked((uint)fieldStorageSize)); W32(output, 72, 0); W32(output, 76, 0); W32(output, 80, checked((uint)relationshipSize));

        var position = HeaderSize;
        for (var field = 0; field < _storage.Length; field++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(position, 2), checked((short)(32 - widths[field])));
            W16(output, position + 2, checked((ushort)(offsets[field] / 8)));
            position += FieldStructureSize;
        }
        recordData.CopyTo(output, position); position += recordData.Length;
        _strings.CopyTo(output, position); position += _strings.Length;
        if (externalIds)
        {
            foreach (var row in _rows) { W32(output, position, row.Id); position += 4; }
        }
        for (var field = 0; field < _storage.Length; field++)
        {
            W16(output, position, checked((ushort)offsets[field])); W16(output, position + 2, checked((ushort)(widths[field] * shapes[field])));
            W32(output, position + 4, 0); W32(output, position + 8, 0); W32(output, position + 12, 0); W32(output, position + 16, 0); W32(output, position + 20, 0);
            position += StorageInfoSize;
        }
        if (relationshipRows.Length > 0)
        {
            W32(output, position, checked((uint)relationshipRows.Length));
            W32(output, position + 4, relationshipRows.Min(value => value.row.Relationship!.Value));
            W32(output, position + 8, relationshipRows.Max(value => value.row.Relationship!.Value));
            position += 12;
            foreach (var value in relationshipRows)
            {
                W32(output, position, value.row.Relationship!.Value); W32(output, position + 4, checked((uint)value.index)); position += 8;
            }
        }
        if (position != output.Length) throw new InvalidDataException("Internal WDC1 encoder length accounting failed.");
        return output;
    }

    private Dictionary<int, List<DbcColumn>> RecordGroups(IReadOnlyList<DbcColumn> columns) => columns
        .Where(column => column.StorageKind == DbcColumnStorageKind.Record)
        .GroupBy(StorageIndex)
        .ToDictionary(group => group.Key, group => group.OrderBy(column => column.ArrayIndex).ToList());

    private int[] StorageShape(Dictionary<int, List<DbcColumn>>? groups = null)
    {
        var shape = new int[_storage.Length];
        for (var field = 0; field < shape.Length; field++)
        {
            var configured = groups?.GetValueOrDefault(field);
            shape[field] = configured is { Count: > 0 } ? configured.Max(column => column.ArrayIndex) + 1 : ArrayCount(field);
        }
        return shape;
    }

    private int ArrayCount(int field)
    {
        var info = _storage[field];
        if (info.StorageType == Wdc1StorageType.PalletArray)
        {
            if (info.PalletArrayCount <= 0) throw new InvalidDataException($"WDC1 field {field:N0} has an empty pallet-array cardinality.");
            return info.PalletArrayCount;
        }
        var elementBits = SourceElementBitWidth(field);
        return info.StorageType == Wdc1StorageType.None && info.FieldSizeBits >= elementBits && info.FieldSizeBits % elementBits == 0
            ? Math.Max(1, info.FieldSizeBits / elementBits)
            : 1;
    }

    private int ElementBitWidth(int field, int arrayCount)
    {
        var info = _storage[field];
        if (arrayCount > 1 && info.StorageType == Wdc1StorageType.None && info.FieldSizeBits % arrayCount == 0) return info.FieldSizeBits / arrayCount;
        return info.StorageType == Wdc1StorageType.None ? SourceElementBitWidth(field) : info.FieldSizeBits;
    }

    private int SourceElementBitWidth(int field)
    {
        var structure = _fieldStructures[field].ElementBitWidth;
        if (structure > 0) return structure;
        var info = _storage[field];
        if (info.BitpackingSizeBits is > 0 and <= 64) return checked((int)info.BitpackingSizeBits);
        if (info.FieldSizeBits is > 0 and <= 64) return info.FieldSizeBits;
        return 32;
    }

    private void ValidateMappings(IReadOnlyList<DbcColumn> columns)
    {
        var recordColumns = columns.Where(column => column.StorageKind == DbcColumnStorageKind.Record).ToArray();
        foreach (var column in recordColumns)
        {
            var storageIndex = StorageIndex(column);
            if (storageIndex < 0 || storageIndex >= _storage.Length) throw new InvalidDataException($"Schema field '{column.Name}' maps to unavailable WDC1 storage field {storageIndex:N0}.");
            if (column.ArrayIndex < 0) throw new InvalidDataException($"Schema field '{column.Name}' has a negative WDC1 array index.");
        }
        var occupiedStorage = recordColumns.Select(StorageIndex).Distinct().Count();
        var relationPlaceholders = columns.Where(column => column.StorageKind == DbcColumnStorageKind.Relationship && column.StorageIndex >= 0).Select(column => column.StorageIndex).Distinct().Count();
        if (occupiedStorage + relationPlaceholders != _storage.Length)
            throw new InvalidDataException($"The selected schema maps {occupiedStorage:N0} record field(s) and {relationPlaceholders:N0} relationship placeholder(s), but WDC1 declares {_storage.Length:N0} storage fields.");
        if (columns.Any(column => column.StorageKind == DbcColumnStorageKind.ExternalId) && !Metadata.HasExternalIds)
            throw new InvalidDataException("The selected schema declares a non-inline ID, but WDC1 has no external ID list.");
        if (columns.Any(column => column.StorageKind == DbcColumnStorageKind.Relationship) && !Metadata.HasRelationshipData)
            throw new InvalidDataException("The selected schema declares relationship data, but WDC1 has no relationship block.");
    }

    private static void ValidateColumnValue(DbcColumn column, ulong value)
    {
        var bits = column.EffectiveBitWidth;
        if (bits is <= 0 or > 64) throw new NotSupportedException($"Field '{column.Name}' uses unsupported {bits:N0}-bit storage.");
        if (column.Type is DbcValueType.Int32 or DbcValueType.Int64)
        {
            if (bits == 64) return;
            var mask = (1UL << bits) - 1;
            if ((value & ~mask) != 0 && (value | mask) != ulong.MaxValue)
                throw new OverflowException($"Signed value for '{column.Name}' does not fit in {bits:N0} bits.");
            return;
        }
        if (bits < 64 && value >= (1UL << bits)) throw new OverflowException($"Value {value:N0} for '{column.Name}' does not fit in {bits:N0} bits.");
    }

    private Row GetRow(int row)
    {
        RequireRows();
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows!.Count);
        return _rows[row];
    }

    private ulong GetRecordValue(Row row, DbcColumn column)
    {
        var storageIndex = StorageIndex(column);
        if (storageIndex < 0 || storageIndex >= row.Values.Length) throw new InvalidDataException($"Schema field '{column.Name}' maps outside the WDC1 record.");
        if (column.ArrayIndex < 0 || column.ArrayIndex >= row.Values[storageIndex].Length)
            throw new InvalidDataException($"Schema field '{column.Name}' addresses unavailable array element {column.ArrayIndex:N0}.");
        return row.Values[storageIndex][column.ArrayIndex];
    }

    private static int StorageIndex(DbcColumn column) => column.StorageIndex >= 0 ? column.StorageIndex : column.Index;

    private void RequireRows()
    {
        if (_rows is null) throw new InvalidOperationException("This offset-map WDC1 must be paired with its exact DBD layout before records can be read or changed.");
    }

    private void CopyFrom(Wdc1TableData source)
    {
        _originalBytes = source._originalBytes.ToArray(); _header = source._header; _fieldStructures = source._fieldStructures.ToArray();
        _storage = source._storage.ToArray(); _additionalData = source._additionalData.Select(value => value.ToArray()).ToArray();
        _recordData = source._recordData.ToArray(); _offsetMap = source._offsetMap.ToArray(); _idList = source._idList.ToArray();
        _copyTable = source._copyTable.ToArray(); _strings = source._strings.ToArray(); _relationshipValues = source._relationshipValues.ToArray();
        _rows = source._rows?.Select(row => row.Clone()).ToList(); _configuredColumns = source._configuredColumns?.ToArray(); _dirty = source._dirty;
    }

    private static int CanonicalWidth(int bits) => bits switch
    {
        <= 8 => 8,
        <= 16 => 16,
        <= 32 => 32,
        <= 64 => 64,
        _ => throw new NotSupportedException($"WDC1 scalar widths above 64 bits are not supported ({bits:N0} bits).")
    };

    private static ulong NormalizeSigned(ulong value, int bits, bool signed)
    {
        if (!signed || bits <= 0 || bits >= 64 || (value & (1UL << (bits - 1))) == 0) return value;
        return value | (ulong.MaxValue << bits);
    }

    private static ulong ReadBits(ReadOnlySpan<byte> bytes, int bitOffset, int bitCount)
    {
        if (bitOffset < 0 || bitCount is <= 0 or > 64 || checked(bitOffset + bitCount) > checked(bytes.Length * 8))
            throw new InvalidDataException($"Bit range {bitOffset:N0}+{bitCount:N0} exceeds a {bytes.Length:N0}-byte WDC1 record.");
        UInt128 value = 0;
        var firstByte = bitOffset / 8;
        var shift = bitOffset & 7;
        var byteCount = (shift + bitCount + 7) / 8;
        for (var index = 0; index < byteCount; index++) value |= (UInt128)bytes[firstByte + index] << (index * 8);
        value >>= shift;
        var mask = bitCount == 64 ? (UInt128)ulong.MaxValue : ((UInt128)1 << bitCount) - 1;
        return (ulong)(value & mask);
    }

    private static void WriteBits(Span<byte> bytes, int bitOffset, int bitCount, ulong value)
    {
        if (bitOffset < 0 || bitCount is <= 0 or > 64 || checked(bitOffset + bitCount) > checked(bytes.Length * 8))
            throw new InvalidDataException("A canonical WDC1 field exceeds its output record.");
        for (var bit = 0; bit < bitCount; bit++)
        {
            var destination = bitOffset + bit;
            var mask = (byte)(1 << (destination & 7));
            if (((value >> bit) & 1) != 0) bytes[destination / 8] |= mask;
            else bytes[destination / 8] &= (byte)~mask;
        }
    }

    private static int IdRange(uint min, uint max)
    {
        if (max < min) throw new InvalidDataException($"Invalid WDC1 ID range {min:N0}..{max:N0}.");
        var range = checked((ulong)max - min + 1);
        if (range > int.MaxValue) throw new InvalidDataException("The WDC1 offset-map ID range is too large to address.");
        return (int)range;
    }

    private static int Positive(uint value, string name) => value is > 0 and <= int.MaxValue ? (int)value : throw new InvalidDataException($"Invalid WDC1 {name}: {value:N0}.");
    private static int PositiveOrZero(uint value, string name) => value <= int.MaxValue ? (int)value : throw new InvalidDataException($"WDC1 {name} exceeds the supported address space: {value:N0}.");
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) { RequireRange(bytes, offset, 4, "uint32"); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4)); }
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) { RequireRange(bytes, offset, 2, "uint16"); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)); }
    private static void W32(Span<byte> bytes, int offset, uint value) { RequireRange(bytes, offset, 4, "uint32 output"); BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, 4), value); }
    private static void W16(Span<byte> bytes, int offset, ushort value) { RequireRange(bytes, offset, 2, "uint16 output"); BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset, 2), value); }
    private static byte[] Slice(byte[] bytes, int offset, int length, string section) { RequireRange(bytes, offset, length, section); return bytes.AsSpan(offset, length).ToArray(); }
    private static void RequireRange(ReadOnlySpan<byte> bytes, int offset, int length, string section)
    {
        if (offset < 0 || length < 0 || (long)offset + length > bytes.Length) throw new InvalidDataException($"The WDC1 {section} exceeds the file bounds.");
    }
}
