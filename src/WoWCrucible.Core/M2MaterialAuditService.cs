using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace WoWCrucible.Core;

public enum M2MaterialEncoding { Packed, Explicit, BlendOverride }

public sealed record M2MaterialAuditEntry(M2MaterialEncoding Encoding, ushort ShaderId, int TextureStages, string Combiner,
    bool CombinerSupported, bool CombinerExact, long MaterialUnits, int SkinFiles, IReadOnlyList<string> Examples);

public sealed record M2MaterialAuditReport(string Root, int Workers, int DiscoveredSkinFiles, int ScannedSkinFiles,
    int WotlkModelFiles, int MissingCompanionModels, int NonWotlkModels, int InvalidPairs, long MaterialUnits,
    long UnsupportedCombinerMaterialUnits, long UnsupportedExplicitCombinerMaterialUnits, IReadOnlyList<M2MaterialAuditEntry> Entries,
    IReadOnlyList<string> Findings, double DurationMilliseconds);

/// <summary>
/// Performs a bounded header/material-only audit of native build-264 M2/SKIN pairs.
/// It never loads model geometry or textures and never mutates the inspected corpus.
/// </summary>
public static class M2MaterialAuditService
{
    public const int MaximumWorkers = 32;
    private const int MaterialStride = 24;
    private const int MaximumMaterials = 131_072;
    private const int MaximumBlendOverrides = 65_536;

    private readonly record struct MaterialKey(M2MaterialEncoding Encoding, ushort ShaderId, ushort TextureStages, string Combiner,
        bool CombinerSupported, bool CombinerExact);

    private sealed class Aggregate
    {
        private readonly int _maximumExamples;
        private readonly SortedSet<string> _examples = new(StringComparer.OrdinalIgnoreCase);
        private long _materialUnits;
        private int _skinFiles;
        public Aggregate(int maximumExamples) => _maximumExamples = maximumExamples;
        public void AddMaterials(int count) => Interlocked.Add(ref _materialUnits, count);
        public void AddSkin(string path)
        {
            Interlocked.Increment(ref _skinFiles);
            lock (_examples)
            {
                _examples.Add(path);
                while (_examples.Count > _maximumExamples) _examples.Remove(_examples.Max!);
            }
        }
        public (long Materials, int Skins, IReadOnlyList<string> Examples) Snapshot()
        {
            lock (_examples) return (Interlocked.Read(ref _materialUnits), Volatile.Read(ref _skinFiles), _examples.ToArray());
        }
    }

    private sealed record ModelHeader(bool Wotlk, bool UsesBlendOverrides, IReadOnlyList<ushort> BlendOverrides);

