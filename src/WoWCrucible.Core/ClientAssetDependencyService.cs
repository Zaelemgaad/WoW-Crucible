using System.Text;

namespace WoWCrucible.Core;

public enum ClientAssetDependencyState { Root, Resolved, TargetOverride, TargetInherited, Missing, CrossSourceConflict, TargetAmbiguous, ExternalBinding, Invalid }
public sealed record ClientAssetLocation(string ClientPath, string Provenance, string SourcePath);
public sealed record ClientAssetDependencyNode(int Depth, string? ParentClientPath, string Kind, string ClientPath,
    ClientAssetDependencyState State, string Provenance, string? SourcePath, IReadOnlyList<string> Candidates, string Message,
    string? TargetArchive = null, string? TargetSha256 = null);
public sealed record ClientAssetDependencyGraph(ClientAssetLocation Root, IReadOnlyList<ClientAssetDependencyNode> Nodes, PatchTargetClientRequirement? TargetRequirement = null)
{
    public IReadOnlyList<ClientAssetDependencyNode> Blocking => Nodes.Where(node => node.State is ClientAssetDependencyState.Missing or ClientAssetDependencyState.CrossSourceConflict or ClientAssetDependencyState.TargetAmbiguous or ClientAssetDependencyState.Invalid).ToArray();
    public IReadOnlyList<ClientAssetDependencyNode> Resolved => Nodes.Where(node => node.State is ClientAssetDependencyState.Root or ClientAssetDependencyState.Resolved or ClientAssetDependencyState.TargetOverride).ToArray();
    public IReadOnlyList<ClientAssetDependencyNode> Inherited => Nodes.Where(node => node.State == ClientAssetDependencyState.TargetInherited).ToArray();
    public IReadOnlyList<ClientAssetDependencyNode> ExternalBindings => Nodes.Where(node => node.State == ClientAssetDependencyState.ExternalBinding).ToArray();
    public IReadOnlyList<PatchEntry> PatchEntries => Resolved.Where(node => node.SourcePath is not null).GroupBy(node => node.ClientPath, StringComparer.OrdinalIgnoreCase).Select(group => new PatchEntry(group.First().SourcePath!, group.Key)).OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
}

public static class ClientAssetDependencyService
{
    private const int MaximumNodes = 250_000;
    private sealed record Work(string SourcePath, string ClientPath, string Provenance, int Depth);
    private sealed record Reference(string Kind, string ClientPath, bool External = false, string? Message = null, bool Missing = false);
    private sealed record Chunk(string Id, byte[] Data);

    public static AssetComparisonIndex OpenLibraryLayout(string libraryRoot)
    {
        libraryRoot = Path.GetFullPath(libraryRoot); var archiveContent = Path.Combine(libraryRoot, "Archives", "Content"); var looseContent = Path.Combine(libraryRoot, "Loose", "Content");
        if (!Directory.Exists(archiveContent) && !Directory.Exists(looseContent)) throw new DirectoryNotFoundException($"No Archives\\Content or legacy Loose\\Content exists under {libraryRoot}.");
        return new(libraryRoot, archiveContent, [], 0, Directory.Exists(looseContent) ? looseContent : null);
    }

