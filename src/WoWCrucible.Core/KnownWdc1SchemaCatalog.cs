namespace WoWCrucible.Core;

internal static class KnownWdc1SchemaCatalog
{
    public static bool TryResolve(string tableName, int build, Wdc1Metadata metadata, out DbcSchemaResolution resolution)
    {
        if (tableName.Equals("WorldSafeLocs", StringComparison.OrdinalIgnoreCase) &&
            build is 0 or 26972 &&
            metadata.LayoutHash == 0x605EA8A6 &&
            metadata.TotalFieldCount == 4 &&
            metadata.HasExternalIds &&
            !metadata.HasRelationshipData)
        {
            // LegionCore 7.3.5 DB2Metadata and DB2LoadInfo define this exact layout.
            // Keep a deterministic fallback for older definition corpora that do not
            // yet map the file's 605EA8A6 layout hash.
            DbcColumn[] columns =
            [
                new(0, 0, 4, "ID", DbcValueType.Int32, true, -1, 0, 32, DbcColumnStorageKind.ExternalId),
                new(1, 4, 4, "Name", DbcValueType.StringOffset, false, 0, 0, 32),
                new(2, 8, 4, "Loc[0]", DbcValueType.Float32, false, 1, 0, 32),
                new(3, 12, 4, "Loc[1]", DbcValueType.Float32, false, 1, 1, 32),
                new(4, 16, 4, "Loc[2]", DbcValueType.Float32, false, 1, 2, 32),
                new(5, 20, 4, "LocO", DbcValueType.Float32, false, 2, 0, 32),
                new(6, 24, 2, "MapID", DbcValueType.UInt32, false, 3, 0, 16)
            ];
            resolution = new(columns, DbcSchemaMatchKind.NamedMatch, metadata.TotalFieldCount, DbcRecordKeyStrategy.Physical(0));
            return true;
        }

        resolution = null!;
        return false;
    }
}
