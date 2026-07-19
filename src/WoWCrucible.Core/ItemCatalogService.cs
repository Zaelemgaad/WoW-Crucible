using System.Text.Json.Serialization;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record ItemCatalogEntry(uint Entry, string Name, int Quality, int ItemLevel, uint ItemSetId,
    IReadOnlyList<string> AcquisitionSources, IReadOnlyList<string>? ReviewNotes = null)
{
    public bool HasKnownAcquisitionPath => AcquisitionSources.Count > 0;
    public IReadOnlyList<string> NoPathReview => ReviewNotes ?? [];
}
public sealed record ItemAcquisitionAudit(string Database, DateTimeOffset AuditedUtc, IReadOnlyList<string> CheckedSources, IReadOnlyList<string> MissingSources,
    int TotalItems, int ObtainableItems, IReadOnlyList<ItemCatalogEntry> NoKnownAcquisitionPath,
    [property: JsonIgnore] IReadOnlyList<ItemCatalogEntry> AllItems);
public sealed record ItemAcquisitionInspection(ItemCatalogEntry? Item, IReadOnlyList<string> AcceptedEvidence, IReadOnlyList<string> RejectedEvidence,
    IReadOnlyList<string> CheckedSources, IReadOnlyList<string> MissingSources)
{
    public bool Found => Item is not null;
    public bool HasKnownAcquisitionPath => Item?.HasKnownAcquisitionPath == true;
}
public sealed record ItemCloneResult(uint SourceEntry, uint NewEntry, string SourceName, string NewName, uint ItemSetId, int CopiedColumns, int CopiedLocaleRows);

public sealed class ItemCatalogService
{
    private sealed record AcquisitionScan(IReadOnlyList<ItemCatalogEntry> Items, IReadOnlyDictionary<uint, HashSet<string>> Rejected,
        IReadOnlyList<string> CheckedSources, IReadOnlyList<string> MissingSources);
    private sealed record AcquisitionSpec(string Table, params string[] Columns);
    internal sealed record LootRow(uint Entry, uint Item, uint Reference);
    internal sealed record LootReachabilityData(
        IReadOnlyDictionary<string, IReadOnlyList<LootRow>> Tables,
        IReadOnlyDictionary<uint, IReadOnlyList<LootRow>> ReferencePools,
        IReadOnlyDictionary<string, IReadOnlySet<uint>> FixedOwners,
        IReadOnlyDictionary<uint, uint> ItemDisenchantPools);
    private static readonly AcquisitionSpec[] DirectAcquisitionSpecs =
    [
        new("npc_vendor", "item"),
        new("playercreateinfo_item", "itemid", "item"),
        new("achievement_reward", "ItemID", "item")
    ];
    private static readonly string[] LootTables =
    [
        "creature_loot_template", "gameobject_loot_template", "item_loot_template", "mail_loot_template",
        "pickpocketing_loot_template", "skinning_loot_template", "disenchant_loot_template", "fishing_loot_template",
        "spell_loot_template", "prospecting_loot_template", "milling_loot_template"
    ];
    private static readonly string[] QuestRewardColumns = ["RewardItem1", "RewardItem2", "RewardItem3", "RewardItem4", "RewardChoiceItemID1", "RewardChoiceItemID2", "RewardChoiceItemID3", "RewardChoiceItemID4", "RewardChoiceItemID5", "RewardChoiceItemID6"];

    public static bool IsDirectLootItem(long item, long reference) => item > 0 && item <= uint.MaxValue && reference <= 0;
    public static bool IsLinkedQuestReward(uint questId, IReadOnlySet<uint> starters, IReadOnlySet<uint> enders) => starters.Contains(questId) && enders.Contains(questId);
    public static bool IsUsableQuestReward(uint questId, IReadOnlySet<uint> starters, IReadOnlySet<uint> enders, IReadOnlySet<uint> disabled)
        => IsLinkedQuestReward(questId, starters, enders) && !disabled.Contains(questId);

    public Task<ItemAcquisitionAudit> AuditAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
        => AuditAsync(profile, null, cancellationToken);

    public async Task<ItemAcquisitionAudit> AuditAsync(DatabaseConnectionProfile profile, string? dbcFolder, CancellationToken cancellationToken = default)
    {
        var scan = await ScanAsync(profile, dbcFolder, cancellationToken);
        var unavailable = scan.Items.Where(item => !item.HasKnownAcquisitionPath).ToArray();
        return new(profile.Database, DateTimeOffset.UtcNow, scan.CheckedSources, scan.MissingSources, scan.Items.Count, scan.Items.Count - unavailable.Length, unavailable, scan.Items);
    }

    public Task<ItemAcquisitionInspection> InspectAsync(DatabaseConnectionProfile profile, uint entry, CancellationToken cancellationToken = default)
        => InspectAsync(profile, entry, null, cancellationToken);

    public async Task<ItemAcquisitionInspection> InspectAsync(DatabaseConnectionProfile profile, uint entry, string? dbcFolder, CancellationToken cancellationToken = default)
    {
        if (entry == 0) throw new ArgumentOutOfRangeException(nameof(entry), "Item ID must be positive.");
        var scan = await ScanAsync(profile, dbcFolder, cancellationToken);
        var item = scan.Items.FirstOrDefault(candidate => candidate.Entry == entry);
        var accepted = item?.AcquisitionSources.Select(source => $"Accepted · {source}").ToArray() ?? [];
        var rejected = item?.NoPathReview.Count > 0
            ? item.NoPathReview
            : scan.Rejected.TryGetValue(entry, out var findings)
                ? findings.Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : item is null || item.HasKnownAcquisitionPath ? [] : [NoEvidenceMessage];
        return new(item, accepted, rejected, scan.CheckedSources, scan.MissingSources);
    }

