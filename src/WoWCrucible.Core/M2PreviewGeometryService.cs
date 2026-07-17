using System.Numerics;

namespace WoWCrucible.Core;

public sealed record M2TextureSlot(int Index, uint Type, uint Flags, string? EmbeddedPath);
public sealed record M2PreviewGeometry(string ModelPath, string SkinPath, IReadOnlyList<Vector3> Vertices, IReadOnlyList<Vector3> Normals, IReadOnlyList<Vector2> TextureCoordinates, IReadOnlyList<int> TriangleIndices, Vector3 Minimum, Vector3 Maximum, IReadOnlyList<M2TextureSlot> TextureSlots);

public static class M2PreviewGeometryService
{
    private const int VertexStride = 48;
    private const int MaximumVertices = 5_000_000;
    private const int MaximumTriangleIndices = 15_000_000;

    public static M2PreviewGeometry Load(string modelPath, string? skinPath = null)
    {
        modelPath = Path.GetFullPath(modelPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The M2 model does not exist.", modelPath);
        var model = File.ReadAllBytes(modelPath);
        if (model.Length < 0x44 || FourCc(model, 0) != "MD20") throw new InvalidDataException("Embedded preview currently requires an unwrapped MD20 model.");
        var version = ReadUInt(model, 4);
        if (version != 264) throw new NotSupportedException($"Embedded preview currently supports Wrath M2 version 264; this model is version {version}.");
        var vertexCount = CheckedCount(ReadUInt(model, 0x3C), MaximumVertices, "M2 vertex");
        var vertexOffset = CheckedOffset(ReadUInt(model, 0x40), "M2 vertex");
        RequireRange(model, vertexOffset, vertexCount, VertexStride, "M2 vertices");
        var vertices = new Vector3[vertexCount]; var normals = new Vector3[vertexCount]; var textureCoordinates = new Vector2[vertexCount];
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        for (var index = 0; index < vertexCount; index++)
        {
            var offset = vertexOffset + index * VertexStride;
            var vertex = ReadVector(model, offset); var normal = ReadVector(model, offset + 20);
            if (!Finite(vertex) || !Finite(normal)) throw new InvalidDataException($"M2 vertex {index:N0} contains a non-finite coordinate.");
            var uv = new Vector2(BitConverter.ToSingle(model, offset + 32), BitConverter.ToSingle(model, offset + 36));
            vertices[index] = vertex; normals[index] = normal; textureCoordinates[index] = Finite(uv) ? uv : Vector2.Zero; minimum = Vector3.Min(minimum, vertex); maximum = Vector3.Max(maximum, vertex);
        }

        var textureSlots = ReadTextureSlots(model);
        skinPath = ResolveSkin(modelPath, skinPath);
        var skin = File.ReadAllBytes(skinPath);
        if (skin.Length < 48 || FourCc(skin, 0) != "SKIN") throw new InvalidDataException("The companion file is not a valid SKIN container.");
        var lookupCount = CheckedCount(ReadUInt(skin, 4), MaximumVertices, "skin vertex lookup"); var lookupOffset = CheckedOffset(ReadUInt(skin, 8), "skin vertex lookup");
        var triangleIndexCount = CheckedCount(ReadUInt(skin, 12), MaximumTriangleIndices, "skin triangle index"); var triangleOffset = CheckedOffset(ReadUInt(skin, 16), "skin triangle");
        if (triangleIndexCount % 3 != 0) throw new InvalidDataException($"SKIN triangle index count {triangleIndexCount:N0} is not divisible by three.");
        RequireRange(skin, lookupOffset, lookupCount, 2, "SKIN vertex lookup"); RequireRange(skin, triangleOffset, triangleIndexCount, 2, "SKIN triangles");
        var lookup = new ushort[lookupCount]; for (var index = 0; index < lookup.Length; index++) lookup[index] = ReadUShort(skin, lookupOffset + index * 2);
        var triangles = new int[triangleIndexCount];
        for (var index = 0; index < triangles.Length; index++)
        {
            var lookupIndex = ReadUShort(skin, triangleOffset + index * 2);
            if (lookupIndex >= lookup.Length) throw new InvalidDataException($"SKIN triangle entry {index:N0} references lookup {lookupIndex:N0}, but only {lookup.Length:N0} entries exist.");
            var vertexIndex = lookup[lookupIndex];
            if (vertexIndex >= vertices.Length) throw new InvalidDataException($"SKIN lookup {lookupIndex:N0} references M2 vertex {vertexIndex:N0}, but only {vertices.Length:N0} vertices exist.");
            triangles[index] = vertexIndex;
        }
        return new(modelPath, skinPath, vertices, normals, textureCoordinates, triangles, minimum, maximum, textureSlots);
    }

    private static IReadOnlyList<M2TextureSlot> ReadTextureSlots(byte[] model)
    {
        const int CountOffset = 0x50; const int DataOffset = 0x54; const int TextureStride = 16; const int MaximumTextures = 4096;
        if (model.Length < DataOffset + 4) return [];
        var count = CheckedCount(ReadUInt(model, CountOffset), MaximumTextures, "M2 texture"); var offset = CheckedOffset(ReadUInt(model, DataOffset), "M2 texture");
        RequireRange(model, offset, count, TextureStride, "M2 textures"); var result = new M2TextureSlot[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * TextureStride; var type = ReadUInt(model, item); var flags = ReadUInt(model, item + 4); var nameLength = ReadUInt(model, item + 8); var nameOffset = ReadUInt(model, item + 12); string? path = null;
            if (type == 0 && nameLength > 0)
            {
                var length = CheckedCount(nameLength, 1024 * 1024, "M2 texture name"); var start = CheckedOffset(nameOffset, "M2 texture name"); RequireRange(model, start, length, 1, "M2 texture name");
                path = System.Text.Encoding.UTF8.GetString(model, start, length).TrimEnd('\0');
            }
            result[index] = new(index, type, flags, path);
        }
        return result;
    }

    private static string ResolveSkin(string modelPath, string? selected)
    {
        if (!string.IsNullOrWhiteSpace(selected))
        {
            var path = Path.GetFullPath(selected); if (!File.Exists(path)) throw new FileNotFoundException("The selected SKIN file does not exist.", path); return path;
        }
        var directory = Path.GetDirectoryName(modelPath)!; var stem = Path.GetFileNameWithoutExtension(modelPath);
        var exact = Path.Combine(directory, stem + "00.skin"); if (File.Exists(exact)) return exact;
        return Directory.EnumerateFiles(directory, stem + "*.skin", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? throw new FileNotFoundException($"No companion {stem}00.skin file was found beside the model.");
    }

    private static void RequireRange(byte[] data, int offset, int count, int stride, string label)
    {
        var end = (long)offset + (long)count * stride;
        if (offset < 0 || end > data.LongLength) throw new InvalidDataException($"{label} range ({offset:N0}..{end:N0}) exceeds the {data.LongLength:N0}-byte file.");
    }
    private static int CheckedCount(uint value, int maximum, string label) => value > maximum ? throw new InvalidDataException($"{label} count {value:N0} exceeds the safety limit {maximum:N0}.") : checked((int)value);
    private static int CheckedOffset(uint value, string label) => value > int.MaxValue ? throw new InvalidDataException($"{label} offset {value:N0} is unsupported.") : (int)value;
    private static uint ReadUInt(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadUShort(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
    private static Vector3 ReadVector(byte[] data, int offset) => new(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4), BitConverter.ToSingle(data, offset + 8));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static string FourCc(byte[] data, int offset) => System.Text.Encoding.ASCII.GetString(data, offset, 4);
}
