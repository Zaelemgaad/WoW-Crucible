using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record PlayableClassDatabaseTarget(string Host, uint Port, string User, string Database, string ServerVersion,
    IReadOnlyDictionary<string, string> TableSchemaSha256);
public sealed record PlayableClassSqlValue(string Column, LegacyDatabaseAuditValue Value);
public sealed record PlayableClassSqlRow(IReadOnlyList<LegacyDatabaseAuditKeyPart> SourceKey,
    IReadOnlyList<LegacyDatabaseAuditKeyPart> TargetKey, IReadOnlyList<PlayableClassSqlValue> Values);
public sealed record PlayableClassSqlTablePlan(string Table, string Selector, int SourceRows, int AlreadyCovered,
    int Conflicts, IReadOnlyList<PlayableClassSqlRow> Rows);
public sealed record PlayableClassDbcTablePlan(string Table, string Action, int AffectedRows);
public sealed record PlayableClassClonePlan(
    string Format,
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string ProjectRoot,
    string ProjectName,
    string DbcRoot,
    string SchemaPath,
    uint SourceClassId,
    uint TargetClassId,
    string SourceClassName,
    string TargetClassName,
    string TargetFileToken,
    uint DisplayPower,
    uint SourceClassMask,
    uint TargetClassMask,
    string SchemaSha256,
    IReadOnlyDictionary<string, string> DbcSha256,
    PlayableClassDatabaseTarget DatabaseTarget,
    IReadOnlyList<PlayableClassDbcTablePlan> DbcTables,
    IReadOnlyList<PlayableClassSqlTablePlan> SqlTables,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string ContentSha256)
{
    public bool Ready => Blockers.Count == 0;
    public int SqlRows => SqlTables.Sum(table => table.Rows.Count);
    public int DbcRows => DbcTables.Sum(table => table.AffectedRows);

    public string PreviewSql()
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- WoW Crucible project-scoped playable class clone");
        builder.AppendLine($"-- {SourceClassName} ({SourceClassId}) -> {TargetClassName} ({TargetClassId})");
        builder.AppendLine($"-- Target: {DatabaseTarget.User}@{DatabaseTarget.Host}:{DatabaseTarget.Port}/{DatabaseTarget.Database}");
        builder.AppendLine("-- Additive INSERT migration. No source class row is updated or deleted.");
        builder.AppendLine("START TRANSACTION;");
        foreach (var table in SqlTables)
        foreach (var row in table.Rows)
        {
            var values = row.Values;
            builder.Append("INSERT INTO ").Append(Quote(table.Table)).Append(" (")
                .Append(string.Join(',', values.Select(value => Quote(value.Column)))).Append(") VALUES (")
                .Append(string.Join(',', values.Select(value => Literal(value.Value)))).AppendLine(");");
        }
        builder.AppendLine("COMMIT;");
        return builder.ToString();
    }

    private static string Quote(string value) => ItemWritePlan.QuoteIdentifier(value);
    private static string Literal(LegacyDatabaseAuditValue value) => value.State switch
    {
        LegacyDatabaseAuditValueState.Null => "NULL",
        LegacyDatabaseAuditValueState.Binary => $"X'{Convert.ToHexString(Convert.FromBase64String(value.Value ?? string.Empty))}'",
        LegacyDatabaseAuditValueState.Scalar => $"CONVERT(X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value.Value ?? string.Empty))}' USING utf8mb4)",
        _ => throw new InvalidDataException("Playable class SQL contains an unresolved value.")
    };
}

public sealed record PlayableClassCloneResult(string OutputRoot, string PlanPath, string ReceiptPath, string SqlPath,
    string ManifestPath, string PatchPath, IReadOnlyDictionary<string, string> OutputSha256, PlayableClassClonePlan Plan);

