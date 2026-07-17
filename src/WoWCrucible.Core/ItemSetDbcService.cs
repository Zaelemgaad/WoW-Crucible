namespace WoWCrucible.Core;

public sealed record ItemSetEffect(int Slot, uint RequiredItems, uint SpellId, string? SpellName);
public sealed record ItemSetDefinition(uint Id, string Name, IReadOnlyList<uint> ItemIds, IReadOnlyList<ItemSetEffect> Effects, uint RequiredSkill, uint RequiredSkillRank);
public sealed record ItemSetCloneResult(uint SourceSetId, uint NewSetId, string Name, IReadOnlyDictionary<uint, uint> ItemIdMap, string OutputPath);

public static class ItemSetDbcService
{
    public static ItemSetDefinition Inspect(string itemSetPath, string schemaPath, uint setId, string? spellPath = null)
    {
        var catalog = DbcSchemaCatalog.Load(schemaPath); var file = WdbcFile.Load(itemSetPath); var schema = catalog.ResolveColumns("ItemSet", file.FieldCount);
        RequireNamed(schema, "ItemSet"); var row = FindRow(file, schema, setId); var columns = schema.Columns;
        var name = Convert.ToString(file.GetDisplayValue(row, Column(columns, "Name_Lang[enUS]"))) ?? string.Empty;
        var items = Enumerable.Range(0, 17).Select(index => file.GetRaw(row, Column(columns, $"ItemID[{index}]"))).Where(id => id != 0).ToArray();
        var spellNames = spellPath is null ? new Dictionary<uint, string>() : LoadSpellNames(spellPath, catalog);
        var effects = Enumerable.Range(0, 8).Select(index =>
        {
            var spellId = file.GetRaw(row, Column(columns, $"SetSpellID[{index}]")); var threshold = file.GetRaw(row, Column(columns, $"SetThreshold[{index}]"));
            return new ItemSetEffect(index + 1, threshold, spellId, spellNames.GetValueOrDefault(spellId));
        }).Where(effect => effect.SpellId != 0 || effect.RequiredItems != 0).ToArray();
        return new(setId, name, items, effects, file.GetRaw(row, Column(columns, "RequiredSkill")), file.GetRaw(row, Column(columns, "RequiredSkillRank")));
    }

    public static ItemSetCloneResult Clone(string itemSetPath, string schemaPath, string outputPath, uint sourceSetId, uint newSetId, IReadOnlyDictionary<uint, uint> itemIdMap, string nameSuffix = " Variant")
    {
        if (sourceSetId == newSetId || sourceSetId == 0 || newSetId == 0) throw new ArgumentException("Source and destination set IDs must be distinct positive values.");
        var catalog = DbcSchemaCatalog.Load(schemaPath); var file = WdbcFile.Load(itemSetPath); var schema = catalog.ResolveColumns("ItemSet", file.FieldCount);
        RequireNamed(schema, "ItemSet"); var rows = DbcRecordIdentity.IndexRows(file, schema.Columns, schema.KeyStrategy);
        if (!rows.TryGetValue(sourceSetId, out var sourceRow)) throw new InvalidDataException($"Item set {sourceSetId} does not exist.");
        if (rows.ContainsKey(newSetId)) throw new InvalidDataException($"Item set {newSetId} already exists; nothing was replaced.");
        var targetRow = file.AddBlankRow();
        foreach (var column in schema.Columns)
        {
            if (column.Type == DbcValueType.StringOffset) file.SetDisplayValue(targetRow, column, file.GetString(file.GetRaw(sourceRow, column)));
            else file.SetRaw(targetRow, column, file.GetRaw(sourceRow, column));
        }
        var key = DbcRecordIdentity.PhysicalColumn(schema.Columns, schema.KeyStrategy) ?? throw new InvalidDataException("ItemSet has no physical ID column."); file.SetRaw(targetRow, key, newSetId);
        foreach (var column in schema.Columns.Where(column => column.Name.StartsWith("Name_Lang[", StringComparison.OrdinalIgnoreCase) && column.Type == DbcValueType.StringOffset))
        {
            var sourceName = file.GetString(file.GetRaw(sourceRow, column)); if (sourceName.Length > 0) file.SetDisplayValue(targetRow, column, sourceName + nameSuffix);
        }
        for (var index = 0; index < 17; index++)
        {
            var column = Column(schema.Columns, $"ItemID[{index}]"); var sourceItem = file.GetRaw(sourceRow, column); if (sourceItem == 0) continue;
            if (!itemIdMap.TryGetValue(sourceItem, out var targetItem)) throw new InvalidDataException($"No new item ID was supplied for set member {sourceItem}.");
            file.SetRaw(targetRow, column, targetItem);
        }
        file.Save(outputPath); var name = Convert.ToString(file.GetDisplayValue(targetRow, Column(schema.Columns, "Name_Lang[enUS]"))) ?? string.Empty;
        return new(sourceSetId, newSetId, name, new Dictionary<uint, uint>(itemIdMap), Path.GetFullPath(outputPath));
    }

    public static void SetEffects(string itemSetPath, string schemaPath, string outputPath, uint setId, IReadOnlyList<ItemSetEffect> effects)
    {
        if (effects.Count > 8) throw new ArgumentOutOfRangeException(nameof(effects), "A WotLK item set supports at most eight bonus slots.");
        var catalog = DbcSchemaCatalog.Load(schemaPath); var file = WdbcFile.Load(itemSetPath); var schema = catalog.ResolveColumns("ItemSet", file.FieldCount);
        RequireNamed(schema, "ItemSet"); var row = FindRow(file, schema, setId);
        for (var index = 0; index < 8; index++)
        {
            var effect = index < effects.Count ? effects[index] : new ItemSetEffect(index + 1, 0, 0, null);
            file.SetRaw(row, Column(schema.Columns, $"SetSpellID[{index}]"), effect.SpellId); file.SetRaw(row, Column(schema.Columns, $"SetThreshold[{index}]"), effect.RequiredItems);
        }
        file.Save(outputPath);
    }

    private static Dictionary<uint, string> LoadSpellNames(string spellPath, DbcSchemaCatalog catalog)
    {
        var file = WdbcFile.Load(spellPath); var schema = catalog.ResolveColumns("Spell", file.FieldCount); RequireNamed(schema, "Spell");
        var id = Column(schema.Columns, "ID"); var name = schema.Columns.FirstOrDefault(column => column.Name.Equals("Name[enUS]", StringComparison.OrdinalIgnoreCase) || column.Name.Equals("Name_Lang[enUS]", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("The Spell schema has no English name column."); var result = new Dictionary<uint, string>();
        for (var row = 0; row < file.RowCount; row++) result[file.GetRaw(row, id)] = Convert.ToString(file.GetDisplayValue(row, name)) ?? string.Empty; return result;
    }
    private static int FindRow(WdbcFile file, DbcSchemaResolution schema, uint id) => DbcRecordIdentity.IndexRows(file, schema.Columns, schema.KeyStrategy).TryGetValue(id, out var row) ? row : throw new InvalidDataException($"Record ID {id} does not exist.");
    private static DbcColumn Column(IReadOnlyList<DbcColumn> columns, string name) => columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"The schema has no '{name}' column.");
    private static void RequireNamed(DbcSchemaResolution schema, string table) { if (schema.MatchKind != DbcSchemaMatchKind.NamedMatch) throw new InvalidDataException($"The selected schema does not exactly match {table}.dbc; refusing relationship edits through a raw fallback."); }
}
