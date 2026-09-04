using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

internal sealed record CrossBuildItemVisualFieldRule(
    string HostField,
    string DonorField,
    CrossBuildAssetFieldRule AssetRule);

/// <summary>
/// Projects Legion's direct ItemVisuals ModelFileID slots into MoP's indirect
/// ItemVisualEffects records. New effect IDs are allocated once, referenced by
/// the converted ItemVisuals rows, and published only when those rows survive
/// conversion and independent verification.
/// </summary>
internal sealed class CrossBuildItemVisualProjection
{
    private sealed record PlannedEffect(uint FileDataId, uint EffectId, string ModelPath);

    private static readonly IReadOnlyList<CrossBuildItemVisualFieldRule> Rules = Enumerable.Range(0, 5)
        .Select(index =>
        {
            var hostField = $"Slot[{index}]";
            var donorField = $"ModelFileID[{index}]";
            var assetRule = CrossBuildAssetBridgeCatalog.Find("mop-18414", "legion-26972", "ITEMVISUALS", hostField)
                ?? throw new InvalidOperationException($"Missing ItemVisuals asset bridge rule for {hostField}.");
            return new CrossBuildItemVisualFieldRule(hostField, donorField, assetRule);
        }).ToArray();

    private readonly CrossBuildAssetBridge _assets;
    private readonly string _hostPath;
    private readonly string _donorPath;
    private readonly DbcColumn _idColumn;
    private readonly DbcColumn _modelColumn;
    private readonly Dictionary<string, uint> _effectByModelPath;
    private readonly Dictionary<uint, PlannedEffect> _plannedByFileDataId = [];
    private readonly Dictionary<uint, PlannedEffect> _plannedByEffectId = [];
    private readonly HashSet<uint> _usedEffectIds = [];
    private readonly HashSet<uint> _occupiedEffectIds;
    private readonly uint _maximumEffectId;
    private uint _nextEffectId;

    private CrossBuildItemVisualProjection(
        CrossBuildAssetBridge assets,
        string hostPath,
        string donorPath,
        DbcColumn idColumn,
        DbcColumn modelColumn,
        Dictionary<string, uint> effectByModelPath,
        HashSet<uint> occupiedEffectIds,
        uint maximumEffectId,
        uint nextEffectId,
        int hostRows)
    {
        _assets = assets;
        _hostPath = hostPath;
        _donorPath = donorPath;
        _idColumn = idColumn;
        _modelColumn = modelColumn;
        _effectByModelPath = effectByModelPath;
        _occupiedEffectIds = occupiedEffectIds;
        _maximumEffectId = maximumEffectId;
        _nextEffectId = nextEffectId;
        HostRows = hostRows;
    }

    public int HostRows { get; }

    public static bool Supports(string hostProfileId, string donorProfileId) =>
        hostProfileId.Equals("mop-18414", StringComparison.OrdinalIgnoreCase) &&
        donorProfileId.Equals("legion-26972", StringComparison.OrdinalIgnoreCase);

