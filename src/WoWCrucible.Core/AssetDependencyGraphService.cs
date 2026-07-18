namespace WoWCrucible.Core;

public enum AssetDependencyState { Resolved, Missing, CrossSourceConflict, ExternalBinding }
public sealed record AssetDependencyResolution(string Kind, string ClientPath, AssetDependencyState State, string? SourcePath, IReadOnlyList<string> Candidates, string Message);
public sealed record AssetDependencyGraph(AssetComparisonModel Model, IReadOnlyList<AssetDependencyResolution> Dependencies)
{
    public IReadOnlyList<AssetDependencyResolution> Blocking => Dependencies.Where(dependency => dependency.State is AssetDependencyState.Missing or AssetDependencyState.CrossSourceConflict).ToArray();
    public IReadOnlyList<AssetDependencyResolution> Resolved => Dependencies.Where(dependency => dependency.State == AssetDependencyState.Resolved).ToArray();
    public IReadOnlyList<AssetDependencyResolution> ExternalBindings => Dependencies.Where(dependency => dependency.State == AssetDependencyState.ExternalBinding).ToArray();
}

public static class AssetDependencyGraphService
{
    public static AssetDependencyGraph AnalyzeModel(AssetComparisonIndex index, AssetComparisonModel model)
    {
        var dependencies = new List<AssetDependencyResolution>(); var directory = Path.GetDirectoryName(model.ModelPath)!; var stem = Path.GetFileNameWithoutExtension(model.ModelPath);
        foreach (var path in Directory.EnumerateFiles(directory, stem + "*", SearchOption.TopDirectoryOnly).Where(path => Path.GetExtension(path).Equals(".skin", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".anim", StringComparison.OrdinalIgnoreCase)))
            dependencies.Add(new(Path.GetExtension(path).Equals(".skin", StringComparison.OrdinalIgnoreCase) ? "skin" : "animation", Combine(model.LogicalPath, Path.GetFileName(path)), AssetDependencyState.Resolved, path, [path], "Companion file from the selected model's provenance layer."));

        foreach (var slot in M2PreviewGeometryService.InspectTextureSlots(model.ModelPath))
        {
            if (slot.Type != 0)
            {
                dependencies.Add(new($"replaceable-texture-{slot.Type}", $"slot:{slot.Index}", AssetDependencyState.ExternalBinding, null, [], $"Replaceable slot {slot.Index} ({ReplaceableName(slot.Type)}) is supplied by character/creature/item data rather than an embedded model path."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(slot.EmbeddedPath)) { dependencies.Add(new("embedded-texture", $"slot:{slot.Index}", AssetDependencyState.Missing, null, [], $"Embedded texture slot {slot.Index} has no client path.")); continue; }
            dependencies.Add(ResolveClientAsset(index, model.Provenance, slot.EmbeddedPath));
        }
        if (!dependencies.Any(dependency => dependency.Kind == "skin")) dependencies.Add(new("skin", Combine(model.LogicalPath, stem + "00.skin"), AssetDependencyState.Missing, null, [], "No valid companion SKIN is present."));
        return new(model, dependencies);
    }

    public static AssetDependencyResolution ResolveClientAsset(AssetComparisonIndex index, string provenance, string rawClientPath)
    {
        var clientPath = PatchInputMapper.NormalizeArchivePath(rawClientPath); var directory = Path.GetDirectoryName(clientPath) ?? string.Empty; var name = Path.GetFileName(clientPath);
        if (provenance.Equals("Loose", StringComparison.OrdinalIgnoreCase) && index.LooseContentRoot is { } loose)
        {
            var path = Path.Combine(loose, clientPath); if (File.Exists(path)) return Resolved(path);
        }
        else
        {
            var sameSource = Path.Combine(index.ContentRoot, directory, provenance, name); if (File.Exists(sameSource)) return Resolved(sameSource);
        }
        var candidates = new List<string>(); var archiveDirectory = Path.Combine(index.ContentRoot, directory);
        if (Directory.Exists(archiveDirectory)) foreach (var source in Directory.EnumerateDirectories(archiveDirectory)) { var candidate = Path.Combine(source, name); if (File.Exists(candidate)) candidates.Add(candidate); }
        if (index.LooseContentRoot is { } looseRoot) { var looseCandidate = Path.Combine(looseRoot, clientPath); if (File.Exists(looseCandidate)) candidates.Add(looseCandidate); }
        return candidates.Count == 0
            ? new("embedded-texture", clientPath, AssetDependencyState.Missing, null, [], $"Required embedded texture '{clientPath}' is absent from the processed library.")
            : new("embedded-texture", clientPath, AssetDependencyState.CrossSourceConflict, null, candidates, $"Required texture exists only in {candidates.Count:N0} other provenance layer(s). Crucible will not silently mix model sources.");

        AssetDependencyResolution Resolved(string path) => new("embedded-texture", clientPath, AssetDependencyState.Resolved, path, [path], "Embedded texture resolved from the selected model's provenance layer.");
    }

    private static string Combine(string logical, string file) => string.IsNullOrEmpty(logical) ? file : logical.Trim('\\', '/') + "\\" + file;
    private static string ReplaceableName(uint type) => type switch { 1 => "body+clothes", 2 => "cape", 6 => "hair/beard", 8 => "fur", 11 => "creature-skin-1", 12 => "creature-skin-2", 13 => "creature-skin-3", _ => "custom" };
}
