using System.Numerics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

public sealed record MapSceneM2Instance(M2PreviewGeometry Geometry, MapM2Placement Placement, string SourcePath);
public sealed record MapSceneWmoInstance(WmoPreviewGeometry Geometry, MapWmoPlacement Placement, string SourcePath);
public enum MapScenePlacementEditMode { MoveOnTerrain, TranslateX, TranslateY, TranslateZ, RotateX, RotateY, RotateZ, UniformScale, History }
public sealed record MapScenePlacementPreview(IReadOnlyList<Vector3> Vertices, IReadOnlyList<int> TriangleIndices, AdtPlacementKind Kind,
    uint? UniqueId, string Label, Vector3 Position, Vector3 Orientation, float Scale);
public sealed record MapScenePlacementPreviewChanged(Vector3 Position, Vector3 Orientation, float Scale, MapScenePlacementEditMode Mode, string? Action = null);
public sealed record MapScenePlacementHistoryAvailability(bool CanUndo, bool CanRedo);

public sealed class MapSceneView : UserControl
{
    private readonly MapSceneCanvas _canvas = new(); private readonly CheckBox _terrain = new() { Content = "Terrain", IsChecked = true }; private readonly CheckBox _objects = new() { Content = "Placed objects", IsChecked = true }; private readonly CheckBox _wireframe = new() { Content = "Wireframe overlay" }; private readonly CheckBox _pick = new() { Content = "Pick placement position" }; private readonly CheckBox _edit = new() { Content = "Edit placement preview" }; private readonly CheckBox _snap = new() { Content = "Use configured snapping" }; private readonly ComboBox _editMode = new() { ItemsSource = new[] { "Move on terrain", "Translate X", "Translate Y", "Translate Z", "Rotate X", "Rotate Y", "Rotate Z", "Uniform scale" }, SelectedIndex = 0 }; private readonly ComboBox _axisSpace = new() { ItemsSource = new[] { "World axes", "Local model axes" }, SelectedIndex = 0 }; private readonly Button _undo = new() { Content = "Undo gesture", IsEnabled = false }; private readonly Button _redo = new() { Content = "Redo gesture", IsEnabled = false };
    public event EventHandler<MapSceneTerrainPick>? TerrainPicked;
    public event EventHandler<MapScenePlacementPreviewChanged>? PlacementPreviewChanged;
    public event EventHandler<string>? StatusChanged;
    public MapSceneView()
    {
        AutomationProperties.SetName(_canvas, "Interactive terrain placement canvas"); AutomationProperties.SetHelpText(_canvas, "Enable Pick placement position, then click a visible terrain triangle to choose exact ADT coordinates.");
        AutomationProperties.SetName(_editMode, "Placement gizmo mode"); AutomationProperties.SetName(_axisSpace, "Placement axis space"); ToolTip.SetTip(_axisSpace, "World/local selection changes translation and the displayed XYZ basis. Rotation modes edit the exact stored Euler X/Y/Z fields; Wrath placements support only uniform scale.");
        AutomationProperties.SetName(_snap, "Use configured placement snapping"); AutomationProperties.SetName(_undo, "Undo placement gesture"); AutomationProperties.SetName(_redo, "Redo placement gesture"); ToolTip.SetTip(_snap, "Position, rotation-degree, and scale steps are configured in the same-window placement controls. Zero disables that kind of snap.");
        var reset = new Button { Content = "Reset view" }; reset.Click += (_, _) => _canvas.ResetView();
        var clearPick = new Button { Content = "Clear marker" }; clearPick.Click += (_, _) => { _canvas.ClearPick(); StatusChanged?.Invoke(this, "Placement marker cleared. Enable placement picking and click terrain to choose another exact point."); };
        _terrain.IsCheckedChanged += (_, _) => _canvas.SetVisibility(_terrain.IsChecked == true, _objects.IsChecked == true, _wireframe.IsChecked == true); _objects.IsCheckedChanged += (_, _) => _canvas.SetVisibility(_terrain.IsChecked == true, _objects.IsChecked == true, _wireframe.IsChecked == true); _wireframe.IsCheckedChanged += (_, _) => _canvas.SetVisibility(_terrain.IsChecked == true, _objects.IsChecked == true, _wireframe.IsChecked == true);
        _pick.IsCheckedChanged += (_, _) => { if (_pick.IsChecked == true && _edit.IsChecked == true) _edit.IsChecked = false; _canvas.SetPickMode(_pick.IsChecked == true); StatusChanged?.Invoke(this, _pick.IsChecked == true ? "PICK MODE · terrain is framed for exact selection. Click one visible triangle; drag rotation is paused until pick mode is disabled." : "View mode · drag to rotate and use the wheel to zoom. The current marker remains visible."); };
        _edit.IsCheckedChanged += (_, _) => { if (_edit.IsChecked == true && _pick.IsChecked == true) _pick.IsChecked = false; _canvas.SetEditEnabled(_edit.IsChecked == true); StatusChanged?.Invoke(this, _edit.IsChecked == true ? "PLACEMENT EDIT · use the selected gizmo mode on the gold preview. Changes update unsaved fields only." : "Placement preview remains visible. Edit mode is off; drag rotates the camera again."); };
        _editMode.SelectionChanged += (_, _) => _canvas.SetEditMode((MapScenePlacementEditMode)Math.Clamp(_editMode.SelectedIndex, 0, 7)); _axisSpace.SelectionChanged += (_, _) => _canvas.SetAxisSpace(_axisSpace.SelectedIndex == 1 ? MapScenePlacementAxisSpace.LocalModel : MapScenePlacementAxisSpace.World);
        _snap.IsCheckedChanged += (_, _) => _canvas.SetSnapEnabled(_snap.IsChecked == true); _undo.Click += (_, _) => _canvas.UndoPlacementPreview(); _redo.Click += (_, _) => _canvas.RedoPlacementPreview();
        _canvas.TerrainPicked += (_, value) => { StatusChanged?.Invoke(this, $"PICKED MCNK {value.CellX},{value.CellY} · X {value.WorldPosition.X:0.###} · Y {value.WorldPosition.Y:0.###} · Z {value.WorldPosition.Z:0.###}"); TerrainPicked?.Invoke(this, value); };
        _canvas.TerrainPickMissed += (_, _) => StatusChanged?.Invoke(this, "No visible terrain triangle exists under that pixel. Rotate or zoom the scene and try again.");
        _canvas.PlacementSnapMissed += (_, _) => StatusChanged?.Invoke(this, "The configured position grid lands outside intact terrain or inside a hole at this pointer. The preview was left unchanged; use a finer step or drag elsewhere."); _canvas.PlacementAxisUnavailable += (_, _) => StatusChanged?.Invoke(this, "The selected axis points into the current camera. Rotate the view until that colored axis has visible screen length, then drag it again.");
        _canvas.PlacementPreviewChanged += (_, value) => { StatusChanged?.Invoke(this, value.Mode switch { MapScenePlacementEditMode.MoveOnTerrain or MapScenePlacementEditMode.TranslateX or MapScenePlacementEditMode.TranslateY or MapScenePlacementEditMode.TranslateZ => $"PREVIEW MOVED · X {value.Position.X:0.###} · Y {value.Position.Y:0.###} · Z {value.Position.Z:0.###}", MapScenePlacementEditMode.RotateX => $"PREVIEW ROTATED · X {value.Orientation.X:0.###}°", MapScenePlacementEditMode.RotateY => $"PREVIEW ROTATED · Y {value.Orientation.Y:0.###}°", MapScenePlacementEditMode.RotateZ => $"PREVIEW ROTATED · Z {value.Orientation.Z:0.###}°", MapScenePlacementEditMode.UniformScale => $"PREVIEW SCALED · {value.Scale:0.####}", _ => $"PREVIEW HISTORY · {value.Action ?? "state restored"}" }); PlacementPreviewChanged?.Invoke(this, value); };
        _canvas.HistoryAvailabilityChanged += (_, value) => { _undo.IsEnabled = value.CanUndo; _redo.IsEnabled = value.CanRedo; };
        var toolbar = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(7), Children = { _terrain, _objects, _wireframe, _pick, _edit, _editMode, _axisSpace, _snap, _undo, _redo, clearPick, reset } } };
        Content = new Grid { RowDefinitions = new("Auto,*"), Children = { toolbar, WithRow(_canvas, 1) } };
    }
    public void SetScene(AdtTerrainSceneGeometry terrain, AdtTerrainMaterialSet? materials, IReadOnlyList<MapSceneM2Instance> m2, IReadOnlyList<MapSceneWmoInstance> wmo, int unresolved, string provenance) => _canvas.SetScene(terrain, materials, m2, wmo, unresolved, provenance);
    public void ClearScene() => _canvas.ClearScene();
    public bool HasScene => _canvas.HasScene;
    public void SetPlacementPreview(MapScenePlacementPreview preview) => _canvas.SetPlacementPreview(preview);
    public void UpdatePlacementPreviewTransform(Vector3 position, Vector3 orientation, float scale) => _canvas.UpdatePlacementPreviewTransform(position, orientation, scale);
    public void SetPlacementSnapSettings(MapScenePlacementSnapSettings settings) => _canvas.SetSnapSettings(settings);
    public void ClearPlacementPreview() => _canvas.ClearPlacementPreview();
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}

