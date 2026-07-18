namespace WoWCrucible.Core;

public sealed record GameObjectDataField(int Index, string Name, string Meaning = "");
public sealed record GameObjectTypeDefinition(int Id, string Name, IReadOnlyList<GameObjectDataField> Fields)
{
    public string Display => $"{Name} [{Id}]";
    public override string ToString() => Display;
    public GameObjectDataField Field(int index) => Fields.FirstOrDefault(field => field.Index == index) ?? new(index, $"Data{index}", "Unused by the stock core for this type; retained as a raw advanced field");
}

public static class GameObjectTypeCatalog
{
    private static GameObjectTypeDefinition Type(int id, string name, params string[] fields) =>
        new(id, name, fields.Select((field, index) => new GameObjectDataField(index, field)).ToArray());

    public static IReadOnlyList<GameObjectTypeDefinition> All { get; } =
    [
        Type(0, "Door", "startOpen", "lockId", "autoCloseTime", "noDamageImmune", "openTextID", "closeTextID", "ignoredByPathing"),
        Type(1, "Button", "startOpen", "lockId", "autoCloseTime", "linkedTrap", "noDamageImmune", "large", "openTextID", "closeTextID", "losOK"),
        Type(2, "Quest giver", "lockId", "questList", "pageMaterial", "gossipID", "customAnim", "noDamageImmune", "openTextID", "losOK", "allowMounted", "large"),
        Type(3, "Chest", "lockId", "lootId", "chestRestockTime", "consumable", "minSuccessOpens", "maxSuccessOpens", "eventId", "linkedTrapId", "questId", "level", "losOK", "leaveLoot", "notInCombat", "logLoot", "openTextID", "groupLootRules", "floatingTooltip"),
        Type(4, "Binder"),
        Type(5, "Generic", "floatingTooltip", "highlight", "serverOnly", "large", "floatOnWater", "questID"),
        Type(6, "Trap", "lockId", "level", "diameter", "spellId", "trapType", "cooldown", "autoCloseTime", "startDelay", "serverOnly", "stealthed", "large", "invisible", "openTextID", "closeTextID", "ignoreTotems"),
        Type(7, "Chair", "slots", "height", "onlyCreatorUse", "triggeredEvent"),
        Type(8, "Spell focus", "focusId", "distance", "linkedTrapId", "serverOnly", "questID", "large", "floatingTooltip"),
        Type(9, "Text", "pageID", "language", "pageMaterial", "allowMounted"),
        Type(10, "Goober / interactive", "lockId", "questId", "eventId", "autoCloseTime", "customAnim", "consumable", "cooldown", "pageId", "language", "pageMaterial", "spellId", "noDamageImmune", "linkedTrapId", "large", "openTextID", "closeTextID", "losOK", "allowMounted", "floatingTooltip", "gossipID", "WorldStateSetsState"),
        Type(11, "Transport", "pauseAtTime", "startOpen", "autoCloseTime", "pause1EventID", "pause2EventID"),
        Type(12, "Area damage", "lockId", "radius", "damageMin", "damageMax", "damageSchool", "autoCloseTime", "openTextID", "closeTextID"),
        Type(13, "Camera", "lockId", "cinematicId", "eventID", "openTextID"),
        Type(14, "Map object"),
        Type(15, "Moving transport", "taxiPathId", "moveSpeed", "accelRate", "startEventID", "stopEventID", "transportPhysics", "mapID", "worldState1", "canBeStopped"),
        Type(16, "Duel arbiter"), Type(17, "Fishing node"),
        Type(18, "Summoning ritual", "requiredParticipants", "spellId", "animationSpell", "ritualPersistent", "casterTargetSpell", "casterTargetSpellTargets", "castersGrouped", "ritualNoTargetCheck"),
        Type(19, "Mailbox"), Type(20, "Do not use"),
        Type(21, "Guard post", "creatureID", "charges"),
        Type(22, "Spell caster", "spellId", "charges", "partyOnly", "allowMounted", "large"),
        Type(23, "Meeting stone", "minimumLevel", "maximumLevel", "areaID"),
        Type(24, "Flag stand", "lockId", "pickupSpell", "radius", "returnAura", "returnSpell", "noDamageImmune", "openTextID", "losOK"),
        Type(25, "Fishing hole", "radius", "lootId", "minimumSuccessOpens", "maximumSuccessOpens", "lockId"),
        Type(26, "Flag drop", "lockId", "eventID", "pickupSpell", "noDamageImmune", "openTextID"),
        Type(27, "Mini game", "gameType"), Type(28, "Do not use 2"),
        Type(29, "Capture point", "radius", "spell", "worldState1", "worldState2", "winEventID1", "winEventID2", "contestedEventID1", "contestedEventID2", "progressEventID1", "progressEventID2", "neutralEventID1", "neutralEventID2", "neutralPercent", "worldState3", "minimumSuperiority", "maximumSuperiority", "minimumTime", "maximumTime", "large", "highlight", "startingValue", "unidirectional"),
        Type(30, "Aura generator", "startOpen", "radius", "auraID1", "conditionID1", "auraID2", "conditionID2", "serverOnly"),
        Type(31, "Dungeon difficulty", "mapID", "difficulty"),
        Type(32, "Barber chair", "chairHeight", "heightOffset"),
        Type(33, "Destructible building", "intactNumHits", "creditProxyCreature", "state1Name", "intactEvent", "damagedDisplayId", "damagedNumHits", "unused6", "unused7", "unused8", "damagedEvent", "destroyedDisplayId", "unused11", "unused12", "unused13", "destroyedEvent", "unused15", "rebuildingTimeSeconds", "unused17", "destructibleData", "rebuildingEvent", "unused20", "unused21", "damageEvent", "unused23"),
        Type(34, "Guild bank"),
        Type(35, "Trap door", "whenToPause", "startOpen", "autoClose")
    ];

