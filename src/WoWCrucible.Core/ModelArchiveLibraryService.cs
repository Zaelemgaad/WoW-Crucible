using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record ModelArchiveLibraryResult(int FilesScanned, int LinkedFiles, long ReclaimedBytes,
    int ExtractedArchives, int ReusedArchives, int AddedFiles, long AddedBytes, IReadOnlyList<string> Errors);

/// <summary>Unpacks model collections without flattening paths, overwriting variants or duplicating identical payloads.</summary>
public sealed class ModelArchiveLibraryService
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".mpq" };
    private readonly Dictionary<(long Length, string Hash), List<string>> _contents = [];
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = [];
    private readonly Action<string>? _progress;
    private readonly CancellationToken _cancellation;
    private string _root = "", _state = "", _sevenZip = "";
    private int _scanned, _linked, _extracted, _reused, _added;
    private long _reclaimed, _addedBytes;
    private sealed record ArchiveReceipt(string Archive, string Hash, string Destination, Dictionary<string, string> Files);
    private sealed class StorageBudgetException(long required, long available) : IOException(
        $"Extraction needs {required / 1048576d:F1} MiB of temporary space, exceeding the reclaimed-space budget {available / 1048576d:F1} MiB.")
    { public long RequiredBytes { get; } = required; }

    public ModelArchiveLibraryService(Action<string>? progress = null, CancellationToken cancellationToken = default)
    { _progress = progress; _cancellation = cancellationToken; }

    public ModelArchiveLibraryResult Expand(string root, string stateDirectory, string sevenZipPath, bool deleteVerifiedArchives = false)
    {
        if (_root.Length != 0) throw new InvalidOperationException("Create a new library operation to start or resume another run.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Model library hardlink cleanup currently requires Windows.");
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _state = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar);
        _sevenZip = Path.GetFullPath(sevenZipPath);
        if (!Directory.Exists(_root) || _root == Path.GetPathRoot(_root)?.TrimEnd(Path.DirectorySeparatorChar)) throw new ArgumentException("Select a model collection directory, not a drive root.");
        if (Inside(_root, _state) || Inside(_state, _root)) throw new ArgumentException("Operation state must be outside the collection.");
        if (!File.Exists(_sevenZip)) throw new FileNotFoundException("7-Zip/NanaZip executable not found.", _sevenZip);
        EnsureNoReparseAncestors(_root); EnsureNoReparseAncestors(_state);
        Directory.CreateDirectory(_state);
        using var operationLock = new FileStream(Path.Combine(_state, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var budgetPath = Path.Combine(_state, "storage-budget.json");
        if (File.Exists(budgetPath))
        {
            using var prior = JsonDocument.Parse(File.ReadAllText(budgetPath));
            if (prior.RootElement.GetProperty("Root").GetString() != _root) throw new InvalidDataException("Storage budget belongs to another collection.");
            _reclaimed = prior.RootElement.GetProperty("ReclaimedBytes").GetInt64();
            _addedBytes = prior.RootElement.GetProperty("AddedBytes").GetInt64();
        }
        var files = EnumerateSafe(_root).OrderBy(path => path.Contains("Prefered", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        _progress?.Invoke($"Hashing and deduplicating {files.Length:N0} files.");
        foreach (var path in files)
        {
            _cancellation.ThrowIfCancellationRequested();
            try { IndexAndDeduplicate(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { Error(path, exception.Message, exception); }
            _scanned++;
            if (_scanned % 4096 == 0) _progress?.Invoke($"Files {_scanned:N0}/{files.Length:N0}; {_linked:N0} linked; storage credit {(_reclaimed - _addedBytes) / 1073741824d:F2} GiB.");
        }
        SaveBudget();
        var receiptsPath = Path.Combine(_state, "archives.jsonl");
        var completed = File.Exists(receiptsPath) ? File.ReadLines(receiptsPath).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<ArchiveReceipt>(line)!).GroupBy(receipt => receipt.Hash).ToDictionary(group => group.Key, group => group.Last()) : [];
        var archives = new Queue<string>(files.Where(IsArchive).OrderBy(path => completed.ContainsKey(Hash(path)) ? 0 : 1));
        var deferred = new List<(string Path, StorageBudgetException Error)>();
        while (archives.Count > 0 || RetryDeferred())
        {
            var archive = archives.Dequeue();
            _cancellation.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(archive)) continue;
                var hash = Hash(archive);
                if (completed.TryGetValue(hash, out var prior) && prior.Files.All(pair => File.Exists(pair.Key) && Hash(pair.Key, true) == pair.Value))
                {
                    foreach (var nested in prior.Files.Keys.Where(IsArchive)) archives.Enqueue(nested);
                    if (deleteVerifiedArchives) DeleteVerifiedArchive(archive, hash, prior);
                    _reused++; continue;
                }
                _progress?.Invoke($"Extracting {_extracted + 1:N0}: {Path.GetRelativePath(_root, archive)}");
                var receipt = ExpandArchive(archive, hash);
                Append(receiptsPath, receipt); completed[hash] = receipt; _extracted++;
                foreach (var nested in receipt.Files.Keys.Where(IsArchive)) archives.Enqueue(nested);
                if (deleteVerifiedArchives) DeleteVerifiedArchive(archive, hash, receipt);
            }
            catch (StorageBudgetException exception) { deferred.Add((archive, exception)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
            { Error(archive, exception.Message, exception); }
        }
        foreach (var item in deferred) Error(item.Path, item.Error.Message);
        SaveBudget();
        var result = new ModelArchiveLibraryResult(_scanned, _linked, _reclaimed, _extracted, _reused, _added, _addedBytes, _errors);
        File.WriteAllText(Path.Combine(_state, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;

        bool RetryDeferred()
        {
            var ready = deferred.Where(item => item.Error.RequiredBytes <= _reclaimed - _addedBytes).ToArray();
            foreach (var item in ready) { deferred.Remove(item); archives.Enqueue(item.Path); }
            return ready.Length > 0;
        }
    }

    private void DeleteVerifiedArchive(string archive, string hash, ArchiveReceipt receipt)
    {
        // MPQs are also client-installable assets; unpacking them does not authorize removing the patch.
        if (Path.GetExtension(archive).Equals(".mpq", StringComparison.OrdinalIgnoreCase)) return;
        EnsureContained(_root, archive);
        foreach (var pair in receipt.Files)
        {
            EnsureContained(_root, pair.Key);
            if (!File.Exists(pair.Key) || Hash(pair.Key, true) != pair.Value)
                throw new IOException("Extracted content changed; archive retained: " + pair.Key);
        }
        if (Hash(archive, true) != hash) throw new IOException("Source archive changed; archive retained.");
        var identity = NativeHardLinks.ReadIdentity(archive);
        var length = new FileInfo(archive).Length;
        Append(Path.Combine(_state, "removed-archives.jsonl"), new { Archive = archive, Hash = hash, receipt.Destination, Bytes = length });
        File.Delete(archive);
        if (identity.LinkCount == 1) _reclaimed += length;
        _hashes.Remove(archive);
        SaveBudget();
    }

    private void IndexAndDeduplicate(string path)
    {
        var info = new FileInfo(path);
        if (info.Length == 0) return;
        var hash = Hash(path); var key = (info.Length, hash);
        if (!_contents.TryGetValue(key, out var candidates)) _contents.Add(key, candidates = []);
        var identity = NativeHardLinks.ReadIdentity(path);
        foreach (var canonical in candidates)
        {
            if (!File.Exists(canonical)) continue;
            var other = NativeHardLinks.ReadIdentity(canonical);
            if (identity.Key == other.Key) return;
            if (identity.VolumeSerial != other.VolumeSerial || other.LinkCount >= 1000) continue;
            if (!SameAlternateStreams(path, canonical)) continue;
            EnsureContained(_root, path); EnsureContained(_root, canonical);
            using (var left = OpenProtected(path))
            using (var right = OpenProtected(canonical))
                if (!SameBytes(left, right) || Digest(left) != hash) throw new IOException("File changed before duplicate replacement.");
            if (NativeHardLinks.ReadIdentity(path).Key != identity.Key || NativeHardLinks.ReadIdentity(canonical).Key != other.Key) throw new IOException("File identity changed before duplicate replacement.");
            Append(Path.Combine(_state, "links.jsonl"), new { Target = path, Canonical = canonical, Hash = hash, Bytes = info.Length, OriginalIdentity = identity.Key });
            NativeHardLinks.ReplaceWithHardLink(path, canonical);
            _linked++; if (identity.LinkCount == 1) _reclaimed += info.Length;
            SaveBudget();
            return;
        }
        candidates.Add(path);
    }

    private ArchiveReceipt ExpandArchive(string archive, string hash)
    {
        var mpq = Path.GetExtension(archive).Equals(".mpq", StringComparison.OrdinalIgnoreCase) ? new PatchArchiveService() : null;
        var mpqEntries = mpq?.ListFiles(archive);
        if (mpqEntries?.GroupBy(entry => ModelBrowserSource.Normalize(entry.ArchivePath), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) == true)
            throw new InvalidDataException("MPQ has same-path locale variants; use MPQ extraction with locale preservation for this archive.");
        var entries = mpqEntries?.ToDictionary(entry => ModelBrowserSource.Normalize(entry.ArchivePath), entry => (long)entry.Size, StringComparer.OrdinalIgnoreCase)
            ?? ReadEntries(RunSevenZip("l", "-slt", "-ba", "-sccUTF-8", "--", archive));
        var total = entries.Values.Sum();
        if (total > _reclaimed - _addedBytes) throw new StorageBudgetException(total, _reclaimed - _addedBytes);
        var stage = Path.Combine(_state, "stage-" + hash[..16].ToLowerInvariant());
        EnsureContained(_state, stage);
        if (Directory.Exists(stage)) throw new IOException("A previous extraction staging folder remains; inspect it before resuming.");
        Directory.CreateDirectory(stage);
        try
        {
            if (mpq is not null) mpq.Extract(archive, stage, mpqEntries!, cancellationToken: _cancellation, overwriteExisting: false);
            else RunSevenZip("x", "-y", "-aoa", "-bb0", "-bsp0", "-sccUTF-8", "-p", "-o" + stage, "--", archive);
            var extracted = EnumerateSafe(stage).ToArray();
            if (extracted.Length != entries.Count) throw new InvalidDataException($"Archive advertised {entries.Count:N0} files but extracted {extracted.Length:N0}.");
            foreach (var path in extracted)
            {
                var relative = Path.GetRelativePath(stage, path).Replace('\\', '/');
                if (!entries.TryGetValue(relative, out var length) || new FileInfo(path).Length != length) throw new InvalidDataException("Extracted file differs from the archive inventory: " + relative);
            }
            if (Hash(archive, true) != hash) throw new IOException("Source archive changed during extraction.");
            var destination = Path.ChangeExtension(archive, null);
            EnsureContained(_root, destination);
            var staged = extracted.Select(path => (Path: path, Relative: Path.GetRelativePath(stage, path), Length: new FileInfo(path).Length, Hash: Hash(path, true))).ToArray();
            bool Conflicts(string candidate) => File.Exists(candidate) || staged.Any(file => Directory.Exists(Path.Combine(candidate, file.Relative)) || File.Exists(Path.Combine(candidate, file.Relative)) && Hash(Path.Combine(candidate, file.Relative)) != file.Hash);
            if (Conflicts(destination)) destination += ".extracted-" + hash[..12].ToLowerInvariant();
            if (Conflicts(destination)) throw new IOException("Both extraction destinations contain different content; nothing will be overwritten.");
            EnsureContained(_root, destination); Directory.CreateDirectory(destination);
            var published = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in staged)
            {
                _cancellation.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(destination, file.Relative));
                EnsureContained(_root, target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    using var existing = OpenProtected(target); using var pending = OpenProtected(file.Path);
                    if (!SameBytes(existing, pending)) throw new IOException("Destination changed before extraction publication: " + target);
                }
                else
                {
                    var key = (file.Length, file.Hash); string? canonical = null;
                    if (_contents.TryGetValue(key, out var candidates))
                        canonical = candidates.FirstOrDefault(path => File.Exists(path) && NativeHardLinks.ReadIdentity(path).LinkCount < 1000 && SameAlternateStreams(path, file.Path));
                    if (canonical is not null)
                    {
                        using (var existing = OpenProtected(canonical))
                        using (var pending = OpenProtected(file.Path))
                            if (!SameBytes(existing, pending)) throw new IOException("Canonical content changed during extraction: " + canonical);
                        NativeHardLinks.Create(target, canonical);
                    }
                    else
                    {
                        File.Move(file.Path, target);
                        _addedBytes += file.Length;
                        SaveBudget();
                        if (!_contents.TryGetValue(key, out var list)) _contents.Add(key, list = []);
                        list.Add(target);
                    }
                    _added++;
                    _hashes[target] = file.Hash;
                }
                published.Add(target, file.Hash);
                if (_added % 128 == 0) SaveBudget();
            }
            SaveBudget();
            return new(archive, hash, destination, published);
        }
        finally
        {
            // Only generated extraction scratch is removed; original archives and published files are retained.
            EnsureContained(_state, stage);
            EnsureNoReparseAncestors(stage);
            if (Directory.Exists(stage))
            {
                foreach (var path in EnumerateSafe(stage)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                foreach (var path in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.SetAttributes(stage, File.GetAttributes(stage) & ~FileAttributes.ReadOnly);
                Directory.Delete(stage, true);
            }
        }
    }

    internal static Dictionary<string, long> ReadEntries(string listing)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var record = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in listing.Replace("\r", "").Split('\n').Append(""))
        {
            if (line.Length == 0) { Add(); record.Clear(); continue; }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator > 0) record[line[..separator]] = line[(separator + 3)..];
        }
        return result;
        void Add()
        {
            if (!record.TryGetValue("Path", out var name)) return;
            var directory = record.GetValueOrDefault("Folder") == "+" || record.GetValueOrDefault("Attributes", "").Split(' ')[0].Contains('D');
            if (directory && name is "" or "." or "/") return;
            name = ModelBrowserSource.Normalize(name);
            if (name.Split('/').Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' '))) throw new InvalidDataException("Unsafe archive entry: " + name);
            if (!string.IsNullOrEmpty(record.GetValueOrDefault("Symbolic Link")) || !string.IsNullOrEmpty(record.GetValueOrDefault("Hard Link")) || !string.IsNullOrEmpty(record.GetValueOrDefault("Copy Link")) || record.GetValueOrDefault("Attributes", "").Contains('L') || record.GetValueOrDefault("Alternate Stream") == "+") throw new InvalidDataException("Archive links and alternate streams are not extracted: " + name);
            if (directory) return;
            if (!record.TryGetValue("Size", out var size) || !long.TryParse(size, out var length) || length < 0) throw new InvalidDataException("Missing file size for archive entry: " + name);
            if (!result.TryAdd(name, length)) throw new InvalidDataException("Ambiguous duplicate archive path: " + name);
        }
    }

    private string RunSevenZip(params string[] arguments)
    {
        var start = new ProcessStartInfo(_sevenZip) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start 7-Zip/NanaZip.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(_cancellation); var error = process.StandardError.ReadToEndAsync(_cancellation);
        try { process.WaitForExitAsync(_cancellation).GetAwaiter().GetResult(); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var stdout = output.GetAwaiter().GetResult(); var stderr = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException($"7-Zip returned {process.ExitCode}: {stderr.Trim()} {stdout.Trim()}");
        return stdout;
    }

    private IEnumerable<string> EnumerateSafe(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            _cancellation.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse point in model collection; review before processing: " + path);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path); else yield return path;
            }
        }
    }

    private string Hash(string path, bool fresh = false)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (!fresh && _hashes.TryGetValue(path, out var hash)) return hash;
        using var file = OpenProtected(path);
        hash = Digest(file); _hashes[path] = hash; return hash;
    }
    private static string Digest(FileStream stream) { stream.Position = 0; return Convert.ToHexString(SHA256.HashData(stream)); }
    private static FileStream OpenProtected(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
    private static bool SameBytes(FileStream left, FileStream right)
    {
        if (left.Length != right.Length) return false;
        left.Position = right.Position = 0;
        var a = ArrayPool<byte>.Shared.Rent(1024 * 1024); var b = ArrayPool<byte>.Shared.Rent(a.Length);
        try
        {
            int count;
            while ((count = left.Read(a)) > 0) { if (right.ReadAtLeast(b, count, false) != count || !a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count))) return false; }
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(a); ArrayPool<byte>.Shared.Return(b); }
    }
    private static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));
    private static bool SameAlternateStreams(string left, string right)
    {
        var names = NativeHardLinks.ReadAlternateStreamNames(left).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var others = NativeHardLinks.ReadAlternateStreamNames(right).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!names.SequenceEqual(others, StringComparer.OrdinalIgnoreCase)) return false;
        foreach (var name in names)
        {
            using var a = OpenProtected(left + name); using var b = OpenProtected(right + name);
            if (!SameBytes(a, b)) return false;
        }
        return true;
    }
    private static bool Inside(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void EnsureContained(string root, string path)
    {
        path = Path.GetFullPath(path);
        if (!Inside(root, path) || path.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path leaves the permitted directory: " + path);
        EnsureNoReparseAncestors(path);
    }
    private static void EnsureNoReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing a reparse-point path: " + current);
    }
    private void Error(string path, string message, Exception? exception = null)
    {
        var error = path + ": " + message; _errors.Add(error); _progress?.Invoke("REVIEW " + error);
        Append(Path.Combine(_state, "errors.jsonl"), new { Path = path, Error = message, Exception = exception?.ToString() });
    }
    private void SaveBudget()
    {
        var path = Path.Combine(_state, "storage-budget.json"); var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { Root = _root, ReclaimedBytes = _reclaimed, AddedBytes = _addedBytes })); File.Move(temporary, path, true);
    }
    private static void Append<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n")); stream.Flush(true);
    }
}
