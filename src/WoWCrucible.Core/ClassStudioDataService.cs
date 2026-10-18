using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record ClassStudioColumn(string Name, string Type, bool Key, int? ByteOffset = null);
public sealed record ClassStudioTable(string Layer, string Name, string Source, string Fingerprint,
    IReadOnlyList<ClassStudioColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, string Format = "dbc");
public sealed record ClassStudioSnapshot(DateTimeOffset ReadUtc, IReadOnlyList<ClassStudioTable> Tables, IReadOnlyList<string> Notices, string? SourceIdentity = null, string? RealmProfile = null, string? RealmName = null, IReadOnlyDictionary<uint, string>? ClassIcons = null);

/// <summary>Read-only, schema-resolved inputs for the shared class editor. Layers stay explicit.</summary>
public sealed class ClassStudioDataService(Func<WdbcFile, DbcSchemaResolution> resolveSchema)
{
    public static readonly string[] ClientTables = ["Spell", "ChrClasses", "Talent", "TalentTab", "SkillLineAbility", "SkillRaceClassInfo", "SkillLine", "SpellIcon", "SpellDuration", "SpellRadius"];
    public static readonly string[] CoATables = ["CharacterAdvancement", "CharacterAdvancementClassTypes", "CharacterAdvancementTabTypes", "ChrSpecs", "SpellRank", "NPCTrainer"];
    public static readonly string[] LearningTables = ["playercreateinfo_spell", "playercreateinfo_spell_custom", "playercreateinfo_cast_spell",
        "player_levelup_spell", "spell_learn_spell", "spell_ranks", "spell_required", "trainer_spell", "npc_trainer", "quest_template", "spell_dbc"];
    private ClientEffectiveAssetCatalog? _assets;
    private string? _iconCache;
    private readonly Dictionary<string,string?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private sealed record LoadedDbc(WdbcFile File, DbcSchemaResolution Schema, IReadOnlyDictionary<uint, int> Index);
    private readonly Dictionary<(string Layer, string Table), LoadedDbc> _loaded = [];
    private readonly Dictionary<string,SpellDescriptionRenderer> _tooltipRenderers = [];
    private readonly Dictionary<(string Layer, string Table), ClassStudioTable> _tables = [];