    public static GameObjectTypeDefinition Find(int id) => All.FirstOrDefault(type => type.Id == id) ?? new(id, $"Unknown type {id}", []);
}

public sealed record GameObjectSpawnDraft(uint Guid, ushort Map, ushort ZoneId, ushort AreaId, byte SpawnMask, uint PhaseMask,
    float X, float Y, float Z, float Orientation, float Rotation0, float Rotation1, float Rotation2, float Rotation3,
    int RespawnSeconds, byte AnimationProgress, byte State, string ScriptName, string Comment);
public sealed record GameObjectLootDraft(uint Entry, uint Item, int Reference, float Chance, bool QuestRequired, ushort LootMode,
    byte GroupId, byte MinimumCount, byte MaximumCount, string Comment);
public sealed record GameObjectTemplateDraft(uint Entry, int Type, uint DisplayId, string Name, string IconName, string CastBarCaption,
    string UnknownText, float Size, IReadOnlyList<long> Data, string AiName, string ScriptName, GameObjectSpawnDraft? Spawn = null,
    IReadOnlyList<GameObjectLootDraft>? Loot = null, IReadOnlyList<uint>? StartsQuests = null, IReadOnlyList<uint>? EndsQuests = null);

public static class GameObjectTemplateAdapter
{
    public static DatabaseCapabilities CreatePortableCapabilities()
    {
        static DatabaseTableCapability Table(string name, params string[] columns) => new(name, columns.Select((column, index) =>
            new DatabaseColumnCapability(column, column.Contains("name", StringComparison.OrdinalIgnoreCase) || column is "Comment" or "IconName" or "castBarCaption" or "unk1" or "AIName" or "ScriptName" ? "varchar" : "int", "portable", true, "0", index == 0 ? "PRI" : string.Empty, string.Empty, index + 1)).ToArray());
        var templateColumns = new[] { "entry", "type", "displayId", "name", "IconName", "castBarCaption", "unk1", "size" }.Concat(Enumerable.Range(0, 24).Select(index => $"Data{index}")).Concat(["AIName", "ScriptName", "VerifiedBuild"]).ToArray();
        var template = Table("gameobject_template", templateColumns); var spawn = Table("gameobject", "guid", "id", "map", "zoneId", "areaId", "spawnMask", "phaseMask", "position_x", "position_y", "position_z", "orientation", "rotation0", "rotation1", "rotation2", "rotation3", "spawntimesecs", "animprogress", "state", "ScriptName", "VerifiedBuild", "Comment");
        var loot = Table("gameobject_loot_template", "Entry", "Item", "Reference", "Chance", "QuestRequired", "LootMode", "GroupId", "MinCount", "MaxCount", "Comment");
        var starter = Table("gameobject_queststarter", "id", "quest"); var ender = Table("gameobject_questender", "id", "quest");
        return new("portable-current-core", "world", new Dictionary<string, DatabaseTableCapability>(StringComparer.OrdinalIgnoreCase) { [template.Name] = template, [spawn.Name] = spawn, [loot.Name] = loot, [starter.Name] = starter, [ender.Name] = ender });
    }

