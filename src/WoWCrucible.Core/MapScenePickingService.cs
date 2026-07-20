using System.Numerics;

namespace WoWCrucible.Core;

public sealed record MapSceneProjection(Vector3 Center, Matrix4x4 View, float Scale, float Width, float Height);
public sealed record MapSceneTerrainPick(Vector3 WorldPosition, int CellX, int CellY, int TriangleIndex, float ViewDepth, Vector2 ScreenPosition);

/// <summary>
/// Uses the same orthographic yaw/pitch/zoom projection as the native map
/// renderer and returns the frontmost exact MCVT triangle hit. World position
/// is barycentrically reconstructed, so the authoring fields receive the real
/// ADT coordinate rather than a screen-space estimate.
/// </summary>
public static class MapScenePickingService
{
    private const int MaximumTriangles = 200_000;

    public static MapSceneProjection CreateProjection(Vector3 sceneMinimum, Vector3 sceneMaximum, float yaw, float pitch, float zoom, float width, float height)
    {
        if (!Finite(sceneMinimum) || !Finite(sceneMaximum) || !float.IsFinite(yaw) || !float.IsFinite(pitch) || !float.IsFinite(zoom) || zoom <= 0 || !float.IsFinite(width) || !float.IsFinite(height) || width <= 1 || height <= 1)
            throw new ArgumentException("Map scene projection requires finite ordered bounds, angles, positive zoom, and a drawable viewport.");
        var extent = sceneMaximum - sceneMinimum; if (extent.X < 0 || extent.Y < 0 || extent.Z < 0) throw new ArgumentException("Map scene projection bounds are reversed.");
        var largest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)); if (!float.IsFinite(largest) || largest <= 0.0001f) throw new InvalidOperationException("Map scene bounds have no projectable extent.");
        return new((sceneMinimum + sceneMaximum) * 0.5f, Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateRotationX(pitch), Math.Min(width, height) * 0.43f / largest * zoom, width, height);
    }

    public static (Vector2 Screen, float Depth) Project(Vector3 world, MapSceneProjection projection)
    {
        if (!Finite(world)) throw new ArgumentException("Projected world position must be finite.", nameof(world));
        var view = Vector3.Transform(world - projection.Center, projection.View); var screen = new Vector2(projection.Width * 0.5f + view.X * projection.Scale, projection.Height * 0.5f - view.Z * projection.Scale);
        if (!float.IsFinite(screen.X) || !float.IsFinite(screen.Y) || !float.IsFinite(view.Y)) throw new InvalidDataException("Map scene projection produced a non-finite point.");
        return (screen, view.Y);
    }

    public static MapSceneTerrainPick? PickTerrain(AdtTerrainSceneGeometry terrain, Vector3 sceneMinimum, Vector3 sceneMaximum,
        float yaw, float pitch, float zoom, float width, float height, Vector2 screenPoint)
    {
        ArgumentNullException.ThrowIfNull(terrain); if (!float.IsFinite(screenPoint.X) || !float.IsFinite(screenPoint.Y)) throw new ArgumentException("Terrain pick point must be finite.", nameof(screenPoint));
        var triangleCount = terrain.TriangleIndices.Count / 3; if (terrain.TriangleIndices.Count % 3 != 0 || triangleCount > MaximumTriangles) throw new InvalidDataException($"Terrain pick requires complete triangles within the {MaximumTriangles:N0}-triangle bound.");
        var projection = CreateProjection(sceneMinimum, sceneMaximum, yaw, pitch, zoom, width, height); MapSceneTerrainPick? best = null;
        foreach (var cell in terrain.Cells)
        {
            if (cell.TriangleStart < 0 || cell.TriangleIndexCount < 0 || cell.TriangleStart + cell.TriangleIndexCount > terrain.TriangleIndices.Count || cell.TriangleIndexCount % 3 != 0) throw new InvalidDataException($"Terrain cell {cell.CellX},{cell.CellY} has an invalid triangle slice.");
            for (var offset = cell.TriangleStart; offset < cell.TriangleStart + cell.TriangleIndexCount; offset += 3)
            {
                var ia = terrain.TriangleIndices[offset]; var ib = terrain.TriangleIndices[offset + 1]; var ic = terrain.TriangleIndices[offset + 2];
                if ((uint)ia >= (uint)terrain.Vertices.Count || (uint)ib >= (uint)terrain.Vertices.Count || (uint)ic >= (uint)terrain.Vertices.Count) throw new InvalidDataException($"Terrain cell {cell.CellX},{cell.CellY} references a vertex outside the scene geometry.");
                var a = terrain.Vertices[ia]; var b = terrain.Vertices[ib]; var c = terrain.Vertices[ic]; var pa = Project(a, projection); var pb = Project(b, projection); var pc = Project(c, projection);
                if (screenPoint.X < Math.Min(pa.Screen.X, Math.Min(pb.Screen.X, pc.Screen.X)) - 0.01f || screenPoint.X > Math.Max(pa.Screen.X, Math.Max(pb.Screen.X, pc.Screen.X)) + 0.01f || screenPoint.Y < Math.Min(pa.Screen.Y, Math.Min(pb.Screen.Y, pc.Screen.Y)) - 0.01f || screenPoint.Y > Math.Max(pa.Screen.Y, Math.Max(pb.Screen.Y, pc.Screen.Y)) + 0.01f) continue;
                if (!Barycentric(screenPoint, pa.Screen, pb.Screen, pc.Screen, out var wa, out var wb, out var wc)) continue;
                var depth = pa.Depth * wa + pb.Depth * wb + pc.Depth * wc; if (best is not null && depth >= best.ViewDepth) continue;
                var world = a * wa + b * wb + c * wc; if (!Finite(world)) throw new InvalidDataException("Terrain pick interpolation produced a non-finite world position.");
                best = new(world, cell.CellX, cell.CellY, offset / 3, depth, screenPoint);
            }
        }
        return best;
    }

    private static bool Barycentric(Vector2 point, Vector2 a, Vector2 b, Vector2 c, out float wa, out float wb, out float wc)
    {
        var denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y); if (!float.IsFinite(denominator) || Math.Abs(denominator) < 0.00001f) { wa = wb = wc = 0; return false; }
        wa = ((b.Y - c.Y) * (point.X - c.X) + (c.X - b.X) * (point.Y - c.Y)) / denominator;
        wb = ((c.Y - a.Y) * (point.X - c.X) + (a.X - c.X) * (point.Y - c.Y)) / denominator; wc = 1f - wa - wb;
        const float tolerance = 0.0002f; return wa >= -tolerance && wb >= -tolerance && wc >= -tolerance && wa <= 1 + tolerance && wb <= 1 + tolerance && wc <= 1 + tolerance;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
