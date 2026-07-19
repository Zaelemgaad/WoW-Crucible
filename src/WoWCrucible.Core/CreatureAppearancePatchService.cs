using System.Security.Cryptography;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record CreatureAppearancePatchEntry(string Kind, string SourcePath, string ArchivePath, string Provenance, string Sha256);

public sealed record CreatureAppearancePatchAsset(
    string Kind,
    string ClientPath,
    ClientAssetDependencyState State,
    string Provenance,
    string? SourcePath,
    IReadOnlyList<string> Candidates,
    string Message);

public sealed record CreatureAppearancePatchPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    CreatureAppearancePortPlan AppearancePlan,
    string ProcessedLibraryRoot,
    string? RequestedProvenance,
    string? EffectiveProvenance,
    IReadOnlyDictionary<string, string> ChangedDbcFiles,
    IReadOnlyDictionary<string, string> ChangedDbcSha256,
    IReadOnlyList<CreatureAppearancePatchEntry> Entries,
    IReadOnlyList<CreatureAppearancePatchAsset> Assets,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Findings)
{
    public bool Ready => Blockers.Count == 0 && Entries.Count > 0;
    public IReadOnlyList<PatchEntry> PatchEntries => Entries.Select(entry => new PatchEntry(entry.SourcePath, entry.ArchivePath)).ToArray();
}

