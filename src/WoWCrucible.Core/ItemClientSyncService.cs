using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record ClientItemRecord(uint Id, int ClassId, int SubclassId, int SoundOverrideSubclassId, int Material,
    uint DisplayInfoId, int InventoryType, int SheatheType);

public sealed record ServerClientItemRecord(ClientItemRecord Client, int Quality, string Name);

public sealed record ItemClientMutation(uint Id, ClientItemRecord? Existing, ClientItemRecord Desired, IReadOnlyList<string> ChangedFields)
{
    public bool AddsRow => Existing is null;
}

public sealed record ItemClientSyncPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string TargetItemDbcPath,
    string TargetItemDbcSha256,
    string ItemDisplayInfoDbcPath,
    string ItemDisplayInfoDbcSha256,
    string SchemaPath,
    string SchemaSha256,
    string DatabaseIdentity,
    string ItemTemplateSchemaSha256,
    string ServerSnapshotSha256,
    int TargetRowCount,
    int ServerRowCount,
    IReadOnlyList<ItemClientMutation> Mutations,
    IReadOnlyList<ClientItemRecord> ClientOnlyRows,
    IReadOnlyList<ServerClientItemRecord> WmvCatalog,
    IReadOnlyList<uint> MissingDisplayIds,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Blockers)
{
    public bool Ready => Blockers.Count == 0;
    public int AddedRows => Mutations.Count(row => row.AddsRow);
    public int UpdatedRows => Mutations.Count(row => !row.AddsRow);
}

public sealed record ItemClientSyncResult(string OutputDirectory, string ItemDbcPath, string WmvCatalogPath,
    string ManifestPath, string PatchPath, string PlanPath, string ReceiptPath, string ItemDbcSha256, ItemClientSyncPlan Plan);

