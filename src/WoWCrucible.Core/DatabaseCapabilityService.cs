using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record DatabaseConnectionProfile(string Host, uint Port, string User, string Password, string Database, MySqlSslMode SslMode = MySqlSslMode.Preferred);
public sealed record DatabaseColumnCapability(string Name, string DataType, string ColumnType, bool Nullable, string? DefaultValue, string Key, string Extra, int Ordinal);
public sealed record DatabaseTableCapability(string Name, IReadOnlyList<DatabaseColumnCapability> Columns)
{
    public DatabaseColumnCapability? Find(string name) => Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
public sealed record DatabaseRelationCapability(string Name, string FromTable, string FromColumn, string ToTable, string ToColumn, bool Declared, string Description)
{
    public bool Touches(string table) => FromTable.Equals(table, StringComparison.OrdinalIgnoreCase) || ToTable.Equals(table, StringComparison.OrdinalIgnoreCase);
}
public sealed record DatabaseCapabilities(string ServerVersion, string Database, IReadOnlyDictionary<string, DatabaseTableCapability> Tables,
    IReadOnlyList<DatabaseRelationCapability>? Relations = null)
{
    public DatabaseTableCapability? FindTable(string name) => Tables.TryGetValue(name, out var table) ? table : null;
    public IReadOnlyList<DatabaseTableCapability> DbcOverlayTables => Tables.Values.Where(table => table.Name.EndsWith("_dbc", StringComparison.OrdinalIgnoreCase)).OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<DatabaseRelationCapability> Relationships => Relations ?? [];
}

public sealed class DatabaseCapabilityService
{
    private static readonly string[] RelevantTables = ["item_template", "creature_template", "creature_template_model", "creature", "quest_template", "creature_queststarter", "creature_questender", "gameobject_queststarter", "gameobject_questender", "npc_vendor", "creature_loot_template", "gameobject_template", "gameobject", "spell_proc"];

    public async Task<DatabaseCapabilities> InspectAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        string version;
        await using (var versionCommand = new MySqlCommand("SELECT VERSION()", connection))
            version = Convert.ToString(await versionCommand.ExecuteScalarAsync(cancellationToken)) ?? "Unknown";

