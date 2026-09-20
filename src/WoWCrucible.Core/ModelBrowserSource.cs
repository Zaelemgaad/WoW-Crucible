using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record ModelBrowserEntry(string FilePath, string? ArchiveEntry, string RelativePath, long Length,
    string Format, uint? Version, string? Error)
{
    public IReadOnlyList<ModelBrowserEntry> Copies { get; init; } = [];
    [JsonIgnore]
    public IEnumerable<ModelBrowserEntry> Locations => new[] { this }.Concat(Copies);
    public string Name => Path.GetFileName(ArchiveEntry ?? FilePath);
    public string Identity => FilePath + "|" + ArchiveEntry;
    public string Container => ArchiveEntry is null ? "Folder" : "ZIP";
    public string FormatLabel => Version is null ? Format : $"{Format} / {Version}";
}

public sealed record ModelBrowserCatalog(string Root, IReadOnlyList<ModelBrowserEntry> Models,
    IReadOnlyList<string> UnopenedArchives, IReadOnlyList<string> Errors)
{
    public IReadOnlyList<string> Files { get; init; } = [];
    private IReadOnlyDictionary<string, string>? _fileIndex;
    internal IReadOnlyDictionary<string, string> FileIndex => _fileIndex ??= Files.ToDictionary(
        path => ModelBrowserSource.Normalize(Path.GetRelativePath(Root, path)), StringComparer.OrdinalIgnoreCase);
    private ILookup<string, string>? _folders;
    internal ILookup<string, string> Folders => _folders ??= FileIndex.Keys.ToLookup(ModelBrowserSource.DirectoryName, StringComparer.OrdinalIgnoreCase);
    private ILookup<string, string>? _names;
    internal ILookup<string, string> Names => _names ??= FileIndex.Keys.ToLookup(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    private ILookup<(uint Id, string Extension), string>? _fileDataIds;
    internal ILookup<(uint Id, string Extension), string> FileDataIds => _fileDataIds ??= FileIndex.Keys.ToLookup(ModelBrowserSource.FileDataIdKey);
    internal Dictionary<uint, List<string>>? SkeletonOwners { get; set; }
    public int SourceModelCount => Models.Sum(entry => 1 + entry.Copies.Count);
}

/// <summary>Read-only model discovery. ZIP entries are streamed, never extracted into the user's collection.</summary>
public static class ModelBrowserCatalogService
{
    public static ModelBrowserCatalog Scan(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var models = new List<ModelBrowserEntry>(); var archives = new List<string>(); var errors = new List<string>();
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(path);
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    var relative = Path.GetRelativePath(root, path);
                    try
                    {
                        if (extension == ".m2")
                        {
                            using var file = File.OpenRead(path);
                            models.Add(ReadHeader(path, null, relative, file.Length, file));
                        }
                        else if (extension == ".zip")
                        {
                            using var zip = ZipFile.OpenRead(path);
                            foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                using var stream = entry.Open();
                                models.Add(ReadHeader(path, entry.FullName, relative + " :: " + entry.FullName, entry.Length, stream));
                            }
                        }
                        else if (extension is ".rar" or ".7z" or ".mpq") archives.Add(relative);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                    { errors.Add(relative + ": " + exception.Message); }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { errors.Add(directory + ": " + exception.Message); }
        }
        var catalog = new ModelBrowserCatalog(root, models, archives, errors) { Files = files };
        return catalog with { Models = ModelBrowserDuplicateService.Group(catalog, cancellationToken) };
    }

    private static ModelBrowserEntry ReadHeader(string path, string? entry, string relative, long length, Stream stream)
    {
        var header = new byte[20]; var count = stream.ReadAtLeast(header, header.Length, false);
        if (count < 8) return new(path, entry, relative, length, "Invalid", null, "Truncated model header.");
        var magic = Encoding.ASCII.GetString(header, 0, 4);
        uint? version = magic switch
        {
            "MD20" => BitConverter.ToUInt32(header, 4),
            "MD21" when count >= 16 && Encoding.ASCII.GetString(header, 8, 4) == "MD20" => BitConverter.ToUInt32(header, 12),
            _ => null
        };
        return new(path, entry, relative, length, magic, version, version is null ? "Unrecognized model header." : null);
    }
}

/// <summary>A bounded, case-insensitive source for one model and its companions, including ZIP-contained models.</summary>
public sealed class ModelBrowserSource : IDisposable
{
    private const int MaximumFileBytes = 256 * 1024 * 1024;
    private readonly ZipArchive? _zip;
    private readonly object _readLock = new();
    private bool _disposed;
    private readonly IReadOnlyDictionary<string, string> _files;
    private readonly ModelBrowserCatalog? _catalog;
    private ILookup<string, string>? _folders;
    private ILookup<string, string>? _names;
    private ILookup<(uint Id, string Extension), string>? _fileDataIds;
    private Dictionary<uint, List<string>>? _skeletonOwners;
    public ModelBrowserEntry Entry { get; }
    public string ModelName { get; }
    public string ModelDirectory { get; }
    public IEnumerable<string> Files => _files.Keys;

