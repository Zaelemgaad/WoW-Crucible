using System.Globalization;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record TextureAppearanceModelSource(string ClientPath, string Provenance, string SourcePath, bool SameProvenance)
{
    public override string ToString() => $"{(SameProvenance ? "SAME" : "OTHER")} · {Provenance} · {ClientPath}";
}

public sealed record TextureSqlConsumer(string Table, IReadOnlyDictionary<string, object?> Key, uint DisplayId, string Field, string Description)
{
    public string Identity => $"{Table} · {string.Join(" · ", Key.Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"))}";
}

public sealed record TextureAppearanceBinding(
    string TextureClientPath,
    string Kind,
    string DbcPath,
    string Table,
    uint RecordId,
    string Field,
    int ReplaceableType,
    string Description,
    string? ModelClientPath,
    IReadOnlyList<TextureAppearanceModelSource> ModelSources,
    IReadOnlyList<TextureSqlConsumer> SqlConsumers)
{
    public override string ToString() => $"{Table} {RecordId:N0} · {Field} · slot {ReplaceableType:N0} · {Description}";
}

public sealed record TextureAppearanceQueryResult(
    string TextureClientPath,
    string DbcRoot,
    IReadOnlyList<TextureAppearanceBinding> Bindings,
    int CharacterSectionRecords,
    int CreatureDisplayRecords,
    int SqlRows,
    bool SqlRequested,
    bool SqlTruncated,
    IReadOnlyList<string> Findings)
{
    public bool CoverageComplete => Findings.Count == 0 && !SqlTruncated;
}

/// <summary>
/// Resolves exact texture paths supplied outside M2 binaries. Character body,
/// face, hair, and underwear bindings come from CharSections; creature slots
/// 11-13 come from CreatureDisplayInfo joined to CreatureModelData. Optional
/// live SQL lookup reports the creature-template rows which actually select a
/// matching display without changing either the DBCs or database.
/// </summary>
public sealed class TextureAppearanceReferenceService
{
    private const int MaximumSqlRows = 10_000;
    private const int SqlIdBatch = 250;

