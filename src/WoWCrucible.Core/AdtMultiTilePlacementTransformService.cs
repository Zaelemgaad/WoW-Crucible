using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record AdtMultiTileCoordinate(int TileX, int TileY);

public sealed record AdtMultiTilePlacementTransformPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    AdtPlacementKind Kind,
    uint UniqueId,
    string MapPrefix,
    string SourceDirectory,
    string SourceFingerprint,
    IReadOnlyList<AdtMultiTileSource> SourceTiles,
    int SelectedTileX,
    int SelectedTileY,
    int SelectedIndex,
    string ClientPath,
    string AssetPath,
    string AssetSha256,
    AdtPlacementVector OriginalPosition,
    AdtPlacementVector OriginalOrientation,
    ushort OriginalScaleRaw,
    AdtPlacementVector EditedPosition,
    AdtPlacementVector EditedOrientation,
    ushort EditedScaleRaw,
    ushort Flags,
    ushort DoodadSet,
    ushort NameSet,
    AdtPlacementVector OriginalMinimum,
    AdtPlacementVector OriginalMaximum,
    AdtPlacementVector EditedMinimum,
    AdtPlacementVector EditedMaximum,
    IReadOnlyList<AdtPlacementLifecyclePlan> DeleteSegments,
    IReadOnlyList<AdtMultiTileCoordinate> TargetTiles);

public sealed record AdtMultiTilePlacementTransformReceipt(
    int FormatVersion,
    DateTimeOffset AppliedUtc,
    string PlanSha256,
    string OutputRoot,
    string ManifestPath,
    IReadOnlyList<AdtMultiTileOutput> Outputs,
    AdtMultiTilePlacementTransformPlan Plan);

public sealed record AdtMultiTilePlacementTransformResult(
    string OutputRoot,
    string PayloadRoot,
    string PlanPath,
    string ManifestPath,
    string ReceiptPath,
    AdtPlacementKind Kind,
    uint UniqueId,
    IReadOnlyList<AdtMultiTileOutput> Outputs);

internal sealed record AdtMultiTilePlacementLineage(
    string MapPrefix,
    string SourceDirectory,
    IReadOnlyList<AdtMultiTileSource> SourceTiles,
    IReadOnlyList<AdtMultiTileOutput> Outputs);

