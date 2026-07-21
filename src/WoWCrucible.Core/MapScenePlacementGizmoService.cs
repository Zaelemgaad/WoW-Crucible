using System.Numerics;

namespace WoWCrucible.Core;

public sealed record MapScenePlacementTransform(Vector3 Position, Vector3 Orientation, float Scale);
public enum MapScenePlacementAxis { X, Y, Z }
public enum MapScenePlacementAxisSpace { World, LocalModel }
public sealed record MapScenePlacementSnapSettings(float PositionStep, float RotationStep, float ScaleStep)
{
    public static MapScenePlacementSnapSettings Disabled { get; } = new(0, 0, 0);
}
public sealed record MapSceneTerrainSnap(Vector3 WorldPosition, int CellX, int CellY, int TriangleIndex);

/// <summary>Gesture-level history for an unsaved placement preview.</summary>
public sealed class MapScenePlacementHistory
{
    private readonly List<MapScenePlacementTransform> _states = [];
    private int _index = -1;

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index >= 0 && _index + 1 < _states.Count;
    public MapScenePlacementTransform? Current => _index >= 0 ? _states[_index] : null;

    public void Reset(MapScenePlacementTransform state) { Validate(state); _states.Clear(); _states.Add(state); _index = 0; }

    public bool Commit(MapScenePlacementTransform state)
    {
        Validate(state); if (_index < 0) { Reset(state); return true; } if (_states[_index] == state) return false;
        if (_index + 1 < _states.Count) _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        _states.Add(state); _index++; return true;
    }

    public void ReplaceCurrent(MapScenePlacementTransform state)
    {
        Validate(state); if (_index < 0) { Reset(state); return; }
        if (_states[_index] == state) return;
        if (_index + 1 < _states.Count) _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        _states[_index] = state;
    }

    public MapScenePlacementTransform Undo()
    {
        if (!CanUndo) throw new InvalidOperationException("Placement preview history has no earlier gesture."); return _states[--_index];
    }

    public MapScenePlacementTransform Redo()
    {
        if (!CanRedo) throw new InvalidOperationException("Placement preview history has no later gesture."); return _states[++_index];
    }

