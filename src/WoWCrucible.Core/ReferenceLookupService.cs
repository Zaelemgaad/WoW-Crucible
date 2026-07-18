using System.Globalization;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum ReferenceDomain
{
    Spell, Item, Creature, Quest, GameObject,
    SpellCastTime, SpellDuration, SpellRange, SpellRuneCost, SpellVisual, SpellIcon, SpellDifficulty
}

public sealed record ReferenceLookupEntry(uint Id, string Name, string Source, string Details)
{
    public string Display => $"{Id:N0} — {(string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name)}";
}

public sealed record ReferenceLookupPage(ReferenceDomain Domain, string Query, IReadOnlyList<ReferenceLookupEntry> Entries, bool HasMore, IReadOnlyList<string> Sources);

public sealed class ReferenceLookupService
{
    private sealed record SqlDefinition(string Table, string[] IdCandidates, string[] NameCandidates, string[] DetailCandidates);
    private static readonly IReadOnlyDictionary<ReferenceDomain, SqlDefinition> SqlDefinitions = new Dictionary<ReferenceDomain, SqlDefinition>
    {
        [ReferenceDomain.Spell] = new("spell_dbc", ["ID"], ["Name_Lang_enUS", "Name_Lang_enGB"], ["SpellLevel", "Description_Lang_enUS"]),
        [ReferenceDomain.Item] = new("item_template", ["entry", "ID"], ["name"], ["Quality", "ItemLevel", "InventoryType"]),
        [ReferenceDomain.Creature] = new("creature_template", ["entry", "ID"], ["name"], ["subname", "minlevel", "maxlevel"]),
        [ReferenceDomain.Quest] = new("quest_template", ["ID", "entry"], ["LogTitle", "Title"], ["QuestLevel", "MinLevel"]),
        [ReferenceDomain.GameObject] = new("gameobject_template", ["entry", "ID"], ["name"], ["type", "displayId"])
    };

