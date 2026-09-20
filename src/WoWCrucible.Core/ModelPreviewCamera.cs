using System.Numerics;

namespace WoWCrucible.Core;

public sealed record ModelPreviewCameraState(Vector3 Target, Vector3 ModelOffset, float Yaw, float Pitch, float Zoom, float Extent)
{
    public Matrix4x4 Rotation => Matrix4x4.CreateRotationZ(Yaw) * Matrix4x4.CreateRotationX(Pitch);
    public float Scale(float width, float height) => Math.Max(1, Math.Min(width, height)) * 0.82f / Extent * Zoom;
}

public sealed class ModelPreviewCamera
{
    public ModelPreviewCameraState State { get; private set; } = new(Vector3.Zero, Vector3.Zero, -MathF.PI / 2, 0.08f, 1, 1);

    public void Frame(Vector3 minimum, Vector3 maximum)
    {
        var size = maximum - minimum;
        var extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (!float.IsFinite(extent) || extent <= 0) throw new ArgumentException("Camera framing needs finite, non-empty bounds.");
        State = new(minimum + size * 0.5f, Vector3.Zero, -MathF.PI / 2, 0.08f, 1, extent);
    }

    public void Focus(Vector3 minimum, Vector3 maximum)
    {
        var size = maximum - minimum;
        var extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (!float.IsFinite(extent) || extent <= 0) return;
        State = State with { Target = minimum + size * 0.5f + State.ModelOffset, Zoom = Math.Clamp(State.Extent / extent * 0.85f, 0.05f, 100f) };
    }

    public void Orbit(float dx, float dy) => State = State with { Yaw = State.Yaw + dx * 0.012f, Pitch = Math.Clamp(State.Pitch + dy * 0.012f, -1.55f, 1.55f) };
    public void Zoom(float wheelDelta) => State = State with { Zoom = Math.Clamp(State.Zoom * MathF.Pow(1.12f, wheelDelta), 0.05f, 100f) };

    public void PanTarget(float dx, float dy, float width, float height, Matrix4x4 sceneTransform) =>
        State = State with { Target = State.Target - ScreenDelta(dx, dy, width, height, sceneTransform) };

    public void MoveModel(float dx, float dy, float width, float height, Matrix4x4 sceneTransform) =>
        State = State with { ModelOffset = State.ModelOffset + ScreenDelta(dx, dy, width, height, sceneTransform) };

    private Vector3 ScreenDelta(float dx, float dy, float width, float height, Matrix4x4 sceneTransform)
    {
        if (!Matrix4x4.Invert(sceneTransform * State.Rotation, out var inverse)) return Vector3.Zero;
        var scale = State.Scale(width, height);
        return Vector3.TransformNormal(new Vector3(dx / scale, 0, -dy / scale), inverse);
    }
}
