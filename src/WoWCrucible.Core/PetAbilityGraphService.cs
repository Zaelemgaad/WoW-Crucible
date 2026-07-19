namespace WoWCrucible.Core;

public enum PetAbilityNodeKind
{
    Creature,
    Family,
    CreatureSpellData,
    SkillLine,
    TalentTab,
    Talent,
    Spell
}

public sealed record PetAbilityGraphNode(
    string Id,
    PetAbilityNodeKind Kind,
    uint NumericId,
    string Label,
    string Detail,
    int? Tier = null,
    int? Column = null);

public sealed record PetAbilityGraphEdge(
    string From,
    string To,
    string Relation,
    string Evidence);

public sealed record PetAbilityGraph(
    uint CreatureEntry,
    string CreatureName,
    int FamilyId,
    string FamilyName,
    int PetTalentType,
    IReadOnlyList<PetAbilityGraphNode> Nodes,
    IReadOnlyList<PetAbilityGraphEdge> Edges,
    IReadOnlyList<string> Findings);

public sealed class PetAbilityGraphService
{
    public async Task<PetAbilityGraph> BuildAsync(DatabaseConnectionProfile profile, string dbcRoot, string schemaPath,
        uint creatureEntry, CancellationToken cancellationToken = default)
    {
        if (creatureEntry == 0) throw new ArgumentOutOfRangeException(nameof(creatureEntry), "Creature entry must be positive.");
        dbcRoot = Path.GetFullPath(dbcRoot);
        schemaPath = Path.GetFullPath(schemaPath);
        if (!Directory.Exists(dbcRoot)) throw new DirectoryNotFoundException($"Server DBC folder does not exist: {dbcRoot}");
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("A WotLK build-12340 schema XML is required for the pet ability graph.", schemaPath);

        var catalog = DbcSchemaCatalog.Load(schemaPath);
        var familyTable = Load(dbcRoot, catalog, "CreatureFamily");
        var creatureSpellDataTable = Load(dbcRoot, catalog, "CreatureSpellData");
        var skillLineTable = Load(dbcRoot, catalog, "SkillLine");
        var skillAbilityTable = Load(dbcRoot, catalog, "SkillLineAbility");
        var talentTabTable = Load(dbcRoot, catalog, "TalentTab");
        var talentTable = Load(dbcRoot, catalog, "Talent");
        var spellPath = Path.Combine(dbcRoot, "Spell.dbc");
        if (!File.Exists(spellPath)) throw new FileNotFoundException("Pet ability names require Spell.dbc in the configured server DBC folder.", spellPath);
        var spells = SpellTooltipService.Load(spellPath).Records;

        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var creatureTable = capabilities.FindTable("creature_template") ?? throw new NotSupportedException("The connected world database has no creature_template table.");
        var entryColumn = creatureTable.Find("entry") ?? throw new NotSupportedException("creature_template has no entry identity column.");
        var creatureRow = await new SqlWorkspaceService().ReadRowAsync(profile, creatureTable,
            new Dictionary<string, object?> { [entryColumn.Name] = creatureEntry }, cancellationToken)
            ?? throw new KeyNotFoundException($"creature_template has no entry {creatureEntry}.");

        var creatureName = Text(Value(creatureRow.Values, "name"), $"Creature {creatureEntry}");
        var familyId = Signed(Value(creatureRow.Values, "family"));
        var petSpellDataId = Positive(Value(creatureRow.Values, "PetSpellDataId"));
        var builder = new GraphBuilder(spells);
        var creatureNode = builder.Add(PetAbilityNodeKind.Creature, creatureEntry, creatureName,
            $"creature_template entry {creatureEntry}; family {familyId}; PetSpellDataId {petSpellDataId}");
        var findings = new List<string>();

        await AddTemplateSpellsAsync(profile, capabilities, creatureTable, creatureRow, creatureEntry, creatureNode, builder, findings, cancellationToken);
        AddCreatureSpellData(creatureSpellDataTable, petSpellDataId, creatureNode, builder, findings);

        var familyName = string.Empty;
        var talentType = -1;
        if (familyId > 0 && familyTable.TryRow((uint)familyId, out var familyRow))
        {
            familyName = familyTable.String(familyRow, "Name_Lang[enUS]");
            talentType = familyTable.Int(familyRow, "PetTalentType");
            var familyNode = builder.Add(PetAbilityNodeKind.Family, (uint)familyId, familyName.Length == 0 ? $"Family {familyId}" : familyName,
                $"CreatureFamily.dbc {familyId}; food mask {familyTable.UInt(familyRow, "PetFoodMask")}; pet talent type {talentType}");
            builder.Edge(creatureNode, familyNode, "uses creature family", $"creature_template.family = {familyId}");
            var skillLines = new[] { familyTable.UInt(familyRow, "SkillLine[0]"), familyTable.UInt(familyRow, "SkillLine[1]") }.Where(value => value != 0).Distinct().ToArray();
            foreach (var skillLine in skillLines) AddSkillLine(skillLineTable, skillAbilityTable, skillLine, familyNode, builder, findings);
            AddTalentTree(talentTabTable, talentTable, talentType, familyNode, builder, findings);
        }
        else if (familyId > 0) findings.Add($"CreatureFamily.dbc has no row {familyId}; family abilities and talents cannot be resolved.");
        else findings.Add("creature_template.family is zero; this creature has no CreatureFamily skill-line or pet-talent link.");

        await AddPetAurasAsync(profile, capabilities, creatureEntry, creatureNode, builder, findings, cancellationToken);
        findings.Add($"Resolved {builder.Nodes.Count(node => node.Kind == PetAbilityNodeKind.Spell):N0} unique spell node(s), {builder.Nodes.Count(node => node.Kind == PetAbilityNodeKind.Talent):N0} talent node(s), and {builder.Edges.Count:N0} evidence edge(s).");
        return new(creatureEntry, creatureName, familyId, familyName, talentType, builder.Nodes, builder.Edges, findings);
    }

