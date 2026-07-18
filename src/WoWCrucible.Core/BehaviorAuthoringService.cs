using System.Globalization;

namespace WoWCrucible.Core;

public sealed record SemanticValue(long Value, string Name, string Meaning = "")
{
    public override string ToString() => $"{Name} [{Value}]";
}

public sealed record BehaviorDomainDefinition(string Id, string Display, string TableName, string Description)
{
    public override string ToString() => Display;
}

public sealed record BehaviorPortableDraft(string Domain, IReadOnlyDictionary<string, string?> Values);

public static class BehaviorDomainCatalog
{
    public static IReadOnlyList<BehaviorDomainDefinition> All { get; } =
    [
        new("gossip-menu", "Gossip menu", "gossip_menu", "Connect a menu ID to an NPC text block."),
        new("gossip-option", "Gossip option", "gossip_menu_option", "Create a selectable response, service, submenu, POI, or coded prompt."),
        new("npc-text", "NPC dialogue", "npc_text", "Author all eight weighted text/emote variants used by gossip menus."),
        new("trainer", "Trainer definition", "trainer", "Define the normalized trainer type, requirement, and greeting."),
        new("trainer-spell", "Trainer spell", "trainer_spell", "Add a spell and its skill, ability, level, and money requirements."),
        new("trainer-creature", "Creature trainer link", "creature_default_trainer", "Assign a normalized trainer definition to a creature template."),
        new("legacy-trainer-spell", "Legacy NPC trainer spell", "npc_trainer", "Edit the older per-creature trainer layout used by legacy cores."),
        new("condition", "Condition", "conditions", "Gate loot, gossip, quests, vendors, SmartAI, and other content."),
        new("smartai", "SmartAI rule", "smart_scripts", "Define a complete event → action → target SmartAI row without hiding raw parameters.")
    ];

