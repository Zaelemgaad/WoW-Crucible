using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record LegacyDatabaseSnapshotOptions(
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null,
    bool IncludeSensitiveState = false,
    bool Overwrite = false);

public sealed record LegacyDatabaseSnapshotProgress(string Stage, string? Table, long Rows, int CompletedTables, int TotalTables);

public sealed record LegacyDatabaseSnapshotColumn(
    string Name,
    int Ordinal,
    string DataType,
    string ColumnType,
    bool Nullable,
    string? DefaultValue,
    string Key,
    string Extra,
    string? CharacterSet,
    string? Collation,
    long? CharacterMaximumLength,
    int? NumericPrecision,
    int? NumericScale,
    int? DateTimePrecision,
    string? GenerationExpression);

public sealed record LegacyDatabaseTableSchema(
    string Name,
    string TableType,
    string? Engine,
    string? Collation,
    string? Comment,
    long? EstimatedRows,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<LegacyDatabaseSnapshotColumn> Columns);

public sealed record LegacyDatabaseSnapshotTable(
    string Name,
    string TableType,
    string? Engine,
    string? Collation,
    string? Comment,
    long? EstimatedRows,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<LegacyDatabaseSnapshotColumn> Columns,
    string SchemaSha256,
    string DataEntry,
    string RowOrdering,
    long Rows,
    long UncompressedBytes,
    string RowsSha256);

public sealed record LegacyDatabaseSnapshotIdentity(
    string Database,
    string ServerVersion,
    string? ServerProduct,
    string? CharacterSet,
    string? Collation,
    IReadOnlyDictionary<string, string> CoreIdentity);

public sealed record LegacyDatabaseSnapshotPolicy(
    IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<string> ExcludePatterns,
    bool SensitiveStateIncluded,
    IReadOnlyList<string> ExcludedTables);

public sealed record LegacyDatabaseSnapshotManifest(
    string Format,
    int FormatVersion,
    string ToolVersion,
    DateTimeOffset CapturedUtc,
    LegacyDatabaseSnapshotIdentity Source,
    LegacyDatabaseSnapshotPolicy Policy,
    IReadOnlyList<LegacyDatabaseSnapshotTable> Tables,
    long TotalRows,
    string SchemaSha256,
    string ContentSha256,
    bool ConsistentSnapshotStarted,
    bool ReadOnlyTransactionEnforced);

public sealed record LegacyDatabaseSnapshotResult(string Path, LegacyDatabaseSnapshotManifest Manifest, long ArtifactBytes);
public sealed record LegacyDatabaseSnapshotInspection(LegacyDatabaseSnapshotManifest? Manifest, bool Valid, IReadOnlyList<string> Findings);

