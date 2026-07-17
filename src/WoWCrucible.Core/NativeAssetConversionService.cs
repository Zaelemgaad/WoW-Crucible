using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum AssetFormat { M2, Wmo, Unknown }
public enum AssetCompatibility { AlreadyWotlk335, RequiresNativeConversion, Unsupported, Invalid }

public sealed record AssetChunk(string Id, long Offset, uint Size);
public sealed record AssetDependency(string Path, string Kind, bool Exists, string? Sha256);
public sealed record AssetInspection(
    string Path,
    string Sha256,
    long Size,
    AssetFormat Format,
    AssetCompatibility Compatibility,
    string Magic,
    uint? Version,
    IReadOnlyList<AssetChunk> Chunks,
    IReadOnlyList<AssetDependency> Dependencies,
    IReadOnlyList<string> Findings);
public sealed record NativeConversionWorkspace(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string Target,
    string RootPath,
    IReadOnlyList<AssetInspection> Assets,
    int CompatibleAssets,
    int ConversionRequired,
    int BlockedAssets);

public static class NativeAssetConversionService
{
    private const uint WotlkM2Version = 264;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AssetInspection Inspect(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The asset does not exist.", path);
        var extension = Path.GetExtension(path);
        var format = extension.Equals(".m2", StringComparison.OrdinalIgnoreCase) ? AssetFormat.M2
            : extension.Equals(".wmo", StringComparison.OrdinalIgnoreCase) ? AssetFormat.Wmo : AssetFormat.Unknown;
        var data = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(data));
        if (data.Length < 4) return Result(AssetCompatibility.Invalid, string.Empty, null, [], [], "File is shorter than a format signature.");
        var magic = FourCc(data, 0);
        return format switch
        {
            AssetFormat.M2 => InspectM2(),
            AssetFormat.Wmo => InspectWmo(),
            _ => Result(AssetCompatibility.Unsupported, magic, null, [], [], $"No native converter profile exists for {extension}.")
        };

        AssetInspection InspectM2()
        {
            var findings = new List<string>(); var chunks = new List<AssetChunk>(); uint? version = null;
            if (magic == "MD20")
            {
                if (data.Length < 8) return Result(AssetCompatibility.Invalid, magic, null, chunks, [], "MD20 header is truncated.");
                version = BitConverter.ToUInt32(data, 4);
                if (version == WotlkM2Version)
                {
                    findings.Add("Unwrapped MD20 version 264 is the native Wrath 3.3.5 model layout.");
                    if (data.Length >= 0x130)
                    {
                        findings.Add($"Header counts: vertices {ReadUInt(0x3C):N0}, textures {ReadUInt(0x50):N0}, animations {ReadUInt(0x1C):N0}, bones {ReadUInt(0x2C):N0}, particles {ReadUInt(0x128):N0}.");
                    }
                    return Result(AssetCompatibility.AlreadyWotlk335, magic, version, chunks, CompanionDependencies(path), findings.ToArray());
                }
                findings.Add($"Unwrapped MD20 version {version} is not the verified Wrath version 264 layout.");
                return Result(AssetCompatibility.RequiresNativeConversion, magic, version, chunks, CompanionDependencies(path), findings.ToArray());
            }
            if (magic != "MD21") return Result(AssetCompatibility.Invalid, magic, null, chunks, [], "M2 signature must be MD20 or the modern MD21 chunk container.");

            if (!TryReadChunks(data, chunks, findings, false)) return Result(AssetCompatibility.Invalid, magic, null, chunks, [], findings.ToArray());
            var md21 = chunks.FirstOrDefault(chunk => chunk.Id == "MD21");
            if (md21 is not null)
            {
                var payload = checked((int)md21.Offset + 8);
                if (md21.Size >= 8 && payload + 8 <= data.Length && FourCc(data, payload) == "MD20") version = BitConverter.ToUInt32(data, payload + 4);
                else if (md21.Size >= 4 && payload + 4 <= data.Length) version = BitConverter.ToUInt32(data, payload);
            }
            var fileIdChunks = chunks.Where(chunk => chunk.Id is "TXID" or "SFID" or "AFID" or "BFID" or "PFID" or "SKID").ToArray();
            foreach (var chunk in fileIdChunks) findings.Add($"{chunk.Id} contains up to {chunk.Size / 4:N0} external FileDataID reference(s) requiring a supplied listfile/source provider.");
            findings.Add("Chunked MD21 models require native structure conversion before the Wrath client can load them; Crucible has not written an output model yet.");
            return Result(AssetCompatibility.RequiresNativeConversion, magic, version, chunks, CompanionDependencies(path), findings.ToArray());
        }

        AssetInspection InspectWmo()
        {
            var chunks = new List<AssetChunk>(); var findings = new List<string>();
            if (!TryReadChunks(data, chunks, findings, true)) return Result(AssetCompatibility.Invalid, magic, null, chunks, [], findings.ToArray());
            var mver = chunks.FirstOrDefault(chunk => chunk.Id == "MVER");
            uint? version = null;
            if (mver is not null && mver.Size >= 4 && mver.Offset + 12 <= data.Length) version = BitConverter.ToUInt32(data, checked((int)mver.Offset + 8));
            if (version == 17)
            {
                findings.Add("WMO MVER is 17, the Wrath-era version, but later clients also retained version 17; chunk-level conversion validation is still required.");
                return Result(AssetCompatibility.RequiresNativeConversion, magic, version, chunks, WmoGroupDependencies(path), findings.ToArray());
            }
            findings.Add(mver is null ? "WMO has no MVER chunk." : $"WMO MVER {version} is not the expected Wrath value 17.");
            return Result(mver is null ? AssetCompatibility.Invalid : AssetCompatibility.Unsupported, magic, version, chunks, WmoGroupDependencies(path), findings.ToArray());
        }