    public ModelBrowserSource(ModelBrowserEntry entry, ModelBrowserCatalog? catalog = null)
    {
        Entry = entry;
        _catalog = entry.ArchiveEntry is null ? catalog : null;
        if (entry.ArchiveEntry is { } archiveEntry)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _files = files;
            _zip = ZipFile.OpenRead(entry.FilePath);
            try
            {
                foreach (var file in _zip.Entries.Where(file => !file.FullName.EndsWith('/')))
                {
                    var key = Normalize(file.FullName);
                    if (!files.TryAdd(key, file.FullName)) throw new InvalidDataException($"ZIP contains ambiguous duplicate path: {key}");
                }
                ModelName = Normalize(archiveEntry);
            }
            catch { _zip.Dispose(); throw; }
        }
        else
        {
            var root = catalog?.Root ?? Path.GetDirectoryName(entry.FilePath)!;
            ModelName = Normalize(Path.GetRelativePath(root, entry.FilePath));
            _files = catalog?.FileIndex ?? Directory.GetFiles(root).ToDictionary(file => Normalize(Path.GetRelativePath(root, file)), StringComparer.OrdinalIgnoreCase);
        }
        ModelDirectory = DirectoryName(ModelName);
    }

    public byte[] Read(string name)
    {
        // ZipArchive and its shared input stream cannot service overlapping texture reads.
        lock (_readLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            name = Normalize(name);
            if (!_files.TryGetValue(name, out var actual)) throw new FileNotFoundException("Model companion not found.", name);
            if (_zip is not null)
            {
                var entry = _zip.GetEntry(actual) ?? throw new FileNotFoundException(actual);
                if (entry.Length > MaximumFileBytes) throw new InvalidDataException($"Asset exceeds the {MaximumFileBytes / 1024 / 1024} MB preview limit: {name}");
                using var stream = entry.Open(); var data = new byte[checked((int)entry.Length)]; stream.ReadExactly(data); return data;
            }
            if (new FileInfo(actual).Length > MaximumFileBytes) throw new InvalidDataException($"Asset exceeds the preview size limit: {name}");
            return File.ReadAllBytes(actual);
        }
    }

    public string? Find(string name)
    {
        name = Normalize(name);
        var sibling = Join(ModelDirectory, name);
        if (_files.ContainsKey(sibling)) return sibling;
        if (_files.ContainsKey(name)) return name;
        var localLeaf = Join(ModelDirectory, Path.GetFileName(name));
        if (_files.ContainsKey(localLeaf)) return localLeaf;
        var suffix = "/" + name;
        var names = _catalog?.Names ?? (_names ??= _files.Keys.ToLookup(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase));
        var matches = names[Path.GetFileName(name)].Where(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public string? FindFileDataId(uint id, string extension)
    {
        if (id == 0) return null;
        var exact = Find(id + extension); if (exact is not null) return exact;
        var index = _catalog?.FileDataIds ?? (_fileDataIds ??= _files.Keys.ToLookup(FileDataIdKey));
        var matches = index[(id, extension.ToLowerInvariant())].ToArray();
        var local = matches.Where(path => DirectoryName(path).Equals(ModelDirectory, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (local.Length == 1) return local[0];
        if (matches.Length == 1) return matches[0];
        if (extension.Equals(".skel", StringComparison.OrdinalIgnoreCase))
        {
            var byId = _catalog?.SkeletonOwners ?? _skeletonOwners;
            if (byId is null)
            {
                byId = [];
                foreach (var candidate in _files.Keys.Where(path => path.EndsWith(".skel", StringComparison.OrdinalIgnoreCase)))
                {
                    var model = candidate[..^5] + ".m2";
                    if (!_files.ContainsKey(model)) continue;
                    try
                    {
                        var data = Read(model);
                        if (M2ModelSource.Magic(data) != "MD21") continue;
                        var chunks = M2ModelSource.Chunks(data);
                        if (!chunks.TryGetValue("SKID", out var skid)) continue;
                        var ownerId = M2ModelSource.U32(skid, 0);
                        if (!byId.TryGetValue(ownerId, out var paths)) byId.Add(ownerId, paths = []);
                        paths.Add(candidate);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                    { /* An unrelated unreadable model cannot establish ownership of this skeleton. */ }
                }
                _skeletonOwners = byId;
                if (_catalog is not null) _catalog.SkeletonOwners = byId;
            }
            var owners = byId.GetValueOrDefault(id) ?? [];
            if (owners.Count > 0)
            {
                var scored = owners.Select(path => (Path: path, Score: SharedFolders(path, ModelName))).ToArray();
                var best = scored.Where(value => value.Score == scored.Max(value => value.Score)).ToArray();
                if (best.Length == 1) return best[0].Path;
            }
        }
        return null;
    }

    private static int SharedFolders(string left, string right) => left.Split('/').Zip(right.Split('/')).TakeWhile(pair => pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase)).Count();

    internal static (uint Id, string Extension) FileDataIdKey(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return uint.TryParse(stem.AsSpan(stem.LastIndexOf('_') + 1), out var id) ? (id, Path.GetExtension(path).ToLowerInvariant()) : (0, "");
    }

    public IReadOnlyList<string> Nearby(string extension, string? directory = null) =>
        (_catalog?.Folders ?? (_folders ??= _files.Keys.ToLookup(DirectoryName, StringComparer.OrdinalIgnoreCase)))[directory ?? ModelDirectory]
        .Where(path => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string Normalize(string value)
    {
        value = value.Replace('\\', '/');
        if (value.StartsWith('/') || value.Contains(':') || value.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException($"Unsafe model asset path: {value}");
        return value;
    }

    internal static string DirectoryName(string value) => value.Contains('/') ? value[..value.LastIndexOf('/')] : string.Empty;
    internal static string Join(string directory, string file) => directory.Length == 0 ? file : directory + "/" + file;
    public void Dispose() { lock (_readLock) { _disposed = true; _zip?.Dispose(); } }
}
