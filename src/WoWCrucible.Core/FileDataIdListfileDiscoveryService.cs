using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

public sealed record FileDataIdListfileCandidate(string Path, long Bytes, DateTime LastWriteUtc);

public sealed record FileDataIdListfileDiscoveryResult(
    IReadOnlyList<FileDataIdListfileCandidate> Candidates,
    FileDataIdListfileSnapshot? Selected,
    IReadOnlyList<string> Findings)
{
    public bool Ready => Selected is not null;
}

/// <summary>
/// Finds only conventional, nearby FileDataID listfile locations. Candidate
/// contents must resolve the exact requested IDs, and conflicting complete
/// mappings are refused instead of choosing by filename or timestamp.
/// </summary>
public static class FileDataIdListfileDiscoveryService
{
    private const int MaximumAncestorDepth = 6;

    public static FileDataIdListfileDiscoveryResult ResolveBest(
        IEnumerable<uint> requestedIds,
        IEnumerable<string> contextPaths,
        CancellationToken cancellationToken = default,
        bool includeDefaultContexts = true)
    {
        var requested = requestedIds.Where(value => value != 0).Distinct().Order().ToArray();
        var candidates = Discover(contextPaths, cancellationToken, includeDefaultContexts);
        if (requested.Length == 0) return new(candidates, null, ["The selected models contain no external texture FileDataIDs; no listfile is required."]);
        if (candidates.Count == 0) return new(candidates, null, ["No nearby FileDataID listfile was discovered. Choose one explicitly or place it in a Listfiles folder beside the application/project."]);

        var complete = new List<(FileDataIdListfileCandidate Candidate, FileDataIdListfileSnapshot Snapshot, string MappingHash)>();
        var findings = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = FileDataIdListfileService.Resolve(candidate.Path, requested, cancellationToken);
                if (!snapshot.Complete)
                {
                    findings.Add($"{candidate.Path} is incomplete for this batch ({snapshot.MissingIds.Count:N0} missing, {snapshot.AmbiguousIds.Count:N0} ambiguous ID(s)).");
                    continue;
                }
                complete.Add((candidate, snapshot, MappingHash(snapshot)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                findings.Add($"{candidate.Path} could not be used: {exception.Message}");
            }
        }
        if (complete.Count == 0)
        {
            findings.Insert(0, $"None of the {candidates.Count:N0} discovered listfile(s) completely resolves the {requested.Length:N0} required FileDataID(s).");
            return new(candidates, null, findings);
        }

        var mappingGroups = complete.GroupBy(value => value.MappingHash, StringComparer.Ordinal).ToArray();
        if (mappingGroups.Length != 1)
        {
            findings.Insert(0, $"Discovered complete listfiles disagree on the requested FileDataID paths ({mappingGroups.Length:N0} distinct mappings); choose the intended build listfile explicitly.");
            foreach (var group in mappingGroups) findings.Add($"Mapping {group.Key[..12]}: {string.Join(" | ", group.Select(value => value.Candidate.Path))}");
            return new(candidates, null, findings);
        }

        var selected = mappingGroups[0].OrderByDescending(value => value.Candidate.LastWriteUtc).ThenByDescending(value => value.Candidate.Bytes).ThenBy(value => value.Candidate.Path, StringComparer.OrdinalIgnoreCase).First();
        findings.Insert(0, complete.Count == 1
            ? $"Auto-selected the only complete nearby listfile: {selected.Candidate.Path}"
            : $"Auto-selected {selected.Candidate.Path}; {complete.Count:N0} complete nearby listfiles agree on every requested mapping.");
        return new(candidates, selected.Snapshot, findings);
    }

    public static IReadOnlyList<FileDataIdListfileCandidate> Discover(IEnumerable<string> contextPaths, CancellationToken cancellationToken = default, bool includeDefaultContexts = true)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var starts = includeDefaultContexts ? contextPaths.Append(CruciblePaths.ApplicationDirectory).Append(Environment.CurrentDirectory) : contextPaths;
        foreach (var input in starts)
        {
            if (string.IsNullOrWhiteSpace(input)) continue;
            string? start;
            try
            {
                var full = Path.GetFullPath(input);
                start = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            }
            catch { continue; }
            for (var depth = 0; depth <= MaximumAncestorDepth && !string.IsNullOrWhiteSpace(start); depth++, start = Path.GetDirectoryName(start)) directories.Add(start);
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddDirectory(Path.Combine(directory, "Listfiles"));
            AddDirectory(Path.Combine(directory, "Listfile"));
            AddDirectory(Path.Combine(directory, "Tools", "CASC", "Listfile"));
            AddDirect(directory);
        }

        return paths.Select(path => new FileInfo(path)).Where(file => file.Exists)
            .Select(file => new FileDataIdListfileCandidate(file.FullName, file.Length, file.LastWriteTimeUtc))
            .OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        void AddDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    if (IsCandidateName(file) && LooksLikeIdPathListfile(file)) paths.Add(Path.GetFullPath(file));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        void AddDirect(string directory)
        {
            if (!Directory.Exists(directory)) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*listfile*.*", SearchOption.TopDirectoryOnly))
                    if (IsCandidateName(file) && LooksLikeIdPathListfile(file)) paths.Add(Path.GetFullPath(file));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    private static bool IsCandidateName(string path) => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeIdPathListfile(string path)
    {
        try
        {
            var info = new FileInfo(path); if (!info.Exists || info.Length == 0) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, true);
            for (var index = 0; index < 256 && reader.ReadLine() is { } line; index++)
            {
                var separator = new[] { line.IndexOf(';'), line.IndexOf(','), line.IndexOf('\t') }.Where(value => value > 0).DefaultIfEmpty(-1).Min();
                if (separator > 0 && uint.TryParse(line.AsSpan(0, separator).Trim().TrimStart('\uFEFF').Trim('"'), out _) && line[(separator + 1)..].Trim().Length > 0) return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static string MappingHash(FileDataIdListfileSnapshot snapshot)
    {
        var text = string.Join('\n', snapshot.Resolved.OrderBy(value => value.FileDataId).Select(value => $"{value.FileDataId};{value.ClientPath.ToUpperInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
