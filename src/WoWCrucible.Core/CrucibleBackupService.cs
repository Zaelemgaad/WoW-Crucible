using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

/// <summary>Central, visible and bounded storage for user-requested safety copies.</summary>
public static class CrucibleBackupService
{
    private static readonly object Gate = new();
    private static string _rootPath = CruciblePaths.BackupDirectory;
    private static bool _enabled = true;
    private static int _retentionPerSource = 3;
    private static long _maximumTotalBytes = 10L * 1024 * 1024 * 1024;
    private static string _lastDecision = "No backup operation has run.";

    public static string RootPath { get { lock (Gate) return _rootPath; } }
    public static bool Enabled { get { lock (Gate) return _enabled; } }
    public static int RetentionPerSource { get { lock (Gate) return _retentionPerSource; } }
    public static long MaximumTotalBytes { get { lock (Gate) return _maximumTotalBytes; } }
    public static string LastDecision { get { lock (Gate) return _lastDecision; } }

    public static void Configure(string? rootPath, bool enabled = true, int retentionPerSource = 3, long maximumTotalBytes = 10L * 1024 * 1024 * 1024)
    {
        if (retentionPerSource is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(retentionPerSource), "Backup retention must be between 1 and 100.");
        if (maximumTotalBytes < 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes), "Backup storage limit must be at least 1 MiB.");
        lock (Gate)
        {
            _rootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath) ? CruciblePaths.BackupDirectory : rootPath);
            _enabled = enabled;
            _retentionPerSource = retentionPerSource;
            _maximumTotalBytes = maximumTotalBytes;
        }
    }

    public static string? Create(string sourcePath, string category)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        lock (Gate)
        {
            if (!File.Exists(sourcePath)) { _lastDecision = $"No source file existed at {sourcePath}."; return null; }
            if (!_enabled) { _lastDecision = "Retained backups are disabled."; return null; }
            var sourceLength = new FileInfo(sourcePath).Length;
            if (sourceLength > _maximumTotalBytes) { _lastDecision = $"Skipped backup: the {FormatBytes(sourceLength)} source is larger than the configured {FormatBytes(_maximumTotalBytes)} backup storage ceiling."; return null; }

            var sourceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToUpperInvariant())))[..12].ToLowerInvariant();
            var folder = Path.Combine(_rootPath, Safe(category), $"{Safe(Path.GetFileNameWithoutExtension(sourcePath))}-{sourceKey}");
            Directory.CreateDirectory(folder);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.bak", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetCreationTimeUtc).ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase).Skip(Math.Max(0, _retentionPerSource - 1)))
                File.Delete(stale);
            var used = Directory.Exists(_rootPath) ? Directory.EnumerateFiles(_rootPath, "*.bak", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0;
            if (used + sourceLength > _maximumTotalBytes) { _lastDecision = $"Skipped backup: {FormatBytes(used)} of the {FormatBytes(_maximumTotalBytes)} backup storage ceiling is already in use."; return null; }

            var extension = Path.GetExtension(sourcePath);
            var destination = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}{extension}.bak");
            File.Copy(sourcePath, destination, false);
            _lastDecision = $"Created {destination}.";
            return destination;
        }
    }

    public static string CreateTransactionSnapshot(string sourcePath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        Directory.CreateDirectory(CruciblePaths.TransactionDirectory);
        var snapshot = Path.Combine(CruciblePaths.TransactionDirectory, $"{Safe(Path.GetFileName(sourcePath))}-{Guid.NewGuid():N}.tmp");
        File.Copy(sourcePath, snapshot, false);
        return snapshot;
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "General" : safe;
    }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB" : $"{bytes / (1024d * 1024):0.##} MiB";
}
