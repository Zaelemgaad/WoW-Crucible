using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

public sealed record FileDataIdPath(uint FileDataId, string ClientPath);

public sealed record FileDataIdListfileSnapshot(
    string SourcePath,
    string SourceSha256,
    IReadOnlyList<uint> RequestedIds,
    IReadOnlyList<FileDataIdPath> Resolved,
    IReadOnlyList<uint> MissingIds,
    IReadOnlyDictionary<uint, IReadOnlyList<string>> AmbiguousIds)
{
    public bool Complete => MissingIds.Count == 0 && AmbiguousIds.Count == 0;
    public IReadOnlyDictionary<uint, string> ResolvedById => Resolved.ToDictionary(value => value.FileDataId, value => value.ClientPath);
}

/// <summary>Streams a community/CASC id-to-path listfile and retains only explicitly requested IDs.</summary>
public static class FileDataIdListfileService
{
    public static FileDataIdListfileSnapshot Resolve(string sourcePath, IEnumerable<uint> requestedIds, CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The FileDataID listfile does not exist.", sourcePath);
        var requested = requestedIds.Where(value => value != 0).Distinct().Order().ToArray();
        var wanted = requested.ToHashSet();
        var candidates = requested.ToDictionary(value => value, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        string hash;
        using (var algorithm = SHA256.Create())
        {
            using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (var hashingStream = new CryptoStream(stream, algorithm, CryptoStreamMode.Read))
            using (var reader = new StreamReader(hashingStream, Encoding.UTF8, true, 1024 * 1024))
            {
                while (reader.ReadLine() is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TrySplit(line, out var rawId, out var rawPath) || !uint.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !wanted.Contains(id)) continue;
                    var candidate = rawPath.Trim().Trim('"').TrimEnd('\0');
                    if (candidate.Length == 0) continue;
                    try { candidates[id].Add(PatchInputMapper.NormalizeArchivePath(candidate)); }
                    catch (ArgumentException) { }
                }
            }
            hash = Convert.ToHexString(algorithm.Hash ?? throw new InvalidDataException("The FileDataID listfile hash could not be finalized."));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = candidates.Where(pair => pair.Value.Count == 1).Select(pair => new FileDataIdPath(pair.Key, pair.Value.Single())).OrderBy(value => value.FileDataId).ToArray();
        var missing = candidates.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).Order().ToArray();
        var ambiguous = candidates.Where(pair => pair.Value.Count > 1).OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        return new(sourcePath, hash, requested, resolved, missing, ambiguous);
    }

    private static bool TrySplit(string line, out string id, out string path)
    {
        var semicolon = line.IndexOf(';'); var tab = line.IndexOf('\t'); var comma = line.IndexOf(',');
        var separator = new[] { semicolon, tab, comma }.Where(value => value > 0).DefaultIfEmpty(-1).Min();
        if (separator < 0) { id = path = string.Empty; return false; }
        id = line[..separator].Trim().TrimStart('\uFEFF').Trim('"'); path = line[(separator + 1)..]; return id.Length > 0;
    }
}
