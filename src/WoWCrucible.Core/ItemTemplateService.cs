using System.Globalization;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record ItemStatDraft(int Type, int Value);
public sealed record ItemSpellDraft(int SpellId, int Trigger, int Charges, float ProcPerMinute, int CooldownMs, int Category, int CategoryCooldownMs);

public sealed record ItemDraft(
    uint Entry, string Name, int Class, int Subclass, uint DisplayId, int Quality, int InventoryType,
    uint ItemLevel, uint RequiredLevel, uint BuyPrice, uint SellPrice, uint Bonding, uint Flags,
    float Armor, float DamageMin, float DamageMax, uint Delay, uint MaxDurability, string Description,
    IReadOnlyList<ItemStatDraft>? Stats = null, IReadOnlyList<ItemSpellDraft>? Spells = null, uint ItemSetId = 0,
    int DamageType = 0, int SoundOverrideSubclassId = -1, int Material = 0, int SheatheType = 0);

public sealed record ItemWritePlan(
    string Table,
    IReadOnlyDictionary<string, object> Values,
    IReadOnlyList<string> OmittedFields,
    IReadOnlyList<string> SelectedOmittedFields)
{
    public bool LosesSelectedSemantics => SelectedOmittedFields.Count > 0;

    public void EnsureSelectedSemanticsAreRepresented()
    {
        if (LosesSelectedSemantics)
            throw new NotSupportedException($"The target item_template cannot represent configured field(s): {string.Join(", ", SelectedOmittedFields)}. Crucible refused the write instead of silently dropping those choices.");
    }

    public string PreviewSql()
    {
        var names = string.Join(", ", Values.Keys.Select(QuoteIdentifier));
        var values = string.Join(", ", Values.Values.Select(SqlLiteral));
        return $"INSERT INTO {QuoteIdentifier(Table)} ({names})\nVALUES ({values});";
    }

    internal static string QuoteIdentifier(string value) => $"`{value.Replace("`", "``")}`";
    private static string SqlLiteral(object value) => value switch
    {
        string text => $"'{text.Replace("\\", "\\\\").Replace("'", "''")}'",
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => $"'{Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''")}'"
    };
}

public static class ItemTemplateAdapter
{
    public static DatabaseTableCapability CreatePortableTable()
    {
        var names = new List<string> { "entry", "class", "subclass", "SoundOverrideSubclass", "Material", "name", "displayid", "Quality", "InventoryType", "sheath", "ItemLevel", "RequiredLevel", "BuyPrice", "SellPrice", "bonding", "Flags", "armor", "dmg_min1", "dmg_max1", "dmg_type1", "delay", "MaxDurability", "description", "itemset" };
        for (var slot = 1; slot <= 10; slot++) { names.Add($"stat_type{slot}"); names.Add($"stat_value{slot}"); }
        for (var slot = 1; slot <= 5; slot++) names.AddRange([$"spellid_{slot}", $"spelltrigger_{slot}", $"spellcharges_{slot}", $"spellppmRate_{slot}", $"spellcooldown_{slot}", $"spellcategory_{slot}", $"spellcategorycooldown_{slot}"]);
        return new("item_template", names.Select((name, index) => new DatabaseColumnCapability(name, name is "name" or "description" ? "varchar" : "int", name is "name" or "description" ? "varchar(255)" : "int", false, "0", name == "entry" ? "PRI" : string.Empty, string.Empty, index + 1)).ToArray());
    }

