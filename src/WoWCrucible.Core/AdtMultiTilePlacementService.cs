using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record AdtMultiTileSource(int TileX, int TileY, string Path, string Sha256);
public sealed record AdtMultiTileOutput(int TileX, int TileY, string Path, string Sha256, int ReferencedCells);

public sealed record AdtMultiTilePlacementPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    AdtPlacementLifecycleOperation Operation,
    AdtPlacementKind Kind,
    uint UniqueId,
    string MapPrefix,
    string SourceDirectory,
    string SourceFingerprint,
    IReadOnlyList<AdtMultiTileSource> SourceTiles,
    IReadOnlyList<AdtPlacementLifecyclePlan> Segments);

public sealed record AdtMultiTilePlacementReceipt(
    int FormatVersion,
    DateTimeOffset AppliedUtc,
    string PlanSha256,
    string OutputRoot,
    string ManifestPath,
    IReadOnlyList<AdtMultiTileOutput> Outputs,
    AdtMultiTilePlacementPlan Plan);

public sealed record AdtMultiTilePlacementResult(
    string OutputRoot,
    string PayloadRoot,
    string PlanPath,
    string ManifestPath,
    string ReceiptPath,
    AdtPlacementLifecycleOperation Operation,
    AdtPlacementKind Kind,
    uint UniqueId,
    IReadOnlyList<AdtMultiTileOutput> Outputs);

/// <summary>
/// Coordinates WoW's duplicated ADT placement records across every tile touched
/// by one object. Source tiles are immutable; publication is a new, tiny,
/// path-correct Payload tree plus a patch manifest and hash-bound receipt.
/// </summary>
public static class AdtMultiTilePlacementService
{
    internal sealed record Workspace(string Prefix, string Directory, IReadOnlyList<AdtMultiTileSource> Tiles);
    private const int CurrentFormatVersion = 1;
    private const int MaximumTiles = 4_096;
    private const double TileSize = 1600.0 / 3.0;
    private const string PlanFileName = "adt-placement-multi-plan.json";
    private const string ManifestFileName = "adt-placement-patch.crucible-patch.json";
    private const string ReceiptFileName = "adt-placement-multi-receipt.json";
    private const string PendingFileName = ".crucible-owned-pending";
    private static readonly Regex TileName = new(@"^(.+)_([0-9]+)_([0-9]+)(?:-[^\/.]+)?\.adt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AdtMultiTilePlacementPlan PlanAdd(string primaryAdtPath, AdtPlacementKind kind, string clientPath, string assetPath,
        Vector3 position, Vector3 orientation, ushort? scaleRaw = null, uint? uniqueId = null, ushort flags = 0, ushort doodadSet = 0, ushort nameSet = 0)
    {
        var workspace = DiscoverWorkspace(primaryAdtPath);
        return BuildAdd(workspace, kind, clientPath, assetPath, position, orientation, scaleRaw, uniqueId, flags, doodadSet, nameSet);
    }

    public static AdtMultiTilePlacementPlan PlanDelete(string selectedAdtPath, AdtPlacementKind kind, int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var workspace = DiscoverWorkspace(selectedAdtPath); selectedAdtPath = Path.GetFullPath(selectedAdtPath);
        var selectedTile = workspace.Tiles.SingleOrDefault(tile => tile.Path.Equals(selectedAdtPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected ADT is not an effective tile in its discovered map workspace.");
        return BuildDelete(workspace, selectedTile, kind, index);
    }

    public static void SavePlan(AdtMultiTilePlacementPlan plan, string path, bool overwrite = false)
    {
        Validate(plan); path = Path.GetFullPath(path);
        if (File.Exists(path) && !overwrite) throw new IOException($"Multi-tile placement plan already exists: {path}");
        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtMultiTilePlacementPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The multi-tile placement plan does not exist.", path);
        var plan = JsonSerializer.Deserialize<AdtMultiTilePlacementPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The multi-tile placement plan is empty.");
        Validate(plan); return plan;
    }

    public static AdtMultiTilePlacementResult Apply(AdtMultiTilePlacementPlan plan, string outputRoot)
    {
        Validate(plan); outputRoot = Path.GetFullPath(outputRoot);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot)) throw new IOException($"Multi-tile output must be a brand-new path: {outputRoot}");
        var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidOperationException("Output root has no parent directory."); Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporary); File.WriteAllText(Path.Combine(temporary, PendingFileName), "WoW Crucible owns this incomplete directory.");
        var moved = false;
        try
        {
            var payloadRoot = Path.Combine(temporary, "Payload"); var mapRoot = Path.Combine(payloadRoot, "World", "Maps", plan.MapPrefix); Directory.CreateDirectory(mapRoot);
            var occupancyFiles = plan.SourceTiles.Select(tile => tile.Path).ToArray(); var staged = new List<(AdtPlacementLifecycleResult Result, int TileX, int TileY)>();
            foreach (var segment in plan.Segments)
            {
                var tile = ParseTile(segment.InputPath); var output = Path.Combine(mapRoot, $"{plan.MapPrefix}_{tile.X}_{tile.Y}.adt");
                var result = AdtPlacementLifecycleService.ApplyCoordinated(segment, output, plan.SourceDirectory, plan.MapPrefix, occupancyFiles);
                staged.Add((result, tile.X, tile.Y));
            }
            AtomicWrite(Path.Combine(temporary, PlanFileName), JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), false);
            Directory.Move(temporary, outputRoot); moved = true;

