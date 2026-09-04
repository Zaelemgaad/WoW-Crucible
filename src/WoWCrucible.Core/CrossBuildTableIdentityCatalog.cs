namespace WoWCrucible.Core;

internal sealed record CrossBuildTableIdentity(
    DbcRecordKeyStrategy Strategy,
    bool ReferenceAddressable,
    bool RetainHostOnSameKeyConflict = false,
    string? Finding = null);

/// <summary>
/// Records build-specific table identities that cannot be inferred from a DBD
/// ID annotation alone. These are semantic contracts, not schema guesses.
/// </summary>
internal static class CrossBuildTableIdentityCatalog
{
    public static CrossBuildTableIdentity Resolve(
        string profileId,
        string canonicalTable,
        IReadOnlyList<DbcColumn> columns,
        DbcRecordKeyStrategy schemaStrategy)
    {
        if (canonicalTable.Equals("ITEMCLASS", StringComparison.OrdinalIgnoreCase) &&
            profileId is "mop-18414" or "legion-26972")
        {
            var classId = RequiredColumn(columns, "ClassID", canonicalTable);
            return new(DbcRecordKeyStrategy.Physical(classId.Index), true, true,
                "Uses ClassID as the cross-build identity; Legion's external DB2 row ID is storage metadata, not the item-class value used by game data.");
        }

        if (canonicalTable is "CHRCLASSES" or "CHRRACES" &&
            profileId is "mop-18414" or "legion-26972")
        {
            if (schemaStrategy.Kind != DbcRecordKeyKind.PhysicalColumn)
                throw new InvalidDataException($"{canonicalTable} fixed-identity policy requires a physical schema key.");
            return new(schemaStrategy, true, true,
                $"{canonicalTable} IDs are fixed gameplay identities. Existing MoP rows remain authoritative when Legion differs; only previously unused IDs are appended.");
        }

        if (canonicalTable.Equals("JOURNALTIERXINSTANCE", StringComparison.OrdinalIgnoreCase) &&
            profileId.Equals("mop-18414", StringComparison.OrdinalIgnoreCase))
        {
            _ = RequiredColumn(columns, "JournalTierID", canonicalTable);
            _ = RequiredColumn(columns, "JournalInstanceID", canonicalTable);
            return new(DbcRecordKeyStrategy.Virtual(), false, false,
                "MoP stores JournalTierXInstance as an unkeyed relationship set. Crucible preserves host order, reuses equal tier/instance pairs, and appends new pairs; ledger targets are output row indexes, not game IDs.");
        }

        return new(schemaStrategy, schemaStrategy.Kind == DbcRecordKeyKind.PhysicalColumn);
    }

    private static DbcColumn RequiredColumn(IReadOnlyList<DbcColumn> columns, string name, string table)
    {
        var matches = columns.Where(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"{table} identity contract requires exactly one {name} field; found {matches.Length:N0}.");
    }
}
