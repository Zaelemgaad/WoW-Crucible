using System.Globalization;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record ContentIdOccupancySource(string Kind, string Name, string Location, int Ids, bool Available, string Detail);
public sealed record ContentIdOccupancyReport(
    ContentIdDomain Domain,
    ContentIdDomain RegistryNamespace,
    DateTimeOffset CheckedUtc,
    IReadOnlyList<uint> OccupiedIds,
    IReadOnlyList<ContentIdOccupancySource> Sources,
    bool Complete,
    IReadOnlyList<string> Warnings)
{
    public uint? MaximumOccupied => OccupiedIds.Count == 0 ? null : OccupiedIds[^1];
}

public sealed class ContentIdOccupancyService
{
    public async Task<ContentIdOccupancyReport> InspectAsync(
        ContentIdDomain domain,
        DatabaseConnectionProfile? profile,
        DatabaseCapabilities? capabilities,
        string? dbcFolder,
        string? schemaPath,
        IEnumerable<uint>? additionalOccupied = null,
        CancellationToken cancellationToken = default)
    {
        var policy = ContentIdDomainCatalog.Get(domain);
        var occupied = additionalOccupied?.ToHashSet() ?? [];
        var sources = new List<ContentIdOccupancySource>();
        var warnings = new List<string>();
        MySqlConnection? connection = null;
        DbcSchemaCatalog? schema = null;
        try
        {
            foreach (var source in policy.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Kind == "SQL")
                {
                    if (profile is null || capabilities is null)
                    {
                        sources.Add(new("SQL", source.Name, profile?.Database ?? "not connected", 0, false, "A verified SQL session is required."));
                        continue;
                    }
                    var table = capabilities.FindTable(source.Name); var column = table?.Find(source.Column ?? string.Empty);
                    if (table is null || column is null)
                    {
                        sources.Add(new("SQL", source.Name, profile.Database, 0, false, $"{source.Name}.{source.Column} is absent from the connected schema."));
                        continue;
                    }
                    connection ??= await OpenAsync(profile, cancellationToken);
                    var sourceIds = new HashSet<uint>();
                    await using var command = new MySqlCommand($"SELECT {ItemWritePlan.QuoteIdentifier(column.Name)} FROM {ItemWritePlan.QuoteIdentifier(table.Name)}", connection) { CommandTimeout = 120 };
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (reader.IsDBNull(0)) continue;
                        var value = Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                        if (value is > 0 and <= uint.MaxValue) sourceIds.Add((uint)value);
                    }
                    occupied.UnionWith(sourceIds);
                    sources.Add(new("SQL", $"{table.Name}.{column.Name}", profile.Database, sourceIds.Count, true, "Read every live identity from the connected schema."));
                    continue;
                }

                if (!Directory.Exists(dbcFolder) || !File.Exists(schemaPath))
                {
                    sources.Add(new("DBC", source.Name, dbcFolder ?? "not configured", 0, false, "A DBC folder and matching schema definition are required."));
                    continue;
                }
                var dbcPath = Path.Combine(Path.GetFullPath(dbcFolder), source.Name + ".dbc");
                if (!File.Exists(dbcPath))
                {
                    sources.Add(new("DBC", source.Name, dbcPath, 0, false, "The expected DBC file is missing."));
                    continue;
                }
                try
                {
                    schema ??= DbcSchemaCatalog.Load(Path.GetFullPath(schemaPath));
                    var file = WdbcFile.Load(dbcPath); var resolution = schema.ResolveColumns(source.Name, file.FieldCount);
                    if (resolution.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey || resolution.MatchKind is DbcSchemaMatchKind.FieldCountMismatchFallback or DbcSchemaMatchKind.MissingTableFallback)
                        throw new InvalidDataException($"{source.Name}.dbc did not resolve to a named schema with a stable key.");
                    var ids = DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy).Keys.Where(id => id != 0).ToArray(); occupied.UnionWith(ids);
                    sources.Add(new("DBC", source.Name, dbcPath, ids.Length, true, $"Read {ids.Length:N0} stable record key(s) from {resolution.MatchKind}."));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    sources.Add(new("DBC", source.Name, dbcPath, 0, false, exception.Message));
                }
            }
        }
        finally { if (connection is not null) await connection.DisposeAsync(); }

        if (additionalOccupied is not null) sources.Add(new("Manual", "Additional occupied IDs", "caller supplied", additionalOccupied.Distinct().Count(), true, "Merged explicit occupied IDs with live sources."));
        if (policy.Sources.Count == 0 && additionalOccupied is null) warnings.Add(policy.Guidance);
        foreach (var missing in sources.Where(source => !source.Available)) warnings.Add($"{missing.Kind} {missing.Name}: {missing.Detail}");
        var complete = policy.Sources.Count == 0
            ? additionalOccupied is not null
            : sources.Where(source => source.Kind is "SQL" or "DBC").All(source => source.Available);
        return new(domain, policy.RegistryNamespace, DateTimeOffset.UtcNow, occupied.Order().ToArray(), sources, complete, warnings);
    }

    private static async Task<MySqlConnection> OpenAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken); return connection;
    }
}
