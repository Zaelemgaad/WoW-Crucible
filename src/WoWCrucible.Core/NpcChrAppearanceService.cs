using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record WmvCharacterEquipment(
    uint Head, uint Unknown, uint Shoulder, uint Boots, uint Belt, uint Shirt, uint Legs, uint Chest,
    uint Bracers, uint Gloves, uint RightHand, uint LeftHand, uint Cape, uint Tabard, uint Quiver)
{
    public IReadOnlyList<(string Slot, uint ItemEntry)> ArmorSlots =>
    [
        ("Head", Head), ("Shoulder", Shoulder), ("Shirt", Shirt), ("Chest", Chest), ("Belt", Belt),
        ("Legs", Legs), ("Boots", Boots), ("Bracers", Bracers), ("Gloves", Gloves),
        ("Tabard", Tabard), ("Cape", Cape)
    ];
}

public sealed record WmvCharacterDescription(
    string SourcePath, string SourceSha256, string ModelPath, uint RaceId, uint SexId,
    uint SkinId, uint FaceId, uint HairColorId, uint HairStyleId, uint FacialHairId, uint FacialColorId,
    WmvCharacterEquipment Equipment, IReadOnlyList<string> TrailingLines);

public sealed record NpcChrItemDisplay(string Slot, uint ItemEntry, uint ItemDisplayId, bool Resolved);

public sealed record NpcChrAppearanceOptions(
    uint? DisplayIdStart = null,
    uint? ExtraIdStart = null,
    uint SoundId = 0,
    float Scale = 1f,
    uint Alpha = 255);

public sealed record NpcChrAppearancePlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    WmvCharacterDescription Character,
    string TexturePath,
    string TextureSha256,
    string BakedTextureName,
    string TargetDbcRoot,
    string SchemaPath,
    string SchemaSha256,
    IReadOnlyDictionary<string, string> TargetDbcSha256,
    NpcChrAppearanceOptions Options,
    uint ModelId,
    uint ExtraId,
    bool ReusesExtra,
    uint DisplayId,
    bool ReusesDisplay,
    IReadOnlyList<NpcChrItemDisplay> ItemDisplays,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Blockers)
{
    public bool Ready => Blockers.Count == 0;
    public int AddedRows => (ReusesExtra ? 0 : 1) + (ReusesDisplay ? 0 : 1);
    public IReadOnlyDictionary<string, uint> WeaponItemEntries => new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["RightHand"] = Character.Equipment.RightHand,
        ["LeftHand"] = Character.Equipment.LeftHand,
        ["Quiver"] = Character.Equipment.Quiver
    };
}

public sealed record NpcChrAppearanceResult(
    string OutputDirectory,
    string PlanPath,
    string ReceiptPath,
    string ManifestPath,
    string PatchPath,
    string BakedTexturePath,
    IReadOnlyDictionary<string, string> OutputDbcFiles,
    IReadOnlyDictionary<string, string> OutputSha256,
    NpcChrAppearancePlan Plan);

