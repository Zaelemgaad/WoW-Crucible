using System.Security.Cryptography;

namespace WoWCrucible.Core;

/// <summary>A working copy of one archive member, retaining its path and locale for explicit saves.</summary>
public sealed class MpqDocumentSource : IDisposable
{
    private readonly PatchArchiveService _archives = new();
    private readonly string _directory;
    private byte[] _savedHash;
    public string ArchivePath { get; }
    public MpqFileEntry Entry { get; }
    public string WorkingPath { get; }
    public string DisplayPath => $"{ArchivePath} → {Entry.ArchivePath} ({MpqLocale.Token(Entry.Locale)})";

    public MpqDocumentSource(string archivePath, MpqFileEntry entry)
    {
        ArchivePath = Path.GetFullPath(archivePath);
        Entry = entry;
        _directory = Path.Combine(CruciblePaths.CacheDirectory, "MpqEdit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        WorkingPath = Path.Combine(_directory, Path.GetFileName(entry.ArchivePath.Replace('\\', '/')));
        try
        {
            _archives.ExtractFile(ArchivePath, WorkingPath, entry);
            _savedHash = Hash(WorkingPath);
        }
        catch { Dispose(); throw; }
    }

    public void Save(WdbcFile file)
    {
        // Check the member, not the whole archive: other open documents may have saved independently.
        var current = _archives.ListFiles(ArchivePath).SingleOrDefault(candidate =>
            candidate.Locale == Entry.Locale && candidate.ArchivePath.Equals(Entry.ArchivePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("This MPQ member was removed or renamed. Reopen it before saving.");
        var checkPath = Path.Combine(_directory, "current-entry.check");
        try
        {
            _archives.ExtractFile(ArchivePath, checkPath, current);
            if (!_savedHash.SequenceEqual(Hash(checkPath)))
                throw new IOException("This MPQ member changed since it was opened. Save As a separate file to retain your edits, then reopen the archive entry.");
            file.Save(WorkingPath, createBackup: false, publish: path =>
            {
                var savedHash = Hash(path);
                _archives.Update(ArchivePath, [new PatchEntry(path, Entry.ArchivePath, Entry.Locale)]);
                _savedHash = savedHash;
            });
        }
        finally { File.Delete(checkPath); }
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
