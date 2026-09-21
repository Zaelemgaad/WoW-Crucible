using System.IO.Enumeration;

namespace WoWCrucible.Core;

public enum ToolInventoryStatus { Tracked, Missing, Unassigned }

public sealed record ToolInventoryEntry(string RelativePath, string FullPath, string Scope, ToolInventoryStatus Status, string Capability, string CrucibleDestination)
{
    public bool Exists => Status != ToolInventoryStatus.Missing;
    public override string ToString() => $"{Status} · {RelativePath} · {Capability} → {CrucibleDestination}";
}

public sealed record ToolInventoryReport(string WorkspaceRoot, DateTimeOffset ScannedUtc, IReadOnlyList<ToolInventoryEntry> Entries)
{
    public int Tracked => Entries.Count(entry => entry.Status == ToolInventoryStatus.Tracked);
    public int Missing => Entries.Count(entry => entry.Status == ToolInventoryStatus.Missing);
    public int Unassigned => Entries.Count(entry => entry.Status == ToolInventoryStatus.Unassigned);
}

public static class ToolConsolidationInventoryService
{
    private sealed record Assignment(string Capability, string Destination);
    private sealed record Rule(string Scope, string Pattern, Assignment Assignment);

    private static readonly IReadOnlyDictionary<string, Assignment> RootAssignments = new Dictionary<string, Assignment>(StringComparer.OrdinalIgnoreCase)
    {
        ["acore-cms"] = new("Account, realm, character, server, module, and web administration", "Server administration and deployment API"),
        ["azerothcore-wotlk"] = new("Current AzerothCore schemas, DBC/SQL bindings, commands, reload rules, and migrations", "Versioned AzerothCore adapter"),
        ["TrinityCore-3.3.5"] = new("Current TrinityCore schemas and behavioral differences", "Independent TrinityCore adapter"),
        ["CascLib"] = new("CASC indexing and extraction source", "Unified archive provider"),
        ["client and old core"] = new("Baseline client comparison, fusion evidence, and legacy SQL recovery", "Client/server diff and portable change-plan import"),
        ["Coffee"] = new("ADT/WDT/WDL/WMO/M2/BLP/DBC format and repair workflows", "Native validators and guarded map/asset transforms"),
        ["Current-AzerothCore-Server"] = new("Installed server and live deployment target", "Shared Server & SQL session"),
        ["HeidiSQL"] = new("SQL browsing, querying, schema and account administration", "SQL Studio"),
        ["Keira3"] = new("Guided item, creature, quest, loot, vendor, condition, and SmartAI editing", "Domain creators and shared selectors"),
        ["listfiles"] = new("MPQ/CASC path discovery corpora", "Unified path catalog and dependency resolver"),
        ["mpqeditor_en"] = new("MPQ tree browsing, extraction, creation, and update", "MPQ workspace"),
        ["MultiConverter"] = new("Modern-to-WotLK M2/WMO conversion", "Native staged asset converter"),
        ["MyDbcEditor"] = new("Generic DBC editing and fixed-value names", "DBC/DB2 workbench"),
        ["Noggit 3.2614"] = new("Terrain, water, objects, textures, zones, lighting, and world editing", "Native map/world project"),
        ["StormLib"] = new("MPQ format implementation and compatibility evidence", "Native MPQ engine boundary"),
        ["stormlib_dll"] = new("MPQ runtime compatibility evidence", "Native MPQ engine boundary"),
        ["Tools"] = new("Expanded legacy tool corpus", "Capability-specific Crucible workspaces"),
        ["Tools-MyDbcEditor 1.2.2.42"] = new("Generic DBC editor variant", "DBC/DB2 workbench"),
        ["TrinityCreator"] = new("Beginner item, quest, creature, loot, vendor, and model workflows", "Guided creator layer"),
        ["WDBX.Editor"] = new("Definition-driven DBC/DB2 editing", "DBC/DB2 workbench"),
        ["WDBXEditor"] = new("Definition corpus and generic DBC editing", "Schema providers and DBC/DB2 workbench"),
        ["wiki"] = new("Offline field, flag, enum, command, and database documentation", "Contextual knowledge provider"),
        ["WoW Spell Editor"] = new("Spell search, effects, SQL staging, and DBC export", "Spell workspace"),
        ["WoW-Spell-Editor"] = new("Spell search, effects, and named fields", "Spell workspace"),
        ["WoWDatabaseEditor"] = new("Projects, quest graphs, SmartAI, maps, and data providers", "Portable projects and graph editors"),
        ["WowForge"] = new("Watched empty root", "Rescan when content appears"),
        ["wow-model-viewer"] = new("Character/item/NPC/object preview interaction reference", "Native embedded renderer"),
        ["wowmodelviewer"] = new("Character composition, equipment, geosets, animation, particles, and export", "Native embedded renderer"),
        ["WoWModelViewerWeb"] = new("Miorey web integration wrapper and remote model delivery", "Browser integration reference; distinct from the desktop viewer"),
        ["Zips&rars"] = new("Preserved source archives and backups", "Read-only archive inventory and import planning"),
        ["WoW-Crucible"] = new("Active Crucible source repository", "Current development tree"),
        ["WoW-Crucible-User-Copy"] = new("Historical Crucible copy", "Review for consolidation into the current checkout")
    };

