using System.Numerics;

namespace WoWCrucible.Core;

public sealed record M2CameraProjection(Vector3 Position, Vector3 Forward, Vector3 Right, Vector3 Up, float VerticalTangent, float NearClip, float FarClip)
{
    public Vector3 ToViewPoint(Vector3 worldPoint)
    {
        var relative = worldPoint - Position;
        return new(Vector3.Dot(relative, Right), Vector3.Dot(relative, Forward), Vector3.Dot(relative, Up));
    }

    public Vector3 ToViewNormal(Vector3 worldNormal) => new(Vector3.Dot(worldNormal, Right), Vector3.Dot(worldNormal, Forward), Vector3.Dot(worldNormal, Up));
    public bool ContainsDepth(float depth) => float.IsFinite(depth) && depth > NearClip && depth < FarClip;
    public Vector3 Project(Vector3 viewPoint) => viewPoint.Y > 0.000001f
        ? new(viewPoint.X / (viewPoint.Y * VerticalTangent), viewPoint.Y, viewPoint.Z / (viewPoint.Y * VerticalTangent))
        : new(0, viewPoint.Y, 0);
}

public static class M2CameraProjectionService
{
    public static M2CameraProjection? TryCreate(M2PreviewCamera camera, M2PreviewCameraPose pose, Matrix4x4 sceneTransform)
    {
        ArgumentNullException.ThrowIfNull(camera); ArgumentNullException.ThrowIfNull(pose);
        if (!Finite(sceneTransform) || !Finite(pose.Position) || !Finite(pose.Target) || !float.IsFinite(pose.RollRadians)) return null;
        var position = Vector3.Transform(pose.Position, sceneTransform); var target = Vector3.Transform(pose.Target, sceneTransform); var upCandidate = Vector3.TransformNormal(Vector3.UnitZ, sceneTransform);
        var forward = target - position;
        if (forward.LengthSquared() < 0.0000001f || upCandidate.LengthSquared() < 0.0000001f) return null;
        forward = Vector3.Normalize(forward); upCandidate = Vector3.Normalize(upCandidate); var right = Vector3.Cross(forward, upCandidate);
        if (right.LengthSquared() < 0.0000001f) right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 0.0000001f) return null;
        right = Vector3.Normalize(right); var up = Vector3.Normalize(Vector3.Cross(right, forward));
        var cosine = MathF.Cos(pose.RollRadians); var sine = MathF.Sin(pose.RollRadians); var unrolledRight = right; var unrolledUp = up;
        right = unrolledRight * cosine + unrolledUp * sine; up = -unrolledRight * sine + unrolledUp * cosine;
        var tangent = MathF.Tan(camera.FieldOfViewRadians * 0.5f);
        return !float.IsFinite(tangent) || tangent <= 0.000001f ? null : new(position, forward, right, up, tangent, camera.NearClip, camera.FarClip);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