    private static async Task AddTemplateSpellsAsync(DatabaseConnectionProfile profile, DatabaseCapabilities capabilities,
        DatabaseTableCapability creatureTable, SqlRowRecord creatureRow, uint creatureEntry, string creatureNode,
        GraphBuilder builder, ICollection<string> findings, CancellationToken cancellationToken)
    {
        var normalized = capabilities.FindTable("creature_template_spell");
        var count = 0;
        if (normalized is not null)
        {
            var creatureColumn = normalized.Find("CreatureID") ?? normalized.Find("creatureid") ?? normalized.Find("entry");
            var spellColumn = normalized.Find("Spell") ?? normalized.Find("spell");
            if (creatureColumn is null || spellColumn is null) findings.Add("creature_template_spell exists but its creature/spell columns are unrecognized.");
            else
            {
                var rows = await ReadAllMatchesAsync(profile, normalized, creatureColumn.Name, creatureEntry,
                    normalized.Find("Index")?.Name, cancellationToken);
                foreach (var row in rows)
                {
                    var spellId = Positive(Value(row.Values, spellColumn.Name)); if (spellId == 0) continue;
                    var slot = Signed(Value(row.Values, "Index")); var spellNode = builder.Spell(spellId);
                    builder.Edge(creatureNode, spellNode, $"template spell slot {slot}", $"{normalized.Name}: CreatureID={creatureEntry}, Index={slot}, Spell={spellId}"); count++;
                }
            }
        }
        else
        {
            for (var slot = 1; slot <= 8; slot++)
            {
                var column = creatureTable.Find($"spell{slot}"); if (column is null) continue;
                var spellId = Positive(Value(creatureRow.Values, column.Name)); if (spellId == 0) continue;
                builder.Edge(creatureNode, builder.Spell(spellId), $"template spell slot {slot}", $"creature_template.{column.Name} = {spellId}"); count++;
            }
        }
        if (count == 0) findings.Add("No nonzero creature-template spell slots were found for this entry.");
    }

    private static void AddCreatureSpellData(DbcTable table, uint dataId, string creatureNode, GraphBuilder builder, ICollection<string> findings)
    {
        if (dataId == 0) return;
        if (!table.TryRow(dataId, out var row)) { findings.Add($"CreatureSpellData.dbc has no row {dataId} referenced by creature_template.PetSpellDataId."); return; }
        var dataNode = builder.Add(PetAbilityNodeKind.CreatureSpellData, dataId, $"Creature spell data {dataId}", "CreatureSpellData.dbc");
        builder.Edge(creatureNode, dataNode, "uses client spell data", $"creature_template.PetSpellDataId = {dataId}");
        for (var slot = 0; slot < 4; slot++)
        {
            var spellId = table.UInt(row, $"Spells[{slot}]"); if (spellId == 0) continue;
            builder.Edge(dataNode, builder.Spell(spellId), $"client spell slot {slot}", $"CreatureSpellData.dbc {dataId}.Spells[{slot}] = {spellId}; availability {table.Int(row, $"Availability[{slot}]")}");
        }
    }

