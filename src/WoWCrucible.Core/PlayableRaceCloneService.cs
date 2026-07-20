using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record PlayableRaceClonePlan(
    string Format,
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string ProjectRoot,
    string ProjectName,
    string DbcRoot,
    string SchemaPath,
    uint SourceRaceId,
    uint TargetRaceId,
    string SourceRaceName,
    string TargetRaceName,
    string TargetClientPrefix,
    string TargetFileToken,
    uint SourceRaceMask,
    uint TargetRaceMask,
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
    public string PreviewSql() => PlayableBundleSqlService.PreviewSql("race", SourceRaceName, SourceRaceId, TargetRaceName, TargetRaceId, DatabaseTarget, SqlTables);
}

public sealed record PlayableRaceCloneResult(string OutputRoot, string PlanPath, string ReceiptPath, string SqlPath,
    string ManifestPath, string PatchPath, IReadOnlyDictionary<string, string> OutputSha256, PlayableRaceClonePlan Plan);

public sealed class PlayableRaceCloneService
{
    public const string PlanFormat = "wow-crucible-playable-race-clone";
    public const int PlanFormatVersion = 1;
    private static readonly (string Table, string RaceColumn)[] DirectTables =
    [
        ("BarberShopStyle", "Race"), ("CharacterFacialHairStyles", "RaceID"), ("CharBaseInfo", "RaceID"),
        ("CharHairGeosets", "RaceID"), ("CharHairTextures", "Race"), ("CharSections", "RaceID"),
        ("CharStartOutfit", "RaceID"), ("EmotesTextSound", "RaceID"), ("NameGen", "RaceID"), ("VocalUISounds", "RaceID")
    ];
    private static readonly (string Table, string[] MaskColumns)[] MaskTables =
    [
        ("DanceMoves", ["Racemask"]), ("Faction", ["ReputationRaceMask[0]", "ReputationRaceMask[1]", "ReputationRaceMask[2]", "ReputationRaceMask[3]"]),
        ("SkillLineAbility", ["RaceMask"]), ("SkillRaceClassInfo", ["RaceMask"]), ("TalentTab", ["RaceMask"])
    ];
    private static readonly string[] DbcTables = ["ChrRaces", .. DirectTables.Select(value => value.Table), .. MaskTables.Select(value => value.Table)];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions HashJson = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public async Task<PlayableRaceClonePlan> CreatePlanAsync(string projectRoot, string dbcRoot, string schemaPath,
        uint sourceRaceId, uint targetRaceId, string targetRaceName, string targetClientPrefix, string targetFileToken,
        DatabaseConnectionProfile profile, DatabaseCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        projectRoot = RequiredDirectory(projectRoot, "Content project"); var project = CrucibleContentProjectService.Load(projectRoot);
        if (!project.TargetProfile.Equals(TargetProfileCatalog.DefaultProfileId, StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Playable race bundle authoring is currently verified only for WotLK 3.3.5a build 12340 projects.");
        ValidateRaceId(sourceRaceId, nameof(sourceRaceId)); ValidateRaceId(targetRaceId, nameof(targetRaceId)); if (sourceRaceId == targetRaceId) throw new ArgumentException("Source and target race IDs must differ.");
        targetRaceName = RequiredText(targetRaceName, "Target race name"); targetClientPrefix = NormalizeToken(targetClientPrefix, "Client prefix", 4); targetFileToken = NormalizeToken(targetFileToken, "Client file token", 32);
        var registry = CrucibleContentProjectService.LoadRegistry(projectRoot); if (!registry.Reservations.Any(reservation => reservation.Domain == ContentIdDomain.Race && reservation.Values.Contains(targetRaceId))) throw new InvalidOperationException($"Race ID {targetRaceId:N0} is not reserved in this project's Race namespace. Reserve it through Projects & shared IDs first.");
        dbcRoot = RequiredDirectory(dbcRoot, "Authoritative DBC folder"); schemaPath = RequiredFile(schemaPath, "WotLK schema"); var schema = DbcSchemaCatalog.Load(schemaPath);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var plans = new List<PlayableBundleDbcTablePlan>(); var blockers = new List<string>(); var warnings = new List<string>(); var sourceName = $"Race {sourceRaceId}";
        foreach (var table in DbcTables)
        {
            cancellationToken.ThrowIfCancellationRequested(); var path = RequiredTablePath(dbcRoot, table); var file = WdbcFile.Load(path); var resolution = Exact(schema, table, file); hashes[table] = Hash(path);
            if (table == "ChrRaces")
            {
                var rows = DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy); if (!rows.TryGetValue(sourceRaceId, out var sourceRow)) blockers.Add($"ChrRaces.dbc has no source race ID {sourceRaceId:N0}.");
                else sourceName = Convert.ToString(file.GetDisplayValue(sourceRow, Column(resolution, "Name_Lang[enUS]")), CultureInfo.InvariantCulture) ?? sourceName;
                if (rows.ContainsKey(targetRaceId)) blockers.Add($"ChrRaces.dbc already contains target race ID {targetRaceId:N0}.");
                plans.Add(new(table, "Clone complete source row, replace ID/all enUS names/client prefix/client file token", 1)); continue;
            }
            var direct = DirectTables.FirstOrDefault(value => value.Table == table); if (direct.Table is not null)
            {
                var race = Column(resolution, direct.RaceColumn); var sourceRows = Enumerable.Range(0, file.RowCount).Where(row => file.GetRaw(row, race) == sourceRaceId).ToArray();
                if (Enumerable.Range(0, file.RowCount).Any(row => file.GetRaw(row, race) == targetRaceId)) blockers.Add($"{table}.dbc already contains target-race rows.");
                if (sourceRows.Length == 0) warnings.Add($"{table}.dbc has no source-race rows to clone."); plans.Add(new(table, $"Clone complete source-race rows and replace {direct.RaceColumn}", sourceRows.Length)); continue;
            }
            var maskPlan = MaskTables.First(value => value.Table == table); var maskColumns = maskPlan.MaskColumns.Select(name => Column(resolution, name)).ToArray(); var sourceMask = RaceMask(sourceRaceId); var targetMask = RaceMask(targetRaceId);
            var affected = Enumerable.Range(0, file.RowCount).Count(row => maskColumns.Any(column => (file.GetRaw(row, column) & sourceMask) != 0 && (file.GetRaw(row, column) & targetMask) == 0));
            plans.Add(new(table, $"Add target race bit to {string.Join(", ", maskPlan.MaskColumns)} wherever source access exists", affected));
        }
        var sql = await PlayableBundleSqlService.InspectAsync(PlayableBundleIdentityKind.Race, sourceRaceId, targetRaceId, RaceMask(sourceRaceId), RaceMask(targetRaceId), profile, capabilities, blockers, cancellationToken); warnings.AddRange(sql.Warnings);
        warnings.Add("This first bundle deliberately reuses the source race's male/female display IDs and client assets. Import or author a complete model/texture/animation/GlueXML chain before claiming a visually distinct race.");
        warnings.Add("CreatureDisplayInfoExtra is NPC appearance data. Crucible does not create unreferenced duplicate NPC rows; promote a complete dependent CreatureDisplayInfo chain separately when the new race needs custom NPC appearances.");
        warnings.Add("Core race enums/masks, character-create GlueXML, faction behavior, language, cinematics, achievements, spells, and runtime handlers still require separate reviewed work before the new race is fully playable.");
        warnings.Add("Building writes a new DBC/SQL/manifest/MPQ bundle only. It does not mutate the configured server, live database, or client.");
        var plan = new PlayableRaceClonePlan(PlanFormat, PlanFormatVersion, DateTimeOffset.UtcNow, projectRoot, project.Name, dbcRoot, schemaPath, sourceRaceId, targetRaceId, sourceName, targetRaceName, targetClientPrefix, targetFileToken, RaceMask(sourceRaceId), RaceMask(targetRaceId), Hash(schemaPath), hashes, sql.Target, plans, sql.Tables, blockers.Distinct(StringComparer.Ordinal).ToArray(), warnings.Distinct(StringComparer.Ordinal).ToArray(), string.Empty);
        return plan with { ContentSha256 = ContentHash(plan) };
    }

    public void SavePlan(string path, PlayableRaceClonePlan plan, bool overwrite = false) { ValidatePlan(plan); AtomicJson(path, plan, overwrite); }
    public PlayableRaceClonePlan LoadPlan(string path) { var plan = JsonSerializer.Deserialize<PlayableRaceClonePlan>(File.ReadAllText(Path.GetFullPath(path)), Json) ?? throw new InvalidDataException("Playable race plan is empty."); ValidatePlan(plan); return plan; }

    public async Task<PlayableRaceCloneResult> BuildAsync(PlayableRaceClonePlan plan, DatabaseConnectionProfile profile, string outputRoot, CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan); if (!plan.Ready) throw new InvalidOperationException($"Playable race plan has {plan.Blockers.Count:N0} blocker(s); nothing was built."); ValidateTarget(plan.DatabaseTarget, profile);
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var fresh = await CreatePlanAsync(plan.ProjectRoot, plan.DbcRoot, plan.SchemaPath, plan.SourceRaceId, plan.TargetRaceId, plan.TargetRaceName, plan.TargetClientPrefix, plan.TargetFileToken, profile, capabilities, cancellationToken);
        if (!fresh.ContentSha256.Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("DBC, project reservation, database rows, or target schema changed after review; rebuild the race plan.");
        outputRoot = Path.GetFullPath(outputRoot); if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any()) throw new IOException($"Playable race output must be new or empty: {outputRoot}"); var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidOperationException("Output folder has no parent."); Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var dbcOutput = Path.Combine(staging, "DBC"); var sqlOutput = Path.Combine(staging, "SQL"); var manifestOutput = Path.Combine(staging, "Manifests"); Directory.CreateDirectory(dbcOutput); Directory.CreateDirectory(sqlOutput); Directory.CreateDirectory(manifestOutput);
            var schema = DbcSchemaCatalog.Load(plan.SchemaPath); var entries = new List<PatchEntry>(); var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in DbcTables)
            {
                cancellationToken.ThrowIfCancellationRequested(); var sourcePath = RequiredTablePath(plan.DbcRoot, table); var file = WdbcFile.Load(sourcePath); var resolution = Exact(schema, table, file); var changed = false;
                if (table == "ChrRaces")
                {
                    var rows = DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy); var row = file.CloneRowWithId(rows[plan.SourceRaceId], PhysicalKey(resolution, table), plan.TargetRaceId);
                    foreach (var name in new[] { "Name_Lang[enUS]", "Name_Female_Lang[enUS]", "Name_Male_Lang[enUS]" }) file.SetDisplayValue(row, Column(resolution, name), plan.TargetRaceName);
                    file.SetDisplayValue(row, Column(resolution, "ClientPrefix"), plan.TargetClientPrefix); file.SetDisplayValue(row, Column(resolution, "ClientFilestring"), plan.TargetFileToken); changed = true;
                }
                else
                {
                    var direct = DirectTables.FirstOrDefault(value => value.Table == table); if (direct.Table is not null)
                    {
                        var race = Column(resolution, direct.RaceColumn); var sourceRows = Enumerable.Range(0, file.RowCount).Where(row => file.GetRaw(row, race) == plan.SourceRaceId).ToArray(); var key = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy);
                        foreach (var sourceRow in sourceRows) { var row = key is null ? file.CloneRow(sourceRow) : file.CloneRow(sourceRow, key); file.SetRaw(row, race, plan.TargetRaceId); changed = true; }
                    }
                    else
                    {
                        var maskPlan = MaskTables.First(value => value.Table == table); foreach (var mask in maskPlan.MaskColumns.Select(name => Column(resolution, name))) for (var row = 0; row < file.RowCount; row++) { var current = file.GetRaw(row, mask); if ((current & plan.SourceRaceMask) == 0 || (current & plan.TargetRaceMask) != 0) continue; file.SetRaw(row, mask, current | plan.TargetRaceMask); changed = true; }
                    }
                }
                if (!changed) continue; var output = Path.Combine(dbcOutput, table + ".dbc"); file.Save(output, false); var reloaded = WdbcFile.Load(output); if (reloaded.RowCount != file.RowCount || reloaded.FieldCount != file.FieldCount) throw new InvalidDataException($"Written {table}.dbc failed independent structural reload validation."); hashes[Path.GetRelativePath(staging, output)] = Hash(output); entries.Add(new(output, $@"DBFilesClient\{table}.dbc"));
            }
            var sqlPath = Path.Combine(sqlOutput, $"race-{plan.TargetRaceId}-clone.sql"); await File.WriteAllTextAsync(sqlPath, plan.PreviewSql(), new UTF8Encoding(false), cancellationToken); hashes[Path.GetRelativePath(staging, sqlPath)] = Hash(sqlPath);
            var planPath = Path.Combine(staging, "race-clone.plan.json"); SavePlan(planPath, plan); hashes[Path.GetRelativePath(staging, planPath)] = Hash(planPath);
            var manifestPath = Path.Combine(manifestOutput, "race-clone.patch.json"); var patchName = $"patch-Crucible-Race-{plan.TargetRaceId}.MPQ"; PatchManifestService.Save(manifestPath, $"Playable race {plan.TargetRaceName} ({plan.TargetRaceId})", patchName, entries, policy: new([@"DBFilesClient\*.dbc"], null, entries.Count, entries.Select(entry => entry.ArchivePath).ToArray()));
            PatchManifestService.Build(manifestPath, staging); var patchPath = Path.Combine(staging, patchName); var validation = PatchManifestService.Validate(PatchManifestService.Load(manifestPath), patchPath); if (!validation.Passed) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message))); hashes[Path.GetRelativePath(staging, manifestPath)] = Hash(manifestPath); hashes[Path.GetRelativePath(staging, patchPath)] = Hash(patchPath);
            var receiptPath = Path.Combine(staging, "race-clone.receipt.json"); var final = new PlayableRaceCloneResult(outputRoot, Path.Combine(outputRoot, Path.GetFileName(planPath)), Path.Combine(outputRoot, Path.GetFileName(receiptPath)), Path.Combine(outputRoot, "SQL", Path.GetFileName(sqlPath)), Path.Combine(outputRoot, "Manifests", Path.GetFileName(manifestPath)), Path.Combine(outputRoot, Path.GetFileName(patchPath)), hashes, plan);
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(final, Json), new UTF8Encoding(false), cancellationToken); hashes[Path.GetRelativePath(staging, receiptPath)] = Hash(receiptPath); if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot); Directory.Move(staging, outputRoot); return final with { OutputSha256 = hashes };
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static DbcSchemaResolution Exact(DbcSchemaCatalog schema, string table, WdbcFile file) { var result = schema.ResolveColumns(table, file.FieldCount); if (result.MatchKind != DbcSchemaMatchKind.NamedMatch || result.Columns.Count != file.FieldCount) throw new InvalidDataException($"{table}.dbc requires an exact named WotLK schema; resolved {result.MatchKind} and {result.Columns.Count:N0}/{file.FieldCount:N0} fields."); return result; }
    private static DbcColumn PhysicalKey(DbcSchemaResolution resolution, string table) => DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy) ?? throw new InvalidDataException($"{table}.dbc requires a physical record key.");
    private static DbcColumn Column(DbcSchemaResolution resolution, string name) => resolution.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"DBC schema is missing {name}.");
    private static uint RaceMask(uint id) => 1u << checked((int)id - 1);
    private static void ValidateRaceId(uint id, string name) { if (id is < 1 or > 31) throw new ArgumentOutOfRangeException(name, "WotLK playable race IDs must be from 1 through 31 because masks are 32-bit."); }
    private static string NormalizeToken(string value, string label, int maximum) { value = RequiredText(value, label); if (value.Length > maximum || value.Any(character => character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_')) throw new InvalidDataException($"{label} must contain 1–{maximum} ASCII letters, digits, or underscores."); return value; }
    private static string RequiredText(string value, string label) { value = value?.Trim() ?? string.Empty; if (value.Length == 0) throw new ArgumentException($"{label} is required."); return value; }
    private static string RequiredDirectory(string path, string label) { path = Path.GetFullPath(path ?? string.Empty); if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"{label} does not exist: {path}"); return path; }
    private static string RequiredFile(string path, string label) { path = Path.GetFullPath(path ?? string.Empty); if (!File.Exists(path)) throw new FileNotFoundException($"{label} does not exist.", path); return path; }
    private static string RequiredTablePath(string root, string table) => Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(table + ".dbc", StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException($"Required {table}.dbc is unavailable.", Path.Combine(root, table + ".dbc"));
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string ContentHash(PlayableRaceClonePlan plan) { object sqlTables = plan.SqlTables.Any(table => table.Rows.Any(row => row.IsGuardedUpdate)) ? plan.SqlTables : LegacySqlIdentity(plan.SqlTables); var identity = new { plan.Format, plan.FormatVersion, plan.ProjectRoot, plan.ProjectName, plan.DbcRoot, plan.SchemaPath, plan.SourceRaceId, plan.TargetRaceId, plan.SourceRaceName, plan.TargetRaceName, plan.TargetClientPrefix, plan.TargetFileToken, plan.SourceRaceMask, plan.TargetRaceMask, plan.SchemaSha256, plan.DbcSha256, plan.DatabaseTarget, plan.DbcTables, SqlTables = sqlTables, plan.Blockers, plan.Warnings }; return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, HashJson))); }
    private static object LegacySqlIdentity(IReadOnlyList<PlayableBundleSqlTablePlan> tables) => tables.Select(table => new { table.Table, table.Selector, table.SourceRows, table.AlreadyCovered, table.Conflicts, Rows = table.Rows.Select(row => new { row.SourceKey, row.TargetKey, row.Values }).ToArray() }).ToArray();
    private static void ValidatePlan(PlayableRaceClonePlan plan) { if (plan.Format != PlanFormat || plan.FormatVersion != PlanFormatVersion) throw new InvalidDataException("Unsupported playable race plan format."); if (!ContentHash(plan).Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Playable race plan content hash is invalid."); }
    private static void ValidateTarget(PlayableBundleDatabaseTarget target, DatabaseConnectionProfile profile) { if (!target.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) || target.Port != profile.Port || !target.User.Equals(profile.User, StringComparison.Ordinal) || !target.Database.Equals(profile.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected database connection does not match the race plan's reviewed target."); }
    private static void AtomicJson(string path, object value, bool overwrite) { path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Output already exists: {path}"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + $".{Guid.NewGuid():N}.tmp"; try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false)); File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
}