    public static ClientAssetLocation InferLocation(AssetComparisonIndex index, string sourcePath)
    {
        sourcePath = Path.GetFullPath(sourcePath); if (!File.Exists(sourcePath)) throw new FileNotFoundException("The dependency root does not exist.", sourcePath);
        if (index.LooseContentRoot is { } loose && IsInside(loose, sourcePath)) return new(PatchInputMapper.NormalizeArchivePath(Path.GetRelativePath(loose, sourcePath)), "Loose", sourcePath);
        if (!IsInside(index.ContentRoot, sourcePath)) throw new InvalidOperationException("The selected root is outside this processed asset library. Choose a file below Archives\\Content (or the legacy Loose\\Content root).");
        var relative = Path.GetRelativePath(index.ContentRoot, sourcePath); var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 2) throw new InvalidDataException("A content-first asset must be stored as <client directory>\\<provenance>\\<file>.");
        var provenance = parts[^2]; var clientParts = parts[..^2].Concat([parts[^1]]); return new(PatchInputMapper.NormalizeArchivePath(string.Join('\\', clientParts)), provenance, sourcePath);
    }

    public static IReadOnlyList<ClientAssetLocation> FindCandidates(AssetComparisonIndex index, string rawClientPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index); var clientPath = NormalizeReference(rawClientPath); var directory = Path.GetDirectoryName(clientPath) ?? string.Empty; var name = Path.GetFileName(clientPath);
        var result = new List<ClientAssetLocation>(); var archiveDirectory = Path.Combine(index.ContentRoot, directory);
        if (Directory.Exists(archiveDirectory))
        {
            foreach (var source in Directory.EnumerateDirectories(archiveDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested(); var candidate = Path.Combine(source, name);
                if (File.Exists(candidate)) result.Add(new(clientPath, Path.GetFileName(source), Path.GetFullPath(candidate)));
            }
        }
        if (index.LooseContentRoot is { } looseRoot)
        {
            cancellationToken.ThrowIfCancellationRequested(); var candidate = Path.Combine(looseRoot, clientPath);
            if (File.Exists(candidate)) result.Add(new(clientPath, "Loose", Path.GetFullPath(candidate)));
        }
        return result.DistinctBy(value => value.SourcePath, StringComparer.OrdinalIgnoreCase).OrderBy(value => value.Provenance, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ClientAssetDependencyGraph Analyze(AssetComparisonIndex index, ClientAssetLocation root, CancellationToken cancellationToken = default)
        => Analyze(index, root, null, null, cancellationToken);

    public static ClientAssetDependencyGraph Analyze(AssetComparisonIndex index, ClientAssetLocation root, IReadOnlyDictionary<string, string>? sourceOverrides, CancellationToken cancellationToken = default)
        => Analyze(index, root, sourceOverrides, null, cancellationToken);

    public static ClientAssetDependencyGraph Analyze(AssetComparisonIndex index, ClientAssetLocation root, IReadOnlyDictionary<string, string>? sourceOverrides,
        ClientEffectiveAssetCatalog? targetClient, CancellationToken cancellationToken = default)
        => Analyze(index, root, sourceOverrides, targetClient, null, cancellationToken);

    public static ClientAssetDependencyGraph Analyze(AssetComparisonIndex index, ClientAssetLocation root, IReadOnlyDictionary<string, string>? sourceOverrides,
        ClientEffectiveAssetCatalog? targetClient, IReadOnlyDictionary<string, string>? targetArchiveOverrides, CancellationToken cancellationToken = default)
    {
        root = root with { ClientPath = PatchInputMapper.NormalizeArchivePath(root.ClientPath), SourcePath = Path.GetFullPath(root.SourcePath) };
        if (!File.Exists(root.SourcePath)) throw new FileNotFoundException("The dependency root does not exist.", root.SourcePath);
        var overrides = (sourceOverrides ?? new Dictionary<string, string>()).ToDictionary(pair => PatchInputMapper.NormalizeArchivePath(pair.Key), pair => Path.GetFullPath(pair.Value), StringComparer.OrdinalIgnoreCase);
        var nodes = new List<ClientAssetDependencyNode>
        {
            new(0, null, "root", root.ClientPath, ClientAssetDependencyState.Root, root.Provenance, root.SourcePath, [root.SourcePath], "Selected dependency-graph root.")
        };
        var queue = new Queue<Work>(); queue.Enqueue(new(root.SourcePath, root.ClientPath, root.Provenance, 0));
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [root.ClientPath] = root.SourcePath };
        while (queue.TryDequeue(out var work))
        {
            cancellationToken.ThrowIfCancellationRequested(); if (!expanded.Add(work.SourcePath)) continue;
            IReadOnlyList<Reference> references;
            try { references = Inspect(work.SourcePath, work.ClientPath); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Add(new(work.Depth + 1, work.ClientPath, "parse", work.ClientPath, ClientAssetDependencyState.Invalid, work.Provenance, work.SourcePath, [work.SourcePath], exception.Message)); continue;
            }
            foreach (var reference in references)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reference.External)
                {
                    Add(new(work.Depth + 1, work.ClientPath, reference.Kind, reference.ClientPath, ClientAssetDependencyState.ExternalBinding, work.Provenance, null, [], reference.Message ?? "Resolved at runtime through DBC/SQL appearance data.")); continue;
                }
                if (reference.Missing)
                {
                    Add(new(work.Depth + 1, work.ClientPath, reference.Kind, reference.ClientPath, ClientAssetDependencyState.Missing, work.Provenance, null, [], reference.Message ?? "The dependency has no usable client path.")); continue;
                }
                var resolution = Resolve(index, work.Provenance, reference.ClientPath, reference.Kind, work.ClientPath, work.Depth + 1, overrides);
                if (resolution.State == ClientAssetDependencyState.Resolved && resolution.SourcePath is { } source)
                {
                    if (resolvedPaths.TryGetValue(resolution.ClientPath, out var existing) && !existing.Equals(source, StringComparison.OrdinalIgnoreCase))
                    {
                        Add(resolution with { State = ClientAssetDependencyState.CrossSourceConflict, SourcePath = null, Candidates = new[] { existing, source }, Message = $"The graph resolves '{resolution.ClientPath}' to multiple physical files. Select one provenance explicitly before deployment." }); continue;
                    }
                    resolvedPaths[resolution.ClientPath] = source; Add(resolution); queue.Enqueue(new(source, resolution.ClientPath, resolution.Provenance, work.Depth + 1));
                }
                else Add(resolution);
            }
        }
        var ordered = nodes.OrderBy(node => node.Depth).ThenBy(node => node.ClientPath, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.Kind, StringComparer.OrdinalIgnoreCase).ToArray();
        if (targetClient is not null) ordered = ApplyTargetClient(ordered, targetClient, targetArchiveOverrides, cancellationToken);
        var inheritedRequirements = ordered.Where(node => node.State == ClientAssetDependencyState.TargetInherited && node.TargetArchive is not null && node.TargetSha256 is not null)
                .GroupBy(node => node.ClientPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                .Select(node => new PatchInheritedAssetRequirement(node.ClientPath, node.TargetArchive!, node.TargetSha256!)).OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var requirement = targetClient is not null && inheritedRequirements.Length > 0 ? new PatchTargetClientRequirement(targetClient.Index.Name, targetClient.Index.ActiveLocale, targetClient.Fingerprint, inheritedRequirements) : null;
        return new(root, ordered, requirement);

        void Add(ClientAssetDependencyNode node) { if (nodes.Count >= MaximumNodes) throw new InvalidDataException($"Dependency graph exceeded {MaximumNodes:N0} nodes; the input is corrupt or unexpectedly broad."); nodes.Add(node); }
    }

    public static ClientAssetDependencyGraph Analyze(AssetComparisonIndex index, string sourcePath, CancellationToken cancellationToken = default)
        => Analyze(index, InferLocation(index, sourcePath), cancellationToken);

    private static ClientAssetDependencyNode[] ApplyTargetClient(IReadOnlyList<ClientAssetDependencyNode> nodes, ClientEffectiveAssetCatalog target, IReadOnlyDictionary<string, string>? targetArchiveOverrides, CancellationToken cancellationToken)
    {
        var targetChoices = (targetArchiveOverrides ?? new Dictionary<string, string>()).ToDictionary(pair => PatchInputMapper.NormalizeArchivePath(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var result = new List<ClientAssetDependencyNode>(nodes.Count);
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.State is ClientAssetDependencyState.ExternalBinding or ClientAssetDependencyState.Invalid) { result.Add(node); continue; }
            ClientEffectiveAssetResolution targetResolution;
            try { targetChoices.TryGetValue(node.ClientPath, out var targetChoice); targetResolution = target.Resolve(node.ClientPath, targetChoice, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.Add(node.SourcePath is null
                    ? node with { State = ClientAssetDependencyState.TargetAmbiguous, Message = $"Target-client resolution failed and this dependency has no staged source: {exception.Message}" }
                    : node with { Message = $"{node.Message} Target-client comparison failed, so Crucible conservatively keeps this file in the patch: {exception.Message}" });
                continue;
            }

            if (node.SourcePath is not null && node.State is ClientAssetDependencyState.Root or ClientAssetDependencyState.Resolved)
            {
                if (targetResolution.State == ClientEffectiveAssetState.Effective)
                {
                    try
                    {
                        var targetHash = target.HashEffective(targetResolution.Effective!, cancellationToken); var provider = targetResolution.Effective!.ArchiveRelativePath;
                        if (target.ContentEquals(node.SourcePath, targetResolution, cancellationToken))
                        {
                            result.Add(node with { State = ClientAssetDependencyState.TargetInherited, Provenance = $"Target client · {provider}", Message = $"Byte-identical target asset already supplied by {provider}; omitted from the minimal patch.", TargetArchive = provider, TargetSha256 = targetHash });
                        }
                        else result.Add(node with { State = ClientAssetDependencyState.TargetOverride, Message = $"{node.Message} Target currently supplies different bytes from {provider}; this source remains in the patch as an intentional override.", TargetArchive = provider, TargetSha256 = targetHash });
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException) { result.Add(node with { Message = $"{node.Message} Exact target comparison failed, so Crucible conservatively keeps this source in the patch: {exception.Message}" }); }
                }
                else if (targetResolution.State == ClientEffectiveAssetState.Ambiguous)
                    result.Add(node with { Message = $"{node.Message} {targetResolution.Message} This source remains staged, so the uncertainty cannot create a missing dependency." });
                else result.Add(node with { Message = $"{node.Message} Target does not supply this path; it remains in the patch." });
                continue;
            }

            if (node.State is ClientAssetDependencyState.Missing or ClientAssetDependencyState.CrossSourceConflict)
            {
                if (targetResolution.State == ClientEffectiveAssetState.Effective)
                {
                    var provider = targetResolution.Effective!.ArchiveRelativePath;
                    try
                    {
                        var targetHash = target.HashEffective(targetResolution.Effective, cancellationToken);
                        result.Add(node with { State = ClientAssetDependencyState.TargetInherited, Provenance = $"Target client · {provider}", SourcePath = null, Candidates = targetResolution.Candidates.Select(candidate => candidate.ArchiveRelativePath).ToArray(), Message = $"Required path is inherited from the effective target archive {provider}; no patch copy is needed. Review visual fidelity if another provenance was expected.", TargetArchive = provider, TargetSha256 = targetHash });
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        result.Add(node with { State = ClientAssetDependencyState.TargetAmbiguous, Candidates = targetResolution.Candidates.Select(candidate => candidate.ArchiveRelativePath).ToArray(), Message = $"The target path exists in {provider}, but its bytes could not be verified and no local source is staged: {exception.Message}" });
                    }
                }
                else if (targetResolution.State == ClientEffectiveAssetState.Ambiguous)
                    result.Add(node with { State = ClientAssetDependencyState.TargetAmbiguous, Candidates = targetResolution.Candidates.Select(candidate => candidate.ArchiveRelativePath).ToArray(), Message = $"This dependency is not staged locally and target precedence is ambiguous. {targetResolution.Message}" });
                else result.Add(node);
                continue;
            }
            result.Add(node);
        }
        return result.ToArray();
    }

    private static ClientAssetDependencyNode Resolve(AssetComparisonIndex index, string provenance, string rawPath, string kind, string parent, int depth, IReadOnlyDictionary<string, string> overrides)
    {
        var clientPath = NormalizeReference(rawPath); var directory = Path.GetDirectoryName(clientPath) ?? string.Empty; var name = Path.GetFileName(clientPath);
        if (overrides.TryGetValue(clientPath, out var chosen))
        {
            if (!File.Exists(chosen)) return new(depth, parent, kind, clientPath, ClientAssetDependencyState.Invalid, provenance, null, [chosen], "The explicitly selected dependency candidate no longer exists.");
            try
            {
                var location = InferLocation(index, chosen);
                if (!location.ClientPath.Equals(clientPath, StringComparison.OrdinalIgnoreCase)) return new(depth, parent, kind, clientPath, ClientAssetDependencyState.Invalid, provenance, null, [chosen], $"The selected candidate maps to '{location.ClientPath}', not '{clientPath}'.");
                return new(depth, parent, kind, clientPath, ClientAssetDependencyState.Resolved, location.Provenance, location.SourcePath, [location.SourcePath], $"Resolved through the explicit provenance choice '{location.Provenance}'.");
            }
            catch (Exception exception) { return new(depth, parent, kind, clientPath, ClientAssetDependencyState.Invalid, provenance, null, [chosen], $"The selected candidate is invalid: {exception.Message}"); }
        }
        string? sameSource = null;
        if (provenance.Equals("Loose", StringComparison.OrdinalIgnoreCase) && index.LooseContentRoot is { } loose) sameSource = Path.Combine(loose, clientPath);
        else sameSource = Path.Combine(index.ContentRoot, directory, provenance, name);
        if (File.Exists(sameSource)) return new(depth, parent, kind, clientPath, ClientAssetDependencyState.Resolved, provenance, sameSource, [sameSource], "Resolved from the root asset's provenance layer.");

        var candidates = FindCandidates(index, clientPath).Select(value => value.SourcePath).ToArray();
        return candidates.Length == 0
            ? new(depth, parent, kind, clientPath, ClientAssetDependencyState.Missing, provenance, null, [], $"Required {kind} '{clientPath}' is absent from the processed library.")
            : new(depth, parent, kind, clientPath, ClientAssetDependencyState.CrossSourceConflict, provenance, null, candidates, $"Required {kind} exists only in {candidates.Length:N0} other provenance layer(s); Crucible will not silently mix sources.");
    }

    private static IReadOnlyList<Reference> Inspect(string sourcePath, string clientPath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        return extension switch
        {
            ".m2" => InspectM2(sourcePath, clientPath),
            ".wmo" => InspectWmo(sourcePath, clientPath),
            ".adt" or ".wdt" => InspectMap(sourcePath),
            ".skin" or ".anim" or ".blp" or ".png" => [],
            _ => []
        };
    }

    private static IReadOnlyList<Reference> InspectM2(string path, string clientPath)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[0x58]; if (stream.Read(header) != header.Length) throw new InvalidDataException("M2 header is truncated.");
        var magic = Encoding.ASCII.GetString(header[..4]); if (magic != "MD20" || BitConverter.ToUInt32(header[4..8]) != 264) throw new InvalidDataException("Recursive M2 closure currently requires an unwrapped Wrath MD20 version 264 model.");
        var result = new List<Reference>(); var directory = Path.GetDirectoryName(clientPath) ?? string.Empty; var stem = Path.GetFileNameWithoutExtension(clientPath);
        var views = BitConverter.ToUInt32(header[0x44..0x48]); if (views > 32) throw new InvalidDataException($"M2 declares an unreasonable {views:N0} view/SKIN count.");
        for (var index = 0; index < views; index++) result.Add(new("skin", Combine(directory, $"{stem}{index:00}.skin")));
        foreach (var animation in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "*.anim", SearchOption.TopDirectoryOnly)) result.Add(new("animation", Combine(directory, Path.GetFileName(animation))));
        foreach (var slot in M2PreviewGeometryService.InspectTextureSlots(path))
            result.Add(slot.Type == 0
                ? string.IsNullOrWhiteSpace(slot.EmbeddedPath) ? new("embedded-texture", $"slot:{slot.Index}", false, $"Embedded texture slot {slot.Index} has no client path.", true) : new("embedded-texture", slot.EmbeddedPath)
                : new($"replaceable-texture-{slot.Type}", $"slot:{slot.Index}", true, $"Replaceable slot {slot.Index} ({ReplaceableName(slot.Type)}) is supplied by DBC/SQL appearance data."));
        return result;
    }

    private static IReadOnlyList<Reference> InspectWmo(string path, string clientPath)
    {
        var chunks = ReadChunks(path); var result = new List<Reference>(); var directory = Path.GetDirectoryName(clientPath) ?? string.Empty; var stem = Path.GetFileNameWithoutExtension(clientPath);
        if (!chunks.Any(chunk => chunk.Id == "MVER")) throw new InvalidDataException("WMO has no MVER chunk.");
        var root = chunks.FirstOrDefault(chunk => chunk.Id == "MOHD"); if (root is null) return [];
        foreach (var texture in Strings(chunks, "MOTX").Where(value => Path.GetExtension(value).Equals(".blp", StringComparison.OrdinalIgnoreCase))) result.Add(new("wmo-texture", texture));
        foreach (var model in Strings(chunks, "MODN").Concat(Strings(chunks, "MOSB")).Where(value => ExtensionIs(value, ".m2", ".mdx"))) result.Add(new("wmo-doodad-model", model));
        if (root.Data.Length < 8) throw new InvalidDataException("WMO MOHD chunk is truncated before its group count."); var groups = BitConverter.ToUInt32(root.Data, 4);
        if (groups > 100_000) throw new InvalidDataException($"WMO declares an unreasonable {groups:N0} group files.");
        for (var index = 0; index < groups; index++) result.Add(new("wmo-group", Combine(directory, $"{stem}_{index:000}.wmo")));
        return result.DistinctBy(reference => (reference.Kind, NormalizeReference(reference.ClientPath))).ToArray();
    }

    private static IReadOnlyList<Reference> InspectMap(string path)
    {
        var chunks = ReadChunks(path); var result = new List<Reference>();
        foreach (var texture in Strings(chunks, "MTEX").Where(value => Path.GetExtension(value).Equals(".blp", StringComparison.OrdinalIgnoreCase))) result.Add(new("terrain-texture", texture));
        foreach (var model in Strings(chunks, "MMDX").Where(value => ExtensionIs(value, ".m2", ".mdx"))) result.Add(new("map-model", model));
        foreach (var wmo in Strings(chunks, "MWMO").Where(value => Path.GetExtension(value).Equals(".wmo", StringComparison.OrdinalIgnoreCase))) result.Add(new("map-wmo", wmo));
        return result.DistinctBy(reference => (reference.Kind, NormalizeReference(reference.ClientPath))).ToArray();
    }

    private static IReadOnlyList<Chunk> ReadChunks(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan); var result = new List<Chunk>(); Span<byte> header = stackalloc byte[8];
        while (stream.Position < stream.Length)
        {
            var offset = stream.Position; if (stream.Read(header) != 8) throw new InvalidDataException($"Chunk header is truncated at byte {offset:N0}.");
            var id = new string(Encoding.ASCII.GetString(header[..4]).Reverse().ToArray()); var size = BitConverter.ToUInt32(header[4..]); if (size > int.MaxValue || stream.Position + size > stream.Length) throw new InvalidDataException($"Chunk {id} at byte {offset:N0} extends beyond the file.");
            if (id is "MVER" or "MOHD" or "MOTX" or "MODN" or "MOSB" or "MTEX" or "MMDX" or "MWMO") { var data = new byte[checked((int)size)]; stream.ReadExactly(data); result.Add(new(id, data)); }
            else stream.Position += size;
        }
        return result;
    }

    private static IEnumerable<string> Strings(IReadOnlyList<Chunk> chunks, string id)
    {
        foreach (var chunk in chunks.Where(chunk => chunk.Id == id))
        {
            var start = 0;
            for (var index = 0; index <= chunk.Data.Length; index++)
                if (index == chunk.Data.Length || chunk.Data[index] == 0)
                {
                    if (index > start) { var value = Encoding.UTF8.GetString(chunk.Data, start, index - start).Trim(); if (value.Length > 0) yield return value; }
                    start = index + 1;
                }
        }
    }

    private static string NormalizeReference(string value)
    {
        value = value.Trim().Replace('/', Path.DirectorySeparatorChar); if (value.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)) value = Path.ChangeExtension(value, ".m2"); return PatchInputMapper.NormalizeArchivePath(value);
    }
    private static string Combine(string directory, string file) => string.IsNullOrEmpty(directory) ? file : directory.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar + file;
    private static string ReplaceableName(uint type) => type switch { 1 => "body+clothes", 2 => "cape", 6 => "hair/beard", 8 => "fur", 11 => "creature-skin-1", 12 => "creature-skin-2", 13 => "creature-skin-3", _ => "custom" };
    private static bool ExtensionIs(string path, params string[] extensions) => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static bool IsInside(string root, string path) { var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)); return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar); }
}