    private static readonly Rule[] ToolRules =
    [
        Direct("Adb_Wdb_Parser*", "ADB/WDB cache parsing and export", "Cache-table provider"), Direct("ADB_WDB_Parser*", "ADB/WDB cache parsing and export", "Cache-table provider"), Direct("WDB Converter*", "WDB conversion", "Cache-table provider"), Direct("WoWParser*", "WoW cache parsing", "Cache-table provider"),
        Direct("ADB-DB2-DBC*", "ADB/DB2/DBC and CSV conversion", "Schema-aware table conversion"), Direct("DB2 Editor*", "DB2 editing", "DBC/DB2 workbench"), Direct("DBC_DB2_Extractor", "DBC/DB2 extraction", "DBC/DB2 workbench"), Direct("SQLtoDB2_Fix*", "SQL-to-DB2 conversion", "Schema-aware table conversion"),
        Direct("AmarothTools", "Client item, NPC, gameobject, listfile, and WMO dependency workflows", "Guided generators and dependency graph"),
        Direct("CASC", "CASC utilities", "Unified archive provider"), Direct("cascview*", "CASC browsing and extraction", "Unified archive provider"), Direct("cascExplorer", "CASC browsing and extraction", "Unified archive provider"), Direct("CSVed*", "CSV table editing", "Schema-aware table workbench"),
        Direct("Legacy*Tools*", "Legacy tool collection", "Individual capability review"), Direct("Obsolete", "Retired tool collection", "Retained reference copies; inspect individual replacement evidence"),
        Direct("MPQExtractor", "MPQ extraction and patch-chain application", "Archive provider; patch-chain compatibility requires separate validation"), Direct("BLPConverter*", "BLP texture conversion", "Texture conversion; output-format coverage requires separate validation"),
        Direct("DBC*", "DBC/DB2 editing, viewing, and conversion", "DBC/DB2 workbench"), Direct("dbc2sql*", "DBC-to-SQL conversion", "Schema-aware table conversion"), Direct("MyDbcEditor*", "Generic DBC editing", "DBC/DB2 workbench"), Direct("WDBXEditor", "Definition-driven DBC editing", "DBC/DB2 workbench"), Direct("WoWDBDefs", "DBD schema corpus", "Build-aware schema provider"),
        Direct("LightMapper", "Light table map and time/color-band visualization", "Map lighting workspace"), Direct("Map", "ADT/WDT/WDL terrain and zone tools", "Native map/world project"), Direct("Models", "M2/MDX conversion, editing, animation, and export", "Native model pipeline"),
        Direct("MPQ", "MPQ utilities", "MPQ workspace"), Direct("MPQEditor*", "MPQ browsing and extraction", "MPQ workspace"), Direct("MPQWorkshop*", "MPQ editing", "MPQ workspace"), Direct("MyWarCraftStudio*", "MPQ and client asset browsing", "MPQ and asset workspace"),
        Direct("Other", "Hex, terminal, login-screen, UI, and research utilities", "Structured inspectors, server console, and UI projects"), Direct("pjdbcEditer*", "SQL-backed DBC editing", "Shared SQL/DBC engine"), Direct("skyboxeditor", "Light and skybox authoring", "Map lighting workspace"), Direct("spell editor*", "Spell authoring", "Spell workspace"), Direct("Tallis", "Plugin/provider concepts", "Extension boundary"), Direct("Textures", "BLP conversion and texture authoring", "Texture Lab"), Direct("wmo editor*", "WMO editing and validation", "WMO workspace"), Direct("WoW Model Viewer*", "Character/item/NPC/object rendering and export", "Native embedded renderer"),
        Nested("AmarothTools", "ClientItem", "Item client records and display dependencies", "Item creator and dependency resolver"), Nested("AmarothTools", "NPCGenerator", "NPC appearance import", "Creature display planner"), Nested("AmarothTools", "GobGenerator", "Bulk gameobject generation", "Gameobject generator"), Nested("AmarothTools", "ListfileCreation", "Listfile generation", "Path catalog"), Nested("AmarothTools", "WMOListFile", "Recursive WMO dependencies", "Asset dependency graph"), Nested("AmarothTools", "AmarothsLauncherRelease", "Amaroth launcher workflow", "Guided generator infrastructure"), Nested("AmarothTools", "AmarothsToolkit", "Amaroth toolkit shell", "Guided generator infrastructure"),
        Nested("Models", "anim porter", "Animation transfer", "Build-aware animation pipeline"), Nested("Models", "M2ModRedux*", "M2 inspect/edit/import/export", "Native model pipeline"), Nested("Models", "MDLVIS*", "MDX model editing", "Native model pipeline"), Nested("Models", "MDX", "MDX tooling", "Native model pipeline"), Nested("Models", "MultiConverter*", "Modern-to-WotLK conversion", "Native staged converter"), Nested("Models", "Scripts", "Model conversion scripts", "Validated conversion recipes"), Nested("Models", "WoW Blender Studio", "Model import/export workflows", "Documented interchange pipeline"),
        Nested("Map", "AdtAdder", "ADT creation and copying", "Map project"), Nested("Map", "ADTGrids", "ADT grid editing", "Map project"), Nested("Map", "FuTa", "Terrain alpha/offset operations", "Map project"), Nested("Map", "GroundEffects", "Ground-effect authoring", "Map project"), Nested("Map", "GruulMeWDT", "WDT creation", "Map project"), Nested("Map", "Noggit*", "Interactive world editing", "Native map/world project"), Nested("Map", "Rius Zone Masher", "Zone/ADT operations", "Map project")
    ];

