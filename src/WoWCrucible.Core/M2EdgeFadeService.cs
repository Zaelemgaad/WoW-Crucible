using System.Numerics;

namespace WoWCrucible.Core;

/// <summary>Produces the linear face-on opacity used by Crucible's approximate M2 edge-fade route.</summary>
public static class M2EdgeFadeService
{
    public static float Opacity(Vector3 viewSpaceNormal, Vector3 directionToViewer)
    {
        if (!Finite(viewSpaceNormal) || !Finite(directionToViewer) || viewSpaceNormal.LengthSquared() < 0.0000001f || directionToViewer.LengthSquared() < 0.0000001f)
            return 1f;
        var facing = MathF.Abs(Vector3.Dot(Vector3.Normalize(viewSpaceNormal), Vector3.Normalize(directionToViewer)));
        return float.IsFinite(facing) ? Math.Clamp(facing, 0f, 1f) : 1f;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