        const string sql = """
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE,
                   COLUMN_DEFAULT, COLUMN_KEY, EXTRA, ORDINAL_POSITION
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @database
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """;
        var columns = new Dictionary<string, List<DatabaseColumnCapability>>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new MySqlCommand(sql, connection) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("@database", profile.Database);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                if (!columns.TryGetValue(table, out var list)) columns[table] = list = [];
                list.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4) == "YES",
                    reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5)), reader.GetString(6), reader.GetString(7), reader.GetInt32(8)));
            }
        }
        var tables = columns.ToDictionary(pair => pair.Key, pair => new DatabaseTableCapability(pair.Key, pair.Value), StringComparer.OrdinalIgnoreCase);
        var relations = await ReadRelationsAsync(connection, profile.Database, tables, cancellationToken);
        return new(version, profile.Database, tables, relations);
    }

    private static async Task<IReadOnlyList<DatabaseRelationCapability>> ReadRelationsAsync(MySqlConnection connection, string database,
        IReadOnlyDictionary<string, DatabaseTableCapability> tables, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CONSTRAINT_NAME, TABLE_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @database AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION
            """;
        var result = new List<DatabaseRelationCapability>();
        await using (var command = new MySqlCommand(sql, connection) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("@database", database);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), true, "Declared by the database schema"));
        }
        foreach (var relation in InferredRelations)
        {
            if (!tables.TryGetValue(relation.FromTable, out var from) || from.Find(relation.FromColumn) is null ||
                !tables.TryGetValue(relation.ToTable, out var to) || to.Find(relation.ToColumn) is null) continue;
            if (result.Any(existing => existing.FromTable.Equals(relation.FromTable, StringComparison.OrdinalIgnoreCase) && existing.FromColumn.Equals(relation.FromColumn, StringComparison.OrdinalIgnoreCase) && existing.ToTable.Equals(relation.ToTable, StringComparison.OrdinalIgnoreCase) && existing.ToColumn.Equals(relation.ToColumn, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(relation);
        }
        return result;
    }

    private static readonly DatabaseRelationCapability[] InferredRelations =
    [
        new("crucible_item_vendor", "npc_vendor", "item", "item_template", "entry", false, "AzerothCore item sold by a vendor"),
        new("crucible_item_creature_loot", "creature_loot_template", "Item", "item_template", "entry", false, "AzerothCore creature loot item"),
        new("crucible_creature_spawn", "creature", "id1", "creature_template", "entry", false, "AzerothCore creature spawn template"),
        new("crucible_creature_model", "creature_template_model", "CreatureID", "creature_template", "entry", false, "AzerothCore creature display mapping"),
        new("crucible_creature_vendor", "npc_vendor", "entry", "creature_template", "entry", false, "AzerothCore creature vendor inventory"),
        new("crucible_creature_queststarter", "creature_queststarter", "id", "creature_template", "entry", false, "AzerothCore creature quest starter"),
        new("crucible_creature_questender", "creature_questender", "id", "creature_template", "entry", false, "AzerothCore creature quest ender"),
        new("crucible_creature_startquest", "creature_queststarter", "quest", "quest_template", "ID", false, "Quest started by a creature"),
        new("crucible_creature_endquest", "creature_questender", "quest", "quest_template", "ID", false, "Quest ended by a creature"),
        new("crucible_gameobject_spawn", "gameobject", "id", "gameobject_template", "entry", false, "AzerothCore gameobject spawn template"),
        new("crucible_gameobject_display", "gameobject_template", "displayId", "gameobjectdisplayinfo_dbc", "ID", false, "Client gameobject display definition mirrored by the server"),
        new("crucible_gameobject_queststarter", "gameobject_queststarter", "id", "gameobject_template", "entry", false, "AzerothCore gameobject quest starter"),
        new("crucible_gameobject_questender", "gameobject_questender", "id", "gameobject_template", "entry", false, "AzerothCore gameobject quest ender"),
        new("crucible_gameobject_startquest", "gameobject_queststarter", "quest", "quest_template", "ID", false, "Quest started by a gameobject"),
        new("crucible_gameobject_endquest", "gameobject_questender", "quest", "quest_template", "ID", false, "Quest ended by a gameobject"),
        new("crucible_creature_gossip", "creature_template", "gossip_menu_id", "gossip_menu", "MenuID", false, "Creature default gossip menu"),
        new("crucible_gossip_text", "gossip_menu", "TextID", "npc_text", "ID", false, "Text displayed by a gossip menu"),
        new("crucible_gossip_option_menu", "gossip_menu_option", "MenuID", "gossip_menu", "MenuID", false, "Option belonging to a gossip menu"),
        new("crucible_gossip_option_submenu", "gossip_menu_option", "ActionMenuID", "gossip_menu", "MenuID", false, "Submenu opened by a gossip option"),
        new("crucible_creature_trainer", "creature_default_trainer", "CreatureId", "creature_template", "entry", false, "Creature assigned to a normalized trainer"),
        new("crucible_default_trainer", "creature_default_trainer", "TrainerId", "trainer", "Id", false, "Normalized trainer assigned to a creature"),
        new("crucible_trainer_spell", "trainer_spell", "TrainerId", "trainer", "Id", false, "Spell taught by a normalized trainer"),
        new("crucible_legacy_trainer", "npc_trainer", "ID", "creature_template", "entry", false, "Legacy trainer spell attached directly to a creature"),
        new("crucible_achievement_item", "achievement_reward", "ItemID", "item_template", "entry", false, "Item granted by an achievement reward"),
        new("crucible_character_start_item", "playercreateinfo_item", "itemid", "item_template", "entry", false, "Item granted to a newly created character"),
        new("crucible_gameobject_loot_item", "gameobject_loot_template", "Item", "item_template", "entry", false, "Item awarded by gameobject loot"),
        new("crucible_item_loot_item", "item_loot_template", "Item", "item_template", "entry", false, "Item awarded by opening another item"),
        new("crucible_mail_loot_item", "mail_loot_template", "Item", "item_template", "entry", false, "Item awarded by mail loot"),
        new("crucible_pickpocket_loot_item", "pickpocketing_loot_template", "Item", "item_template", "entry", false, "Item awarded by pickpocketing"),
        new("crucible_skinning_loot_item", "skinning_loot_template", "Item", "item_template", "entry", false, "Item awarded by skinning"),
        new("crucible_disenchant_loot_item", "disenchant_loot_template", "Item", "item_template", "entry", false, "Item awarded by disenchanting"),
        new("crucible_fishing_loot_item", "fishing_loot_template", "Item", "item_template", "entry", false, "Item awarded by fishing"),
        new("crucible_spell_loot_item", "spell_loot_template", "Item", "item_template", "entry", false, "Item awarded by spell loot"),
        new("crucible_prospecting_loot_item", "prospecting_loot_template", "Item", "item_template", "entry", false, "Item awarded by prospecting"),
        new("crucible_milling_loot_item", "milling_loot_template", "Item", "item_template", "entry", false, "Item awarded by milling"),
        new("crucible_player_loot_item", "player_loot_template", "Item", "item_template", "entry", false, "Item awarded by player loot"),
        new("crucible_quest_start_item", "quest_template", "StartItem", "item_template", "entry", false, "Item that starts or accompanies a quest"),
        new("crucible_quest_reward_item_1", "quest_template", "RewardItem1", "item_template", "entry", false, "Guaranteed quest reward item slot 1"),
        new("crucible_quest_reward_item_2", "quest_template", "RewardItem2", "item_template", "entry", false, "Guaranteed quest reward item slot 2"),
        new("crucible_quest_reward_item_3", "quest_template", "RewardItem3", "item_template", "entry", false, "Guaranteed quest reward item slot 3"),
        new("crucible_quest_reward_item_4", "quest_template", "RewardItem4", "item_template", "entry", false, "Guaranteed quest reward item slot 4"),
        new("crucible_quest_choice_item_1", "quest_template", "RewardChoiceItemID1", "item_template", "entry", false, "Optional quest reward item slot 1"),
        new("crucible_quest_choice_item_2", "quest_template", "RewardChoiceItemID2", "item_template", "entry", false, "Optional quest reward item slot 2"),
        new("crucible_quest_choice_item_3", "quest_template", "RewardChoiceItemID3", "item_template", "entry", false, "Optional quest reward item slot 3"),
        new("crucible_quest_choice_item_4", "quest_template", "RewardChoiceItemID4", "item_template", "entry", false, "Optional quest reward item slot 4"),
        new("crucible_quest_choice_item_5", "quest_template", "RewardChoiceItemID5", "item_template", "entry", false, "Optional quest reward item slot 5"),
        new("crucible_quest_choice_item_6", "quest_template", "RewardChoiceItemID6", "item_template", "entry", false, "Optional quest reward item slot 6"),
        new("crucible_quest_drop_item_1", "quest_template", "ItemDrop1", "item_template", "entry", false, "Quest objective drop item slot 1"),
        new("crucible_quest_drop_item_2", "quest_template", "ItemDrop2", "item_template", "entry", false, "Quest objective drop item slot 2"),
        new("crucible_quest_drop_item_3", "quest_template", "ItemDrop3", "item_template", "entry", false, "Quest objective drop item slot 3"),
        new("crucible_quest_drop_item_4", "quest_template", "ItemDrop4", "item_template", "entry", false, "Quest objective drop item slot 4"),
        new("crucible_item_display", "item_template", "displayid", "itemdisplayinfo_dbc", "ID", false, "Client display used by an item template"),
        new("crucible_item_set", "item_template", "ItemSet", "itemset_dbc", "ID", false, "Client item set containing this item"),
        new("crucible_item_required_spell", "item_template", "requiredspell", "spell_dbc", "ID", false, "Spell required to use an item"),
        new("crucible_item_spell_1", "item_template", "spellid_1", "spell_dbc", "ID", false, "Item spell slot 1"),
        new("crucible_item_spell_2", "item_template", "spellid_2", "spell_dbc", "ID", false, "Item spell slot 2"),
        new("crucible_item_spell_3", "item_template", "spellid_3", "spell_dbc", "ID", false, "Item spell slot 3"),
        new("crucible_item_spell_4", "item_template", "spellid_4", "spell_dbc", "ID", false, "Item spell slot 4"),
        new("crucible_item_spell_5", "item_template", "spellid_5", "spell_dbc", "ID", false, "Item spell slot 5"),
        new("crucible_quest_next", "quest_template", "RewardNextQuest", "quest_template", "ID", false, "Next quest in a quest chain"),
        new("crucible_quest_reward_spell", "quest_template", "RewardSpell", "spell_dbc", "ID", false, "Spell learned or cast as a quest reward"),
        new("crucible_quest_display_spell", "quest_template", "RewardDisplaySpell", "spell_dbc", "ID", false, "Spell displayed or cast for a quest reward"),
        new("crucible_quest_reward_title", "quest_template", "RewardTitle", "chartitles_dbc", "ID", false, "Character title awarded by a quest"),
        new("crucible_creature_kill_credit_1", "creature_template", "KillCredit1", "creature_template", "entry", false, "Alternate creature kill credit slot 1"),
        new("crucible_creature_kill_credit_2", "creature_template", "KillCredit2", "creature_template", "entry", false, "Alternate creature kill credit slot 2"),
        new("crucible_creature_loot_pool", "creature_template", "lootid", "creature_loot_template", "Entry", false, "Creature loot pool"),
        new("crucible_creature_pickpocket_pool", "creature_template", "pickpocketloot", "pickpocketing_loot_template", "Entry", false, "Creature pickpocket loot pool"),
        new("crucible_creature_skinning_pool", "creature_template", "skinloot", "skinning_loot_template", "Entry", false, "Creature skinning loot pool"),
        new("crucible_creature_faction", "creature_template", "faction", "factiontemplate_dbc", "ID", false, "Creature faction template"),
        new("crucible_creature_family", "creature_template", "family", "creaturefamily_dbc", "ID", false, "Creature family"),
        new("crucible_creature_pet_spells", "creature_template", "PetSpellDataId", "creaturespelldata_dbc", "ID", false, "Creature pet spell data"),
        new("crucible_pet_levelstats_creature", "pet_levelstats", "creature_entry", "creature_template", "entry", false, "Pet stats owner creature template"),
        new("crucible_pet_name_creature", "pet_name_generation", "entry", "creature_template", "entry", false, "Generated pet-name creature template"),
        new("crucible_pet_name_locale_base", "pet_name_generation_locale", "ID", "pet_name_generation", "id", false, "Localized pet-name source fragment"),
        new("crucible_pet_name_locale_creature", "pet_name_generation_locale", "Entry", "creature_template", "entry", false, "Localized generated pet-name creature template"),
        new("crucible_spell_pet_aura_trigger", "spell_pet_auras", "spell", "spell_dbc", "ID", false, "Spell controlling a pet aura mapping"),
        new("crucible_spell_pet_aura_pet", "spell_pet_auras", "pet", "creature_template", "entry", false, "Pet creature template receiving the aura; zero means every pet"),
        new("crucible_spell_pet_aura_effect", "spell_pet_auras", "aura", "spell_dbc", "ID", false, "Aura applied to the selected pet"),
        new("crucible_creature_vehicle", "creature_template", "VehicleId", "vehicle_dbc", "ID", false, "Creature vehicle definition"),
        new("crucible_creature_display", "creature_template_model", "CreatureDisplayID", "creaturedisplayinfo_dbc", "ID", false, "Client display assigned to a creature model slot"),
        new("crucible_spell_proc_spell", "spell_proc", "SpellId", "spell_dbc", "ID", false, "Server proc override for a spell"),
        new("crucible_spell_script_spell", "spell_script_names", "spell_id", "spell_dbc", "ID", false, "Server script attached to a spell"),
        new("crucible_spell_group_spell", "spell_group", "spell_id", "spell_dbc", "ID", false, "Spell belonging to a spell group"),
        new("crucible_spell_area_spell", "spell_area", "spell", "spell_dbc", "ID", false, "Spell controlled by an area rule"),
        new("crucible_spell_area_aura", "spell_area", "aura_spell", "spell_dbc", "ID", false, "Aura required by an area rule"),
        new("crucible_spell_link_trigger", "spell_linked_spell", "spell_trigger", "spell_dbc", "ID", false, "Trigger of a linked-spell rule; negative IDs require semantic review"),
        new("crucible_spell_link_effect", "spell_linked_spell", "spell_effect", "spell_dbc", "ID", false, "Effect of a linked-spell rule; negative IDs require semantic review"),
        new("crucible_trainer_spell_definition", "trainer_spell", "SpellId", "spell_dbc", "ID", false, "Spell taught by a normalized trainer"),
        new("crucible_legacy_trainer_spell_definition", "npc_trainer", "SpellID", "spell_dbc", "ID", false, "Spell taught by a legacy trainer"),
        new("crucible_character_start_spell", "playercreateinfo_spell_custom", "Spell", "spell_dbc", "ID", false, "Spell granted to a newly created character"),
        new("crucible_spell_cast_time", "spell_dbc", "CastingTimeIndex", "spellcasttimes_dbc", "ID", false, "Spell casting-time definition"),
        new("crucible_spell_duration", "spell_dbc", "DurationIndex", "spellduration_dbc", "ID", false, "Spell duration definition"),
        new("crucible_spell_range", "spell_dbc", "RangeIndex", "spellrange_dbc", "ID", false, "Spell range definition"),
        new("crucible_spell_next", "spell_dbc", "ModalNextSpell", "spell_dbc", "ID", false, "Modal follow-up spell"),
        new("crucible_spell_visual_1", "spell_dbc", "SpellVisualID_1", "spellvisual_dbc", "ID", false, "Primary spell visual"),
        new("crucible_spell_visual_2", "spell_dbc", "SpellVisualID_2", "spellvisual_dbc", "ID", false, "Secondary spell visual"),
        new("crucible_spell_difficulty", "spell_dbc", "SpellDifficultyID", "spelldifficulty_dbc", "ID", false, "Spell difficulty mapping"),
        new("crucible_spell_reagent_1", "spell_dbc", "Reagent_1", "item_template", "entry", false, "Spell reagent slot 1"),
        new("crucible_spell_reagent_2", "spell_dbc", "Reagent_2", "item_template", "entry", false, "Spell reagent slot 2"),
        new("crucible_spell_reagent_3", "spell_dbc", "Reagent_3", "item_template", "entry", false, "Spell reagent slot 3"),
        new("crucible_spell_reagent_4", "spell_dbc", "Reagent_4", "item_template", "entry", false, "Spell reagent slot 4"),
        new("crucible_spell_reagent_5", "spell_dbc", "Reagent_5", "item_template", "entry", false, "Spell reagent slot 5"),
        new("crucible_spell_reagent_6", "spell_dbc", "Reagent_6", "item_template", "entry", false, "Spell reagent slot 6"),
        new("crucible_spell_reagent_7", "spell_dbc", "Reagent_7", "item_template", "entry", false, "Spell reagent slot 7"),
        new("crucible_spell_reagent_8", "spell_dbc", "Reagent_8", "item_template", "entry", false, "Spell reagent slot 8"),
        new("crucible_spell_created_item_1", "spell_dbc", "EffectItemType_1", "item_template", "entry", false, "Item created or referenced by spell effect slot 1"),
        new("crucible_spell_created_item_2", "spell_dbc", "EffectItemType_2", "item_template", "entry", false, "Item created or referenced by spell effect slot 2"),
        new("crucible_spell_created_item_3", "spell_dbc", "EffectItemType_3", "item_template", "entry", false, "Item created or referenced by spell effect slot 3"),
        new("crucible_spell_trigger_1", "spell_dbc", "EffectTriggerSpell_1", "spell_dbc", "ID", false, "Spell triggered by effect slot 1"),
        new("crucible_spell_trigger_2", "spell_dbc", "EffectTriggerSpell_2", "spell_dbc", "ID", false, "Spell triggered by effect slot 2"),
        new("crucible_spell_trigger_3", "spell_dbc", "EffectTriggerSpell_3", "spell_dbc", "ID", false, "Spell triggered by effect slot 3")
    ];

    public static string BuildConnectionString(DatabaseConnectionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.User) || string.IsNullOrWhiteSpace(profile.Database))
            throw new ArgumentException("Host, user, and database are required.");
        return new MySqlConnectionStringBuilder
        {
            Server = profile.Host, Port = profile.Port, UserID = profile.User, Password = profile.Password,
            Database = profile.Database, SslMode = profile.SslMode, ConnectionTimeout = 5,
            DefaultCommandTimeout = 15, AllowUserVariables = false, Pooling = true
        }.ConnectionString;
    }

    public static IReadOnlyList<string> ExpectedTables => RelevantTables;
}