internal sealed class MapSceneCanvas : Control
{
    private sealed record Mesh(IReadOnlyList<Vector3> Vertices, IReadOnlyList<int> Indices, IReadOnlyList<SKColor>? VertexColors, Matrix4x4 Transform, SKColor Color, string Kind, uint? UniqueId = null);
    private sealed record Scene(AdtTerrainSceneGeometry TerrainGeometry, IReadOnlyList<Mesh> Meshes, Vector3 Minimum, Vector3 Maximum, Vector3 TerrainMinimum, Vector3 TerrainMaximum, int M2, int Wmo, int Unresolved, string Provenance, int SourceTriangles, int MaterialCells, int CompleteMaterialCells);
    private readonly MapScenePlacementHistory _history = new(); private Scene? _scene; private MapSceneTerrainPick? _pick; private MapScenePlacementPreview? _preview; private MapScenePlacementSnapSettings _snapSettings = MapScenePlacementSnapSettings.Disabled; private float _yaw = -0.72f; private float _pitch = 0.72f; private float _zoom = 1f; private Point? _dragStart; private Point? _editStart; private MapScenePlacementPreview? _editBaseline; private bool _terrain = true; private bool _objects = true; private bool _wireframe; private bool _pickMode; private bool _editEnabled; private bool _snapEnabled; private MapScenePlacementEditMode _editMode; private MapScenePlacementAxisSpace _axisSpace;
    public event EventHandler<MapSceneTerrainPick>? TerrainPicked;
    public event EventHandler? TerrainPickMissed;
    public event EventHandler? PlacementSnapMissed;
    public event EventHandler? PlacementAxisUnavailable;
    public event EventHandler<MapScenePlacementPreviewChanged>? PlacementPreviewChanged;
    public event EventHandler<MapScenePlacementHistoryAvailability>? HistoryAvailabilityChanged;
    public bool HasScene => _scene is not null;
    public MapSceneCanvas() { ClipToBounds = true; Focusable = true; }
    protected override AutomationPeer OnCreateAutomationPeer() => new ControlAutomationPeer(this);
    public void SetScene(AdtTerrainSceneGeometry terrain, AdtTerrainMaterialSet? materials, IReadOnlyList<MapSceneM2Instance> m2, IReadOnlyList<MapSceneWmoInstance> wmo, int unresolved, string provenance)
    {
        var materialByCell = materials?.Cells.ToDictionary(value => (value.CellX, value.CellY)) ?? new Dictionary<(int, int), AdtTerrainMaterialCell>(); var meshes = new List<Mesh>();
        foreach (var cell in terrain.Cells)
        {
            var vertices = terrain.Vertices.Skip(cell.VertexStart).Take(cell.VertexCount).ToArray(); var indices = terrain.TriangleIndices.Skip(cell.TriangleStart).Take(cell.TriangleIndexCount).Select(value => value - cell.VertexStart).ToArray(); IReadOnlyList<SKColor>? colors = null;
            if (materialByCell.TryGetValue((cell.CellX, cell.CellY), out var material)) colors = Enumerable.Range(0, cell.VertexCount).Select(index => MaterialColor(material.Composite, index)).ToArray();
            meshes.Add(new(vertices, indices, colors, Matrix4x4.Identity, new SKColor(72, 121, 74), "terrain"));
        }
        meshes.AddRange(m2.Select(value => new Mesh(value.Geometry.Vertices, value.Geometry.TriangleIndices, null, M2PreviewSceneService.MapObjectTransform(value.Placement.Orientation, value.Placement.Scale, value.Placement.Position), new SKColor(91, 155, 213), "M2", value.Placement.UniqueId)));
        meshes.AddRange(wmo.Select(value => new Mesh(value.Geometry.Vertices, value.Geometry.TriangleIndices, null, M2PreviewSceneService.MapObjectTransform(value.Placement.Orientation, value.Placement.Scale, value.Placement.Position), new SKColor(178, 142, 91), "WMO", value.Placement.UniqueId)));
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity); var triangles = 0;
        foreach (var mesh in meshes)
        {
            triangles += mesh.Indices.Count / 3; foreach (var vertex in mesh.Vertices) { var world = Vector3.Transform(vertex, mesh.Transform); minimum = Vector3.Min(minimum, world); maximum = Vector3.Max(maximum, world); }
        }
        var terrainMinimum = new Vector3(float.PositiveInfinity); var terrainMaximum = new Vector3(float.NegativeInfinity); foreach (var vertex in terrain.Vertices) { terrainMinimum = Vector3.Min(terrainMinimum, vertex); terrainMaximum = Vector3.Max(terrainMaximum, vertex); }
        _scene = new(terrain, meshes, minimum, maximum, terrainMinimum, terrainMaximum, m2.Count, wmo.Count, unresolved, provenance, triangles, materials?.Cells.Count ?? 0, materials?.CompleteCells ?? 0); _pick = null; _preview = null; NotifyHistory(); ResetView();

