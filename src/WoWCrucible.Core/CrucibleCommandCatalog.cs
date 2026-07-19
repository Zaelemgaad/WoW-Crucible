namespace WoWCrucible.Core;

public sealed record CrucibleCommandDescriptor(string Id, string Title, string Category, string Description, IReadOnlyList<string> Aliases, string? Shortcut = null)
{
    public override string ToString() => Title;
}

public sealed record CrucibleCommandMatch(CrucibleCommandDescriptor Command, int Score);

/// <summary>One searchable command vocabulary shared by the desktop and CLI.</summary>
public static class CrucibleCommandCatalog
{
    public static IReadOnlyList<CrucibleCommandDescriptor> All { get; } =
    [
        Command("workspace.dbc", "DBC tables", "Workspace", "Open the multi-document decoded/raw DBC and DB2 table editor.", "client tables records wdbc db2 spreadsheet editor"),
        Command("workspace.dbc-layers", "DBC layers & promotion", "Workspace", "Compare base and override tables, promote selected changes, and stage them for a patch.", "overlay compare merge effective dbc promotion base override"),
        Command("workspace.dbd", "DBD schemas & audit", "Workspace", "Inspect WoWDBDefs layouts and validate a client-table corpus against a target build.", "schema definitions wdbx wowdbdefs validation recursive"),
        Command("workspace.projects", "Projects & shared IDs", "Workspace", "Create portable content projects and reserve collision-checked IDs across live SQL, DBCs, and every guided editor.", "project registry allocator allocation occupied collision custom ids fusion content"),
        Command("workspace.items", "Items & sets", "Workspace", "Audit acquisition paths, create or clone items, edit complete rows, tooltips, displays, and item sets.", "item_template weapon armor loot cut unused unobtainable acquisition favorite set heidi easy editor"),
        Command("workspace.creatures", "Creatures & NPCs", "Workspace", "Create and edit creatures, NPC models, spawns, vendors, loot, quests, and trainers.", "creature_template npc pet vendor trainer loot spawn model"),
        Command("workspace.gameobjects", "Gameobjects", "Workspace", "Create and edit all gameobject types, displays, spawns, loot, and quest links.", "gameobject_template gob object chest door fishing spawn display"),
        Command("workspace.quests", "Quests", "Workspace", "Edit complete quest rows, objectives, rewards, text, POIs, and giver relationships.", "quest_template mission objective reward poi giver taker"),
        Command("workspace.pets", "Pets & companions", "Workspace", "Edit pet level stats, generated and localized names, spell aura mappings, and related creature/spell references.", "pet companion minipet summon tame levelstats pet_levelstats pet_name_generation spell_pet_auras"),
        Command("workspace.behaviors", "Behaviors & dialogue", "Workspace", "Author gossip, NPC text, conditions, SmartAI, vendors, and loot rows.", "smart_scripts smartai gossip conditions dialogue npc_text loot vendor behavior"),
        Command("workspace.mpq", "MPQ patches & archives", "Workspace", "Browse, extract, create, update, merge, validate, and deploy small MPQ patches.", "mpq editor stormlib archive patch merge merger extract list tree manifest drag drop"),
        Command("workspace.client", "Client workshop", "Workspace", "Index clients, inspect effective archive layers, extract assets, and plan client/server fusion.", "client data folder patch layers fusion extract index cache deploy"),
        Command("workspace.maps", "Maps & world", "Workspace", "Inspect and edit ADT/WDT/WDL terrain, textures, alpha maps, objects, and WMO scenes.", "noggit map adt wdt wdl terrain world alpha height texture wmo placement"),
        Command("workspace.textures", "Texture lab", "Workspace", "Inspect, decode, encode, validate, and preview BLP textures and mipmaps.", "blp png dxt texture converter alpha mipmaps image"),
        Command("workspace.assets", "Assets & compare", "Workspace", "Compare every provenance layer, make definitive-set decisions, preview M2 models, and export poses.", "asset compare model viewer m2 skin geoset character animation appearance definitive set duplicate"),
        Command("workspace.conversion", "Modern asset conversion", "Workspace", "Inspect modern M2/WMO assets and build immutable, validated downgrade workspaces.", "multiconverter legion cata modern port downgrade m2 wmo conversion"),
        Command("workspace.tools", "Tool inventory", "Workspace", "Audit every local legacy-tool root and its native Crucible replacement status.", "legacy obsolete tools audit consolidation matrix inventory"),
        Command("workspace.server", "Server & SQL", "Workspace", "Detect server paths/configuration, connect SQL automatically, control processes, and plan deployment.", "server core worldserver authserver mysql wsl config connection start stop restart deploy"),
        Command("workspace.sql", "SQL Studio", "Workspace", "Browse every schema/table/field, query and edit exact rows, manage favorites, objects, users, and imports.", "heidi heidisql database mysql query full sql favorite row table schema procedure trigger view users grants", "Ctrl+K, then SQL"),
        Command("workspace.cli-guide", "CLI guide", "Help", "Open the complete copy/paste command reference inside Crucible.", "terminal command line automation help sheet reference"),
        Command("action.open-dbc", "Open a DBC or DB2", "File", "Choose and stage another client table in the multi-document editor.", "open file table db2 wdbc", "Ctrl+O"),
        Command("action.open-m2", "Preview an M2", "File", "Choose an extracted Wrath M2 and resolve its companion SKIN for native preview.", "open model skin character preview"),
        Command("action.save", "Save current DBC", "File", "Atomically save the active client table to its current path.", "write persist current table", "Ctrl+S"),
        Command("action.save-as", "Save current DBC as…", "File", "Atomically save the active table to a new path.", "copy rename output dbc"),
        Command("action.export-rows", "Export decoded DBC rows", "File", "Export selected columns and IDs as CSV, JSON, or JSON Lines.", "csv json jsonl rows columns data export"),
        Command("action.import-rows", "Import structured DBC rows", "File", "Preview and apply keyed CSV/JSON/JSONL changes without losing schema safety.", "csv json jsonl rows data import bulk"),
        Command("action.spell", "Open selected spell workspace", "Editor", "Open the effect-centric editor for the selected Spell.dbc row.", "spell effect ability tooltip clone"),
        Command("action.logs", "Open Crucible logs", "Diagnostics", "Reveal normal crash logs and Devbug diagnostic sessions beside the executable.", "debug devbug crash diagnostics terminal"),
        Command("action.devbug", "Toggle Devbug Mode", "Diagnostics", "Switch live detailed tracing and three-session debug retention on or off.", "debug verbose logging terminal trace"),
        Command("action.back", "Back to previous workspace", "Navigation", "Return through the same-window workspace history, then to the DBC editor.", "close return editor previous escape", "Alt+Left")
    ];