    public static BehaviorDomainDefinition Find(string idOrTable) => All.FirstOrDefault(item =>
        item.Id.Equals(idOrTable, StringComparison.OrdinalIgnoreCase) || item.TableName.Equals(idOrTable, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown behavior domain '{idOrTable}'.");
}

public static class BehaviorSemanticCatalog
{
    public static IReadOnlyList<SemanticValue> Choices(string table, string column) => (table.ToLowerInvariant(), column.ToLowerInvariant()) switch
    {
        ("gossip_menu_option", "optionicon") => GossipIcons,
        ("gossip_menu_option", "optiontype") => GossipOptions,
        ("trainer", "type") => TrainerTypes,
        ("conditions", "sourcetypeorreferenceid") => ConditionSources,
        ("conditions", "conditiontypeorreference") => ConditionTypes,
        ("smart_scripts", "source_type") => SmartSources,
        ("smart_scripts", "event_type") => SmartEvents,
        ("smart_scripts", "action_type") => SmartActions,
        ("smart_scripts", "target_type") => SmartTargets,
        _ => []
    };

    public static string Describe(string table, string column) => (table.ToLowerInvariant(), column.ToLowerInvariant()) switch
    {
        ("gossip_menu", "menuid") => "Menu identity referenced by creature_template.gossip_menu_id or another option's ActionMenuID.",
        ("gossip_menu", "textid") => "NPC text ID displayed for this menu.",
        ("gossip_menu_option", "actionmenuid") => "Next menu to open; 0 performs no submenu transition.",
        ("gossip_menu_option", "actionpoiid") => "Point-of-interest record shown when selected.",
        ("gossip_menu_option", "optionnpcflag") => "Required creature npcflag bitmask for this service option.",
        ("trainer", "requirement") => "Class ID for class trainers or skill-line ID for profession trainers.",
        ("trainer_spell", "reqability1") or ("trainer_spell", "reqability2") or ("trainer_spell", "reqability3") => "A prerequisite spell; zero means unused.",
        ("conditions", "elsegroup") => "Rows in the same ElseGroup are ANDed; different ElseGroup values form OR branches.",
        ("conditions", "negativecondition") => "1 reverses the result of this condition.",
        ("smart_scripts", "entryorguid") => "Positive template entry, negative spawn GUID, or action-list ID depending on source_type.",
        ("smart_scripts", "link") => "ID of a SMART_EVENT_LINK row executed after this action; zero disables linking.",
        ("smart_scripts", "event_phase_mask") => "Bitmask of SmartAI phases in which the event is active; zero means always.",
        ("smart_scripts", "event_chance") => "Chance from 0–100 that the event action runs.",
        ("smart_scripts", "event_flags") => "Repeatability, difficulty, debug, and other SmartAI event flags.",
        _ when column.StartsWith("event_param", StringComparison.OrdinalIgnoreCase) => "Meaning depends on the selected event type; the decoded event tooltip lists its parameter order.",
        _ when column.StartsWith("action_param", StringComparison.OrdinalIgnoreCase) => "Meaning depends on the selected action type; the decoded action tooltip lists its parameter order.",
        _ when column.StartsWith("target_param", StringComparison.OrdinalIgnoreCase) => "Meaning depends on the selected target type; the decoded target tooltip lists its parameter order.",
        _ => string.Empty
    };

    public static IReadOnlyList<SemanticValue> GossipIcons { get; } = Parse("""
0|Chat bubble|White chat bubble
1|Vendor|Brown bag
2|Taxi|Flight marker
3|Trainer|Brown book
4|Interact|Golden interaction wheel
5|Interact alternate|Golden interaction wheel alternate
6|Money bag|Bag with coin
7|Talk|Chat bubble with ellipsis
8|Tabard|White tabard
9|Battle|Crossed swords
10|Dot|Yellow point
11|Chat 11|
12|Chat 12|
13|Chat 13|
14|Invalid 14|Do not use in 3.3.5a
15|Invalid 15|Do not use in 3.3.5a
16|Chat 16|
17|Chat 17|
18|Chat 18|
19|Chat 19|
20|Chat 20|
""");

    public static IReadOnlyList<SemanticValue> GossipOptions { get; } = Parse("""
0|None|
1|Gossip|Normal scripted/dialogue choice
2|Quest giver|
3|Vendor|
4|Taxi vendor|
5|Trainer|
6|Spirit healer|
7|Spirit guide|
8|Innkeeper|
9|Banker|
10|Petitioner|
11|Tabard designer|
12|Battleground|
13|Auctioneer|
14|Stable pet|
15|Armorer|
16|Unlearn talents|
17|Unlearn pet talents|
18|Learn dual specialization|
19|Outdoor PvP|
20|Dual-specialization information|
""");

    public static IReadOnlyList<SemanticValue> TrainerTypes { get; } = Parse("""
0|Class|Requirement is a class ID
1|Mount|Requirement is a race/faction-related trainer requirement
2|Profession|Requirement is a skill-line ID
3|Pet|Pet training
""");

    public static IReadOnlyList<SemanticValue> ConditionTypes { get; } = Parse("""
0|Always true|No condition
1|Aura|spell, effect index, use target
2|Item|item, count, include bank
3|Item equipped|item
4|Zone|zone
5|Reputation rank|faction, rank mask
6|Team|469 Alliance or 67 Horde
7|Skill|skill, value
8|Quest rewarded|quest
9|Quest active|quest
10|Drunken state|state
11|World state|index, value
12|Active event|event
13|Instance info|entry, data, type
14|Quest not taken|quest
15|Class|class
16|Race|race
17|Achievement|achievement
18|Title|title
19|Spawn mask|mask
20|Gender|gender
21|Unit state|state mask
22|Map|map
23|Area|area
24|Creature type|type
25|Known spell|spell
26|Phase mask|mask
27|Level|level, comparison
28|Quest complete|quest
29|Nearby creature|entry, distance, dead
30|Nearby gameobject|entry, distance, state
31|Object entry/GUID|type, entry, guid
32|Object type mask|mask
33|Relation to target|target, relation
34|Reaction to target|target, rank mask
35|Distance to target|target, distance, comparison
36|Alive|
37|Health value|value, comparison
38|Health percent|percent, comparison
39|Realm achievement|achievement
40|In water|
41|Terrain swap|Not for 3.3.5a
42|Stand state|state type, state
43|Daily quest done|quest
44|Charmed|
45|Pet type|mask
46|On taxi|
47|Quest state|quest, state mask
48|Quest objective progress|quest, objective index, count
49|Difficulty|difficulty
101|Quest satisfies exclusive group|quest
102|Has aura type|aura type
103|World script|condition ID, state
104|AI data|data ID, value
105|Queued random dungeon|check difficulty, difficulty
106|Unit in combat|
""");

    public static IReadOnlyList<SemanticValue> ConditionSources { get; } = Parse("""
0|None|
1|Creature loot|
2|Disenchant loot|
3|Fishing loot|
4|Gameobject loot|
5|Item loot|
6|Mail loot|
7|Milling loot|
8|Pickpocket loot|
9|Prospecting loot|
10|Reference loot|
11|Skinning loot|
12|Spell loot|
13|Spell implicit target|
14|Gossip menu|
15|Gossip menu option|
16|Creature vehicle|
17|Spell|
18|Spell click event|
19|Quest available|
20|Gossip hello|
21|Vehicle spell|
22|Smart event|
23|NPC vendor|
24|Spell proc|
25|Terrain swap|Not for 3.3.5a
26|Phase|Not for 3.3.5a
27|Graveyard|Not for 3.3.5a
28|Player loot|
29|Creature respawn|
30|Object visibility|
""");

    public static IReadOnlyList<SemanticValue> SmartSources { get; } = Parse("""
0|Creature|
1|Gameobject|
2|Area trigger|
3|Event|
4|Gossip|
5|Quest|
6|Spell|
7|Transport|
8|Instance|
9|Timed action list|
""");

    public static IReadOnlyList<SemanticValue> SmartEvents { get; } = Parse("""
0|Update in combat|initial min/max, repeat min/max
1|Update out of combat|initial min/max, repeat min/max
2|Health percent|minimum, maximum, repeat min/max
3|Mana percent|minimum, maximum, repeat min/max
4|Aggro|
5|Kill|cooldown min/max, player only, creature entry
6|Death|
7|Evade|
8|Spell hit|spell, school, cooldown min/max
9|Range|min/max, repeat min/max, range min/max
10|Out-of-combat line of sight|hostility, range, cooldown min/max, player only
11|Respawn|type, map, zone
12|Target health percent|min/max, repeat min/max
13|Victim casting|repeat min/max, spell
14|Friendly health|deficit, radius, repeat min/max
15|Friendly crowd controlled|radius, repeat min/max
16|Friendly missing buff|spell, radius, repeat min/max, combat only
17|Summoned unit|creature, cooldown min/max
18|Target mana percent|min/max, repeat min/max
19|Accepted quest|quest, cooldown min/max
20|Reward quest|quest, cooldown min/max
21|Reached home|
22|Receive emote|emote, cooldown min/max
23|Has aura|spell, stacks, repeat min/max
24|Target buffed|spell, stacks, repeat min/max
25|Reset|
26|In-combat line of sight|hostility, range, cooldown min/max, player only
27|Passenger boarded|cooldown min/max
28|Passenger removed|cooldown min/max
29|Charmed|on remove
30|Charmed target|
31|Spell hit target|spell, school, cooldown min/max
32|Damaged|min/max damage, cooldown min/max
33|Damaged target|min/max damage, cooldown min/max
34|Movement inform|movement type, point, path
35|Summon despawned|entry, cooldown min/max
36|Corpse removed|
37|AI initialized|
38|Data set|ID, value, cooldown min/max
39|Escort start|point, path
40|Escort reached|point, path
41|Transport add player|
42|Transport add creature|entry
43|Transport remove player|
44|Transport relocate|point
45|Instance player enter|team, cooldown min/max
46|Area trigger|trigger
47|Quest accepted|
48|Quest objective complete|
49|Quest completion|
50|Quest rewarded|
51|Quest failed|
52|Text over|text group, creature
53|Receive heal|min/max, cooldown min/max
54|Just summoned|
55|Escort paused|point, path
56|Escort resumed|point, path
57|Escort stopped|point, path
58|Escort ended|point, path
59|Timed event triggered|ID
60|Update|initial min/max, repeat min/max
61|Link|internal linked action
62|Gossip select|menu, action
63|Just created|
64|Gossip hello|filter
65|Follow completed|
66|Event phase changed|phase mask
67|Behind target|min/max, repeat min/max, range min/max
68|Game event start|event
69|Game event end|event
70|Gameobject state changed|state
71|Gameobject event inform|event
72|Action done|event
73|Spell click|
74|Friendly health percent|min/max, repeat min/max, percent, range
75|Distance to creature|GUID, entry, distance, repeat
76|Distance to gameobject|GUID, entry, distance, repeat
77|Counter set|ID, value, cooldown min/max
78|Scene start|Not for 3.3.5a
79|Scene trigger|Not for 3.3.5a
80|Scene cancel|Not for 3.3.5a
81|Scene complete|Not for 3.3.5a
82|Summoned unit dies|creature, cooldown min/max
101|Near players|min count, radius, first timer, repeat min/max
102|Near players negation|max count, radius, first timer, repeat min/max
103|Near unit|type, entry, count, range, timer
104|Near unit negation|type, entry, count, range, timer
105|Area casting|min/max, repeat min/max, range min/max
106|Area range|min/max, repeat min/max, range min/max
107|Summoned unit evade|creature, cooldown min/max
108|Waypoint reached|point, path
109|Waypoint ended|point, path
110|In melee range|min/max, repeat min/max, distance, invert
""");

    public static IReadOnlyList<SemanticValue> SmartActions { get; } = Parse("""
0|None|
1|Talk|text group, wait duration, invoker, use talk target
2|Set faction|faction
3|Morph|creature entry or model
4|Sound|sound, only self, distance
5|Play emote|emote
6|Fail quest|quest
7|Offer quest|quest, direct add
8|Set reaction|state
9|Activate gameobject|
10|Random emote|emote IDs
11|Cast|spell, cast flags, trigger flags, target limit
12|Summon creature|entry, summon type, duration, attack options, flags
13|Modify single threat percent|percent
14|Modify all threat percent|percent
15|Area explored / event happens|quest
17|Set emote state|emote
18|Set unit flag|flags, field
19|Remove unit flag|flags, field
20|Auto attack|enabled
21|Combat movement|enabled
22|Set event phase|phase
23|Increment event phase|amount
24|Evade|
25|Flee for assist|emote
26|Group event happens|quest
27|Stop combat|
28|Remove aura|spell, charges
29|Follow|distance, angle, end entry, credit, credit type, alive state
30|Random phase|phase IDs
31|Random phase range|min/max
32|Reset gameobject|
33|Killed monster credit|creature
34|Set instance data|field, data
35|Set instance GUID data|field
36|Update template|entry, level
37|Die|delay
38|Combat with zone|range
39|Call for help|radius, emote
40|Set sheath|state
41|Force despawn|timer
42|Set invincibility health|minimum
43|Mount|creature entry or model
44|Set phase mask|mask
45|Set data|field, data
46|Move forward|distance
47|Set visibility|enabled
48|Set active|enabled
49|Attack start|
50|Summon gameobject|entry, despawn, target summon, type
51|Kill unit|
52|Activate taxi|taxi
53|Escort start|run, path, repeat, quest, despawn, reaction
54|Escort pause|time
55|Escort stop|despawn, quest, fail
56|Add item|item, count
57|Remove item|item, count
58|Install AI template|template
59|Set run|enabled
60|Set fly|enabled
61|Set swim|enabled
62|Teleport|map
63|Set counter|ID, value, reset
64|Store target list|variable ID
65|Escort resume|
66|Set orientation|quick, random, angle
67|Create timed event|ID, initial min/max, repeat min/max, chance
68|Play movie|movie
69|Move to position|point, transport, controlled, contact distance
70|Respawn target|force / time
71|Equip|equipment entry, slots
72|Close gossip|
73|Trigger timed event|ID
74|Remove timed event|ID
75|Add aura|spell
76|Override script base object|Dangerous: can crash a misconfigured core
77|Reset script base object|
78|Call script reset|
79|Set ranged movement|distance, angle
80|Call timed action list|ID, stop after combat, timer mode
81|Set NPC flags|flags
82|Add NPC flags|flags
83|Remove NPC flags|flags
84|Simple talk|text group
85|Self cast|spell, cast flags, trigger flags, target limit
86|Cross cast|spell and caster-target parameters
87|Random timed action list|IDs
88|Random timed action-list range|min/max
89|Random move|max distance
90|Set unit bytes|bytes, field
91|Remove unit bytes|bytes, field
92|Interrupt spell|
93|Gameobject custom animation|animation
94|Set dynamic flags|flags
95|Add dynamic flags|flags
96|Remove dynamic flags|flags
97|Jump to position|XY speed, Z speed, self jump
98|Send gossip menu|menu, option
99|Set gameobject loot state|state
100|Send target to target|ID
101|Set home position|
102|Set health regeneration|enabled
103|Set root|enabled
104|Set gameobject flags|flags
105|Add gameobject flags|flags
106|Remove gameobject flags|flags
107|Summon creature group|group, attack options
108|Set power|type, amount
109|Add power|type, amount
110|Remove power|type, amount
111|Stop game event|event
112|Start game event|event
113|Start closest waypoint|waypoint IDs
114|Rise up|distance
115|Random sound|sounds, only self, distance
116|Set corpse delay|timer
117|Disable evade|enabled
118|Set gameobject state|state
119|Set can fly|Not supported by current core
120|Remove auras by type|Not supported by current core
121|Set sight distance|distance
122|Flee|time
123|Add threat|positive/negative amount
124|Load equipment|ID
125|Trigger random timed event|min/max
126|Remove all gameobjects|
127|Remove movement|Not supported by current core
128|Play animation kit|Not for 3.3.5a
129|Play scene|Not for 3.3.5a
130|Cancel scene|Not for 3.3.5a
131|Spawn group|group, ignore respawn, force
132|Despawn group|group, delete respawn times
133|Respawn by spawn ID|Not supported by current core
134|Invoker cast|spell, cast flags, trigger flags, target limit
135|Play cinematic|cinematic
136|Set movement speed|movement type, integer, fraction
142|Set health percent|percent
201|Move to target position|point
203|Exit vehicle|
204|Set movement flags|flags
205|Set combat distance|distance
206|Dismount|
207|Set hover|enabled
208|Add immunity|type, ID, value
209|Remove immunity|type, ID, value
210|Fall|
211|Set event flag reset|enabled
212|Stop motion|stop moving, movement expired
213|Disable environment update|
214|Zone under attack|
215|Load grid|
216|Music|sound, only self, type
217|Random music|sounds, only self, type
218|Custom cast|spell, flags, base points
219|Cone summon|entry, duration, ring spacing, row spacing, length, angle
220|Player talk|string, yell
221|Vortex summon|entry, duration, spiral parameters
222|Encounter start|
223|Do action|action ID
224|Attack stop|
225|Set GUID|invoker/base GUID to target
226|Scripted spawn|state and timer parameters
227|Set scale|scale
228|Radial summon|entry, duration, repetitions, angles, distance
229|Play spell visual|visual and impact visual
230|Follow group|state, type, distance
231|Set orientation to target|type and target parameters
232|Waypoint start|path, repeat, source
233|Random waypoint data|paths, repeat
234|Movement stop|
235|Movement pause|timer
236|Movement resume|timer override
237|World script|event, parameter
238|Disable reward|reputation, loot
239|Set animation tier|tier
240|Set gossip menu|menu
241|Summon gameobject group|group
242|Increment data|field, increment
""");

    public static IReadOnlyList<SemanticValue> SmartTargets { get; } = Parse("""
0|None|
1|Self|
2|Victim|
3|Second threat|max distance, player only, power, missing aura
4|Last threat|max distance, player only, power, missing aura
5|Random hostile|max distance, player only, power, missing aura
6|Random hostile except top|max distance, player only, power, missing aura
7|Action invoker|
8|Position|uses target coordinates
9|Creature in range|entry, min/max distance, alive state
10|Creature GUID|GUID, entry
11|Creature in distance|entry, max distance, alive state
12|Stored target|variable ID
13|Gameobject in range|entry, min/max
14|Gameobject GUID|GUID, entry
15|Gameobject in distance|entry, max
16|Invoker party|include pets
17|Player in range|min/max, max count
18|Player in distance|max distance
19|Closest creature|entry, max distance, dead
20|Closest gameobject|entry, max distance
21|Closest player|max distance
22|Invoker vehicle|
23|Owner or summoner|
24|Threat list|max distance, player only
25|Closest enemy|max distance, player only
26|Closest friendly|max distance, player only
27|Loot recipients|
28|Farthest|max distance, player only, line of sight, min distance
29|Vehicle passenger|seat
201|Player with aura|spell, negate, max/min distance
202|Random point|range, amount, center mode
203|Role selection|max range, role mask, resize
204|Summoned creatures|entry
205|Instance storage|index, object type
206|Formation|selection type, entry, exclude self
""");

    private static IReadOnlyList<SemanticValue> Parse(string data)
    {
        var result = new List<SemanticValue>();
        foreach (var line in data.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 3); if (parts.Length < 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
            result.Add(new(value, parts[1], parts.Length > 2 ? parts[2] : string.Empty));
        }
        return result;
    }
}

public static class BehaviorAuthoringAdapter
{
    public static DatabaseTableCapability Table(BehaviorDomainDefinition domain, DatabaseCapabilities? capabilities = null) => capabilities?.FindTable(domain.TableName) ?? PortableTable(domain.TableName);

    public static IReadOnlyDictionary<string, object?> Defaults(DatabaseTableCapability table)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns.Where(column => !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)))
            values[column.Name] = column.DefaultValue ?? (column.Nullable ? null : IsText(column) ? string.Empty : "0");
        Set(values, table, "event_chance", 100); Set(values, table, "VerifiedBuild", 0); Set(values, table, "Type", 2); Set(values, table, "Probability0", 1f);
        switch (table.Name.ToLowerInvariant())
        {
            case "gossip_menu": Set(values, table, "MenuID", 900000); Set(values, table, "TextID", 900000); break;
            case "gossip_menu_option": Set(values, table, "MenuID", 900000); Set(values, table, "OptionText", "New Crucible gossip option"); break;
            case "npc_text": Set(values, table, "ID", 900000); Set(values, table, "text0_0", "New Crucible dialogue"); break;
            case "trainer": Set(values, table, "Id", 900000); Set(values, table, "Greeting", "Hello! Ready for some training?"); break;
            case "trainer_spell": Set(values, table, "TrainerId", 900000); Set(values, table, "SpellId", 1); break;
            case "creature_default_trainer": Set(values, table, "CreatureId", 900000); Set(values, table, "TrainerId", 900000); break;
            case "npc_trainer": Set(values, table, "ID", 900000); Set(values, table, "SpellID", 1); break;
            case "conditions": Set(values, table, "SourceTypeOrReferenceId", 22); Set(values, table, "SourceEntry", 900000); Set(values, table, "ConditionTypeOrReference", 15); Set(values, table, "ConditionValue1", 1); Set(values, table, "Comment", "New Crucible condition"); break;
            case "smart_scripts": Set(values, table, "entryorguid", 900000); Set(values, table, "event_type", 4); Set(values, table, "target_type", 1); Set(values, table, "comment", "New Crucible SmartAI rule"); break;
        }
        return values;
    }

    public static WorldContentWritePlan CreatePlan(BehaviorDomainDefinition domain, DatabaseTableCapability table, IReadOnlyDictionary<string, object?> supplied)
    {
        var unknown = supplied.Keys.Where(name => table.Find(name) is null).ToArray(); if (unknown.Length > 0) throw new InvalidDataException($"Unknown {table.Name} column(s): {string.Join(", ", unknown)}.");
        var values = supplied.Where(pair => table.Find(pair.Key)?.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) != true).ToDictionary(pair => table.Find(pair.Key)!.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).ToArray(); if (primary.Length == 0) throw new NotSupportedException($"{table.Name} has no primary key and cannot be safely authored.");
        var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); foreach (var column in primary) { if (!values.TryGetValue(column.Name, out var value) || value is null || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))) throw new InvalidDataException($"Primary-key field {column.Name} is required."); key[column.Name] = value; }
        Validate(table.Name, values); return new(domain.Display, [new(table.Name, key, values)], []);
    }

    public static string Group(string table, string column)
    {
        if (table.Equals("smart_scripts", StringComparison.OrdinalIgnoreCase)) return column.StartsWith("event_", StringComparison.OrdinalIgnoreCase) ? "Event" : column.StartsWith("action_", StringComparison.OrdinalIgnoreCase) ? "Action" : column.StartsWith("target_", StringComparison.OrdinalIgnoreCase) ? "Target" : "Identity";
        if (table.Equals("npc_text", StringComparison.OrdinalIgnoreCase) && column.Length > 4) { var digit = column.FirstOrDefault(char.IsDigit); if (digit != default) return $"Variant {digit}"; }
        return "Complete row";
    }

    public static DatabaseTableCapability PortableTable(string name)
    {
        static DatabaseColumnCapability C(string name, string type = "int", bool nullable = false, string? value = "0", string key = "", int ordinal = 0) => new(name, type, type, nullable, value, key, string.Empty, ordinal);
        static DatabaseTableCapability T(string name, params DatabaseColumnCapability[] columns) => new(name, columns.Select((column, index) => column with { Ordinal = index + 1 }).ToArray());
        return name.ToLowerInvariant() switch
        {
            "gossip_menu" => T(name, C("MenuID", key: "PRI"), C("TextID", key: "PRI")),
            "gossip_menu_option" => T(name, C("MenuID", key: "PRI"), C("OptionID", "smallint", key: "PRI"), C("OptionIcon"), C("OptionText", "text", true, null), C("OptionBroadcastTextID"), C("OptionType", "tinyint"), C("OptionNpcFlag"), C("ActionMenuID"), C("ActionPoiID"), C("BoxCoded", "tinyint"), C("BoxMoney"), C("BoxText", "text", true, null), C("BoxBroadcastTextID"), C("VerifiedBuild", nullable: true, value: null)),
            "npc_text" => NpcText(name, C, T),
            "trainer" => T(name, C("Id", key: "PRI"), C("Type", "tinyint", value: "2"), C("Requirement"), C("Greeting", "text", true, null), C("VerifiedBuild", nullable: true)),
            "trainer_spell" => T(name, C("TrainerId", key: "PRI"), C("SpellId", key: "PRI"), C("MoneyCost"), C("ReqSkillLine"), C("ReqSkillRank"), C("ReqAbility1"), C("ReqAbility2"), C("ReqAbility3"), C("ReqLevel", "tinyint"), C("VerifiedBuild", nullable: true)),
            "creature_default_trainer" => T(name, C("CreatureId", key: "PRI"), C("TrainerId")),
            "npc_trainer" => T(name, C("ID", key: "PRI"), C("SpellID", key: "PRI"), C("MoneyCost"), C("ReqSkillLine"), C("ReqSkillRank"), C("ReqLevel", "tinyint"), C("ReqSpell")),
            "conditions" => T(name, C("SourceTypeOrReferenceId", key: "PRI"), C("SourceGroup", key: "PRI"), C("SourceEntry", key: "PRI"), C("SourceId", key: "PRI"), C("ElseGroup", key: "PRI"), C("ConditionTypeOrReference", key: "PRI"), C("ConditionTarget", "tinyint", key: "PRI"), C("ConditionValue1", key: "PRI"), C("ConditionValue2", key: "PRI"), C("ConditionValue3", key: "PRI"), C("NegativeCondition", "tinyint"), C("ErrorType"), C("ErrorTextId"), C("ScriptName", "varchar", value: ""), C("Comment", "varchar", true, null)),
            "smart_scripts" => SmartScripts(name, C, T),
            _ => throw new NotSupportedException($"No portable behavior schema exists for {name}.")
        };
    }

    private static DatabaseTableCapability NpcText(string name, Func<string, string, bool, string?, string, int, DatabaseColumnCapability> c, Func<string, DatabaseColumnCapability[], DatabaseTableCapability> t)
    {
        var columns = new List<DatabaseColumnCapability> { c("ID", "int", false, "0", "PRI", 0) };
        for (var slot = 0; slot < 8; slot++) { columns.Add(c($"text{slot}_0", "longtext", true, null, "", 0)); columns.Add(c($"text{slot}_1", "longtext", true, null, "", 0)); columns.Add(c($"BroadcastTextID{slot}", "int", false, "0", "", 0)); columns.Add(c($"lang{slot}", "tinyint", false, "0", "", 0)); columns.Add(c($"Probability{slot}", "float", false, "0", "", 0)); for (var emote = 0; emote < 6; emote++) columns.Add(c($"em{slot}_{emote}", "smallint", false, "0", "", 0)); }
        columns.Add(c("VerifiedBuild", "int", true, null, "", 0)); return t(name, columns.ToArray());
    }

    private static DatabaseTableCapability SmartScripts(string name, Func<string, string, bool, string?, string, int, DatabaseColumnCapability> c, Func<string, DatabaseColumnCapability[], DatabaseTableCapability> t)
    {
        var columns = new List<DatabaseColumnCapability> { c("entryorguid", "int", false, "0", "PRI", 0), c("source_type", "tinyint", false, "0", "PRI", 0), c("id", "smallint", false, "0", "PRI", 0), c("link", "smallint", false, "0", "PRI", 0), c("event_type", "tinyint", false, "0", "", 0), c("event_phase_mask", "smallint", false, "0", "", 0), c("event_chance", "tinyint", false, "100", "", 0), c("event_flags", "smallint", false, "0", "", 0) };
        for (var index = 1; index <= 6; index++) columns.Add(c($"event_param{index}", "int", false, "0", "", 0)); columns.Add(c("action_type", "tinyint", false, "0", "", 0)); for (var index = 1; index <= 6; index++) columns.Add(c($"action_param{index}", "int", false, "0", "", 0)); columns.Add(c("target_type", "tinyint", false, "0", "", 0)); for (var index = 1; index <= 4; index++) columns.Add(c($"target_param{index}", "int", false, "0", "", 0)); foreach (var axis in new[] { "x", "y", "z", "o" }) columns.Add(c($"target_{axis}", "float", false, "0", "", 0)); columns.Add(c("comment", "text", false, "", "", 0)); return t(name, columns.ToArray());
    }

    private static void Validate(string table, IReadOnlyDictionary<string, object?> values)
    {
        long Number(string name) { try { return Convert.ToInt64(values.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value, CultureInfo.InvariantCulture); } catch { return 0; } }
        if (table.Equals("smart_scripts", StringComparison.OrdinalIgnoreCase) && Number("event_chance") is < 0 or > 100) throw new InvalidDataException("SmartAI event_chance must be from 0 to 100.");
        if (table.Equals("smart_scripts", StringComparison.OrdinalIgnoreCase) && Number("entryorguid") == 0) throw new InvalidDataException("SmartAI entryorguid must be nonzero.");
        if (table.Equals("smart_scripts", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Convert.ToString(values.FirstOrDefault(pair => pair.Key.Equals("comment", StringComparison.OrdinalIgnoreCase)).Value, CultureInfo.InvariantCulture))) throw new InvalidDataException("A descriptive SmartAI comment is required.");
        if (table.Equals("conditions", StringComparison.OrdinalIgnoreCase) && Number("NegativeCondition") is < 0 or > 1) throw new InvalidDataException("NegativeCondition must be 0 or 1.");
        if (table.Equals("gossip_menu_option", StringComparison.OrdinalIgnoreCase) && Number("OptionIcon") is < 0 or > 20) throw new InvalidDataException("WotLK gossip OptionIcon must be from 0 to 20.");
        if (table.Equals("gossip_menu_option", StringComparison.OrdinalIgnoreCase) && Number("OptionType") is < 0 or > 20) throw new InvalidDataException("WotLK gossip OptionType must be from 0 to 20.");
    }

    private static bool IsText(DatabaseColumnCapability column) => column.DataType.Contains("char", StringComparison.OrdinalIgnoreCase) || column.DataType.Contains("text", StringComparison.OrdinalIgnoreCase) || column.DataType.Contains("blob", StringComparison.OrdinalIgnoreCase);
    private static void Set(IDictionary<string, object?> values, DatabaseTableCapability table, string name, object value) { if (table.Find(name) is { } column) values[column.Name] = value; }
}