    public static M2MaterialAuditReport Audit(string root, int workers = 0, int maximumExamples = 5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("An M2/SKIN file or folder is required.", nameof(root));
        if (workers == 0) workers = Math.Clamp(Environment.ProcessorCount, 1, 8);
        if (workers is < 1 or > MaximumWorkers) throw new ArgumentOutOfRangeException(nameof(workers), $"Workers must be 1 through {MaximumWorkers}, or zero for automatic.");
        if (maximumExamples is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumExamples), "Maximum examples must be 1 through 100.");
        root = Path.GetFullPath(root);
        string[] skins;
        if (File.Exists(root))
        {
            if (!Path.GetExtension(root).Equals(".skin", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A material-audit file input must be a .skin file.", nameof(root));
            skins = [root];
        }
        else if (Directory.Exists(root))
            skins = Directory.EnumerateFiles(root, "*.skin", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        else throw new FileNotFoundException("The M2/SKIN audit input does not exist.", root);

        var aggregates = new ConcurrentDictionary<MaterialKey, Aggregate>();
        var findings = new BoundedFindings(24);
        var scanned = 0; var wotlk = 0; var missing = 0; var nonWotlk = 0; var invalid = 0; long materialUnits = 0;
        var stopwatch = Stopwatch.StartNew();
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken };
        Parallel.ForEach(skins, options, skinPath =>
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var relative = File.Exists(root) ? Path.GetFileName(skinPath) : Path.GetRelativePath(root, skinPath);
            try
            {
                var modelPath = ResolveCompanionModel(skinPath);
                if (modelPath is null) { Interlocked.Increment(ref missing); findings.Add($"MISSING_MODEL\t{relative}"); return; }
                var model = ReadModelHeader(modelPath);
                if (!model.Wotlk) { Interlocked.Increment(ref nonWotlk); return; }
                Interlocked.Increment(ref wotlk);
                var materials = ReadMaterials(skinPath);
                Interlocked.Increment(ref scanned); Interlocked.Add(ref materialUnits, materials.Count);
                var seen = new HashSet<MaterialKey>();
                foreach (var group in materials.GroupBy(value => value))
                {
                    var raw = group.Key.Shader; var stages = group.Key.Stages;
                    M2MaterialEncoding encoding; M2PreviewTextureCombiner combiner;
                    if (model.UsesBlendOverrides)
                    {
                        encoding = M2MaterialEncoding.BlendOverride;
                        combiner = M2PreviewGeometryService.DescribeBlendOverride(model.BlendOverrides, raw, stages);
                    }
                    else
                    {
                        encoding = (raw & 0x8000) != 0 ? M2MaterialEncoding.Explicit : M2MaterialEncoding.Packed;
                        combiner = M2PreviewGeometryService.DescribeCombiner(raw, stages);
                    }
                    var key = new MaterialKey(encoding, raw, stages, combiner.Name, combiner.Supported, combiner.Exact);
                    var aggregate = aggregates.GetOrAdd(key, _ => new Aggregate(maximumExamples)); aggregate.AddMaterials(group.Count());
                    if (seen.Add(key)) aggregate.AddSkin(relative);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                Interlocked.Increment(ref invalid); findings.Add($"INVALID\t{relative}\t{exception.Message}");
            }
        });
        stopwatch.Stop();

        var entries = aggregates.Select(pair =>
        {
            var snapshot = pair.Value.Snapshot();
            return new M2MaterialAuditEntry(pair.Key.Encoding, pair.Key.ShaderId, pair.Key.TextureStages, pair.Key.Combiner,
                pair.Key.CombinerSupported, pair.Key.CombinerExact, snapshot.Materials, snapshot.Skins, snapshot.Examples);
        }).OrderByDescending(entry => entry.MaterialUnits).ThenBy(entry => entry.Encoding).ThenBy(entry => entry.ShaderId).ThenBy(entry => entry.TextureStages).ToArray();
        return new(root, workers, skins.Length, scanned, wotlk, missing, nonWotlk, invalid, materialUnits,
            entries.Where(entry => !entry.CombinerSupported).Sum(entry => entry.MaterialUnits),
            entries.Where(entry => entry.Encoding == M2MaterialEncoding.Explicit && !entry.CombinerSupported).Sum(entry => entry.MaterialUnits),
            entries, findings.Snapshot(), stopwatch.Elapsed.TotalMilliseconds);
    }

    private static string? ResolveCompanionModel(string skinPath)
    {
        var stem = Path.GetFileNameWithoutExtension(skinPath);
        if (stem.Length >= 2 && char.IsDigit(stem[^1]) && char.IsDigit(stem[^2])) stem = stem[..^2];
        var model = Path.Combine(Path.GetDirectoryName(skinPath)!, stem + ".m2");
        return File.Exists(model) ? model : null;
    }

    private static ModelHeader ReadModelHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length < 20) return new(false, false, []);
        var header = new byte[Math.Min(0x138, checked((int)stream.Length))]; ReadExactly(stream, header);
        if (!Encoding.ASCII.GetString(header, 0, 4).Equals("MD20", StringComparison.Ordinal) || BitConverter.ToUInt32(header, 4) != 264) return new(false, false, []);
        var usesOverrides = (BitConverter.ToUInt32(header, 0x10) & 0x8) != 0;
        if (!usesOverrides) return new(true, false, []);
        if (header.Length < 0x138) throw new InvalidDataException("M2 declares blend overrides but its extended header is truncated.");
        var count = BitConverter.ToUInt32(header, 0x130); var offset = BitConverter.ToUInt32(header, 0x134);
        if (count > MaximumBlendOverrides || offset > stream.Length || (long)offset + count * 2L > stream.Length) throw new InvalidDataException("M2 blend-override table is out of bounds.");
        var bytes = new byte[checked((int)count * 2)]; stream.Position = offset; ReadExactly(stream, bytes);
        var values = new ushort[count]; for (var index = 0; index < values.Length; index++) values[index] = BitConverter.ToUInt16(bytes, index * 2);
        return new(true, true, values);
    }

    private static IReadOnlyList<(ushort Shader, ushort Stages)> ReadMaterials(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length < 44) throw new InvalidDataException("SKIN header is truncated.");
        var header = new byte[44]; ReadExactly(stream, header);
        if (!Encoding.ASCII.GetString(header, 0, 4).Equals("SKIN", StringComparison.Ordinal)) throw new InvalidDataException("File does not begin with SKIN.");
        var count = BitConverter.ToUInt32(header, 36); var offset = BitConverter.ToUInt32(header, 40);
        if (count > MaximumMaterials || offset > stream.Length || (long)offset + count * MaterialStride > stream.Length) throw new InvalidDataException("SKIN material-unit table is out of bounds.");
        var bytes = new byte[checked((int)count * MaterialStride)]; stream.Position = offset; ReadExactly(stream, bytes);
        var result = new (ushort Shader, ushort Stages)[count];
        for (var index = 0; index < result.Length; index++) result[index] = (BitConverter.ToUInt16(bytes, index * MaterialStride + 2), BitConverter.ToUInt16(bytes, index * MaterialStride + 14));
        return result;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0; while (read < buffer.Length) { var current = stream.Read(buffer, read, buffer.Length - read); if (current == 0) throw new EndOfStreamException("Unexpected end of file."); read += current; }
    }

    private sealed class BoundedFindings(int maximum)
    {
        private readonly SortedSet<string> _values = new(StringComparer.OrdinalIgnoreCase);
        public void Add(string value) { lock (_values) { _values.Add(value); while (_values.Count > maximum) _values.Remove(_values.Max!); } }
        public IReadOnlyList<string> Snapshot() { lock (_values) return _values.ToArray(); }
    }
}