    private static void AddSkillLine(DbcTable skillLines, DbcTable abilities, uint skillLineId, string familyNode,
        GraphBuilder builder, ICollection<string> findings)
    {
        var label = $"Skill line {skillLineId}";
        if (skillLines.TryRow(skillLineId, out var skillRow)) label = skillLines.String(skillRow, "DisplayName_Lang[enUS]") is { Length: > 0 } value ? value : label;
        else findings.Add($"SkillLine.dbc has no row {skillLineId} referenced by CreatureFamily.dbc.");
        var skillNode = builder.Add(PetAbilityNodeKind.SkillLine, skillLineId, label, $"SkillLine.dbc {skillLineId}");
        builder.Edge(familyNode, skillNode, "family skill line", $"CreatureFamily.dbc SkillLine = {skillLineId}");
        var matched = 0;
        for (var row = 0; row < abilities.File.RowCount; row++)
        {
            if (abilities.UInt(row, "SkillLine") != skillLineId) continue;
            var spellId = abilities.UInt(row, "Spell"); if (spellId == 0) continue;
            var abilityId = abilities.UInt(row, "ID");
            builder.Edge(skillNode, builder.Spell(spellId), "skill-line ability",
                $"SkillLineAbility.dbc {abilityId}: SkillLine={skillLineId}, Spell={spellId}, AcquireMethod={abilities.Int(row, "AcquireMethod")}, MinSkillLineRank={abilities.Int(row, "MinSkillLineRank")}");
            var superseded = abilities.UInt(row, "SupercededBySpell"); if (superseded != 0) builder.Edge(builder.Spell(spellId), builder.Spell(superseded), "superseded by", $"SkillLineAbility.dbc {abilityId}.SupercededBySpell = {superseded}");
            matched++;
        }
        if (matched == 0) findings.Add($"SkillLineAbility.dbc has no nonzero spell rows for skill line {skillLineId}.");
    }

    private static void AddTalentTree(DbcTable tabs, DbcTable talents, int talentType, string familyNode,
        GraphBuilder builder, ICollection<string> findings)
    {
        if (talentType is < 0 or > 30) { findings.Add("This family has no WotLK hunter-pet talent type; that is normal for warlock and other non-hunter companions."); return; }
        var mask = 1u << talentType; var tabRows = Enumerable.Range(0, tabs.File.RowCount).Where(row => (tabs.UInt(row, "PetTalentMask") & mask) != 0).ToArray();
        if (tabRows.Length == 0) { findings.Add($"No TalentTab.dbc row has PetTalentMask bit {mask} for PetTalentType {talentType}."); return; }
        foreach (var tabRow in tabRows)
        {
            var tabId = tabs.UInt(tabRow, "ID"); var name = tabs.String(tabRow, "Name_Lang[enUS]");
            var tabNode = builder.Add(PetAbilityNodeKind.TalentTab, tabId, name.Length == 0 ? $"Talent tab {tabId}" : name,
                $"TalentTab.dbc {tabId}; PetTalentMask {tabs.UInt(tabRow, "PetTalentMask")}; background {tabs.String(tabRow, "BackgroundFile")}");
            builder.Edge(familyNode, tabNode, "pet talent tree", $"CreatureFamily.PetTalentType={talentType} → mask {mask}; TalentTab.dbc {tabId}");
            var talentRows = Enumerable.Range(0, talents.File.RowCount).Where(row => talents.UInt(row, "TabID") == tabId).ToArray();
            foreach (var talentRow in talentRows)
            {
                var talentId = talents.UInt(talentRow, "ID"); var tier = talents.Int(talentRow, "TierID"); var column = talents.Int(talentRow, "ColumnIndex");
                var ranks = Enumerable.Range(0, 9).Select(index => talents.UInt(talentRow, $"SpellRank[{index}]")).Where(id => id != 0).ToArray();
                var talentLabel = ranks.FirstOrDefault() is { } first && first != 0 ? builder.SpellLabel(first) : $"Talent {talentId}";
                var talentNode = builder.Add(PetAbilityNodeKind.Talent, talentId, talentLabel, $"Talent.dbc {talentId}; tier {tier}; column {column}; {ranks.Length} rank(s)", tier, column);
                builder.Edge(tabNode, talentNode, "contains talent", $"Talent.dbc {talentId}.TabID = {tabId}");
                for (var rank = 0; rank < ranks.Length; rank++) builder.Edge(talentNode, builder.Spell(ranks[rank]), $"rank {rank + 1}", $"Talent.dbc {talentId}.SpellRank[{rank}] = {ranks[rank]}");
                for (var prerequisite = 0; prerequisite < 3; prerequisite++)
                {
                    var prerequisiteId = talents.UInt(talentRow, $"PrereqTalent[{prerequisite}]"); if (prerequisiteId == 0) continue;
                    var prerequisiteNode = builder.Add(PetAbilityNodeKind.Talent, prerequisiteId, $"Talent {prerequisiteId}", $"Referenced prerequisite talent {prerequisiteId}");
                    builder.Edge(prerequisiteNode, talentNode, "talent prerequisite", $"Talent.dbc {talentId}.PrereqTalent[{prerequisite}] = {prerequisiteId}; required rank {talents.Int(talentRow, $"PrereqRank[{prerequisite}]")}");
                }
                var requiredSpell = talents.UInt(talentRow, "RequiredSpellID"); if (requiredSpell != 0) builder.Edge(builder.Spell(requiredSpell), talentNode, "required spell", $"Talent.dbc {talentId}.RequiredSpellID = {requiredSpell}");
            }
        }
    }

