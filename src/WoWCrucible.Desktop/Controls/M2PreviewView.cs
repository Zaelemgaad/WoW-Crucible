using System.Numerics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

public sealed record M2PreviewMountedModel(M2PreviewGeometry Geometry, Matrix4x4 Transform, RgbaTexture? Texture, string Label, int? ParentAttachmentIndex = null);

public sealed class M2PreviewView : UserControl, IDisposable
{
    private sealed record CameraChoice(int? Index, string Label) { public override string ToString() => Label; }
    private readonly M2PreviewCanvas _canvas = new();
    private readonly Grid _cameraBar = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, Margin = new Thickness(0, 4) };
    private readonly ComboBox _cameras = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly Grid _playback = new() { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto") };
    private readonly Button _play = new() { Content = "Play" };
    private readonly ComboBox _sequences = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly TextBlock _time = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Slider _timeline = new() { Minimum = 0, Maximum = 1, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 30d) };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private M2PreviewGeometry? _geometry;
    private M2AnimationPose? _pose;
    private double _elapsedBeforePlay;
    private bool _updatingTimeline;

    public M2PreviewView()
    {
        _cameraBar.Children.Add(new TextBlock { Text = "View", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }); _cameraBar.Children.Add(_cameras); Grid.SetColumn(_cameras, 1);
        ClipToBounds = true;
        _playback.Children.Add(_play); Grid.SetColumn(_play, 0);
        _playback.Children.Add(_sequences); Grid.SetColumn(_sequences, 1);
        _playback.Children.Add(_time); Grid.SetColumn(_time, 2);
        _playback.Children.Add(_timeline); Grid.SetRow(_timeline, 1); Grid.SetColumnSpan(_timeline, 3);
        var root = new Grid { RowDefinitions = new("*,Auto,Auto") };
        root.Children.Add(_canvas);
        root.Children.Add(_cameraBar); Grid.SetRow(_cameraBar, 1);
        root.Children.Add(_playback); Grid.SetRow(_playback, 2);
        Content = root;
        _play.Click += (_, _) => TogglePlayback();
        _sequences.SelectionChanged += (_, _) => SelectSequence();
        _cameras.SelectionChanged += (_, _) => _canvas.SetCamera((_cameras.SelectedItem as CameraChoice)?.Index);
        _timeline.PropertyChanged += (_, args) => { if (args.Property == RangeBase.ValueProperty && !_updatingTimeline) Scrub(_timeline.Value); };
        _timer.Tick += (_, _) => Tick();
        DetachedFromVisualTree += (_, _) => StopPlayback();
        _playback.IsVisible = false;
        _cameraBar.IsVisible = false;
    }

    public void SetGeometry(M2PreviewGeometry geometry)
    {
        StopPlayback(); _geometry = geometry; _pose = null; _elapsedBeforePlay = 0;
        _canvas.SetGeometry(geometry);
        _cameras.ItemsSource = new[] { new CameraChoice(null, "Orbit camera") }.Concat(geometry.Cameras.Select(camera => new CameraChoice(camera.Index, $"{camera.Name} · {camera.FieldOfViewDegrees:0.#}°"))).ToArray();
        _cameras.SelectedIndex = 0; _cameraBar.IsVisible = geometry.Cameras.Count > 0;
        _sequences.ItemsSource = geometry.Sequences;
        _playback.IsVisible = geometry.Sequences.Count > 0;
        _sequences.SelectedIndex = geometry.Sequences.Count > 0 ? 0 : -1;
        UpdatePlaybackAvailability();
    }

    public void ClearGeometry() { StopPlayback(); _geometry = null; _pose = null; _playback.IsVisible = false; _cameraBar.IsVisible = false; _cameras.ItemsSource = null; _canvas.ClearGeometry(); }
    public void SetTexture(string? previewPath) => _canvas.SetTexture(previewPath);
    public void SetDecodedTexture(RgbaTexture? texture) => _canvas.SetDecodedTexture(texture);
    public void SetDecodedTextures(IReadOnlyDictionary<int, RgbaTexture> textures) => _canvas.SetDecodedTextures(textures);
    public void SetSceneTransform(Matrix4x4 transform, string? label = null) => _canvas.SetSceneTransform(transform, label);
    public void SetAttachmentOverlay(bool visible, int? highlightedAttachmentIndex = null) => _canvas.SetAttachmentOverlay(visible, highlightedAttachmentIndex);
    public void SetMountedModels(IEnumerable<M2PreviewMountedModel> models)
    {
        _canvas.SetMountedModels(models); UpdatePlaybackAvailability();
    }
    public void ClearMountedModels() { _canvas.ClearMountedModels(); UpdatePlaybackAvailability(); }
    public M2AnimationPose? SnapshotPose() => _geometry is null || _pose is null ? null : M2AnimationService.SnapshotPose(_geometry, _pose);

    private void SelectSequence()
    {
        StopPlayback(); _elapsedBeforePlay = 0;
        var sequence = SelectedSequence();
        if (_geometry is null || sequence is null) { _pose = null; _canvas.SetPose(null); return; }
        _timeline.Maximum = Math.Max(1, sequence.DurationMilliseconds); _timeline.Value = 0;
        _pose ??= M2AnimationService.CreatePose(_geometry);
        try { M2AnimationService.SampleInto(_geometry, sequence.Index, 0, _pose); _canvas.SetPose(_pose); _time.Text = $"0 / {sequence.DurationMilliseconds:N0} ms"; }
        catch (Exception exception) { _pose = null; _canvas.SetPose(null); _time.Text = $"Static fallback: {exception.Message}"; }
        UpdatePlaybackAvailability();
    }

    private void TogglePlayback()
    {
        if (_timer.IsEnabled) { _elapsedBeforePlay = CurrentElapsed(); StopPlayback(); return; }
        if (_geometry is null || SelectedSequence() is null || _pose is null) return;
        _clock.Restart(); _timer.Start(); _play.Content = "Pause";
    }

    private void Tick()
    {
        if (!IsEffectivelyVisible) { StopPlayback(); return; }
        var sequence = SelectedSequence(); if (_geometry is null || sequence is null || _pose is null) { StopPlayback(); return; }
        var elapsed = CurrentElapsed();
        if (!sequence.Loops && elapsed >= sequence.DurationMilliseconds) { elapsed = sequence.DurationMilliseconds; _elapsedBeforePlay = elapsed; StopPlayback(); }
        try
        {
            M2AnimationService.SampleInto(_geometry, sequence.Index, elapsed, _pose); _canvas.SetPose(_pose);
            _updatingTimeline = true; _timeline.Value = _pose.TimeMilliseconds; _updatingTimeline = false;
            _time.Text = $"{_pose.TimeMilliseconds:N0} / {sequence.DurationMilliseconds:N0} ms";
        }
        catch (Exception exception) { StopPlayback(); _canvas.SetPose(null); _time.Text = $"Static fallback: {exception.Message}"; }
    }

    private void Scrub(double value)
    {
        if (_geometry is null || SelectedSequence() is not { } sequence || _pose is null) return;
        _elapsedBeforePlay = value; _clock.Restart();
        try { M2AnimationService.SampleInto(_geometry, sequence.Index, value, _pose); _canvas.SetPose(_pose); _time.Text = $"{_pose.TimeMilliseconds:N0} / {sequence.DurationMilliseconds:N0} ms"; }
        catch (Exception exception) { StopPlayback(); _canvas.SetPose(null); _time.Text = $"Static fallback: {exception.Message}"; }
    }

    private M2PreviewSequence? SelectedSequence() => _sequences.SelectedItem as M2PreviewSequence;
    private double CurrentElapsed() => _elapsedBeforePlay + _clock.Elapsed.TotalMilliseconds;
    private void StopPlayback() { _timer.Stop(); _clock.Stop(); _play.Content = "Play"; }
    private void UpdatePlaybackAvailability()
    {
        _play.IsEnabled = _geometry is not null && _pose is not null;
        _timeline.IsEnabled = _play.IsEnabled;
    }

    public void Dispose() { StopPlayback(); _timer.Stop(); _canvas.Dispose(); _geometry = null; _pose = null; }
}

