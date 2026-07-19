using System.Numerics;

namespace WoWCrucible.Core;

/// <summary>Fixed-function OpenGL sphere-map coordinates for Crucible's X/Z screen and Y depth convention.</summary>
public static class M2EnvironmentMapService
{
    public static Vector2 Coordinate(Vector3 previewSpaceNormal)
    {
        if (!Finite(previewSpaceNormal) || previewSpaceNormal.LengthSquared() < 0.0000001f) return new(0.5f, 0.5f);
        previewSpaceNormal = Vector3.Normalize(previewSpaceNormal);

        // Crucible projects preview X horizontally, Z vertically and uses Y as depth.
        // Convert that basis to conventional OpenGL eye space before applying the
        // fixed-function GL_SPHERE_MAP reflection equation.
        var eyeNormal = Vector3.Normalize(new Vector3(previewSpaceNormal.X, previewSpaceNormal.Z, -previewSpaceNormal.Y));
        var reflection = Vector3.Reflect(new Vector3(0, 0, -1), eyeNormal);
        var denominator = 2f * MathF.Sqrt(reflection.X * reflection.X + reflection.Y * reflection.Y + (reflection.Z + 1f) * (reflection.Z + 1f));
        if (!float.IsFinite(denominator) || denominator < 0.0000001f) return new(0.5f, 0.5f);
        var coordinate = new Vector2(0.5f + reflection.X / denominator, 0.5f + reflection.Y / denominator);
        return Finite(coordinate) ? Vector2.Clamp(coordinate, Vector2.Zero, Vector2.One) : new(0.5f, 0.5f);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