    private static void Validate(MapScenePlacementTransform state)
    {
        ArgumentNullException.ThrowIfNull(state); if (!MapScenePlacementGizmoService.IsFinite(state.Position) || !MapScenePlacementGizmoService.IsFinite(state.Orientation) || !MapScenePlacementGizmoService.IsEncodableScale(state.Scale)) throw new ArgumentException("Placement history requires a finite transform with a WotLK-encodable scale.", nameof(state));
    }
}

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
        => RotateAxis(baselineDegrees, MapScenePlacementAxis.Z, horizontalPixels, degreesPerPixel);

    public static Vector3 RotateAxis(Vector3 baselineDegrees, MapScenePlacementAxis axis, float horizontalPixels, float degreesPerPixel = 0.5f)
    {
        if (!IsFinite(baselineDegrees) || !Enum.IsDefined(axis) || !float.IsFinite(horizontalPixels) || !float.IsFinite(degreesPerPixel) || degreesPerPixel <= 0) throw new ArgumentException("Placement axis rotation requires finite inputs, a defined axis, and a positive sensitivity.");
        var baseline = axis switch { MapScenePlacementAxis.X => baselineDegrees.X, MapScenePlacementAxis.Y => baselineDegrees.Y, _ => baselineDegrees.Z }; var value = NormalizeDegrees((float)(((double)baseline + (double)horizontalPixels * degreesPerPixel) % 360d));
        return axis switch { MapScenePlacementAxis.X => baselineDegrees with { X = value }, MapScenePlacementAxis.Y => baselineDegrees with { Y = value }, _ => baselineDegrees with { Z = value } };
    }

    public static Vector3 AxisDirection(Vector3 orientationDegrees, MapScenePlacementAxis axis, MapScenePlacementAxisSpace space)
    {
        if (!IsFinite(orientationDegrees) || !Enum.IsDefined(axis) || !Enum.IsDefined(space)) throw new ArgumentException("Placement axis direction requires finite orientation and defined axis/space.");
        var direction = axis switch { MapScenePlacementAxis.X => Vector3.UnitX, MapScenePlacementAxis.Y => Vector3.UnitY, _ => Vector3.UnitZ };
        if (space == MapScenePlacementAxisSpace.LocalModel) direction = Vector3.TransformNormal(direction, M2PreviewSceneService.MapObjectTransform(orientationDegrees, 1));
        if (!IsFinite(direction) || Math.Abs(direction.LengthSquared() - 1f) > .001f) throw new InvalidDataException("Placement axis transform did not produce a unit direction."); return Vector3.Normalize(direction);
    }

    public static Vector3 TranslateAxis(Vector3 baselinePosition, Vector3 orientationDegrees, MapScenePlacementAxis axis, MapScenePlacementAxisSpace space, float distance)
    {
        if (!IsFinite(baselinePosition) || !float.IsFinite(distance)) throw new ArgumentException("Placement axis translation requires finite position and distance.");
        var result = baselinePosition + AxisDirection(orientationDegrees, axis, space) * distance; return IsFinite(result) ? result : throw new InvalidOperationException("Placement axis translation produced a non-finite position.");
    }

    public static float ProjectedAxisDragDistance(Vector2 pointerDelta, Vector2 projectedUnitAxis)
    {
        if (!float.IsFinite(pointerDelta.X) || !float.IsFinite(pointerDelta.Y) || !float.IsFinite(projectedUnitAxis.X) || !float.IsFinite(projectedUnitAxis.Y)) throw new ArgumentException("Projected placement dragging requires finite screen vectors.");
        var length = projectedUnitAxis.Length(); if (length < .001f) throw new InvalidOperationException("The selected placement axis points into the current camera and cannot be dragged from this view."); return Vector2.Dot(pointerDelta, projectedUnitAxis / length) / length;
    }

    public static float UniformScale(float baselineScale, float upwardPixels, float sensitivity = 0.01f)
    {
        if (!float.IsFinite(baselineScale) || baselineScale <= 0 || !float.IsFinite(upwardPixels) || !float.IsFinite(sensitivity) || sensitivity <= 0) throw new ArgumentException("Placement scale requires finite inputs and positive scale/sensitivity.");
        return Math.Clamp(baselineScale * MathF.Exp(upwardPixels * sensitivity), MinimumScale, MaximumScale);
    }

    public static bool IsEncodableScale(float scale) => float.IsFinite(scale) && scale >= MinimumScale && scale <= MaximumScale;

    public static MapScenePlacementSnapSettings ValidateSnapSettings(MapScenePlacementSnapSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings); if (!Step(settings.PositionStep) || !Step(settings.RotationStep) || !Step(settings.ScaleStep)) throw new ArgumentException("Placement snap steps must be finite and zero or positive.", nameof(settings)); return settings;
    }

    public static Vector3 SnapRotation(Vector3 orientationDegrees, float step)
        => SnapRotationAxis(orientationDegrees, MapScenePlacementAxis.Z, step);

    public static Vector3 SnapRotationAxis(Vector3 orientationDegrees, MapScenePlacementAxis axis, float step)
    {
        if (!IsFinite(orientationDegrees) || !Enum.IsDefined(axis) || !Step(step)) throw new ArgumentException("Placement rotation snapping requires finite orientation, a defined axis, and a nonnegative step.");
        if (step == 0) return orientationDegrees; var value = NormalizeDegrees(Snap(NormalizeDegrees(axis switch { MapScenePlacementAxis.X => orientationDegrees.X, MapScenePlacementAxis.Y => orientationDegrees.Y, _ => orientationDegrees.Z }), step));
        return axis switch { MapScenePlacementAxis.X => orientationDegrees with { X = value }, MapScenePlacementAxis.Y => orientationDegrees with { Y = value }, _ => orientationDegrees with { Z = value } };
    }

    public static float SnapDistance(float distance, float step)
    {
        if (!float.IsFinite(distance) || !Step(step)) throw new ArgumentException("Placement distance snapping requires finite distance and a nonnegative step."); return step == 0 ? distance : Snap(distance, step);
    }

    public static float SnapScale(float scale, float step)
    {
        if (!IsEncodableScale(scale) || !Step(step)) throw new ArgumentException("Placement scale snapping requires an encodable scale and a nonnegative step.");
        if (step == 0) return scale; var snapped = Snap(scale, step); if (snapped <= 0) snapped = step; return Math.Clamp(snapped, MinimumScale, MaximumScale);
    }

    /// <summary>Snaps horizontal world coordinates and then derives exact Z from the containing MCVT triangle.</summary>
    public static MapSceneTerrainSnap? SnapTerrain(AdtTerrainSceneGeometry terrain, Vector3 position, float step)
    {
        ArgumentNullException.ThrowIfNull(terrain); if (!IsFinite(position) || !Step(step)) throw new ArgumentException("Terrain snapping requires finite position and a nonnegative step.");
        if (terrain.TriangleIndices.Count % 3 != 0 || terrain.TriangleIndices.Count / 3 > 200_000) throw new InvalidDataException("Terrain snapping requires complete geometry within the 200,000-triangle scene bound.");
        if (step == 0) return null; var x = Snap(position.X, step); var y = Snap(position.Y, step); MapSceneTerrainSnap? best = null; var bestDistance = float.PositiveInfinity;
        foreach (var cell in terrain.Cells)
        {
            if (cell.TriangleStart < 0 || cell.TriangleIndexCount < 0 || cell.TriangleStart + cell.TriangleIndexCount > terrain.TriangleIndices.Count || cell.TriangleIndexCount % 3 != 0) throw new InvalidDataException($"Terrain cell {cell.CellX},{cell.CellY} has an invalid triangle slice.");
            for (var offset = cell.TriangleStart; offset < cell.TriangleStart + cell.TriangleIndexCount; offset += 3)
            {
                var ia = terrain.TriangleIndices[offset]; var ib = terrain.TriangleIndices[offset + 1]; var ic = terrain.TriangleIndices[offset + 2];
                if ((uint)ia >= (uint)terrain.Vertices.Count || (uint)ib >= (uint)terrain.Vertices.Count || (uint)ic >= (uint)terrain.Vertices.Count) throw new InvalidDataException($"Terrain cell {cell.CellX},{cell.CellY} references a vertex outside the scene geometry.");
                var a = terrain.Vertices[ia]; var b = terrain.Vertices[ib]; var c = terrain.Vertices[ic]; if (!Barycentric(x, y, a, b, c, out var wa, out var wb, out var wc)) continue;
                var z = a.Z * wa + b.Z * wb + c.Z * wc; var distance = Math.Abs(z - position.Z); if (!float.IsFinite(z) || distance >= bestDistance) continue;
                bestDistance = distance; best = new(new(x, y, z), cell.CellX, cell.CellY, offset / 3);
            }
        }
        return best;
    }

    private static float Snap(float value, float step)
    {
        var snapped = Math.Round((double)value / step, MidpointRounding.AwayFromZero) * step; if (!double.IsFinite(snapped) || snapped is > float.MaxValue or < -float.MaxValue) throw new InvalidOperationException("Placement snapping produced a non-finite value."); return (float)snapped;
    }

    private static bool Barycentric(float x, float y, Vector3 a, Vector3 b, Vector3 c, out float wa, out float wb, out float wc)
    {
        var denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y); if (!float.IsFinite(denominator) || Math.Abs(denominator) < 0.00001f) { wa = wb = wc = 0; return false; }
        wa = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / denominator; wb = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / denominator; wc = 1f - wa - wb;
        const float tolerance = 0.0002f; return wa >= -tolerance && wb >= -tolerance && wc >= -tolerance && wa <= 1 + tolerance && wb <= 1 + tolerance && wc <= 1 + tolerance;
    }

    private static bool Step(float value) => float.IsFinite(value) && value >= 0;

    private static float NormalizeDegrees(float value)
    {
        var normalized = value % 360f; if (normalized > 180f) normalized -= 360f; else if (normalized <= -180f) normalized += 360f; return normalized;
    }

    internal static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool Finite(Vector3 value) => IsFinite(value);
}