/// <summary>
/// Turns a reviewed additive creature-appearance DBC result into a tiny MPQ-ready
/// closure. Every model dependency stays in one provenance unless byte-identical
/// duplicates make the choice immaterial; cross-layer guesses remain blockers.
/// </summary>
public static class CreatureAppearancePatchService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static CreatureAppearancePatchPlan CreatePlan(CreatureAppearancePortResult appearanceResult, string processedLibraryRoot, string? provenance = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appearanceResult); ValidateAppearanceResult(appearanceResult); CreatureAppearancePortService.VerifyPlanSourceInputs(appearanceResult.Plan);
        processedLibraryRoot = Path.GetFullPath(processedLibraryRoot); var index = ClientAssetDependencyService.OpenLibraryLayout(processedLibraryRoot);
        provenance = string.IsNullOrWhiteSpace(provenance) ? null : provenance.Trim(); var inferred = InferProvenance(index, appearanceResult.Plan.SourceDbcRoot); var effective = provenance ?? inferred;
        var findings = new List<string>(); var blockers = new List<string>(); var assets = new List<CreatureAppearancePatchAsset>(); var entries = new Dictionary<string, CreatureAppearancePatchEntry>(StringComparer.OrdinalIgnoreCase);
        if (provenance is not null) findings.Add($"Every source asset is bound to explicit provenance '{provenance}'.");
        else if (inferred is not null) findings.Add($"Source DBC location binds this appearance to processed provenance '{inferred}'.");
        else findings.Add("The source DBC folder is outside the processed library; unambiguous or byte-identical asset paths may resolve automatically, while different-byte multi-source paths remain blocked.");

        foreach (var pair in appearanceResult.OutputFiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            AddEntry("changed-dbc", pair.Value, $"DBFilesClient\\{pair.Key}.dbc", "Crucible DBC output");

        foreach (var requirementGroup in appearanceResult.Plan.RequiredAssets.GroupBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested(); var clientPath = PatchInputMapper.NormalizeArchivePath(requirementGroup.Key); var kind = string.Join('+', requirementGroup.Select(asset => asset.Kind).Distinct(StringComparer.OrdinalIgnoreCase)); var candidates = ClientAssetDependencyService.FindCandidates(index, clientPath, cancellationToken);
            var selected = SelectCandidate(clientPath, kind, candidates, effective, blockers, assets);
            if (selected is null) continue;
            var extension = Path.GetExtension(clientPath);
            if (extension.Equals(".m2", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wmo", StringComparison.OrdinalIgnoreCase) || extension.Equals(".adt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wdt", StringComparison.OrdinalIgnoreCase))
            {
                ClientAssetDependencyGraph graph;
                try { graph = ClientAssetDependencyService.Analyze(index, selected, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var message = $"Could not inspect dependency closure for {clientPath}: {exception.Message}"; blockers.Add(message); assets.Add(new(kind, clientPath, ClientAssetDependencyState.Invalid, selected.Provenance, selected.SourcePath, [selected.SourcePath], message)); continue;
                }
                foreach (var node in graph.Nodes)
                {
                    assets.Add(new(node.Kind, node.ClientPath, node.State, node.Provenance, node.SourcePath, node.Candidates, node.Message));
                    if (node.State is ClientAssetDependencyState.Missing or ClientAssetDependencyState.CrossSourceConflict or ClientAssetDependencyState.TargetAmbiguous or ClientAssetDependencyState.Invalid) blockers.Add($"{node.ClientPath}: {node.Message}");
                }
                foreach (var entry in graph.PatchEntries) AddEntry("dependency", entry.SourcePath, entry.ArchivePath, selected.Provenance);
            }
            else
            {
                assets.Add(new(kind, clientPath, ClientAssetDependencyState.Resolved, selected.Provenance, selected.SourcePath, [selected.SourcePath], "Resolved required appearance asset."));
                AddEntry(kind, selected.SourcePath, clientPath, selected.Provenance);
            }
        }

        var orderedAssets = assets.DistinctBy(asset => (asset.Kind.ToUpperInvariant(), asset.ClientPath.ToUpperInvariant(), asset.State, (asset.SourcePath ?? string.Empty).ToUpperInvariant())).OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).ThenBy(asset => asset.Kind, StringComparer.OrdinalIgnoreCase).ToArray();
        var orderedBlockers = blockers.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(); var orderedEntries = entries.Values.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
        findings.Add(orderedBlockers.Length == 0 ? $"Patch closure is ready with {orderedEntries.Length:N0} unique file(s)." : $"Patch closure has {orderedBlockers.Length:N0} blocker(s); no manifest or MPQ should be built yet.");
        return new(FormatVersion, DateTimeOffset.UtcNow, appearanceResult.Plan, processedLibraryRoot, provenance, effective,
            appearanceResult.OutputFiles.ToDictionary(pair => pair.Key, pair => Path.GetFullPath(pair.Value), StringComparer.OrdinalIgnoreCase),
            appearanceResult.OutputSha256.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase), orderedEntries, orderedAssets, orderedBlockers, findings);

        ClientAssetLocation? SelectCandidate(string clientPath, string kind, IReadOnlyList<ClientAssetLocation> candidates, string? selectedProvenance, ICollection<string> planBlockers, ICollection<CreatureAppearancePatchAsset> planAssets)
        {
            var eligible = selectedProvenance is null ? candidates.ToArray() : candidates.Where(candidate => candidate.Provenance.Equals(selectedProvenance, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (eligible.Length == 0)
            {
                var state = candidates.Count == 0 ? ClientAssetDependencyState.Missing : ClientAssetDependencyState.CrossSourceConflict;
                var message = candidates.Count == 0 ? $"Required asset is absent from the processed library: {clientPath}" : $"Required asset exists in {candidates.Count:N0} other provenance layer(s), but not the bound provenance '{selectedProvenance}'.";
                planBlockers.Add(message); planAssets.Add(new(kind, clientPath, state, selectedProvenance ?? string.Empty, null, candidates.Select(candidate => candidate.SourcePath).ToArray(), message)); return null;
            }
            if (eligible.Length == 1) return eligible[0];
            var groups = eligible.GroupBy(candidate => Hash(candidate.SourcePath), StringComparer.OrdinalIgnoreCase).ToArray();
            if (groups.Length == 1)
            {
                var chosen = eligible.OrderBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase).First(); findings.Add($"{clientPath} has {eligible.Length:N0} byte-identical candidates; selected {chosen.SourcePath} deterministically."); return chosen;
            }
            var conflict = $"Required asset {clientPath} has {eligible.Length:N0} different-byte candidates{(selectedProvenance is null ? string.Empty : $" within provenance '{selectedProvenance}'")}; choose a unique provenance or remove the collision.";
            planBlockers.Add(conflict); planAssets.Add(new(kind, clientPath, ClientAssetDependencyState.CrossSourceConflict, selectedProvenance ?? string.Empty, null, eligible.Select(candidate => candidate.SourcePath).ToArray(), conflict)); return null;
        }

        void AddEntry(string kind, string sourcePath, string archivePath, string sourceProvenance)
        {
            sourcePath = Path.GetFullPath(sourcePath); archivePath = PatchInputMapper.NormalizeArchivePath(archivePath); var hash = Hash(sourcePath); var candidate = new CreatureAppearancePatchEntry(kind, sourcePath, archivePath, sourceProvenance, hash);
            if (!entries.TryGetValue(archivePath, out var existing)) { entries[archivePath] = candidate; return; }
            if (existing.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)) return;
            blockers.Add($"Patch path {archivePath} resolves to different bytes from '{existing.SourcePath}' and '{sourcePath}'.");
        }
    }

    public static void SavePlan(string path, CreatureAppearancePatchPlan plan)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(plan, JsonOptions)); File.Move(temporary, path, true);
    }

    public static CreatureAppearancePatchPlan LoadPlan(string path)
    {
        var plan = JsonSerializer.Deserialize<CreatureAppearancePatchPlan>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Creature appearance patch plan is empty.");
        if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported creature appearance patch plan version {plan.FormatVersion}."); return plan;
    }

    public static PatchManifest ExportManifest(CreatureAppearancePatchPlan plan, string manifestPath, string outputFileName = "patch-Crucible-Appearance.MPQ")
    {
        Verify(plan); if (!plan.Ready) throw new InvalidOperationException($"Appearance patch plan has {plan.Blockers.Count:N0} blocker(s); resolve them before exporting a manifest.");
        var required = plan.ChangedDbcFiles.Count == 0 ? Array.Empty<string>() : ["DBFilesClient\\*.dbc"];
        var policy = new PatchManifestPolicy(ExpectedEntryCount: plan.Entries.Count, RequiredGlobs: required);
        PatchManifestService.Save(manifestPath, $"Creature display {plan.AppearancePlan.TargetDisplayId}", outputFileName, plan.PatchEntries, policy: policy);
        return PatchManifestService.Load(manifestPath);
    }

    public static void Verify(CreatureAppearancePatchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported creature appearance patch plan version {plan.FormatVersion}."); CreatureAppearancePortService.VerifyPlanSourceInputs(plan.AppearancePlan);
        var expectedTables = plan.AppearancePlan.ChangedTables.Order(StringComparer.OrdinalIgnoreCase).ToArray(); var actualTables = plan.ChangedDbcFiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!expectedTables.SequenceEqual(actualTables, StringComparer.OrdinalIgnoreCase) || !actualTables.SequenceEqual(plan.ChangedDbcSha256.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Appearance patch plan changed-DBC inventory does not match its appearance plan.");
        foreach (var table in actualTables) if (!Hash(plan.ChangedDbcFiles[table]).Equals(plan.ChangedDbcSha256[table], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Changed {table}.dbc no longer matches the patch plan.");
        foreach (var entry in plan.Entries)
        {
            if (!Hash(entry.SourcePath).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Patch source changed after planning: {entry.SourcePath}");
            if (!PatchInputMapper.NormalizeArchivePath(entry.ArchivePath).Equals(entry.ArchivePath, StringComparison.Ordinal)) throw new InvalidDataException($"Patch entry path is not normalized: {entry.ArchivePath}");
        }
        var validation = PatchManifestService.ValidateEntries(plan.PatchEntries, new(ExpectedEntryCount: plan.Entries.Count)); if (!validation.Passed) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));
    }

    private static void ValidateAppearanceResult(CreatureAppearancePortResult result)
    {
        if (result.TargetDisplayId != result.Plan.TargetDisplayId) throw new InvalidDataException("Appearance result target display does not match its plan.");
        var expected = result.Plan.ChangedTables.Order(StringComparer.OrdinalIgnoreCase).ToArray(); var actual = result.OutputFiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase) || !actual.SequenceEqual(result.OutputSha256.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Appearance result does not contain exactly its changed DBC outputs.");
        foreach (var table in actual) if (!Hash(result.OutputFiles[table]).Equals(result.OutputSha256[table], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Appearance output {table}.dbc no longer matches its receipt hash.");
    }

    private static string? InferProvenance(AssetComparisonIndex index, string sourceDbcRoot)
    {
        sourceDbcRoot = Path.GetFullPath(sourceDbcRoot); if (!IsInside(index.ContentRoot, sourceDbcRoot)) return null; var relative = Path.GetRelativePath(index.ContentRoot, sourceDbcRoot); var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("DBFilesClient", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    private static string Hash(string path) { using var stream = File.OpenRead(Path.GetFullPath(path)); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool IsInside(string root, string path) { var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)); return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar); }
}
