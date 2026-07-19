using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record WmoPreviewMaterial(int Index, uint Flags, uint Shader, uint BlendMode, string? Texture1, string? Texture2, string? Texture3);
public sealed record WmoPreviewBatch(int GroupIndex, int MaterialIndex, int TriangleStart, int TriangleIndexCount);
public sealed record WmoPreviewGroup(int Index, string Path, uint Flags, int VertexStart, int VertexCount, int TriangleStart,
    int TriangleIndexCount, int BatchStart, int BatchCount, Vector3 Minimum, Vector3 Maximum, IReadOnlyList<string> Findings);
public sealed record WmoPreviewGeometry(string RootPath, uint Version, IReadOnlyList<Vector3> Vertices, IReadOnlyList<Vector3> Normals,
    IReadOnlyList<Vector2> TextureCoordinates, IReadOnlyList<uint> VertexColors, IReadOnlyList<int> TriangleIndices,
    IReadOnlyList<WmoPreviewMaterial> Materials, IReadOnlyList<WmoPreviewBatch> Batches, IReadOnlyList<WmoPreviewGroup> Groups,
    Vector3 Minimum, Vector3 Maximum, IReadOnlyList<string> Findings);
public sealed record WmoPreviewTextureSet(IReadOnlyDictionary<int, RgbaTexture> Textures, IReadOnlyDictionary<int, string> Sources, IReadOnlyList<string> Findings);

public static partial class WmoPreviewGeometryService
{
    private sealed record Chunk(string Id, int HeaderOffset, int PayloadOffset, int Size);
    private const int MaximumFileBytes = 512 * 1024 * 1024;
    private const int MaximumGroups = 4096;
    private const int MaximumVertices = 5_000_000;
    private const int MaximumTriangleIndices = 30_000_000;