        static SKColor MaterialColor(RgbaTexture texture, int vertexIndex)
        {
            var local = AdtTerrainBrushService.VertexPosition(vertexIndex); var x = Math.Clamp((int)Math.Round(local.X * (texture.Width - 1)), 0, texture.Width - 1); var y = Math.Clamp((int)Math.Round(local.Y * (texture.Height - 1)), 0, texture.Height - 1); var offset = (y * texture.Width + x) * 4; return new(texture.Pixels[offset], texture.Pixels[offset + 1], texture.Pixels[offset + 2], texture.Pixels[offset + 3]);
        }
    }
    public void ClearScene() { _scene = null; _pick = null; _preview = null; NotifyHistory(); InvalidateVisual(); }
    public void ClearPick() { _pick = null; InvalidateVisual(); }
    public void SetPlacementPreview(MapScenePlacementPreview preview) { ValidatePreview(preview); _preview = preview; _history.Reset(Transform(preview)); NotifyHistory(); InvalidateVisual(); }
    public void UpdatePlacementPreviewTransform(Vector3 position, Vector3 orientation, float scale)
    {
        if (_preview is null || !Finite(position) || !Finite(orientation) || !MapScenePlacementGizmoService.IsEncodableScale(scale)) return; _preview = _preview with { Position = position, Orientation = orientation, Scale = scale }; _history.ReplaceCurrent(Transform(_preview)); NotifyHistory(); InvalidateVisual();
    }
    public void SetSnapSettings(MapScenePlacementSnapSettings settings) { _snapSettings = MapScenePlacementGizmoService.ValidateSnapSettings(settings); }
    public void ClearPlacementPreview() { _preview = null; _editStart = null; _editBaseline = null; NotifyHistory(); InvalidateVisual(); }
    public void ResetView() { _yaw = -0.72f; _pitch = 0.72f; _zoom = 1; InvalidateVisual(); }
    public void SetVisibility(bool terrain, bool objects, bool wireframe) { _terrain = terrain; _objects = objects; _wireframe = wireframe; InvalidateVisual(); }
    public void SetPickMode(bool enabled) { _pickMode = enabled; _dragStart = null; InvalidateVisual(); }
    public void SetEditEnabled(bool enabled) { _editEnabled = enabled; _dragStart = null; _editStart = null; _editBaseline = null; InvalidateVisual(); }
    public void SetEditMode(MapScenePlacementEditMode mode) { _editMode = mode; _editStart = null; _editBaseline = null; InvalidateVisual(); }
    public void SetAxisSpace(MapScenePlacementAxisSpace space) { _axisSpace = space; _editStart = null; _editBaseline = null; InvalidateVisual(); }
    public void SetSnapEnabled(bool enabled) { _snapEnabled = enabled; InvalidateVisual(); }
    public void UndoPlacementPreview() { if (_preview is null || !_history.CanUndo) return; ApplyHistory(_history.Undo(), "UNDO"); }
    public void RedoPlacementPreview() { if (_preview is null || !_history.CanRedo) return; ApplyHistory(_history.Redo(), "REDO"); }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Brush.Parse("#080B10"), Bounds); if (_scene is null) { context.DrawText(new FormattedText("Build a terrain + placement scene from a loaded ADT", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brush.Parse("#8995A9")), new Point(16, 28)); return; }
        context.Custom(new DrawOperation(Bounds, _scene, _yaw, _pitch, _zoom, _terrain, _objects, _wireframe, _pickMode, _pick, _editEnabled, _editMode, _axisSpace, _preview));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; Focus();
        if (_pickMode)
        {
            var point = e.GetPosition(this); var scene = _scene; var picked = scene is null || !_terrain ? null : MapScenePickingService.PickTerrain(scene.TerrainGeometry, scene.TerrainMinimum, scene.TerrainMaximum, _yaw, _pitch, _zoom, (float)Bounds.Width, (float)Bounds.Height, new((float)point.X, (float)point.Y));
            if (picked is null) TerrainPickMissed?.Invoke(this, EventArgs.Empty); else { _pick = picked; TerrainPicked?.Invoke(this, picked); InvalidateVisual(); } e.Handled = true; return;
        }
        if (_editEnabled && _preview is not null)
        {
            var point = e.GetPosition(this); _editStart = point; _editBaseline = _preview; e.Pointer.Capture(this);
            if (_editMode == MapScenePlacementEditMode.MoveOnTerrain) UpdateMovePreview(point);
            e.Handled = true; return;
        }
        _dragStart = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; var point = e.GetPosition(this);
        if (_editEnabled && _editStart is { } editStart && _editBaseline is { } baseline)
        {
            if (_editMode == MapScenePlacementEditMode.MoveOnTerrain) UpdateMovePreview(point);
            else if (TranslationAxis(_editMode) is { } translationAxis) UpdateAxisTranslation(baseline, editStart, point, translationAxis);
            else if (RotationAxis(_editMode) is { } rotationAxis) { var orientation = MapScenePlacementGizmoService.RotateAxis(baseline.Orientation, rotationAxis, (float)(point.X - editStart.X)); if (_snapEnabled) orientation = MapScenePlacementGizmoService.SnapRotationAxis(orientation, rotationAxis, _snapSettings.RotationStep); UpdatePreview(baseline with { Orientation = orientation }); }
            else
            {
                var scale = MapScenePlacementGizmoService.UniformScale(baseline.Scale, (float)(editStart.Y - point.Y)); if (_snapEnabled) scale = MapScenePlacementGizmoService.SnapScale(scale, _snapSettings.ScaleStep); UpdatePreview(baseline with { Scale = scale });
            }
            e.Handled = true; return;
        }
        if (_dragStart is not { } start) return; _yaw += (float)(point.X - start.X) * 0.01f; _pitch = Math.Clamp(_pitch + (float)(point.Y - start.Y) * 0.01f, -1.5f, 1.5f); _dragStart = point; InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _dragStart = null; FinishEdit(); e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _dragStart = null; FinishEdit(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { base.OnPointerWheelChanged(e); _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.12f : 0.89f), 0.08f, 20f); InvalidateVisual(); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (!_editEnabled || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { RedoPlacementPreview(); e.Handled = true; }
        else if (e.Key == Key.Z) { UndoPlacementPreview(); e.Handled = true; }
        else if (e.Key == Key.Y) { RedoPlacementPreview(); e.Handled = true; }
    }

    private void UpdateMovePreview(Point point)
    {
        var scene = _scene; var baseline = _preview; if (scene is null || baseline is null || !_terrain) return; var picked = MapScenePickingService.PickTerrain(scene.TerrainGeometry, scene.TerrainMinimum, scene.TerrainMaximum, _yaw, _pitch, _zoom, (float)Bounds.Width, (float)Bounds.Height, new((float)point.X, (float)point.Y)); if (picked is null) return;
        if (_snapEnabled && _snapSettings.PositionStep > 0)
        {
            var snapped = MapScenePlacementGizmoService.SnapTerrain(scene.TerrainGeometry, picked.WorldPosition, _snapSettings.PositionStep); if (snapped is null) { PlacementSnapMissed?.Invoke(this, EventArgs.Empty); return; }
            picked = picked with { WorldPosition = snapped.WorldPosition, CellX = snapped.CellX, CellY = snapped.CellY, TriangleIndex = snapped.TriangleIndex };
        }
        _pick = picked; UpdatePreview(baseline with { Position = picked.WorldPosition });
    }
    private void UpdateAxisTranslation(MapScenePlacementPreview baseline, Point start, Point point, MapScenePlacementAxis axis)
    {
        var scene = _scene; if (scene is null) return; var projection = MapScenePickingService.CreateProjection(scene.TerrainMinimum, scene.TerrainMaximum, _yaw, _pitch, _zoom, (float)Bounds.Width, (float)Bounds.Height); var direction = MapScenePlacementGizmoService.AxisDirection(baseline.Orientation, axis, _axisSpace); var origin = MapScenePickingService.Project(baseline.Position, projection).Screen; var tip = MapScenePickingService.Project(baseline.Position + direction, projection).Screen;
        try { var distance = MapScenePlacementGizmoService.ProjectedAxisDragDistance(new((float)(point.X - start.X), (float)(point.Y - start.Y)), tip - origin); if (_snapEnabled) distance = MapScenePlacementGizmoService.SnapDistance(distance, _snapSettings.PositionStep); UpdatePreview(baseline with { Position = MapScenePlacementGizmoService.TranslateAxis(baseline.Position, baseline.Orientation, axis, _axisSpace, distance) }); }
        catch (InvalidOperationException) { PlacementAxisUnavailable?.Invoke(this, EventArgs.Empty); }
    }
    private void UpdatePreview(MapScenePlacementPreview preview) { _preview = preview; PlacementPreviewChanged?.Invoke(this, new(preview.Position, preview.Orientation, preview.Scale, _editMode)); InvalidateVisual(); }
    private void FinishEdit()
    {
        if (_editBaseline is not null && _preview is not null) _history.Commit(Transform(_preview)); _editStart = null; _editBaseline = null; NotifyHistory();
    }
    private void ApplyHistory(MapScenePlacementTransform state, string action)
    {
        if (_preview is null) return; _preview = _preview with { Position = state.Position, Orientation = state.Orientation, Scale = state.Scale }; NotifyHistory(); PlacementPreviewChanged?.Invoke(this, new(state.Position, state.Orientation, state.Scale, MapScenePlacementEditMode.History, action)); InvalidateVisual();
    }
    private void NotifyHistory() => HistoryAvailabilityChanged?.Invoke(this, new(_preview is not null && _history.CanUndo, _preview is not null && _history.CanRedo));
    private static MapScenePlacementTransform Transform(MapScenePlacementPreview preview) => new(preview.Position, preview.Orientation, preview.Scale);
    private static MapScenePlacementAxis? TranslationAxis(MapScenePlacementEditMode mode) => mode switch { MapScenePlacementEditMode.TranslateX => MapScenePlacementAxis.X, MapScenePlacementEditMode.TranslateY => MapScenePlacementAxis.Y, MapScenePlacementEditMode.TranslateZ => MapScenePlacementAxis.Z, _ => null };
    private static MapScenePlacementAxis? RotationAxis(MapScenePlacementEditMode mode) => mode switch { MapScenePlacementEditMode.RotateX => MapScenePlacementAxis.X, MapScenePlacementEditMode.RotateY => MapScenePlacementAxis.Y, MapScenePlacementEditMode.RotateZ => MapScenePlacementAxis.Z, _ => null };
    private static void ValidatePreview(MapScenePlacementPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview); if (preview.Vertices.Count == 0 || preview.TriangleIndices.Count == 0 || preview.TriangleIndices.Count % 3 != 0 || preview.TriangleIndices.Any(index => (uint)index >= (uint)preview.Vertices.Count) || preview.Vertices.Any(value => !Finite(value)) || !Finite(preview.Position) || !Finite(preview.Orientation) || !MapScenePlacementGizmoService.IsEncodableScale(preview.Scale)) throw new InvalidDataException("Placement preview requires finite bounded geometry and a WotLK-encodable transform.");
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class DrawOperation(Rect bounds, Scene scene, float yaw, float pitch, float zoom, bool terrain, bool objects, bool wireframe, bool pickMode, MapSceneTerrainPick? pick, bool editEnabled, MapScenePlacementEditMode editMode, MapScenePlacementAxisSpace axisSpace, MapScenePlacementPreview? preview) : ICustomDrawOperation
    {
        private readonly record struct Face(float Depth, float Ax, float Ay, float Bx, float By, float Cx, float Cy, SKColor AColor, SKColor BColor, SKColor CColor);
        public Rect Bounds => bounds; public bool HitTest(Point point) => bounds.Contains(point); public bool Equals(ICustomDrawOperation? other) => false; public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>(); if (feature is null) return; using var lease = feature.Lease(); var canvas = lease.SkCanvas; var width = (float)bounds.Width; var height = (float)bounds.Height; if (width <= 1 || height <= 1) return;
            var visible = scene.Meshes.Where(mesh => mesh.Kind == "terrain" ? terrain : objects).ToList();
            if (preview is not null)
            {
                var kind = preview.Kind == AdtPlacementKind.M2 ? "M2" : "WMO"; if (preview.UniqueId is { } uniqueId) visible.RemoveAll(mesh => mesh.Kind == kind && mesh.UniqueId == uniqueId);
                visible.Add(new(preview.Vertices, preview.TriangleIndices, null, M2PreviewSceneService.MapObjectTransform(preview.Orientation, preview.Scale, preview.Position), new SKColor(255, 194, 61), kind, preview.UniqueId));
            }
            if (visible.Count == 0) return; var terrainFrame = pickMode || editEnabled; var projection = MapScenePickingService.CreateProjection(terrainFrame ? scene.TerrainMinimum : scene.Minimum, terrainFrame ? scene.TerrainMaximum : scene.Maximum, yaw, pitch, zoom, width, height); var center = projection.Center; var view = projection.View; var scale = projection.Scale; const int faceBudget = 100_000; var totalTriangles = visible.Sum(mesh => mesh.Indices.Count / 3); var sample = Math.Max(1, (int)Math.Ceiling(totalTriangles / (double)faceBudget)); var faces = new List<Face>(Math.Min(totalTriangles, faceBudget)); var light = Vector3.Normalize(new Vector3(-0.4f, -0.55f, 0.85f));
            foreach (var mesh in visible)
            {
                var transformed = new Vector3[mesh.Vertices.Count]; for (var index = 0; index < transformed.Length; index++) transformed[index] = Vector3.Transform(Vector3.Transform(mesh.Vertices[index], mesh.Transform) - center, view);
                for (var index = 0; index + 2 < mesh.Indices.Count; index += 3 * sample)
                {
                    var ia = mesh.Indices[index]; var ib = mesh.Indices[index + 1]; var ic = mesh.Indices[index + 2]; if ((uint)ia >= transformed.Length || (uint)ib >= transformed.Length || (uint)ic >= transformed.Length) continue; var a = transformed[ia]; var b = transformed[ib]; var c = transformed[ic];
                    var ax = width * .5f + a.X * scale; var ay = height * .5f - a.Z * scale; var bx = width * .5f + b.X * scale; var by = height * .5f - b.Z * scale; var cx = width * .5f + c.X * scale; var cy = height * .5f - c.Z * scale; if (Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) < .015f) continue;
                    var normal = Vector3.Cross(b - a, c - a); if (normal.LengthSquared() > .000001f) normal = Vector3.Normalize(normal); var shade = Math.Clamp(.28f + .72f * Math.Abs(Vector3.Dot(normal, light)), .22f, 1f); var baseA = mesh.VertexColors is null ? mesh.Color : mesh.VertexColors[ia]; var baseB = mesh.VertexColors is null ? mesh.Color : mesh.VertexColors[ib]; var baseC = mesh.VertexColors is null ? mesh.Color : mesh.VertexColors[ic]; faces.Add(new((a.Y + b.Y + c.Y) / 3f, ax, ay, bx, by, cx, cy, Shade(baseA, shade), Shade(baseB, shade), Shade(baseC, shade)));
                }
            }
            faces.Sort(static (left, right) => right.Depth.CompareTo(left.Depth)); var positions = new SKPoint[faces.Count * 3]; var colors = new SKColor[positions.Length];
            for (var index = 0; index < faces.Count; index++) { var face = faces[index]; var offset = index * 3; positions[offset] = new(face.Ax, face.Ay); positions[offset + 1] = new(face.Bx, face.By); positions[offset + 2] = new(face.Cx, face.Cy); colors[offset] = face.AColor; colors[offset + 1] = face.BColor; colors[offset + 2] = face.CColor; }
            using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill }) using (var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, null, colors)) canvas.DrawVertices(vertices, SKBlendMode.SrcOver, fill);
            if (wireframe) { using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = .65f, Color = new SKColor(7, 11, 17, 110) }; using var path = new SKPath(); foreach (var face in faces) { path.Rewind(); path.MoveTo(face.Ax, face.Ay); path.LineTo(face.Bx, face.By); path.LineTo(face.Cx, face.Cy); path.Close(); canvas.DrawPath(path, edge); } }
            if (pick is not null)
            {
                var projected = MapScenePickingService.Project(pick.WorldPosition, projection).Screen; using var marker = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, Color = new SKColor(255, 205, 72) }; using var markerFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(255, 205, 72, 70) };
                canvas.DrawCircle(projected.X, projected.Y, 9, markerFill); canvas.DrawCircle(projected.X, projected.Y, 9, marker); canvas.DrawLine(projected.X - 14, projected.Y, projected.X + 14, projected.Y, marker); canvas.DrawLine(projected.X, projected.Y - 14, projected.X, projected.Y + 14, marker);
            }
            if (preview is not null)
            {
                var terrainExtent = scene.TerrainMaximum - scene.TerrainMinimum; var axisLength = Math.Max(8f, Math.Max(terrainExtent.X, terrainExtent.Y) * 0.075f); var origin = MapScenePickingService.Project(preview.Position, projection).Screen; var xDirection = MapScenePlacementGizmoService.AxisDirection(preview.Orientation, MapScenePlacementAxis.X, axisSpace); var yDirection = MapScenePlacementGizmoService.AxisDirection(preview.Orientation, MapScenePlacementAxis.Y, axisSpace); var zDirection = MapScenePlacementGizmoService.AxisDirection(preview.Orientation, MapScenePlacementAxis.Z, axisSpace); var x = MapScenePickingService.Project(preview.Position + xDirection * axisLength, projection).Screen; var y = MapScenePickingService.Project(preview.Position + yDirection * axisLength, projection).Screen; var z = MapScenePickingService.Project(preview.Position + zDirection * axisLength, projection).Screen; var active = editMode switch { MapScenePlacementEditMode.TranslateX or MapScenePlacementEditMode.RotateX => MapScenePlacementAxis.X, MapScenePlacementEditMode.TranslateY or MapScenePlacementEditMode.RotateY => MapScenePlacementAxis.Y, MapScenePlacementEditMode.TranslateZ or MapScenePlacementEditMode.RotateZ => MapScenePlacementAxis.Z, _ => (MapScenePlacementAxis?)null };
                using var axis = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke }; using var axisText = new SKPaint { IsAntialias = true }; using var axisFont = new SKFont(SKTypeface.Default, 11); DrawAxis(x, "X", MapScenePlacementAxis.X, new SKColor(239, 82, 82)); DrawAxis(y, "Y", MapScenePlacementAxis.Y, new SKColor(90, 214, 112)); DrawAxis(z, "Z", MapScenePlacementAxis.Z, new SKColor(91, 154, 255)); using var centerPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(255, 205, 72) }; canvas.DrawCircle(origin.X, origin.Y, editEnabled ? 6 : 4, centerPaint);
                void DrawAxis(Vector2 endpoint, string label, MapScenePlacementAxis current, SKColor color) { axis.Color = color; axis.StrokeWidth = editEnabled && active == current ? 4f : editEnabled ? 2.5f : 2f; canvas.DrawLine(origin.X, origin.Y, endpoint.X, endpoint.Y, axis); axisText.Color = color; canvas.DrawText(label, endpoint.X + 3, endpoint.Y - 3, SKTextAlign.Left, axisFont, axisText); }
            }
            using var text = new SKPaint { IsAntialias = true, Color = new SKColor(223, 230, 240) }; using var font = new SKFont(SKTypeface.Default, 13); using var hint = new SKFont(SKTypeface.Default, 12); canvas.DrawText($"ADT terrain + {scene.M2:N0} M2 + {scene.Wmo:N0} WMO{(preview is null ? "" : " + gold preview")} · {faces.Count:N0}/{scene.SourceTriangles:N0} rendered triangles · provenance {scene.Provenance}", 12, 22, SKTextAlign.Left, font, text); text.Color = scene.Unresolved == 0 ? new SKColor(155, 194, 163) : new SKColor(255, 186, 91); canvas.DrawText(scene.Unresolved == 0 ? "All selected placements resolved" : $"{scene.Unresolved:N0} placement(s) unresolved or outside the selected provenance", 12, 40, SKTextAlign.Left, hint, text); text.Color = scene.MaterialCells == 0 || scene.CompleteMaterialCells < scene.MaterialCells ? new SKColor(255, 186, 91) : new SKColor(155, 194, 163); canvas.DrawText(scene.MaterialCells == 0 ? "Diagnostic terrain color · no material set loaded" : $"MCLY/MCAL terrain materials · {scene.CompleteMaterialCells:N0}/{scene.MaterialCells:N0} cells complete", 12, 57, SKTextAlign.Left, hint, text); text.Color = pickMode || editEnabled ? new SKColor(255, 205, 72) : new SKColor(160, 172, 190); var space = axisSpace == MapScenePlacementAxisSpace.World ? "WORLD" : "LOCAL MODEL"; var footer = pickMode ? "PICK MODE · click terrain · wheel to zoom" : editEnabled ? editMode switch { MapScenePlacementEditMode.MoveOnTerrain => "EDIT MOVE · drag the gold preview across exact terrain", MapScenePlacementEditMode.TranslateX => $"EDIT TRANSLATE X · drag along red X · {space}", MapScenePlacementEditMode.TranslateY => $"EDIT TRANSLATE Y · drag along green Y · {space}", MapScenePlacementEditMode.TranslateZ => $"EDIT TRANSLATE Z · drag along blue Z · {space}", MapScenePlacementEditMode.RotateX => "EDIT ROTATE X · drag horizontally", MapScenePlacementEditMode.RotateY => "EDIT ROTATE Y · drag horizontally", MapScenePlacementEditMode.RotateZ => "EDIT ROTATE Z · drag horizontally", _ => "EDIT UNIFORM SCALE · drag vertically" } : "Drag to rotate · wheel to zoom"; canvas.DrawText(footer, 12, height - 12, SKTextAlign.Left, hint, text);

            static SKColor Shade(SKColor color, float amount) => new((byte)(color.Red * amount), (byte)(color.Green * amount), (byte)(color.Blue * amount), color.Alpha);
        }
    }
}