    public static WorldContentWritePlan CreatePlan(GameObjectTemplateDraft draft, DatabaseCapabilities capabilities)
    {
        if (draft.Entry == 0 || string.IsNullOrWhiteSpace(draft.Name)) throw new InvalidDataException("Gameobject entry and name are required.");
        if (draft.Type is < 0 or > 35) throw new InvalidDataException("Gameobject type must be from 0 through 35.");
        if (!float.IsFinite(draft.Size) || draft.Size <= 0) throw new InvalidDataException("Gameobject size must be a finite positive number.");
        var table = capabilities.FindTable("gameobject_template") ?? throw new NotSupportedException("The connected world database has no gameobject_template table.");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); var omitted = new List<string>();
        Add(table, values, omitted, "entry", draft.Entry, true); Add(table, values, omitted, "type", draft.Type, true); Add(table, values, omitted, "displayId", draft.DisplayId);
        Add(table, values, omitted, "name", draft.Name.Trim(), true); Add(table, values, omitted, "IconName", draft.IconName.Trim()); Add(table, values, omitted, "castBarCaption", draft.CastBarCaption.Trim()); Add(table, values, omitted, "unk1", draft.UnknownText.Trim()); Add(table, values, omitted, "size", draft.Size);
        for (var index = 0; index < 24; index++) Add(table, values, omitted, $"Data{index}", index < draft.Data.Count ? draft.Data[index] : 0);
        Add(table, values, omitted, "AIName", draft.AiName.Trim()); Add(table, values, omitted, "ScriptName", draft.ScriptName.Trim()); Add(table, values, omitted, "VerifiedBuild", 0);
        var rows = new List<WorldSqlRowPlan> { new(table.Name, Key(table, ("entry", (object?)draft.Entry)), values) };
        AddSpawn(draft, capabilities, rows, omitted); AddLoot(draft, capabilities, rows, omitted); AddQuests(draft, capabilities, rows, omitted, "gameobject_queststarter", draft.StartsQuests); AddQuests(draft, capabilities, rows, omitted, "gameobject_questender", draft.EndsQuests);
        return new("Gameobject template", rows, omitted.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void AddSpawn(GameObjectTemplateDraft draft, DatabaseCapabilities capabilities, ICollection<WorldSqlRowPlan> rows, ICollection<string> omitted)
    {
        if (draft.Spawn is not { } spawn) return; if (spawn.Guid == 0) throw new InvalidDataException("A gameobject spawn requires a nonzero GUID so it can be inserted and audited safely.");
        var numbers = new[] { spawn.X, spawn.Y, spawn.Z, spawn.Orientation, spawn.Rotation0, spawn.Rotation1, spawn.Rotation2, spawn.Rotation3 }; if (numbers.Any(value => !float.IsFinite(value))) throw new InvalidDataException("Spawn position, orientation, and rotation values must be finite.");
        if (spawn.SpawnMask == 0 || spawn.PhaseMask == 0) throw new InvalidDataException("Spawn and phase masks cannot be zero.");
        var table = capabilities.FindTable("gameobject") ?? throw new NotSupportedException("A spawn was supplied, but the connected schema has no gameobject table."); var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Add(table, values, omitted, "guid", spawn.Guid, true); Add(table, values, omitted, "id", draft.Entry, true); Add(table, values, omitted, "map", spawn.Map); Add(table, values, omitted, "zoneId", spawn.ZoneId); Add(table, values, omitted, "areaId", spawn.AreaId); Add(table, values, omitted, "spawnMask", spawn.SpawnMask); Add(table, values, omitted, "phaseMask", spawn.PhaseMask);
        Add(table, values, omitted, "position_x", spawn.X); Add(table, values, omitted, "position_y", spawn.Y); Add(table, values, omitted, "position_z", spawn.Z); Add(table, values, omitted, "orientation", spawn.Orientation); Add(table, values, omitted, "rotation0", spawn.Rotation0); Add(table, values, omitted, "rotation1", spawn.Rotation1); Add(table, values, omitted, "rotation2", spawn.Rotation2); Add(table, values, omitted, "rotation3", spawn.Rotation3); Add(table, values, omitted, "spawntimesecs", spawn.RespawnSeconds); Add(table, values, omitted, "animprogress", spawn.AnimationProgress); Add(table, values, omitted, "state", spawn.State); Add(table, values, omitted, "ScriptName", spawn.ScriptName.Trim()); Add(table, values, omitted, "VerifiedBuild", 0); Add(table, values, omitted, "Comment", spawn.Comment.Trim());
        rows.Add(new(table.Name, Key(table, ("guid", (object?)spawn.Guid)), values));
    }

    private static void AddLoot(GameObjectTemplateDraft draft, DatabaseCapabilities capabilities, ICollection<WorldSqlRowPlan> rows, ICollection<string> omitted)
    {
        var loot = (draft.Loot ?? []).Where(item => item.Item != 0 || item.Reference != 0).ToArray(); if (loot.Length == 0) return;
        if (draft.Type is not (3 or 25)) throw new InvalidDataException("Stock AzerothCore loads gameobject loot from chest [3] and fishing-hole [25] types. Change the type or remove the loot rows.");
        var table = capabilities.FindTable("gameobject_loot_template") ?? throw new NotSupportedException("Loot was supplied, but gameobject_loot_template is unavailable.");
        foreach (var item in loot)
        {
            if (item.Entry == 0 || !float.IsFinite(item.Chance) || item.Chance is < 0 or > 100 || item.MinimumCount == 0 || item.MaximumCount < item.MinimumCount) throw new InvalidDataException("Every loot row needs a nonzero entry, chance from 0–100, and valid nonzero count range.");
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); Add(table, values, omitted, "Entry", item.Entry, true); Add(table, values, omitted, "Item", item.Item, true); Add(table, values, omitted, "Reference", item.Reference); Add(table, values, omitted, "Chance", item.Chance); Add(table, values, omitted, "QuestRequired", item.QuestRequired ? 1 : 0); Add(table, values, omitted, "LootMode", item.LootMode); Add(table, values, omitted, "GroupId", item.GroupId); Add(table, values, omitted, "MinCount", item.MinimumCount); Add(table, values, omitted, "MaxCount", item.MaximumCount); Add(table, values, omitted, "Comment", item.Comment.Trim());
            rows.Add(new(table.Name, AvailableKey(table, ("Entry", (object?)item.Entry), ("Item", item.Item), ("Reference", item.Reference), ("GroupId", item.GroupId)), values));
        }
    }

