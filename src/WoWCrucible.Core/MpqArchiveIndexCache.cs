using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record MpqArchiveIndexCacheResult(IReadOnlyList<MpqFileEntry> Entries, bool Cached, string CachePath);

public static class MpqArchiveIndexCache
{
    private const string Format = "wow-crucible-mpq-index-v2";
    private const int MaximumFiles = 64;
    private const long MaximumBytes = 512L * 1024 * 1024;
    private sealed record Identity(string ArchivePath, long ArchiveBytes, long ArchiveWriteTicks, string? ListfilePath, long? ListfileBytes, long? ListfileWriteTicks);
    private sealed record Document(string Format, Identity Identity, IReadOnlyList<MpqFileEntry> Entries);

    public static MpqArchiveIndexCacheResult LoadOrCreate(string archivePath, string? listfilePath, Func<IReadOnlyList<MpqFileEntry>> loader, CancellationToken cancellationToken = default) =>
        LoadOrCreate(archivePath, listfilePath, CruciblePaths.MpqIndexCacheDirectory, loader, cancellationToken);

    public static MpqArchiveIndexCacheResult LoadOrCreate(string archivePath, string? listfilePath, string cacheDirectory, Func<IReadOnlyList<MpqFileEntry>> loader, CancellationToken cancellationToken = default)
    {
        var identity = GetIdentity(archivePath, listfilePath); string cachePath;
        try { Directory.CreateDirectory(cacheDirectory); cachePath = Path.Combine(cacheDirectory, CacheKey(identity) + ".json.gz"); }
        catch { cancellationToken.ThrowIfCancellationRequested(); return new(loader(), false, cacheDirectory); }
        if (TryRead(cachePath, identity, cancellationToken, out var cached)) { try { File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow); } catch { } return new(cached, true, cachePath); }
        cancellationToken.ThrowIfCancellationRequested(); var entries = loader(); cancellationToken.ThrowIfCancellationRequested();
        try { WriteAtomically(cachePath, new(Format, identity, entries), cancellationToken); Prune(cacheDirectory, cachePath); }
        catch (OperationCanceledException) { throw; }
        catch { }
        return new(entries, false, cachePath);
    }

    private static Identity GetIdentity(string archivePath, string? listfilePath)
    {
        var archive = new FileInfo(Path.GetFullPath(archivePath)); if (!archive.Exists) throw new FileNotFoundException("MPQ archive not found.", archive.FullName); FileInfo? listfile = null;
        if (!string.IsNullOrWhiteSpace(listfilePath)) { listfile = new(Path.GetFullPath(listfilePath)); if (!listfile.Exists) throw new FileNotFoundException("External MPQ listfile not found.", listfile.FullName); }
        return new(archive.FullName, archive.Length, archive.LastWriteTimeUtc.Ticks, listfile?.FullName, listfile?.Length, listfile?.LastWriteTimeUtc.Ticks);
    }

    private static string CacheKey(Identity identity)
    {
        var input = $"{identity.ArchivePath.ToUpperInvariant()}\n{identity.ListfilePath?.ToUpperInvariant()}"; return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static bool TryRead(string path, Identity expected, CancellationToken cancellationToken, out IReadOnlyList<MpqFileEntry> entries)
    {
        entries = []; if (!File.Exists(path)) return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress); var document = JsonSerializer.Deserialize<Document>(gzip);
            if (document is null || document.Format != Format || document.Identity != expected || document.Entries is null) return false; entries = document.Entries; return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { try { File.Delete(path); } catch { } return false; }
    }

    private static void WriteAtomically(string path, Document document, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try { using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var gzip = new GZipStream(file, CompressionLevel.Fastest)) JsonSerializer.Serialize(gzip, document); cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Prune(string directory, string keep)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "*.json.gz").Select(path => new FileInfo(path)).OrderByDescending(file => file.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase)).ThenByDescending(file => file.LastWriteTimeUtc).ToArray(); long bytes = 0; var kept = 0;
            foreach (var file in files) { if (file.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase) || kept < MaximumFiles && bytes + file.Length <= MaximumBytes) { kept++; bytes += file.Length; } else file.Delete(); }
        }
        catch { }
    }
}