            var finalPayload = Path.Combine(outputRoot, "Payload");
            var outputs = staged.Select(value =>
            {
                var path = Path.Combine(finalPayload, "World", "Maps", plan.MapPrefix, $"{plan.MapPrefix}_{value.TileX}_{value.TileY}.adt");
                return new AdtMultiTileOutput(value.TileX, value.TileY, path, Sha256(path), value.Result.ReferencedCells);
            }).OrderBy(tile => tile.TileY).ThenBy(tile => tile.TileX).ToArray();
            var manifestPath = Path.Combine(outputRoot, ManifestFileName);
            PatchManifestService.Save(manifestPath, $"{plan.MapPrefix} {plan.Operation} placement {plan.UniqueId}", "patch-Crucible-Map.MPQ",
                outputs.Select(tile => new PatchEntry(tile.Path, $@"World\Maps\{plan.MapPrefix}\{Path.GetFileName(tile.Path)}")),
                policy: new(ExpectedEntryCount: outputs.Length, AllowedGlobs: [$@"World\Maps\{plan.MapPrefix}\*.adt"]));
            var planPath = Path.Combine(outputRoot, PlanFileName); var receiptPath = Path.Combine(outputRoot, ReceiptFileName);
            var receipt = new AdtMultiTilePlacementReceipt(CurrentFormatVersion, DateTimeOffset.UtcNow, Sha256(planPath), outputRoot, manifestPath, outputs, plan);
            AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), false);
            File.Delete(Path.Combine(outputRoot, PendingFileName));
            return new(outputRoot, finalPayload, planPath, manifestPath, receiptPath, plan.Operation, plan.Kind, plan.UniqueId, outputs);
        }
        catch
        {
            DeleteOwnedDirectory(moved ? outputRoot : temporary); throw;
        }
    }

    private static AdtMultiTilePlacementPlan BuildAdd(Workspace workspace, AdtPlacementKind kind, string clientPath, string assetPath,
        Vector3 position, Vector3 orientation, ushort? scaleRaw, uint? uniqueId, ushort flags, ushort doodadSet, ushort nameSet)
    {
        if (workspace.Tiles.Count == 0) throw new InvalidDataException("The map workspace has no effective ADT tiles.");
        var rawScale = scaleRaw ?? 1024; Vector3 minimum; Vector3 maximum;
        if (kind == AdtPlacementKind.M2)
        {
            var evidence = M2PlacementBoundsService.InspectModel(assetPath); (minimum, maximum) = M2PlacementBoundsService.Calculate(evidence, position, orientation, rawScale / 1024f);
        }
        else
        {
            var evidence = WmoPlacementBoundsService.InspectRoot(assetPath); var bounds = WmoPlacementBoundsService.Calculate(evidence, position, orientation, rawScale == 0 ? 1f : rawScale / 1024f); minimum = bounds.Minimum; maximum = bounds.Maximum;
        }
        var occupied = workspace.Tiles.SelectMany(tile =>
        {
            var inspection = MapAssetInspectionService.Inspect(tile.Path); return inspection.M2Placements.Select(value => value.UniqueId).Concat(inspection.WmoPlacements.Select(value => value.UniqueId));
        }).ToHashSet();
        var uid = uniqueId ?? (occupied.Count == 0 ? 1u : occupied.Max() == uint.MaxValue ? throw new InvalidOperationException("The map already uses uint.MaxValue; choose a reviewed unused UID.") : occupied.Max() + 1);
        if (uid == 0 || occupied.Contains(uid)) throw new InvalidOperationException($"Placement UID {uid:N0} is zero or already occupied in the effective {workspace.Prefix} map workspace.");
        var coordinates = TouchedTiles(minimum, maximum); var byCoordinate = workspace.Tiles.ToDictionary(tile => (tile.TileX, tile.TileY));
        var missing = coordinates.Where(coordinate => !byCoordinate.ContainsKey(coordinate)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"Placement bounds require missing ADT tile(s): {string.Join(", ", missing.Select(value => $"{workspace.Prefix}_{value.X}_{value.Y}.adt"))}. Crucible will not publish a partial object.");
        var occupancyFiles = workspace.Tiles.Select(tile => tile.Path).ToArray();
        var segments = coordinates.Select(coordinate => AdtPlacementLifecycleService.PlanCoordinatedAdd(byCoordinate[coordinate].Path, kind, clientPath, assetPath,
            position, orientation, rawScale, uid, flags, doodadSet, nameSet, workspace.Directory, workspace.Prefix, occupancyFiles)).ToArray();
        return new(CurrentFormatVersion, DateTimeOffset.UtcNow, AdtPlacementLifecycleOperation.Add, kind, uid, workspace.Prefix, workspace.Directory,
            Fingerprint(workspace.Tiles), workspace.Tiles, segments);
    }

    private static AdtMultiTilePlacementPlan BuildDelete(Workspace workspace, AdtMultiTileSource selectedTile, AdtPlacementKind kind, int index)
    {
        var selected = MapAssetInspectionService.Inspect(selectedTile.Path); uint uid;
        MapM2Placement? selectedM2 = null; MapWmoPlacement? selectedWmo = null;
        if (kind == AdtPlacementKind.M2) { selectedM2 = selected.M2Placements.ElementAtOrDefault(index) ?? throw new ArgumentOutOfRangeException(nameof(index)); uid = selectedM2.UniqueId; }
        else { selectedWmo = selected.WmoPlacements.ElementAtOrDefault(index) ?? throw new ArgumentOutOfRangeException(nameof(index)); uid = selectedWmo.UniqueId; }
        var segments = new List<AdtPlacementLifecyclePlan>();
        foreach (var tile in workspace.Tiles.OrderBy(value => value.TileY).ThenBy(value => value.TileX))
        {
            var inspection = MapAssetInspectionService.Inspect(tile.Path);
            var oppositeMatches = kind == AdtPlacementKind.M2 ? inspection.WmoPlacements.Count(value => value.UniqueId == uid) : inspection.M2Placements.Count(value => value.UniqueId == uid);
            if (oppositeMatches > 0) throw new InvalidDataException($"UID {uid:N0} is also used by {oppositeMatches:N0} {(kind == AdtPlacementKind.M2 ? "WMO" : "M2")} record(s) in tile {tile.TileX},{tile.TileY}; coordinated deletion is blocked because the map UID namespace is corrupt or ambiguous.");
            if (kind == AdtPlacementKind.M2)
            {
                var matches = inspection.M2Placements.Where(value => value.UniqueId == uid).ToArray(); if (matches.Length > 1) throw new InvalidDataException($"Tile {tile.TileX},{tile.TileY} contains duplicate M2 UID {uid:N0} records."); if (matches.Length == 0) continue;
                if (!SamePlacement(selectedM2!, matches[0])) throw new InvalidDataException($"M2 UID {uid:N0} differs semantically in tile {tile.TileX},{tile.TileY}; deletion is blocked for manual review.");
                segments.Add(AdtPlacementLifecycleService.PlanCoordinatedDelete(tile.Path, kind, matches[0].Index));
            }
            else
            {
                var matches = inspection.WmoPlacements.Where(value => value.UniqueId == uid).ToArray(); if (matches.Length > 1) throw new InvalidDataException($"Tile {tile.TileX},{tile.TileY} contains duplicate WMO UID {uid:N0} records."); if (matches.Length == 0) continue;
                if (!SamePlacement(selectedWmo!, matches[0])) throw new InvalidDataException($"WMO UID {uid:N0} differs semantically in tile {tile.TileX},{tile.TileY}; deletion is blocked for manual review.");
                segments.Add(AdtPlacementLifecycleService.PlanCoordinatedDelete(tile.Path, kind, matches[0].Index));
            }
        }
        if (segments.Count == 0) throw new InvalidDataException($"Placement UID {uid:N0} disappeared from the effective map workspace.");
        return new(CurrentFormatVersion, DateTimeOffset.UtcNow, AdtPlacementLifecycleOperation.Delete, kind, uid, workspace.Prefix, workspace.Directory,
            Fingerprint(workspace.Tiles), workspace.Tiles, segments);
    }

    private static void Validate(AdtMultiTilePlacementPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.FormatVersion != CurrentFormatVersion || !Enum.IsDefined(plan.Operation) || !Enum.IsDefined(plan.Kind) || plan.UniqueId == 0 || string.IsNullOrWhiteSpace(plan.MapPrefix) || plan.SourceTiles.Count is 0 or > MaximumTiles || plan.Segments.Count is 0 or > MaximumTiles)
            throw new InvalidDataException("Multi-tile placement plan has an unsupported format, operation, kind, UID, map, or tile count.");
        var sources = plan.SourceTiles.Select(tile => tile with { Path = Path.GetFullPath(tile.Path) }).OrderBy(tile => tile.TileY).ThenBy(tile => tile.TileX).ToArray();
        if (sources.Select(tile => (tile.TileX, tile.TileY)).Distinct().Count() != sources.Length || sources.Any(tile => tile.TileX is < 0 or > 63 || tile.TileY is < 0 or > 63 || !File.Exists(tile.Path) || !Sha256(tile.Path).Equals(tile.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A multi-tile source is missing, changed, duplicated, or outside the 64x64 map grid.");
        if (!Fingerprint(sources).Equals(plan.SourceFingerprint, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The effective map workspace fingerprint no longer matches the reviewed plan.");
        if (!Path.GetFullPath(plan.SourceDirectory).Equals(plan.SourceDirectory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Multi-tile source directory must be absolute.");
        var occupancyFiles = sources.Select(tile => tile.Path).ToArray();
        foreach (var segment in plan.Segments)
        {
            if (!segment.CoordinatedMultiTile || segment.Operation != plan.Operation || segment.Kind != plan.Kind || segment.UniqueId != plan.UniqueId || !sources.Any(tile => tile.Path.Equals(Path.GetFullPath(segment.InputPath), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A multi-tile segment has mismatched transaction identity or source.");
            AdtPlacementLifecycleService.ValidateCoordinated(segment, plan.SourceDirectory, plan.MapPrefix, occupancyFiles);
        }
        var workspace = new Workspace(plan.MapPrefix, plan.SourceDirectory, sources); AdtMultiTilePlacementPlan rebuilt;
        if (plan.Operation == AdtPlacementLifecycleOperation.Add)
        {
            var segment = plan.Segments[0]; rebuilt = BuildAdd(workspace, plan.Kind, segment.ClientPath!, segment.AssetPath!, segment.Position.ToVector3(), segment.Orientation.ToVector3(), segment.ScaleRaw, plan.UniqueId, segment.Flags, segment.DoodadSet, segment.NameSet);
        }
        else
        {
            var first = plan.Segments[0]; var tile = sources.Single(value => value.Path.Equals(Path.GetFullPath(first.InputPath), StringComparison.OrdinalIgnoreCase)); rebuilt = BuildDelete(workspace, tile, plan.Kind, first.Index);
        }
        if (!Equivalent(plan, rebuilt)) throw new InvalidDataException("Multi-tile placement plan no longer matches the exact map, geometry, UID occurrences, or tile-local references.");
    }

    internal static Workspace DiscoverWorkspace(string inputPath)
    {
        inputPath = Path.GetFullPath(inputPath); if (!File.Exists(inputPath)) throw new FileNotFoundException("The selected ADT does not exist.", inputPath);
        var transformed = AdtMultiTilePlacementTransformService.TryFindLineage(inputPath);
        if (transformed is not null) return EffectiveWorkspace(inputPath, transformed.MapPrefix, transformed.SourceDirectory, transformed.SourceTiles, transformed.Outputs, "transform");
        var inherited = FindReceipt(inputPath);
        if (inherited is not null)
        {
            return EffectiveWorkspace(inputPath, inherited.Plan.MapPrefix, inherited.Plan.SourceDirectory, inherited.Plan.SourceTiles, inherited.Outputs, "placement");
        }
        var parsed = ParseTile(inputPath); var directory = Path.GetDirectoryName(inputPath)!;
        var candidates = Directory.EnumerateFiles(directory, parsed.Prefix + "_*.adt", SearchOption.TopDirectoryOnly).Select(path => (Path: Path.GetFullPath(path), Match: TileName.Match(Path.GetFileName(path)))).Where(value => value.Match.Success && value.Match.Groups[1].Value.Equals(parsed.Prefix, StringComparison.OrdinalIgnoreCase)).Select(value => new { value.Path, X = int.Parse(value.Match.Groups[2].Value), Y = int.Parse(value.Match.Groups[3].Value) }).ToArray();
        var tiles = new List<AdtMultiTileSource>();
        foreach (var group in candidates.GroupBy(value => (value.X, value.Y)).OrderBy(group => group.Key.Y).ThenBy(group => group.Key.X))
        {
            var exactName = $"{parsed.Prefix}_{group.Key.X}_{group.Key.Y}.adt"; var exact = group.Where(value => Path.GetFileName(value.Path).Equals(exactName, StringComparison.OrdinalIgnoreCase)).ToArray();
            var chosen = group.Any(value => value.Path.Equals(inputPath, StringComparison.OrdinalIgnoreCase)) && group.Key == (parsed.X, parsed.Y) ? group.Single(value => value.Path.Equals(inputPath, StringComparison.OrdinalIgnoreCase)) : exact.Length == 1 ? exact[0] : group.Count() == 1 ? group.Single() : throw new InvalidDataException($"Multiple candidate ADTs represent tile {group.Key.X},{group.Key.Y}; select a prior coordinated output or remove ambiguous staged copies.");
            tiles.Add(new(group.Key.X, group.Key.Y, chosen.Path, Sha256(chosen.Path)));
        }
        if (tiles.Count is 0 or > MaximumTiles) throw new InvalidDataException($"Discovered {tiles.Count:N0} effective ADT tiles; the safe bound is 1 through {MaximumTiles:N0}.");
        return new(parsed.Prefix, directory, tiles);
    }

    private static Workspace EffectiveWorkspace(string inputPath, string mapPrefix, string sourceDirectory,
        IReadOnlyList<AdtMultiTileSource> sourceTiles, IReadOnlyList<AdtMultiTileOutput> outputs, string lineageKind)
    {
        var effective = sourceTiles.ToDictionary(tile => (tile.TileX, tile.TileY));
        foreach (var output in outputs)
        {
            if (!File.Exists(output.Path) || !Sha256(output.Path).Equals(output.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Inherited multi-tile {lineageKind} output changed or disappeared: {output.Path}");
            effective[(output.TileX, output.TileY)] = new(output.TileX, output.TileY, Path.GetFullPath(output.Path), output.Sha256);
        }
        var inheritedTiles = effective.Values.OrderBy(tile => tile.TileY).ThenBy(tile => tile.TileX).ToArray();
        if (!inheritedTiles.Any(tile => tile.Path.Equals(inputPath, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"Selected ADT is not an effective output in its inherited multi-tile {lineageKind} receipt.");
        return new(mapPrefix, sourceDirectory, inheritedTiles);
    }

    private static AdtMultiTilePlacementReceipt? FindReceipt(string inputPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(inputPath)!);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, ReceiptFileName); if (!File.Exists(path)) continue;
            var receipt = JsonSerializer.Deserialize<AdtMultiTilePlacementReceipt>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException($"Multi-tile receipt is empty: {path}");
            if (receipt.FormatVersion != CurrentFormatVersion || !Path.GetFullPath(receipt.OutputRoot).Equals(directory.FullName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Multi-tile receipt has an unsupported format or root: {path}");
            var planPath = Path.Combine(directory.FullName, PlanFileName);
            if (!File.Exists(planPath) || !Sha256(planPath).Equals(receipt.PlanSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Multi-tile receipt plan is missing or no longer matches its recorded SHA-256: {path}");
            var persistedPlan = JsonSerializer.Deserialize<AdtMultiTilePlacementPlan>(File.ReadAllBytes(planPath), JsonOptions)
                ?? throw new InvalidDataException($"Multi-tile receipt plan is empty: {planPath}");
            if (!Equivalent(receipt.Plan, persistedPlan))
                throw new InvalidDataException($"Multi-tile receipt and persisted plan disagree: {path}");
            if (receipt.Outputs.Count is 0 or > MaximumTiles || receipt.Outputs.Count != receipt.Plan.Segments.Count || receipt.Outputs.Select(output => (output.TileX, output.TileY)).Distinct().Count() != receipt.Outputs.Count)
                throw new InvalidDataException($"Multi-tile receipt has an invalid or duplicated output set: {path}");
            var expectedManifest = Path.Combine(directory.FullName, ManifestFileName);
            if (!Path.GetFullPath(receipt.ManifestPath).Equals(expectedManifest, StringComparison.OrdinalIgnoreCase) || !File.Exists(expectedManifest) || !PatchManifestService.Validate(PatchManifestService.Load(expectedManifest)).Passed)
                throw new InvalidDataException($"Multi-tile receipt manifest is missing, invalid, or outside its output root: {path}");
            foreach (var output in receipt.Outputs)
            {
                var expectedOutput = Path.Combine(directory.FullName, "Payload", "World", "Maps", receipt.Plan.MapPrefix, $"{receipt.Plan.MapPrefix}_{output.TileX}_{output.TileY}.adt");
                if (!Path.GetFullPath(output.Path).Equals(expectedOutput, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Multi-tile receipt output is not in its exact path-correct payload location: {output.Path}");
            }
            if (!receipt.Outputs.Any(output => output.Path.Equals(Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase))) continue;
            return receipt;
        }
        return null;
    }

    internal static IReadOnlyList<(int X, int Y)> TouchedTiles(Vector3 minimum, Vector3 maximum)
    {
        if (!Finite(minimum) || !Finite(maximum) || minimum.X > maximum.X || minimum.Z > maximum.Z) throw new InvalidDataException("Placement geometry produced invalid horizontal bounds.");
        if (minimum.X < 0 || maximum.X > 64 * TileSize || minimum.Z < 0 || maximum.Z > 64 * TileSize)
            throw new InvalidOperationException("Placement bounds cross outside the 64x64 ADT world grid; Crucible will not clamp or publish partial geometry.");
        var minX = Math.Clamp((int)Math.Floor(minimum.X / TileSize), 0, 63); var maxX = Math.Clamp((int)Math.Floor(maximum.X / TileSize), 0, 63);
        var minY = Math.Clamp((int)Math.Floor(minimum.Z / TileSize), 0, 63); var maxY = Math.Clamp((int)Math.Floor(maximum.Z / TileSize), 0, 63);
        var result = new List<(int X, int Y)>(); for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++) result.Add((x, y)); return result;
    }

    internal static bool SamePlacement(MapM2Placement left, MapM2Placement right) => left.UniqueId == right.UniqueId && SamePath(left.ClientPath, right.ClientPath) && left.Position == right.Position && left.Orientation == right.Orientation && left.ScaleRaw == right.ScaleRaw && left.Flags == right.Flags;
    internal static bool SamePlacement(MapWmoPlacement left, MapWmoPlacement right) => left.UniqueId == right.UniqueId && SamePath(left.ClientPath, right.ClientPath) && left.Position == right.Position && left.Orientation == right.Orientation && left.MinimumExtent == right.MinimumExtent && left.MaximumExtent == right.MaximumExtent && left.Flags == right.Flags && left.DoodadSet == right.DoodadSet && left.NameSet == right.NameSet && left.ScaleRaw == right.ScaleRaw;
    private static bool SamePath(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static (string Prefix, int X, int Y) ParseTile(string path)
    {
        var match = TileName.Match(Path.GetFileName(path)); if (!match.Success) throw new InvalidDataException($"ADT filename must be <map>_<x>_<y>.adt (an edit suffix is accepted): {path}");
        return (match.Groups[1].Value, int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }

    internal static string Fingerprint(IEnumerable<AdtMultiTileSource> source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var tile in source.OrderBy(value => value.TileY).ThenBy(value => value.TileX))
        {
            hash.AppendData(BitConverter.GetBytes(tile.TileX)); hash.AppendData(BitConverter.GetBytes(tile.TileY)); Append(hash, Path.GetFullPath(tile.Path).ToUpperInvariant()); Append(hash, tile.Sha256.ToUpperInvariant());
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value) { var bytes = Encoding.UTF8.GetBytes(value); hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
    private static bool Equivalent(AdtMultiTilePlacementPlan expected, AdtMultiTilePlacementPlan actual)
    {
        static AdtMultiTilePlacementPlan Canonical(AdtMultiTilePlacementPlan value) => value with
        {
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Segments = value.Segments.Select(segment => segment with { CreatedUtc = DateTimeOffset.UnixEpoch }).ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(Canonical(expected), JsonOptions).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(Canonical(actual), JsonOptions));
    }
    internal static string Sha256(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite) { var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"); try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
    private static void DeleteOwnedDirectory(string path) { if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, PendingFileName))) return; Directory.Delete(path, true); }
}