    private static void AddQuests(GameObjectTemplateDraft draft, DatabaseCapabilities capabilities, ICollection<WorldSqlRowPlan> rows, ICollection<string> omitted, string tableName, IReadOnlyList<uint>? quests)
    {
        var ids = (quests ?? []).Where(id => id != 0).Distinct().ToArray(); if (ids.Length == 0) return; if (draft.Type != 2) throw new InvalidDataException("Quest starter/ender links require gameobject type Quest giver [2].");
        var table = capabilities.FindTable(tableName) ?? throw new NotSupportedException($"Quest links were supplied, but {tableName} is unavailable.");
        foreach (var quest in ids) { var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); Add(table, values, omitted, "id", draft.Entry, true); Add(table, values, omitted, "quest", quest, true); rows.Add(new(table.Name, Key(table, ("id", (object?)draft.Entry), ("quest", quest)), values)); }
    }

    private static IReadOnlyDictionary<string, object?> Key(DatabaseTableCapability table, params (string Name, object? Value)[] keys) => keys.ToDictionary(key => table.Find(key.Name)?.Name ?? throw new NotSupportedException($"{table.Name} has no key column '{key.Name}'."), key => key.Value, StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, object?> AvailableKey(DatabaseTableCapability table, params (string Name, object? Value)[] candidates) { var keys = candidates.Where(candidate => table.Find(candidate.Name) is not null).ToArray(); if (keys.Length < 2) throw new NotSupportedException($"{table.Name} does not expose enough identity columns for a safe additive insert."); return Key(table, keys); }
    private static void Add(DatabaseTableCapability table, IDictionary<string, object?> values, ICollection<string> omitted, string name, object? value, bool required = false) { var column = table.Find(name); if (column is null) { if (required) throw new NotSupportedException($"{table.Name} has no required '{name}' column."); omitted.Add($"{table.Name}.{name}"); } else values[column.Name] = value; }
}