    private static async Task AddPetAurasAsync(DatabaseConnectionProfile profile, DatabaseCapabilities capabilities, uint creatureEntry,
        string creatureNode, GraphBuilder builder, ICollection<string> findings, CancellationToken cancellationToken)
    {
        var table = capabilities.FindTable("spell_pet_auras"); if (table is null) { findings.Add("The connected schema has no spell_pet_auras table."); return; }
        var petColumn = table.Find("pet"); var triggerColumn = table.Find("spell"); var auraColumn = table.Find("aura");
        if (petColumn is null || triggerColumn is null || auraColumn is null) { findings.Add("spell_pet_auras exists but its pet/spell/aura columns are unrecognized."); return; }
        var rows = new List<SqlRowRecord>();
        foreach (var pet in new[] { 0u, creatureEntry }.Distinct())
        {
            rows.AddRange(await ReadAllMatchesAsync(profile, table, petColumn.Name, pet, null, cancellationToken));
        }
        foreach (var row in rows.DistinctBy(RowIdentity, StringComparer.Ordinal))
        {
            var trigger = Positive(Value(row.Values, triggerColumn.Name)); var aura = Positive(Value(row.Values, auraColumn.Name)); if (trigger == 0 || aura == 0) continue;
            var pet = Positive(Value(row.Values, petColumn.Name)); var effect = Signed(Value(row.Values, "effectId")); var triggerNode = builder.Spell(trigger); var auraNode = builder.Spell(aura);
            builder.Edge(creatureNode, triggerNode, pet == 0 ? "global pet-aura trigger" : "pet-specific aura trigger", $"spell_pet_auras: spell={trigger}, effectId={effect}, pet={pet}, aura={aura}");
            builder.Edge(triggerNode, auraNode, $"effect {effect} applies aura", $"spell_pet_auras: spell={trigger}, effectId={effect}, pet={pet}, aura={aura}");
        }
    }