/// <summary>
/// Moves, rotates, or scales one semantic ADT object as a map-wide transaction.
/// Every old UID copy is removed first, then the same UID is inserted into every
/// tile intersected by the edited geometry. Only a new path-correct payload is
/// published; all source and inherited ADTs remain immutable.
/// </summary>
public static class AdtMultiTilePlacementTransformService
{
    private const int CurrentFormatVersion = 1;
    private const int MaximumTiles = 4_096;
    private const string PlanFileName = "adt-placement-multi-transform-plan.json";
    private const string ManifestFileName = "adt-placement-transform-patch.crucible-patch.json";
    private const string ReceiptFileName = "adt-placement-multi-transform-receipt.json";
    private const string PendingFileName = ".crucible-owned-pending";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AdtMultiTilePlacementTransformPlan Plan(string selectedAdtPath, AdtPlacementKind kind, int index, string assetPath,
        Vector3? position = null, Vector3? orientation = null, ushort? scaleRaw = null)
    {
        selectedAdtPath = Path.GetFullPath(selectedAdtPath); assetPath = Path.GetFullPath(assetPath);
        if (!File.Exists(assetPath)) throw new FileNotFoundException("The exact extracted placement asset does not exist.", assetPath);
        var workspace = AdtMultiTilePlacementService.DiscoverWorkspace(selectedAdtPath);
        var selectedTile = workspace.Tiles.SingleOrDefault(tile => tile.Path.Equals(selectedAdtPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected ADT is not an effective tile in its discovered map workspace.");
        var delete = AdtMultiTilePlacementService.PlanDelete(selectedAdtPath, kind, index);
        var inspection = MapAssetInspectionService.Inspect(selectedAdtPath);

        string clientPath; uint uid; Vector3 originalPosition; Vector3 originalOrientation; ushort originalScale; ushort flags; ushort doodadSet; ushort nameSet;
        Vector3 originalMinimum; Vector3 originalMaximum; Vector3 editedMinimum; Vector3 editedMaximum;
        Vector3 editedPosition; Vector3 editedOrientation; ushort editedScale;
        if (kind == AdtPlacementKind.M2)
        {
            var source = inspection.M2Placements.ElementAtOrDefault(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
            clientPath = source.ClientPath ?? throw new InvalidDataException("The selected MDDF has no resolved MMDX/MMID client path."); uid = source.UniqueId;
            originalPosition = source.Position; originalOrientation = source.Orientation; originalScale = source.ScaleRaw; flags = source.Flags; doodadSet = 0; nameSet = 0;
            editedPosition = position ?? source.Position; editedOrientation = orientation ?? source.Orientation; editedScale = scaleRaw ?? source.ScaleRaw;
            if (editedScale == 0) throw new InvalidDataException("M2 placement scale cannot be zero.");
            RequireAssetIdentity(clientPath, assetPath, kind); var evidence = M2PlacementBoundsService.InspectModel(assetPath);
            (originalMinimum, originalMaximum) = M2PlacementBoundsService.Calculate(evidence, originalPosition, originalOrientation, originalScale / 1024f);
            (editedMinimum, editedMaximum) = M2PlacementBoundsService.Calculate(evidence, editedPosition, editedOrientation, editedScale / 1024f);
        }
        else
        {
            var source = inspection.WmoPlacements.ElementAtOrDefault(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
            clientPath = source.ClientPath ?? throw new InvalidDataException("The selected MODF has no resolved MWMO/MWID client path."); uid = source.UniqueId;
            originalPosition = source.Position; originalOrientation = source.Orientation; originalScale = source.ScaleRaw; flags = source.Flags; doodadSet = source.DoodadSet; nameSet = source.NameSet;
            editedPosition = position ?? source.Position; editedOrientation = orientation ?? source.Orientation; editedScale = scaleRaw ?? source.ScaleRaw;
            RequireAssetIdentity(clientPath, assetPath, kind); var evidence = WmoPlacementBoundsService.InspectRoot(assetPath); WmoPlacementBoundsService.RequireCalibrated(source, evidence);
            var originalBounds = WmoPlacementBoundsService.Calculate(evidence, originalPosition, originalOrientation, EffectiveScale(originalScale)); originalMinimum = originalBounds.Minimum; originalMaximum = originalBounds.Maximum;
            var editedBounds = WmoPlacementBoundsService.Calculate(evidence, editedPosition, editedOrientation, EffectiveScale(editedScale)); editedMinimum = editedBounds.Minimum; editedMaximum = editedBounds.Maximum;
        }
        RequireFinite(editedPosition, "edited position"); RequireFinite(editedOrientation, "edited orientation");
        if (Same(originalPosition, editedPosition) && Same(originalOrientation, editedOrientation) && originalScale == editedScale)
            throw new InvalidOperationException("The edited transform is byte-identical to every current UID occurrence; no transaction is required.");
        if (delete.UniqueId != uid) throw new InvalidDataException("The selected placement identity changed while building its coordinated delete set.");
        var targets = AdtMultiTilePlacementService.TouchedTiles(editedMinimum, editedMaximum).Select(value => new AdtMultiTileCoordinate(value.X, value.Y)).ToArray();
        var available = workspace.Tiles.Select(tile => (tile.TileX, tile.TileY)).ToHashSet(); var missing = targets.Where(tile => !available.Contains((tile.TileX, tile.TileY))).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"Edited placement bounds require missing ADT tile(s): {string.Join(", ", missing.Select(value => $"{workspace.Prefix}_{value.TileX}_{value.TileY}.adt"))}. Crucible will not move only part of the object.");
        return new(CurrentFormatVersion, DateTimeOffset.UtcNow, kind, uid, workspace.Prefix, workspace.Directory,
            AdtMultiTilePlacementService.Fingerprint(workspace.Tiles), workspace.Tiles, selectedTile.TileX, selectedTile.TileY, index,
            clientPath, assetPath, AdtMultiTilePlacementService.Sha256(assetPath), AdtPlacementVector.From(originalPosition), AdtPlacementVector.From(originalOrientation), originalScale,
            AdtPlacementVector.From(editedPosition), AdtPlacementVector.From(editedOrientation), editedScale, flags, doodadSet, nameSet,
            AdtPlacementVector.From(originalMinimum), AdtPlacementVector.From(originalMaximum), AdtPlacementVector.From(editedMinimum), AdtPlacementVector.From(editedMaximum), delete.Segments, targets);
    }

    public static void SavePlan(AdtMultiTilePlacementTransformPlan plan, string path, bool overwrite = false)
    {
        Validate(plan); path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Multi-tile transform plan already exists: {path}");
        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtMultiTilePlacementTransformPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The multi-tile transform plan does not exist.", path);
        var plan = JsonSerializer.Deserialize<AdtMultiTilePlacementTransformPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The multi-tile transform plan is empty.");
        Validate(plan); return plan;
    }

    public static AdtMultiTilePlacementTransformResult Apply(AdtMultiTilePlacementTransformPlan plan, string outputRoot)
    {
        Validate(plan); outputRoot = Path.GetFullPath(outputRoot);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot)) throw new IOException($"Multi-tile transform output must be a brand-new path: {outputRoot}");
        var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidOperationException("Output root has no parent directory."); Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.{Guid.NewGuid():N}.tmp"); Directory.CreateDirectory(temporary);
        File.WriteAllText(Path.Combine(temporary, PendingFileName), "WoW Crucible owns this incomplete directory."); var moved = false;
        try
        {
            var intermediate = Path.Combine(temporary, ".intermediate"); Directory.CreateDirectory(intermediate);
            var effective = plan.SourceTiles.ToDictionary(tile => (tile.TileX, tile.TileY), tile => tile.Path);
            var originalOccupancy = plan.SourceTiles.Select(tile => tile.Path).ToArray();
            var removed = new Dictionary<(int X, int Y), (string Path, int References)>();
            foreach (var segment in plan.DeleteSegments)
            {
                var tile = AdtMultiTilePlacementService.ParseTile(segment.InputPath); var path = Path.Combine(intermediate, $"{plan.MapPrefix}_{tile.X}_{tile.Y}.adt");
                var result = AdtPlacementLifecycleService.ApplyCoordinated(segment, path, plan.SourceDirectory, plan.MapPrefix, originalOccupancy);
                effective[(tile.X, tile.Y)] = path; removed[(tile.X, tile.Y)] = (path, result.ReferencedCells);
            }
            var occupancyAfterDelete = effective.OrderBy(value => value.Key.Item2).ThenBy(value => value.Key.Item1).Select(value => value.Value).ToArray();
            if (occupancyAfterDelete.SelectMany(path =>
                {
                    var value = MapAssetInspectionService.Inspect(path); return value.M2Placements.Select(row => row.UniqueId).Concat(value.WmoPlacements.Select(row => row.UniqueId));
                }).Any(uid => uid == plan.UniqueId)) throw new InvalidDataException($"UID {plan.UniqueId:N0} remained in the effective workspace after coordinated removal.");

            var targetPlans = plan.TargetTiles.Select(tile => AdtPlacementLifecycleService.PlanCoordinatedAdd(effective[(tile.TileX, tile.TileY)], plan.Kind, plan.ClientPath, plan.AssetPath,
                plan.EditedPosition.ToVector3(), plan.EditedOrientation.ToVector3(), plan.EditedScaleRaw, plan.UniqueId, plan.Flags, plan.DoodadSet, plan.NameSet,
                plan.SourceDirectory, plan.MapPrefix, occupancyAfterDelete)).ToArray();
            var payloadRoot = Path.Combine(temporary, "Payload"); var mapRoot = Path.Combine(payloadRoot, "World", "Maps", plan.MapPrefix); Directory.CreateDirectory(mapRoot);
            var outputs = new Dictionary<(int X, int Y), AdtMultiTileOutput>();
            foreach (var add in targetPlans)
            {
                var tile = AdtMultiTilePlacementService.ParseTile(add.InputPath); var path = Path.Combine(mapRoot, $"{plan.MapPrefix}_{tile.X}_{tile.Y}.adt");
                var result = AdtPlacementLifecycleService.ApplyCoordinated(add, path, plan.SourceDirectory, plan.MapPrefix, occupancyAfterDelete);
                outputs[(tile.X, tile.Y)] = new(tile.X, tile.Y, path, result.OutputSha256, result.ReferencedCells);
            }
            foreach (var removedTile in removed.Where(value => !outputs.ContainsKey(value.Key)))
            {
                var path = Path.Combine(mapRoot, $"{plan.MapPrefix}_{removedTile.Key.X}_{removedTile.Key.Y}.adt"); File.Copy(removedTile.Value.Path, path, false);
                outputs[removedTile.Key] = new(removedTile.Key.X, removedTile.Key.Y, path, AdtMultiTilePlacementService.Sha256(path), removedTile.Value.References);
            }
            VerifyOutputs(plan, outputs.Values); Directory.Delete(intermediate, true);
            AtomicWrite(Path.Combine(temporary, PlanFileName), JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), false);
            Directory.Move(temporary, outputRoot); moved = true;
            var finalPayload = Path.Combine(outputRoot, "Payload"); var finalOutputs = outputs.Values.Select(output =>
            {
                var path = Path.Combine(finalPayload, "World", "Maps", plan.MapPrefix, Path.GetFileName(output.Path));
                return output with { Path = path, Sha256 = AdtMultiTilePlacementService.Sha256(path) };
            }).OrderBy(output => output.TileY).ThenBy(output => output.TileX).ToArray();
            var manifestPath = Path.Combine(outputRoot, ManifestFileName);
            PatchManifestService.Save(manifestPath, $"{plan.MapPrefix} transform placement {plan.UniqueId}", "patch-Crucible-Map.MPQ",
                finalOutputs.Select(tile => new PatchEntry(tile.Path, $@"World\Maps\{plan.MapPrefix}\{Path.GetFileName(tile.Path)}")),
                policy: new(ExpectedEntryCount: finalOutputs.Length, AllowedGlobs: [$@"World\Maps\{plan.MapPrefix}\*.adt"]));
            var planPath = Path.Combine(outputRoot, PlanFileName); var receiptPath = Path.Combine(outputRoot, ReceiptFileName);
            var receipt = new AdtMultiTilePlacementTransformReceipt(CurrentFormatVersion, DateTimeOffset.UtcNow, AdtMultiTilePlacementService.Sha256(planPath), outputRoot, manifestPath, finalOutputs, plan);
            AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), false); File.Delete(Path.Combine(outputRoot, PendingFileName));
            return new(outputRoot, finalPayload, planPath, manifestPath, receiptPath, plan.Kind, plan.UniqueId, finalOutputs);
        }
        catch { DeleteOwnedDirectory(moved ? outputRoot : temporary); throw; }
    }

    internal static AdtMultiTilePlacementLineage? TryFindLineage(string inputPath)
    {
        inputPath = Path.GetFullPath(inputPath); var directory = new DirectoryInfo(Path.GetDirectoryName(inputPath)!);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            var receiptPath = Path.Combine(directory.FullName, ReceiptFileName); if (!File.Exists(receiptPath)) continue;
            var receipt = JsonSerializer.Deserialize<AdtMultiTilePlacementTransformReceipt>(File.ReadAllBytes(receiptPath), JsonOptions) ?? throw new InvalidDataException($"Multi-tile transform receipt is empty: {receiptPath}");
            if (receipt.FormatVersion != CurrentFormatVersion || !Path.GetFullPath(receipt.OutputRoot).Equals(directory.FullName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Multi-tile transform receipt has an unsupported format or root: {receiptPath}");
            var planPath = Path.Combine(directory.FullName, PlanFileName);
            if (!File.Exists(planPath) || !AdtMultiTilePlacementService.Sha256(planPath).Equals(receipt.PlanSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Multi-tile transform receipt plan is missing or changed: {receiptPath}");
            var persisted = JsonSerializer.Deserialize<AdtMultiTilePlacementTransformPlan>(File.ReadAllBytes(planPath), JsonOptions) ?? throw new InvalidDataException($"Multi-tile transform plan is empty: {planPath}");
            if (!Equivalent(receipt.Plan, persisted)) throw new InvalidDataException($"Multi-tile transform receipt and persisted plan disagree: {receiptPath}"); Validate(persisted);
            var expectedManifest = Path.Combine(directory.FullName, ManifestFileName);
            if (!Path.GetFullPath(receipt.ManifestPath).Equals(expectedManifest, StringComparison.OrdinalIgnoreCase) || !File.Exists(expectedManifest) || !PatchManifestService.Validate(PatchManifestService.Load(expectedManifest)).Passed) throw new InvalidDataException($"Multi-tile transform manifest is missing, invalid, or outside its output root: {receiptPath}");
            if (receipt.Outputs.Count is 0 or > MaximumTiles || receipt.Outputs.Select(output => (output.TileX, output.TileY)).Distinct().Count() != receipt.Outputs.Count) throw new InvalidDataException($"Multi-tile transform receipt has an invalid output set: {receiptPath}");
            foreach (var output in receipt.Outputs)
            {
                var expected = Path.Combine(directory.FullName, "Payload", "World", "Maps", persisted.MapPrefix, $"{persisted.MapPrefix}_{output.TileX}_{output.TileY}.adt");
                if (!Path.GetFullPath(output.Path).Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(expected) || !AdtMultiTilePlacementService.Sha256(expected).Equals(output.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Multi-tile transform output is missing, changed, or outside its exact payload path: {output.Path}");
            }
            if (!receipt.Outputs.Any(output => output.Path.Equals(inputPath, StringComparison.OrdinalIgnoreCase))) continue;
            return new(persisted.MapPrefix, persisted.SourceDirectory, persisted.SourceTiles, receipt.Outputs);
        }
        return null;
    }

    private static void Validate(AdtMultiTilePlacementTransformPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.FormatVersion != CurrentFormatVersion || !Enum.IsDefined(plan.Kind) || plan.UniqueId == 0 || plan.SelectedIndex < 0 || string.IsNullOrWhiteSpace(plan.MapPrefix) || string.IsNullOrWhiteSpace(plan.ClientPath) || plan.SourceTiles.Count is 0 or > MaximumTiles || plan.DeleteSegments.Count is 0 or > MaximumTiles || plan.TargetTiles.Count is 0 or > MaximumTiles)
            throw new InvalidDataException("Multi-tile transform plan has an unsupported format, kind, identity, map, or tile count.");
        if (!Path.GetFullPath(plan.SourceDirectory).Equals(plan.SourceDirectory, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(plan.AssetPath).Equals(plan.AssetPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Multi-tile transform source and asset paths must be absolute.");
        var sources = plan.SourceTiles.OrderBy(tile => tile.TileY).ThenBy(tile => tile.TileX).ToArray();
        if (sources.Select(tile => (tile.TileX, tile.TileY)).Distinct().Count() != sources.Length || sources.Any(tile => !File.Exists(tile.Path) || !AdtMultiTilePlacementService.Sha256(tile.Path).Equals(tile.Sha256, StringComparison.OrdinalIgnoreCase)) || !AdtMultiTilePlacementService.Fingerprint(sources).Equals(plan.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A multi-tile transform source is missing, changed, duplicated, or no longer matches the workspace fingerprint.");
        if (!File.Exists(plan.AssetPath) || !AdtMultiTilePlacementService.Sha256(plan.AssetPath).Equals(plan.AssetSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The transform asset is missing or changed.");
        var selected = sources.SingleOrDefault(tile => tile.TileX == plan.SelectedTileX && tile.TileY == plan.SelectedTileY) ?? throw new InvalidDataException("The transform selected tile is absent from its source workspace.");
        var rebuilt = Plan(selected.Path, plan.Kind, plan.SelectedIndex, plan.AssetPath, plan.EditedPosition.ToVector3(), plan.EditedOrientation.ToVector3(), plan.EditedScaleRaw);
        if (!Equivalent(plan, rebuilt)) throw new InvalidDataException("Multi-tile transform plan no longer matches its exact UID copies, geometry, edited footprint, or workspace.");
    }

    private static void VerifyOutputs(AdtMultiTilePlacementTransformPlan plan, IEnumerable<AdtMultiTileOutput> outputs)
    {
        var targets = plan.TargetTiles.Select(tile => (tile.TileX, tile.TileY)).ToHashSet(); var outputSet = outputs.ToArray();
        var expectedCoordinates = plan.DeleteSegments.Select(segment => { var tile = AdtMultiTilePlacementService.ParseTile(segment.InputPath); return (tile.X, tile.Y); }).Concat(targets).ToHashSet();
        if (!outputSet.Select(output => (output.TileX, output.TileY)).ToHashSet().SetEquals(expectedCoordinates)) throw new InvalidDataException("Transform payload did not contain the exact union of old and edited placement tiles.");
        foreach (var output in outputSet)
        {
            var inspection = MapAssetInspectionService.Inspect(output.Path);
            var matches = plan.Kind == AdtPlacementKind.M2
                ? inspection.M2Placements.Where(value => value.UniqueId == plan.UniqueId).Select(value => (Position: value.Position, Orientation: value.Orientation, Scale: value.ScaleRaw, Flags: value.Flags, Doodad: (ushort)0, Name: (ushort)0, Min: (Vector3?)null, Max: (Vector3?)null, Path: value.ClientPath)).ToArray()
                : inspection.WmoPlacements.Where(value => value.UniqueId == plan.UniqueId).Select(value => (Position: value.Position, Orientation: value.Orientation, Scale: value.ScaleRaw, Flags: value.Flags, Doodad: value.DoodadSet, Name: value.NameSet, Min: (Vector3?)value.MinimumExtent, Max: (Vector3?)value.MaximumExtent, Path: value.ClientPath)).ToArray();
            if (!targets.Contains((output.TileX, output.TileY))) { if (matches.Length != 0) throw new InvalidDataException($"Old-only tile {output.TileX},{output.TileY} retained transformed UID {plan.UniqueId:N0}."); continue; }
            if (matches.Length != 1) throw new InvalidDataException($"Target tile {output.TileX},{output.TileY} does not contain exactly one transformed UID {plan.UniqueId:N0} record.");
            var value = matches[0];
            if (!Same(value.Position, plan.EditedPosition.ToVector3()) || !Same(value.Orientation, plan.EditedOrientation.ToVector3()) || value.Scale != plan.EditedScaleRaw || value.Flags != plan.Flags || value.Doodad != plan.DoodadSet || value.Name != plan.NameSet || !string.Equals(value.Path, plan.ClientPath, StringComparison.OrdinalIgnoreCase) ||
                plan.Kind == AdtPlacementKind.Wmo && (!Same(value.Min!.Value, plan.EditedMinimum.ToVector3()) || !Same(value.Max!.Value, plan.EditedMaximum.ToVector3())))
                throw new InvalidDataException($"Target tile {output.TileX},{output.TileY} did not re-parse to the exact reviewed transformed placement.");
        }
    }

    private static bool Equivalent(AdtMultiTilePlacementTransformPlan left, AdtMultiTilePlacementTransformPlan right)
    {
        static AdtMultiTilePlacementTransformPlan Canonical(AdtMultiTilePlacementTransformPlan value) => value with
        {
            CreatedUtc = DateTimeOffset.UnixEpoch,
            DeleteSegments = value.DeleteSegments.Select(segment => segment with { CreatedUtc = DateTimeOffset.UnixEpoch }).ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(Canonical(left), JsonOptions).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(Canonical(right), JsonOptions));
    }

    private static void RequireAssetIdentity(string clientPath, string assetPath, AdtPlacementKind kind)
    {
        var clientName = Path.GetFileNameWithoutExtension(clientPath); var assetName = Path.GetFileNameWithoutExtension(assetPath);
        if (!clientName.Equals(assetName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Extracted geometry '{Path.GetFileName(assetPath)}' does not match client path '{clientPath}'.");
        var assetExtension = Path.GetExtension(assetPath); var compatible = kind == AdtPlacementKind.Wmo ? assetExtension.Equals(".wmo", StringComparison.OrdinalIgnoreCase) : assetExtension.Equals(".m2", StringComparison.OrdinalIgnoreCase) || assetExtension.Equals(".mdx", StringComparison.OrdinalIgnoreCase);
        if (!compatible) throw new InvalidDataException("The selected extracted geometry has the wrong placement asset type.");
    }

    private static float EffectiveScale(ushort raw) => raw == 0 ? 1f : raw / 1024f;
    private static bool Same(Vector3 left, Vector3 right) => BitConverter.SingleToInt32Bits(left.X) == BitConverter.SingleToInt32Bits(right.X) && BitConverter.SingleToInt32Bits(left.Y) == BitConverter.SingleToInt32Bits(right.Y) && BitConverter.SingleToInt32Bits(left.Z) == BitConverter.SingleToInt32Bits(right.Z);
    private static void RequireFinite(Vector3 value, string label) { if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException($"{label} must contain three finite values."); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite) { var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"); try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
    private static void DeleteOwnedDirectory(string path) { if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, PendingFileName))) return; Directory.Delete(path, true); }
}
