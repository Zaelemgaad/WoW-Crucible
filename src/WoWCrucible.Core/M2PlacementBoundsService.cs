using System.Numerics;
using System.Security.Cryptography;

namespace WoWCrucible.Core;

public sealed record M2PlacementBoundsEvidence(
    string ModelPath,
    string Sha256,
    uint VertexCount,
    Vector3 ModelMinimum,
    Vector3 ModelMaximum);

public static class M2PlacementBoundsService
{
    private const int VertexStride = 48;
    private const int MaximumVertices = 5_000_000;
    private const long MaximumModelBytes = 512L * 1024 * 1024;

    public static M2PlacementBoundsEvidence InspectModel(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The M2 placement source does not exist.", path);
        if (Path.GetExtension(path).ToLowerInvariant() is not ".m2" and not ".mdx") throw new ArgumentException("M2 placement evidence must be an extracted .m2 or legacy-named .mdx file.", nameof(path));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        if (stream.Length is < 0x130 or > MaximumModelBytes) throw new InvalidDataException($"M2 placement evidence must be a bounded complete model from 304 bytes through {MaximumModelBytes:N0} bytes.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "MD20") throw new InvalidDataException("M2 placement evidence is not an unwrapped MD20 model.");
        var version = BitConverter.ToUInt32(bytes, 4); if (version != 264) throw new NotSupportedException($"M2 placement bounds currently require Wrath model version 264; this model is version {version:N0}.");
        var count = BitConverter.ToUInt32(bytes, 0x3C); var offset = BitConverter.ToUInt32(bytes, 0x40);
        if (count is 0 or > MaximumVertices || offset > int.MaxValue || offset + (ulong)count * VertexStride > (ulong)bytes.LongLength) throw new InvalidDataException($"M2 vertex table is empty, over the {MaximumVertices:N0}-vertex bound, or extends beyond the model.");
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        for (var index = 0u; index < count; index++)
        {
            var at = checked((int)offset + checked((int)index) * VertexStride); var value = new Vector3(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4), BitConverter.ToSingle(bytes, at + 8));
            if (!Finite(value)) throw new InvalidDataException($"M2 vertex {index:N0} contains a non-finite coordinate.");
            minimum = Vector3.Min(minimum, value); maximum = Vector3.Max(maximum, value);
        }
        return new(path, Convert.ToHexString(SHA256.HashData(bytes)), count, minimum, maximum);
    }

    public static (Vector3 Minimum, Vector3 Maximum) Calculate(M2PlacementBoundsEvidence evidence, Vector3 position, Vector3 orientation, float scale)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return M2PreviewSceneService.TransformBounds(evidence.ModelMinimum, evidence.ModelMaximum, M2PreviewSceneService.MapObjectTransform(orientation, scale, position));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
