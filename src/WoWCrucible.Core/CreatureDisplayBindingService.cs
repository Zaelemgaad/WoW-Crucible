using System.Security.Cryptography;

namespace WoWCrucible.Core;

public sealed record CreatureDisplayBindingRequest(string Role, uint DisplayId);

public sealed record CreatureDisplayBoundAsset(
    string Kind,
    string ClientPath,
    string SourcePath,
    string Provenance,
    string Sha256);

public sealed record CreatureDisplayBindingPlan(
    string Role,
    uint DisplayId,
    uint ModelId,
    string ModelClientPath,
    float DisplayScale,
    float ModelScale,
    IReadOnlyList<string> TextureVariations,
    string? EffectiveProvenance,
    IReadOnlyList<CreatureDisplayBoundAsset> Assets,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings)
{
    public bool Ready => Blockers.Count == 0;
}

/// <summary>
/// Binds one or more existing CreatureDisplayInfo rows to their complete model
/// dependency closure. The DBC catalog is loaded once, and the processed-library
/// layout is opened once, so a male/female race pair does not repeat broad scans.
/// </summary>
public static class CreatureDisplayBindingService
{
    public static IReadOnlyList<CreatureDisplayBindingPlan> CreatePlans(
        string dbcRoot,
        string schemaPath,
        IReadOnlyList<CreatureDisplayBindingRequest> requests,
        string? processedLibraryRoot = null,
        string? requestedProvenance = null,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) return [];
        dbcRoot = Path.GetFullPath(dbcRoot); schemaPath = Path.GetFullPath(schemaPath);
        requestedProvenance = string.IsNullOrWhiteSpace(requestedProvenance) ? null : requestedProvenance.Trim();
        processedLibraryRoot = string.IsNullOrWhiteSpace(processedLibraryRoot) ? null : Path.GetFullPath(processedLibraryRoot);

        var schemaCatalog = DbcSchemaCatalog.Load(schemaPath); ValidateIdentity("CreatureDisplayInfo"); ValidateIdentity("CreatureModelData");
        var catalog = new CreatureDisplayPreviewService().LoadCatalog(dbcRoot, schemaPath, cancellationToken);
        var byId = catalog.Entries.GroupBy(entry => entry.DisplayId).ToDictionary(group => group.Key, group => group.First());
        var index = processedLibraryRoot is null ? null : ClientAssetDependencyService.OpenLibraryLayout(processedLibraryRoot);
        var hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<CreatureDisplayBindingPlan>(requests.Count);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockers = new List<string>(); var warnings = new List<string>(); var assets = new Dictionary<string, CreatureDisplayBoundAsset>(StringComparer.OrdinalIgnoreCase);
            if (request.DisplayId == 0 || !byId.TryGetValue(request.DisplayId, out var display))
            {
                blockers.Add($"CreatureDisplayInfo.dbc has no positive display ID {request.DisplayId:N0} for {request.Role}.");
                output.Add(new(request.Role, request.DisplayId, 0, string.Empty, 1f, 1f, [], null, [], blockers, warnings));
                continue;
            }
            if (!display.Usable) blockers.Add(string.IsNullOrWhiteSpace(display.Finding) ? $"Display {display.DisplayId:N0} is not usable." : display.Finding);
            if (!display.ModelClientPath.StartsWith("Character\\", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"{request.Role} display {display.DisplayId:N0} uses '{display.ModelClientPath}', outside the normal Character\\ race-model tree. It may intentionally work, but character customization, armor geosets, and animations require in-client verification.");

            string? effectiveProvenance = null;
            if (index is null)
            {
                warnings.Add($"No processed asset library was supplied for {request.Role}; the bundle will bind display {display.DisplayId:N0}, but assumes its model and textures already exist in the target client.");
            }
            else if (display.Usable)
            {
                var model = Select(display.ModelClientPath, "creature-model", requestedProvenance);
                if (model is not null)
                {
                    effectiveProvenance = model.Provenance;
                    try
                    {
                        var graph = ClientAssetDependencyService.Analyze(index, model, cancellationToken);
                        foreach (var node in graph.Blocking) blockers.Add($"{request.Role} {node.ClientPath}: {node.Message}");
                        foreach (var entry in graph.PatchEntries) AddAsset("model-dependency", entry.ArchivePath, entry.SourcePath, model.Provenance);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        blockers.Add($"Could not inspect {request.Role} model closure for {display.ModelClientPath}: {exception.Message}");
                    }
                }
                foreach (var texture in display.TextureVariations.Where(path => !string.IsNullOrWhiteSpace(path)))
                {
                    var selected = Select(texture, "creature-texture", effectiveProvenance ?? requestedProvenance);
                    if (selected is not null) AddAsset("creature-texture", texture, selected.SourcePath, selected.Provenance);
                }
            }

            output.Add(new(request.Role, display.DisplayId, display.ModelId, display.ModelClientPath, display.DisplayScale, display.ModelScale,
                display.TextureVariations, effectiveProvenance, assets.Values.OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).ToArray(),
                blockers.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                warnings.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()));

