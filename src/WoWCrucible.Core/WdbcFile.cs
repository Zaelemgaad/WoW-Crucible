using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WoWCrucible.Core;

public sealed class WdbcFile
{
    public const int HeaderSize = 20;
    private byte[] _records;
    private byte[] _strings;
    private Dictionary<string, uint>? _stringOffsets;

    private WdbcFile(string sourcePath, int rowCount, int fieldCount, int recordSize, byte[] records, byte[] strings)
    {
        SourcePath = sourcePath;
        RowCount = rowCount;
        FieldCount = fieldCount;
        RecordSize = recordSize;
        _records = records;
        _strings = strings;
    }

    public string SourcePath { get; }
    public int RowCount { get; private set; }
    public int FieldCount { get; }
    public int RecordSize { get; }
    public int StringTableSize => _strings.Length;
    public bool IsDirty { get; private set; }

    public static WdbcFile Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("WDBC"u8))
            throw new InvalidDataException("This file is not a 3.3.5a WDBC file.");

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

    public uint GetRaw(int row, DbcColumn column)
    {
        ValidateCell(row, column);
        var cell = _records.AsSpan(row * RecordSize + column.Offset, column.Size);
        return column.Size == 1 ? cell[0] : BinaryPrimitives.ReadUInt32LittleEndian(cell);
    }

    public void SetRaw(int row, DbcColumn column, uint raw)
    {
        ValidateCell(row, column);
        if (column.Size == 1 && raw > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(raw), "The value does not fit in this byte field.");
        var cell = _records.AsSpan(row * RecordSize + column.Offset, column.Size);
        if (column.Size == 1) cell[0] = (byte)raw;
        else BinaryPrimitives.WriteUInt32LittleEndian(cell, raw);
        IsDirty = true;
    }

    public object GetDisplayValue(int row, DbcColumn column) => column.Type switch
    {
        DbcValueType.Int32 => unchecked((int)GetRaw(row, column)),
        DbcValueType.UInt32 or DbcValueType.Byte => GetRaw(row, column),
        DbcValueType.Float32 => BitConverter.UInt32BitsToSingle(GetRaw(row, column)),
        DbcValueType.StringOffset => GetString(GetRaw(row, column)),
        _ => GetRaw(row, column)
    };

    public void SetDisplayValue(int row, DbcColumn column, object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        uint raw = column.Type switch
        {
            DbcValueType.Int32 => unchecked((uint)int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
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

    public int AddBlankRows(int count, DbcColumn? idColumn = null)
    {
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
            Span<byte> header = stackalloc byte[HeaderSize];
            "WDBC"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..8], RowCount);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..12], FieldCount);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..16], RecordSize);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..20], _strings.Length);
            stream.Write(header);
            stream.Write(_records.AsSpan(0, checked(RowCount * RecordSize)));
            stream.Write(_strings);
            stream.Flush(true);
        }
        File.Move(temp, fullPath, true);
        IsDirty = false;
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

    private void ValidateCell(int row, DbcColumn column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(column.Index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column.Index, FieldCount);
        if (column.Offset < 0 || column.Offset + column.Size > RecordSize)
            throw new InvalidDataException($"Schema field '{column.Name}' exceeds the {RecordSize}-byte record.");
    }
}
