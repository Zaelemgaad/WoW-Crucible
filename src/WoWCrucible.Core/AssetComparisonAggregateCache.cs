using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

internal sealed record AssetCatalogIdentity(long Length, long CreationTimeUtcTicks, long LastWriteTimeUtcTicks, string EdgeFingerprintSha256);
internal sealed record AssetComparisonAggregateDirectory(string LogicalPath, int PngFiles, string[] PngProvenanceSources, int M2Files, int SkinFiles);
internal sealed record AssetComparisonAggregateSidecar(
    int FormatVersion,
    string CatalogGeneration,
    DateTimeOffset GeneratedUtc,
    AssetCatalogIdentity CatalogIdentity,
    AssetComparisonAggregateDirectory[] Directories);

internal sealed class AssetComparisonAggregateBuilder
{
    private const string ArchivePrefix = "Archives\\Content\\";
    private const string LoosePrefix = "Loose\\Content\\";
    private readonly Dictionary<string, MutableDirectory> _directories = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string relativePath, string format)
    {
        format = format.TrimStart('.');
        if (!format.Equals("PNG", StringComparison.OrdinalIgnoreCase) &&
            !format.Equals("M2", StringComparison.OrdinalIgnoreCase) &&
            !format.Equals("SKIN", StringComparison.OrdinalIgnoreCase)) return;
        if (!TryMap(relativePath, out var logicalPath, out var provenance)) return;

        if (!_directories.TryGetValue(logicalPath, out var directory))
            _directories[logicalPath] = directory = new MutableDirectory();
        if (format.Equals("PNG", StringComparison.OrdinalIgnoreCase))
        {
            directory.PngFiles++;
            directory.PngProvenanceSources.Add(provenance);
        }
        else if (format.Equals("M2", StringComparison.OrdinalIgnoreCase)) directory.M2Files++;
        else directory.SkinFiles++;
    }

    public AssetComparisonAggregateDirectory[] Build() => _directories
        .Select(pair => new AssetComparisonAggregateDirectory(
            pair.Key,
            pair.Value.PngFiles,
            pair.Value.PngProvenanceSources.OrderBy(source => source, StringComparer.OrdinalIgnoreCase).ToArray(),
            pair.Value.M2Files,
            pair.Value.SkinFiles))
        .OrderBy(directory => directory.LogicalPath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool TryMap(string relativePath, out string logicalPath, out string provenance)
    {
        relativePath = relativePath.Replace('/', '\\');
        if (relativePath.StartsWith(ArchivePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var parts = relativePath[ArchivePrefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                provenance = parts[^2];
                logicalPath = parts.Length == 2 ? string.Empty : string.Join('\\', parts[..^2]);
                return true;
            }
        }
        else if (relativePath.StartsWith(LoosePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var parts = relativePath[LoosePrefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                provenance = "Loose";
                logicalPath = parts.Length == 1 ? string.Empty : string.Join('\\', parts[..^1]);
                return true;
            }
        }

        logicalPath = string.Empty;
        provenance = string.Empty;
        return false;
    }

    private sealed class MutableDirectory
    {
        public int PngFiles { get; set; }
        public HashSet<string> PngProvenanceSources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int M2Files { get; set; }
        public int SkinFiles { get; set; }
    }
}

internal static class AssetComparisonAggregateCache
{
    internal const int CurrentFormatVersion = 1;
    internal const string FileName = "asset-comparison-index.json";
    private const int FingerprintBlockBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool TryLoad(string libraryRoot, string catalogPath, CancellationToken cancellationToken,
        out AssetComparisonAggregateDirectory[] directories)
    {
        directories = [];
        var sidecarPath = Path.Combine(libraryRoot, FileName);
        if (!File.Exists(sidecarPath) || !File.Exists(catalogPath)) return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var sidecar = JsonSerializer.Deserialize<AssetComparisonAggregateSidecar>(stream, JsonOptions);
            cancellationToken.ThrowIfCancellationRequested();
            if (sidecar is null || sidecar.FormatVersion != CurrentFormatVersion ||
                !Guid.TryParseExact(sidecar.CatalogGeneration, "N", out _) || sidecar.Directories is null ||
                sidecar.CatalogIdentity != CaptureIdentity(catalogPath, cancellationToken) || !ValidateDirectories(sidecar.Directories)) return false;
            directories = sidecar.Directories;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { return false; }
    }

    public static void TryWrite(string libraryRoot, string catalogPath, AssetComparisonAggregateDirectory[] directories, CancellationToken cancellationToken)
    {
        try { Write(libraryRoot, catalogPath, directories, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { }
    }

    public static void Write(string libraryRoot, string catalogPath, AssetComparisonAggregateDirectory[] directories, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sidecarPath = Path.Combine(libraryRoot, FileName);
        var temporaryPath = sidecarPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var sidecar = new AssetComparisonAggregateSidecar(
            CurrentFormatVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            CaptureIdentity(catalogPath, cancellationToken),
            directories);
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, sidecar, JsonOptions);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, sidecarPath, true);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    public static AssetComparisonAggregateDirectory[] ReadCatalog(string catalogPath, CancellationToken cancellationToken)
    {
        var builder = new AssetComparisonAggregateBuilder();
        using var reader = new StreamReader(catalogPath, Encoding.UTF8, true, 1024 * 1024);
        var header = reader.ReadLine() ?? throw new InvalidDataException("The asset catalog is empty.");
        var headerFields = ParseCsv(header);
        var requiredHeader = new[] { "category", "format", "source", "relative_path", "bytes" };
        if (headerFields.Count < requiredHeader.Length || !requiredHeader.Select((field, index) => headerFields[index].Equals(field, StringComparison.OrdinalIgnoreCase)).All(match => match))
            throw new InvalidDataException("The asset catalog header is not recognized.");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsv(line);
            if (fields.Count < 5) throw new InvalidDataException("The asset catalog contains a truncated row.");
            builder.Add(fields[3], fields[1]);
        }
        var directories = builder.Build();
        if (!ValidateDirectories(directories))
            throw new InvalidDataException("The asset catalog contains an unsafe or duplicate logical content path.");
        return directories;
    }

    public static AssetComparisonAggregateDirectory[] ScanFileSystem(string libraryRoot, IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        var builder = new AssetComparisonAggregateBuilder();
        foreach (var root in roots)
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file).TrimStart('.');
            if (!extension.Equals("PNG", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals("M2", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals("SKIN", StringComparison.OrdinalIgnoreCase)) continue;
            builder.Add(Path.GetRelativePath(libraryRoot, file), extension);
        }
        return builder.Build();
    }

    private static bool ValidateDirectories(IReadOnlyList<AssetComparisonAggregateDirectory> directories)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (directory is null || directory.LogicalPath is null || directory.PngProvenanceSources is null ||
                directory.PngFiles < 0 || directory.M2Files < 0 || directory.SkinFiles < 0 ||
                directory.PngProvenanceSources.Any(source => string.IsNullOrWhiteSpace(source) || source is "." or ".." || source.Contains('\\') || source.Contains('/') || Path.IsPathRooted(source)) ||
                !paths.Add(directory.LogicalPath) || Path.IsPathRooted(directory.LogicalPath) ||
                (directory.LogicalPath.Length > 0 && directory.LogicalPath.Split('\\', '/').Any(segment => segment.Length == 0 || segment is "." or ".."))) return false;
        }
        return true;
    }

    private static AssetCatalogIdentity CaptureIdentity(string catalogPath, CancellationToken cancellationToken)
    {
        var before = new FileInfo(catalogPath);
        before.Refresh();
        if (!before.Exists) throw new FileNotFoundException("The asset catalog does not exist.", catalogPath);
        var length = before.Length;
        var creationTicks = before.CreationTimeUtc.Ticks;
        var writeTicks = before.LastWriteTimeUtc.Ticks;
        var fingerprint = Fingerprint(catalogPath, length, cancellationToken);
        var after = new FileInfo(catalogPath);
        after.Refresh();
        if (after.Length != length || after.CreationTimeUtc.Ticks != creationTicks || after.LastWriteTimeUtc.Ticks != writeTicks)
            throw new IOException("The asset catalog changed while its comparison index was being generated.");
        return new(length, creationTicks, writeTicks, fingerprint);
    }

    private static string Fingerprint(string path, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FingerprintBlockBytes, FileOptions.RandomAccess);
        var offsets = new[] { 0L, Math.Max(0L, length / 2 - FingerprintBlockBytes / 2L), Math.Max(0L, length - FingerprintBlockBytes) }.Distinct().ToArray();
        var buffer = new byte[FingerprintBlockBytes];
        foreach (var offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = offset;
            hash.AppendData(BitConverter.GetBytes(offset));
            var remaining = (int)Math.Min(FingerprintBlockBytes, length - offset);
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, remaining);
                if (read == 0) throw new EndOfStreamException("The asset catalog was truncated while its identity was being read.");
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IReadOnlyList<string> ParseCsv(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); }
            else value.Append(character);
        }
        if (quoted) throw new InvalidDataException("The asset catalog contains an unterminated quoted field.");
        fields.Add(value.ToString());
        return fields;
    }
}
