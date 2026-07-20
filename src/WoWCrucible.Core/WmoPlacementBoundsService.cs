using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

public sealed record WmoRootBoundsEvidence(
    string RootPath,
    string Sha256,
    uint Version,
    Vector3 DeclaredMinimum,
    Vector3 DeclaredMaximum);

public sealed record WmoPlacementBounds(Vector3 Minimum, Vector3 Maximum);

/// <summary>
/// Reads the authoritative local-space bounding box from a Wrath WMO root and
/// converts it to the axis-aligned world-space MODF box for one exact
/// placement transform. This is deliberately independent of render-group
/// availability: MOHD is the client-authored placement-bounds contract.
/// </summary>
public static class WmoPlacementBoundsService
{
    private const int MaximumRootBytes = 512 * 1024 * 1024;

    public static WmoRootBoundsEvidence InspectRoot(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The WMO root does not exist.", path);
        if (!Path.GetExtension(path).Equals(".wmo", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Geometry-bound MODF editing requires a Wrath .wmo root.");
        var info = new FileInfo(path); if (info.Length is < 12 or > MaximumRootBytes) throw new InvalidDataException($"WMO root length {info.Length:N0} is outside the bounded 12..{MaximumRootBytes:N0}-byte range.");

        uint? version = null; Vector3? minimum = null; Vector3? maximum = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        var header = new byte[8];
        while (stream.Position < stream.Length)
        {
            var headerOffset = stream.Position; if (stream.Read(header) != 8) throw new InvalidDataException($"WMO chunk header is truncated at byte {headerOffset:N0}.");
            var id = Decode(header.AsSpan(0, 4)); var size = BitConverter.ToUInt32(header, 4); var payload = stream.Position; var end = payload + size;
            if (size > int.MaxValue || end > stream.Length) throw new InvalidDataException($"WMO chunk {id} at byte {headerOffset:N0} extends beyond the root file.");
            if (id == "MVER")
            {
                if (version is not null) throw new InvalidDataException("WMO root contains more than one MVER chunk.");
                if (size < 4) throw new InvalidDataException("WMO MVER is shorter than four bytes.");
                var bytes = new byte[4]; stream.ReadExactly(bytes); version = BitConverter.ToUInt32(bytes);
            }
            else if (id == "MOHD")
            {
                if (minimum is not null) throw new InvalidDataException("WMO root contains more than one MOHD chunk.");
                if (size < 64) throw new InvalidDataException("Wrath WMO MOHD is shorter than its 64-byte header.");
                var bytes = new byte[64]; stream.ReadExactly(bytes);
                minimum = ReadVector(bytes, 36); maximum = ReadVector(bytes, 48);
            }
            stream.Position = end;
        }
        if (version != 17) throw new NotSupportedException($"Geometry-bound MODF editing requires a Wrath version-17 WMO root; this file declares {(version is null ? "no MVER" : version.Value.ToString("N0"))}.");
        if (minimum is null || maximum is null) throw new InvalidDataException("WMO root has no complete MOHD declared bounds.");
        RequireFinite(minimum.Value, "WMO declared minimum"); RequireFinite(maximum.Value, "WMO declared maximum");
        if (minimum.Value.X > maximum.Value.X || minimum.Value.Y > maximum.Value.Y || minimum.Value.Z > maximum.Value.Z) throw new InvalidDataException("WMO MOHD declared bounds are reversed.");
        stream.Position = 0; var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new(path, hash, version.Value, minimum.Value, maximum.Value);
    }

    public static WmoPlacementBounds Calculate(WmoRootBoundsEvidence root, Vector3 position, Vector3 orientationDegrees, float scale)
    {
        ArgumentNullException.ThrowIfNull(root); return Calculate(root.DeclaredMinimum, root.DeclaredMaximum, position, orientationDegrees, scale);
    }

    public static WmoPlacementBounds Calculate(Vector3 localMinimum, Vector3 localMaximum, Vector3 position, Vector3 orientationDegrees, float scale)
    {
        RequireFinite(localMinimum, "WMO local minimum"); RequireFinite(localMaximum, "WMO local maximum"); RequireFinite(position, "WMO position"); RequireFinite(orientationDegrees, "WMO orientation");
        if (localMinimum.X > localMaximum.X || localMinimum.Y > localMaximum.Y || localMinimum.Z > localMaximum.Z) throw new ArgumentException("WMO local bounds are reversed.");
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale), "WMO placement scale must be finite and positive.");
        var transform = M2PreviewSceneService.MapObjectTransform(orientationDegrees, scale, position);
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        foreach (var x in new[] { localMinimum.X, localMaximum.X }) foreach (var y in new[] { localMinimum.Y, localMaximum.Y }) foreach (var z in new[] { localMinimum.Z, localMaximum.Z })
        {
            var world = Vector3.Transform(new Vector3(x, y, z), transform); minimum = Vector3.Min(minimum, world); maximum = Vector3.Max(maximum, world);
        }
        RequireFinite(minimum, "calculated MODF minimum"); RequireFinite(maximum, "calculated MODF maximum"); return new(minimum, maximum);
    }

    public static void RequireCalibrated(MapWmoPlacement placement, WmoRootBoundsEvidence root)
    {
        ArgumentNullException.ThrowIfNull(placement); ArgumentNullException.ThrowIfNull(root);
        var expected = Calculate(root, placement.Position, placement.Orientation, placement.Scale); var span = placement.MaximumExtent - placement.MinimumExtent;
        var tolerance = Math.Max(0.01f, Math.Max(Math.Abs(span.X), Math.Max(Math.Abs(span.Y), Math.Abs(span.Z))) * 0.00001f);
        if (!Close(expected.Minimum, placement.MinimumExtent, tolerance) || !Close(expected.Maximum, placement.MaximumExtent, tolerance))
            throw new InvalidDataException($"The selected WMO root does not reproduce this MODF record's current bounds within {tolerance:R} world units. Choose the exact effective-client provenance before rotating or scaling it.");
    }

    private static Vector3 ReadVector(byte[] bytes, int offset) => new(BitConverter.ToSingle(bytes, offset), BitConverter.ToSingle(bytes, offset + 4), BitConverter.ToSingle(bytes, offset + 8));
    private static bool Close(Vector3 left, Vector3 right, float tolerance) => Math.Abs(left.X - right.X) <= tolerance && Math.Abs(left.Y - right.Y) <= tolerance && Math.Abs(left.Z - right.Z) <= tolerance;
    private static void RequireFinite(Vector3 value, string label) { if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException($"{label} must contain three finite values."); }
    private static string Decode(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
}