/// <summary>
/// Captures an immutable, read-only representation of a legacy world database. The artifact contains schema
/// metadata plus compressed, type-preserving row arrays and deliberately never serializes a connection profile.
/// </summary>
public sealed class LegacyDatabaseSnapshotService
{
    public const string ArtifactFormat = "wow-crucible-legacy-world-snapshot";
    public const int ArtifactFormatVersion = 1;

    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.General) { WriteIndented = true };
    private static readonly string[] WorldSignals =
    [
        "item_template", "creature_template", "gameobject_template", "quest_template", "playercreateinfo", "spell_proc", "spell_dbc"
    ];

    // These are account/character runtime state, not reusable world definitions. A deliberate --include-sensitive
    // is required to capture them even if a mixed or incorrectly selected database happens to contain world tables.
    private static readonly string[] SensitiveStatePatterns =
    [
        // Authentication/realm state.
        "account", "account_access", "account_banned", "account_muted", "auth_*", "ip_banned", "logs", "logs_ip_actions",
        "realm", "realmcharacters", "realmlist", "secret_digest", "uptime",
        // Character/account runtime state. Patterns are intentionally narrow: world definitions such as
        // mail_loot_template, instance_template, guild_rewards, and pet_levelstats must remain capturable.
        "account_data", "account_instance_times", "account_tutorial", "addons", "arena_team", "arena_team_member", "arena_team_stats",
        "auctionhouse", "banned_addons", "bugreport", "calendar_events", "calendar_invites", "channels", "characters", "character_account_data",
        "character_achievement", "character_achievement_progress", "character_action", "character_aura", "character_banned", "character_battleground_data",
        "character_battleground_random", "character_declinedname", "character_equipmentsets", "character_gifts", "character_glyphs", "character_homebind",
        "character_instance", "character_inventory", "character_pet", "character_pet_declinedname", "character_queststatus*", "character_reputation",
        "character_skills", "character_social", "character_spell", "character_spell_cooldown", "character_stats", "character_talent", "character_void_storage",
        "corpse", "game_event_condition_save", "game_event_save", "gm_subsurvey", "gm_survey", "gm_ticket", "group_instance", "group_member", "groups",
        "guild", "guild_achievement", "guild_bank_eventlog", "guild_bank_item", "guild_bank_right", "guild_bank_tab", "guild_eventlog", "guild_member",
        "guild_member_withdraw", "guild_newslog", "guild_rank", "instance", "instance_reset", "item_instance", "item_loot_items", "item_loot_money",
        "lag_reports", "lfg_data", "mail", "mail_items", "pet_aura", "pet_spell", "pet_spell_cooldown", "petition", "petition_sign",
        "pool_quest_save", "pvpstats_*", "quest_tracker", "reserved_name", "respawn", "warden_action", "worldstates"
    ];

    public async Task<LegacyDatabaseSnapshotResult> CaptureAsync(
        DatabaseConnectionProfile profile,
        string outputPath,
        LegacyDatabaseSnapshotOptions? options = null,
        IProgress<LegacyDatabaseSnapshotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        var fullOutput = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutput) ?? throw new ArgumentException("Snapshot output must have a parent directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(fullOutput) && !options.Overwrite)
            throw new IOException($"Snapshot already exists: {fullOutput}. Use overwrite explicitly if replacement is intended.");

        var temporary = Path.Combine(outputDirectory, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var connection = new MySqlConnection(BuildSnapshotConnectionString(profile));
            await connection.OpenAsync(cancellationToken);

            var snapshotTransaction = await TryBeginReadOnlySnapshotAsync(connection, cancellationToken);
            progress?.Report(new("Reading schema", null, 0, 0, 0));
            var identity = await ReadIdentityAsync(connection, profile.Database, cancellationToken);
            var allTables = await ReadSchemaAsync(connection, profile.Database, cancellationToken);
            var selected = SelectTables(allTables, options, enforceWorldDatabase: true, out var excluded);
            if (selected.Count == 0)
                throw new InvalidOperationException("The snapshot filters selected zero world-content tables.");

            var capturedTables = new List<LegacyDatabaseSnapshotTable>(selected.Count);
            var totalRows = 0L;
            LegacyDatabaseSnapshotManifest? completedManifest = null;

            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                {
                    for (var index = 0; index < selected.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var schema = selected[index];
                        progress?.Report(new("Capturing", schema.Name, 0, index, selected.Count));
                        var captured = await CaptureTableAsync(connection, archive, schema, index, selected.Count, progress, cancellationToken);
                        capturedTables.Add(captured);
                        totalRows = checked(totalRows + captured.Rows);
                        progress?.Report(new("Captured", schema.Name, captured.Rows, index + 1, selected.Count));
                    }

                    progress?.Report(new("Verifying schema", null, totalRows, selected.Count, selected.Count));
                    await VerifySchemasUnchangedAsync(connection, profile.Database, selected, cancellationToken);
                    await EndSnapshotAsync(connection, snapshotTransaction.Started, cancellationToken);

                    var orderedTables = capturedTables.OrderBy(table => table.Name, StringComparer.Ordinal).ToArray();
                    completedManifest = new LegacyDatabaseSnapshotManifest(
                        ArtifactFormat,
                        ArtifactFormatVersion,
                        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                        DateTimeOffset.UtcNow,
                        identity,
                        new(options.IncludePatterns?.ToArray() ?? [], options.ExcludePatterns?.ToArray() ?? [], options.IncludeSensitiveState, excluded),
                        orderedTables,
                        totalRows,
                        ComputeSchemaAggregateHash(orderedTables.Select(table => (table.Name, table.SchemaSha256))),
                        ComputeAggregateHash(orderedTables.Select(table => (table.Name, table.RowsSha256, table.Rows))),
                        snapshotTransaction.Started,
                        snapshotTransaction.ReadOnlyEnforced);

                    var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    manifestEntry.LastWriteTime = completedManifest.CapturedUtc;
                    await using var manifestStream = manifestEntry.Open();
                    await JsonSerializer.SerializeAsync(manifestStream, completedManifest, ArtifactJson, cancellationToken);
                }
                await file.FlushAsync(cancellationToken);
            }

            File.Move(temporary, fullOutput, options.Overwrite);
            return new(fullOutput, completedManifest ?? throw new InvalidDataException("Snapshot manifest was not completed."), new FileInfo(fullOutput).Length);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public async Task<LegacyDatabaseSnapshotInspection> InspectAsync(string artifactPath, bool verifyRows = true, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();
        LegacyDatabaseSnapshotManifest? manifest = null;
        try
        {
            await using var file = new FileStream(Path.GetFullPath(artifactPath), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
            var manifestEntries = archive.Entries.Where(entry => entry.FullName.Equals("manifest.json", StringComparison.Ordinal)).ToArray();
            if (manifestEntries.Length != 1)
                return new(null, false, [$"Artifact must contain exactly one manifest.json; found {manifestEntries.Length}."]);
            if (manifestEntries[0].Length > 64L * 1024 * 1024)
                return new(null, false, ["manifest.json exceeds the 64 MiB safety limit."]);
            await using (var manifestStream = manifestEntries[0].Open())
                manifest = await JsonSerializer.DeserializeAsync<LegacyDatabaseSnapshotManifest>(manifestStream, ArtifactJson, cancellationToken);
            if (manifest is null) return new(null, false, ["manifest.json is empty or invalid."]);
            if (!string.Equals(manifest.Format, ArtifactFormat, StringComparison.Ordinal)) findings.Add($"Unsupported artifact format '{manifest.Format}'.");
            if (manifest.FormatVersion != ArtifactFormatVersion) findings.Add($"Unsupported artifact version {manifest.FormatVersion}.");
            if (manifest.Tables is null) return new(manifest, false, ["manifest.json has no table collection."]);
            if (manifest.Source is null) findings.Add("manifest.json has no source database identity.");
            if (manifest.Policy is null) findings.Add("manifest.json has no capture policy.");
            if (manifest.TotalRows < 0) findings.Add("Manifest total row count cannot be negative.");
            if (manifest.ReadOnlyTransactionEnforced && !manifest.ConsistentSnapshotStarted) findings.Add("Manifest claims a read-only transaction without a started snapshot transaction.");
            if (!IsSha256(manifest.SchemaSha256) || !IsSha256(manifest.ContentSha256)) findings.Add("Manifest aggregate hashes are not SHA-256 values.");

            var duplicateTableNames = manifest.Tables.GroupBy(table => table.Name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicateTableNames.Length > 0) findings.Add($"Duplicate table metadata: {string.Join(", ", duplicateTableNames)}.");
            var duplicateDataEntries = manifest.Tables.GroupBy(table => table.DataEntry, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicateDataEntries.Length > 0) findings.Add($"Multiple tables declare the same data entry: {string.Join(", ", duplicateDataEntries)}.");
            var duplicateEntries = archive.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicateEntries.Length > 0) findings.Add($"Duplicate ZIP entries: {string.Join(", ", duplicateEntries)}.");
            var declaredEntries = manifest.Tables.Select(table => table.DataEntry).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
            foreach (var unexpected in archive.Entries.Where(entry => !declaredEntries.Contains(entry.FullName))) findings.Add($"Unexpected ZIP entry: {unexpected.FullName}.");

            foreach (var table in manifest.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (table.Columns is null || table.PrimaryKey is null || string.IsNullOrWhiteSpace(table.Name) || string.IsNullOrWhiteSpace(table.DataEntry))
                {
                    findings.Add("A table has missing name, entry, column, or primary-key metadata.");
                    continue;
                }
                if (table.Rows < 0 || table.UncompressedBytes < 0)
                {
                    findings.Add($"{table.Name}: row and byte counts cannot be negative.");
                    continue;
                }
                if (!IsSha256(table.SchemaSha256) || !IsSha256(table.RowsSha256))
                {
                    findings.Add($"{table.Name}: schema or row-data hash is not a SHA-256 value.");
                    continue;
                }
                var duplicateColumns = table.Columns.GroupBy(column => column.Name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
                if (duplicateColumns.Length > 0) findings.Add($"{table.Name}: duplicate column metadata ({string.Join(", ", duplicateColumns)}).");
                if (!table.Columns.Select(column => column.Ordinal).SequenceEqual(Enumerable.Range(1, table.Columns.Count)))
                    findings.Add($"{table.Name}: column ordinals are not contiguous and ordered from 1.");
                if (table.PrimaryKey.Count != table.PrimaryKey.Distinct(StringComparer.Ordinal).Count() || table.PrimaryKey.Any(key => !table.Columns.Any(column => column.Name.Equals(key, StringComparison.Ordinal))))
                    findings.Add($"{table.Name}: primary-key metadata is duplicated or references an unknown column.");
                var expectedDataEntry = $"tables/{Uri.EscapeDataString(table.Name)}.rows.json";
                if (!table.DataEntry.Equals(expectedDataEntry, StringComparison.Ordinal))
                {
                    findings.Add($"{table.Name}: invalid data-entry path '{table.DataEntry}' (expected '{expectedDataEntry}').");
                    continue;
                }
                if (!ComputeSchemaHash(ToSchema(table)).Equals(table.SchemaSha256, StringComparison.OrdinalIgnoreCase))
                    findings.Add($"{table.Name}: schema hash mismatch.");
                var entries = archive.Entries.Where(entry => entry.FullName.Equals(table.DataEntry, StringComparison.Ordinal)).ToArray();
                if (entries.Length != 1)
                {
                    findings.Add($"{table.Name}: expected one '{table.DataEntry}' entry, found {entries.Length}.");
                    continue;
                }
                if (entries[0].Length != table.UncompressedBytes) findings.Add($"{table.Name}: ZIP and manifest uncompressed byte counts differ.");
                await using var entryStream = entries[0].Open();
                using var hashing = new HashingReadStream(entryStream);
                long rows = 0;
                if (verifyRows)
                {
                    await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(hashing, cancellationToken: cancellationToken))
                    {
                        if (row.ValueKind != JsonValueKind.Array)
                        {
                            findings.Add($"{table.Name}: row {rows + 1:N0} is not a column array.");
                            break;
                        }
                        if (row.GetArrayLength() != table.Columns.Count)
                        {
                            findings.Add($"{table.Name}: row {rows + 1:N0} has {row.GetArrayLength()} values for {table.Columns.Count} columns.");
                            break;
                        }
                        rows++;
                    }
                }
                else
                {
                    await hashing.CopyToAsync(Stream.Null, cancellationToken);
                    rows = table.Rows;
                }
                var hash = hashing.GetHashAndReset();
                if (!hash.Equals(table.RowsSha256, StringComparison.OrdinalIgnoreCase)) findings.Add($"{table.Name}: row-data hash mismatch.");
                if (verifyRows && rows != table.Rows) findings.Add($"{table.Name}: manifest says {table.Rows:N0} rows but artifact contains {rows:N0}.");
                if (hashing.BytesRead != table.UncompressedBytes) findings.Add($"{table.Name}: uncompressed byte count mismatch.");
            }

            if (manifest.Tables.All(table => IsSha256(table.SchemaSha256) && IsSha256(table.RowsSha256)))
            {
                var expectedSchema = ComputeSchemaAggregateHash(manifest.Tables.OrderBy(table => table.Name, StringComparer.Ordinal).Select(table => (table.Name, table.SchemaSha256)));
                var expectedContent = ComputeAggregateHash(manifest.Tables.OrderBy(table => table.Name, StringComparer.Ordinal).Select(table => (table.Name, table.RowsSha256, table.Rows)));
                if (!expectedSchema.Equals(manifest.SchemaSha256, StringComparison.OrdinalIgnoreCase)) findings.Add("Aggregate schema hash mismatch.");
                if (!expectedContent.Equals(manifest.ContentSha256, StringComparison.OrdinalIgnoreCase)) findings.Add("Aggregate content hash mismatch.");
            }
            if (manifest.Tables.Sum(table => table.Rows) != manifest.TotalRows) findings.Add("Manifest total row count does not equal the sum of its table row counts.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            findings.Add(exception.Message);
        }
        return new(manifest, findings.Count == 0, findings);
    }

    public static IReadOnlyList<LegacyDatabaseTableSchema> SelectTables(
        IReadOnlyList<LegacyDatabaseTableSchema> allTables,
        LegacyDatabaseSnapshotOptions? options,
        bool enforceWorldDatabase,
        out IReadOnlyList<string> excludedTables)
    {
        options ??= new();
        var baseTables = allTables.Where(table => table.TableType.Equals("BASE TABLE", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (enforceWorldDatabase && !options.IncludeSensitiveState && !baseTables.Any(table => WorldSignals.Contains(table.Name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This does not look like a world database (no item_template, creature_template, quest_template, playercreateinfo, or known world spell table). Refusing to capture likely auth/character state. Select the world database, or explicitly use --include-sensitive if that is truly intended.");

        var includes = ValidatePatterns(options.IncludePatterns, "include");
        var excludes = ValidatePatterns(options.ExcludePatterns, "exclude");
        var excluded = allTables.Where(table => !table.TableType.Equals("BASE TABLE", StringComparison.OrdinalIgnoreCase)).Select(table => table.Name).ToList();
        var selected = new List<LegacyDatabaseTableSchema>();
        foreach (var table in baseTables.OrderBy(table => table.Name, StringComparer.Ordinal))
        {
            var included = includes.Length == 0 || includes.Any(pattern => GlobMatches(table.Name, pattern));
            var explicitlyExcluded = excludes.Any(pattern => GlobMatches(table.Name, pattern));
            var sensitive = !options.IncludeSensitiveState && SensitiveStatePatterns.Any(pattern => GlobMatches(table.Name, pattern));
            if (included && !explicitlyExcluded && !sensitive) selected.Add(table);
            else excluded.Add(table.Name);
        }
        excludedTables = excluded;
        return selected;
    }

    private static string[] ValidatePatterns(IReadOnlyList<string>? patterns, string kind)
    {
        if (patterns is null) return [];
        if (patterns.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException($"Snapshot {kind} patterns cannot be empty.");
        return patterns.Select(pattern => pattern.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool GlobMatches(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var expression = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    public static string ComputeSchemaHash(LegacyDatabaseTableSchema schema)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema.Name,
            schema.TableType,
            schema.Engine,
            schema.Collation,
            schema.Comment,
            schema.PrimaryKey,
            schema.Columns
        }, new JsonSerializerOptions(JsonSerializerDefaults.General));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task<LegacyDatabaseSnapshotTable> CaptureTableAsync(
        MySqlConnection connection,
        ZipArchive archive,
        LegacyDatabaseTableSchema schema,
        int completedTables,
        int totalTables,
        IProgress<LegacyDatabaseSnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entryName = $"tables/{Uri.EscapeDataString(schema.Name)}.rows.json";
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        entry.LastWriteTime = DateTimeOffset.UtcNow;
        var columnList = string.Join(",", schema.Columns.Select(column => QuoteIdentifier(column.Name)));
        var ordering = schema.PrimaryKey.Count > 0 ? string.Join(",", schema.PrimaryKey) : "capture-order (table has no primary key)";
        var orderSql = schema.PrimaryKey.Count == 0 ? string.Empty : " ORDER BY " + string.Join(",", schema.PrimaryKey.Select(QuoteIdentifier));
        var sql = $"SELECT {columnList} FROM {QuoteIdentifier(schema.Name)}{orderSql}";
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        await using var entryStream = entry.Open();
        using var hashing = new HashingWriteStream(entryStream);
        using (var writer = new Utf8JsonWriter(hashing, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartArray();
            long rows = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                writer.WriteStartArray();
                for (var column = 0; column < reader.FieldCount; column++) WriteValue(writer, reader, column);
                writer.WriteEndArray();
                rows++;
                if (rows % 100_000 == 0)
                {
                    writer.Flush();
                    progress?.Report(new("Capturing", schema.Name, rows, completedTables, totalTables));
                }
            }
            writer.WriteEndArray();
            writer.Flush();
            var rowsHash = hashing.GetHashAndReset();
            return new(schema.Name, schema.TableType, schema.Engine, schema.Collation, schema.Comment, schema.EstimatedRows,
                schema.PrimaryKey, schema.Columns, ComputeSchemaHash(schema), entryName, ordering, rows, hashing.BytesWritten, rowsHash);
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, MySqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNullValue();
            return;
        }
        var value = reader.GetValue(ordinal);
        switch (value)
        {
            case byte[] bytes:
                writer.WriteStartObject(); writer.WriteBase64String("$binary", bytes); writer.WriteEndObject(); break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeSpan timeSpan:
                writer.WriteStringValue(timeSpan.ToString("c", CultureInfo.InvariantCulture)); break;
            case IFormattable formattable:
                writer.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture)); break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static async Task<IReadOnlyList<LegacyDatabaseTableSchema>> ReadSchemaAsync(MySqlConnection connection, string database, CancellationToken cancellationToken)
    {
        const string tableSql = """
            SELECT TABLE_NAME, TABLE_TYPE, ENGINE, TABLE_COLLATION, TABLE_COMMENT, TABLE_ROWS
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @database
            ORDER BY TABLE_NAME
            """;
        var tables = new Dictionary<string, MutableTable>(StringComparer.Ordinal);
        await using (var command = new MySqlCommand(tableSql, connection) { CommandTimeout = 60 })
        {
            command.Parameters.AddWithValue("@database", database);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                tables[name] = new(name, reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3), NullableString(reader, 4), reader.IsDBNull(5) ? null : reader.GetInt64(5));
            }
        }

        var informationSchemaColumns = await ReadInformationSchemaColumnsAsync(connection, cancellationToken);
        var dateTimePrecision = informationSchemaColumns.Contains("DATETIME_PRECISION") ? "DATETIME_PRECISION" : "NULL AS DATETIME_PRECISION";
        var generationExpression = informationSchemaColumns.Contains("GENERATION_EXPRESSION") ? "GENERATION_EXPRESSION" : "NULL AS GENERATION_EXPRESSION";
        var columnSql = $$"""
            SELECT TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT,
                   COLUMN_KEY, EXTRA, CHARACTER_SET_NAME, COLLATION_NAME, CHARACTER_MAXIMUM_LENGTH,
                   NUMERIC_PRECISION, NUMERIC_SCALE, {{dateTimePrecision}}, {{generationExpression}}
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @database
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """;
        await using (var command = new MySqlCommand(columnSql, connection) { CommandTimeout = 60 })
        {
            command.Parameters.AddWithValue("@database", database);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!tables.TryGetValue(reader.GetString(0), out var table)) continue;
                table.Columns.Add(new(reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5).Equals("YES", StringComparison.OrdinalIgnoreCase), NullableString(reader, 6), reader.GetString(7), reader.GetString(8),
                    NullableString(reader, 9), NullableString(reader, 10), NullableLong(reader, 11), NullableInt(reader, 12), NullableInt(reader, 13),
                    NullableInt(reader, 14), NullableString(reader, 15)));
            }
        }

        const string keySql = """
            SELECT TABLE_NAME, COLUMN_NAME
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @database AND INDEX_NAME = 'PRIMARY'
            ORDER BY TABLE_NAME, SEQ_IN_INDEX
            """;
        await using (var command = new MySqlCommand(keySql, connection) { CommandTimeout = 60 })
        {
            command.Parameters.AddWithValue("@database", database);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (tables.TryGetValue(reader.GetString(0), out var table)) table.PrimaryKey.Add(reader.GetString(1));
        }
        return tables.Values.OrderBy(table => table.Name, StringComparer.Ordinal).Select(table => table.Freeze()).ToArray();
    }

    private static async Task<HashSet<string>> ReadInformationSchemaColumnsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string sql = """
            SELECT COLUMN_NAME
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = 'information_schema' AND TABLE_NAME = 'COLUMNS'
            """;
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<LegacyDatabaseSnapshotIdentity> ReadIdentityAsync(MySqlConnection connection, string database, CancellationToken cancellationToken)
    {
        string version;
        await using (var command = new MySqlCommand("SELECT VERSION()", connection))
            version = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? "unknown";
        string? product = null, characterSet = null, collation = null;
        try
        {
            await using var command = new MySqlCommand("SELECT @@version_comment, @@character_set_database, @@collation_database", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                product = NullableString(reader, 0); characterSet = NullableString(reader, 1); collation = NullableString(reader, 2);
            }
        }
        catch (MySqlException)
        {
            // VERSION() and information_schema are sufficient when restricted accounts cannot read optional variables.
        }

        var coreIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string identitySql = """
                SELECT COLUMN_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @database AND TABLE_NAME = 'version'
                ORDER BY ORDINAL_POSITION
                """;
            var columns = new List<string>();
            await using (var command = new MySqlCommand(identitySql, connection))
            {
                command.Parameters.AddWithValue("@database", database);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var column = reader.GetString(0);
                    if (IsSafeIdentityColumn(column)) columns.Add(column);
                }
            }
            if (columns.Count > 0)
            {
                await using var command = new MySqlCommand($"SELECT {string.Join(",", columns.Select(QuoteIdentifier))} FROM {QuoteIdentifier("version")} LIMIT 1", connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                    for (var index = 0; index < columns.Count; index++)
                        if (!reader.IsDBNull(index))
                        {
                            var value = Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
                            coreIdentity[columns[index]] = value.Length <= 4096 ? value : value[..4096];
                        }
            }
        }
        catch (MySqlException)
        {
            // Core/version tables differ by emulator and permissions; identity discovery is best effort.
        }
        return new(database, version, product, characterSet, collation, coreIdentity);
    }

    private static bool IsSafeIdentityColumn(string column)
    {
        var forbidden = new[] { "password", "passwd", "secret", "token", "credential", "username", "user_name" };
        if (forbidden.Any(fragment => column.Contains(fragment, StringComparison.OrdinalIgnoreCase))) return false;
        var identity = new[] { "version", "revision", "hash", "branch", "build", "release", "cache", "date", "core", "database", "db_" };
        return identity.Any(fragment => column.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task VerifySchemasUnchangedAsync(MySqlConnection connection, string database, IReadOnlyList<LegacyDatabaseTableSchema> capturedSchemas, CancellationToken cancellationToken)
    {
        var current = (await ReadSchemaAsync(connection, database, cancellationToken)).ToDictionary(table => table.Name, StringComparer.Ordinal);
        foreach (var captured in capturedSchemas)
        {
            if (!current.TryGetValue(captured.Name, out var finalSchema))
                throw new InvalidOperationException($"Table '{captured.Name}' disappeared while its snapshot was being captured. No artifact was published.");
            if (!ComputeSchemaHash(captured).Equals(ComputeSchemaHash(finalSchema), StringComparison.Ordinal))
                throw new InvalidOperationException($"Table '{captured.Name}' changed schema while its snapshot was being captured. No artifact was published.");
        }
    }

    private static async Task<(bool Started, bool ReadOnlyEnforced)> TryBeginReadOnlySnapshotAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new MySqlCommand("START TRANSACTION WITH CONSISTENT SNAPSHOT, READ ONLY", connection) { CommandTimeout = 15 };
            await command.ExecuteNonQueryAsync(cancellationToken);
            return (true, true);
        }
        catch (MySqlException)
        {
            // Older MySQL/MariaDB variants may reject READ ONLY. Every remaining command is still hard-coded SELECT.
            try
            {
                await using var fallback = new MySqlCommand("START TRANSACTION WITH CONSISTENT SNAPSHOT", connection) { CommandTimeout = 15 };
                await fallback.ExecuteNonQueryAsync(cancellationToken);
                return (true, false);
            }
            catch (MySqlException)
            {
                // A snapshot can still be captured by a restricted SELECT-only account without transaction support.
                return (false, false);
            }
        }
    }

    private static async Task EndSnapshotAsync(MySqlConnection connection, bool started, CancellationToken cancellationToken)
    {
        if (!started || connection.State != ConnectionState.Open) return;
        try
        {
            await using var command = new MySqlCommand("ROLLBACK", connection) { CommandTimeout = 15 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException)
        {
            // The connection may have dropped after all SELECTs completed. Disposal below still releases the session.
        }
    }

    private static string BuildSnapshotConnectionString(DatabaseConnectionProfile profile)
    {
        var builder = new MySqlConnectionStringBuilder(DatabaseCapabilityService.BuildConnectionString(profile))
        {
            AllowZeroDateTime = true,
            ConvertZeroDateTime = false,
            DefaultCommandTimeout = 0,
            ApplicationName = "WoW Crucible Legacy Snapshot"
        };
        return builder.ConnectionString;
    }

    public static string ComputeAggregateHash(IEnumerable<(string Name, string Hash, long Rows)> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Name)); hash.AppendData([0]);
            hash.AppendData(Encoding.ASCII.GetBytes(entry.Hash.ToLowerInvariant())); hash.AppendData([0]);
            hash.AppendData(Encoding.ASCII.GetBytes(entry.Rows.ToString(CultureInfo.InvariantCulture))); hash.AppendData([(byte)'\n']);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeSchemaAggregateHash(IEnumerable<(string Name, string Hash)> entries) =>
        ComputeAggregateHash(entries.Select(entry => (entry.Name, entry.Hash, 0L)));

    private static LegacyDatabaseTableSchema ToSchema(LegacyDatabaseSnapshotTable table) =>
        new(table.Name, table.TableType, table.Engine, table.Collation, table.Comment, table.EstimatedRows, table.PrimaryKey, table.Columns);

    private static string QuoteIdentifier(string identifier) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    private static string? NullableString(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static long? NullableLong(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static int? NullableInt(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private sealed class MutableTable(string name, string tableType, string? engine, string? collation, string? comment, long? estimatedRows)
    {
        public string Name { get; } = name;
        public List<LegacyDatabaseSnapshotColumn> Columns { get; } = [];
        public List<string> PrimaryKey { get; } = [];
        public LegacyDatabaseTableSchema Freeze() => new(Name, tableType, engine, collation, comment, estimatedRows, PrimaryKey.ToArray(), Columns.ToArray());
    }

    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long BytesWritten { get; private set; }
        public string GetHashAndReset() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); _hash.AppendData(buffer, offset, count); BytesWritten += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); _hash.AppendData(buffer); BytesWritten += buffer.Length; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { await inner.WriteAsync(buffer, cancellationToken); _hash.AppendData(buffer.Span); BytesWritten += buffer.Length; }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long BytesRead { get; private set; }
        public string GetHashAndReset() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); if (read > 0) { _hash.AppendData(buffer, offset, read); BytesRead += read; } return read; }
        public override int Read(Span<byte> buffer) { var read = inner.Read(buffer); if (read > 0) { _hash.AppendData(buffer[..read]); BytesRead += read; } return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await inner.ReadAsync(buffer, cancellationToken); if (read > 0) { _hash.AppendData(buffer.Span[..read]); BytesRead += read; } return read; }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int ReadByte() { Span<byte> value = stackalloc byte[1]; return Read(value) == 0 ? -1 : value[0]; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
