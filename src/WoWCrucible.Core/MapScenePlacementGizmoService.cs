using System.Numerics;

namespace WoWCrucible.Core;

/// <summary>
/// Deterministic transform math shared by the visual ADT placement gizmo and
/// tests. Scale limits are the actual unsigned 16-bit Wrath placement format,
/// not an arbitrary GUI restriction.
/// </summary>
public static class MapScenePlacementGizmoService
{
    public const float MinimumScale = 1f / 1024f;
    public const float MaximumScale = ushort.MaxValue / 1024f;

    public static Vector3 RotateZ(Vector3 baselineDegrees, float horizontalPixels, float degreesPerPixel = 0.5f)
    {
        if (!Finite(baselineDegrees) || !float.IsFinite(horizontalPixels) || !float.IsFinite(degreesPerPixel) || degreesPerPixel <= 0) throw new ArgumentException("Placement rotation requires finite inputs and a positive sensitivity.");
        return baselineDegrees with { Z = NormalizeDegrees(baselineDegrees.Z + horizontalPixels * degreesPerPixel) };
    }

    public static float UniformScale(float baselineScale, float upwardPixels, float sensitivity = 0.01f)
    {
        if (!float.IsFinite(baselineScale) || baselineScale <= 0 || !float.IsFinite(upwardPixels) || !float.IsFinite(sensitivity) || sensitivity <= 0) throw new ArgumentException("Placement scale requires finite inputs and positive scale/sensitivity.");
        return Math.Clamp(baselineScale * MathF.Exp(upwardPixels * sensitivity), MinimumScale, MaximumScale);
    }

    public static bool IsEncodableScale(float scale) => float.IsFinite(scale) && scale >= MinimumScale && scale <= MaximumScale;

    private static float NormalizeDegrees(float value)
    {
        var normalized = value % 360f; if (normalized > 180f) normalized -= 360f; else if (normalized <= -180f) normalized += 360f; return normalized;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