    public static CrossBuildItemVisualProjection Create(
        string hostProfileId,
        string donorProfileId,
        string hostItemVisualEffectsPath,
        string donorItemVisualsPath,
        ClientTableSchemaProvider hostSchema,
        CrossBuildAssetBridge assets)
    {
        if (!Supports(hostProfileId, donorProfileId))
            throw new InvalidOperationException($"ItemVisual projection does not support {hostProfileId} <- {donorProfileId}.");
        hostItemVisualEffectsPath = Path.GetFullPath(hostItemVisualEffectsPath);
        donorItemVisualsPath = Path.GetFullPath(donorItemVisualsPath);
        var file = WdbcFile.Load(hostItemVisualEffectsPath);
        if (!file.AllowsStructuralMutation)
            throw new InvalidDataException($"{Path.GetFileName(hostItemVisualEffectsPath)} cannot accept projected ItemVisualEffects rows.");
        var resolution = hostSchema.Resolve(file);
        var identity = CrossBuildTableIdentityCatalog.Resolve(hostProfileId, "ITEMVISUALEFFECTS", resolution.Columns, resolution.KeyStrategy);
        var idColumn = DbcRecordIdentity.PhysicalColumn(resolution.Columns, identity.Strategy)
            ?? throw new InvalidDataException("MoP ItemVisualEffects requires a physical ID column because ItemVisuals stores those IDs directly.");
        var modelColumn = resolution.Columns.SingleOrDefault(column => column.Name.Equals("Model", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("MoP ItemVisualEffects schema is missing Model.");
        if (modelColumn.Type != DbcValueType.StringOffset)
            throw new InvalidDataException("MoP ItemVisualEffects.Model is not a string field.");

        var rows = DbcRecordIdentity.IndexRows(file, resolution.Columns, identity.Strategy);
        var occupied = rows.Keys.ToHashSet();
        var byPath = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in rows.OrderBy(pair => pair.Key))
        {
            var path = NormalizeModelPath(file.GetString(file.GetRaw64(pair.Value, modelColumn)));
            if (path.Length > 0) byPath.TryAdd(path, pair.Key);
        }
        var maximum = MaximumPositiveId(idColumn);
        var next = occupied.DefaultIfEmpty().Max();
        next = next < maximum ? next + 1 : 1;
        return new(assets, hostItemVisualEffectsPath, donorItemVisualsPath, idColumn, modelColumn, byPath, occupied, maximum, next, file.RowCount);
    }

    public static CrossBuildItemVisualFieldRule? FindRule(string canonicalTable, string hostField)
    {
        if (!canonicalTable.Equals("ITEMVISUALS", StringComparison.OrdinalIgnoreCase)) return null;
        return Rules.SingleOrDefault(rule => rule.HostField.Equals(hostField, StringComparison.OrdinalIgnoreCase));
    }

    public bool TryResolve(
        CrossBuildItemVisualFieldRule rule,
        uint fileDataId,
        bool markUsed,
        out uint effectId,
        out string error)
    {
        if (fileDataId == 0)
        {
            effectId = 0;
            error = string.Empty;
            return true;
        }
        if (!_assets.TryResolve(rule.AssetRule, fileDataId, markUsed, out var modelPath, out error))
        {
            effectId = 0;
            return false;
        }
        modelPath = NormalizeModelPath(modelPath);
        if (_effectByModelPath.TryGetValue(modelPath, out effectId))
        {
            if (markUsed && _plannedByEffectId.ContainsKey(effectId)) _usedEffectIds.Add(effectId);
            error = string.Empty;
            return true;
        }
        if (!_plannedByFileDataId.TryGetValue(fileDataId, out var planned))
        {
            effectId = AllocateEffectId();
            planned = new(fileDataId, effectId, modelPath);
            _plannedByFileDataId[fileDataId] = planned;
            _plannedByEffectId[effectId] = planned;
            _effectByModelPath[modelPath] = effectId;
        }
        effectId = planned.EffectId;
        if (markUsed) _usedEffectIds.Add(effectId);
        error = string.Empty;
        return true;
    }

    public CrossBuildMashupTableReport? Publish(
        ClientTableSchemaProvider hostSchema,
        string hostTableRoot,
        string clientPayloadRoot,
        string serverPayloadRoot,
        string idLedgerPath,
        CancellationToken cancellationToken)
    {
        if (_usedEffectIds.Count == 0) return null;
        var source = WdbcFile.Load(_hostPath);
        var resolution = hostSchema.Resolve(source);
        var identity = CrossBuildTableIdentityCatalog.Resolve("mop-18414", "ITEMVISUALEFFECTS", resolution.Columns, resolution.KeyStrategy);
        var idColumn = DbcRecordIdentity.PhysicalColumn(resolution.Columns, identity.Strategy)
            ?? throw new InvalidDataException("MoP ItemVisualEffects lost its physical ID column before publication.");
        var modelColumn = resolution.Columns.Single(column => column.Name.Equals("Model", StringComparison.OrdinalIgnoreCase));

        foreach (var effectId in _usedEffectIds.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effect = _plannedByEffectId[effectId];
            var row = source.AddBlankRow();
            source.SetRaw64(row, idColumn, effect.EffectId);
            source.SetDisplayValue(row, modelColumn, effect.ModelPath);
        }

        var outputClient = Path.Combine(clientPayloadRoot, Path.GetFileName(_hostPath));
        Directory.CreateDirectory(Path.GetDirectoryName(outputClient)!);
        source.Save(outputClient, createBackup: false);
        var relativeServer = Path.GetRelativePath(hostTableRoot, _hostPath);
        if (relativeServer.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Host ItemVisualEffects escaped its declared table root: {_hostPath}");
        var outputServer = Path.Combine(serverPayloadRoot, relativeServer);
        Directory.CreateDirectory(Path.GetDirectoryName(outputServer)!);
        File.Copy(outputClient, outputServer, overwrite: true);
        Verify(outputClient, hostSchema, identity.Strategy);

        using (var ledger = new StreamWriter(idLedgerPath, append: true, new UTF8Encoding(false)))
            foreach (var effectId in _usedEffectIds.Order())
            {
                var effect = _plannedByEffectId[effectId];
                ledger.WriteLine($"ITEMVISUALEFFECTS,{effect.FileDataId.ToString(CultureInfo.InvariantCulture)},{effect.EffectId.ToString(CultureInfo.InvariantCulture)},project-model-filedata");
            }

        var used = _usedEffectIds.Count;
        return new("ITEMVISUALEFFECTS", Path.GetFileNameWithoutExtension(_hostPath), "ItemVisuals.ModelFileID", _hostPath, _donorPath,
            CrossBuildMashupTableStatus.Converted, HostRows, _plannedByFileDataId.Count, _plannedByFileDataId.Count, 1,
            ["ItemVisuals.ModelFileID[*] -> ItemVisualEffects.Model (verified model projection)"], [], [], 0, 0, 0, used, 0, 0, 0,
            outputClient, outputServer, idLedgerPath,
            [$"Synthesized {used:N0} MoP ItemVisualEffects row(s) for verified Legion model assets referenced by emitted ItemVisuals rows."], []);
    }

    private void Verify(string outputPath, ClientTableSchemaProvider hostSchema, DbcRecordKeyStrategy strategy)
    {
        var output = WdbcFile.Load(outputPath);
        var resolution = hostSchema.Resolve(output);
        var rows = DbcRecordIdentity.IndexRows(output, resolution.Columns, strategy);
        if (output.RowCount != HostRows + _usedEffectIds.Count)
            throw new InvalidDataException($"ItemVisualEffects output has {output.RowCount:N0} rows; expected {HostRows + _usedEffectIds.Count:N0}.");
        foreach (var effectId in _usedEffectIds)
        {
            if (!rows.TryGetValue(effectId, out var row)) throw new InvalidDataException($"Projected ItemVisualEffects ID {effectId:N0} is missing.");
            var actual = NormalizeModelPath(output.GetString(output.GetRaw64(row, _modelColumn)));
            if (!actual.Equals(_plannedByEffectId[effectId].ModelPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Projected ItemVisualEffects ID {effectId:N0} has the wrong model path.");
        }
        var stable = outputPath + ".stable-test";
        try
        {
            output.Save(stable, createBackup: false);
            if (!Hash(outputPath).Equals(Hash(stable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ItemVisualEffects did not produce a stable second serialization.");
        }
        finally { if (File.Exists(stable)) File.Delete(stable); }
    }

    private uint AllocateEffectId()
    {
        for (ulong attempts = 0; attempts <= _maximumEffectId; attempts++)
        {
            if (_nextEffectId == 0 || _nextEffectId > _maximumEffectId) _nextEffectId = 1;
            var candidate = _nextEffectId;
            _nextEffectId = candidate == _maximumEffectId ? 1 : candidate + 1;
            if (_occupiedEffectIds.Add(candidate)) return candidate;
        }
        throw new InvalidOperationException($"No collision-free ItemVisualEffects ID remains in 1..{_maximumEffectId:N0}.");
    }

    private static string NormalizeModelPath(string value)
    {
        value = value.Trim().TrimEnd('\0').Replace('/', '\\');
        while (value.Contains("\\\\", StringComparison.Ordinal)) value = value.Replace("\\\\", "\\", StringComparison.Ordinal);
        return value.TrimStart('\\');
    }

    private static uint MaximumPositiveId(DbcColumn key)
    {
        var width = Math.Clamp(key.EffectiveBitWidth, 1, 32);
        var signed = key.Type is DbcValueType.Int32 or DbcValueType.Int64;
        return signed ? width == 32 ? int.MaxValue : (1u << (width - 1)) - 1 : width == 32 ? uint.MaxValue : (1u << width) - 1;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