    public async Task<ClassStudioSnapshot> LoadAsync(string? serverPath, string? clientPath, string cachePath,
        DatabaseConnectionProfile? database = null, DatabaseCapabilities? capabilities = null, CancellationToken cancellationToken = default, string? corePath = null,
        ClientArchiveOrderPolicy clientOrder = ClientArchiveOrderPolicy.StandardWrath)
    {
        _loaded.Clear(); _tables.Clear(); _tooltipRenderers.Clear(); _icons.Clear(); _assets=null; var notices = new List<string>();
        IReadOnlyDictionary<uint, string>? classIcons = null;
        if (!string.IsNullOrWhiteSpace(serverPath)) ReadLoose(serverPath, "server", notices, cancellationToken);
        if (!string.IsNullOrWhiteSpace(clientPath))
        {
            var loose = FindTableDirectory(clientPath);
            if (loose is not null) ReadLoose(loose, "client", notices, cancellationToken);
            else
            {
                var root = Path.GetFullPath(clientPath);
                if (Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)).Equals("Data", StringComparison.OrdinalIgnoreCase)) root = Path.GetDirectoryName(root)!;
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
                var indexPath = Path.Combine(cachePath, key);
                new ClientArchiveIndexService().Build(root, indexPath, hashArchives: false, cancellationToken: cancellationToken);
                var catalog = ClientEffectiveAssetCatalog.Load(indexPath, cancellationToken, clientOrder);
                _assets=catalog; _iconCache=Path.Combine(indexPath,"icons");
                foreach (var table in ClientTables.Concat(CoATables))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resolution = catalog.Resolve($"DBFilesClient\\{table}.dbc", cancellationToken: cancellationToken);
                    if (resolution.State != ClientEffectiveAssetState.Effective || resolution.Effective is null)
                    { if (ClientTables.Contains(table) || resolution.State == ClientEffectiveAssetState.Ambiguous) notices.Add($"client/{table}: {resolution.Message}"); continue; }
                    var file = catalog.ExtractEffective(resolution.Effective, Path.Combine(indexPath, "tables"), overwrite: true);
                    if (CoATables.Contains(table)) AddCoATable(file, "client", resolution.Effective.ArchiveRelativePath);
                    else ReadTable(file, "client", resolution.Effective.ArchiveRelativePath);
                }
                if (_tables.TryGetValue(("client", "ChrClasses"), out var classes))
                    classIcons = ClassStudioIconService.Read(catalog, classes.Rows, Path.Combine(indexPath, "icons"), notices, cancellationToken);
            }
        }
        if (database is not null && capabilities is not null)
        {
            await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(database));
            await connection.OpenAsync(cancellationToken);
            foreach (var name in LearningTables)
            {
                var table = capabilities.FindTable(name);
                if (table is null) { notices.Add($"SQL: {name} is not present in this server schema."); continue; }
                var rows = new List<IReadOnlyDictionary<string, object?>>();
                // Names come only from inspected metadata and a fixed allow-list, never from a browser query.
                await using var command = new MySqlCommand($"SELECT * FROM `{table.Name.Replace("`", "``")}`", connection) { CommandTimeout = 60 };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, object?>();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        // JavaScript cannot round-trip every SQL bigint/decimal through a Number.
                        row.Add(reader.GetName(i), value is long or ulong or decimal ? Convert.ToString(value, CultureInfo.InvariantCulture) : value);
                    }
                    rows.Add(row);
                }
                var columns = table.Columns.Select(c => new ClassStudioColumn(c.Name, c.ColumnType, c.Key == "PRI")).ToArray();
                _tables.Add(("sql", name), new("sql", name, database.Database + "." + name, "", columns, rows, "sql"));
            }
        }
        else notices.Add("SQL is not connected. Trainer, starting, quest and server-script learning rules are not a complete learn list. Connect the world database in Crucible Workspace setup, then reload sources.");
        if (!string.IsNullOrWhiteSpace(corePath))
            foreach (var table in CoAClassStudioSource.ReadCore(corePath, notices)) _tables.Add((table.Layer, table.Name), table);
        if (_tables.Values.Any(t => t.Format == "coa-plan")) notices.Add("CoA advancement tables expose verified fields and byte offsets. Their CSV is a change-plan format, not a stock DBC import schema. Keep the JSON original values and source hashes when implementing these changes. Hidden/disabled entries remain in the source tables; talent boards follow realm presentation filters.");
        notices.Add("The CoA core source lists are read from source, not the running executable. Additional compiled hooks can grant spells beyond the inspected lists. SQL spell_dbc overrides remain a separate editable layer.");
        return new(DateTimeOffset.UtcNow, _tables.Values.ToArray(), notices, ClassIcons: classIcons);
    }

    public IReadOnlyDictionary<string,string?> ReadIcons(IEnumerable<string> names, CancellationToken token = default)
    {
        var requested=names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(requested.Length>512)throw new InvalidDataException("At most 512 icons per request.");
        if(_assets is null || _iconCache is null)throw new InvalidOperationException("Select a client archive folder to read its icons.");
        var missing=requested.Where(n=>!_icons.ContainsKey(n)).ToArray();
        if(missing.Length>0)
        {
            var found=ClassStudioIconService.ReadPaths(_assets,missing,_iconCache,[],token);
            foreach(var name in missing)_icons[name]=found.GetValueOrDefault(name);
        }
        return requested.ToDictionary(n=>n,n=>_icons[n]);
    }

    public SpellDescriptionPreview RenderTooltip(string layer, uint spellId, string text)
    {
        if(!_tooltipRenderers.TryGetValue(layer,out var renderer))
        {
            if(!_loaded.TryGetValue((layer,"Spell"),out var spell)) return SpellDescriptionRenderer.WithoutData.Render(text,spellId);
            var related=_loaded.Where(p=>p.Key.Layer==layer && p.Key.Table is "SpellDuration" or "SpellRadius").ToDictionary(p=>p.Key.Table,p=>p.Value.File);
            renderer=SpellTooltipService.CreateRenderer(spell.File,related);_tooltipRenderers.Add(layer,renderer);
        }
        return renderer.Render(text,spellId);
    }

    public IReadOnlyDictionary<string, object?> ReadRow(string layer, string table, uint id)
    {
        if (!_loaded.TryGetValue((layer, table), out var loaded)) throw new KeyNotFoundException($"No loaded DBC: {layer}/{table}");
        if (!loaded.Index.ContainsKey(id)) throw new KeyNotFoundException($"{table} has no record {id}.");
        return DbcRowExportService.Preview(loaded.File, loaded.Schema, recordKeys: [id], limit: 1).Rows[0];
    }

    public static string? FindTableDirectory(string path) => new[] { path, Path.Combine(path, "dbc"), Path.Combine(path, "data", "dbc"), Path.Combine(path, "DBFilesClient") }
        .Select(Path.GetFullPath).FirstOrDefault(p => File.Exists(Path.Combine(p, "Spell.dbc")));

    private void ReadLoose(string path, string layer, List<string> notices, CancellationToken cancellationToken)
    {
        var directory = FindTableDirectory(path) ?? throw new DirectoryNotFoundException($"No Spell.dbc in the selected folder or its dbc, data/dbc or DBFilesClient directory: {path}");
        foreach (var table in ClientTables)
        {
            cancellationToken.ThrowIfCancellationRequested(); var file = Path.Combine(directory, table + ".dbc");
            if (File.Exists(file)) ReadTable(file, layer, file); else notices.Add($"{layer}: {table}.dbc is missing.");
        }
        foreach (var table in CoATables)
        {
            var file = Path.Combine(directory, table + ".dbc");
            if (File.Exists(file)) AddCoATable(file, layer, file);
        }
        var custom = Path.Combine(directory, "customs");
        if (Directory.Exists(custom))
        {
            foreach (var table in ClientTables)
            {
                var file = Path.Combine(custom, table + ".dbc");
                if (File.Exists(file)) ReadTable(file, layer + "-custom", file);
            }
            notices.Add($"{layer}-custom exposes customs/ separately; select it explicitly. Runtime overlay rules vary by core.");
        }
    }

    private void AddCoATable(string path, string layer, string source)
    {
        var table = CoAClassStudioSource.ReadTable(path, layer, source);
        _tables.Add((layer, table.Name), table);
    }

    private void ReadTable(string path, string layer, string source)
    {
        var file = WdbcFile.Load(path); var schema = resolveSchema(file); var name = Path.GetFileNameWithoutExtension(path);
        if (!schema.IsExactFor(file)) throw new InvalidDataException($"{source}: exact schema required ({schema.MatchKind}). Configure the correct definitions in Crucible.");
        var index = DbcRecordIdentity.IndexRows(file, schema.Columns, schema.KeyStrategy);
        _loaded.Add((layer, name), new(file, schema, index));
        IReadOnlyList<string>? selected = null;
        if (name == "Spell")
        {
            // Catalog summaries are small; full editable records are retrieved on demand using the same export service.
            selected = schema.Columns.Where(c => c.Name == "ID" || c.Name is "SpellLevel" or "SpellIconID" or "SpellClassSet" ||
                c.Name.Contains("[enUS]", StringComparison.Ordinal) || c.Name.StartsWith("Effect[", StringComparison.Ordinal) || c.Name.StartsWith("EffectTriggerSpell[", StringComparison.Ordinal)).Select(c => c.Name).ToArray();
        }
        var rows = file.RowCount == 0 ? [] : DbcRowExportService.Preview(file, schema, selected, limit: file.RowCount).Rows;
        using var stream = File.OpenRead(path);
        _tables.Add((layer, name), new(layer, name, source, Convert.ToHexString(SHA256.HashData(stream)),
            schema.Columns.Select(c => new ClassStudioColumn(c.Name, c.Type.ToString(), c.IsIndex)).ToArray(), rows));
    }
}