internal sealed class M2PreviewCanvas : Control, IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> LoggedParticleFailures = new(StringComparer.Ordinal);
    private sealed record MountedModel(M2PreviewGeometry Geometry, Matrix4x4 Transform, SKBitmap? Texture, string Label, int? ParentAttachmentIndex);
    private M2PreviewGeometry? _geometry;
    private SKBitmap? _texture;
    private readonly Dictionary<int, SKBitmap> _materialTextures = [];
    private readonly List<MountedModel> _mountedModels = [];
    private float _yaw = -0.65f;
    private float _pitch = 0.35f;
    private float _zoom = 1;
    private Avalonia.Point? _dragStart;
    private bool _showAttachments;
    private int? _highlightedAttachmentIndex;
    private M2AnimationPose? _pose;
    private int? _nativeCameraIndex;
    private Matrix4x4 _sceneTransform = Matrix4x4.Identity;
    private string? _sceneTransformLabel;

    public M2PreviewCanvas() => ClipToBounds = true;

    public void SetGeometry(M2PreviewGeometry geometry)
    {
        _geometry = geometry;
        _pose = null;
        ClearMaterialTextures();
        ClearMountedModels();
        _yaw = -0.65f;
        _pitch = 0.35f;
        _zoom = 1;
        _sceneTransform = Matrix4x4.Identity;
        _sceneTransformLabel = null;
        _nativeCameraIndex = null;
        InvalidateVisual();
    }

    public void ClearGeometry()
    {
        _geometry = null;
        _pose = null;
        ClearMountedModels();
        InvalidateVisual();
    }

    public void SetTexture(string? previewPath)
    {
        _texture?.Dispose(); _texture = null;
        if (!string.IsNullOrWhiteSpace(previewPath) && File.Exists(previewPath)) _texture = SKBitmap.Decode(previewPath);
        InvalidateVisual();
    }

    public void SetDecodedTexture(RgbaTexture? texture)
    {
        ClearMaterialTextures();
        _texture?.Dispose(); _texture = null;
        if (texture is not null)
        {
            var bitmap = new SKBitmap(new SKImageInfo(texture.Width, texture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var rowBytes = checked(texture.Width * 4); var address = bitmap.GetPixels();
            for (var row = 0; row < texture.Height; row++) Marshal.Copy(texture.Pixels, row * rowBytes, IntPtr.Add(address, row * bitmap.RowBytes), rowBytes);
            _texture = bitmap;
        }
        InvalidateVisual();
    }

    public void SetDecodedTextures(IReadOnlyDictionary<int, RgbaTexture> textures)
    {
        _texture?.Dispose(); _texture = null;
        ClearMaterialTextures();
        foreach (var (textureDefinitionIndex, texture) in textures) _materialTextures[textureDefinitionIndex] = CreateBitmap(texture);
        InvalidateVisual();
    }

    public void SetAttachmentOverlay(bool visible, int? highlightedAttachmentIndex = null)
    {
        _showAttachments = visible;
        _highlightedAttachmentIndex = highlightedAttachmentIndex;
        InvalidateVisual();
    }

    public void SetSceneTransform(Matrix4x4 transform, string? label = null)
    {
        if (!Finite(transform)) throw new ArgumentException("The M2 scene transform must contain only finite values.", nameof(transform));
        _sceneTransform = transform;
        _sceneTransformLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        InvalidateVisual();
    }

    public void SetMountedModels(IEnumerable<M2PreviewMountedModel> models)
    {
        ClearMountedModels();
        foreach (var model in models)
        {
            ArgumentNullException.ThrowIfNull(model.Geometry);
            if (!Finite(model.Transform)) throw new ArgumentException($"Mounted model '{model.Label}' has a non-finite transform.", nameof(models));
            if (model.ParentAttachmentIndex is { } attachmentIndex && (_geometry is null || (uint)attachmentIndex >= (uint)_geometry.Attachments.Count))
                throw new ArgumentException($"Mounted model '{model.Label}' references missing parent attachment record {attachmentIndex:N0}.", nameof(models));
            _mountedModels.Add(new(model.Geometry, model.Transform, model.Texture is null ? null : CreateBitmap(model.Texture), model.Label, model.ParentAttachmentIndex));
        }
        InvalidateVisual();
    }

    public void ClearMountedModels()
    {
        foreach (var model in _mountedModels) model.Texture?.Dispose();
        _mountedModels.Clear();
        InvalidateVisual();
    }

    public void SetPose(M2AnimationPose? pose) { _pose = pose; InvalidateVisual(); }
    public void SetCamera(int? cameraIndex)
    {
        if (cameraIndex is { } index && (_geometry is null || (uint)index >= (uint)_geometry.Cameras.Count)) throw new ArgumentOutOfRangeException(nameof(cameraIndex));
        _nativeCameraIndex = cameraIndex; InvalidateVisual();
    }

    private static SKBitmap CreateBitmap(RgbaTexture texture)
    {
        var bitmap = new SKBitmap(new SKImageInfo(texture.Width, texture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var rowBytes = checked(texture.Width * 4); var address = bitmap.GetPixels();
        for (var row = 0; row < texture.Height; row++) Marshal.Copy(texture.Pixels, row * rowBytes, IntPtr.Add(address, row * bitmap.RowBytes), rowBytes);
        return bitmap;
    }

    private void ClearMaterialTextures()
    {
        foreach (var texture in _materialTextures.Values) texture.Dispose();
        _materialTextures.Clear();
    }

    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    public void Dispose()
    {
        _texture?.Dispose(); _texture = null;
        ClearMaterialTextures();
        ClearMountedModels();
        _geometry = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#090D14")), Bounds);
        if (_geometry is null) return;
        context.Custom(new M2DrawOperation(Bounds, _geometry, _pose, _texture, _materialTextures, _mountedModels, _sceneTransform, _sceneTransformLabel, _yaw, _pitch, _zoom, _showAttachments, _highlightedAttachmentIndex, _nativeCameraIndex));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var current = e.GetPosition(this);
        _yaw += (float)(current.X - start.X) * 0.012f;
        _pitch = Math.Clamp(_pitch + (float)(current.Y - start.Y) * 0.012f, -1.5f, 1.5f);
        _dragStart = current;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.12f : 0.89f), 0.15f, 8f);
        InvalidateVisual();
        e.Handled = true;
    }

    private sealed class M2DrawOperation(Rect bounds, M2PreviewGeometry geometry, M2AnimationPose? pose, SKBitmap? texture, IReadOnlyDictionary<int, SKBitmap> materialTextures, IReadOnlyList<MountedModel> mountedModels, Matrix4x4 sceneTransform, string? sceneTransformLabel, float yaw, float pitch, float zoom, bool showAttachments, int? highlightedAttachmentIndex, int? nativeCameraIndex) : ICustomDrawOperation
    {
        private sealed record SceneSource(M2PreviewGeometry Geometry, Matrix4x4 Transform, SKBitmap? ManualTexture, IReadOnlyDictionary<int, SKBitmap>? MaterialTextures, string Label, IReadOnlyList<Vector3>? PosedVertices, IReadOnlyList<Vector3>? PosedNormals, Vector3 Minimum, Vector3 Maximum);
        public Rect Bounds => bounds;
        public bool HitTest(Avalonia.Point point) => Bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var width = (float)bounds.Width;
            var height = (float)bounds.Height;
            var sources = new List<SceneSource>(mountedModels.Count + 1) { new(geometry, Matrix4x4.Identity, texture, materialTextures, Path.GetFileName(geometry.ModelPath), pose?.Vertices, pose?.Normals, pose?.Minimum ?? geometry.Minimum, pose?.Maximum ?? geometry.Maximum) };
            foreach (var model in mountedModels)
            {
                var transform = model.Transform;
                if (pose is not null && model.ParentAttachmentIndex is { } attachmentIndex)
                {
                    var attachment = geometry.Attachments[attachmentIndex];
                    transform = model.Transform * pose.BoneTransforms[attachment.BoneIndex];
                }
                sources.Add(new SceneSource(model.Geometry, transform, model.Texture, null, model.Label, null, null, model.Geometry.Minimum, model.Geometry.Maximum));
            }
            var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
            foreach (var source in sources)
            {
                var transformedBounds = M2PreviewSceneService.TransformBounds(source.Minimum, source.Maximum, source.Transform);
                minimum = Vector3.Min(minimum, transformedBounds.Minimum); maximum = Vector3.Max(maximum, transformedBounds.Maximum);
            }
            var center = (minimum + maximum) * 0.5f;
            var extent = maximum - minimum;
            var largest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
            if (!float.IsFinite(largest) || largest <= 0.00001f) return;

            var orbitScale = Math.Min(width, height) * 0.42f / largest * zoom;
            var orbitRotation = sceneTransform * Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateRotationX(pitch);
            var useNativeCamera = nativeCameraIndex is { } requestedCamera && (uint)requestedCamera < (uint)geometry.Cameras.Count;
            M2PreviewCamera? nativeCamera = useNativeCamera ? geometry.Cameras[nativeCameraIndex!.Value] : null;
            var cameraPose = nativeCamera is null ? null : pose is not null && nativeCamera.Index < pose.Cameras.Length ? pose.Cameras[nativeCamera.Index] : new M2PreviewCameraPose(nativeCamera.BasePosition, nativeCamera.BaseTarget, 0);
            var cameraProjection = nativeCamera is not null && cameraPose is not null ? M2CameraProjectionService.TryCreate(nativeCamera, cameraPose, sceneTransform) : null;
            useNativeCamera = cameraProjection is not null;
            var scale = useNativeCamera ? height * 0.5f : orbitScale;
            var triangleCount = sources.Sum(source => (source.Geometry.Batches.Count == 0 ? source.Geometry.TriangleIndices.Count : source.Geometry.Batches.Sum(batch => batch.TriangleIndexCount)) / 3);
            var sampling = Math.Max(1, (int)Math.Ceiling(triangleCount / 30_000d));
            var faces = new List<Face>(Math.Min(triangleCount, 30_000));
            for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                var source = sources[sourceIndex]; var sourceGeometry = source.Geometry; var sourceVertices = source.PosedVertices ?? sourceGeometry.Vertices; var sourceNormals = source.PosedNormals ?? sourceGeometry.Normals; var transformed = new Vector3[sourceVertices.Count]; var viewVertices = new Vector3[sourceVertices.Count]; var transformedNormals = new Vector3[sourceNormals.Count];
                for (var index = 0; index < transformed.Length; index++)
                {
                    if (useNativeCamera)
                    {
                        var world = Vector3.Transform(Vector3.Transform(sourceVertices[index], source.Transform), sceneTransform); var view = cameraProjection!.ToViewPoint(world); viewVertices[index] = view; transformed[index] = cameraProjection.Project(view);
                    }
                    else transformed[index] = viewVertices[index] = Vector3.Transform(Vector3.Transform(sourceVertices[index], source.Transform) - center, orbitRotation);
                }
                for (var index = 0; index < transformedNormals.Length; index++)
                {
                    var sourceNormal = Vector3.TransformNormal(sourceNormals[index], source.Transform);
                    Vector3 normal;
                    if (useNativeCamera) { var worldNormal = Vector3.TransformNormal(sourceNormal, sceneTransform); normal = cameraProjection!.ToViewNormal(worldNormal); }
                    else normal = Vector3.TransformNormal(sourceNormal, orbitRotation);
                    transformedNormals[index] = normal.LengthSquared() > 0.0000001f && float.IsFinite(normal.X) && float.IsFinite(normal.Y) && float.IsFinite(normal.Z) ? Vector3.Normalize(normal) : Vector3.UnitZ;
                }
                var activeLights = new List<SceneLight>();
                if (sourceIndex == 0 && pose is not null)
                    for (var index = 0; index < Math.Min(geometry.Lights.Count, pose.Lights.Length); index++)
                    {
                        var value = pose.Lights[index]; Vector3 lightPosition; Vector3 lightDirection;
                        if (useNativeCamera)
                        {
                            lightPosition = cameraProjection!.ToViewPoint(Vector3.Transform(value.Position, sceneTransform)); lightDirection = cameraProjection.ToViewNormal(Vector3.TransformNormal(value.Direction, sceneTransform));
                        }
                        else { lightPosition = Vector3.Transform(value.Position - center, orbitRotation); lightDirection = Vector3.TransformNormal(value.Direction, orbitRotation); }
                        if (lightDirection.LengthSquared() > 0.0000001f) lightDirection = Vector3.Normalize(lightDirection);
                        activeLights.Add(new(geometry.Lights[index].Type, value with { Position = lightPosition, Direction = lightDirection }));
                    }
                IReadOnlyList<M2PreviewBatch> batches = sourceGeometry.Batches.Count == 0 ? [new M2PreviewBatch(0, 0, 0, sourceGeometry.TriangleIndices.Count, null, null)] : sourceGeometry.Batches;
                var firstBatchBySubmesh = batches.GroupBy(batch => batch.SubmeshIndex).ToDictionary(group => group.Key, group => group.First());
                var texturedSubmeshes = batches.Where(batch => ResolveTextureStages(source, batch).Count > 0).Select(batch => batch.SubmeshIndex).ToHashSet();
                foreach (var batch in batches)
                {
                    var firstPass = ReferenceEquals(batch, firstBatchBySubmesh[batch.SubmeshIndex]);
                    if (source.ManualTexture is not null && !firstPass) continue;
                    var end = Math.Min(sourceGeometry.TriangleIndices.Count, batch.TriangleStart + batch.TriangleIndexCount);
                    var activeStages = ResolveTextureStages(source, batch);
                    if (activeStages.Count == 0 && (texturedSubmeshes.Contains(batch.SubmeshIndex) || !firstPass)) continue;
                    var passOrder = batch.PriorityPlane * 131_072 + (batch.MaterialUnitIndex ?? 0);
                    for (var offset = batch.TriangleStart; offset + 2 < end; offset += 3 * sampling)
                    {
                        var ia = sourceGeometry.TriangleIndices[offset]; var ib = sourceGeometry.TriangleIndices[offset + 1]; var ic = sourceGeometry.TriangleIndices[offset + 2];
                        var a = transformed[ia]; var b = transformed[ib]; var c = transformed[ic];
                        if (useNativeCamera && (!cameraProjection!.ContainsDepth(a.Y) || !cameraProjection.ContainsDepth(b.Y) || !cameraProjection.ContainsDepth(c.Y))) continue;
                        var ax = width * 0.5f + a.X * scale; var ay = height * 0.5f - a.Z * scale;
                        var bx = width * 0.5f + b.X * scale; var by = height * 0.5f - b.Z * scale;
                        var cx = width * 0.5f + c.X * scale; var cy = height * 0.5f - c.Z * scale;
                        var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                        if (Math.Abs(area) < 0.02f) continue;
                        var viewA = viewVertices[ia]; var viewB = viewVertices[ib]; var viewC = viewVertices[ic]; var normal = Vector3.Cross(viewB - viewA, viewC - viewA);
                        if (normal.LengthSquared() > 0.000001f) normal = Vector3.Normalize(normal);
                        var lighting = (batch.RenderFlags & 0x1) != 0 ? Vector3.One : SceneLighting(normal, (viewA + viewB + viewC) / 3f, activeLights);
                        faces.Add(new((a.Y + b.Y + c.Y) / 3f, passOrder, sourceIndex, batch.MaterialUnitIndex ?? -1, ia, ib, ic, ax, ay, bx, by, cx, cy,
                            M2EnvironmentMapService.Coordinate(transformedNormals[ia]), M2EnvironmentMapService.Coordinate(transformedNormals[ib]), M2EnvironmentMapService.Coordinate(transformedNormals[ic]),
                            lighting, activeStages, batch.BlendMode));
                    }
                }
            }
            faces.Sort(static (left, right) =>
            {
                var depth = right.Depth.CompareTo(left.Depth); return depth != 0 ? depth : left.PassOrder.CompareTo(right.PassOrder);
            });

            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.65f, Color = new SKColor(5, 9, 15, 58) };
            using var path = new SKPath();
            foreach (var face in faces.Where(face => face.TextureStages.Count == 0))
            {
                fill.Color = new SKColor(Channel(face.Lighting.X * 150), Channel(face.Lighting.Y * 190), Channel(face.Lighting.Z * 220));
                path.Rewind(); path.MoveTo(face.Ax, face.Ay); path.LineTo(face.Bx, face.By); path.LineTo(face.Cx, face.Cy); path.Close();
                canvas.DrawPath(path, fill); canvas.DrawPath(path, edge);
            }

            var texturedFaces = faces.Where(face => face.TextureStages.Count > 0).GroupBy(face => new TextureGroup(face.PassOrder, face.SourceIndex, face.MaterialKey, face.BlendMode)).OrderBy(group => group.Key.PassOrder);
            foreach (var group in texturedFaces)
            {
                var sourceGeometry = sources[group.Key.SourceIndex].Geometry; var groupFaces = group.ToArray(); var stages = groupFaces[0].TextureStages;
                var positions = new SKPoint[groupFaces.Length * 3]; var shadedColors = new SKColor[groupFaces.Length * 3]; var whiteColors = new SKColor[groupFaces.Length * 3];
                for (var index = 0; index < groupFaces.Length; index++)
                {
                    var face = groupFaces[index]; var offset = index * 3;
                    positions[offset] = new(face.Ax, face.Ay); positions[offset + 1] = new(face.Bx, face.By); positions[offset + 2] = new(face.Cx, face.Cy);
                    var shade = new SKColor(Channel(face.Lighting.X * 255), Channel(face.Lighting.Y * 255), Channel(face.Lighting.Z * 255), 255); shadedColors[offset] = shadedColors[offset + 1] = shadedColors[offset + 2] = shade;
                    whiteColors[offset] = whiteColors[offset + 1] = whiteColors[offset + 2] = SKColors.White;
                }
                if (stages.Count == 1) DrawStage(stages[0], CanvasBlendMode(group.Key.BlendMode), shadedColors);
                else
                {
                    using var composite = new SKPaint { BlendMode = CanvasBlendMode(group.Key.BlendMode) };
                    var layerBounds = new SKRect(groupFaces.Min(face => Math.Min(face.Ax, Math.Min(face.Bx, face.Cx))) - 1,
                        groupFaces.Min(face => Math.Min(face.Ay, Math.Min(face.By, face.Cy))) - 1,
                        groupFaces.Max(face => Math.Max(face.Ax, Math.Max(face.Bx, face.Cx))) + 1,
                        groupFaces.Max(face => Math.Max(face.Ay, Math.Max(face.By, face.Cy))) + 1);
                    canvas.SaveLayer(layerBounds, composite);
                    DrawStage(stages[0], SKBlendMode.SrcOver, shadedColors);
                    foreach (var stage in stages.Skip(1)) DrawStage(stage, StageBlendMode(stage.Blend), whiteColors);
                    canvas.Restore();
                }

                void DrawStage(ResolvedTextureStage stage, SKBlendMode blend, SKColor[] colors)
                {
                    var coordinates = new SKPoint[groupFaces.Length * 3];
                    for (var index = 0; index < groupFaces.Length; index++)
                    {
                        var face = groupFaces[index]; var offset = index * 3;
                        AddUv(offset, face.Ia, face.EnvironmentA); AddUv(offset + 1, face.Ib, face.EnvironmentB); AddUv(offset + 2, face.Ic, face.EnvironmentC);
                    }
                    using var shader = SKShader.CreateBitmap(stage.Texture, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
                    using var paint = new SKPaint { IsAntialias = true, Shader = shader, BlendMode = blend };
                    if (stage.Blend == M2PreviewTextureStageBlend.Modulate2X) paint.ColorFilter = SKColorFilter.CreateColorMatrix([2,0,0,0,0, 0,2,0,0,0, 0,0,2,0,0, 0,0,0,1,0]);
                    if (stage.Blend == M2PreviewTextureStageBlend.AddNoAlpha) paint.ColorFilter = SKColorFilter.CreateColorMatrix([1,0,0,0,0, 0,1,0,0,0, 0,0,1,0,0, 0,0,0,0,0]);
                    using var mesh = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, coordinates, colors);
                    canvas.DrawVertices(mesh, SKBlendMode.Modulate, paint);

                    void AddUv(int destination, int vertex, Vector2 environment)
                    {
                        var uv = stage.CoordinateSource switch
                        {
                            M2PreviewTextureCoordinateSource.Environment => environment,
                            M2PreviewTextureCoordinateSource.Secondary when sourceGeometry.SecondaryTextureCoordinates.Count == sourceGeometry.Vertices.Count => sourceGeometry.SecondaryTextureCoordinates[vertex],
                            _ => sourceGeometry.TextureCoordinates[vertex]
                        };
                        coordinates[destination] = new(uv.X * stage.Texture.Width, uv.Y * stage.Texture.Height);
                    }
                }
            }

            IReadOnlyList<M2PreviewParticleSprite> particleSprites;
            string? particleFailure = null;
            try { particleSprites = M2ParticlePreviewService.BuildSprites(geometry, pose); }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
                particleSprites = [];
                particleFailure = exception.Message;
                var key = geometry.ModelPath + "\0" + exception.Message;
                if (LoggedParticleFailures.TryAdd(key, 0)) DesktopCrashLogger.Log($"M2 particle preview unavailable: {geometry.ModelPath}", exception);
            }
            var projectedParticles = new List<ProjectedParticle>(particleSprites.Count);
            foreach (var particle in particleSprites)
            {
                Vector3 point;
                if (useNativeCamera)
                {
                    var view = cameraProjection!.ToViewPoint(Vector3.Transform(particle.Position, sceneTransform));
                    if (!cameraProjection.ContainsDepth(view.Y)) continue;
                    point = cameraProjection.Project(view);
                }
                else point = Vector3.Transform(particle.Position - center, orbitRotation);
                var x = width * 0.5f + point.X * scale; var y = height * 0.5f - point.Z * scale;
                var radius = Math.Clamp(particle.Size * scale, 0.5f, 256f);
                if (x + radius < 0 || x - radius > width || y + radius < 0 || y - radius > height) continue;
                projectedParticles.Add(new(point.Y, x, y, radius, particle));
            }
            projectedParticles.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));
            foreach (var projected in projectedParticles)
            {
                var particle = projected.Sprite;
                var tint = new SKColor(Channel(particle.Color.X * 255), Channel(particle.Color.Y * 255), Channel(particle.Color.Z * 255), Channel(particle.Color.W * 255));
                var blend = particle.BlendMode switch { 3 or 4 => SKBlendMode.Plus, 5 or 6 => SKBlendMode.Modulate, _ => SKBlendMode.SrcOver };
                using var particlePaint = new SKPaint { IsAntialias = true, BlendMode = blend, Color = tint };
                particlePaint.ColorFilter = SKColorFilter.CreateBlendMode(tint, SKBlendMode.Modulate);
                canvas.Save();
                if (Math.Abs(particle.Rotation) > 0.0001f) canvas.RotateRadians(particle.Rotation, projected.X, projected.Y);
                var destination = new SKRect(projected.X - projected.Radius, projected.Y - projected.Radius, projected.X + projected.Radius, projected.Y + projected.Radius);
                if (materialTextures.TryGetValue(particle.TextureDefinitionIndex, out var particleTexture))
                {
                    var columns = Math.Max(1, (int)particle.Columns); var rows = Math.Max(1, (int)particle.Rows);
                    var tileWidth = particleTexture.Width / (float)columns; var tileHeight = particleTexture.Height / (float)rows;
                    var column = particle.TileIndex % columns; var row = particle.TileIndex / columns;
                    var source = new SKRect(column * tileWidth, row * tileHeight, (column + 1) * tileWidth, (row + 1) * tileHeight);
                    canvas.DrawBitmap(particleTexture, source, destination, particlePaint);
                }
                else canvas.DrawCircle(projected.X, projected.Y, projected.Radius, particlePaint);
                canvas.Restore();
            }

            if (showAttachments && geometry.Attachments.Count > 0)
            {
                using var marker = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(69, 211, 255, 210) };
                using var markerEdge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(6, 14, 24, 230) };
                using var labelBack = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(5, 10, 18, 220) };
                using var labelText = new SKPaint { IsAntialias = true, Color = new SKColor(232, 247, 255) };
                using var labelFont = new SKFont(SKTypeface.Default, 12);
                foreach (var attachment in geometry.Attachments)
                {
                    var attachmentPosition = pose is not null && attachment.Index < pose.AttachmentPositions.Length ? pose.AttachmentPositions[attachment.Index] : attachment.Position; Vector3 point;
                    if (useNativeCamera)
                    {
                        var view = cameraProjection!.ToViewPoint(Vector3.Transform(attachmentPosition, sceneTransform)); if (!cameraProjection.ContainsDepth(view.Y)) continue; point = cameraProjection.Project(view);
                    }
                    else point = Vector3.Transform(attachmentPosition - center, orbitRotation);
                    var x = width * 0.5f + point.X * scale; var y = height * 0.5f - point.Z * scale;
                    var selected = highlightedAttachmentIndex == attachment.Index;
                    var radius = selected ? 6f : 3.2f;
                    marker.Color = selected ? new SKColor(255, 189, 72, 245) : new SKColor(69, 211, 255, 190);
                    canvas.DrawCircle(x, y, radius, marker); canvas.DrawCircle(x, y, radius, markerEdge);
                    if (!selected) continue;
                    var label = $"{attachment.Id:N0} · {attachment.Name} · bone {attachment.BoneIndex:N0}";
                    var widthText = labelFont.MeasureText(label, labelText); var left = Math.Clamp(x + 9, 4, Math.Max(4, width - widthText - 12)); var top = Math.Clamp(y - 21, 4, Math.Max(4, height - 25));
                    canvas.DrawRoundRect(new SKRect(left - 4, top - 2, left + widthText + 4, top + 17), 4, 4, labelBack);
                    canvas.DrawText(label, left, top + 12, SKTextAlign.Left, labelFont, labelText);
                }
            }

            using var text = new SKPaint { IsAntialias = true, Color = new SKColor(225, 231, 240) };
            using var titleFont = new SKFont(SKTypeface.Default, 13);
            using var hintFont = new SKFont(SKTypeface.Default, 12);
            var geosets = geometry.Submeshes.Count == 0 ? "complete mesh" : $"{geometry.Submeshes.Count(section => section.Visible):N0}/{geometry.Submeshes.Count:N0} geosets";
            var textureCount = texture is not null ? "manual texture" : $"{materialTextures.Count:N0} material texture(s)";
            var multiTextureUnits = geometry.Batches.Count(batch => batch.TextureStages.Count > 1);
            var multiTexture = multiTextureUnits == 0 ? string.Empty : $" · {multiTextureUnits:N0} multi-texture unit(s)";
            var approximateUnits = geometry.Batches.Count(batch => batch.TextureStages.Count > 1 && batch.Combiner.Supported && !batch.Combiner.Exact);
            var approximate = approximateUnits == 0 ? string.Empty : $" · {approximateUnits:N0} approximate";
            var environmentUnits = geometry.Batches.Count(batch => batch.TextureStages.Any(stage => stage.CoordinateSource == M2PreviewTextureCoordinateSource.Environment));
            var environment = environmentUnits == 0 ? string.Empty : $" · {environmentUnits:N0} sphere-map unit(s)";
            var camera = useNativeCamera ? $" · {nativeCamera!.Name}" : string.Empty;
            var embeddedLights = geometry.Lights.Count == 0 ? string.Empty : $" · {geometry.Lights.Count:N0} embedded light(s)";
            var particles = geometry.ParticleEmitters.Count == 0 ? string.Empty : $" · {projectedParticles.Count:N0}/{geometry.ParticleEmitters.Count:N0} particle sprites/emitters";
            var particleFallback = particleFailure is null ? string.Empty : " · particle fallback";
            var fallbackUnits = geometry.Batches.Count(batch => batch.TextureStages.Count > 1 && (!batch.Combiner.Supported || batch.TextureStages.Any(stage => stage.CoordinateSource == M2PreviewTextureCoordinateSource.Unsupported)));
            var fallback = fallbackUnits == 0 ? string.Empty : $" · {fallbackUnits:N0} first-stage fallback(s)";
            var attachments = showAttachments ? $" · {geometry.Attachments.Count:N0} attachment point(s)" : string.Empty;
            var mounted = mountedModels.Count == 0 ? string.Empty : $" · {mountedModels.Count:N0} mounted model(s)";
            var animation = pose is null ? string.Empty : $" · animation {geometry.Sequences[pose.SequenceIndex].AnimationId:N0}:{geometry.Sequences[pose.SequenceIndex].SubAnimationId:N0}";
            var scene = sceneTransformLabel is null ? string.Empty : $" · {sceneTransformLabel}";
            canvas.DrawText($"{Path.GetFileName(geometry.ModelPath)} · {geosets} · {textureCount}{multiTexture}{environment}{camera}{embeddedLights}{particles}{particleFallback}{approximate}{fallback} · {faces.Count:N0} displayed faces{animation}{attachments}{mounted}{scene}", 12, 23, SKTextAlign.Left, titleFont, text);
            text.Color = new SKColor(170, 182, 200);
            canvas.DrawText("Drag to rotate · wheel to zoom", 12, height - 12, SKTextAlign.Left, hintFont, text);
        }

        private static IReadOnlyList<ResolvedTextureStage> ResolveTextureStages(SceneSource source, M2PreviewBatch batch)
        {
            if (source.ManualTexture is not null) return [new(source.ManualTexture, M2PreviewTextureCoordinateSource.Primary, M2PreviewTextureStageBlend.Source)];
            if (batch.TextureStages.Count == 0)
                return batch.TextureDefinitionIndex is { } index && source.MaterialTextures?.TryGetValue(index, out var texture) == true
                    ? [new(texture, M2PreviewTextureCoordinateSource.Primary, M2PreviewTextureStageBlend.Source)]
                    : [];
            var result = new List<ResolvedTextureStage>(batch.TextureStages.Count);
            foreach (var stage in batch.TextureStages)
            {
                if (stage.TextureDefinitionIndex < 0 || source.MaterialTextures is not { } textures || !textures.TryGetValue(stage.TextureDefinitionIndex, out var texture)) return result.Count == 0 ? [] : [result[0]];
                if (stage.CoordinateSource == M2PreviewTextureCoordinateSource.Unsupported || stage.Blend == M2PreviewTextureStageBlend.Unsupported)
                    return result.Count == 0 ? [] : [result[0]];
                result.Add(new(texture, stage.CoordinateSource, stage.Blend));
            }
            return batch.Combiner.Supported ? result : result.Count == 0 ? [] : [result[0]];
        }

        private static SKBlendMode StageBlendMode(M2PreviewTextureStageBlend blend) => blend switch
        {
            M2PreviewTextureStageBlend.Modulate or M2PreviewTextureStageBlend.Modulate2X => SKBlendMode.Modulate,
            M2PreviewTextureStageBlend.Add or M2PreviewTextureStageBlend.AddNoAlpha => SKBlendMode.Plus,
            _ => SKBlendMode.SrcOver
        };

        private static Vector3 SceneLighting(Vector3 normal, Vector3 center, IReadOnlyList<SceneLight> lights)
        {
            if (lights.Count == 0)
            {
                var fallback = Vector3.Normalize(new Vector3(-0.35f, -0.65f, 0.9f));
                return new Vector3(Math.Clamp(0.25f + 0.75f * Math.Abs(Vector3.Dot(normal, fallback)), 0.2f, 1f));
            }
            var lighting = new Vector3(0.08f);
            foreach (var light in lights)
            {
                var value = light.Pose; lighting += Vector3.Max(Vector3.Zero, value.AmbientColor) * Math.Max(0, value.AmbientIntensity);
                Vector3 towardLight; var attenuation = 1f;
                if (light.Type == 0) towardLight = -value.Direction;
                else
                {
                    towardLight = value.Position - center; var distance = towardLight.Length(); if (distance <= 0.000001f) towardLight = Vector3.UnitY; else towardLight /= distance;
                    if (value.UseAttenuation && value.AttenuationEnd > value.AttenuationStart) attenuation = 1f - Math.Clamp((distance - value.AttenuationStart) / (value.AttenuationEnd - value.AttenuationStart), 0f, 1f);
                }
                if (towardLight.LengthSquared() > 0.0000001f) towardLight = Vector3.Normalize(towardLight);
                lighting += Vector3.Max(Vector3.Zero, value.DiffuseColor) * (Math.Max(0, value.DiffuseIntensity) * Math.Abs(Vector3.Dot(normal, towardLight)) * attenuation);
            }
            return Vector3.Clamp(lighting, new Vector3(0.12f), Vector3.One);
        }

        private static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);

        private static SKBlendMode CanvasBlendMode(ushort blendMode) => blendMode switch
        {
            3 or 4 => SKBlendMode.Plus,
            5 or 6 => SKBlendMode.Modulate,
            _ => SKBlendMode.SrcOver
        };

        private sealed record ResolvedTextureStage(SKBitmap Texture, M2PreviewTextureCoordinateSource CoordinateSource, M2PreviewTextureStageBlend Blend);
        private sealed record SceneLight(short Type, M2PreviewLightPose Pose);
        private readonly record struct ProjectedParticle(float Depth, float X, float Y, float Radius, M2PreviewParticleSprite Sprite);
        private readonly record struct TextureGroup(int PassOrder, int SourceIndex, int MaterialKey, ushort BlendMode);
        private readonly record struct Face(float Depth, int PassOrder, int SourceIndex, int MaterialKey, int Ia, int Ib, int Ic, float Ax, float Ay, float Bx, float By, float Cx, float Cy,
            Vector2 EnvironmentA, Vector2 EnvironmentB, Vector2 EnvironmentC, Vector3 Lighting, IReadOnlyList<ResolvedTextureStage> TextureStages, ushort BlendMode);
    }
}
