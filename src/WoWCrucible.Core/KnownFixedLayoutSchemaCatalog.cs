namespace WoWCrucible.Core;

/// <summary>
/// Documents narrow build-history gaps where the real client table and the
/// immediately preceding WoWDBDefs layout are proven to have the same binary
/// shape. Entries are table-, build-, field-count-, and record-size-specific;
/// this is never a general "nearest layout" fallback.
/// </summary>
internal static class KnownFixedLayoutSchemaCatalog
{
    private sealed record Entry(string Table, int Build, int FieldCount, int RecordSize, DbdClientBuild ProvenBuild);

    private static readonly Entry[] Entries =
    [
        // WoWDBDefs ends these layouts at 5.4.8.18273, while the real SkyFire
        // 5.4.8.18414 DBC/DB2 corpus retains the same exact binary layouts.
        new("CreatureDisplayInfoExtra", 18414, 21, 84, new(5, 4, 8, 18273)),
        new("PvpDifficulty", 18414, 6, 24, new(5, 4, 8, 18273)),
        new("SoundAmbience", 18414, 3, 12, new(5, 4, 8, 18273)),
        new("BattlePetAbility", 18414, 8, 32, new(5, 4, 8, 18273)),
        new("SpellMissileMotion", 18414, 5, 20, new(5, 4, 8, 18273)),
        new("AnimKitSegment", 18414, 16, 64, new(5, 4, 8, 18273)),
        new("DurabilityCosts", 18414, 30, 120, new(5, 4, 8, 18273)),
        new("ItemSet", 18414, 37, 148, new(5, 4, 8, 18273)),
        new("WorldSafeLocs", 18414, 7, 28, new(5, 4, 8, 18273))
    ];

    public static bool TryResolve(
        DbdDefinition definition,
        int build,
        int fieldCount,
        int recordSize,
        out DbdLayout layout,
        out int provenBuild)
    {
        layout = null!;
        provenBuild = 0;
        var entry = Entries.SingleOrDefault(candidate =>
            candidate.Table.Equals(definition.TableName, StringComparison.OrdinalIgnoreCase) &&
            candidate.Build == build &&
            candidate.FieldCount == fieldCount &&
            candidate.RecordSize == recordSize);
        if (entry is null) return false;

        var candidates = definition.Layouts.Where(candidate => candidate.Builds.Any(range => range.Contains(entry.ProvenBuild))).ToArray();
        if (candidates.Length != 1) return false;

        var columns = DbdSchemaService.ResolveColumns(definition, candidates[0], build);
        var declaredFields = columns.Count(column => !DbcSchemaCatalog.IsPadding(column.Name));
        var resolvedSize = columns.Count == 0 ? 0 : columns.Max(column => checked(column.Offset + column.Size));
        if (declaredFields != fieldCount || resolvedSize != recordSize) return false;

        layout = candidates[0];
        provenBuild = entry.ProvenBuild.Build;
        return true;
    }
}