    public static IReadOnlyList<CrucibleCommandMatch> Search(string? query, int maximum = 40)
    {
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        var normalized = Normalize(query ?? string.Empty); var terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All.Select((command, index) => Match(command, index, normalized, terms)).Where(match => match is not null).Select(match => match!)
            .OrderByDescending(match => match.Score).ThenBy(match => match.Command.Id, StringComparer.Ordinal).Take(maximum).ToArray();
    }

    private static CrucibleCommandMatch? Match(CrucibleCommandDescriptor command, int index, string query, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return new(command, 10_000 - index);
        var title = Normalize(command.Title); var category = Normalize(command.Category); var aliases = Normalize(string.Join(' ', command.Aliases));
        var description = Normalize(command.Description); var id = Normalize(command.Id); var haystack = $"{title} {category} {aliases} {description} {id}";
        if (terms.Any(term => !haystack.Contains(term, StringComparison.Ordinal))) return null;
        var score = 1_000 - index;
        if (title == query) score += 20_000;
        else if (title.StartsWith(query, StringComparison.Ordinal)) score += 12_000;
        else if (title.Contains(query, StringComparison.Ordinal)) score += 8_000;
        if (category == query) score += 3_000;
        foreach (var term in terms)
        {
            if (title.Split(' ').Any(word => word == term)) score += 1_000;
            else if (title.Split(' ').Any(word => word.StartsWith(term, StringComparison.Ordinal))) score += 700;
            else if (aliases.Split(' ').Any(word => word == term)) score += 450;
            else if (aliases.Split(' ').Any(word => word.StartsWith(term, StringComparison.Ordinal))) score += 250;
        }
        return new(command, score);
    }

    private static CrucibleCommandDescriptor Command(string id, string title, string category, string description, string aliases, string? shortcut = null) =>
        new(id, title, category, description, aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries), shortcut);

    private static string Normalize(string value)
    {
        var characters = value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray();
        return string.Join(' ', new string(characters).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