/// <summary>
/// Safely replaces the useful intent of Amaroth ClientItem without regenerating Item.dbc from SQL and deleting
/// client-only/NPC equipment records. Existing rows are preserved; SQL-backed rows are additively synchronized.
/// </summary>
public static class ItemClientSyncService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<ClientItemRecord> LoadClientItems(string itemDbcPath, string schemaPath)
    {
        itemDbcPath = RequiredFile(itemDbcPath, "Item.dbc"); schemaPath = RequiredFile(schemaPath, "DBC schema");
        var file = WdbcFile.Load(itemDbcPath); var schema = DbcSchemaCatalog.Load(schemaPath).ResolveColumns("Item", file.FieldCount);
        return ReadClientItems(file, ItemColumns(schema));
    }

    public static IReadOnlyList<ClientItemRecord> LoadClientItems(string itemDbcPath)
    {
        itemDbcPath = RequiredFile(itemDbcPath, "Item.dbc"); var file = WdbcFile.Load(itemDbcPath);
        if (file.FieldCount != 8 || file.RecordSize != 32)
            throw new InvalidDataException($"Item.dbc has {file.FieldCount:N0} fields and {file.RecordSize:N0}-byte records; WotLK build 12340 requires 8 fields and 32-byte records.");
        return ReadClientItems(file, new(
            new(0, 0, 4, "ID", DbcValueType.UInt32, true),
            new(1, 4, 4, "ClassID", DbcValueType.Int32),
            new(2, 8, 4, "SubclassID", DbcValueType.Int32),
            new(3, 12, 4, "Sound_Override_Subclassid", DbcValueType.Int32),
            new(4, 16, 4, "Material", DbcValueType.Int32),
            new(5, 20, 4, "DisplayInfoID", DbcValueType.UInt32),
            new(6, 24, 4, "InventoryType", DbcValueType.Int32),
            new(7, 28, 4, "SheatheType", DbcValueType.Int32)));
    }

    private static IReadOnlyList<ClientItemRecord> ReadClientItems(WdbcFile file, Columns columns)
    {
        var result = new List<ClientItemRecord>(file.RowCount); var seen = new HashSet<uint>();
        for (var row = 0; row < file.RowCount; row++)
        {
            var value = new ClientItemRecord(file.GetRaw(row, columns.Id), Signed(file.GetRaw(row, columns.ClassId)), Signed(file.GetRaw(row, columns.SubclassId)), Signed(file.GetRaw(row, columns.Sound)),
                Signed(file.GetRaw(row, columns.Material)), file.GetRaw(row, columns.Display), Signed(file.GetRaw(row, columns.Inventory)), Signed(file.GetRaw(row, columns.Sheath)));
            if (!seen.Add(value.Id)) throw new InvalidDataException($"Item.dbc contains duplicate ID {value.Id:N0}; synchronization is ambiguous.");
            result.Add(value);
        }
        return result;
    }

    public static async Task<(IReadOnlyList<ServerClientItemRecord> Rows, DatabaseTableCapability Table)> ReadServerItemsAsync(
        DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var table = capabilities.FindTable("item_template") ?? throw new NotSupportedException($"{profile.Database} has no item_template table.");
        var entry = RequiredColumn(table, "entry"); var classId = RequiredColumn(table, "class"); var subclass = RequiredColumn(table, "subclass");
        var sound = RequiredColumn(table, "SoundOverrideSubclass", "SoundOverrideSubclassID", "sound_override_subclass");
        var material = RequiredColumn(table, "Material", "material"); var display = RequiredColumn(table, "displayid", "displayID");
        var inventory = RequiredColumn(table, "InventoryType", "inventorytype"); var sheath = RequiredColumn(table, "sheath", "SheatheType");
        var quality = RequiredColumn(table, "Quality", "quality"); var name = RequiredColumn(table, "name", "Name");
        var selected = new[] { entry, classId, subclass, sound, material, display, inventory, sheath, quality, name };
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand($"SELECT {string.Join(',', selected.Select(column => Quote(column.Name)))} FROM {Quote(table.Name)} ORDER BY {Quote(entry.Name)}", connection) { CommandTimeout = 180 };
        var rows = new List<ServerClientItemRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var client = new ClientItemRecord(U32(reader.GetValue(0)), I32(reader.GetValue(1)), I32(reader.GetValue(2)), I32(reader.GetValue(3)), I32(reader.GetValue(4)), U32(reader.GetValue(5)), I32(reader.GetValue(6)), I32(reader.GetValue(7)));
            rows.Add(new(client, I32(reader.GetValue(8)), Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture) ?? string.Empty));
        }
        return (rows, table);
    }

    public static async Task<ItemClientSyncPlan> CreatePlanAsync(string itemDbcPath, string itemDisplayInfoDbcPath, string schemaPath,
        DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var server = await ReadServerItemsAsync(profile, cancellationToken);
        return CreatePlan(itemDbcPath, itemDisplayInfoDbcPath, schemaPath, server.Rows,
            $"{profile.User}@{profile.Host}:{profile.Port}/{profile.Database}", CacheServerPlanService.SchemaFingerprint(server.Table), cancellationToken);
    }

    public static ItemClientSyncPlan CreatePlan(string itemDbcPath, string itemDisplayInfoDbcPath, string schemaPath,
        IReadOnlyList<ServerClientItemRecord> serverRows, string databaseIdentity = "offline snapshot", string itemTemplateSchemaSha256 = "offline",
        CancellationToken cancellationToken = default)
    {
        itemDbcPath = RequiredFile(itemDbcPath, "Item.dbc"); itemDisplayInfoDbcPath = RequiredFile(itemDisplayInfoDbcPath, "ItemDisplayInfo.dbc"); schemaPath = RequiredFile(schemaPath, "DBC schema");
        var client = LoadClientItems(itemDbcPath, schemaPath); var clientById = client.ToDictionary(row => row.Id);
        var duplicateServer = serverRows.GroupBy(row => row.Client.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicateServer is not null) throw new InvalidDataException($"Server item snapshot contains duplicate entry {duplicateServer.Key:N0}.");
        var serverOrdered = serverRows.OrderBy(row => row.Client.Id).ToArray(); var serverById = serverOrdered.ToDictionary(row => row.Client.Id);
        var mutations = new List<ItemClientMutation>();
        foreach (var row in serverOrdered)
        {
            cancellationToken.ThrowIfCancellationRequested(); clientById.TryGetValue(row.Client.Id, out var existing); var changed = Changed(existing, row.Client);
            if (changed.Count > 0) mutations.Add(new(row.Client.Id, existing, row.Client, changed));
        }
        var clientOnly = client.Where(row => !serverById.ContainsKey(row.Id)).OrderBy(row => row.Id).ToArray();
        var displayFile = WdbcFile.Load(itemDisplayInfoDbcPath); var displaySchema = DbcSchemaCatalog.Load(schemaPath).ResolveColumns("ItemDisplayInfo", displayFile.FieldCount); var displayKey = DbcRecordIdentity.PhysicalColumn(displaySchema.Columns, displaySchema.KeyStrategy) ?? Column(displaySchema, "ID");
        var displays = Enumerable.Range(0, displayFile.RowCount).Select(row => displayFile.GetRaw(row, displayKey)).ToHashSet();
        var missingDisplays = serverOrdered.Select(row => row.Client.DisplayInfoId).Where(id => id != 0 && !displays.Contains(id)).Distinct().Order().ToArray();
        var findings = new List<string>
        {
            $"Target Item.dbc contains {client.Count:N0} unique row(s); all are preserved unless the same ID has an explicit SQL-backed field update.",
            $"World item_template snapshot contains {serverOrdered.Length:N0} row(s): {mutations.Count(row => row.AddsRow):N0} add(s), {mutations.Count(row => !row.AddsRow):N0} field update(s), and {serverOrdered.Length - mutations.Count:N0} already-equal row(s).",
            $"Preserving {clientOnly.Length:N0} client-only/NPC equipment row(s) missing from item_template. No incomplete fake server rows will be generated.",
            $"WMV compatibility catalog contains {serverOrdered.Length:N0} SQL-backed names; it is a separate ASCII CSV and never becomes authoritative item data."
        };
        var blockers = missingDisplays.Length == 0 ? Array.Empty<string>() : [$"{missingDisplays.Length:N0} SQL-backed display ID(s) do not exist in the selected ItemDisplayInfo.dbc: {string.Join(", ", missingDisplays.Take(32))}{(missingDisplays.Length > 32 ? " …" : string.Empty)}"];
        return new(FormatVersion, DateTimeOffset.UtcNow, itemDbcPath, Hash(itemDbcPath), itemDisplayInfoDbcPath, Hash(itemDisplayInfoDbcPath), schemaPath, Hash(schemaPath),
            databaseIdentity, itemTemplateSchemaSha256, SnapshotHash(serverOrdered), client.Count, serverOrdered.Length, mutations, clientOnly, serverOrdered, missingDisplays, findings, blockers);
    }

    public static void SavePlan(string path, ItemClientSyncPlan plan, bool overwrite = false) => AtomicJson(path, plan, overwrite);

    public static ItemClientSyncPlan LoadPlan(string path)
    {
        path = RequiredFile(path, "Item client sync plan"); var plan = JsonSerializer.Deserialize<ItemClientSyncPlan>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Item client sync plan is empty.");
        if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported Item client sync plan format {plan.FormatVersion:N0}."); return plan;
    }

    public static ItemClientSyncResult Apply(ItemClientSyncPlan plan, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); if (!plan.Ready) throw new InvalidOperationException("Item client sync plan is blocked:\n" + string.Join("\n", plan.Blockers));
        VerifyHash(plan.TargetItemDbcPath, plan.TargetItemDbcSha256, "Item.dbc"); VerifyHash(plan.ItemDisplayInfoDbcPath, plan.ItemDisplayInfoDbcSha256, "ItemDisplayInfo.dbc"); VerifyHash(plan.SchemaPath, plan.SchemaSha256, "schema");
        if (!SnapshotHash(plan.WmvCatalog).Equals(plan.ServerSnapshotSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Embedded server item snapshot changed or is corrupt.");
        var recreated = CreatePlan(plan.TargetItemDbcPath, plan.ItemDisplayInfoDbcPath, plan.SchemaPath, plan.WmvCatalog, plan.DatabaseIdentity, plan.ItemTemplateSchemaSha256, cancellationToken);
        if (!Equivalent(plan, recreated)) throw new InvalidDataException("Item client synchronization plan does not match a fresh deterministic comparison over its bound target and embedded SQL snapshot.");
        outputDirectory = Path.GetFullPath(outputDirectory); if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) throw new IOException($"Item client sync output must be new or empty: {outputDirectory}");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("Output directory has no parent."); Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var file = WdbcFile.Load(plan.TargetItemDbcPath); var schema = DbcSchemaCatalog.Load(plan.SchemaPath).ResolveColumns("Item", file.FieldCount); var columns = ItemColumns(schema);
            var rows = Enumerable.Range(0, file.RowCount).ToDictionary(row => file.GetRaw(row, columns.Id));
            foreach (var mutation in plan.Mutations)
            {
                cancellationToken.ThrowIfCancellationRequested(); if (!rows.TryGetValue(mutation.Id, out var row)) { row = file.AddBlankRow(); rows[mutation.Id] = row; }
                Write(file, row, columns, mutation.Desired);
            }
            var dbcPath = Path.Combine(staging, "DBC", "Item.dbc"); Directory.CreateDirectory(Path.GetDirectoryName(dbcPath)!); file.Save(dbcPath);
            var verified = LoadClientItems(dbcPath, plan.SchemaPath).ToDictionary(row => row.Id);
            if (verified.Count != plan.TargetRowCount + plan.AddedRows) throw new InvalidDataException($"Written Item.dbc has {verified.Count:N0} rows; expected {plan.TargetRowCount + plan.AddedRows:N0}.");
            foreach (var server in plan.WmvCatalog) if (!verified.TryGetValue(server.Client.Id, out var value) || value != server.Client) throw new InvalidDataException($"Written Item.dbc did not reproduce SQL-backed item {server.Client.Id:N0}.");
            foreach (var preserved in plan.ClientOnlyRows) if (!verified.TryGetValue(preserved.Id, out var value) || value != preserved) throw new InvalidDataException($"Client-only Item.dbc row {preserved.Id:N0} was not preserved byte-semantically.");
            var wmvPath = Path.Combine(staging, "WMV", "items.csv"); Directory.CreateDirectory(Path.GetDirectoryName(wmvPath)!); WriteWmvCatalog(wmvPath, plan.WmvCatalog);
            var stagedDbc = Path.Combine(staging, "Staging", "DBFilesClient", "Item.dbc"); Directory.CreateDirectory(Path.GetDirectoryName(stagedDbc)!); File.Copy(dbcPath, stagedDbc);
            var manifest = Path.Combine(staging, "Manifests", "item-client-sync.patch.json"); PatchManifestService.Save(manifest, "WoW Crucible Item client synchronization", "patch-Crucible-Items.MPQ", [new(stagedDbc, @"DBFilesClient\Item.dbc")], policy: new(ExpectedEntryCount: 1, RequiredGlobs: [@"DBFilesClient\Item.dbc"]));
            var patchDirectory = Path.Combine(staging, "Patch"); Directory.CreateDirectory(patchDirectory); PatchManifestService.Build(manifest, patchDirectory);
            var planPath = Path.Combine(staging, "Reports", "item-client-sync.plan.json"); AtomicJson(planPath, plan, false); var itemHash = Hash(dbcPath);
            var receiptPath = Path.Combine(staging, "Reports", "item-client-sync.receipt.json"); AtomicJson(receiptPath, new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, PlanSha256 = Hash(planPath), ItemDbcSha256 = itemHash, plan.AddedRows, plan.UpdatedRows, PreservedClientOnlyRows = plan.ClientOnlyRows.Count, WmvRows = plan.WmvCatalog.Count }, false);
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, false); Directory.Move(staging, outputDirectory);
            return new(outputDirectory, Path.Combine(outputDirectory, "DBC", "Item.dbc"), Path.Combine(outputDirectory, "WMV", "items.csv"), Path.Combine(outputDirectory, "Manifests", "item-client-sync.patch.json"), Path.Combine(outputDirectory, "Patch", "patch-Crucible-Items.MPQ"), Path.Combine(outputDirectory, "Reports", "item-client-sync.plan.json"), Path.Combine(outputDirectory, "Reports", "item-client-sync.receipt.json"), itemHash, plan);
        }
        catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
    }

    private sealed record Columns(DbcColumn Id, DbcColumn ClassId, DbcColumn SubclassId, DbcColumn Sound, DbcColumn Material, DbcColumn Display, DbcColumn Inventory, DbcColumn Sheath);
    private static Columns ItemColumns(DbcSchemaResolution schema) => new(Column(schema, "ID"), Column(schema, "ClassID"), Column(schema, "SubclassID"), Column(schema, "Sound_Override_Subclassid"), Column(schema, "Material"), Column(schema, "DisplayInfoID"), Column(schema, "InventoryType"), Column(schema, "SheatheType"));
    private static void Write(WdbcFile file, int row, Columns c, ClientItemRecord value)
    {
        file.SetRaw(row, c.Id, value.Id); file.SetRaw(row, c.ClassId, Raw(value.ClassId)); file.SetRaw(row, c.SubclassId, Raw(value.SubclassId)); file.SetRaw(row, c.Sound, Raw(value.SoundOverrideSubclassId)); file.SetRaw(row, c.Material, Raw(value.Material)); file.SetRaw(row, c.Display, value.DisplayInfoId); file.SetRaw(row, c.Inventory, Raw(value.InventoryType)); file.SetRaw(row, c.Sheath, Raw(value.SheatheType));
    }
    private static IReadOnlyList<string> Changed(ClientItemRecord? current, ClientItemRecord desired)
    {
        if (current is null) return ["ID", "ClassID", "SubclassID", "SoundOverrideSubclass", "Material", "DisplayInfoID", "InventoryType", "SheatheType"];
        var result = new List<string>(); if (current.ClassId != desired.ClassId) result.Add("ClassID"); if (current.SubclassId != desired.SubclassId) result.Add("SubclassID"); if (current.SoundOverrideSubclassId != desired.SoundOverrideSubclassId) result.Add("SoundOverrideSubclass"); if (current.Material != desired.Material) result.Add("Material"); if (current.DisplayInfoId != desired.DisplayInfoId) result.Add("DisplayInfoID"); if (current.InventoryType != desired.InventoryType) result.Add("InventoryType"); if (current.SheatheType != desired.SheatheType) result.Add("SheatheType"); return result;
    }
    private static bool Equivalent(ItemClientSyncPlan left, ItemClientSyncPlan right) =>
        left.TargetItemDbcSha256.Equals(right.TargetItemDbcSha256, StringComparison.OrdinalIgnoreCase) && left.ItemDisplayInfoDbcSha256.Equals(right.ItemDisplayInfoDbcSha256, StringComparison.OrdinalIgnoreCase) && left.SchemaSha256.Equals(right.SchemaSha256, StringComparison.OrdinalIgnoreCase) &&
        left.ServerSnapshotSha256.Equals(right.ServerSnapshotSha256, StringComparison.OrdinalIgnoreCase) && left.TargetRowCount == right.TargetRowCount && left.ServerRowCount == right.ServerRowCount && left.ClientOnlyRows.SequenceEqual(right.ClientOnlyRows) && left.MissingDisplayIds.SequenceEqual(right.MissingDisplayIds) &&
        left.Mutations.Count == right.Mutations.Count && left.Mutations.Zip(right.Mutations).All(pair => pair.First.Id == pair.Second.Id && pair.First.Existing == pair.Second.Existing && pair.First.Desired == pair.Second.Desired && pair.First.ChangedFields.SequenceEqual(pair.Second.ChangedFields));
    private static string SnapshotHash(IReadOnlyList<ServerClientItemRecord> rows) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows.OrderBy(row => row.Client.Id))));
    private static void WriteWmvCatalog(string path, IEnumerable<ServerClientItemRecord> rows)
    {
        using var writer = new StreamWriter(path, false, Encoding.ASCII); foreach (var row in rows.OrderBy(row => row.Client.Id)) writer.WriteLine($"{row.Client.Id},{row.Quality},{Csv(Ascii(row.Name))}");
    }
    private static string Ascii(string value) { var normalized = value.Normalize(NormalizationForm.FormD); var builder = new StringBuilder(); foreach (var c in normalized) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c <= 127 ? c : '?'); return builder.ToString().Normalize(NormalizationForm.FormC); }
    private static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
    private static DatabaseColumnCapability RequiredColumn(DatabaseTableCapability table, params string[] names) => names.Select(table.Find).FirstOrDefault(column => column is not null) ?? throw new NotSupportedException($"{table.Name} has no required column matching {string.Join('/', names)}.");
    private static DbcColumn Column(DbcSchemaResolution schema, string name) => schema.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"DBC schema has no '{name}' column.");
    private static int Signed(uint value) => unchecked((int)value); private static uint Raw(int value) => unchecked((uint)value);
    private static int I32(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture); private static uint U32(object value) => Convert.ToUInt32(value, CultureInfo.InvariantCulture);
    private static string Quote(string value) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";
    private static string RequiredFile(string path, string label) { if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{label} path is required."); path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException($"{label} was not found.", path); return path; }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void VerifyHash(string path, string expected, string label) { if (!File.Exists(path)) throw new FileNotFoundException($"Bound {label} disappeared.", path); var actual = Hash(path); if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Bound {label} changed after planning. Expected {expected}; found {actual}."); }
    private static void AtomicJson(string path, object value, bool overwrite) { path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Output exists: {path}"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + $".tmp-{Guid.NewGuid():N}"; try { File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temp, path, overwrite); } finally { if (File.Exists(temp)) File.Delete(temp); } }
}