    public static ItemWritePlan CreatePlan(ItemDraft draft, DatabaseTableCapability table)
    {
        if (!table.Name.Equals("item_template", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The selected table is not item_template.");
        if (draft.Entry == 0 || string.IsNullOrWhiteSpace(draft.Name)) throw new InvalidDataException("Entry ID and name are required.");
        var semanticValues = new (string Name, object Value, bool Selected)[]
        {
            ("entry", draft.Entry, true), ("class", draft.Class, true), ("subclass", draft.Subclass, true), ("name", draft.Name.Trim(), true),
            ("displayid", draft.DisplayId, true), ("Quality", draft.Quality, true), ("InventoryType", draft.InventoryType, true),
            ("SoundOverrideSubclass", draft.SoundOverrideSubclassId, draft.SoundOverrideSubclassId != -1), ("Material", draft.Material, draft.Material != 0), ("sheath", draft.SheatheType, draft.SheatheType != 0),
            ("ItemLevel", draft.ItemLevel, draft.ItemLevel != 0), ("RequiredLevel", draft.RequiredLevel, draft.RequiredLevel != 0), ("BuyPrice", draft.BuyPrice, draft.BuyPrice != 0),
            ("SellPrice", draft.SellPrice, draft.SellPrice != 0), ("bonding", draft.Bonding, draft.Bonding != 0), ("Flags", draft.Flags, draft.Flags != 0), ("armor", draft.Armor, draft.Armor != 0),
            ("dmg_min1", draft.DamageMin, draft.DamageMin != 0), ("dmg_max1", draft.DamageMax, draft.DamageMax != 0), ("dmg_type1", draft.DamageType, draft.DamageType != 0), ("delay", draft.Delay, draft.Delay != 0),
            ("MaxDurability", draft.MaxDurability, draft.MaxDurability != 0), ("description", draft.Description ?? string.Empty, !string.IsNullOrWhiteSpace(draft.Description)), ("itemset", draft.ItemSetId, draft.ItemSetId != 0)
        };
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var omitted = new List<string>();
        var selectedOmitted = new List<string>();
        foreach (var (name, value, selected) in semanticValues)
        {
            var column = table.Find(name);
            if (column is null) { omitted.Add(name); if (selected) selectedOmitted.Add(name); } else values[column.Name] = value;
        }
        // TrinityCore reads only the first StatsCount slots, so validate incomplete choices and compact real stats before assigning slots.
        var requestedStats = (draft.Stats ?? []).Take(10).ToArray();
        for (var index = 0; index < requestedStats.Length; index++)
        {
            if (requestedStats[index].Type == 0 && requestedStats[index].Value != 0)
                throw new InvalidDataException($"Stat slot {index + 1} has value {requestedStats[index].Value} but no stat type. Choose a named stat or clear its value.");
            if (requestedStats[index].Type != 0 && requestedStats[index].Value == 0)
                throw new InvalidDataException($"Stat slot {index + 1} selects type {requestedStats[index].Type} with a zero value. That stat is invisible in game; enter a nonzero value or choose None.");
        }
        var stats = requestedStats.Where(stat => stat.Type != 0 && stat.Value != 0).ToArray();
        for (var slot = 1; slot <= 10; slot++)
        {
            var stat = slot <= stats.Length ? stats[slot - 1] : new ItemStatDraft(0, 0);
            var selected = slot <= stats.Length;
            AddIfPresent(table, values, omitted, selectedOmitted, $"stat_type{slot}", stat.Type, selected); AddIfPresent(table, values, omitted, selectedOmitted, $"stat_value{slot}", stat.Value, selected);
        }
        var spells = draft.Spells ?? [];
        for (var slot = 1; slot <= 5; slot++)
        {
            var spell = slot <= spells.Count ? spells[slot - 1] : new ItemSpellDraft(0, 0, 0, 0, -1, 0, -1);
            var selected = spell.SpellId != 0 || spell.Trigger != 0 || spell.Charges != 0 || spell.ProcPerMinute != 0 || spell.CooldownMs != -1 || spell.Category != 0 || spell.CategoryCooldownMs != -1;
            AddIfPresent(table, values, omitted, selectedOmitted, $"spellid_{slot}", spell.SpellId, selected); AddIfPresent(table, values, omitted, selectedOmitted, $"spelltrigger_{slot}", spell.Trigger, selected);
            AddIfPresent(table, values, omitted, selectedOmitted, $"spellcharges_{slot}", spell.Charges, selected); AddIfPresent(table, values, omitted, selectedOmitted, $"spellppmRate_{slot}", spell.ProcPerMinute, selected);
            AddIfPresent(table, values, omitted, selectedOmitted, $"spellcooldown_{slot}", spell.CooldownMs, selected); AddIfPresent(table, values, omitted, selectedOmitted, $"spellcategory_{slot}", spell.Category, selected);
            AddIfPresent(table, values, omitted, selectedOmitted, $"spellcategorycooldown_{slot}", spell.CategoryCooldownMs, selected);
        }
        var statCount = table.Find("StatsCount");
        if (statCount is not null) values[statCount.Name] = stats.Length;
        foreach (var required in new[] { "entry", "class", "subclass", "name", "displayid", "Quality", "InventoryType" })
            if (table.Find(required) is null) throw new NotSupportedException($"This item_template has no '{required}' column, so the item adapter cannot safely target it.");
        return new(table.Name, values, omitted.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), selectedOmitted.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void AddIfPresent(DatabaseTableCapability table, Dictionary<string, object> values, List<string> omitted, List<string> selectedOmitted, string name, object value, bool selected)
    {
        var column = table.Find(name); if (column is null) { omitted.Add(name); if (selected) selectedOmitted.Add(name); } else values[column.Name] = value;
    }
}

public sealed class ItemTemplateService
{
    public async Task InsertAsync(DatabaseConnectionProfile profile, ItemWritePlan plan, CancellationToken cancellationToken = default)
    {
        plan.EnsureSelectedSemanticsAreRepresented();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var entry = plan.Values.First(pair => pair.Key.Equals("entry", StringComparison.OrdinalIgnoreCase)).Value;
        await using (var exists = new MySqlCommand($"SELECT 1 FROM {ItemWritePlan.QuoteIdentifier(plan.Table)} WHERE `entry`=@entry LIMIT 1", connection, transaction))
        {
            exists.Parameters.AddWithValue("@entry", entry);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
                throw new InvalidOperationException($"Item entry {entry} already exists. Crucible will not silently replace it.");
        }
        var parameterNames = plan.Values.Select((_, index) => $"@p{index}").ToArray();
        var sql = $"INSERT INTO {ItemWritePlan.QuoteIdentifier(plan.Table)} ({string.Join(",", plan.Values.Keys.Select(ItemWritePlan.QuoteIdentifier))}) VALUES ({string.Join(",", parameterNames)})";
        await using var command = new MySqlCommand(sql, connection, transaction);
        var valueIndex = 0;
        foreach (var value in plan.Values.Values) command.Parameters.AddWithValue(parameterNames[valueIndex++], value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
