namespace WoWCrucible.Core;

public enum DbcValueType
{
    Int32,
    UInt32,
    Int64,
    UInt64,
    Byte,
    Float32,
    StringOffset,
    Raw32
}

public enum DbcColumnStorageKind
{
    Record,
    ExternalId,
    Relationship
}

public sealed record DbcColumn(
    int Index,
    int Offset,
    int Size,
    string Name,
    DbcValueType Type,
    bool IsIndex = false,
    int StorageIndex = -1,
    int ArrayIndex = 0,
    int BitWidth = 0,
    DbcColumnStorageKind StorageKind = DbcColumnStorageKind.Record)
{
    public int EffectiveBitWidth => BitWidth > 0 ? BitWidth : checked(Size * 8);
}
