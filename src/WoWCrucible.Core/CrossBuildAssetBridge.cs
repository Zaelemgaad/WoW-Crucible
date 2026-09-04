using System.Globalization;
using System.Text;

namespace WoWCrucible.Core;

internal enum CrossBuildAssetKind
{
    Texture,
    Model
}

internal sealed record CrossBuildAssetFieldRule(
    string HostProfileId,
    string DonorProfileId,
    string CanonicalTable,
    string HostField,
    string DonorField,
    CrossBuildAssetKind Kind,
    string Extension);

internal sealed record CrossBuildAssetRequest(CrossBuildAssetFieldRule Rule, uint FileDataId);

internal static class CrossBuildAssetBridgeCatalog
{
    private const string Mop = "mop-18414";
    private const string Legion = "legion-26972";

    private static readonly IReadOnlyList<CrossBuildAssetFieldRule> Rules =
    [
        new(Mop, Legion, "FOOTPRINTTEXTURES", "FootstepFilename", "FileDataID", CrossBuildAssetKind.Texture, ".blp"),
        new(Mop, Legion, "GAMEOBJECTARTKIT", "TextureVariation[0]", "TextureVariationFileID[0]", CrossBuildAssetKind.Texture, ".blp"),
        new(Mop, Legion, "GAMEOBJECTARTKIT", "TextureVariation[1]", "TextureVariationFileID[1]", CrossBuildAssetKind.Texture, ".blp"),
        new(Mop, Legion, "GAMEOBJECTARTKIT", "TextureVariation[2]", "TextureVariationFileID[2]", CrossBuildAssetKind.Texture, ".blp"),
        new(Mop, Legion, "GAMEOBJECTARTKIT", "AttachModel[0]", "AttachModelFileID", CrossBuildAssetKind.Model, ".m2"),
        new(Mop, Legion, "ITEMVISUALS", "Slot[0]", "ModelFileID[0]", CrossBuildAssetKind.Model, ".m2"),
        new(Mop, Legion, "ITEMVISUALS", "Slot[1]", "ModelFileID[1]", CrossBuildAssetKind.Model, ".m2"),
        new(Mop, Legion, "ITEMVISUALS", "Slot[2]", "ModelFileID[2]", CrossBuildAssetKind.Model, ".m2"),
        new(Mop, Legion, "ITEMVISUALS", "Slot[3]", "ModelFileID[3]", CrossBuildAssetKind.Model, ".m2"),
        new(Mop, Legion, "ITEMVISUALS", "Slot[4]", "ModelFileID[4]", CrossBuildAssetKind.Model, ".m2"),
        new(Mop, Legion, "LOADINGSCREENS", "FileName", "NarrowScreenFileDataID", CrossBuildAssetKind.Texture, ".blp"),
        new(Mop, Legion, "VEHICLEUIINDICATOR", "BackgroundTexture", "BackgroundTextureFileID", CrossBuildAssetKind.Texture, ".blp")
    ];