    private static string RowIdentity(SqlRowRecord row)
    {
        var values = row.Key.Count > 0 ? row.Key : row.Values;
        return string.Join('\u001f', values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture)}"));
    }

    private static async Task<IReadOnlyList<SqlRowRecord>> ReadAllMatchesAsync(DatabaseConnectionProfile profile,
        DatabaseTableCapability table, string filterColumn, uint filterValue, string? sortColumn, CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var rows = new List<SqlRowRecord>();
        var service = new SqlWorkspaceService();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await service.ReadPageAsync(profile, table, offset, pageSize,
                filterColumn: filterColumn,
                filterValue: filterValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                sortColumn: sortColumn,
                cancellationToken: cancellationToken);
            rows.AddRange(page.Rows);
            if (rows.Count >= page.TotalRows || page.Rows.Count == 0) break;
        }
        return rows;
    }

    private static DbcTable Load(string root, DbcSchemaCatalog catalog, string table)
    {
        var path = Path.Combine(root, table + ".dbc"); if (!File.Exists(path)) throw new FileNotFoundException($"Pet ability graph requires {table}.dbc.", path);
        var file = WdbcFile.Load(path); var schema = catalog.ResolveColumns(table, file.FieldCount);
        if (schema.MatchKind != DbcSchemaMatchKind.NamedMatch || schema.Columns.Count != file.FieldCount) throw new InvalidDataException($"{table}.dbc did not resolve to an exact named build-12340 schema ({file.FieldCount} file fields, {schema.Columns.Count} schema fields, {schema.MatchKind}).");
        return new(file, schema.Columns);
    }

    private static object? Value(IReadOnlyDictionary<string, object?> values, string name) => values.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static uint Positive(object? value) { try { var number = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture); return number is > 0 and <= uint.MaxValue ? (uint)number : 0; } catch { return 0; } }
    private static int Signed(object? value) { try { return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; } }
    private static string Text(object? value, string fallback = "") => string.IsNullOrWhiteSpace(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)) ? fallback : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;

    private sealed class DbcTable
    {
        private readonly Dictionary<string, DbcColumn> _columns;
        private readonly Dictionary<uint, int> _rows;
        public WdbcFile File { get; }
        public DbcTable(WdbcFile file, IReadOnlyList<DbcColumn> columns)
        {
            File = file; _columns = columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
            var id = Column("ID"); _rows = Enumerable.Range(0, file.RowCount).ToDictionary(row => file.GetRaw(row, id));
        }
        public bool TryRow(uint id, out int row) => _rows.TryGetValue(id, out row);
        public uint UInt(int row, string column) { var value = File.GetDisplayValue(row, Column(column)); try { return Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; } }
        public int Int(int row, string column) { var value = File.GetDisplayValue(row, Column(column)); try { return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; } }
        public string String(int row, string column) => Convert.ToString(File.GetDisplayValue(row, Column(column)), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        private DbcColumn Column(string name) => _columns.TryGetValue(name, out var column) ? column : throw new InvalidDataException($"DBC schema is missing required column {name}.");
    }

    private sealed class GraphBuilder(IReadOnlyDictionary<uint, SpellTooltipRecord> spells)
    {
        private readonly Dictionary<string, PetAbilityGraphNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);
        private readonly List<PetAbilityGraphEdge> _edges = [];
        public IReadOnlyList<PetAbilityGraphNode> Nodes => _nodes.Values.OrderBy(node => node.Kind).ThenBy(node => node.Tier).ThenBy(node => node.Column).ThenBy(node => node.NumericId).ToArray();
        public IReadOnlyList<PetAbilityGraphEdge> Edges => _edges;
        public string Add(PetAbilityNodeKind kind, uint id, string label, string detail, int? tier = null, int? column = null)
        {
            var key = $"{kind}:{id}"; var candidate = new PetAbilityGraphNode(key, kind, id, label, detail, tier, column);
            if (!_nodes.TryGetValue(key, out var existing) || existing.Label.StartsWith(existing.Kind + " ", StringComparison.OrdinalIgnoreCase)) _nodes[key] = candidate;
            return key;
        }
        public string Spell(uint id)
        {
            var record = spells.GetValueOrDefault(id); return Add(PetAbilityNodeKind.Spell, id, SpellLabel(id), record is null ? $"Spell.dbc has no record {id}." : SpellTooltipService.Clean(record.Description));
        }
        public string SpellLabel(uint id) => spells.TryGetValue(id, out var record) && !string.IsNullOrWhiteSpace(record.Name) ? $"{record.Name} [{id}]" : $"Spell {id}";
        public void Edge(string from, string to, string relation, string evidence)
        {
            var key = $"{from}\0{to}\0{relation}\0{evidence}"; if (_edgeKeys.Add(key)) _edges.Add(new(from, to, relation, evidence));
        }
    }
}
