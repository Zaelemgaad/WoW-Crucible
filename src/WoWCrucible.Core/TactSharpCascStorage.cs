using System.Globalization;
using TACTSharp;

namespace WoWCrucible.Core;

/// <summary>
/// Local-only fallback for custom CASC installations that retain root, encoding,
/// and index data but omit the install manifest required by CascLib.
/// </summary>
internal sealed class TactSharpCascStorage
{
    private const int BufferSize = 1024 * 1024;
    private readonly BuildInstance _build;
    private readonly LocalCascIndex _localIndex;

    private TactSharpCascStorage(string storagePath, BuildInstance build)
    {
        _build = build;
        _localIndex = new(storagePath);
    }

    public static TactSharpCascStorage Open(string storagePath)
    {
        var configs = FindConfigs(storagePath);
        Settings.LogLevel = TSLogLevel.Warn;
        var build = new BuildInstance();
        build.Settings.BaseDir = storagePath;
        build.Settings.CacheDir = Path.Combine(CruciblePaths.CacheDirectory, "TACTSharp");
        build.Settings.ListfileFallback = false;
        build.Settings.TryCDN = false;
        build.Settings.Locale = RootInstance.LocaleFlags.enUS;
        build.Settings.RootMode = RootInstance.LoadMode.Normal;
        Directory.CreateDirectory(build.Settings.CacheDir);
        build.LoadConfigs(configs.BuildConfig, configs.CdnConfig);
        try { build.Load(); }
        catch (Exception exception) when (IsMissingOptionalInstallManifest(exception, build)) { }
        if (build.Root is null || build.Encoding is null || build.GroupIndex is null)
            throw new InvalidDataException("The local CASC root, encoding table, or group index did not load.");
        return new(storagePath, build);
    }

