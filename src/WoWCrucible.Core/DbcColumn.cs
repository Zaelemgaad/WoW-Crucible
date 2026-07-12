namespace WoWCrucible.Core;

public enum DbcValueType
{
    Int32,
    UInt32,
    Byte,
    Float32,
    StringOffset,
    Raw32
}

public sealed record DbcColumn(int Index, int Offset, int Size, string Name, DbcValueType Type, bool IsIndex = false);