    public static ToolInventoryReport Scan(string workspaceRoot, bool includeMissing = true)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot); if (!Directory.Exists(workspaceRoot)) throw new DirectoryNotFoundException($"Tool workspace does not exist: {workspaceRoot}");
        var consolidated = IsConsolidatedWorkspace(workspaceRoot);
        var entries = new List<ToolInventoryEntry>(); var foundRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(workspaceRoot, "*", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory); if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || IsLinked(directory)) continue; foundRoots.Add(name);
            if (consolidated && !RootAssignments.ContainsKey(name)) continue;
            entries.Add(RootAssignments.TryGetValue(name, out var assignment) ? Entry(name, directory, "Workspace", ToolInventoryStatus.Tracked, assignment) : Entry(name, directory, "Workspace", ToolInventoryStatus.Unassigned, Unassigned()));
        }
        if (includeMissing && !consolidated) foreach (var pair in RootAssignments.Where(pair => !foundRoots.Contains(pair.Key))) entries.Add(Entry(pair.Key, Path.Combine(workspaceRoot, pair.Key), "Workspace", ToolInventoryStatus.Missing, pair.Value));
        var toolsRoot = Path.Combine(workspaceRoot, "Tools");
        if (Directory.Exists(toolsRoot) && !IsLinked(toolsRoot))
        {
            ScanScope(toolsRoot, "Tools", SearchOption.TopDirectoryOnly);
            foreach (var file in Directory.EnumerateFiles(toolsRoot).Where(path => !IsLinked(path)))
            {
                if (Path.GetExtension(file).ToLowerInvariant() is not (".exe" or ".msi" or ".zip" or ".rar" or ".7z" or ".py" or ".cmd" or ".ps1")) continue;
                entries.Add(Entry(Path.GetRelativePath(workspaceRoot, file), file, "Tools", ToolInventoryStatus.Unassigned, Unassigned()));
            }
            ScanLegacyGroups(toolsRoot);
            foreach (var collection in Directory.EnumerateDirectories(toolsRoot).Where(path => !IsLinked(path)))
            {
                var name = Path.GetFileName(collection);
                if (name.StartsWith("Legacy", StringComparison.OrdinalIgnoreCase) && name.Contains("Tools", StringComparison.OrdinalIgnoreCase))
                {
                    ScanScope(collection, "Tools", SearchOption.TopDirectoryOnly);
                    ScanLegacyGroups(collection);
                }
                else if (name.Equals("Obsolete", StringComparison.OrdinalIgnoreCase)) ScanScope(collection, "Obsolete", SearchOption.TopDirectoryOnly);
                else if (name.StartsWith("Coffee", StringComparison.OrdinalIgnoreCase))
                {
                    var coffeeTools = Path.Combine(collection, "Tools");
                    if (Directory.Exists(coffeeTools) && !IsLinked(coffeeTools))
                        foreach (var category in Directory.EnumerateDirectories(coffeeTools).Where(path => !IsLinked(path)))
                            ScanScope(category, $"Coffee/{Path.GetFileName(category)}", SearchOption.TopDirectoryOnly);
                }
            }
        }
        return new(workspaceRoot, DateTimeOffset.UtcNow, entries.OrderBy(entry => entry.Status == ToolInventoryStatus.Unassigned ? 0 : entry.Status == ToolInventoryStatus.Tracked ? 1 : 2).ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());

        void ScanLegacyGroups(string collection)
        {
            foreach (var parent in new[] { "AmarothTools", "Models", "Map", "DBC", "Textures", "Other", "CASC" })
            {
                var nested = Path.Combine(collection, parent);
                if (Directory.Exists(nested) && !IsLinked(nested)) ScanScope(nested, $"Tools/{parent}", SearchOption.TopDirectoryOnly);
            }
        }

        void ScanScope(string path, string scope, SearchOption search)
        {
            foreach (var directory in Directory.EnumerateDirectories(path, "*", search).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(directory); if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || IsLinked(directory)) continue; var relative = Path.GetRelativePath(workspaceRoot, directory).Replace('\\', '/');
                var rule = ToolRules.FirstOrDefault(candidate => candidate.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase) && FileSystemName.MatchesSimpleExpression(candidate.Pattern, name, true));
                var normalizedName = name.EndsWith(" Working Copy", StringComparison.OrdinalIgnoreCase) ? name[..^13] : name;
                Assignment? assignment = null;
                if (scope == "Tools")
                {
                    RootAssignments.TryGetValue(normalizedName.Replace("WoW Crucible", "WoW-Crucible", StringComparison.OrdinalIgnoreCase), out assignment);
                    if (assignment is null) RootAssignments.TryGetValue(normalizedName.Replace(" ", string.Empty), out assignment);
                    rule ??= ToolRules.FirstOrDefault(candidate => candidate.Scope == scope && FileSystemName.MatchesSimpleExpression(candidate.Pattern, normalizedName.Replace(" ", string.Empty), true));
                }
                assignment ??= rule?.Assignment;
                if (scope == "Obsolete") assignment = ToolRules.FirstOrDefault(candidate => FileSystemName.MatchesSimpleExpression(candidate.Pattern, name, true))?.Assignment;
                entries.Add(assignment is null ? Entry(relative, directory, scope, ToolInventoryStatus.Unassigned, Unassigned()) : Entry(relative, directory, scope, ToolInventoryStatus.Tracked, assignment));
            }
        }
    }

    public static string FindWorkspaceRoot(string startPath)
    {
        var current = File.Exists(startPath) ? Directory.GetParent(Path.GetFullPath(startPath)) : new DirectoryInfo(Path.GetFullPath(startPath));
        while (current is not null)
        {
            if (IsConsolidatedWorkspace(current.FullName) || Directory.Exists(Path.Combine(current.FullName, "Tools")) && (Directory.Exists(Path.Combine(current.FullName, "WoW-Crucible")) || Directory.Exists(Path.Combine(current.FullName, "WDBXEditor")))) return current.FullName;
            current = current.Parent;
        }
        return Path.GetFullPath(startPath);
    }

    private static bool IsConsolidatedWorkspace(string root) => File.Exists(Path.Combine(root, "Tools", "WoW Crucible", "WoWCrucible.slnx")) || File.Exists(Path.Combine(root, "Tools", "WoW-Crucible", "WoWCrucible.slnx"));
    private static bool IsLinked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static ToolInventoryEntry Entry(string relative, string fullPath, string scope, ToolInventoryStatus status, Assignment assignment) => new(relative.Replace('\\', '/'), Path.GetFullPath(fullPath), scope, status, assignment.Capability, assignment.Destination);
    private static Assignment Unassigned() => new("New tool root has not been audited", "Assign before claiming corpus coverage");
    private static Rule Direct(string pattern, string capability, string destination) => new("Tools", pattern, new(capability, destination));
    private static Rule Nested(string parent, string pattern, string capability, string destination) => new($"Tools/{parent}", pattern, new(capability, destination));
}
