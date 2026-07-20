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
    public uint? MaleDisplayIdOverride { get; init; }
    public uint? FemaleDisplayIdOverride { get; init; }
    public string? ProcessedAssetLibraryRoot { get; init; }
    public string? RequestedAssetProvenance { get; init; }
    public IReadOnlyList<CreatureDisplayBindingPlan> DisplayBindings { get; init; } = [];
    public string? AppearanceSourceDbcRoot { get; init; }
    public uint? MaleSourceDisplayId { get; init; }
    public uint? FemaleSourceDisplayId { get; init; }
    public CreatureAppearanceBatchPortPlan? AppearancePromotion { get; init; }
    public uint? AppearanceSourceRaceId { get; init; }
    public PlayableRaceCustomizationPromotionPlan? CustomizationPromotion { get; init; }
    public bool Ready => Blockers.Count == 0;
    public int SqlRows => SqlTables.Sum(table => table.Rows.Count);
    public int DbcRows => DbcTables.Sum(table => table.AffectedRows);
    public string PreviewSql() => PlayableBundleSqlService.PreviewSql("race", SourceRaceName, SourceRaceId, TargetRaceName, TargetRaceId, DatabaseTarget, SqlTables);
}

public sealed record PlayableRaceAppearanceOptions(
    uint? MaleDisplayId = null,
    uint? FemaleDisplayId = null,
    string? ProcessedAssetLibraryRoot = null,
    string? RequestedAssetProvenance = null,
    string? SourceDbcRoot = null,
    uint? AppearanceSourceRaceId = null)
{
    public bool Enabled => MaleDisplayId.HasValue || FemaleDisplayId.HasValue || !string.IsNullOrWhiteSpace(ProcessedAssetLibraryRoot) || !string.IsNullOrWhiteSpace(RequestedAssetProvenance) || !string.IsNullOrWhiteSpace(SourceDbcRoot) || AppearanceSourceRaceId.HasValue;
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
        => await CreatePlanAsync(projectRoot, dbcRoot, schemaPath, sourceRaceId, targetRaceId, targetRaceName, targetClientPrefix, targetFileToken,
            profile, capabilities, null, cancellationToken);

    public async Task<PlayableRaceClonePlan> CreatePlanAsync(string projectRoot, string dbcRoot, string schemaPath,
        uint sourceRaceId, uint targetRaceId, string targetRaceName, string targetClientPrefix, string targetFileToken,
        DatabaseConnectionProfile profile, DatabaseCapabilities capabilities, PlayableRaceAppearanceOptions? appearance,
        CancellationToken cancellationToken = default)
    {
        projectRoot = RequiredDirectory(projectRoot, "Content project"); var project = CrucibleContentProjectService.Load(projectRoot);
        if (!project.TargetProfile.Equals(TargetProfileCatalog.DefaultProfileId, StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Playable race bundle authoring is currently verified only for WotLK 3.3.5a build 12340 projects.");
        ValidateRaceId(sourceRaceId, nameof(sourceRaceId)); ValidateRaceId(targetRaceId, nameof(targetRaceId)); if (sourceRaceId == targetRaceId) throw new ArgumentException("Source and target race IDs must differ.");
        targetRaceName = RequiredText(targetRaceName, "Target race name"); targetClientPrefix = NormalizeToken(targetClientPrefix, "Client prefix", 4); targetFileToken = NormalizeToken(targetFileToken, "Client file token", 32);
        var registry = CrucibleContentProjectService.LoadRegistry(projectRoot); if (!registry.Reservations.Any(reservation => reservation.Domain == ContentIdDomain.Race && reservation.Values.Contains(targetRaceId))) throw new InvalidOperationException($"Race ID {targetRaceId:N0} is not reserved in this project's Race namespace. Reserve it through Projects & shared IDs first.");
        dbcRoot = RequiredDirectory(dbcRoot, "Authoritative DBC folder"); schemaPath = RequiredFile(schemaPath, "WotLK schema"); var schema = DbcSchemaCatalog.Load(schemaPath);
        appearance ??= new();
        if (!string.IsNullOrWhiteSpace(appearance.RequestedAssetProvenance) && string.IsNullOrWhiteSpace(appearance.ProcessedAssetLibraryRoot))
            throw new ArgumentException("An exact appearance provenance requires a processed asset library.", nameof(appearance));
        if (!string.IsNullOrWhiteSpace(appearance.SourceDbcRoot) && (!appearance.MaleDisplayId.HasValue || !appearance.FemaleDisplayId.HasValue))
            throw new ArgumentException("Source-layer appearance promotion requires both male and female source display IDs.", nameof(appearance));
        if (appearance.AppearanceSourceRaceId.HasValue && string.IsNullOrWhiteSpace(appearance.SourceDbcRoot)) throw new ArgumentException("A customization source race requires an appearance source DBC folder.", nameof(appearance));
        if (appearance.AppearanceSourceRaceId is { } appearanceRace) ValidateRaceId(appearanceRace, nameof(appearance.AppearanceSourceRaceId));
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var plans = new List<PlayableBundleDbcTablePlan>(); var blockers = new List<string>(); var warnings = new List<string>(); var sourceName = $"Race {sourceRaceId}"; uint sourceMaleDisplay = 0; uint sourceFemaleDisplay = 0;
        foreach (var table in DbcTables)
        {
            cancellationToken.ThrowIfCancellationRequested(); var path = RequiredTablePath(dbcRoot, table); var file = WdbcFile.Load(path); var resolution = Exact(schema, table, file); hashes[table] = Hash(path);
            if (appearance.AppearanceSourceRaceId.HasValue && PlayableRaceCustomizationPromotionService.TableNames.Contains(table)) continue;
            if (table == "ChrRaces")
            {
                var rows = DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy); if (!rows.TryGetValue(sourceRaceId, out var sourceRow)) blockers.Add($"ChrRaces.dbc has no source race ID {sourceRaceId:N0}.");
                else
                {
                    sourceName = Convert.ToString(file.GetDisplayValue(sourceRow, Column(resolution, "Name_Lang[enUS]")), CultureInfo.InvariantCulture) ?? sourceName;
                    sourceMaleDisplay = file.GetRaw(sourceRow, Column(resolution, "MaleDisplayId")); sourceFemaleDisplay = file.GetRaw(sourceRow, Column(resolution, "FemaleDisplayId"));
                }
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
        var bindings = Array.Empty<CreatureDisplayBindingPlan>(); CreatureAppearanceBatchPortPlan? promotion = null; PlayableRaceCustomizationPromotionPlan? customization = null; uint? finalMaleDisplay = appearance.MaleDisplayId; uint? finalFemaleDisplay = appearance.FemaleDisplayId; string? appearanceSourceRoot = null;
        if (appearance.Enabled)
        {
            var library = string.IsNullOrWhiteSpace(appearance.ProcessedAssetLibraryRoot) ? null : RequiredDirectory(appearance.ProcessedAssetLibraryRoot, "Processed asset library");
            var sourceAppearance = string.IsNullOrWhiteSpace(appearance.SourceDbcRoot) ? null : RequiredDirectory(appearance.SourceDbcRoot, "Appearance source DBC folder");
            var male = appearance.MaleDisplayId ?? sourceMaleDisplay; var female = appearance.FemaleDisplayId ?? sourceFemaleDisplay;
            if (sourceAppearance is not null)
            {
                appearanceSourceRoot = sourceAppearance;
                promotion = CreatureAppearancePortService.CreateBatchPlan(sourceAppearance, dbcRoot, schemaPath, [new("male", male), new("female", female)], cancellationToken);
                var promotedByRole = promotion.Bindings.ToDictionary(binding => binding.Role, StringComparer.OrdinalIgnoreCase); finalMaleDisplay = promotedByRole["male"].TargetDisplayId; finalFemaleDisplay = promotedByRole["female"].TargetDisplayId;
                var sourceBindings = CreatureDisplayBindingService.CreatePlans(sourceAppearance, schemaPath, [new("male", male), new("female", female)], library, appearance.RequestedAssetProvenance, cancellationToken);
                bindings = sourceBindings.Select(binding =>
                {
                    var promoted = promotedByRole[binding.Role];
                    var bindingWarnings = binding.Warnings.Where(warning => !warning.StartsWith("No processed asset library was supplied", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (library is null) bindingWarnings.Add($"No processed asset library was supplied for {binding.Role}; the bundle will bind promoted target display {promoted.TargetDisplayId:N0} sourced from display {promoted.SourceDisplayId:N0}, but assumes its model and textures already exist in the target client.");
                    bindingWarnings.Add($"{binding.Role} source display {promoted.SourceDisplayId:N0} / model {promoted.SourceModelId:N0} is promoted collision-safely to target display {promoted.TargetDisplayId:N0} / model {promoted.TargetModelId:N0}.");
                    return binding with
                    {
                        DisplayId = promoted.TargetDisplayId,
                        ModelId = promoted.TargetModelId,
                        Warnings = bindingWarnings
                    };
                }).ToArray();
                plans.AddRange(promotion.Rows.Where(row => row.AddsRow).GroupBy(row => row.Table, StringComparer.OrdinalIgnoreCase).Select(group => new PlayableBundleDbcTablePlan(group.Key, "Promote reviewed source appearance rows with semantic reuse and collision-safe reference remapping", group.Count())));
                warnings.AddRange(promotion.Findings);
            }
            else
            {
                bindings = CreatureDisplayBindingService.CreatePlans(dbcRoot, schemaPath,
                    [new("male", male), new("female", female)], library, appearance.RequestedAssetProvenance, cancellationToken).ToArray();
            }
            blockers.AddRange(bindings.SelectMany(binding => binding.Blockers)); warnings.AddRange(bindings.SelectMany(binding => binding.Warnings));
            foreach (var conflict in bindings.SelectMany(binding => binding.Assets).GroupBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).Where(group => group.Select(asset => asset.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
                blockers.Add($"Male/female appearance closures resolve client path {conflict.Key} to different bytes. Select one shared provenance before building.");
            foreach (var table in new[] { "CreatureDisplayInfo", "CreatureModelData" }) { var path = RequiredTablePath(dbcRoot, table); hashes[table] = Hash(path); }
            if (appearance.MaleDisplayId.HasValue || appearance.FemaleDisplayId.HasValue)
                warnings.Add(appearance.AppearanceSourceRaceId is { } customizationRace
                    ? $"ChrRaces will bind reviewed target display IDs male {finalMaleDisplay ?? male:N0} and female {finalFemaleDisplay ?? female:N0}. Character customization will be promoted from source race {customizationRace:N0}; verify UVs, geosets, animations, armor attachment points, and GlueXML in-client."
                    : $"ChrRaces will bind reviewed target display IDs male {finalMaleDisplay ?? male:N0} and female {finalFemaleDisplay ?? female:N0}. Character customization rows are still cloned from {sourceName}; verify UVs, geosets, animations, armor attachment points, and GlueXML in-client.");
            else warnings.Add("The source male/female display IDs remain unchanged, but their selected processed-library asset closure is included in the bundle.");
            if (appearance.AppearanceSourceRaceId is { } customizationSourceRace)
            {
                var maleProvenance = bindings.FirstOrDefault(binding => binding.Role.Equals("male", StringComparison.OrdinalIgnoreCase))?.EffectiveProvenance; var femaleProvenance = bindings.FirstOrDefault(binding => binding.Role.Equals("female", StringComparison.OrdinalIgnoreCase))?.EffectiveProvenance;
                customization = PlayableRaceCustomizationPromotionService.CreatePlan(appearanceSourceRoot!, dbcRoot, schemaPath, customizationSourceRace, targetRaceId, library, appearance.RequestedAssetProvenance, maleProvenance, femaleProvenance, cancellationToken);
                blockers.AddRange(customization.Blockers); warnings.AddRange(customization.Warnings); plans.AddRange(customization.Tables.Select(table => new PlayableBundleDbcTablePlan(table.Table, $"Promote race {customizationSourceRace:N0} customization rows; {table.KeyKind} identity with semantic reuse/collision-safe allocation", table.AddedRows)));
                warnings.Add($"The target race uses customization rows promoted from source race {customizationSourceRace:N0}, not the gameplay/source-stat race {sourceRaceId:N0}. Missing source tables remain visibly empty; Crucible never substitutes unrelated appearance options.");
            }
        }
        else warnings.Add("This first bundle deliberately reuses the source race's male/female display IDs and client assets. Import or author a complete model/texture/animation/GlueXML chain before claiming a visually distinct race.");
        var sql = await PlayableBundleSqlService.InspectAsync(PlayableBundleIdentityKind.Race, sourceRaceId, targetRaceId, RaceMask(sourceRaceId), RaceMask(targetRaceId), profile, capabilities, blockers, cancellationToken); warnings.AddRange(sql.Warnings);
        if (customization is not null)
        {
            var customizationOverlays = PlayableRaceCustomizationPromotionService.TableNames.Select(table => table.ToLowerInvariant() + "_dbc").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var table in sql.Tables.Where(table => customizationOverlays.Contains(table.Table) && (table.SourceRows > 0 || table.Rows.Count > 0 || table.AlreadyCovered > 0 || table.Conflicts > 0)))
                blockers.Add($"Live SQL overlay {table.Table} contains race-customization data tied to gameplay/source race {sourceRaceId:N0} or target race {targetRaceId:N0}. It can override the promoted source-race-{customization.SourceRaceId:N0} DBC surface, so Crucible refuses to clone or mix it without a coordinated source SQL mapping.");
        }
        warnings.Add(promotion is null
            ? "CreatureDisplayInfoExtra is NPC appearance data. Crucible does not create unreferenced duplicate NPC rows; promote a complete dependent CreatureDisplayInfo chain separately when the new race needs custom NPC appearances."
            : "Source-layer promotion includes CreatureDisplayInfoExtra and ItemDisplayInfo only when the selected male/female display chain actually references them; unrelated NPC appearance rows are never duplicated.");
        warnings.Add("Core race enums/masks, character-create GlueXML, faction behavior, language, cinematics, achievements, spells, and runtime handlers still require separate reviewed work before the new race is fully playable.");
        foreach (var conflict in bindings.SelectMany(binding => binding.Assets).Concat(customization?.Assets ?? []).GroupBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).Where(group => group.Select(asset => asset.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            blockers.Add($"Display and customization closures resolve client path {conflict.Key} to different bytes. Select one coherent provenance before building.");
        warnings.Add("Building writes a new DBC/SQL/manifest/MPQ bundle only. It does not mutate the configured server, live database, or client.");
        var plan = new PlayableRaceClonePlan(PlanFormat, PlanFormatVersion, DateTimeOffset.UtcNow, projectRoot, project.Name, dbcRoot, schemaPath, sourceRaceId, targetRaceId, sourceName, targetRaceName, targetClientPrefix, targetFileToken, RaceMask(sourceRaceId), RaceMask(targetRaceId), Hash(schemaPath), hashes, sql.Target, plans, sql.Tables, blockers.Distinct(StringComparer.Ordinal).ToArray(), warnings.Distinct(StringComparer.Ordinal).ToArray(), string.Empty)
        {
            MaleDisplayIdOverride = finalMaleDisplay,
            FemaleDisplayIdOverride = finalFemaleDisplay,
            ProcessedAssetLibraryRoot = string.IsNullOrWhiteSpace(appearance.ProcessedAssetLibraryRoot) ? null : Path.GetFullPath(appearance.ProcessedAssetLibraryRoot),
            RequestedAssetProvenance = string.IsNullOrWhiteSpace(appearance.RequestedAssetProvenance) ? null : appearance.RequestedAssetProvenance.Trim(),
            DisplayBindings = bindings,
            AppearanceSourceDbcRoot = appearanceSourceRoot,
            MaleSourceDisplayId = appearanceSourceRoot is null ? null : appearance.MaleDisplayId,
            FemaleSourceDisplayId = appearanceSourceRoot is null ? null : appearance.FemaleDisplayId,
            AppearancePromotion = promotion,
            AppearanceSourceRaceId = appearance.AppearanceSourceRaceId,
            CustomizationPromotion = customization
        };
        return plan with { ContentSha256 = ContentHash(plan) };
    }

    public void SavePlan(string path, PlayableRaceClonePlan plan, bool overwrite = false) { ValidatePlan(plan); AtomicJson(path, plan, overwrite); }
    public PlayableRaceClonePlan LoadPlan(string path) { var plan = JsonSerializer.Deserialize<PlayableRaceClonePlan>(File.ReadAllText(Path.GetFullPath(path)), Json) ?? throw new InvalidDataException("Playable race plan is empty."); ValidatePlan(plan); return plan; }

    public async Task<PlayableRaceCloneResult> BuildAsync(PlayableRaceClonePlan plan, DatabaseConnectionProfile profile, string outputRoot, CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan); if (!plan.Ready) throw new InvalidOperationException($"Playable race plan has {plan.Blockers.Count:N0} blocker(s); nothing was built."); ValidateTarget(plan.DatabaseTarget, profile);
        CreatureDisplayBindingService.VerifyAssets(plan.DisplayBindings); PlayableRaceCustomizationPromotionService.VerifyAssets(plan.CustomizationPromotion);
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var fresh = await CreatePlanAsync(plan.ProjectRoot, plan.DbcRoot, plan.SchemaPath, plan.SourceRaceId, plan.TargetRaceId, plan.TargetRaceName, plan.TargetClientPrefix, plan.TargetFileToken, profile, capabilities,
            new(plan.AppearanceSourceDbcRoot is null ? plan.MaleDisplayIdOverride : plan.MaleSourceDisplayId, plan.AppearanceSourceDbcRoot is null ? plan.FemaleDisplayIdOverride : plan.FemaleSourceDisplayId, plan.ProcessedAssetLibraryRoot, plan.RequestedAssetProvenance, plan.AppearanceSourceDbcRoot, plan.AppearanceSourceRaceId), cancellationToken);
        if (!fresh.ContentSha256.Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("DBC, project reservation, database rows, or target schema changed after review; rebuild the race plan.");
        outputRoot = Path.GetFullPath(outputRoot); if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any()) throw new IOException($"Playable race output must be new or empty: {outputRoot}"); var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidOperationException("Output folder has no parent."); Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var dbcOutput = Path.Combine(staging, "DBC"); var sqlOutput = Path.Combine(staging, "SQL"); var manifestOutput = Path.Combine(staging, "Manifests"); Directory.CreateDirectory(dbcOutput); Directory.CreateDirectory(sqlOutput); Directory.CreateDirectory(manifestOutput);
            var schema = DbcSchemaCatalog.Load(plan.SchemaPath); var entries = new List<PatchEntry>(); var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in DbcTables)
            {
                if (plan.CustomizationPromotion is not null && PlayableRaceCustomizationPromotionService.TableNames.Contains(table)) continue;
                cancellationToken.ThrowIfCancellationRequested(); var sourcePath = RequiredTablePath(plan.DbcRoot, table); var file = WdbcFile.Load(sourcePath); var resolution = Exact(schema, table, file); var changed = false;
                if (table == "ChrRaces")
                {
                    var rows = DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy); var row = file.CloneRowWithId(rows[plan.SourceRaceId], PhysicalKey(resolution, table), plan.TargetRaceId);
                    foreach (var name in new[] { "Name_Lang[enUS]", "Name_Female_Lang[enUS]", "Name_Male_Lang[enUS]" }) file.SetDisplayValue(row, Column(resolution, name), plan.TargetRaceName);
                    file.SetDisplayValue(row, Column(resolution, "ClientPrefix"), plan.TargetClientPrefix); file.SetDisplayValue(row, Column(resolution, "ClientFilestring"), plan.TargetFileToken); changed = true;
                    if (plan.MaleDisplayIdOverride is { } male) file.SetRaw(row, Column(resolution, "MaleDisplayId"), male);
                    if (plan.FemaleDisplayIdOverride is { } female) file.SetRaw(row, Column(resolution, "FemaleDisplayId"), female);
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
            if (plan.AppearancePromotion is not null)
            {
                var promotionOutput = Path.Combine(staging, ".appearance-promotion"); var promoted = CreatureAppearancePortService.ApplyBatch(plan.AppearancePromotion, promotionOutput, cancellationToken);
                try
                {
                    foreach (var pair in promoted.OutputFiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var output = Path.Combine(dbcOutput, pair.Key + ".dbc"); if (File.Exists(output)) throw new InvalidDataException($"Appearance promotion unexpectedly collided with another race-bundle output: {pair.Key}.dbc"); File.Copy(pair.Value, output, false);
                        if (!Hash(output).Equals(promoted.OutputSha256[pair.Key], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Copied promoted {pair.Key}.dbc failed SHA-256 validation."); hashes[Path.GetRelativePath(staging, output)] = promoted.OutputSha256[pair.Key]; entries.Add(new(output, $@"DBFilesClient\{pair.Key}.dbc"));
                    }
                }
                finally { if (Directory.Exists(promotionOutput)) Directory.Delete(promotionOutput, true); }
            }
            if (plan.CustomizationPromotion is not null)
            {
                var customizationOutput = Path.Combine(staging, ".customization-promotion"); var promoted = PlayableRaceCustomizationPromotionService.Apply(plan.CustomizationPromotion, customizationOutput, cancellationToken);
                try
                {
                    foreach (var pair in promoted.OutputFiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var output = Path.Combine(dbcOutput, pair.Key + ".dbc"); if (File.Exists(output)) throw new InvalidDataException($"Customization promotion unexpectedly collided with another race-bundle output: {pair.Key}.dbc"); File.Copy(pair.Value, output, false);
                        if (!Hash(output).Equals(promoted.OutputSha256[pair.Key], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Copied promoted {pair.Key}.dbc failed SHA-256 validation."); hashes[Path.GetRelativePath(staging, output)] = promoted.OutputSha256[pair.Key]; entries.Add(new(output, $@"DBFilesClient\{pair.Key}.dbc"));
                    }
                }
                finally { if (Directory.Exists(customizationOutput)) Directory.Delete(customizationOutput, true); }
            }
            foreach (var asset in plan.DisplayBindings.SelectMany(binding => binding.Assets).Concat(plan.CustomizationPromotion?.Assets ?? []).GroupBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested(); var relative = asset.ClientPath.Replace('\\', Path.DirectorySeparatorChar); var output = Path.Combine(staging, "Assets", relative); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(asset.SourcePath, output, false);
                if (!Hash(output).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Copied appearance asset failed SHA-256 validation: {asset.ClientPath}"); hashes[Path.GetRelativePath(staging, output)] = asset.Sha256; entries.Add(new(output, asset.ClientPath));
            }
            var sqlPath = Path.Combine(sqlOutput, $"race-{plan.TargetRaceId}-clone.sql"); await File.WriteAllTextAsync(sqlPath, plan.PreviewSql(), new UTF8Encoding(false), cancellationToken); hashes[Path.GetRelativePath(staging, sqlPath)] = Hash(sqlPath);
            var planPath = Path.Combine(staging, "race-clone.plan.json"); SavePlan(planPath, plan); hashes[Path.GetRelativePath(staging, planPath)] = Hash(planPath);
            var manifestPath = Path.Combine(manifestOutput, "race-clone.patch.json"); var patchName = $"patch-Crucible-Race-{plan.TargetRaceId}.MPQ"; var archivePaths = entries.Select(entry => entry.ArchivePath).ToArray(); PatchManifestService.Save(manifestPath, $"Playable race {plan.TargetRaceName} ({plan.TargetRaceId})", patchName, entries, policy: new(archivePaths, null, entries.Count, archivePaths.Where(path => path.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase)).ToArray()));
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
    private static string ContentHash(PlayableRaceClonePlan plan)
    {
        object sqlTables = plan.SqlTables.Any(table => table.Rows.Any(row => row.IsGuardedUpdate)) ? plan.SqlTables : LegacySqlIdentity(plan.SqlTables);
        var legacy = new { plan.Format, plan.FormatVersion, plan.ProjectRoot, plan.ProjectName, plan.DbcRoot, plan.SchemaPath, plan.SourceRaceId, plan.TargetRaceId, plan.SourceRaceName, plan.TargetRaceName, plan.TargetClientPrefix, plan.TargetFileToken, plan.SourceRaceMask, plan.TargetRaceMask, plan.SchemaSha256, plan.DbcSha256, plan.DatabaseTarget, plan.DbcTables, SqlTables = sqlTables, plan.Blockers, plan.Warnings };
        if (plan.MaleDisplayIdOverride is null && plan.FemaleDisplayIdOverride is null && plan.ProcessedAssetLibraryRoot is null && plan.RequestedAssetProvenance is null && plan.DisplayBindings.Count == 0)
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacy, HashJson)));
        var identity = new { Legacy = legacy, plan.MaleDisplayIdOverride, plan.FemaleDisplayIdOverride, plan.ProcessedAssetLibraryRoot, plan.RequestedAssetProvenance, plan.DisplayBindings };
        if (plan.AppearanceSourceDbcRoot is null && plan.MaleSourceDisplayId is null && plan.FemaleSourceDisplayId is null && plan.AppearancePromotion is null)
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, HashJson)));
        object? promotionIdentity = plan.AppearancePromotion is null ? null : new
        {
            plan.AppearancePromotion.FormatVersion,
            plan.AppearancePromotion.SourceDbcRoot,
            plan.AppearancePromotion.TargetDbcRoot,
            plan.AppearancePromotion.SchemaPath,
            plan.AppearancePromotion.SchemaSha256,
            plan.AppearancePromotion.SourceFileSha256,
            plan.AppearancePromotion.TargetFileSha256,
            plan.AppearancePromotion.Bindings,
            plan.AppearancePromotion.Rows,
            plan.AppearancePromotion.RequiredAssets,
            plan.AppearancePromotion.Findings
        };
        var promotedIdentity = new { Previous = identity, plan.AppearanceSourceDbcRoot, plan.MaleSourceDisplayId, plan.FemaleSourceDisplayId, AppearancePromotion = promotionIdentity };
        if (plan.AppearanceSourceRaceId is null && plan.CustomizationPromotion is null) return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(promotedIdentity, HashJson)));
        object? customizationPlanIdentity = plan.CustomizationPromotion is null ? null : new
        {
            plan.CustomizationPromotion.FormatVersion,
            plan.CustomizationPromotion.SourceDbcRoot,
            plan.CustomizationPromotion.TargetDbcRoot,
            plan.CustomizationPromotion.SchemaPath,
            plan.CustomizationPromotion.SchemaSha256,
            plan.CustomizationPromotion.SourceRaceId,
            plan.CustomizationPromotion.TargetRaceId,
            plan.CustomizationPromotion.ProcessedAssetLibraryRoot,
            plan.CustomizationPromotion.RequestedAssetProvenance,
            plan.CustomizationPromotion.MaleAssetProvenance,
            plan.CustomizationPromotion.FemaleAssetProvenance,
            plan.CustomizationPromotion.SourceFileSha256,
            plan.CustomizationPromotion.TargetFileSha256,
            plan.CustomizationPromotion.Tables,
            plan.CustomizationPromotion.RequiredTexturePaths,
            plan.CustomizationPromotion.Assets,
            plan.CustomizationPromotion.Blockers,
            plan.CustomizationPromotion.Warnings
        };
        var customizationIdentity = new { Previous = promotedIdentity, plan.AppearanceSourceRaceId, CustomizationPromotion = customizationPlanIdentity };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(customizationIdentity, HashJson)));
    }
    private static object LegacySqlIdentity(IReadOnlyList<PlayableBundleSqlTablePlan> tables) => tables.Select(table => new { table.Table, table.Selector, table.SourceRows, table.AlreadyCovered, table.Conflicts, Rows = table.Rows.Select(row => new { row.SourceKey, row.TargetKey, row.Values }).ToArray() }).ToArray();
    private static void ValidatePlan(PlayableRaceClonePlan plan) { if (plan.Format != PlanFormat || plan.FormatVersion != PlanFormatVersion) throw new InvalidDataException("Unsupported playable race plan format."); if (!ContentHash(plan).Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Playable race plan content hash is invalid."); }
    private static void ValidateTarget(PlayableBundleDatabaseTarget target, DatabaseConnectionProfile profile) { if (!target.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) || target.Port != profile.Port || !target.User.Equals(profile.User, StringComparison.Ordinal) || !target.Database.Equals(profile.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected database connection does not match the race plan's reviewed target."); }
    private static void AtomicJson(string path, object value, bool overwrite) { path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Output already exists: {path}"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + $".{Guid.NewGuid():N}.tmp"; try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false)); File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
}