/// <summary>
/// Clean-room implementation of the useful WMV .chr -> WotLK NPC appearance workflow.
/// The old generator is format evidence only: this service never consumes its binaries,
/// CSVs, source code, hardcoded model IDs, random sound selection, or REPLACE statements.
/// </summary>
public static class NpcChrAppearanceService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] RequiredTables = ["CreatureModelData", "CreatureDisplayInfoExtra", "CreatureDisplayInfo"];

    public static WmvCharacterDescription Parse(string path)
    {
        path = RequiredFile(path, "WMV character file");
        var lines = File.ReadAllLines(path);
        if (lines.Length < 18) throw new InvalidDataException($"WMV character file requires at least 18 lines; {Path.GetFileName(path)} has {lines.Length:N0}.");
        var model = NormalizeClientPath(lines[0]);
        if (model.Length == 0) throw new InvalidDataException("WMV character line 1 has no model path.");
        var identity = ParseVector(lines[1], 2, "line 2 race/sex");
        if (identity[0] == 0) throw new InvalidDataException("WMV character race ID must be positive.");
        if (identity[1] is not 0 and not 1) throw new InvalidDataException($"WMV character sex ID must be 0 or 1; found {identity[1]}.");
        var visual = ParseVector(lines[2], 6, "line 3 appearance");
        var values = Enumerable.Range(3, 15).Select(index => ParseUnsigned(lines[index], $"line {index + 1}")).ToArray();
        var equipment = new WmvCharacterEquipment(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8], values[9], values[10], values[11], values[12], values[13], values[14]);
        return new(path, Hash(path), model, identity[0], identity[1], visual[0], visual[1], visual[2], visual[3], visual[4], visual[5], equipment, lines.Skip(18).ToArray());
    }

    public static async Task<IReadOnlyDictionary<uint, uint>> ResolveItemDisplaysAsync(DatabaseConnectionProfile profile, IEnumerable<uint> itemEntries, CancellationToken cancellationToken = default)
    {
        var requested = itemEntries.Where(entry => entry != 0).Distinct().Order().ToArray();
        if (requested.Length == 0) return new Dictionary<uint, uint>();
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var table = capabilities.FindTable("item_template") ?? throw new NotSupportedException($"{profile.Database} has no item_template table.");
        var entry = table.Find("entry")?.Name ?? throw new NotSupportedException("item_template has no entry column.");
        var display = table.Find("displayid")?.Name ?? throw new NotSupportedException("item_template has no displayid column.");
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        var parameters = requested.Select((_, index) => $"@i{index}").ToArray();
        await using var command = new MySqlCommand($"SELECT `{entry.Replace("`", "``", StringComparison.Ordinal)}`,`{display.Replace("`", "``", StringComparison.Ordinal)}` FROM `{table.Name.Replace("`", "``", StringComparison.Ordinal)}` WHERE `{entry.Replace("`", "``", StringComparison.Ordinal)}` IN ({string.Join(',', parameters)})", connection) { CommandTimeout = 60 };
        for (var index = 0; index < requested.Length; index++) command.Parameters.AddWithValue(parameters[index], requested[index]);
        var result = new Dictionary<uint, uint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[Convert.ToUInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] = Convert.ToUInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        return result;
    }

    public static NpcChrAppearancePlan CreatePlan(string chrPath, string texturePath, string targetDbcRoot, string schemaPath,
        IReadOnlyDictionary<uint, uint> itemDisplays, NpcChrAppearanceOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (!float.IsFinite(options.Scale) || options.Scale <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Creature scale must be finite and positive.");
        if (options.Alpha > 255) throw new ArgumentOutOfRangeException(nameof(options), "Creature alpha must fit the WotLK 0..255 field.");
        var character = Parse(chrPath);
        texturePath = RequiredFile(texturePath, "Baked character texture");
        targetDbcRoot = RequiredDirectory(targetDbcRoot, "Target DBC root");
        schemaPath = RequiredFile(schemaPath, "WotLK schema");
        var textureHash = Hash(texturePath);
        var bakeName = $"CrucibleNpc-{textureHash[..24]}.blp";
        var schema = DbcSchemaCatalog.Load(schemaPath);
        var contexts = RequiredTables.ToDictionary(table => table, table => Open(table, targetDbcRoot, schema), StringComparer.OrdinalIgnoreCase);
        var hashes = contexts.ToDictionary(pair => pair.Key, pair => pair.Value.Sha256, StringComparer.OrdinalIgnoreCase);
        var findings = new List<string>(); var blockers = new List<string>();
        cancellationToken.ThrowIfCancellationRequested();

        var modelContext = contexts["CreatureModelData"];
        var modelColumn = Column(modelContext, "ModelName");
        var requestedModel = NormalizeModelPath(character.ModelPath);
        var modelMatches = modelContext.Rows.Where(pair => NormalizeModelPath(Convert.ToString(modelContext.File.GetDisplayValue(pair.Value, modelColumn), CultureInfo.InvariantCulture) ?? string.Empty).Equals(requestedModel, StringComparison.OrdinalIgnoreCase)).OrderBy(pair => pair.Key).ToArray();
        var modelId = modelMatches.FirstOrDefault().Key;
        if (modelMatches.Length == 0) blockers.Add($"No CreatureModelData.ModelName matches .chr model '{character.ModelPath}' after M2/MDX normalization.");
        else
        {
            findings.Add($"Resolved .chr model '{character.ModelPath}' to existing CreatureModelData ID {modelId:N0}; no model row will be invented.");
            if (modelMatches.Length > 1) findings.Add($"CreatureModelData contains {modelMatches.Length:N0} equivalent model paths; deterministic lowest ID {modelId:N0} was selected.");
        }

        var resolvedItems = new List<NpcChrItemDisplay>();
        foreach (var (slot, itemEntry) in character.Equipment.ArmorSlots)
        {
            if (itemEntry == 0) { resolvedItems.Add(new(slot, 0, 0, true)); continue; }
            if (!itemDisplays.TryGetValue(itemEntry, out var itemDisplay)) { resolvedItems.Add(new(slot, itemEntry, 0, false)); blockers.Add($"{slot} item entry {itemEntry:N0} was not found in the selected item_template mapping."); }
            else { resolvedItems.Add(new(slot, itemEntry, itemDisplay, true)); if (itemDisplay == 0) findings.Add($"{slot} item entry {itemEntry:N0} resolves to displayid 0 and will intentionally be invisible."); }
        }

        if (character.Equipment.Unknown != 0) findings.Add($"The legacy unknown equipment slot contains item {character.Equipment.Unknown:N0}; build 12340 CreatureDisplayInfoExtra has no field for it, so it remains recorded in the plan only.");
        if (character.Equipment.RightHand != 0 || character.Equipment.LeftHand != 0 || character.Equipment.Quiver != 0)
            findings.Add($"Weapon/quiver entries are preserved for creature_equip_template authoring: right {character.Equipment.RightHand:N0}, left {character.Equipment.LeftHand:N0}, quiver {character.Equipment.Quiver:N0}. They are not armor-display fields in CreatureDisplayInfoExtra.");
        if (character.FacialColorId != 0) findings.Add($"Facial color {character.FacialColorId:N0} is preserved in the plan; build 12340 CreatureDisplayInfoExtra has no separate facial-color field.");
        if (character.TrailingLines.Count > 0) findings.Add($"Preserved {character.TrailingLines.Count:N0} trailing .chr line(s) for review; the supported WMV contract consumes exactly the first 18 lines.");

        var extra = contexts["CreatureDisplayInfoExtra"];
        var extraValues = ExtraValues(character, bakeName, resolvedItems);
        var equivalentExtra = FindEquivalent(extra, extraValues);
        var extraId = equivalentExtra ?? Allocate(extra.Rows.Keys, options.ExtraIdStart);
        var reusesExtra = equivalentExtra is not null;
        findings.Add(reusesExtra ? $"Reusing semantically identical CreatureDisplayInfoExtra ID {extraId:N0}." : $"Planning new CreatureDisplayInfoExtra ID {extraId:N0}; no identical target row exists.");

        var display = contexts["CreatureDisplayInfo"];
        var displayValues = DisplayValues(modelId, extraId, options);
        var equivalentDisplay = modelId == 0 ? null : FindEquivalent(display, displayValues);
        var displayId = equivalentDisplay ?? Allocate(display.Rows.Keys, options.DisplayIdStart);
        var reusesDisplay = equivalentDisplay is not null;
        findings.Add(reusesDisplay ? $"Reusing semantically identical CreatureDisplayInfo ID {displayId:N0}." : $"Planning new CreatureDisplayInfo ID {displayId:N0}; no identical target row exists.");
        findings.Add($"Baked texture will be staged as Textures\\BakedNpcTextures\\{bakeName}; the content-derived name prevents accidental different-byte replacement.");

        return new(FormatVersion, DateTimeOffset.UtcNow, character, texturePath, textureHash, bakeName, targetDbcRoot, schemaPath, Hash(schemaPath), hashes,
            options, modelId, extraId, reusesExtra, displayId, reusesDisplay, resolvedItems, findings, blockers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static void SavePlan(string path, NpcChrAppearancePlan plan, bool overwrite = false) => AtomicJson(path, plan, overwrite);

    public static NpcChrAppearancePlan LoadPlan(string path)
    {
        path = RequiredFile(path, "NPC .chr appearance plan");
        var plan = JsonSerializer.Deserialize<NpcChrAppearancePlan>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("NPC .chr appearance plan is empty.");
        if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported NPC .chr appearance plan format {plan.FormatVersion:N0}.");
        return plan;
    }

    public static NpcChrAppearanceResult Apply(NpcChrAppearancePlan plan, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Ready) throw new InvalidOperationException("NPC appearance plan is blocked:\n" + string.Join("\n", plan.Blockers));
        VerifyInputs(plan);
        var mapping = plan.ItemDisplays.Where(item => item.ItemEntry != 0 && item.Resolved).ToDictionary(item => item.ItemEntry, item => item.ItemDisplayId);
        var recreated = CreatePlan(plan.Character.SourcePath, plan.TexturePath, plan.TargetDbcRoot, plan.SchemaPath, mapping, plan.Options, cancellationToken);
        if (!EquivalentPlans(plan, recreated)) throw new InvalidDataException("NPC appearance plan no longer matches a fresh deterministic plan over its bound inputs.");
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) throw new IOException($"NPC appearance output must be new or empty: {outputDirectory}");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("Output directory has no parent."); Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var schema = DbcSchemaCatalog.Load(plan.SchemaPath); var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var outputHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!plan.ReusesExtra)
            {
                var context = Open("CreatureDisplayInfoExtra", plan.TargetDbcRoot, schema); var row = context.File.AddBlankRow(); WriteRow(context, row, ExtraValues(plan.Character, plan.BakedTextureName, plan.ItemDisplays)); context.File.SetRaw(row, context.Key, plan.ExtraId);
                var path = Path.Combine(staging, "DBC", "CreatureDisplayInfoExtra.dbc"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); context.File.Save(path); outputs[context.Table] = path; outputHashes[context.Table] = Hash(path);
            }
            if (!plan.ReusesDisplay)
            {
                var context = Open("CreatureDisplayInfo", plan.TargetDbcRoot, schema); var row = context.File.AddBlankRow(); WriteRow(context, row, DisplayValues(plan.ModelId, plan.ExtraId, plan.Options)); context.File.SetRaw(row, context.Key, plan.DisplayId);
                var path = Path.Combine(staging, "DBC", "CreatureDisplayInfo.dbc"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); context.File.Save(path); outputs[context.Table] = path; outputHashes[context.Table] = Hash(path);
            }

            var texture = Path.Combine(staging, "Staging", "Textures", "BakedNpcTextures", plan.BakedTextureName); Directory.CreateDirectory(Path.GetDirectoryName(texture)!);
            if (Path.GetExtension(plan.TexturePath).Equals(".blp", StringComparison.OrdinalIgnoreCase)) { _ = BlpTextureService.Inspect(plan.TexturePath); File.Copy(plan.TexturePath, texture); }
            else BlpTextureService.EncodeFromImage(plan.TexturePath, texture, new(BlpOutputFormat.Auto, true, BlpOutputQuality.Best));
            _ = BlpTextureService.Inspect(texture);
            var entries = new List<PatchEntry> { new(texture, $"Textures\\BakedNpcTextures\\{plan.BakedTextureName}") };
            foreach (var pair in outputs)
            {
                var staged = Path.Combine(staging, "Staging", "DBFilesClient", Path.GetFileName(pair.Value)); Directory.CreateDirectory(Path.GetDirectoryName(staged)!); File.Copy(pair.Value, staged);
                entries.Add(new(staged, $"DBFilesClient\\{Path.GetFileName(staged)}"));
            }
            var manifest = Path.Combine(staging, "Manifests", "npc-appearance.patch.json");
            PatchManifestService.Save(manifest, "WoW Crucible WMV NPC appearance", "patch-Crucible-NpcAppearance.MPQ", entries, policy: new(ExpectedEntryCount: entries.Count, RequiredGlobs: [$"Textures\\BakedNpcTextures\\{plan.BakedTextureName}"]));
            var patchDirectory = Path.Combine(staging, "Patch"); Directory.CreateDirectory(patchDirectory); PatchManifestService.Build(manifest, patchDirectory);
            var planPath = Path.Combine(staging, "Reports", "npc-appearance.plan.json"); AtomicJson(planPath, plan, false);
            var receiptPath = Path.Combine(staging, "Reports", "npc-appearance.receipt.json");
            var receipt = new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, PlanSha256 = Hash(planPath), plan.ModelId, plan.ExtraId, plan.DisplayId, plan.ReusesExtra, plan.ReusesDisplay, BakedTextureSha256 = Hash(texture), OutputDbcSha256 = outputHashes, PatchEntries = entries.Count };
            AtomicJson(receiptPath, receipt, false);
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, false); Directory.Move(staging, outputDirectory);
            var finalOutputs = outputs.ToDictionary(pair => pair.Key, pair => Path.Combine(outputDirectory, Path.GetRelativePath(staging, pair.Value)), StringComparer.OrdinalIgnoreCase);
            return new(outputDirectory, Path.Combine(outputDirectory, "Reports", "npc-appearance.plan.json"), Path.Combine(outputDirectory, "Reports", "npc-appearance.receipt.json"), Path.Combine(outputDirectory, "Manifests", "npc-appearance.patch.json"), Path.Combine(outputDirectory, "Patch", "patch-Crucible-NpcAppearance.MPQ"), Path.Combine(outputDirectory, "Staging", "Textures", "BakedNpcTextures", plan.BakedTextureName), finalOutputs, outputHashes, plan);
        }
        catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
    }

    private static IReadOnlyDictionary<string, object?> ExtraValues(WmvCharacterDescription character, string bakeName, IReadOnlyList<NpcChrItemDisplay> items)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayRaceID"] = character.RaceId, ["DisplaySexID"] = character.SexId, ["SkinID"] = character.SkinId,
            ["FaceID"] = character.FaceId, ["HairStyleID"] = character.HairStyleId, ["HairColorID"] = character.HairColorId,
            ["FacialHairID"] = character.FacialHairId, ["Flags"] = 0u, ["BakeName"] = bakeName
        };
        for (var index = 0; index < 11; index++) values[$"NPCItemDisplay[{index}]"] = index < items.Count ? items[index].ItemDisplayId : 0u;
        return values;
    }

    private static IReadOnlyDictionary<string, object?> DisplayValues(uint modelId, uint extraId, NpcChrAppearanceOptions options) => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
    {
        ["ModelID"] = modelId, ["SoundID"] = options.SoundId, ["ExtendedDisplayInfoID"] = extraId,
        ["CreatureModelScale"] = options.Scale, ["CreatureModelAlpha"] = options.Alpha,
        ["TextureVariation[0]"] = string.Empty, ["TextureVariation[1]"] = string.Empty, ["TextureVariation[2]"] = string.Empty,
        ["PortraitTextureName"] = string.Empty, ["BloodLevel"] = 0u, ["BloodID"] = 0u, ["NPCSoundID"] = 0u,
        ["ParticleColorID"] = 0u, ["CreatureGeosetData"] = 0u, ["ObjectEffectPackageID"] = 0u
    };

    private static uint? FindEquivalent(TableContext context, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var pair in context.Rows.OrderBy(pair => pair.Key))
        {
            var matches = true;
            foreach (var column in context.Columns.Where(column => column.Index != context.Key.Index))
            {
                var expected = values.GetValueOrDefault(column.Name, column.Type == DbcValueType.StringOffset ? string.Empty : 0u);
                if (!ValueEquals(context.File.GetDisplayValue(pair.Value, column), expected, column.Type)) { matches = false; break; }
            }
            if (matches) return pair.Key;
        }
        return null;
    }

    private static bool ValueEquals(object actual, object? expected, DbcValueType type)
    {
        if (type == DbcValueType.StringOffset) return string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture), Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (type == DbcValueType.Float32) return BitConverter.SingleToUInt32Bits(Convert.ToSingle(actual, CultureInfo.InvariantCulture)) == BitConverter.SingleToUInt32Bits(Convert.ToSingle(expected, CultureInfo.InvariantCulture));
        return Convert.ToUInt32(actual, CultureInfo.InvariantCulture) == Convert.ToUInt32(expected, CultureInfo.InvariantCulture);
    }

    private static void WriteRow(TableContext context, int row, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var column in context.Columns.Where(column => column.Index != context.Key.Index))
        {
            var value = values.GetValueOrDefault(column.Name, column.Type == DbcValueType.StringOffset ? string.Empty : 0u);
            context.File.SetDisplayValue(row, column, value);
        }
    }

    private static TableContext Open(string table, string root, DbcSchemaCatalog schema)
    {
        var path = RequiredFile(Path.Combine(root, table + ".dbc"), $"Target {table}.dbc"); var file = WdbcFile.Load(path); var resolution = schema.ResolveColumns(table, file.FieldCount);
        if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch) throw new InvalidDataException($"{table}.dbc requires an exact named schema; selected definition resolved {resolution.MatchKind}.");
        var key = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy) ?? throw new InvalidDataException($"{table}.dbc has no physical key.");
        return new(table, path, Hash(path), file, resolution.Columns, key, DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy));
    }

    private static DbcColumn Column(TableContext context, string name) => context.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{context.Table} schema has no {name} column.");
    private static uint Allocate(IEnumerable<uint> occupiedValues, uint? start)
    {
        var occupied = occupiedValues.ToHashSet(); var next = start ?? (occupied.Count == 0 ? 1u : checked(occupied.Max() + 1)); if (next == 0) next = 1;
        while (occupied.Contains(next)) next = checked(next + 1); return next;
    }

    private static void VerifyInputs(NpcChrAppearancePlan plan)
    {
        if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported NPC appearance plan format {plan.FormatVersion:N0}.");
        if (!Hash(RequiredFile(plan.Character.SourcePath, "Plan .chr source")).Equals(plan.Character.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The .chr source changed after planning.");
        if (!Hash(RequiredFile(plan.TexturePath, "Plan texture source")).Equals(plan.TextureSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The baked texture source changed after planning.");
        if (!Hash(RequiredFile(plan.SchemaPath, "Plan schema")).Equals(plan.SchemaSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The DBC schema changed after planning.");
        foreach (var pair in plan.TargetDbcSha256) if (!Hash(RequiredFile(Path.Combine(plan.TargetDbcRoot, pair.Key + ".dbc"), $"Plan target {pair.Key}.dbc")).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Target {pair.Key}.dbc changed after planning.");
    }

    private static bool EquivalentPlans(NpcChrAppearancePlan left, NpcChrAppearancePlan right) =>
        left.ModelId == right.ModelId && left.ExtraId == right.ExtraId && left.ReusesExtra == right.ReusesExtra && left.DisplayId == right.DisplayId && left.ReusesDisplay == right.ReusesDisplay &&
        left.BakedTextureName.Equals(right.BakedTextureName, StringComparison.Ordinal) && left.ItemDisplays.SequenceEqual(right.ItemDisplays) && left.Blockers.SequenceEqual(right.Blockers, StringComparer.OrdinalIgnoreCase);

    private static uint[] ParseVector(string line, int count, string context)
    {
        var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length != count) throw new InvalidDataException($"WMV character {context} requires exactly {count:N0} unsigned integers; found {values.Length:N0}.");
        return values.Select((value, index) => ParseUnsigned(value, $"{context} value {index + 1}")).ToArray();
    }

    private static uint ParseUnsigned(string text, string context) => uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException($"WMV character {context} is not an unsigned integer: '{text}'.");
    private static string NormalizeClientPath(string value) => value.Trim().Trim('"').Replace('/', '\\').TrimStart('\\');
    private static string NormalizeModelPath(string value)
    {
        var path = NormalizeClientPath(value); var extension = Path.GetExtension(path);
        if (extension.Equals(".m2", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase)) path = path[..^extension.Length] + ".mdx";
        return path;
    }
    private static string RequiredFile(string path, string label) { path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException($"{label} was not found.", path); return path; }
    private static string RequiredDirectory(string path, string label) { path = Path.GetFullPath(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"{label} was not found: {path}"); return path; }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AtomicJson(string path, object value, bool overwrite) { path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Output already exists: {path}"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + $".{Guid.NewGuid():N}.tmp"; try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }

    private sealed record TableContext(string Table, string Path, string Sha256, WdbcFile File, IReadOnlyList<DbcColumn> Columns, DbcColumn Key, IReadOnlyDictionary<uint, int> Rows);
}