    public static CrossBuildAssetFieldRule? Find(
        string hostProfileId,
        string donorProfileId,
        string canonicalTable,
        string hostField) => Rules.SingleOrDefault(rule =>
            rule.HostProfileId.Equals(hostProfileId, StringComparison.OrdinalIgnoreCase) &&
            rule.DonorProfileId.Equals(donorProfileId, StringComparison.OrdinalIgnoreCase) &&
            rule.CanonicalTable.Equals(canonicalTable, StringComparison.OrdinalIgnoreCase) &&
            rule.HostField.Equals(hostField, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<CrossBuildAssetFieldRule> ForTable(
        string hostProfileId,
        string donorProfileId,
        string canonicalTable) => Rules.Where(rule =>
            rule.HostProfileId.Equals(hostProfileId, StringComparison.OrdinalIgnoreCase) &&
            rule.DonorProfileId.Equals(donorProfileId, StringComparison.OrdinalIgnoreCase) &&
            rule.CanonicalTable.Equals(canonicalTable, StringComparison.OrdinalIgnoreCase)).ToArray();
}

/// <summary>
/// Resolves modern FileDataID fields into paths that the MPQ host can consume.
/// Existing host paths are retained; missing BLP payloads are extracted from the
/// local donor CASC and published under a deterministic collision-free namespace.
/// Modern M2 payloads and their SKIN/texture closure are published only after the
/// complete graph passes native MoP projection and independent output checks.
/// </summary>
internal sealed class CrossBuildAssetBridge
{
    private sealed record Resolution(
        uint FileDataId,
        string? HostValue,
        IReadOnlyList<PatchEntry> Payloads,
        string? Error,
        bool ReusedHost);

    private sealed record ConvertedModel(
        uint FileDataId,
        string ClientPath,
        StaticM2DownportResult Result,
        IReadOnlyList<string> TexturePaths);

    private readonly IReadOnlyDictionary<uint, Resolution> _resolutions;
    private readonly HashSet<uint> _used = [];

    private CrossBuildAssetBridge(
        IReadOnlyDictionary<uint, Resolution> resolutions,
        IReadOnlyList<string> findings)
    {
        _resolutions = resolutions;
        Findings = findings;
    }

    public IReadOnlyList<string> Findings { get; }

    public IReadOnlyList<PatchEntry> PatchEntries => _used
        .Select(id => _resolutions[id])
        .SelectMany(value => value.Payloads)
        .GroupBy(value => value.ArchivePath, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.DistinctBy(value => Path.GetFullPath(value.SourcePath), StringComparer.OrdinalIgnoreCase).Single())
        .OrderBy(value => value.ArchivePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static CrossBuildAssetBridge Create(
        string hostClientDataRoot,
        string donorClientRoot,
        string donorFileDataListPath,
        IEnumerable<CrossBuildAssetRequest> sourceRequests,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        var requests = sourceRequests.Where(value => value.FileDataId != 0)
            .GroupBy(value => value.FileDataId)
            .Select(group => new { FileDataId = group.Key, Rules = group.Select(value => value.Rule).Distinct().ToArray() })
            .OrderBy(value => value.FileDataId)
            .ToArray();
        if (requests.Length == 0) return new(new Dictionary<uint, Resolution>(), ["No nonzero FileDataID value was requested by the selected donor rows."]);

        hostClientDataRoot = ResolveDataRoot(hostClientDataRoot);
        donorClientRoot = RequiredDirectory(donorClientRoot, "Donor CASC client root");
        donorFileDataListPath = RequiredFile(donorFileDataListPath, "Donor FileDataID listfile");
        stagingRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(stagingRoot);

        var snapshot = FileDataIdListfileService.Resolve(donorFileDataListPath, requests.Select(value => value.FileDataId), cancellationToken);
        var resolvedById = snapshot.ResolvedById;
        var hostPaths = IndexHostPaths(hostClientDataRoot, snapshot.Resolved.Select(value => value.ClientPath), stagingRoot, cancellationToken);
        var donorCandidates = requests
            .Where(value => resolvedById.TryGetValue(value.FileDataId, out var path) && !hostPaths.Contains(path))
            .Select(value => new FileDataIdPath(value.FileDataId, resolvedById[value.FileDataId]))
            .ToArray();

        var probes = donorCandidates.Length == 0
            ? new Dictionary<uint, CascPathProbe>()
            : new CascArchiveService().ProbePaths(donorClientRoot, donorCandidates, cancellationToken).ToDictionary(value => value.FileDataId);
        var extractable = probes.Values.Where(value => value.IsAvailableLocally).Select(value => new CascFileEntry(
            value.ArchivePath, value.Size, value.FileDataId, 0, 0, true, CascEntryNameType.FullPath, string.Empty, string.Empty)).ToArray();
        var extractedRoot = Path.Combine(stagingRoot, "donor-casc-assets");
        if (extractable.Length > 0)
            new CascArchiveService().Extract(donorClientRoot, extractedRoot, extractable, cancellationToken: cancellationToken, overwriteExisting: false);

        var modelErrors = new Dictionary<uint, string>();
        var modelSources = new Dictionary<uint, string>();
        var skinIdsByModel = new Dictionary<uint, uint>();
        var textureIdsByModel = new Dictionary<uint, IReadOnlyList<uint>>();
        foreach (var request in requests.Where(value => value.Rules.Any(rule => rule.Kind == CrossBuildAssetKind.Model)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolvedById.TryGetValue(request.FileDataId, out var clientPath) || hostPaths.Contains(clientPath)) continue;
            if (!probes.TryGetValue(request.FileDataId, out var probe) || !probe.IsAvailableLocally) continue;
            var sourcePath = ExtractedPath(extractedRoot, clientPath);
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length != probe.Size)
                throw new IOException($"Extracted CASC model failed size verification: {clientPath}");
            modelSources[request.FileDataId] = sourcePath;
            try
            {
                var skinIds = StaticM2DownportService.ReferencedSkinFileDataIds(sourcePath, cancellationToken);
                if (skinIds.Count != 1)
                {
                    modelErrors[request.FileDataId] = $"Modern model {clientPath} requires exactly one SFID skin reference for the verified MoP projection; found {skinIds.Count:N0}.";
                    continue;
                }
                skinIdsByModel[request.FileDataId] = skinIds[0];
                textureIdsByModel[request.FileDataId] = StaticM2DownportService.RequiredExternalTextureIds([sourcePath], cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                modelErrors[request.FileDataId] = $"Modern model {clientPath} could not expose its dependency IDs: {exception.Message}";
            }
        }

        var dependencyIds = skinIdsByModel.Values.Concat(textureIdsByModel.Values.SelectMany(value => value)).Distinct().Order().ToArray();
        var dependencySnapshot = FileDataIdListfileService.Resolve(donorFileDataListPath, dependencyIds, cancellationToken);
        var dependencyPaths = dependencySnapshot.ResolvedById;
        foreach (var pair in skinIdsByModel.ToArray())
        {
            if (dependencySnapshot.AmbiguousIds.TryGetValue(pair.Value, out var ambiguous))
                modelErrors[pair.Key] = $"Skin FileDataID {pair.Value:N0} resolves to multiple paths: {string.Join(", ", ambiguous)}";
            else if (!dependencyPaths.ContainsKey(pair.Value))
                modelErrors[pair.Key] = $"Skin FileDataID {pair.Value:N0} is absent from the selected listfile.";
            var missingTexture = textureIdsByModel.GetValueOrDefault(pair.Key, []).FirstOrDefault(id => !dependencyPaths.ContainsKey(id));
            if (missingTexture != 0)
                modelErrors[pair.Key] = dependencySnapshot.AmbiguousIds.TryGetValue(missingTexture, out var textureAmbiguity)
                    ? $"Texture FileDataID {missingTexture:N0} resolves to multiple paths: {string.Join(", ", textureAmbiguity)}"
                    : $"Texture FileDataID {missingTexture:N0} is absent from the selected listfile.";
        }

        var textureDependencyPaths = textureIdsByModel.Values.SelectMany(value => value).Distinct()
            .Where(dependencyPaths.ContainsKey).Select(id => dependencyPaths[id]).ToArray();
        var hostDependencyPaths = IndexHostPaths(hostClientDataRoot, textureDependencyPaths, Path.Combine(stagingRoot, "dependency-host-index"), cancellationToken);
        var dependenciesToExtract = skinIdsByModel.Values.Distinct()
            .Concat(textureIdsByModel.Values.SelectMany(value => value).Distinct().Where(id => dependencyPaths.TryGetValue(id, out var path) && !hostDependencyPaths.Contains(path)))
            .Distinct().Where(dependencyPaths.ContainsKey)
            .Select(id => new FileDataIdPath(id, dependencyPaths[id])).ToArray();
        var dependencyProbes = dependenciesToExtract.Length == 0
            ? new Dictionary<uint, CascPathProbe>()
            : new CascArchiveService().ProbePaths(donorClientRoot, dependenciesToExtract, cancellationToken).ToDictionary(value => value.FileDataId);
        var extractableDependencies = dependencyProbes.Values.Where(value => value.IsAvailableLocally).Select(value => new CascFileEntry(
            value.ArchivePath, value.Size, value.FileDataId, 0, 0, true, CascEntryNameType.FullPath, string.Empty, string.Empty)).ToArray();
        if (extractableDependencies.Length > 0)
            new CascArchiveService().Extract(donorClientRoot, extractedRoot, extractableDependencies, cancellationToken: cancellationToken, overwriteExisting: false);

        foreach (var pair in skinIdsByModel)
        {
            if (modelErrors.ContainsKey(pair.Key) || !dependencyPaths.TryGetValue(pair.Value, out var skinPath)) continue;
            if (!dependencyProbes.TryGetValue(pair.Value, out var skinProbe) || !skinProbe.IsAvailableLocally)
            {
                var native = skinProbe?.NativeError is { } code ? $" (CASC error {code})" : string.Empty;
                modelErrors[pair.Key] = $"Modern skin {skinPath} is not present in the local Legion CASC payload{native}.";
                continue;
            }
            var missingTexture = textureIdsByModel.GetValueOrDefault(pair.Key, []).FirstOrDefault(id =>
                dependencyPaths.TryGetValue(id, out var path) && !hostDependencyPaths.Contains(path) &&
                (!dependencyProbes.TryGetValue(id, out var textureProbe) || !textureProbe.IsAvailableLocally));
            if (missingTexture != 0)
            {
                var texturePath = dependencyPaths[missingTexture];
                var native = dependencyProbes.TryGetValue(missingTexture, out var textureProbe) && textureProbe.NativeError is { } code ? $" (CASC error {code})" : string.Empty;
                modelErrors[pair.Key] = $"Modern model texture {texturePath} is not present in the local Legion CASC payload{native}.";
            }
        }

        var convertedModels = new Dictionary<uint, ConvertedModel>();
        foreach (var request in requests.Where(value => value.Rules.Any(rule => rule.Kind == CrossBuildAssetKind.Model)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (modelErrors.ContainsKey(request.FileDataId) || !modelSources.TryGetValue(request.FileDataId, out var modelSource) ||
                !skinIdsByModel.TryGetValue(request.FileDataId, out var skinId) || !dependencyPaths.TryGetValue(skinId, out var skinClientPath)) continue;
            var clientPath = resolvedById[request.FileDataId];
            var skinSource = ExtractedPath(extractedRoot, skinClientPath);
            try
            {
                var plan = StaticM2DownportService.PlanForMopWithListfileSnapshot(modelSource, skinSource, dependencySnapshot, cancellationToken);
                if (!plan.Ready)
                {
                    modelErrors[request.FileDataId] = $"Modern model {clientPath} is outside the verified native MoP projection profile: {string.Join("; ", plan.Blockers)}";
                    continue;
                }
                var output = Path.Combine(stagingRoot, "mop-projected-models", request.FileDataId.ToString(CultureInfo.InvariantCulture));
                var result = StaticM2DownportService.ConvertPrepared(plan, output, dependencySnapshot, cancellationToken);
                var texturePaths = M2PreviewGeometryService.InspectTextureSlots(result.OutputModelPath)
                    .Select(slot => slot.EmbeddedPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => PatchInputMapper.NormalizeArchivePath(path!))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                convertedModels[request.FileDataId] = new(request.FileDataId, clientPath, result, texturePaths);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                modelErrors[request.FileDataId] = $"Modern model {clientPath} failed verified native MoP M2/SKIN projection: {exception.Message}";
            }
        }

        var embeddedTexturePaths = convertedModels.Values.SelectMany(value => value.TexturePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hostEmbeddedTexturePaths = IndexHostPaths(hostClientDataRoot, embeddedTexturePaths, Path.Combine(stagingRoot, "embedded-texture-host-index"), cancellationToken);
        var missingEmbeddedPaths = embeddedTexturePaths.Where(path => !hostEmbeddedTexturePaths.Contains(path)).ToArray();
        var embeddedRequests = missingEmbeddedPaths.Select((path, index) => new FileDataIdPath(checked((uint)index + 1), path)).ToArray();
        var embeddedProbes = embeddedRequests.Length == 0
            ? new Dictionary<string, CascPathProbe>(StringComparer.OrdinalIgnoreCase)
            : new CascArchiveService().ProbePaths(donorClientRoot, embeddedRequests, cancellationToken)
                .ToDictionary(value => value.ArchivePath, StringComparer.OrdinalIgnoreCase);
        var extractableEmbedded = embeddedProbes.Values.Where(value => value.IsAvailableLocally).Select(value => new CascFileEntry(
            value.ArchivePath, value.Size, value.FileDataId, 0, 0, true, CascEntryNameType.FullPath, string.Empty, string.Empty)).ToArray();
        if (extractableEmbedded.Length > 0)
            new CascArchiveService().Extract(donorClientRoot, extractedRoot, extractableEmbedded, cancellationToken: cancellationToken, overwriteExisting: false);

        foreach (var model in convertedModels.Values)
        {
            var missing = model.TexturePaths.FirstOrDefault(path => !hostEmbeddedTexturePaths.Contains(path) &&
                (!embeddedProbes.TryGetValue(path, out var probe) || !probe.IsAvailableLocally));
            if (missing is not null)
            {
                var native = embeddedProbes.TryGetValue(missing, out var probe) && probe.NativeError is { } code ? $" (CASC error {code})" : string.Empty;
                modelErrors[model.FileDataId] = $"Converted model {model.ClientPath} references texture {missing}, which is absent from the MoP host and local Legion CASC{native}.";
            }
        }

        var resolutions = new Dictionary<uint, Resolution>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.MissingIds.Contains(request.FileDataId))
            {
                resolutions[request.FileDataId] = new(request.FileDataId, null, [],
                    $"FileDataID {request.FileDataId.ToString(CultureInfo.InvariantCulture)} is absent from the selected listfile.", false);
                continue;
            }
            if (snapshot.AmbiguousIds.TryGetValue(request.FileDataId, out var ambiguous))
            {
                resolutions[request.FileDataId] = new(request.FileDataId, null, [],
                    $"FileDataID {request.FileDataId.ToString(CultureInfo.InvariantCulture)} resolves to multiple paths: {string.Join(", ", ambiguous)}", false);
                continue;
            }
            var clientPath = resolvedById[request.FileDataId];
            var incompatibleRule = request.Rules.FirstOrDefault(rule => !Path.GetExtension(clientPath).Equals(rule.Extension, StringComparison.OrdinalIgnoreCase));
            if (incompatibleRule is not null)
            {
                resolutions[request.FileDataId] = new(request.FileDataId, null, [],
                    $"FileDataID {request.FileDataId.ToString(CultureInfo.InvariantCulture)} resolved to {clientPath}, not the required {incompatibleRule.Extension} asset type.", false);
                continue;
            }
            if (hostPaths.Contains(clientPath))
            {
                resolutions[request.FileDataId] = new(request.FileDataId, clientPath, [], null, true);
                continue;
            }
            if (request.Rules.Any(rule => rule.Kind == CrossBuildAssetKind.Model))
            {
                if (!convertedModels.TryGetValue(request.FileDataId, out var model))
                {
                    resolutions[request.FileDataId] = new(request.FileDataId, null, [],
                        modelErrors.GetValueOrDefault(request.FileDataId) ?? $"Modern model {clientPath} was not available for verified native MoP projection.", false);
                    continue;
                }
                if (modelErrors.TryGetValue(request.FileDataId, out var modelError))
                {
                    resolutions[request.FileDataId] = new(request.FileDataId, null, [], modelError, false);
                    continue;
                }
                var skinArchivePath = ConventionalSkinPath(clientPath);
                var payloads = new List<PatchEntry>
                {
                    new(model.Result.OutputModelPath, clientPath),
                    new(model.Result.OutputSkinPath, skinArchivePath)
                };
                foreach (var texturePath in model.TexturePaths.Where(path => !hostEmbeddedTexturePaths.Contains(path)))
                {
                    var textureSource = ExtractedPath(extractedRoot, texturePath);
                    _ = BlpTextureService.Inspect(textureSource);
                    payloads.Add(new(textureSource, texturePath));
                }
                resolutions[request.FileDataId] = new(request.FileDataId, clientPath, payloads, null, false);
                continue;
            }
            if (!probes.TryGetValue(request.FileDataId, out var probe) || !probe.IsAvailableLocally)
            {
                var native = probe?.NativeError is { } code ? $" (CASC error {code})" : string.Empty;
                resolutions[request.FileDataId] = new(request.FileDataId, null, [],
                    $"Donor texture {clientPath} is not present in the local CASC payload{native}.", false);
                continue;
            }
            var sourcePath = ExtractedPath(extractedRoot, clientPath);
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length != probe.Size)
                throw new IOException($"Extracted CASC asset failed size verification: {clientPath}");
            _ = BlpTextureService.Inspect(sourcePath);
            var archivePath = $"Crucible\\CrossBuild\\26972\\{request.FileDataId.ToString(CultureInfo.InvariantCulture)}{Path.GetExtension(clientPath).ToLowerInvariant()}";
            resolutions[request.FileDataId] = new(request.FileDataId, archivePath, [new(sourcePath, archivePath)], null, false);
        }

        var values = resolutions.Values.ToArray();
        var payloadCount = values.SelectMany(value => value.Payloads).Select(value => value.ArchivePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var findings = new List<string>
        {
            $"Resolved {snapshot.Resolved.Count:N0}/{snapshot.RequestedIds.Count:N0} requested FileDataID path(s) from listfile SHA-256 {snapshot.SourceSha256}.",
            $"Reused {values.Count(value => value.ReusedHost):N0} asset path(s) already supplied by the MoP host and prepared {payloadCount:N0} verified patch payload(s), including {convertedModels.Count - modelErrors.Keys.Count(convertedModels.ContainsKey):N0} native MoP M2/SKIN model projection(s).",
            $"Blocked {values.Count(value => value.Error is not null):N0} asset ID(s) whose path, local bytes, type, dependency closure, or verified model translation was unavailable; owning donor rows are skipped with exact reasons."
        };
        return new(resolutions, findings);
    }

    public bool TryResolve(CrossBuildAssetFieldRule rule, uint fileDataId, bool markUsed, out string value, out string error)
    {
        if (fileDataId == 0)
        {
            value = string.Empty;
            error = string.Empty;
            return true;
        }
        if (!_resolutions.TryGetValue(fileDataId, out var resolution))
        {
            value = string.Empty;
            error = $"FileDataID {fileDataId.ToString(CultureInfo.InvariantCulture)} was not prepared for {rule.CanonicalTable}.{rule.DonorField}.";
            return false;
        }
        if (resolution.Error is not null || resolution.HostValue is null)
        {
            value = string.Empty;
            error = resolution.Error ?? $"FileDataID {fileDataId.ToString(CultureInfo.InvariantCulture)} has no host path.";
            return false;
        }
        if (markUsed && resolution.Payloads.Count > 0) _used.Add(fileDataId);
        value = resolution.HostValue;
        error = string.Empty;
        return true;
    }

    private static string ExtractedPath(string root, string clientPath) =>
        Path.Combine(root, PatchInputMapper.NormalizeArchivePath(clientPath).Replace('\\', Path.DirectorySeparatorChar));

    private static string ConventionalSkinPath(string modelPath)
    {
        var normalized = PatchInputMapper.NormalizeArchivePath(modelPath);
        return normalized[..^Path.GetExtension(normalized).Length] + "00.skin";
    }

    internal static HashSet<string> IndexHostPaths(
        string dataRoot,
        IEnumerable<string> requestedPaths,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var wanted = requestedPaths.Select(PatchInputMapper.NormalizeArchivePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        stagingRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        foreach (var path in wanted)
            if (File.Exists(Path.Combine(dataRoot, path.Replace('\\', Path.DirectorySeparatorChar)))) present.Add(path);

        var listfile = Path.Combine(stagingRoot, "host-asset-paths.listfile.txt");
        File.WriteAllLines(listfile, wanted.Order(StringComparer.OrdinalIgnoreCase), new UTF8Encoding(false));
        var archives = Directory.EnumerateFiles(dataRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".mpq", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var service = new PatchArchiveService();
        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MpqFileEntry> entries;
            try { entries = service.ListFiles(archive, "*", listfile); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidDataException($"Could not establish host asset presence because {archive} could not be indexed: {exception.Message}", exception);
            }
            foreach (var entry in entries)
                if (!entry.IsMetadata && wanted.Contains(entry.ArchivePath)) present.Add(PatchInputMapper.NormalizeArchivePath(entry.ArchivePath));
        }
        return present;
    }

    internal static string ResolveDataRoot(string value)
    {
        var root = RequiredDirectory(value, "Host client Data root");
        var nested = Path.Combine(root, "Data");
        if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals("Data", StringComparison.OrdinalIgnoreCase) && Directory.Exists(nested)) root = nested;
        return root;
    }

    private static string RequiredDirectory(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        return Directory.Exists(fullPath) ? fullPath : throw new DirectoryNotFoundException($"{label} does not exist: {fullPath}");
    }

    private static string RequiredFile(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath) ? fullPath : throw new FileNotFoundException($"{label} does not exist.", fullPath);
    }
}