    public async Task<TextureAppearanceQueryResult> QueryAsync(string processedLibrary, string dbcRoot, string? schemaPath,
        string textureClientPath, string? textureProvenance = null, DatabaseConnectionProfile? profile = null,
        DatabaseCapabilities? capabilities = null, CancellationToken cancellationToken = default)
    {
        processedLibrary = Path.GetFullPath(processedLibrary); dbcRoot = Path.GetFullPath(dbcRoot);
        textureClientPath = PatchInputMapper.NormalizeArchivePath(textureClientPath);
        if (!Path.GetExtension(textureClientPath).Equals(".blp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Appearance texture lookup requires an exact .blp client path.");
        var layout = ClientAssetDependencyService.OpenLibraryLayout(processedLibrary); var findings = new List<string>(); var drafts = new List<BindingDraft>();

        var charSectionsPath = FindFile(dbcRoot, "CharSections.dbc");
        if (charSectionsPath is null) findings.Add("Configured DBC root has no CharSections.dbc; playable-character appearance coverage is unavailable.");
        else
        {
            try
            {
                var identities = CharacterAppearanceService.KnownIdentities.ToDictionary(item => (item.RaceId, item.SexId));
                foreach (var section in CharacterAppearanceService.LoadAllSections(charSectionsPath))
                {
                    cancellationToken.ThrowIfCancellationRequested(); identities.TryGetValue((section.RaceId, section.SexId), out var identity);
                    for (var index = 0; index < 3; index++)
                    {
                        var path = index switch { 0 => section.Texture0, 1 => section.Texture1, _ => section.Texture2 };
                        if (path is null || !path.Equals(textureClientPath, StringComparison.OrdinalIgnoreCase)) continue;
                        var modelClientPath = identity is null ? null : $@"Character\{identity.RaceName}\{identity.SexName}\{identity.RaceName}{identity.SexName}.m2";
                        var sources = modelClientPath is null ? [] : Sources(layout, modelClientPath, textureProvenance, cancellationToken);
                        var subject = identity is null ? $"race {section.RaceId:N0} sex {section.SexId:N0}" : $"{identity.RaceName} {identity.SexName}";
                        var replaceable = section.Kind == CharacterSectionKind.Hair && index == 0 ? 6 : 1;
                        drafts.Add(new(textureClientPath, "character-section", charSectionsPath, "CharSections", section.Id, $"TextureName[{index}]", replaceable,
                            $"{subject} · {section.Kind} style {section.VariationIndex:N0} color {section.ColorIndex:N0}", modelClientPath, sources));
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                findings.Add($"CharSections appearance lookup failed: {exception.Message}");
            }
        }

        var displayPath = FindFile(dbcRoot, "CreatureDisplayInfo.dbc"); var modelPath = FindFile(dbcRoot, "CreatureModelData.dbc");
        if (displayPath is null || modelPath is null) findings.Add("Configured DBC root lacks a complete CreatureDisplayInfo/CreatureModelData pair; creature replaceable-slot coverage is unavailable.");
        else
        {
            try
            {
                var catalog = new CreatureDisplayPreviewService().LoadCatalog(dbcRoot, schemaPath, cancellationToken);
                foreach (var display in catalog.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var index = 0; index < display.TextureVariations.Count; index++)
                    {
                        if (!display.TextureVariations[index].Equals(textureClientPath, StringComparison.OrdinalIgnoreCase)) continue;
                        drafts.Add(new(textureClientPath, "creature-display", displayPath, "CreatureDisplayInfo", display.DisplayId, $"TextureVariation[{index}]", 11 + index,
                            $"Creature display {display.DisplayId:N0} · model {display.ModelId:N0}", display.ModelClientPath,
                            string.IsNullOrWhiteSpace(display.ModelClientPath) ? [] : Sources(layout, display.ModelClientPath, textureProvenance, cancellationToken)));
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                findings.Add($"Creature appearance lookup failed: {exception.Message}");
            }
        }

        var displayIds = drafts.Where(draft => draft.Table.Equals("CreatureDisplayInfo", StringComparison.OrdinalIgnoreCase)).Select(draft => draft.RecordId).Where(id => id != 0).Distinct().Order().ToArray();
        var sqlUses = new Dictionary<uint, List<TextureSqlConsumer>>(); var sqlTruncated = false;
        if (profile is not null && displayIds.Length > 0)
        {
            try
            {
                capabilities ??= await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
                (sqlUses, sqlTruncated) = await QuerySqlUsesAsync(profile, capabilities, displayIds, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                findings.Add($"Connected SQL appearance lookup failed: {exception.Message}");
            }
        }

        var bindings = drafts.Select(draft => new TextureAppearanceBinding(draft.TextureClientPath, draft.Kind, draft.DbcPath, draft.Table, draft.RecordId, draft.Field,
            draft.ReplaceableType, draft.Description, draft.ModelClientPath, draft.ModelSources, sqlUses.GetValueOrDefault(draft.RecordId, [])))
            .OrderBy(binding => binding.Table, StringComparer.OrdinalIgnoreCase).ThenBy(binding => binding.RecordId).ThenBy(binding => binding.Field, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(textureClientPath, dbcRoot, bindings, bindings.Where(binding => binding.Table == "CharSections").Select(binding => binding.RecordId).Distinct().Count(),
            bindings.Where(binding => binding.Table == "CreatureDisplayInfo").Select(binding => binding.RecordId).Distinct().Count(), sqlUses.Values.Sum(rows => rows.Count), profile is not null, sqlTruncated, findings);
    }

    private static IReadOnlyList<TextureAppearanceModelSource> Sources(AssetComparisonIndex layout, string clientPath, string? textureProvenance, CancellationToken cancellationToken)
    {
        try
        {
            return ClientAssetDependencyService.FindCandidates(layout, clientPath, cancellationToken)
                .Select(source => new TextureAppearanceModelSource(source.ClientPath, source.Provenance, source.SourcePath,
                    textureProvenance is not null && source.Provenance.Equals(textureProvenance, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(source => source.SameProvenance).ThenBy(source => source.Provenance, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException) { return []; }
    }

    private static async Task<(Dictionary<uint, List<TextureSqlConsumer>> Uses, bool Truncated)> QuerySqlUsesAsync(DatabaseConnectionProfile profile,
        DatabaseCapabilities capabilities, IReadOnlyList<uint> displayIds, CancellationToken cancellationToken)
    {
        var uses = new Dictionary<uint, List<TextureSqlConsumer>>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var truncated = false; var total = 0;
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var mapping = capabilities.FindTable("creature_template_model");
        if (mapping is not null)
        {
            var display = mapping.Find("CreatureDisplayID") ?? mapping.Find("displayid"); var creature = mapping.Find("CreatureID") ?? mapping.Find("creatureid") ?? mapping.Find("entry");
            if (display is not null && creature is not null)
            {
                var selected = Primary(mapping).Concat([creature.Name, display.Name]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var batch in displayIds.Chunk(SqlIdBatch))
                {
                    var rows = await ReadMatchesAsync(connection, mapping, selected, [display.Name], batch, MaximumSqlRows - total + 1, cancellationToken);
                    foreach (var row in rows)
                    {
                        if (total >= MaximumSqlRows) { truncated = true; break; }
                        var displayId = UInt(Value(row, display.Name)); if (displayId == 0) continue; var key = Key(mapping, row);
                        Add(displayId, new(mapping.Name, key, displayId, display.Name, $"Creature {Convert.ToString(Value(row, creature.Name), CultureInfo.InvariantCulture)} selects display {displayId:N0}."));
                    }
                    if (truncated) break;
                }
            }
        }

        var template = capabilities.FindTable("creature_template");
        if (!truncated && template is not null)
        {
            var modelColumns = Enumerable.Range(1, 4).Select(index => template.Find($"modelid{index}")).Where(column => column is not null).Select(column => column!).ToArray();
            if (modelColumns.Length > 0)
            {
                var name = template.Find("name"); var selected = Primary(template).Concat(modelColumns.Select(column => column.Name)).Concat(name is null ? [] : [name.Name]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var batch in displayIds.Chunk(SqlIdBatch))
                {
                    var rows = await ReadMatchesAsync(connection, template, selected, modelColumns.Select(column => column.Name).ToArray(), batch, MaximumSqlRows - total + 1, cancellationToken);
                    foreach (var row in rows)
                    {
                        foreach (var modelColumn in modelColumns)
                        {
                            var displayId = UInt(Value(row, modelColumn.Name)); if (!batch.Contains(displayId)) continue;
                            if (total >= MaximumSqlRows) { truncated = true; break; }
                            var key = Key(template, row); var label = name is null ? "Creature template" : Convert.ToString(Value(row, name.Name), CultureInfo.InvariantCulture) ?? "Creature template";
                            Add(displayId, new(template.Name, key, displayId, modelColumn.Name, $"{label} selects display {displayId:N0} through {modelColumn.Name}."));
                        }
                        if (truncated) break;
                    }
                    if (truncated) break;
                }
            }
        }
        return (uses, truncated);

        void Add(uint displayId, TextureSqlConsumer use)
        {
            var identity = $"{use.Table}\u001f{use.Field}\u001f{displayId}\u001f{string.Join('\u001e', use.Key.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"))}";
            if (!seen.Add(identity)) return; if (!uses.TryGetValue(displayId, out var list)) uses[displayId] = list = []; list.Add(use); total++;
        }
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadMatchesAsync(MySqlConnection connection, DatabaseTableCapability table,
        IReadOnlyList<string> selectedColumns, IReadOnlyList<string> matchColumns, IReadOnlyList<uint> ids, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0 || ids.Count == 0) return [];
        await using var command = connection.CreateCommand(); var parameters = ids.Select((id, index) => (Name: $"@id{index}", Value: id)).ToArray();
        var inList = string.Join(',', parameters.Select(parameter => parameter.Name)); var predicates = matchColumns.Select(column => $"{ItemWritePlan.QuoteIdentifier(column)} IN ({inList})");
        command.CommandText = $"SELECT {string.Join(',', selectedColumns.Select(ItemWritePlan.QuoteIdentifier))} FROM {ItemWritePlan.QuoteIdentifier(table.Name)} WHERE {string.Join(" OR ", predicates)} LIMIT @limit";
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value); command.Parameters.AddWithValue("@limit", limit);
        var result = new List<IReadOnlyDictionary<string, object?>>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++) row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            result.Add(row);
        }
        return result;
    }

    private static IReadOnlyList<string> Primary(DatabaseTableCapability table) => table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray();
    private static IReadOnlyDictionary<string, object?> Key(DatabaseTableCapability table, IReadOnlyDictionary<string, object?> row)
        => Primary(table).ToDictionary(name => name, name => Value(row, name), StringComparer.OrdinalIgnoreCase);
    private static object? Value(IReadOnlyDictionary<string, object?> row, string name) => row.TryGetValue(name, out var value) ? value : null;
    private static uint UInt(object? value) { try { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); } catch { return 0; } }
    private static string? FindFile(string root, string name) => !Directory.Exists(root) ? null : Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));

    private sealed record BindingDraft(string TextureClientPath, string Kind, string DbcPath, string Table, uint RecordId, string Field, int ReplaceableType,
        string Description, string? ModelClientPath, IReadOnlyList<TextureAppearanceModelSource> ModelSources);
}