        AssetInspection Result(AssetCompatibility compatibility, string resultMagic, uint? resultVersion, IReadOnlyList<AssetChunk> chunks, IReadOnlyList<AssetDependency> dependencies, params string[] findings)
            => new(path, hash, data.LongLength, format, compatibility, resultMagic, resultVersion, chunks, dependencies, findings);

        uint ReadUInt(int offset) => offset + 4 <= data.Length ? BitConverter.ToUInt32(data, offset) : 0;
    }

    public static NativeConversionWorkspace CreateWorkspace(IEnumerable<string> inputs, string outputRoot)
    {
        var paths = ExpandInputs(inputs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("Add at least one M2 or WMO asset to the native conversion workspace.");
        outputRoot = Path.GetFullPath(outputRoot);
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
            throw new IOException($"Conversion workspace must be new or empty: {outputRoot}");
        Directory.CreateDirectory(outputRoot);
        var sourceRoot = Path.Combine(outputRoot, "source"); Directory.CreateDirectory(sourceRoot);
        var inspections = paths.Select(Inspect).ToArray();
        foreach (var inspection in inspections)
        {
            var folder = Path.Combine(sourceRoot, inspection.Sha256[..12]); Directory.CreateDirectory(folder);
            File.Copy(inspection.Path, Path.Combine(folder, Path.GetFileName(inspection.Path)), false);
            foreach (var dependency in inspection.Dependencies.Where(dependency => dependency.Exists))
                File.Copy(dependency.Path, Path.Combine(folder, Path.GetFileName(dependency.Path)), false);
        }
        Directory.CreateDirectory(Path.Combine(outputRoot, "converted"));
        var result = new NativeConversionWorkspace(1, DateTimeOffset.UtcNow, "WoW 3.3.5a build 12340", outputRoot, inspections,
            inspections.Count(asset => asset.Compatibility == AssetCompatibility.AlreadyWotlk335),
            inspections.Count(asset => asset.Compatibility == AssetCompatibility.RequiresNativeConversion),
            inspections.Count(asset => asset.Compatibility is AssetCompatibility.Invalid or AssetCompatibility.Unsupported));
        File.WriteAllText(Path.Combine(outputRoot, "conversion-report.json"), JsonSerializer.Serialize(result, JsonOptions));
        return result;
    }

    private static IEnumerable<string> ExpandInputs(IEnumerable<string> inputs)
    {
        foreach (var raw in inputs)
        {
            var path = Path.GetFullPath(raw);
            if (File.Exists(path)) { yield return path; continue; }
            if (!Directory.Exists(path)) throw new FileNotFoundException("Asset input does not exist.", path);
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                         .Where(file => Path.GetExtension(file).Equals(".m2", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".wmo", StringComparison.OrdinalIgnoreCase)))
                yield return file;
        }
    }

    private static bool TryReadChunks(byte[] data, List<AssetChunk> chunks, List<string> findings, bool reverseIds)
    {
        long offset = 0;
        while (offset < data.LongLength)
        {
            if (data.LongLength - offset < 8) { findings.Add($"Trailing {data.LongLength - offset} byte(s) cannot form a chunk header."); return false; }
            var rawId = FourCc(data, checked((int)offset)); var id = reverseIds ? new string(rawId.Reverse().ToArray()) : rawId; var size = BitConverter.ToUInt32(data, checked((int)offset + 4));
            if (!id.All(character => character is >= ' ' and <= '~')) { findings.Add($"Invalid chunk signature at byte {offset:N0}."); return false; }
            var end = offset + 8L + size;
            if (end > data.LongLength) { findings.Add($"Chunk {id} at byte {offset:N0} declares {size:N0} bytes beyond end of file."); return false; }
            chunks.Add(new(id, offset, size)); offset = end;
        }
        return true;
    }

    private static IReadOnlyList<AssetDependency> CompanionDependencies(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath)!; var stem = Path.GetFileNameWithoutExtension(modelPath); var result = new List<AssetDependency>();
        foreach (var path in Directory.EnumerateFiles(directory, stem + "*", SearchOption.TopDirectoryOnly)
                     .Where(path => Path.GetExtension(path).Equals(".skin", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".anim", StringComparison.OrdinalIgnoreCase)))
            result.Add(Dependency(path, Path.GetExtension(path).Equals(".skin", StringComparison.OrdinalIgnoreCase) ? "skin" : "animation"));
        return result;
    }

    private static IReadOnlyList<AssetDependency> WmoGroupDependencies(string rootPath)
    {
        var directory = Path.GetDirectoryName(rootPath)!; var stem = Path.GetFileNameWithoutExtension(rootPath);
        return Directory.EnumerateFiles(directory, stem + "_*.wmo", SearchOption.TopDirectoryOnly).Select(path => Dependency(path, "WMO group")).ToArray();
    }

    private static AssetDependency Dependency(string path, string kind)
    {
        using var stream = File.OpenRead(path);
        return new(path, kind, true, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string FourCc(byte[] data, int offset) => Encoding.ASCII.GetString(data, offset, 4);
}
