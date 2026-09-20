using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

// Skia's triangle API has no depth buffer. Mesh fragments are resolved here before
// the completed surface is composited with the existing Skia overlays/effects.
internal static class DepthBufferedMesh
{
    internal sealed record Texture(SKColor[] Pixels, int Width, int Height, bool WrapU, bool WrapV,
        M2PreviewTextureCoordinateSource Coordinates, M2PreviewTextureStageBlend Blend);
    internal sealed record Material(Texture[] Textures, IReadOnlyList<M2TextureRenderPass> Passes, ushort Blend, bool DepthWrite, int Order);
    internal readonly record struct Vertex(Vector3 Screen, float InverseW, Vector2 Uv, Vector2 Uv2, Vector2 Environment, Vector3 Light, float EdgeFade);
    internal readonly record struct Triangle(Vertex A, Vertex B, Vertex C, Material Material)
    {
        public float Depth => (A.Screen.Z + B.Screen.Z + C.Screen.Z) / 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static SKBitmap Render(int width, int height, IReadOnlyList<Triangle> triangles, float pixelScale)
    {
        var length = checked(width * height);
        var depth = ArrayPool<float>.Shared.Rent(length);
        var colors = ArrayPool<Vector4>.Shared.Rent(length);
        var output = ArrayPool<int>.Shared.Rent(length);
        try
        {
            Array.Fill(depth, float.PositiveInfinity, 0, length); Array.Clear(colors, 0, length);
            // Transparent layers test against opaque depth. Coplanar material layers
            // retain their authored priority; opaque geometry never depends on sorting.
            foreach (var triangle in triangles.Where(t => t.Material.Blend <= 1).OrderBy(t => t.Material.Order))
                Draw(triangle, width, height, pixelScale, depth, colors);
            foreach (var triangle in triangles.Where(t => t.Material.Blend > 1).OrderBy(t => t.Material.Order).ThenByDescending(t => t.Depth))
                Draw(triangle, width, height, pixelScale, depth, colors);
            for (var i = 0; i < length; i++)
            {
                var value = Vector4.Clamp(colors[i], Vector4.Zero, Vector4.One);
                output[i] = (int)((uint)Byte(value.W) << 24 | (uint)Byte(value.X) << 16 | (uint)Byte(value.Y) << 8 | Byte(value.Z));
            }
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            try
            {
                var address = bitmap.GetPixels();
                if (address == IntPtr.Zero) throw new OutOfMemoryException("Could not allocate depth-tested preview surface.");
                for (var row = 0; row < height; row++) Marshal.Copy(output, row * width, IntPtr.Add(address, row * bitmap.RowBytes), width);
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(depth); ArrayPool<Vector4>.Shared.Return(colors); ArrayPool<int>.Shared.Return(output);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Draw(in Triangle triangle, int width, int height, float scale, float[] depth, Vector4[] colors)
    {
        var a = new Vector2(triangle.A.Screen.X, triangle.A.Screen.Y) * scale;
        var b = new Vector2(triangle.B.Screen.X, triangle.B.Screen.Y) * scale;
        var c = new Vector2(triangle.C.Screen.X, triangle.C.Screen.Y) * scale;
        var area = Cross(b - a, c - a);
        if (!float.IsFinite(area) || MathF.Abs(area) < 0.00001f) return;
        var lo = Vector2.Max(Vector2.Zero, Vector2.Min(a, Vector2.Min(b, c)));
        var hi = Vector2.Min(new(width - 1, height - 1), Vector2.Max(a, Vector2.Max(b, c)));
        if (lo.X > hi.X || lo.Y > hi.Y) return;
        var minX = (int)MathF.Floor(lo.X); var maxX = (int)MathF.Ceiling(hi.X);
        var minY = (int)MathF.Floor(lo.Y); var maxY = (int)MathF.Ceiling(hi.Y);
        var inverseArea = 1f / area;
        var aDx = (b.Y - c.Y) * inverseArea; var bDx = (c.Y - a.Y) * inverseArea;
        var aDy = (c.X - b.X) * inverseArea; var bDy = (a.X - c.X) * inverseArea;
        var origin = new Vector2(minX + 0.5f, minY + 0.5f);
        var startA = Cross(b - origin, c - origin) * inverseArea;
        var startB = Cross(c - origin, a - origin) * inverseArea;
        var sign = MathF.Sign(area);
        var edgeA = TopLeft((c - b) * sign); var edgeB = TopLeft((a - c) * sign); var edgeC = TopLeft((b - a) * sign);
        var material = triangle.Material;
        for (var y = minY; y <= maxY; y++, startA += aDy, startB += bDy)
        {
            var wa = startA; var wb = startB;
            for (var x = minX; x <= maxX; x++, wa += aDx, wb += bDx)
            {
                var wc = 1 - wa - wb;
                if (!Inside(wa, edgeA) || !Inside(wb, edgeB) || !Inside(wc, edgeC)) continue;
                var iw = wa * triangle.A.InverseW + wb * triangle.B.InverseW + wc * triangle.C.InverseW;
                if (!float.IsFinite(iw) || iw <= 0) continue;
                var weights = new Vector3(wa * triangle.A.InverseW, wb * triangle.B.InverseW, wc * triangle.C.InverseW) / iw;
                var z = weights.X * triangle.A.Screen.Z + weights.Y * triangle.B.Screen.Z + weights.Z * triangle.C.Screen.Z;
                var index = y * width + x;
                if (!float.IsFinite(z) || z > depth[index] + 0.00001f) continue;
                var color = Shade(triangle, weights);
                if (material.Blend == 1 && color.W < 0.5f) continue;
                if (material.Blend <= 1) color.W = 1;
                if (color.W <= 0 && material.Blend is not 3 and not 5 and not 6) continue;
                colors[index] = Blend(color, colors[index], material.Blend);
                if (material.DepthWrite) depth[index] = z;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Inside(float weight, bool topLeft) => weight > 0.000001f || weight >= -0.000001f && topLeft;
    private static bool TopLeft(Vector2 edge) => edge.Y < 0 || edge.Y == 0 && edge.X > 0;
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Byte(float value) => (byte)Math.Clamp((int)(value * 255 + 0.5f), 0, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static Vector4 Shade(in Triangle triangle, Vector3 weights)
    {
        var light = triangle.A.Light * weights.X + triangle.B.Light * weights.Y + triangle.C.Light * weights.Z;
        var textures = triangle.Material.Textures;
        if (textures.Length == 0) return new(new Vector3(150 / 255f, 190 / 255f, 220 / 255f) * light, 1);
        if (textures.Length == 1)
        {
            var color = ReadStage(textures[0], triangle, weights); return color * new Vector4(light, 1);
        }
        var result = Vector4.Zero;
        foreach (var pass in triangle.Material.Passes)
        {
            var stage = textures[pass.StageIndex]; var color = ReadStage(stage, triangle, weights);
            if (pass.UseLighting) color *= new Vector4(light, 1);
            if (pass.UseEdgeFade) color.W *= triangle.A.EdgeFade * weights.X + triangle.B.EdgeFade * weights.Y + triangle.C.EdgeFade * weights.Z;
            if (stage.Blend == M2PreviewTextureStageBlend.Modulate2X) color *= new Vector4(2, 2, 2, 1);
            var premultiplied = new Vector4(new Vector3(color.X, color.Y, color.Z) * color.W, color.W);
            result = pass.Blend switch
            {
                M2TextureRenderPassBlend.Modulate => result * premultiplied,
                M2TextureRenderPassBlend.Add => result + premultiplied,
                M2TextureRenderPassBlend.AddNoAlpha => result + new Vector4(color.X, color.Y, color.Z, 0),
                M2TextureRenderPassBlend.DestinationOut => result * (1 - color.W),
                M2TextureRenderPassBlend.DestinationOver => result + premultiplied * (1 - result.W),
                _ => premultiplied + result * (1 - color.W)
            };
        }
        return result.W > 0 ? new(result.X / result.W, result.Y / result.W, result.Z / result.W, Math.Min(1, result.W)) : result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static Vector4 ReadStage(Texture texture, in Triangle triangle, Vector3 weights)
    {
        var uv = texture.Coordinates switch
        {
            M2PreviewTextureCoordinateSource.Secondary => triangle.A.Uv2 * weights.X + triangle.B.Uv2 * weights.Y + triangle.C.Uv2 * weights.Z,
            M2PreviewTextureCoordinateSource.Environment => triangle.A.Environment * weights.X + triangle.B.Environment * weights.Y + triangle.C.Environment * weights.Z,
            _ => triangle.A.Uv * weights.X + triangle.B.Uv * weights.Y + triangle.C.Uv * weights.Z
        };
        if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) return Vector4.Zero;
        var x = (texture.WrapU ? uv.X - MathF.Floor(uv.X) : Math.Clamp(uv.X, 0, 1)) * texture.Width - 0.5f;
        var y = (texture.WrapV ? uv.Y - MathF.Floor(uv.Y) : Math.Clamp(uv.Y, 0, 1)) * texture.Height - 0.5f;
        var left = (int)MathF.Floor(x); var top = (int)MathF.Floor(y); var fx = x - left; var fy = y - top;
        var x0 = Coordinate(left, texture.Width, texture.WrapU); var x1 = Coordinate(left + 1, texture.Width, texture.WrapU);
        var y0 = Coordinate(top, texture.Height, texture.WrapV) * texture.Width; var y1 = Coordinate(top + 1, texture.Height, texture.WrapV) * texture.Width;
        return Vector4.Lerp(Vector4.Lerp(Color(texture.Pixels[y0 + x0]), Color(texture.Pixels[y0 + x1]), fx),
            Vector4.Lerp(Color(texture.Pixels[y1 + x0]), Color(texture.Pixels[y1 + x1]), fx), fy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Coordinate(int value, int size, bool wrap) => wrap ? (value % size + size) % size : Math.Clamp(value, 0, size - 1);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector4 Color(SKColor color) => new Vector4(color.Red, color.Green, color.Blue, color.Alpha) / 255;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static Vector4 Blend(Vector4 source, Vector4 destination, ushort mode)
    {
        var rgb = new Vector3(source.X, source.Y, source.Z); var dst = new Vector3(destination.X, destination.Y, destination.Z);
        var alpha = source.W + destination.W * (1 - source.W);
        return mode switch
        {
            0 or 1 => source,
            3 => new(rgb + dst, Math.Max(source.W, destination.W)),
            4 => new(rgb * source.W + dst, alpha),
            5 => new(rgb * dst, destination.W),
            6 => new(2 * rgb * dst, destination.W),
            7 => new(rgb + dst * (1 - source.W), alpha),
            _ => new(rgb * source.W + dst * (1 - source.W), alpha)
        };
    }
}