public sealed class PlayableClassCloneService
{
    public const string PlanFormat = "wow-crucible-playable-class-clone";
    public const int PlanFormatVersion = 1;
    private static readonly string[] DbcTables = ["ChrClasses", "CharBaseInfo", "CharStartOutfit", "SkillLineAbility", "SkillRaceClassInfo", "TalentTab"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions HashJson = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public async Task<PlayableClassClonePlan> CreatePlanAsync(string projectRoot, string dbcRoot, string schemaPath,
        uint sourceClassId, uint targetClassId, string targetClassName, string targetFileToken, uint? displayPower,
        DatabaseConnectionProfile profile, DatabaseCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        projectRoot = RequiredDirectory(projectRoot, "Content project"); var project = CrucibleContentProjectService.Load(projectRoot);
        if (!project.TargetProfile.Equals(TargetProfileCatalog.DefaultProfileId, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Playable class bundle authoring is currently verified only for WotLK 3.3.5a build 12340 projects.");
        ValidateClassId(sourceClassId, nameof(sourceClassId)); ValidateClassId(targetClassId, nameof(targetClassId));
        if (sourceClassId == targetClassId) throw new ArgumentException("Source and target class IDs must differ.");
        targetClassName = RequiredText(targetClassName, "Target class name"); targetFileToken = NormalizeFileToken(targetFileToken);
        var registry = CrucibleContentProjectService.LoadRegistry(projectRoot);
        if (!registry.Reservations.Any(reservation => reservation.Domain == ContentIdDomain.Class && reservation.Values.Contains(targetClassId)))
            throw new InvalidOperationException($"Class ID {targetClassId:N0} is not reserved in this project's Class namespace. Reserve it through Projects & shared IDs first.");
        dbcRoot = RequiredDirectory(dbcRoot, "Authoritative DBC folder"); schemaPath = RequiredFile(schemaPath, "WotLK schema");
        if (!profile.Database.Equals(capabilities.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The database profile and inspected capabilities name different databases.");

        var schema = DbcSchemaCatalog.Load(schemaPath); var dbcHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dbcPlans = new List<PlayableClassDbcTablePlan>(); var blockers = new List<string>(); var warnings = new List<string>();
        string sourceName = $"Class {sourceClassId}"; uint resolvedPower = 0;
        foreach (var table in DbcTables)
        {
            cancellationToken.ThrowIfCancellationRequested(); var path = RequiredTablePath(dbcRoot, table); var file = WdbcFile.Load(path); var resolution = Exact(schema, table, file); dbcHashes[table] = Hash(path);
            if (table == "ChrClasses")
            {
                var key = PhysicalKey(resolution, table); var rows = DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy);
                if (!rows.TryGetValue(sourceClassId, out var sourceRow)) blockers.Add($"ChrClasses.dbc has no source class ID {sourceClassId:N0}.");
                else
                {
                    sourceName = Convert.ToString(file.GetDisplayValue(sourceRow, Column(resolution, "Name_Lang[enUS]")), CultureInfo.InvariantCulture) ?? sourceName;
                    resolvedPower = file.GetRaw(sourceRow, Column(resolution, "DisplayPower"));
                }
                if (rows.ContainsKey(targetClassId)) blockers.Add($"ChrClasses.dbc already contains target class ID {targetClassId:N0}.");
                dbcPlans.Add(new(table, "Clone complete source row, replace physical ID/name/file token/primary display power", 1));
                _ = key;
            }
            else if (table == "CharBaseInfo")
            {
                var classColumn = Column(resolution, "ClassID"); var raceColumn = Column(resolution, "RaceID");
                var sourceRows = Enumerable.Range(0, file.RowCount).Where(row => file.GetRaw(row, classColumn) == sourceClassId).ToArray();
                var targetRaces = Enumerable.Range(0, file.RowCount).Where(row => file.GetRaw(row, classColumn) == targetClassId).Select(row => file.GetRaw(row, raceColumn)).ToHashSet();
                if (sourceRows.Length == 0) blockers.Add($"CharBaseInfo.dbc exposes no playable race for source class {sourceClassId:N0}.");
                if (sourceRows.Any(row => targetRaces.Contains(file.GetRaw(row, raceColumn)))) blockers.Add("CharBaseInfo.dbc already contains one or more target race/class pairs.");
                dbcPlans.Add(new(table, "Append each source-class race pair with the target class byte; virtual row identities remain append-only", sourceRows.Length));
            }
            else if (table == "CharStartOutfit")
            {
                var classColumn = Column(resolution, "ClassID"); var count = Enumerable.Range(0, file.RowCount).Count(row => file.GetRaw(row, classColumn) == sourceClassId);
                if (count == 0) warnings.Add($"CharStartOutfit.dbc has no source outfit for class {sourceClassId:N0}; no client starting outfit can be cloned.");
                if (Enumerable.Range(0, file.RowCount).Any(row => file.GetRaw(row, classColumn) == targetClassId)) blockers.Add("CharStartOutfit.dbc already contains target-class rows.");
                dbcPlans.Add(new(table, "Clone complete source outfits to newly allocated physical row IDs and replace ClassID", count));
            }
            else
            {
                var mask = Column(resolution, "ClassMask"); var sourceMask = ClassMask(sourceClassId); var targetMask = ClassMask(targetClassId);
                var count = Enumerable.Range(0, file.RowCount).Count(row => (file.GetRaw(row, mask) & sourceMask) != 0 && (file.GetRaw(row, mask) & targetMask) == 0);
                dbcPlans.Add(new(table, "Add the target class bit to every source-access mask without removing existing classes", count));
            }
        }
        resolvedPower = displayPower ?? resolvedPower;
        if (resolvedPower > 6) blockers.Add($"DisplayPower {resolvedPower:N0} is outside the verified WotLK power-type range 0–6.");

        var sqlPlans = new List<PlayableClassSqlTablePlan>(); var tableFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var candidates = capabilities.Tables.Values.Where(IsClassTable).OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!candidates.Any(table => table.Name.Equals("playercreateinfo", StringComparison.OrdinalIgnoreCase))) blockers.Add("The connected schema has no playercreateinfo table.");
        if (!candidates.Any(table => table.Name.Contains("class", StringComparison.OrdinalIgnoreCase) && table.Name.Contains("stat", StringComparison.OrdinalIgnoreCase))) warnings.Add("No recognized class-stat table was found; the class may have no level/stat curve on this core.");
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        foreach (var table in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested(); tableFingerprints[table.Name] = CacheServerPlanService.SchemaFingerprint(table);
            var direct = DirectClassColumn(table); var mask = direct is null ? MaskClassColumn(table) : null;
            var result = direct is not null
                ? await PlanDirectTableAsync(connection, table, direct, sourceClassId, targetClassId, blockers, cancellationToken)
                : await PlanMaskTableAsync(connection, table, mask!, ClassMask(sourceClassId), ClassMask(targetClassId), blockers, cancellationToken);
            sqlPlans.Add(result);
        }
        warnings.Add("WotLK ChrClasses.DisplayPower controls one primary client power bar. Rage, energy, runic power, form sharing, and simultaneous custom resource systems still require separately reviewed core/UI/spell work; this bundle never claims otherwise.");
        warnings.Add("The bundle clones source-class availability, starting data, masks, and complete recognized SQL rows. Review class-family spells, talents, UI strings, GlueXML, core enums/bitmasks, and runtime handlers before treating the new class as playable.");
        warnings.Add("Building writes a new DBC/SQL/manifest/MPQ bundle only. It does not mutate the configured server, live database, or client.");

        var target = new PlayableClassDatabaseTarget(profile.Host, profile.Port, profile.User, profile.Database, capabilities.ServerVersion, tableFingerprints);
        var plan = new PlayableClassClonePlan(PlanFormat, PlanFormatVersion, DateTimeOffset.UtcNow, projectRoot, project.Name, dbcRoot, schemaPath,
            sourceClassId, targetClassId, sourceName, targetClassName, targetFileToken, resolvedPower, ClassMask(sourceClassId), ClassMask(targetClassId),
            Hash(schemaPath), dbcHashes, target, dbcPlans, sqlPlans, blockers.Distinct(StringComparer.Ordinal).ToArray(), warnings.Distinct(StringComparer.Ordinal).ToArray(), string.Empty);
        return plan with { ContentSha256 = ContentHash(plan) };
    }

    public void SavePlan(string path, PlayableClassClonePlan plan, bool overwrite = false)
    {
        ValidatePlan(plan); AtomicJson(path, plan, overwrite);
    }

    public PlayableClassClonePlan LoadPlan(string path)
    {
        var plan = JsonSerializer.Deserialize<PlayableClassClonePlan>(File.ReadAllText(Path.GetFullPath(path)), Json) ?? throw new InvalidDataException("Playable class plan is empty.");
        ValidatePlan(plan); return plan;
    }

    public async Task<PlayableClassCloneResult> BuildAsync(PlayableClassClonePlan plan, DatabaseConnectionProfile profile, string outputRoot,
        CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan); if (!plan.Ready) throw new InvalidOperationException($"Playable class plan has {plan.Blockers.Count:N0} blocker(s); nothing was built.");
        ValidateTarget(plan.DatabaseTarget, profile); var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var fresh = await CreatePlanAsync(plan.ProjectRoot, plan.DbcRoot, plan.SchemaPath, plan.SourceClassId, plan.TargetClassId, plan.TargetClassName, plan.TargetFileToken, plan.DisplayPower, profile, capabilities, cancellationToken);
        if (!fresh.ContentSha256.Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("DBC, project reservation, database rows, or target schema changed after review; rebuild the class plan.");
        outputRoot = Path.GetFullPath(outputRoot); if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any()) throw new IOException($"Playable class output must be new or empty: {outputRoot}");
        var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidOperationException("Output folder has no parent."); Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var dbcOutput = Path.Combine(staging, "DBC"); var sqlOutput = Path.Combine(staging, "SQL"); var manifestOutput = Path.Combine(staging, "Manifests"); Directory.CreateDirectory(dbcOutput); Directory.CreateDirectory(sqlOutput); Directory.CreateDirectory(manifestOutput);
            var schema = DbcSchemaCatalog.Load(plan.SchemaPath); var entries = new List<PatchEntry>(); var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in DbcTables)
            {
                cancellationToken.ThrowIfCancellationRequested(); var sourcePath = RequiredTablePath(plan.DbcRoot, table); var file = WdbcFile.Load(sourcePath); var resolution = Exact(schema, table, file); var changed = false;
                if (table == "ChrClasses")
                {
                    var key = PhysicalKey(resolution, table); var rows = DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy); var sourceRow = rows[plan.SourceClassId];
                    var row = file.CloneRowWithId(sourceRow, key, plan.TargetClassId); file.SetDisplayValue(row, Column(resolution, "Name_Lang[enUS]"), plan.TargetClassName); file.SetDisplayValue(row, Column(resolution, "Filename"), plan.TargetFileToken); file.SetRaw(row, Column(resolution, "DisplayPower"), plan.DisplayPower); changed = true;
                }
                else if (table == "CharBaseInfo")
                {
                    var classColumn = Column(resolution, "ClassID"); var sourceRows = Enumerable.Range(0, file.RowCount).Where(row => file.GetRaw(row, classColumn) == plan.SourceClassId).ToArray();
                    foreach (var sourceRow in sourceRows) { var row = file.CloneRow(sourceRow); file.SetRaw(row, classColumn, plan.TargetClassId); changed = true; }
                }
                else if (table == "CharStartOutfit")
                {
                    var key = PhysicalKey(resolution, table); var classColumn = Column(resolution, "ClassID"); var sourceRows = Enumerable.Range(0, file.RowCount).Where(row => file.GetRaw(row, classColumn) == plan.SourceClassId).ToArray();
                    foreach (var sourceRow in sourceRows) { var row = file.CloneRow(sourceRow, key); file.SetRaw(row, classColumn, plan.TargetClassId); changed = true; }
                }
                else
                {
                    var mask = Column(resolution, "ClassMask"); for (var row = 0; row < file.RowCount; row++) { var current = file.GetRaw(row, mask); if ((current & plan.SourceClassMask) == 0 || (current & plan.TargetClassMask) != 0) continue; file.SetRaw(row, mask, current | plan.TargetClassMask); changed = true; }
                }
                if (!changed) continue; var output = Path.Combine(dbcOutput, table + ".dbc"); file.Save(output, false); var reloaded = WdbcFile.Load(output); if (reloaded.RowCount != file.RowCount || reloaded.FieldCount != file.FieldCount) throw new InvalidDataException($"Written {table}.dbc failed independent structural reload validation.");
                hashes[Path.GetRelativePath(staging, output)] = Hash(output); entries.Add(new(output, $@"DBFilesClient\{table}.dbc"));
            }
            var sqlPath = Path.Combine(sqlOutput, $"class-{plan.TargetClassId}-clone.sql"); await File.WriteAllTextAsync(sqlPath, plan.PreviewSql(), new UTF8Encoding(false), cancellationToken); hashes[Path.GetRelativePath(staging, sqlPath)] = Hash(sqlPath);
            var planPath = Path.Combine(staging, "class-clone.plan.json"); SavePlan(planPath, plan); hashes[Path.GetRelativePath(staging, planPath)] = Hash(planPath);
            var manifestPath = Path.Combine(manifestOutput, "class-clone.patch.json"); var patchName = $"patch-Crucible-Class-{plan.TargetClassId}.MPQ";
            PatchManifestService.Save(manifestPath, $"Playable class {plan.TargetClassName} ({plan.TargetClassId})", patchName, entries, policy: new([@"DBFilesClient\*.dbc"], null, entries.Count, entries.Select(entry => entry.ArchivePath).ToArray()));
            PatchManifestService.Build(manifestPath, staging); var patchPath = Path.Combine(staging, patchName); var validation = PatchManifestService.Validate(PatchManifestService.Load(manifestPath), patchPath); if (!validation.Passed) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));
            hashes[Path.GetRelativePath(staging, manifestPath)] = Hash(manifestPath); hashes[Path.GetRelativePath(staging, patchPath)] = Hash(patchPath);
            var receiptPath = Path.Combine(staging, "class-clone.receipt.json"); var final = new PlayableClassCloneResult(outputRoot, Path.Combine(outputRoot, Path.GetFileName(planPath)), Path.Combine(outputRoot, Path.GetFileName(receiptPath)), Path.Combine(outputRoot, "SQL", Path.GetFileName(sqlPath)), Path.Combine(outputRoot, "Manifests", Path.GetFileName(manifestPath)), Path.Combine(outputRoot, Path.GetFileName(patchPath)), hashes, plan);
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(final, Json), new UTF8Encoding(false), cancellationToken); hashes[Path.GetRelativePath(staging, receiptPath)] = Hash(receiptPath);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot); Directory.Move(staging, outputRoot); return final with { OutputSha256 = hashes };
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static async Task<PlayableClassSqlTablePlan> PlanDirectTableAsync(MySqlConnection connection, DatabaseTableCapability table, DatabaseColumnCapability selector,
        uint sourceClass, uint targetClass, ICollection<string> blockers, CancellationToken cancellationToken)
    {
        var source = await ReadRowsAsync(connection, table, $"{Quote(selector.Name)} <=> @value", sourceClass, cancellationToken); var target = await ReadRowsAsync(connection, table, $"{Quote(selector.Name)} <=> @value", targetClass, cancellationToken);
        var targetByKey = target.GroupBy(row => Identity(table, row, null), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal); var planned = new List<PlayableClassSqlRow>(); var covered = 0; var conflicts = 0;
        foreach (var row in source)
        {
            var changed = new Dictionary<string, LegacyDatabaseAuditValue>(row, StringComparer.OrdinalIgnoreCase) { [selector.Name] = Scalar(targetClass) }; var identity = Identity(table, changed, null);
            if (targetByKey.TryGetValue(identity, out var existing)) { if (existing.Any(candidate => RowsEqual(changed, candidate))) covered++; else { conflicts++; blockers.Add($"{table.Name} target identity {DisplayKey(table, changed)} already exists with different values."); } continue; }
            planned.Add(ToPlanRow(table, row, changed));
        }
        return new(table.Name, $"{selector.Name}={sourceClass}", source.Count, covered, conflicts, planned);
    }

    private static async Task<PlayableClassSqlTablePlan> PlanMaskTableAsync(MySqlConnection connection, DatabaseTableCapability table, DatabaseColumnCapability selector,
        uint sourceMask, uint targetMask, ICollection<string> blockers, CancellationToken cancellationToken)
    {
        var source = await ReadRowsAsync(connection, table, $"({Quote(selector.Name)} & @value) <> 0", sourceMask, cancellationToken); var target = await ReadRowsAsync(connection, table, $"({Quote(selector.Name)} & @value) <> 0", targetMask, cancellationToken);
        var targetBySemanticKey = target.GroupBy(row => Identity(table, row, selector.Name), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var planned = new List<PlayableClassSqlRow>(); var covered = 0; var conflicts = 0;
        foreach (var row in source)
        {
            var changed = new Dictionary<string, LegacyDatabaseAuditValue>(row, StringComparer.OrdinalIgnoreCase) { [selector.Name] = Scalar(targetMask) }; var identity = Identity(table, changed, selector.Name);
            if (targetBySemanticKey.TryGetValue(identity, out var candidates))
            {
                if (candidates.Any(candidate => RowsEqualExcept(changed, candidate, selector.Name))) covered++;
                else { conflicts++; blockers.Add($"{table.Name} already has target-mask data for {DisplayKey(table, changed, selector.Name)} with different values."); }
                continue;
            }
            planned.Add(ToPlanRow(table, row, changed));
        }
        return new(table.Name, $"{selector.Name} includes 0x{sourceMask:X8}", source.Count, covered, conflicts, planned);
    }

    private static async Task<List<Dictionary<string, LegacyDatabaseAuditValue>>> ReadRowsAsync(MySqlConnection connection, DatabaseTableCapability table, string where, object value, CancellationToken cancellationToken)
    {
        var columns = table.Columns.Where(column => !column.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).ToArray();
        var primary = columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).ToArray(); var order = primary.Length == 0 ? string.Empty : $" ORDER BY {string.Join(',', primary.Select(column => Quote(column.Name)))}";
        await using var command = new MySqlCommand($"SELECT {string.Join(',', columns.Select(column => Quote(column.Name)))} FROM {Quote(table.Name)} WHERE {where}{order} LIMIT 100001", connection) { CommandTimeout = 120 }; command.Parameters.AddWithValue("@value", value);
        var rows = new List<Dictionary<string, LegacyDatabaseAuditValue>>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count == 100_000) throw new InvalidDataException($"{table.Name} matched more than 100,000 class rows; refine the adapter before cloning.");
            var row = new Dictionary<string, LegacyDatabaseAuditValue>(StringComparer.OrdinalIgnoreCase); for (var index = 0; index < reader.FieldCount; index++) row[reader.GetName(index)] = Encode(reader.IsDBNull(index) ? null : reader.GetValue(index)); rows.Add(row);
        }
        rows.Sort((left, right) => string.CompareOrdinal(Identity(table, left, null), Identity(table, right, null))); return rows;
    }

    private static PlayableClassSqlRow ToPlanRow(DatabaseTableCapability table, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> source, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> target)
    {
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).ToArray();
        IReadOnlyList<LegacyDatabaseAuditKeyPart> Key(IReadOnlyDictionary<string, LegacyDatabaseAuditValue> row) => (primary.Length > 0 ? primary.Select(column => column.Name) : row.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)).Select(name => new LegacyDatabaseAuditKeyPart(name, row[name])).ToArray();
        var values = table.Columns.Where(column => target.ContainsKey(column.Name) && !column.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).Select(column => new PlayableClassSqlValue(column.Name, target[column.Name])).ToArray();
        return new(Key(source), Key(target), values);
    }

    private static string Identity(DatabaseTableCapability table, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> row, string? omit)
    {
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase) && !column.Name.Equals(omit, StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).Select(column => column.Name).ToArray();
        var names = primary.Length > 0 ? primary : row.Keys.Where(name => !name.Equals(omit, StringComparison.OrdinalIgnoreCase)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        return string.Join('\u001e', names.Select(name => $"{name}\u001d{(int)row[name].State}\u001d{row[name].Value}"));
    }

    private static string DisplayKey(DatabaseTableCapability table, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> row, string? omit = null)
    {
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase) && !column.Name.Equals(omit, StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).Select(column => column.Name).ToArray();
        var names = primary.Length > 0 ? primary : row.Keys.Where(name => !name.Equals(omit, StringComparison.OrdinalIgnoreCase)).Take(4).ToArray(); return string.Join(", ", names.Select(name => $"{name}={row[name].Value ?? "NULL"}"));
    }

    private static bool RowsEqual(IReadOnlyDictionary<string, LegacyDatabaseAuditValue> left, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> right) => RowsEqualExcept(left, right, null);
    private static bool RowsEqualExcept(IReadOnlyDictionary<string, LegacyDatabaseAuditValue> left, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> right, string? omit) =>
        left.Where(pair => !pair.Key.Equals(omit, StringComparison.OrdinalIgnoreCase)).All(pair => right.TryGetValue(pair.Key, out var value) && Equal(pair.Value, value)) &&
        right.Keys.Where(name => !name.Equals(omit, StringComparison.OrdinalIgnoreCase)).All(left.ContainsKey);
    private static bool Equal(LegacyDatabaseAuditValue left, LegacyDatabaseAuditValue right) => left.State == right.State && string.Equals(left.Value, right.Value, StringComparison.Ordinal);
    private static LegacyDatabaseAuditValue Encode(object? value) => value switch { null or DBNull => LegacyDatabaseAuditValue.Null, byte[] bytes => new(LegacyDatabaseAuditValueState.Binary, Convert.ToBase64String(bytes)), DateTime date => new(LegacyDatabaseAuditValueState.Scalar, date.ToString("O", CultureInfo.InvariantCulture)), DateTimeOffset date => new(LegacyDatabaseAuditValueState.Scalar, date.ToString("O", CultureInfo.InvariantCulture)), TimeSpan span => new(LegacyDatabaseAuditValueState.Scalar, span.ToString("c", CultureInfo.InvariantCulture)), IFormattable formattable => new(LegacyDatabaseAuditValueState.Scalar, formattable.ToString(null, CultureInfo.InvariantCulture)), _ => new(LegacyDatabaseAuditValueState.Scalar, Convert.ToString(value, CultureInfo.InvariantCulture)) };
    private static LegacyDatabaseAuditValue Scalar(uint value) => new(LegacyDatabaseAuditValueState.Scalar, value.ToString(CultureInfo.InvariantCulture));

    private static bool IsClassTable(DatabaseTableCapability table) =>
        (table.Name.StartsWith("playercreateinfo", StringComparison.OrdinalIgnoreCase) && (DirectClassColumn(table) is not null || MaskClassColumn(table) is not null)) ||
        table.Name.Equals("player_class_stats", StringComparison.OrdinalIgnoreCase) || table.Name.Equals("player_classlevelstats", StringComparison.OrdinalIgnoreCase) || table.Name.Equals("player_levelstats", StringComparison.OrdinalIgnoreCase);
    private static DatabaseColumnCapability? DirectClassColumn(DatabaseTableCapability table) => table.Find("class") ?? table.Find("Class");
    private static DatabaseColumnCapability? MaskClassColumn(DatabaseTableCapability table) => table.Find("classMask") ?? table.Find("classmask");

    private static DbcSchemaResolution Exact(DbcSchemaCatalog schema, string table, WdbcFile file) { var result = schema.ResolveColumns(table, file.FieldCount); if (result.MatchKind != DbcSchemaMatchKind.NamedMatch || result.Columns.Count != file.FieldCount) throw new InvalidDataException($"{table}.dbc requires an exact named WotLK schema; resolved {result.MatchKind} and {result.Columns.Count:N0}/{file.FieldCount:N0} fields."); return result; }
    private static DbcColumn PhysicalKey(DbcSchemaResolution resolution, string table) => DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy) ?? throw new InvalidDataException($"{table}.dbc requires a physical record key.");
    private static DbcColumn Column(DbcSchemaResolution resolution, string name) => resolution.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"DBC schema is missing {name}.");
    private static uint ClassMask(uint id) => 1u << checked((int)id - 1);
    private static void ValidateClassId(uint id, string name) { if (id is < 1 or > 31) throw new ArgumentOutOfRangeException(name, "WotLK playable class IDs must be from 1 through 31 because masks are 32-bit."); }
    private static string NormalizeFileToken(string value) { value = RequiredText(value, "Class file token").Trim().ToUpperInvariant(); if (value.Length > 32 || value.Any(character => character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_')) throw new InvalidDataException("Class file token must contain 1–32 ASCII uppercase letters, digits, or underscores."); return value; }
    private static string RequiredText(string value, string label) { value = value?.Trim() ?? string.Empty; if (value.Length == 0) throw new ArgumentException($"{label} is required."); return value; }
    private static string RequiredDirectory(string path, string label) { path = Path.GetFullPath(path ?? string.Empty); if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"{label} does not exist: {path}"); return path; }
    private static string RequiredFile(string path, string label) { path = Path.GetFullPath(path ?? string.Empty); if (!File.Exists(path)) throw new FileNotFoundException($"{label} does not exist.", path); return path; }
    private static string RequiredTablePath(string root, string table) => Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(table + ".dbc", StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException($"Required {table}.dbc is unavailable.", Path.Combine(root, table + ".dbc"));
    private static string Quote(string value) => ItemWritePlan.QuoteIdentifier(value);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string ContentHash(PlayableClassClonePlan plan)
    {
        var identity = new { plan.Format, plan.FormatVersion, plan.ProjectRoot, plan.ProjectName, plan.DbcRoot, plan.SchemaPath, plan.SourceClassId, plan.TargetClassId, plan.SourceClassName, plan.TargetClassName, plan.TargetFileToken, plan.DisplayPower, plan.SourceClassMask, plan.TargetClassMask, plan.SchemaSha256, plan.DbcSha256, plan.DatabaseTarget, plan.DbcTables, plan.SqlTables, plan.Blockers, plan.Warnings };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, HashJson)));
    }
    private static void ValidatePlan(PlayableClassClonePlan plan) { if (plan.Format != PlanFormat || plan.FormatVersion != PlanFormatVersion) throw new InvalidDataException("Unsupported playable class plan format."); if (!ContentHash(plan).Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Playable class plan content hash is invalid."); }
    private static void ValidateTarget(PlayableClassDatabaseTarget target, DatabaseConnectionProfile profile) { if (!target.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) || target.Port != profile.Port || !target.User.Equals(profile.User, StringComparison.Ordinal) || !target.Database.Equals(profile.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected database connection does not match the class plan's reviewed target."); }
    private static void AtomicJson(string path, object value, bool overwrite) { path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Output already exists: {path}"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + $".{Guid.NewGuid():N}.tmp"; try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false)); File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
}
