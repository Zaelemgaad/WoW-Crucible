using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

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
    PlayableBundleDatabaseTarget DatabaseTarget,
    IReadOnlyList<PlayableBundleDbcTablePlan> DbcTables,
    IReadOnlyList<PlayableBundleSqlTablePlan> SqlTables,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string ContentSha256)
{
    public bool Ready => Blockers.Count == 0;
    public int SqlRows => SqlTables.Sum(table => table.Rows.Count);
    public int DbcRows => DbcTables.Sum(table => table.AffectedRows);

    public string PreviewSql() => PlayableBundleSqlService.PreviewSql("class", SourceClassName, SourceClassId, TargetClassName, TargetClassId, DatabaseTarget, SqlTables);
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
        var dbcPlans = new List<PlayableBundleDbcTablePlan>(); var blockers = new List<string>(); var warnings = new List<string>();
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

        var sql = await PlayableBundleSqlService.InspectAsync(PlayableBundleIdentityKind.Class, sourceClassId, targetClassId, ClassMask(sourceClassId), ClassMask(targetClassId), profile, capabilities, blockers, cancellationToken);
        var sqlPlans = sql.Tables; warnings.AddRange(sql.Warnings);
        warnings.Add("WotLK ChrClasses.DisplayPower controls one primary client power bar. Rage, energy, runic power, form sharing, and simultaneous custom resource systems still require separately reviewed core/UI/spell work; this bundle never claims otherwise.");
        warnings.Add("The bundle clones source-class availability, starting data, masks, and complete recognized SQL rows. Review class-family spells, talents, UI strings, GlueXML, core enums/bitmasks, and runtime handlers before treating the new class as playable.");
        warnings.Add("Building writes a new DBC/SQL/manifest/MPQ bundle only. It does not mutate the configured server, live database, or client.");

        var plan = new PlayableClassClonePlan(PlanFormat, PlanFormatVersion, DateTimeOffset.UtcNow, projectRoot, project.Name, dbcRoot, schemaPath,
            sourceClassId, targetClassId, sourceName, targetClassName, targetFileToken, resolvedPower, ClassMask(sourceClassId), ClassMask(targetClassId),
            Hash(schemaPath), dbcHashes, sql.Target, dbcPlans, sqlPlans, blockers.Distinct(StringComparer.Ordinal).ToArray(), warnings.Distinct(StringComparer.Ordinal).ToArray(), string.Empty);
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
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string ContentHash(PlayableClassClonePlan plan)
    {
        object sqlTables = plan.SqlTables.Any(table => table.Rows.Any(row => row.IsGuardedUpdate)) ? plan.SqlTables : LegacySqlIdentity(plan.SqlTables);
        var identity = new { plan.Format, plan.FormatVersion, plan.ProjectRoot, plan.ProjectName, plan.DbcRoot, plan.SchemaPath, plan.SourceClassId, plan.TargetClassId, plan.SourceClassName, plan.TargetClassName, plan.TargetFileToken, plan.DisplayPower, plan.SourceClassMask, plan.TargetClassMask, plan.SchemaSha256, plan.DbcSha256, plan.DatabaseTarget, plan.DbcTables, SqlTables = sqlTables, plan.Blockers, plan.Warnings };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, HashJson)));
    }
    private static object LegacySqlIdentity(IReadOnlyList<PlayableBundleSqlTablePlan> tables) => tables.Select(table => new { table.Table, table.Selector, table.SourceRows, table.AlreadyCovered, table.Conflicts, Rows = table.Rows.Select(row => new { row.SourceKey, row.TargetKey, row.Values }).ToArray() }).ToArray();
    private static void ValidatePlan(PlayableClassClonePlan plan) { if (plan.Format != PlanFormat || plan.FormatVersion != PlanFormatVersion) throw new InvalidDataException("Unsupported playable class plan format."); if (!ContentHash(plan).Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Playable class plan content hash is invalid."); }
    private static void ValidateTarget(PlayableBundleDatabaseTarget target, DatabaseConnectionProfile profile) { if (!target.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) || target.Port != profile.Port || !target.User.Equals(profile.User, StringComparison.Ordinal) || !target.Database.Equals(profile.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected database connection does not match the class plan's reviewed target."); }
    private static void AtomicJson(string path, object value, bool overwrite) { path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Output already exists: {path}"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + $".{Guid.NewGuid():N}.tmp"; try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false)); File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
}