    public IReadOnlyList<CascFileEntry> ListFiles(string listfilePath, string mask, CancellationToken cancellationToken)
    {
        if (_build.Root is null || _build.Encoding is null) throw new InvalidOperationException("The local CASC root is not loaded.");
        var result = new List<CascFileEntry>();
        var identities = new HashSet<(uint FileDataId, string Path)>();
        foreach (var line in File.ReadLines(listfilePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileDataIdListfileService.TryParseMapping(line, out var mapping)) continue;
            string path;
            try { path = PatchInputMapper.NormalizeArchivePath(mapping.ClientPath); }
            catch (ArgumentException) { continue; }
            if (!Matches(path, mask) || !identities.Add((mapping.FileDataId, path))) continue;
            var rootEntries = _build.Root.GetEntriesByFDID(mapping.FileDataId);
            if (rootEntries.Count == 0) continue;
            var root = rootEntries[0];
            var contentKey = root.md5.AsSpan().ToArray();
            var encoded = _build.Encoding.FindContentKey(contentKey);
            byte[]? selectedKey = null;
            var available = false;
            for (var index = 0; index < encoded.Length; index++)
            {
                var candidate = encoded[index].ToArray();
                selectedKey ??= candidate;
                if (!_localIndex.Contains(candidate)) continue;
                selectedKey = candidate;
                available = true;
                break;
            }
            result.Add(new(path, checked((long)encoded.DecodedFileSize), mapping.FileDataId,
                (uint)root.localeFlags, (uint)root.contentFlags, available, CascEntryNameType.FullPath,
                Convert.ToHexString(contentKey), selectedKey is null ? string.Empty : Convert.ToHexString(selectedKey)));
        }
        return result.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.FileDataId).ToArray();
    }

    public void Extract(string destinationRoot, IReadOnlyList<CascFileEntry> entries,
        IProgress<(int Done, int Total, string Path)>? progress, CancellationToken cancellationToken, bool overwriteExisting)
    {
        destinationRoot = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var internalPath = PatchInputMapper.NormalizeArchivePath(entry.ArchivePath);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, internalPath.Replace('\\', Path.DirectorySeparatorChar)));
            CascArchiveService.EnsureDescendant(destinationRoot, destination, internalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!overwriteExisting && File.Exists(destination))
            {
                progress?.Report((index + 1, entries.Count, internalPath));
                continue;
            }
            if (!entry.IsAvailableLocally) throw new FileNotFoundException($"CASC file is present in the root but is not stored in this installation: {internalPath}");
            var bytes = entry.EncodedKey.Length > 0
                ? _build.OpenFileByEKey(Convert.FromHexString(entry.EncodedKey), checked((ulong)entry.Size))
                : _build.OpenFileByFDID(entry.FileDataId);
            WriteAtomically(destination, bytes, overwriteExisting);
            progress?.Report((index + 1, entries.Count, internalPath));
        }
    }

    internal static (string BuildConfig, string CdnConfig) FindConfigs(string storagePath)
    {
        var configRoot = Path.Combine(storagePath, "Data", "config");
        if (!Directory.Exists(configRoot)) throw new DirectoryNotFoundException($"CASC config folder not found: {configRoot}");
        var candidates = Directory.EnumerateFiles(configRoot, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Keys: ReadConfigKeys(path))).ToArray();
        return (FindSingle(candidates, ["root", "encoding"], "build"), FindSingle(candidates, ["archives"], "CDN"));
    }

    private static string FindSingle((string Path, HashSet<string> Keys)[] candidates, string[] requiredKeys, string label)
    {
        var matches = candidates.Where(candidate => requiredKeys.All(candidate.Keys.Contains)).Select(candidate => candidate.Path).ToArray();
        if (matches.Length == 0) throw new FileNotFoundException($"No local {label} config contains: {string.Join(", ", requiredKeys)}");
        if (matches.Length > 1) throw new InvalidDataException($"Multiple local {label} configs match this installation; Crucible will not guess: {string.Join(" | ", matches)}");
        return matches[0];
    }

    private static HashSet<string> ReadConfigKeys(string path)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            if (key.Length > 0) keys.Add(key);
        }
        return keys;
    }

    private static bool IsMissingOptionalInstallManifest(Exception exception, BuildInstance build) =>
        build.Root is not null && build.Encoding is not null && build.GroupIndex is not null &&
        (exception is FileNotFoundException || exception.Message.Contains("install", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Equals("No root key found in build config", StringComparison.Ordinal));

    private static bool Matches(string path, string mask) => string.IsNullOrWhiteSpace(mask) || mask == "*" || MpqPathFilter.Matches(path, mask);

    private static void WriteAtomically(string destination, byte[] bytes, bool overwriteExisting)
    {
        var temporary = destination + $".{Environment.ProcessId}.{Guid.NewGuid():N}.extracting";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            File.Move(temporary, destination, overwriteExisting);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private sealed class LocalCascIndex
    {
        private readonly string _dataRoot;
        private readonly Dictionary<byte, CASCIndexInstance> _indices = [];

        public LocalCascIndex(string storagePath)
        {
            _dataRoot = Path.Combine(storagePath, "Data", "data");
            if (!Directory.Exists(_dataRoot)) throw new DirectoryNotFoundException($"Local CASC data folder not found: {_dataRoot}");
            var selected = new Dictionary<byte, (int Version, string Path)>();
            foreach (var path in Directory.EnumerateFiles(_dataRoot, "*.idx", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Contains("tempfile", StringComparison.OrdinalIgnoreCase) || name.Length < 3 ||
                    !byte.TryParse(name.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bucket) ||
                    !int.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var version)) continue;
                if (!selected.TryGetValue(bucket, out var current) || version > current.Version) selected[bucket] = (version, path);
            }
            foreach (var pair in selected) _indices[pair.Key] = new(pair.Value.Path);
            if (_indices.Count == 0) throw new InvalidDataException("No valid local CASC .idx files were found.");
        }

        public bool Contains(byte[] encodedKey)
        {
            if (encodedKey.Length < 9) return false;
            var xor = encodedKey[0] ^ encodedKey[1] ^ encodedKey[2] ^ encodedKey[3] ^ encodedKey[4] ^ encodedKey[5] ^ encodedKey[6] ^ encodedKey[7] ^ encodedKey[8];
            var bucket = (byte)((xor & 0xF) ^ (xor >> 4));
            if (!_indices.TryGetValue(bucket, out var index)) return false;
            var (offset, size, archiveIndex) = index.GetIndexInfo(encodedKey);
            if (offset < 0 || size < 0 || archiveIndex < 0) return false;
            var archive = Path.Combine(_dataRoot, $"data.{archiveIndex:000}");
            return File.Exists(archive) && (long)offset + size <= new FileInfo(archive).Length;
        }
    }
}