            ClientAssetLocation? Select(string clientPath, string kind, string? selectedProvenance)
            {
                var candidates = ClientAssetDependencyService.FindCandidates(index!, clientPath, cancellationToken);
                var eligible = selectedProvenance is null ? candidates.ToArray() : candidates.Where(candidate => candidate.Provenance.Equals(selectedProvenance, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (eligible.Length == 0)
                {
                    blockers.Add(candidates.Count == 0
                        ? $"{request.Role} required {kind} is absent from the processed library: {clientPath}"
                        : $"{request.Role} required {kind} exists only outside bound provenance '{selectedProvenance}': {clientPath}");
                    return null;
                }
                if (eligible.Length == 1) return eligible[0];
                var groups = eligible.GroupBy(candidate => Hash(candidate.SourcePath), StringComparer.OrdinalIgnoreCase).ToArray();
                if (groups.Length == 1)
                {
                    var chosen = eligible.OrderBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase).First();
                    warnings.Add($"{clientPath} has {eligible.Length:N0} byte-identical candidates; selected {chosen.SourcePath} deterministically."); return chosen;
                }
                blockers.Add($"{request.Role} required {kind} has {eligible.Length:N0} different-byte candidates{(selectedProvenance is null ? string.Empty : $" within provenance '{selectedProvenance}'")}: {clientPath}");
                return null;
            }

            void AddAsset(string kind, string clientPath, string sourcePath, string provenance)
            {
                clientPath = PatchInputMapper.NormalizeArchivePath(clientPath); sourcePath = Path.GetFullPath(sourcePath); var hash = Hash(sourcePath);
                if (assets.TryGetValue(clientPath, out var existing) && !existing.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    blockers.Add($"{request.Role} client path {clientPath} resolves to different bytes from '{existing.SourcePath}' and '{sourcePath}'."); return;
                }
                assets[clientPath] = new(kind, clientPath, sourcePath, provenance, hash);
            }
        }
        return output;

        void ValidateIdentity(string table)
        {
            var path = Directory.EnumerateFiles(dbcRoot, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(candidate => Path.GetFileName(candidate).Equals(table + ".dbc", StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"Required {table}.dbc is unavailable.", Path.Combine(dbcRoot, table + ".dbc"));
            var file = WdbcFile.Load(path); var resolution = schemaCatalog.ResolveColumns(table, file.FieldCount);
            if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch || resolution.Columns.Count != file.FieldCount)
                throw new InvalidDataException($"{table}.dbc requires an exact named schema before a playable race can bind it.");
            _ = DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy);
        }

        string Hash(string path)
        {
            path = Path.GetFullPath(path); if (hashCache.TryGetValue(path, out var existing)) return existing;
            using var stream = File.OpenRead(path); var value = Convert.ToHexString(SHA256.HashData(stream)); hashCache[path] = value; return value;
        }
    }

    public static void VerifyAssets(IEnumerable<CreatureDisplayBindingPlan> plans)
    {
        foreach (var asset in plans.SelectMany(plan => plan.Assets).DistinctBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(asset.SourcePath); var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Bound appearance asset changed after review: {asset.SourcePath}");
        }
    }
}