    private async Task<AcquisitionScan> ScanAsync(DatabaseConnectionProfile profile, string? dbcFolder, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        var schema = await ReadSchemaAsync(connection, profile.Database, cancellationToken);
        var itemTable = ResolveTable(schema, "item_template") ?? throw new NotSupportedException("The selected database has no item_template table.");
        var acquired = new Dictionary<uint, HashSet<string>>(); var rejected = new Dictionary<uint, HashSet<string>>(); var checkedSources = new List<string>(); var missingSources = new List<string>();
        foreach (var spec in DirectAcquisitionSpecs)
        {
            var table = ResolveTable(schema, spec.Table);
            if (table is null) { missingSources.Add(spec.Table); continue; }
            var columns = spec.Columns.Select(candidate => ResolveColumn(schema[table], candidate)).Where(column => column is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (columns.Length == 0) { missingSources.Add(spec.Table); continue; }
            checkedSources.Add(table);
            if (spec.Table.Equals("npc_vendor", StringComparison.OrdinalIgnoreCase))
            {
                var ownerColumn = ResolveColumn(schema[table], "entry", "Entry"); var templateTable = ResolveTable(schema, "creature_template");
                var templateEntry = templateTable is null ? null : ResolveColumn(schema[templateTable], "entry", "Entry");
                if (ownerColumn is null || templateTable is null || templateEntry is null) { missingSources.Add("npc_vendor template ownership"); continue; }
                var validOwners = new HashSet<uint>(); await ReadPositiveColumnAsync(connection, templateTable, templateEntry, validOwners, cancellationToken);
                foreach (var column in columns)
                {
                    await using var command = new MySqlCommand($"SELECT DISTINCT {Quote(column!)},{Quote(ownerColumn)} FROM {Quote(table)} WHERE {Quote(column!)}>0", connection) { CommandTimeout = 120 };
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var rawItem = Convert.ToInt64(reader.GetValue(0)); var rawOwner = Convert.ToInt64(reader.GetValue(1));
                        if (rawItem is <= 0 or > uint.MaxValue) continue;
                        if (rawOwner is > 0 and <= uint.MaxValue && validOwners.Contains((uint)rawOwner)) AddAcquired(acquired, (uint)rawItem, $"{table}.{column} (template-linked vendor {rawOwner})");
                        else AddRejected(rejected, (uint)rawItem, $"Ignored · {table}.{column} vendor owner {rawOwner} has no creature_template row.");
                    }
                }
                continue;
            }
            foreach (var column in columns)
            {
                await using var command = new MySqlCommand($"SELECT DISTINCT {Quote(column!)} FROM {Quote(table)} WHERE {Quote(column!)} > 0", connection) { CommandTimeout = 120 };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var entry = Convert.ToUInt32(reader.GetValue(0));
                    AddAcquired(acquired, entry, $"{table}.{column}");
                }
            }
        }
        var reachableSpells = new HashSet<uint>();
        await CollectLinkedQuestRewardsAsync(connection, schema, acquired, rejected, reachableSpells, checkedSources, missingSources, cancellationToken);
        CollectCharStartOutfitItems(dbcFolder, acquired, checkedSources, missingSources);
        var loot = await ReadLootReachabilityAsync(connection, schema, itemTable, dbcFolder, checkedSources, missingSources, cancellationToken);
        // Item loot and item-use/create-spell effects form a real graph: a reachable
        // container can award an item that teaches a spell, which can create another
        // millable/container item. Iterate to a fixed point instead of assuming the
        // order of the source tables proves reachability.
        while (true)
        {
            var itemCount = acquired.Count;
            var spellCount = reachableSpells.Count;
            await CollectReachableSpellCreatedItemsAsync(connection, schema, itemTable, dbcFolder, reachableSpells, acquired, checkedSources, missingSources, cancellationToken);
            ApplyReachableLoot(loot, acquired, reachableSpells, rejected, cancellationToken);
            if (itemCount == acquired.Count && spellCount == reachableSpells.Count) break;
        }