    public async Task<ReferenceLookupPage> SearchSqlAsync(DatabaseConnectionProfile profile, DatabaseCapabilities capabilities,
        ReferenceDomain domain, string? query, int limit = 250, CancellationToken cancellationToken = default)
    {
        query = query?.Trim() ?? string.Empty; limit = Math.Clamp(limit, 1, 1000);
        if (!SqlDefinitions.TryGetValue(domain, out var definition)) return new(domain, query, [], false, []);
        var table = capabilities.FindTable(definition.Table);
        if (table is null) return new(domain, query, [], false, []);
        var id = definition.IdCandidates.Select(table.Find).FirstOrDefault(column => column is not null);
        var names = definition.NameCandidates.Select(table.Find).Where(column => column is not null).DistinctBy(column => column!.Name, StringComparer.OrdinalIgnoreCase).Cast<DatabaseColumnCapability>().ToArray();
        var details = definition.DetailCandidates.Select(table.Find).Where(column => column is not null).DistinctBy(column => column!.Name, StringComparer.OrdinalIgnoreCase).Cast<DatabaseColumnCapability>().ToArray();
        if (id is null || names.Length == 0) return new(domain, query, [], false, []);
        var selected = new[] { id }.Concat(names).Concat(details).DistinctBy(column => column.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var numeric = uint.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactId);
        var where = query.Length == 0 ? string.Empty : numeric
            ? $" WHERE {Quote(id.Name)} = @id"
            : $" WHERE ({string.Join(" OR ", names.Select(column => $"{Quote(column.Name)} LIKE @search"))})";
        var sql = $"SELECT {string.Join(',', selected.Select(column => Quote(column.Name)))} FROM {Quote(table.Name)}{where} ORDER BY {Quote(id.Name)} LIMIT @limit";
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 60 };
        if (numeric) command.Parameters.AddWithValue("@id", exactId); else if (query.Length > 0) command.Parameters.AddWithValue("@search", $"%{query}%");
        command.Parameters.AddWithValue("@limit", limit + 1);
        var entries = new List<ReferenceLookupEntry>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader[id.Name]; if (value is DBNull) continue;
            var rawId = Convert.ToUInt64(value, CultureInfo.InvariantCulture); if (rawId == 0 || rawId > uint.MaxValue) continue;
            var name = names.Select(column => reader[column.Name] is DBNull ? null : Convert.ToString(reader[column.Name], CultureInfo.InvariantCulture)).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;
            var description = string.Join(" · ", details.Select(column => (Column: column.Name, Value: reader[column.Name] is DBNull ? null : Convert.ToString(reader[column.Name], CultureInfo.InvariantCulture))).Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => $"{pair.Column}: {pair.Value}"));
            entries.Add(new((uint)rawId, name, table.Name, description));
        }
        var hasMore = entries.Count > limit; if (hasMore) entries.RemoveRange(limit, entries.Count - limit);
        return new(domain, query, entries, hasMore, [table.Name]);
    }

    public static ReferenceLookupPage SearchDbc(ReferenceDomain domain, WdbcFile file, IReadOnlyList<DbcColumn> columns,
        int idColumnIndex, int nameColumnIndex, string? query, int limit = 250, params int[] detailColumnIndices)
    {
        query = query?.Trim() ?? string.Empty; limit = Math.Clamp(limit, 1, 1000);
        if (file.FieldCount != columns.Count || idColumnIndex < 0 || idColumnIndex >= columns.Count || nameColumnIndex < -1 || nameColumnIndex >= columns.Count)
            throw new InvalidDataException("The DBC reference definition does not match the selected file.");
        var numeric = uint.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactId); var entries = new List<ReferenceLookupEntry>(limit + 1);
        for (var row = 0; row < file.RowCount && entries.Count <= limit; row++)
        {
            var id = file.GetRaw(row, columns[idColumnIndex]); if (id == 0) continue;
            var name = nameColumnIndex < 0 ? string.Empty : Convert.ToString(file.GetDisplayValue(row, columns[nameColumnIndex]), CultureInfo.InvariantCulture) ?? string.Empty;
            var details = string.Join(" · ", detailColumnIndices.Where(index => index >= 0 && index < columns.Count).Select(index => $"{columns[index].Name}: {Convert.ToString(file.GetDisplayValue(row, columns[index]), CultureInfo.InvariantCulture)}"));
            if (query.Length > 0 && (numeric ? id != exactId : !name.Contains(query, StringComparison.OrdinalIgnoreCase) && !details.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
            entries.Add(new(id, name, Path.GetFileName(file.SourcePath), details));
        }
        var hasMore = entries.Count > limit; if (hasMore) entries.RemoveRange(limit, entries.Count - limit);
        return new(domain, query, entries, hasMore, [Path.GetFileName(file.SourcePath)]);
    }

    public static ReferenceLookupPage Merge(ReferenceDomain domain, string? query, int limit, params ReferenceLookupPage[] pages)
    {
        limit = Math.Clamp(limit, 1, 1000); var sources = pages.SelectMany(page => page.Sources).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = pages.SelectMany(page => page.Entries).GroupBy(entry => entry.Id).Select(group =>
        {
            var preferred = group.OrderByDescending(entry => !string.IsNullOrWhiteSpace(entry.Name)).ThenBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase).First();
            var allSources = string.Join(" + ", group.Select(entry => entry.Source).Distinct(StringComparer.OrdinalIgnoreCase));
            var details = string.Join(" · ", group.Select(entry => entry.Details).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
            return preferred with { Source = allSources, Details = details };
        }).OrderBy(entry => entry.Id).Take(limit + 1).ToList();
        var hasMore = pages.Any(page => page.HasMore) || entries.Count > limit; if (entries.Count > limit) entries.RemoveRange(limit, entries.Count - limit);
        return new(domain, query?.Trim() ?? string.Empty, entries, hasMore, sources);
    }

    private static string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";
}