    public static WmoPreviewGeometry Load(string path, IReadOnlyDictionary<int, string>? groupFiles = null, CancellationToken cancellationToken = default)
    {
        path = ResolveRootPath(path);
        var root = ReadFile(path, "WMO root");
        var rootChunks = ReadChunks(root, 0, root.Length, "WMO root");
        var version = ReadVersion(root, rootChunks, "WMO root");
        if (version != 17) throw new NotSupportedException($"Embedded WMO preview supports version 17; this root declares version {version:N0}.");
        var mohd = Single(rootChunks, "MOHD", true)!;
        if (mohd.Size < 64) throw new InvalidDataException($"WMO MOHD must contain the 64-byte Wrath header; it declares {mohd.Size:N0} bytes.");
        var groupCount = checked((int)ReadUInt(root, mohd.PayloadOffset + 4));
        if (groupCount is < 1 or > MaximumGroups) throw new InvalidDataException($"WMO root declares an invalid group count: {groupCount:N0}.");

        var textureOffsets = ReadStringOffsets(root, Single(rootChunks, "MOTX", false));
        var materials = ReadMaterials(root, Single(rootChunks, "MOMT", false), textureOffsets);
        var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>(); var colors = new List<uint>();
        var indices = new List<int>(); var batches = new List<WmoPreviewBatch>(); var groups = new List<WmoPreviewGroup>(); var findings = new List<string>();
        var rootStem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));

        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupPath = groupFiles?.GetValueOrDefault(groupIndex) ?? $"{rootStem}_{groupIndex:000}.wmo";
            if (!File.Exists(groupPath)) { findings.Add($"Missing group {groupIndex:000}: {groupPath}"); continue; }
            var vertexStart = vertices.Count; var normalStart = normals.Count; var uvStart = uvs.Count; var colorStart = colors.Count;
            var indexStart = indices.Count; var batchStart = batches.Count; var loadedGroupStart = groups.Count;
            try
            {
                var groupData = ReadFile(groupPath, $"WMO group {groupIndex:000}");
                LoadGroup(groupIndex, groupPath, groupData, materials.Count, vertices, normals, uvs, colors, indices, batches, groups);
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or OverflowException or IOException)
            {
                RollBack(vertices, vertexStart); RollBack(normals, normalStart); RollBack(uvs, uvStart); RollBack(colors, colorStart);
                RollBack(indices, indexStart); RollBack(batches, batchStart); RollBack(groups, loadedGroupStart);
                findings.Add($"Group {groupIndex:000} rejected: {exception.Message}");
            }
            if (vertices.Count > MaximumVertices || indices.Count > MaximumTriangleIndices)
                throw new InvalidDataException("WMO preview geometry exceeds the bounded vertex/triangle budget.");
        }

        if (groups.Count == 0 || vertices.Count == 0 || indices.Count == 0)
            throw new InvalidDataException($"WMO root has no renderable groups. {string.Join(" ", findings.Take(8))}");
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        foreach (var vertex in vertices) { minimum = Vector3.Min(minimum, vertex); maximum = Vector3.Max(maximum, vertex); }
        if (!Finite(minimum) || !Finite(maximum)) throw new InvalidDataException("WMO geometry has non-finite bounds.");
        if (materials.Count == 0) findings.Add("Root has no MOMT materials; geometry will use diagnostic colors.");
        return new(path, version, vertices, normals, uvs, colors, indices, materials, batches, groups, minimum, maximum, findings);
    }

    private static void RollBack<T>(List<T> values, int count)
    {
        if (values.Count > count) values.RemoveRange(count, values.Count - count);
    }

    public static IReadOnlyDictionary<int, string> ResolveTextureFiles(WmoPreviewGeometry geometry, string? contentRoot = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var result = new Dictionary<int, string>();
        for (var index = 0; index < geometry.Materials.Count; index++)
        {
            var texture = geometry.Materials[index].Texture1;
            if (string.IsNullOrWhiteSpace(texture)) continue;
            var resolved = ResolveClientPath(geometry.RootPath, texture, contentRoot);
            if (resolved is not null) result[index] = resolved;
        }
        return result;
    }

    public static WmoPreviewTextureSet LoadTextures(WmoPreviewGeometry geometry, string? contentRoot = null, int maximumDimension = 1024, CancellationToken cancellationToken = default)
    {
        if (maximumDimension is < 64 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumDimension), "Preview texture dimension must be 64–4096 pixels.");
        var sources = ResolveTextureFiles(geometry, contentRoot); var textures = new Dictionary<int, RgbaTexture>(); var findings = new List<string>();
        var decodedByPath = new Dictionary<string, RgbaTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in geometry.Materials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(material.Index, out var path)) { if (!string.IsNullOrWhiteSpace(material.Texture1)) findings.Add($"Material {material.Index:N0} texture was not found: {material.Texture1}"); continue; }
            try
            {
                if (!decodedByPath.TryGetValue(path, out var texture))
                {
                    var info = BlpTextureService.Inspect(path); var mip = info.MipLevels.FirstOrDefault(value => Math.Max(value.Width, value.Height) <= maximumDimension) ?? info.MipLevels[^1];
                    texture = BlpTextureService.Decode(path, mip.Index); decodedByPath[path] = texture;
                }
                textures[material.Index] = texture;
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException)
            {
                findings.Add($"Material {material.Index:N0} texture decode failed ({path}): {exception.Message}");
            }
        }
        return new(textures, sources, findings);
    }

    private static void LoadGroup(int groupIndex, string path, byte[] data, int materialCount,
        List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<uint> colors, List<int> indices,
        List<WmoPreviewBatch> batches, List<WmoPreviewGroup> groups)
    {
        var top = ReadChunks(data, 0, data.Length, $"WMO group {groupIndex:000}");
        var version = ReadVersion(data, top, $"WMO group {groupIndex:000}");
        if (version != 17) throw new NotSupportedException($"group MVER is {version:N0}, not 17");
        var mogp = Single(top, "MOGP", true)!;
        if (mogp.Size < 68) throw new InvalidDataException("MOGP is shorter than its 68-byte group header.");
        var flags = ReadUInt(data, mogp.PayloadOffset + 8);
        var nestedStart = checked(mogp.PayloadOffset + 68); var nestedLength = checked(mogp.Size - 68);
        var chunks = ReadChunks(data, nestedStart, nestedLength, $"WMO group {groupIndex:000} MOGP");
        var mopy = Single(chunks, "MOPY", true)!; var movi = Single(chunks, "MOVI", true)!; var movt = Single(chunks, "MOVT", true)!;
        if (mopy.Size % 2 != 0 || movi.Size % 6 != 0 || movt.Size % 12 != 0)
            throw new InvalidDataException("MOPY, MOVI, or MOVT has an invalid fixed-record size.");
        var vertexCount = movt.Size / 12; var triangleCount = movi.Size / 6; var polygonCount = mopy.Size / 2;
        if (vertexCount == 0 || triangleCount == 0) throw new InvalidDataException("group contains no vertices or triangles.");
        if (polygonCount < triangleCount) throw new InvalidDataException($"MOPY has {polygonCount:N0} polygon records for {triangleCount:N0} MOVI triangles.");
        var groupFindings = new List<string>();
        if (polygonCount > triangleCount) groupFindings.Add($"MOPY has {polygonCount - triangleCount:N0} unused trailing polygon record(s).");

        var vertexStart = vertices.Count;
        for (var i = 0; i < vertexCount; i++) vertices.Add(ReadVector3(data, movt.PayloadOffset + i * 12, "MOVT vertex"));
        var monr = Single(chunks, "MONR", false); var motv = chunks.FirstOrDefault(chunk => chunk.Id == "MOTV"); var mocv = chunks.FirstOrDefault(chunk => chunk.Id == "MOCV");
        if (monr is not null && monr.Size % 12 == 0 && monr.Size / 12 >= vertexCount)
            for (var i = 0; i < vertexCount; i++) normals.Add(ReadVector3(data, monr.PayloadOffset + i * 12, "MONR normal"));
        else { normals.AddRange(Enumerable.Repeat(Vector3.Zero, vertexCount)); groupFindings.Add("MONR is missing or shorter than MOVT; face normals are used for lighting."); }
        if (motv is not null && motv.Size % 8 == 0 && motv.Size / 8 >= vertexCount)
            for (var i = 0; i < vertexCount; i++) uvs.Add(ReadVector2(data, motv.PayloadOffset + i * 8, "MOTV coordinate"));
        else { uvs.AddRange(Enumerable.Repeat(Vector2.Zero, vertexCount)); groupFindings.Add("MOTV is missing or shorter than MOVT; material colors are used where UVs are unavailable."); }
        if (mocv is not null && mocv.Size >= vertexCount * 4)
            for (var i = 0; i < vertexCount; i++) colors.Add(ReadUInt(data, mocv.PayloadOffset + i * 4));
        else colors.AddRange(Enumerable.Repeat(0xFFFFFFFFu, vertexCount));

        var groupTriangleStart = indices.Count; var batchStart = batches.Count; var currentMaterial = int.MinValue; var currentBatchStart = 0;
        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            var polygonOffset = mopy.PayloadOffset + triangle * 2; var polygonFlags = data[polygonOffset]; var material = data[polygonOffset + 1];
            if (material == 0xFF) continue;
            if (material >= materialCount && materialCount > 0) groupFindings.Add($"Triangle {triangle:N0} references missing material {material:N0}; diagnostic color used.");
            var triangleStart = indices.Count;
            for (var corner = 0; corner < 3; corner++)
            {
                var local = ReadUShort(data, movi.PayloadOffset + triangle * 6 + corner * 2);
                if (local >= vertexCount) throw new InvalidDataException($"MOVI triangle {triangle:N0} references vertex {local:N0}, beyond {vertexCount:N0} vertices.");
                indices.Add(vertexStart + local);
            }
            if (currentMaterial != material)
            {
                if (currentMaterial != int.MinValue) batches.Add(new(groupIndex, currentMaterial, currentBatchStart, triangleStart - currentBatchStart));
                currentMaterial = material; currentBatchStart = triangleStart;
            }
            if ((polygonFlags & 0x20) == 0 && groupFindings.Count(value => value.StartsWith("Includes", StringComparison.Ordinal)) == 0)
                groupFindings.Add("Includes non-collision triangles without the MOPY render flag for diagnostic completeness.");
        }
        if (currentMaterial != int.MinValue) batches.Add(new(groupIndex, currentMaterial, currentBatchStart, indices.Count - currentBatchStart));
        if (indices.Count == groupTriangleStart) { vertices.RemoveRange(vertexStart, vertexCount); normals.RemoveRange(vertexStart, vertexCount); uvs.RemoveRange(vertexStart, vertexCount); colors.RemoveRange(vertexStart, vertexCount); throw new InvalidDataException("group contains only collision-only material 255 triangles."); }
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        for (var i = vertexStart; i < vertices.Count; i++) { minimum = Vector3.Min(minimum, vertices[i]); maximum = Vector3.Max(maximum, vertices[i]); }
        groups.Add(new(groupIndex, path, flags, vertexStart, vertexCount, groupTriangleStart, indices.Count - groupTriangleStart,
            batchStart, batches.Count - batchStart, minimum, maximum, groupFindings.Distinct(StringComparer.Ordinal).ToArray()));
    }

    private static IReadOnlyList<WmoPreviewMaterial> ReadMaterials(byte[] data, Chunk? chunk, IReadOnlyDictionary<uint, string> textures)
    {
        if (chunk is null) return [];
        if (chunk.Size % 64 != 0) throw new InvalidDataException($"MOMT size {chunk.Size:N0} is not divisible by its 64-byte Wrath record size.");
        var result = new WmoPreviewMaterial[chunk.Size / 64];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = chunk.PayloadOffset + index * 64;
            result[index] = new(index, ReadUInt(data, offset), ReadUInt(data, offset + 4), ReadUInt(data, offset + 8),
                Texture(ReadUInt(data, offset + 12)), Texture(ReadUInt(data, offset + 24)), Texture(ReadUInt(data, offset + 36)));
        }
        return result;
        string? Texture(uint offset) => textures.TryGetValue(offset, out var value) ? value : null;
    }

    private static IReadOnlyDictionary<uint, string> ReadStringOffsets(byte[] data, Chunk? chunk)
    {
        var result = new Dictionary<uint, string>(); if (chunk is null) return result;
        var index = 0;
        while (index < chunk.Size)
        {
            var end = Array.IndexOf(data, (byte)0, chunk.PayloadOffset + index, chunk.Size - index); if (end < 0) end = chunk.PayloadOffset + chunk.Size;
            if (end > chunk.PayloadOffset + index)
            {
                var value = Encoding.UTF8.GetString(data, chunk.PayloadOffset + index, end - chunk.PayloadOffset - index).Trim();
                if (value.Length > 0) result[(uint)index] = value;
            }
            index = end - chunk.PayloadOffset + 1;
        }
        return result;
    }

    private static string ResolveRootPath(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The WMO file does not exist.", path);
        if (!Path.GetExtension(path).Equals(".wmo", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("WMO preview requires a .wmo root or group file.");
        var match = GroupName().Match(Path.GetFileNameWithoutExtension(path));
        if (!match.Success) return path;
        var root = Path.Combine(Path.GetDirectoryName(path)!, match.Groups[1].Value + ".wmo");
        if (!File.Exists(root)) throw new FileNotFoundException("The selected group has no companion WMO root.", root);
        return root;
    }

    private static string? ResolveClientPath(string rootPath, string clientPath, string? contentRoot)
    {
        var relative = clientPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(contentRoot)) { var explicitPath = Path.Combine(Path.GetFullPath(contentRoot), relative); if (File.Exists(explicitPath)) return explicitPath; }
        var directory = Path.GetDirectoryName(rootPath);
        for (var depth = 0; directory is not null && depth < 10; depth++, directory = Path.GetDirectoryName(directory))
        {
            var candidate = Path.Combine(directory, relative); if (File.Exists(candidate)) return candidate;
            if (Path.GetFileName(directory).Equals("Content", StringComparison.OrdinalIgnoreCase))
            {
                var provenance = Path.GetFileName(Path.GetDirectoryName(rootPath)); var logicalDirectory = Path.Combine(directory, Path.GetDirectoryName(relative) ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(provenance))
                {
                    var provenanceCandidate = Path.Combine(logicalDirectory, provenance, Path.GetFileName(relative)); if (File.Exists(provenanceCandidate)) return provenanceCandidate;
                }
                if (Directory.Exists(logicalDirectory))
                {
                    var alternatives = Directory.EnumerateDirectories(logicalDirectory).Select(folder => Path.Combine(folder, Path.GetFileName(relative))).Where(File.Exists).Take(2).ToArray();
                    if (alternatives.Length == 1) return alternatives[0];
                }
            }
        }
        var local = Path.Combine(Path.GetDirectoryName(rootPath)!, Path.GetFileName(relative)); return File.Exists(local) ? local : null;
    }

    private static byte[] ReadFile(string path, string label)
    {
        var info = new FileInfo(path); if (info.Length is < 8 or > MaximumFileBytes) throw new InvalidDataException($"{label} size {info.Length:N0} is outside the safe preview range.");
        return File.ReadAllBytes(path);
    }

    private static IReadOnlyList<Chunk> ReadChunks(byte[] data, int start, int length, string label)
    {
        if (start < 0 || length < 0 || (long)start + length > data.Length) throw new InvalidDataException($"{label} chunk range is outside the file.");
        var result = new List<Chunk>(); var offset = start; var end = start + length;
        while (offset < end)
        {
            if (end - offset < 8) throw new InvalidDataException($"{label} has a truncated chunk header at byte {offset:N0}.");
            var id = new string(Encoding.ASCII.GetString(data, offset, 4).Reverse().ToArray()); var size = ReadUInt(data, offset + 4);
            if (!id.All(character => character is >= ' ' and <= '~')) throw new InvalidDataException($"{label} has an invalid chunk signature at byte {offset:N0}.");
            var payload = offset + 8L; var chunkEnd = payload + size;
            if (size > int.MaxValue || chunkEnd > end) throw new InvalidDataException($"{label} chunk {id} at byte {offset:N0} extends beyond its container.");
            result.Add(new(id, offset, checked((int)payload), checked((int)size))); offset = checked((int)chunkEnd);
        }
        return result;
    }

    private static Chunk? Single(IReadOnlyList<Chunk> chunks, string id, bool required)
    {
        var values = chunks.Where(chunk => chunk.Id == id).ToArray();
        if (values.Length > 1) throw new InvalidDataException($"WMO contains duplicate {id} chunks.");
        if (values.Length == 0 && required) throw new InvalidDataException($"WMO has no {id} chunk.");
        return values.FirstOrDefault();
    }

    private static uint ReadVersion(byte[] data, IReadOnlyList<Chunk> chunks, string label)
    {
        var chunk = Single(chunks, "MVER", true)!; if (chunk.Size < 4) throw new InvalidDataException($"{label} MVER is truncated."); return ReadUInt(data, chunk.PayloadOffset);
    }
    private static uint ReadUInt(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadUShort(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
    private static Vector2 ReadVector2(byte[] data, int offset, string label)
    {
        var value = new Vector2(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4)); if (!float.IsFinite(value.X) || !float.IsFinite(value.Y)) throw new InvalidDataException($"{label} is non-finite."); return value;
    }
    private static Vector3 ReadVector3(byte[] data, int offset, string label)
    {
        var value = new Vector3(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4), BitConverter.ToSingle(data, offset + 8)); if (!Finite(value)) throw new InvalidDataException($"{label} is non-finite."); return value;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    [GeneratedRegex(@"^(.+)_\d{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex GroupName();
}