        var itemColumns = schema[itemTable];
        var entryColumn = RequireColumn(itemColumns, "entry"); var nameColumn = RequireColumn(itemColumns, "name");
        var qualityColumn = ResolveColumn(itemColumns, "Quality", "quality"); var levelColumn = ResolveColumn(itemColumns, "ItemLevel", "itemlevel"); var setColumn = ResolveColumn(itemColumns, "itemset", "ItemSet");
        var sql = $"SELECT {Quote(entryColumn)}, {Quote(nameColumn)}, {(qualityColumn is null ? "0" : Quote(qualityColumn))}, {(levelColumn is null ? "0" : Quote(levelColumn))}, {(setColumn is null ? "0" : Quote(setColumn))} FROM {Quote(itemTable)} ORDER BY {Quote(entryColumn)}";
        var items = new List<ItemCatalogEntry>();
        await using (var command = new MySqlCommand(sql, connection) { CommandTimeout = 120 })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var entry = Convert.ToUInt32(reader.GetValue(0)); var sources = acquired.TryGetValue(entry, out var found) ? found.Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
                var review = sources.Length > 0 ? [] : rejected.TryGetValue(entry, out var rejectedFindings)
                    ? rejectedFindings.Order(StringComparer.OrdinalIgnoreCase).ToArray()
                    : [NoEvidenceMessage];
                items.Add(new(entry, Convert.ToString(reader.GetValue(1)) ?? string.Empty, Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)), Convert.ToUInt32(reader.GetValue(4)), sources, review));
            }
        return new(items, rejected,
            checkedSources.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            missingSources.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private const string NoEvidenceMessage = "No accepted acquisition row was found in the checked SQL and DBC coverage. Custom scripts and core code still require manual review.";

    public static IReadOnlySet<uint> ReadCharStartOutfitItems(string path)
    {
        var file = WdbcFile.Load(path);
        if (file.FieldCount != 77 || file.RecordSize < 104) throw new InvalidDataException($"CharStartOutfit.dbc must use the WotLK 77-field layout; this file has {file.FieldCount} fields and {file.RecordSize}-byte records.");
        var result = new HashSet<uint>();
        for (var row = 0; row < file.RowCount; row++)
            for (var slot = 0; slot < 24; slot++)
            {
                var raw = file.GetRaw(row, new(slot + 5, 8 + slot * 4, 4, $"ItemID[{slot}]", DbcValueType.Int32));
                var signed = unchecked((int)raw); if (signed > 0) result.Add((uint)signed);
            }
        return result;
    }

    private static void CollectCharStartOutfitItems(string? dbcFolder, Dictionary<uint, HashSet<string>> acquired,
        ICollection<string> checkedSources, ICollection<string> missingSources)
    {
        if (string.IsNullOrWhiteSpace(dbcFolder)) { missingSources.Add("CharStartOutfit.dbc (configure the server DBC folder)"); return; }
        var path = Directory.Exists(dbcFolder) ? Path.Combine(dbcFolder, "CharStartOutfit.dbc") : dbcFolder;
        if (!File.Exists(path)) { missingSources.Add($"CharStartOutfit.dbc ({path})"); return; }
        foreach (var entry in ReadCharStartOutfitItems(path)) AddAcquired(acquired, entry, "CharStartOutfit.dbc (starting equipment)");
        checkedSources.Add("CharStartOutfit.dbc (starting equipment)");
    }

    private static async Task<LootReachabilityData> ReadLootReachabilityAsync(MySqlConnection connection, Dictionary<string, List<string>> schema,
        string itemTable, string? dbcFolder, ICollection<string> checkedSources, ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, IReadOnlyList<LootRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in LootTables)
        {
            var table = ResolveTable(schema, requested);
            if (table is null) { missingSources.Add(requested); continue; }
            var entry = ResolveColumn(schema[table], "Entry", "entry");
            var item = ResolveColumn(schema[table], "Item", "item");
            if (entry is null || item is null) { missingSources.Add($"{requested} owner/item columns"); continue; }
            var reference = ResolveColumn(schema[table], "Reference", "reference");
            checkedSources.Add(table);
            var sql = reference is null
                ? $"SELECT {Quote(entry)},{Quote(item)},0 FROM {Quote(table)} WHERE {Quote(item)}>0"
                : $"SELECT {Quote(entry)},{Quote(item)},{Quote(reference)} FROM {Quote(table)} WHERE {Quote(item)}>0 OR {Quote(reference)}>0";
            var rows = new List<LootRow>();
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 120 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rawEntry = Convert.ToInt64(reader.GetValue(0)); var rawItem = Convert.ToInt64(reader.GetValue(1)); var rawReference = Convert.ToInt64(reader.GetValue(2));
                if (rawEntry is <= 0 or > uint.MaxValue) continue;
                rows.Add(new((uint)rawEntry, rawItem is > 0 and <= uint.MaxValue ? (uint)rawItem : 0, rawReference is > 0 and <= uint.MaxValue ? (uint)rawReference : 0));
            }
            tables[requested] = rows;
        }

        var referenceTable = ResolveTable(schema, "reference_loot_template");
        var referenceRows = new Dictionary<uint, IReadOnlyList<LootRow>>();
        if (referenceTable is null) missingSources.Add("reference_loot_template (reachable pools)");
        else
        {
            var columns = schema[referenceTable]; var entryColumn = ResolveColumn(columns, "Entry", "entry"); var itemColumn = ResolveColumn(columns, "Item", "item"); var referenceColumn = ResolveColumn(columns, "Reference", "reference");
            if (entryColumn is null || itemColumn is null || referenceColumn is null) missingSources.Add("reference_loot_template (reachable pools)");
            else
            {
                checkedSources.Add("reference_loot_template (reachable pools only)");
                var grouped = new Dictionary<uint, List<LootRow>>();
                await using var command = new MySqlCommand($"SELECT {Quote(entryColumn)},{Quote(itemColumn)},{Quote(referenceColumn)} FROM {Quote(referenceTable)}", connection) { CommandTimeout = 120 };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var rawEntry = Convert.ToInt64(reader.GetValue(0)); if (rawEntry is <= 0 or > uint.MaxValue) continue;
                    var entry = (uint)rawEntry; var rawItem = Convert.ToInt64(reader.GetValue(1)); var rawReference = Convert.ToInt64(reader.GetValue(2));
                    if (!grouped.TryGetValue(entry, out var values)) grouped[entry] = values = [];
                    values.Add(new(entry, rawItem is > 0 and <= uint.MaxValue ? (uint)rawItem : 0, rawReference is > 0 and <= uint.MaxValue ? (uint)rawReference : 0));
                }
                referenceRows = grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LootRow>)pair.Value);
            }
        }

        var fixedOwners = new Dictionary<string, IReadOnlySet<uint>>(StringComparer.OrdinalIgnoreCase)
        {
            ["creature_loot_template"] = await ReadCreatureLootOwnersAsync(connection, schema, "lootid", checkedSources, missingSources, cancellationToken),
            ["pickpocketing_loot_template"] = await ReadCreatureLootOwnersAsync(connection, schema, "pickpocketloot", checkedSources, missingSources, cancellationToken),
            ["skinning_loot_template"] = await ReadCreatureLootOwnersAsync(connection, schema, "skinloot", checkedSources, missingSources, cancellationToken),
            ["gameobject_loot_template"] = await ReadGameObjectLootOwnersAsync(connection, schema, checkedSources, missingSources, cancellationToken),
            ["mail_loot_template"] = await ReadMailLootOwnersAsync(connection, schema, checkedSources, missingSources, cancellationToken),
            ["fishing_loot_template"] = await ReadFishingLootOwnersAsync(dbcFolder, connection, schema, checkedSources, missingSources, cancellationToken)
        };
        var disenchantPools = await ReadItemDisenchantPoolsAsync(connection, schema, itemTable, checkedSources, missingSources, cancellationToken);
        return new(tables, referenceRows, fixedOwners, disenchantPools);
    }

    internal static void ApplyReachableLoot(LootReachabilityData data, Dictionary<uint, HashSet<string>> acquired, IReadOnlySet<uint> reachableSpells,
        Dictionary<uint, HashSet<string>> rejected, CancellationToken cancellationToken)
    {
        var groupedTables = data.Tables.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyDictionary<uint, IReadOnlyList<LootRow>>)pair.Value.GroupBy(row => row.Entry)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<LootRow>)group.ToArray()), StringComparer.OrdinalIgnoreCase);
        var processedOwners = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        var processedPools = new HashSet<uint>();
        var changed = true;
        while (changed)
        {
            changed = false; cancellationToken.ThrowIfCancellationRequested();
            foreach (var (table, rows) in data.Tables)
            {
                if (!processedOwners.TryGetValue(table, out var processed)) processedOwners[table] = processed = [];
                var owners = data.FixedOwners.TryGetValue(table, out var fixedValues) ? fixedValues : table switch
                {
                    "item_loot_template" or "prospecting_loot_template" or "milling_loot_template" => acquired.Keys.ToHashSet(),
                    "disenchant_loot_template" => acquired.Keys.Where(data.ItemDisenchantPools.ContainsKey).Select(item => data.ItemDisenchantPools[item]).ToHashSet(),
                    "spell_loot_template" => reachableSpells,
                    _ => EmptyOwners
                };
                foreach (var owner in owners)
                {
                    if (!processed.Add(owner) || !groupedTables[table].TryGetValue(owner, out var ownerRows)) continue;
                    foreach (var row in ownerRows)
                    {
                        if (row.Reference != 0) changed |= ApplyReferencePool(row.Reference, $"{table} owner {owner}", data.ReferencePools, acquired, rejected, processedPools);
                        else if (row.Item != 0)
                        {
                            var before = acquired.Count; AddAcquired(acquired, row.Item, $"{table} owner {owner} (reachable direct loot)"); changed |= acquired.Count != before;
                        }
                    }
                }
            }
        }

        foreach (var (table, rows) in data.Tables)
        {
            var processed = processedOwners.TryGetValue(table, out var values) ? values : EmptyOwners;
            foreach (var row in rows.Where(row => !processed.Contains(row.Entry)))
                if (row.Reference == 0) AddRejected(rejected, row.Item, $"Ignored · {table} owner {row.Entry} has no known reachable source.");
                // With a nonzero Reference, Trinity/AzerothCore interprets Item as
                // a control value rather than an item ID. Never attach that row as
                // either accepted or rejected evidence for the numerically equal item.
        }
        foreach (var (pool, rows) in data.ReferencePools.Where(pair => !processedPools.Contains(pair.Key)))
            foreach (var row in rows)
                if (row.Reference == 0) AddRejected(rejected, row.Item, $"Ignored · reference_loot_template pool {pool} has no reachable parent loot row.");
    }

    private static bool ApplyReferencePool(uint firstPool, string owner, IReadOnlyDictionary<uint, IReadOnlyList<LootRow>> pools,
        Dictionary<uint, HashSet<string>> acquired, Dictionary<uint, HashSet<string>> rejected, ISet<uint> processedPools)
    {
        var changed = false; var pending = new Queue<uint>(); pending.Enqueue(firstPool);
        while (pending.TryDequeue(out var pool))
        {
            if (!processedPools.Add(pool) || !pools.TryGetValue(pool, out var rows)) continue;
            foreach (var row in rows)
                if (row.Reference != 0)
                {
                    pending.Enqueue(row.Reference);
                }
                else if (row.Item != 0)
                {
                    var before = acquired.Count; AddAcquired(acquired, row.Item, $"reference_loot_template pool {pool} reached from {owner}"); changed |= acquired.Count != before;
                }
        }
        return changed;
    }

    private static readonly IReadOnlySet<uint> EmptyOwners = new HashSet<uint>();

    private static async Task<IReadOnlySet<uint>> ReadCreatureLootOwnersAsync(MySqlConnection connection, Dictionary<string, List<string>> schema,
        string ownerColumnName, ICollection<string> checkedSources, ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var templateTable = ResolveTable(schema, "creature_template");
        if (templateTable is null) { missingSources.Add($"creature_template ({ownerColumnName} ownership)"); return EmptyOwners; }
        var owner = ResolveColumn(schema[templateTable], ownerColumnName);
        if (owner is null) { missingSources.Add($"{templateTable}.{ownerColumnName} ownership"); return EmptyOwners; }
        var result = new HashSet<uint>(); await ReadPositiveColumnAsync(connection, templateTable, owner, result, cancellationToken);
        // A static spawn is deliberately not required. Dungeon/event scripts can
        // summon valid creature templates at runtime; the template-to-loot link is
        // the strongest schema-level evidence available without executing scripts.
        checkedSources.Add($"{templateTable}.{ownerColumnName} (template-linked owners)");
        return result;
    }

    private static async Task<IReadOnlySet<uint>> ReadGameObjectLootOwnersAsync(MySqlConnection connection, Dictionary<string, List<string>> schema,
        ICollection<string> checkedSources, ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var templateTable = ResolveTable(schema, "gameobject_template");
        if (templateTable is null) { missingSources.Add("gameobject_template loot ownership"); return EmptyOwners; }
        var type = ResolveColumn(schema[templateTable], "type"); var data1 = ResolveColumn(schema[templateTable], "Data1", "data1");
        if (type is null || data1 is null) { missingSources.Add("gameobject_template chest/fishing-hole loot ownership"); return EmptyOwners; }
        var result = new HashSet<uint>();
        await using var command = new MySqlCommand($"SELECT DISTINCT {Quote(data1)} FROM {Quote(templateTable)} WHERE {Quote(type)} IN (3,25) AND {Quote(data1)}>0", connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var loot = Convert.ToInt64(reader.GetValue(0)); if (loot is > 0 and <= uint.MaxValue) result.Add((uint)loot);
        }
        // Script-created boss caches are normal, so template ownership—not a
        // literal gameobject spawn row—is the correct core-compatible boundary.
        checkedSources.Add("gameobject_template.Data1 (template-linked chest/fishing-hole owners)");
        return result;
    }

    private static async Task<IReadOnlySet<uint>> ReadMailLootOwnersAsync(MySqlConnection connection, Dictionary<string, List<string>> schema,
        ICollection<string> checkedSources, ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var result = new HashSet<uint>(); var levelTable = ResolveTable(schema, "mail_level_reward");
        if (levelTable is null) missingSources.Add("mail_level_reward (mail loot owners)");
        else
        {
            var mail = ResolveColumn(schema[levelTable], "mailTemplateId", "mailTemplateID");
            if (mail is null) missingSources.Add("mail_level_reward.mailTemplateId");
            else { await ReadPositiveColumnAsync(connection, levelTable, mail, result, cancellationToken); checkedSources.Add("mail_level_reward (mail loot owners)"); }
        }
        foreach (var (requested, candidates) in new[]
        {
            ("achievement_reward", new[] { "MailTemplateID", "mailTemplateId" }),
            ("arena_season_reward_group", new[] { "reward_mail_template_id", "RewardMailTemplateID" })
        })
        {
            var table = ResolveTable(schema, requested); var mail = table is null ? null : ResolveColumn(schema[table], candidates);
            if (table is null || mail is null) missingSources.Add($"{requested} (mail loot owners)");
            else { await ReadPositiveColumnAsync(connection, table, mail, result, cancellationToken); checkedSources.Add($"{table} (mail loot owners)"); }
        }
        var addonTable = ResolveTable(schema, "quest_template_addon");
        if (addonTable is null) missingSources.Add("quest_template_addon (mail loot owners)");
        else
        {
            var id = ResolveColumn(schema[addonTable], "ID", "entry"); var mail = ResolveColumn(schema[addonTable], "RewardMailTemplateID", "RewardMailTemplateId");
            if (id is null || mail is null) missingSources.Add("quest_template_addon reward-mail columns");
            else
            {
                var starters = await ReadQuestLinksAsync(connection, schema, ["creature_queststarter", "gameobject_queststarter", "game_event_creature_quest", "game_event_gameobject_quest"], missingSources, cancellationToken);
                var enders = await ReadQuestLinksAsync(connection, schema, ["creature_questender", "gameobject_questender"], missingSources, cancellationToken);
                var systemGranted = await ReadSystemGrantedQuestIdsAsync(connection, schema, checkedSources, missingSources, cancellationToken);
                var disabled = await ReadDisabledQuestsAsync(connection, schema, missingSources, cancellationToken);
                var disabledIds = disabled.Keys.ToHashSet();
                await using var command = new MySqlCommand($"SELECT {Quote(id)},{Quote(mail)} FROM {Quote(addonTable)} WHERE {Quote(mail)}>0", connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var quest = Convert.ToInt64(reader.GetValue(0)); var template = Convert.ToInt64(reader.GetValue(1));
                    if (quest is > 0 and <= uint.MaxValue && template is > 0 and <= uint.MaxValue &&
                        (IsLinkedQuestReward((uint)quest, starters, enders) || systemGranted.Contains((uint)quest)) && !disabledIds.Contains((uint)quest)) result.Add((uint)template);
                }
                checkedSources.Add("quest_template_addon (usable quest mail owners)");
            }
        }
        return result;
    }

    private static async Task<IReadOnlySet<uint>> ReadFishingLootOwnersAsync(string? dbcFolder, MySqlConnection connection, Dictionary<string, List<string>> schema,
        ICollection<string> checkedSources, ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var result = new HashSet<uint>();
        if (!string.IsNullOrWhiteSpace(dbcFolder))
        {
            var path = Directory.Exists(dbcFolder) ? Path.Combine(dbcFolder, "AreaTable.dbc") : string.Empty;
            if (File.Exists(path))
            {
                var file = WdbcFile.Load(path); var id = new DbcColumn(0, 0, 4, "ID", DbcValueType.UInt32, true);
                for (var row = 0; row < file.RowCount; row++) { var value = file.GetRaw(row, id); if (value != 0) result.Add(value); }
                checkedSources.Add("AreaTable.dbc (fishing loot owners)");
            }
        }
        if (result.Count == 0)
        {
            var table = ResolveTable(schema, "skill_fishing_base_level"); var entry = table is null ? null : ResolveColumn(schema[table], "entry", "ID");
            if (table is null || entry is null) missingSources.Add("AreaTable.dbc/skill_fishing_base_level (fishing loot owners)");
            else { await ReadPositiveColumnAsync(connection, table, entry, result, cancellationToken); checkedSources.Add("skill_fishing_base_level (fishing loot owners fallback)"); }
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<uint, uint>> ReadItemDisenchantPoolsAsync(MySqlConnection connection, Dictionary<string, List<string>> schema,
        string itemTable, ICollection<string> checkedSources, ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var entry = ResolveColumn(schema[itemTable], "entry", "ID"); var pool = ResolveColumn(schema[itemTable], "DisenchantID", "disenchantid");
        if (entry is null || pool is null) { missingSources.Add($"{itemTable}.DisenchantID"); return new Dictionary<uint, uint>(); }
        var result = new Dictionary<uint, uint>();
        await using var command = new MySqlCommand($"SELECT {Quote(entry)},{Quote(pool)} FROM {Quote(itemTable)} WHERE {Quote(pool)}>0", connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawItem = Convert.ToInt64(reader.GetValue(0)); var rawPool = Convert.ToInt64(reader.GetValue(1));
            if (rawItem is > 0 and <= uint.MaxValue && rawPool is > 0 and <= uint.MaxValue) result[(uint)rawItem] = (uint)rawPool;
        }
        checkedSources.Add($"{itemTable}.DisenchantID (reachable item ownership)");
        return result;
    }

    private static async Task ReadPositiveColumnAsync(MySqlConnection connection, string table, string column, ISet<uint> destination, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"SELECT DISTINCT {Quote(column)} FROM {Quote(table)} WHERE {Quote(column)}>0", connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var raw = Convert.ToInt64(reader.GetValue(0)); if (raw is > 0 and <= uint.MaxValue) destination.Add((uint)raw);
        }
    }

    private static async Task<HashSet<uint>> ReadSystemGrantedQuestIdsAsync(MySqlConnection connection, Dictionary<string, List<string>> schema,
        ICollection<string> checkedSources, ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var result = new HashSet<uint>(); var table = ResolveTable(schema, "lfg_dungeon_rewards");
        if (table is null) { missingSources.Add("lfg_dungeon_rewards (system-granted quests)"); return result; }
        var columns = new[] { "firstQuestId", "otherQuestId" }.Select(name => ResolveColumn(schema[table], name)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (columns.Length == 0) { missingSources.Add("lfg_dungeon_rewards quest columns"); return result; }
        foreach (var column in columns) await ReadPositiveColumnAsync(connection, table, column, result, cancellationToken);
        checkedSources.Add("lfg_dungeon_rewards (system-granted reward quests)");
        return result;
    }

    private static async Task CollectLinkedQuestRewardsAsync(MySqlConnection connection, Dictionary<string, List<string>> schema,
        Dictionary<uint, HashSet<string>> acquired, Dictionary<uint, HashSet<string>> rejected, ISet<uint> reachableSpells,
        ICollection<string> checkedSources, ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var questTable = ResolveTable(schema, "quest_template");
        if (questTable is null) { missingSources.Add("quest_template"); return; }
        var questId = ResolveColumn(schema[questTable], "ID", "entry");
        var rewards = QuestRewardColumns.Select(name => ResolveColumn(schema[questTable], name)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (questId is null || rewards.Length == 0) { missingSources.Add("quest_template reward columns"); return; }
        var startItem = ResolveColumn(schema[questTable], "StartItem", "SourceItemId");
        var rewardSpell = ResolveColumn(schema[questTable], "RewardSpell");

        var starters = await ReadQuestLinksAsync(connection, schema, ["creature_queststarter", "gameobject_queststarter", "game_event_creature_quest", "game_event_gameobject_quest"], missingSources, cancellationToken);
        var enders = await ReadQuestLinksAsync(connection, schema, ["creature_questender", "gameobject_questender"], missingSources, cancellationToken);
        var systemGranted = await ReadSystemGrantedQuestIdsAsync(connection, schema, checkedSources, missingSources, cancellationToken);
        var disabled = await ReadDisabledQuestsAsync(connection, schema, missingSources, cancellationToken);
        var disabledIds = disabled.Keys.ToHashSet();
        checkedSources.Add("quest_template (usable rewards, starting items, and reward spells)");
        var selected = rewards.Concat(startItem is null ? [] : [startItem]).Concat(rewardSpell is null ? [] : [rewardSpell]).ToArray();
        var sql = $"SELECT {Quote(questId)},{string.Join(',', selected.Select(Quote))} FROM {Quote(questTable)}";
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var currentQuest = Convert.ToUInt32(reader.GetValue(0));
            var usable = (IsLinkedQuestReward(currentQuest, starters, enders) || systemGranted.Contains(currentQuest)) && !disabledIds.Contains(currentQuest);
            for (var index = 0; index < rewards.Length; index++)
            {
                var reward = Convert.ToUInt32(reader.GetValue(index + 1)); if (reward == 0) continue;
                if (usable) AddAcquired(acquired, reward, $"quest_template reward from usable quest {currentQuest}");
                else
                {
                    var reason = disabled.TryGetValue(currentQuest, out var comment)
                        ? $"Ignored · quest {currentQuest} is disabled{(string.IsNullOrWhiteSpace(comment) ? string.Empty : $" ({comment})")}."
                        : $"Ignored · quest {currentQuest} does not have both a live starter and ender.";
                    AddRejected(rejected, reward, reason);
                }
            }
            var next = rewards.Length + 1;
            if (startItem is not null)
            {
                var entry = Convert.ToUInt32(reader.GetValue(next++));
                if (entry != 0)
                {
                    var usableStart = (starters.Contains(currentQuest) || systemGranted.Contains(currentQuest)) && !disabledIds.Contains(currentQuest);
                    if (usableStart) AddAcquired(acquired, entry, $"quest_template starting item from usable quest {currentQuest}");
                    else AddRejected(rejected, entry, disabled.TryGetValue(currentQuest, out var comment)
                        ? $"Ignored · quest {currentQuest} starting item belongs to a disabled quest{(string.IsNullOrWhiteSpace(comment) ? string.Empty : $" ({comment})")}."
                        : $"Ignored · quest {currentQuest} starting item has no live quest starter.");
                }
            }
            if (rewardSpell is not null)
            {
                var raw = Convert.ToInt64(reader.GetValue(next));
                if (usable && raw != 0 && Math.Abs(raw) <= uint.MaxValue) reachableSpells.Add((uint)Math.Abs(raw));
            }
        }
    }

    internal sealed record SpellCreation(IReadOnlyList<uint> CreatedItems, IReadOnlyList<uint> TriggeredOrLearnedSpells);

    internal static IReadOnlyDictionary<uint, SpellCreation> ReadSpellCreationGraph(string path)
    {
        var file = WdbcFile.Load(path);
        if (file.FieldCount != 234 || file.RecordSize != 936)
            throw new InvalidDataException($"Spell.dbc has {file.FieldCount:N0} fields and {file.RecordSize:N0}-byte records; Wrath build 12340 requires 234 fields and 936-byte records.");
        var id = new DbcColumn(0, 0, 4, "ID", DbcValueType.UInt32, true);
        var result = new Dictionary<uint, SpellCreation>();
        for (var row = 0; row < file.RowCount; row++)
        {
            var created = new HashSet<uint>(); var learned = new HashSet<uint>();
            for (var effectIndex = 0; effectIndex < 3; effectIndex++)
            {
                var effect = file.GetRaw(row, new(71 + effectIndex, (71 + effectIndex) * 4, 4, $"Effect[{effectIndex}]", DbcValueType.UInt32));
                var item = file.GetRaw(row, new(107 + effectIndex, (107 + effectIndex) * 4, 4, $"EffectItemType[{effectIndex}]", DbcValueType.UInt32));
                var trigger = file.GetRaw(row, new(116 + effectIndex, (116 + effectIndex) * 4, 4, $"EffectTriggerSpell[{effectIndex}]", DbcValueType.UInt32));
                if (item != 0 && effect is 24 or 66 or 157) created.Add(item);
                if (trigger != 0 && effect is 32 or 36 or 64 or 142 or 148 or 151) learned.Add(trigger);
            }
            if (created.Count > 0 || learned.Count > 0) result[file.GetRaw(row, id)] = new(created.ToArray(), learned.ToArray());
        }
        return result;
    }

    private static async Task CollectReachableSpellCreatedItemsAsync(MySqlConnection connection, Dictionary<string, List<string>> schema, string itemTable,
        string? dbcFolder, HashSet<uint> reachableSpells, Dictionary<uint, HashSet<string>> acquired, ICollection<string> checkedSources,
        ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        async Task AddSpellsAsync(string requestedTable, string[] spellCandidates, string? predicate = null)
        {
            var table = ResolveTable(schema, requestedTable); if (table is null) { missingSources.Add($"{requestedTable} (reachable spells)"); return; }
            var spell = ResolveColumn(schema[table], spellCandidates); if (spell is null) { missingSources.Add($"{requestedTable} (reachable spells)"); return; }
            await using var command = new MySqlCommand($"SELECT DISTINCT {Quote(spell)} FROM {Quote(table)} WHERE {Quote(spell)}<>0{(string.IsNullOrWhiteSpace(predicate) ? string.Empty : $" AND {predicate}")}", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) { var raw = Convert.ToInt64(reader.GetValue(0)); if (raw != 0 && Math.Abs(raw) <= uint.MaxValue) reachableSpells.Add((uint)Math.Abs(raw)); }
            checkedSources.Add($"{table} (reachable spells)");
        }

        await AddSpellsAsync("trainer_spell", ["SpellId", "SpellID", "spell"]);
        await AddSpellsAsync("npc_trainer", ["SpellID", "SpellId", "spell"]);
        var actionTable = ResolveTable(schema, "playercreateinfo_action");
        if (actionTable is not null)
        {
            var type = ResolveColumn(schema[actionTable], "type");
            await AddSpellsAsync(actionTable, ["action", "Action"], type is null ? null : $"{Quote(type)}=0");
        }
        else missingSources.Add("playercreateinfo_action (reachable spells)");

        if (string.IsNullOrWhiteSpace(dbcFolder)) { missingSources.Add("Spell.dbc (reachable create-item effects)"); return; }
        var spellPath = Directory.Exists(dbcFolder) ? Path.Combine(dbcFolder, "Spell.dbc") : dbcFolder;
        if (!File.Exists(spellPath)) { missingSources.Add($"Spell.dbc ({spellPath})"); return; }
        var graph = ReadSpellCreationGraph(spellPath);

        var itemColumns = schema[itemTable]; var entryColumn = RequireColumn(itemColumns, "entry");
        var itemSpellColumns = Enumerable.Range(1, 5).Select(index => ResolveColumn(itemColumns, $"spellid_{index}", $"spellid{index}", $"SpellId{index}"))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var itemSpells = new Dictionary<uint, uint[]>();
        if (itemSpellColumns.Length > 0)
        {
            await using var command = new MySqlCommand($"SELECT {Quote(entryColumn)},{string.Join(',', itemSpellColumns.Select(Quote))} FROM {Quote(itemTable)}", connection) { CommandTimeout = 120 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var spells = Enumerable.Range(1, reader.FieldCount - 1).Select(index => Convert.ToInt64(reader.GetValue(index))).Where(value => value != 0 && Math.Abs(value) <= uint.MaxValue).Select(value => (uint)Math.Abs(value)).Distinct().ToArray();
                if (spells.Length > 0) itemSpells[Convert.ToUInt32(reader.GetValue(0))] = spells;
            }
            checkedSources.Add($"{itemTable} item spells (reachable item use/learn effects)");
        }
        else missingSources.Add($"{itemTable} item spell columns");

        var pending = new Queue<uint>(); var queued = new HashSet<uint>(); var visited = new HashSet<uint>();
        void Enqueue(uint spell) { if (spell != 0 && !visited.Contains(spell) && queued.Add(spell)) pending.Enqueue(spell); }
        foreach (var spell in reachableSpells) Enqueue(spell);
        foreach (var item in acquired.Keys.ToArray()) if (itemSpells.TryGetValue(item, out var spells)) foreach (var spell in spells) Enqueue(spell);
        while (pending.TryDequeue(out var spell))
        {
            cancellationToken.ThrowIfCancellationRequested(); queued.Remove(spell); if (!visited.Add(spell) || !graph.TryGetValue(spell, out var creation)) continue;
            foreach (var learned in creation.TriggeredOrLearnedSpells) Enqueue(learned);
            foreach (var item in creation.CreatedItems)
            {
                var newlyAcquired = !acquired.ContainsKey(item); AddAcquired(acquired, item, $"Spell.dbc create-item effect from reachable spell {spell}");
                if (newlyAcquired && itemSpells.TryGetValue(item, out var itemEffects)) foreach (var itemSpell in itemEffects) Enqueue(itemSpell);
            }
        }
        checkedSources.Add("Spell.dbc (reachable create-item and learn/trigger-spell effects)");
    }

    private static async Task<Dictionary<uint, string>> ReadDisabledQuestsAsync(MySqlConnection connection, Dictionary<string, List<string>> schema,
        ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var table = ResolveTable(schema, "disables"); if (table is null) { missingSources.Add("disables (quest state)"); return []; }
        var columns = schema[table]; var type = ResolveColumn(columns, "sourceType"); var entry = ResolveColumn(columns, "entry"); var comment = ResolveColumn(columns, "comment");
        if (type is null || entry is null) { missingSources.Add("disables (quest state)"); return []; }
        var result = new Dictionary<uint, string>();
        await using var command = new MySqlCommand($"SELECT {Quote(entry)},{(comment is null ? "''" : Quote(comment))} FROM {Quote(table)} WHERE {Quote(type)}=1", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var raw = Convert.ToInt64(reader.GetValue(0)); if (raw is <= 0 or > uint.MaxValue) continue;
            result[(uint)raw] = Convert.ToString(reader.GetValue(1)) ?? string.Empty;
        }
        return result;
    }

    private static async Task<HashSet<uint>> ReadQuestLinksAsync(MySqlConnection connection, Dictionary<string, List<string>> schema, IEnumerable<string> requestedTables, ICollection<string> missingSources, CancellationToken cancellationToken)
    {
        var result = new HashSet<uint>(); var found = false;
        foreach (var requested in requestedTables)
        {
            var table = ResolveTable(schema, requested);
            if (table is null) { missingSources.Add(requested); continue; }
            var quest = ResolveColumn(schema[table], "quest", "Quest");
            if (quest is null) { missingSources.Add(requested); continue; }
            found = true;
            await using var command = new MySqlCommand($"SELECT DISTINCT {Quote(quest)} FROM {Quote(table)} WHERE {Quote(quest)}>0", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(Convert.ToUInt32(reader.GetValue(0)));
        }
        if (!found) result.Clear();
        return result;
    }

    private static void AddAcquired(Dictionary<uint, HashSet<string>> acquired, uint entry, string source)
    {
        if (entry == 0) return;
        if (!acquired.TryGetValue(entry, out var sources)) acquired[entry] = sources = new(StringComparer.OrdinalIgnoreCase);
        sources.Add(source);
    }

    private static void AddRejected(Dictionary<uint, HashSet<string>> rejected, uint entry, string reason)
    {
        if (entry == 0) return;
        if (!rejected.TryGetValue(entry, out var reasons)) rejected[entry] = reasons = new(StringComparer.OrdinalIgnoreCase);
        reasons.Add(reason);
    }

    public async Task<IReadOnlyDictionary<uint, string>> GetItemNamesAsync(DatabaseConnectionProfile profile, IEnumerable<uint> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<uint, string>();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        var schema = await ReadSchemaAsync(connection, profile.Database, cancellationToken);
        var table = ResolveTable(schema, "item_template") ?? throw new NotSupportedException("The selected database has no item_template table.");
        var entry = RequireColumn(schema[table], "entry"); var name = RequireColumn(schema[table], "name");
        var parameters = ids.Select((_, index) => $"@id{index}").ToArray();
        await using var command = new MySqlCommand($"SELECT {Quote(entry)},{Quote(name)} FROM {Quote(table)} WHERE {Quote(entry)} IN ({string.Join(',', parameters)})", connection);
        for (var index = 0; index < ids.Length; index++) command.Parameters.AddWithValue(parameters[index], ids[index]);
        var result = new Dictionary<uint, string>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[Convert.ToUInt32(reader.GetValue(0))] = Convert.ToString(reader.GetValue(1)) ?? string.Empty;
        return result;
    }

    public async Task<ItemCloneResult> CloneAsync(DatabaseConnectionProfile profile, uint sourceEntry, uint newEntry, string nameSuffix = " Variant", uint? itemSetId = null, CancellationToken cancellationToken = default)
    {
        if (sourceEntry == 0 || newEntry == 0 || sourceEntry == newEntry) throw new ArgumentException("Source and destination item IDs must be distinct positive values.");
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var columns = await ReadTableColumnsAsync(connection, profile.Database, "item_template", cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var entry = RequireColumn(columns, "entry"); var name = RequireColumn(columns, "name"); var set = ResolveColumn(columns, "itemset", "ItemSet");
        var writable = columns.Where(column => !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)).ToArray();
        string sourceName;
        await using (var check = new MySqlCommand($"SELECT {Quote(name)} FROM `item_template` WHERE {Quote(entry)}=@source LIMIT 1", connection, transaction))
        {
            check.Parameters.AddWithValue("@source", sourceEntry); sourceName = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken)) ?? throw new InvalidOperationException($"Source item {sourceEntry} does not exist.");
        }
        await using (var exists = new MySqlCommand($"SELECT 1 FROM `item_template` WHERE {Quote(entry)}=@destination LIMIT 1", connection, transaction))
        {
            exists.Parameters.AddWithValue("@destination", newEntry); if (await exists.ExecuteScalarAsync(cancellationToken) is not null) throw new InvalidOperationException($"Destination item {newEntry} already exists; nothing was replaced.");
        }
        var expressions = writable.Select(column => column.Name.Equals(entry, StringComparison.OrdinalIgnoreCase) ? "@destination" : column.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && nameSuffix.Length > 0 ? $"CONCAT({Quote(column.Name)}, @suffix)" : set is not null && itemSetId is not null && column.Name.Equals(set, StringComparison.OrdinalIgnoreCase) ? "@itemset" : Quote(column.Name)).ToArray();
        var insertSql = $"INSERT INTO `item_template` ({string.Join(",", writable.Select(column => Quote(column.Name)))}) SELECT {string.Join(",", expressions)} FROM `item_template` WHERE {Quote(entry)}=@source";
        await using (var insert = new MySqlCommand(insertSql, connection, transaction) { CommandTimeout = 30 })
        {
            insert.Parameters.AddWithValue("@source", sourceEntry); insert.Parameters.AddWithValue("@destination", newEntry); insert.Parameters.AddWithValue("@suffix", nameSuffix); insert.Parameters.AddWithValue("@itemset", itemSetId ?? 0);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Item clone did not insert exactly one row.");
        }
        var localeRows = await CloneLocaleRowsAsync(connection, transaction, profile.Database, sourceEntry, newEntry, nameSuffix, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var effectiveSet = itemSetId ?? await ReadItemSetAsync(connection, newEntry, set, cancellationToken);
        return new(sourceEntry, newEntry, sourceName, sourceName + nameSuffix, effectiveSet, writable.Length, localeRows);
    }

    private static async Task<int> CloneLocaleRowsAsync(MySqlConnection connection, MySqlTransaction transaction, string database, uint source, uint destination, string suffix, CancellationToken cancellationToken)
    {
        var columns = await ReadTableColumnsAsync(connection, database, "item_template_locale", cancellationToken, required: false, transaction: transaction); if (columns.Count == 0) return 0;
        var key = ResolveColumn(columns, "ID", "entry"); if (key is null) return 0; var name = ResolveColumn(columns, "Name", "name");
        var writable = columns.Where(column => !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)).ToArray();
        var expressions = writable.Select(column => column.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ? "@destination" : name is not null && suffix.Length > 0 && column.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? $"CONCAT({Quote(column.Name)}, @suffix)" : Quote(column.Name));
        var sql = $"INSERT INTO `item_template_locale` ({string.Join(",", writable.Select(column => Quote(column.Name)))}) SELECT {string.Join(",", expressions)} FROM `item_template_locale` WHERE {Quote(key)}=@source";
        await using var command = new MySqlCommand(sql, connection, transaction); command.Parameters.AddWithValue("@source", source); command.Parameters.AddWithValue("@destination", destination); command.Parameters.AddWithValue("@suffix", suffix);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<uint> ReadItemSetAsync(MySqlConnection connection, uint entry, string? setColumn, CancellationToken cancellationToken)
    {
        if (setColumn is null) return 0; await using var command = new MySqlCommand($"SELECT {Quote(setColumn)} FROM `item_template` WHERE `entry`=@entry", connection); command.Parameters.AddWithValue("@entry", entry); return Convert.ToUInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<Dictionary<string, List<string>>> ReadSchemaAsync(MySqlConnection connection, string database, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TABLE_NAME,COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@database ORDER BY TABLE_NAME,ORDINAL_POSITION";
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); await using var command = new MySqlCommand(sql, connection); command.Parameters.AddWithValue("@database", database);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) { var table = reader.GetString(0); if (!result.TryGetValue(table, out var columns)) result[table] = columns = []; columns.Add(reader.GetString(1)); } return result;
    }
    private static async Task<IReadOnlyList<DatabaseColumnCapability>> ReadTableColumnsAsync(MySqlConnection connection, string database, string table, CancellationToken cancellationToken, bool required = true, MySqlTransaction? transaction = null)
    {
        const string sql = "SELECT COLUMN_NAME,DATA_TYPE,COLUMN_TYPE,IS_NULLABLE,COLUMN_DEFAULT,COLUMN_KEY,EXTRA,ORDINAL_POSITION FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@database AND TABLE_NAME=@table ORDER BY ORDINAL_POSITION";
        var result = new List<DatabaseColumnCapability>(); await using var command = new MySqlCommand(sql, connection, transaction); command.Parameters.AddWithValue("@database", database); command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3)=="YES",reader.IsDBNull(4)?null:Convert.ToString(reader.GetValue(4)),reader.GetString(5),reader.GetString(6),reader.GetInt32(7)));
        if (required && result.Count == 0) throw new NotSupportedException($"The selected database has no {table} table."); return result;
    }
    private static string? ResolveTable(Dictionary<string, List<string>> schema, string requested) => schema.Keys.FirstOrDefault(table => table.Equals(requested, StringComparison.OrdinalIgnoreCase));
    private static string? ResolveColumn(IEnumerable<string> columns, params string[] requested) => requested.Select(name => columns.FirstOrDefault(column => column.Equals(name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(column => column is not null);
    private static string? ResolveColumn(IEnumerable<DatabaseColumnCapability> columns, params string[] requested) => requested.Select(name => columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name).FirstOrDefault(column => column is not null);
    private static string RequireColumn(IEnumerable<string> columns, string requested) => ResolveColumn(columns, requested) ?? throw new NotSupportedException($"Required column '{requested}' is missing.");
    private static string RequireColumn(IEnumerable<DatabaseColumnCapability> columns, string requested) => ResolveColumn(columns, requested) ?? throw new NotSupportedException($"Required column '{requested}' is missing.");
    private static string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";
}
